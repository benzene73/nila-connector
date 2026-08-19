using Microsoft.Extensions.Logging;
using NilaConnector.Core.Models;
using NilaConnector.Core.Tally;

namespace NilaConnector.Core.Nila;

public sealed partial class SyncCoordinator : IDisposable
{
    private readonly ITallyClient _tally;
    private readonly NilaSyncClient _nila;
    private readonly IDeviceCredentialStore _credentials;
    private readonly ILogger<SyncCoordinator> _logger;
    private readonly SemaphoreSlim _syncLock = new(1, 1);

    public SyncCoordinator(
        ITallyClient tally,
        NilaSyncClient nila,
        IDeviceCredentialStore credentials,
        ILogger<SyncCoordinator> logger)
    {
        _tally = tally;
        _nila = nila;
        _credentials = credentials;
        _logger = logger;
    }

    public event EventHandler<CompanySyncProgress>? ProgressChanged;

    public void Dispose()
    {
        _syncLock.Dispose();
        GC.SuppressFinalize(this);
    }

    public async Task SyncAsync(TallyCompany company, CancellationToken cancellationToken)
    {
        var credential = _credentials.Load()
            ?? throw new InvalidOperationException("Link this computer to Nila before syncing.");

        await _syncLock.WaitAsync(cancellationToken);
        SyncRunResponse? run = null;
        var pipeline = "masters";

        try
        {
            Publish(company, "Starting", "Pending", "Pending", null, null, null);
            run = await _nila.StartAsync(credential, company, cancellationToken);

            Publish(company, "Syncing", "Reading…", "Pending", null, null, null);
            var masters = await _tally.GetMastersAsync(company.Name, cancellationToken);
            await UploadChunksAsync(
                masters,
                (chunk, complete) => _nila.PutMastersAsync(credential, run.Id, chunk, complete, cancellationToken));
            Publish(company, "Syncing", $"Synced {masters.Count}", "Reading…", masters.Count, null, null);

            pipeline = "vouchers";
            var vouchers = await _tally.GetVouchersAsync(
                company.Name,
                run.PeriodStart,
                run.PeriodEnd,
                cancellationToken);
            await UploadChunksAsync(
                vouchers,
                (chunk, complete) => _nila.PutVouchersAsync(credential, run.Id, chunk, complete, cancellationToken));
            Publish(
                company,
                "Up to date",
                $"Synced {masters.Count}",
                $"Synced {vouchers.Count} ({run.PeriodStart:MMM d}–{run.PeriodEnd:MMM d})",
                masters.Count,
                vouchers.Count,
                null);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            if (run is not null)
            {
                try
                {
                    await _nila.FailAsync(credential, run.Id, pipeline, SafeError(exception), cancellationToken);
                }
                catch (Exception reportException) when (reportException is not OperationCanceledException)
                {
                    LogFailureReport(_logger, reportException.GetType().Name);
                }
            }

            Publish(company, "Needs attention", pipeline == "masters" ? "Failed" : "Synced", pipeline == "vouchers" ? "Failed" : "Pending", null, null, SafeError(exception));
            throw;
        }
        finally
        {
            _syncLock.Release();
        }
    }

    private void Publish(
        TallyCompany company,
        string status,
        string masters,
        string vouchers,
        int? mastersCount,
        int? vouchersCount,
        string? error) =>
        ProgressChanged?.Invoke(
            this,
            new CompanySyncProgress(company, status, masters, vouchers, mastersCount, vouchersCount, error));

    private static string SafeError(Exception exception) => exception switch
    {
        TallyClientException tally => tally.Message,
        HttpRequestException => "Nila could not be reached.",
        _ => "Sync failed. See diagnostics for details.",
    };

    private static async Task UploadChunksAsync<T>(
        IReadOnlyList<T> records,
        Func<IReadOnlyList<T>, bool, Task<SyncRunResponse>> upload)
    {
        const int chunkSize = 500;
        if (records.Count == 0)
        {
            _ = await upload([], true);
            return;
        }

        for (var offset = 0; offset < records.Count; offset += chunkSize)
        {
            var chunk = records.Skip(offset).Take(chunkSize).ToArray();
            _ = await upload(chunk, offset + chunk.Length == records.Count);
        }
    }

    [LoggerMessage(EventId = 3201, Level = LogLevel.Warning, Message = "Could not report sync failure ({ExceptionType})")]
    private static partial void LogFailureReport(ILogger logger, string exceptionType);
}

public sealed record CompanySyncProgress(
    TallyCompany Company,
    string Status,
    string Masters,
    string Vouchers,
    int? MastersCount,
    int? VouchersCount,
    string? Error);
