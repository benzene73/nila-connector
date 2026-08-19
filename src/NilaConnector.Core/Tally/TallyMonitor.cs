using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NilaConnector.Core.Models;
using NilaConnector.Core.State;

namespace NilaConnector.Core.Tally;

public sealed partial class TallyMonitor(
    ITallyClient client,
    TallyClientOptions options,
    ConnectorStateStore stateStore,
    ILogger<TallyMonitor> logger) : BackgroundService
{
    private ConnectionStatus? _lastStatus;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await ProbeAsync(stoppingToken);

            try
            {
                await Task.Delay(options.PollInterval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private async Task ProbeAsync(CancellationToken cancellationToken)
    {
        try
        {
            var companies = await client.GetOpenCompaniesAsync(cancellationToken);
            stateStore.SetTally(ConnectionStatus.Connected, companies);

            if (_lastStatus != ConnectionStatus.Connected)
            {
                LogConnected(logger, companies.Count);
            }

            _lastStatus = ConnectionStatus.Connected;
        }
        catch (TallyClientException exception)
        {
            var status = exception.Kind is TallyErrorKind.Unreachable or TallyErrorKind.Timeout
                ? ConnectionStatus.Disconnected
                : ConnectionStatus.Error;

            stateStore.SetTally(status, [], exception.Message);

            if (_lastStatus != status)
            {
                LogUnavailable(logger, exception.Kind, exception.Message);
            }

            _lastStatus = status;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            const string message = "An unexpected error occurred while checking Tally.";
            stateStore.SetTally(ConnectionStatus.Error, [], message);
            if (_lastStatus != ConnectionStatus.Error)
            {
                LogUnexpected(logger, exception);
            }

            _lastStatus = ConnectionStatus.Error;
        }
    }

    [LoggerMessage(
        EventId = 2000,
        Level = LogLevel.Information,
        Message = "Tally connected; {CompanyCount} loaded companies discovered")]
    private static partial void LogConnected(ILogger logger, int companyCount);

    [LoggerMessage(
        EventId = 2001,
        Level = LogLevel.Warning,
        Message = "Tally unavailable ({ErrorKind}): {Reason}")]
    private static partial void LogUnavailable(ILogger logger, TallyErrorKind errorKind, string reason);

    [LoggerMessage(
        EventId = 2002,
        Level = LogLevel.Error,
        Message = "Unexpected error while checking Tally")]
    private static partial void LogUnexpected(ILogger logger, Exception exception);
}
