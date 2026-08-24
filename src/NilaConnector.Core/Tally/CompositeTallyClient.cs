using NilaConnector.Core.Models;

namespace NilaConnector.Core.Tally;

public sealed class CompositeTallyClient : ITallyClient, IDisposable
{
    private readonly ITallyGatewayClient _gatewayClient;
    private readonly ITallyReadClient _readClient;
    private readonly SemaphoreSlim _requestLock = new(1, 1);
    private bool _disposed;

    public CompositeTallyClient(ITallyGatewayClient gatewayClient, ITallyReadClient readClient)
    {
        _gatewayClient = gatewayClient ?? throw new ArgumentNullException(nameof(gatewayClient));
        _readClient = readClient ?? throw new ArgumentNullException(nameof(readClient));
    }

    public Task<IReadOnlyList<TallyCompany>> GetOpenCompaniesAsync(CancellationToken cancellationToken) =>
        ExecuteAsync(() => _readClient.GetOpenCompaniesAsync(cancellationToken), cancellationToken);

    public Task<IReadOnlyList<TallyMaster>> GetMastersAsync(
        string company,
        CancellationToken cancellationToken) =>
        ExecuteAsync(() => _readClient.GetMastersAsync(company, cancellationToken), cancellationToken);

    public Task<IReadOnlyList<TallySyncedVoucher>> GetVouchersAsync(
        string company,
        DateOnly fromDate,
        DateOnly toDate,
        CancellationToken cancellationToken) =>
        ExecuteAsync(
            () => _readClient.GetVouchersAsync(company, fromDate, toDate, cancellationToken),
            cancellationToken);

    public Task<VoucherSemanticFingerprint?> GetVoucherFingerprintAsync(
        string company,
        DateOnly voucherDate,
        long masterId,
        CancellationToken cancellationToken) =>
        ExecuteAsync(
            () => _readClient.GetVoucherFingerprintAsync(
                company,
                voucherDate,
                masterId,
                cancellationToken),
            cancellationToken);

    public Task<TallyVoucherResult> CreateVoucherAsync(
        CreateVoucherCommand command,
        CancellationToken cancellationToken) =>
        ExecuteAsync(() => _gatewayClient.CreateVoucherAsync(command, cancellationToken), cancellationToken);

    public Task<TallyVoucherBatchResult> CreateLedgerAsync(
        CreateLedgerCommand command,
        CancellationToken cancellationToken) =>
        ExecuteAsync(() => _gatewayClient.CreateLedgerAsync(command, cancellationToken), cancellationToken);

    public Task<TallyVoucherBatchResult> ImportVouchersAsync(
        ImportVouchersCommand command,
        CancellationToken cancellationToken) =>
        ExecuteAsync(() => _gatewayClient.ImportVouchersAsync(command, cancellationToken), cancellationToken);

    private async Task<T> ExecuteAsync<T>(Func<Task<T>> operation, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _requestLock.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return await operation().ConfigureAwait(false);
        }
        finally
        {
            _requestLock.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        if (_gatewayClient is IDisposable gatewayDisposable)
        {
            gatewayDisposable.Dispose();
        }

        if (_readClient is IDisposable readDisposable && !ReferenceEquals(_readClient, _gatewayClient))
        {
            readDisposable.Dispose();
        }

        _disposed = true;
    }
}
