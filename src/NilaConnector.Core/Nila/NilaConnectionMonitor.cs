using System.Buffers;
using System.Globalization;
using System.Net.WebSockets;
using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NilaConnector.Core.Models;
using NilaConnector.Core.State;

namespace NilaConnector.Core.Nila;

public sealed partial class NilaConnectionMonitor(
    NilaConnectionOptions options,
    IDeviceCredentialStore credentialStore,
    JobRunner jobRunner,
    SyncCoordinator syncCoordinator,
    ConnectorStateStore stateStore,
    ILogger<NilaConnectionMonitor> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var attempt = 0;
        var reportedUnlinked = false;

        while (!stoppingToken.IsCancellationRequested)
        {
            var credential = credentialStore.Load();
            if (!ValidCredential(credential))
            {
                stateStore.SetNila(ConnectionStatus.Disconnected, "Device is not linked to Nila.");
                if (!reportedUnlinked)
                {
                    LogNotLinked(logger);
                    reportedUnlinked = true;
                }

                await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
                continue;
            }

            reportedUnlinked = false;
            stateStore.SetNila(ConnectionStatus.Connecting);

            try
            {
                await RunSessionAsync(credential!, () => attempt = 0, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception) when (exception is WebSocketException or IOException or InvalidDataException or TimeoutException)
            {
                stateStore.SetNila(ConnectionStatus.Disconnected, "Could not reach Nila.");
                LogDisconnected(logger, exception.Message);
            }

            attempt++;
            var delay = ReconnectDelay(attempt);

            try
            {
                await Task.Delay(delay, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }

        stateStore.SetNila(ConnectionStatus.Disconnected);
    }

    private async Task RunSessionAsync(
        DeviceCredential credential,
        Action connected,
        CancellationToken stoppingToken)
    {
        using var socket = new ClientWebSocket();
        using var sendLock = new SemaphoreSlim(1, 1);
        socket.Options.AddSubProtocol(PhoenixProtocol.AuthSubprotocol(credential.DeviceToken));
        socket.Options.AddSubProtocol("phoenix");

        using var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        connectTimeout.CancelAfter(options.ConnectTimeout);
        try
        {
            await socket.ConnectAsync(credential.WebSocketEndpoint, connectTimeout.Token);
        }
        catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
        {
            throw new TimeoutException("Timed out connecting to Nila.");
        }

        var topic = $"tally_device:{credential.DeviceId}";
        const string joinReference = "1";
        var reportedCompanies = stateStore.Current.Companies.ToArray();
        await SendAsync(
            socket,
            PhoenixProtocol.Join(joinReference, topic, options.ConnectorVersion, reportedCompanies),
            sendLock,
            stoppingToken);

        using var joinTimeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        joinTimeout.CancelAfter(options.ConnectTimeout);

        PhoenixMessage joinReply;
        try
        {
            joinReply = await ReceiveAsync(socket, joinTimeout.Token);
        }
        catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
        {
            throw new TimeoutException("Timed out joining the Nila device channel.");
        }
        if (!joinReply.IsSuccessfulReplyTo(joinReference))
        {
            throw new InvalidDataException("Nila rejected the device channel join.");
        }

        stateStore.SetNila(ConnectionStatus.Connected);
        LogConnected(logger, credential.DeviceId);
        connected();

        using var session = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var companiesChanged = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler<ConnectorState> stateChanged = (_, state) =>
        {
            if (!state.Companies.SequenceEqual(reportedCompanies))
            {
                companiesChanged.TrySetResult();
            }
        };
        stateStore.Changed += stateChanged;
        var heartbeatAcknowledgements = Channel.CreateUnbounded<string>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = true,
        });
        var receive = ReceiveLoopAsync(
            socket,
            topic,
            joinReference,
            sendLock,
            heartbeatAcknowledgements.Writer,
            session.Token);
        var heartbeat = HeartbeatLoopAsync(
            socket,
            sendLock,
            heartbeatAcknowledgements.Reader,
            session.Token);
        try
        {
            var completed = await Task.WhenAny(receive, heartbeat, companiesChanged.Task);
            await session.CancelAsync();
            if (completed != companiesChanged.Task)
            {
                await completed;
            }
        }
        finally
        {
            stateStore.Changed -= stateChanged;
        }
    }

    private async Task ReceiveLoopAsync(
        ClientWebSocket socket,
        string topic,
        string joinReference,
        SemaphoreSlim sendLock,
        ChannelWriter<string> heartbeatAcknowledgements,
        CancellationToken cancellationToken)
    {
        Task<PhoenixMessage>? receiveTask = null;
        Task<ConnectorJobResult>? jobTask = null;
        Task? syncTask = null;
        Task? resultAcknowledgementTimeout = null;
        string? resultReference = null;
        string? resultStatus = null;
        var resultJobId = 0;
        var queuedSyncs = new Queue<(TallyCompany Company, SyncRunResponse Run)>();

        try
        {
            while (socket.State == WebSocketState.Open)
            {
                receiveTask ??= ReceiveAsync(socket, cancellationToken);
                var pending = new List<Task> { receiveTask };
                if (jobTask is not null)
                {
                    pending.Add(jobTask);
                }

                if (syncTask is not null)
                {
                    pending.Add(syncTask);
                }

                if (resultAcknowledgementTimeout is not null)
                {
                    pending.Add(resultAcknowledgementTimeout);
                }

                var completed = await Task.WhenAny(pending);

                if (completed == jobTask)
                {
                    var result = await jobTask;
                    resultJobId = result.JobId;
                    resultReference = $"result-{result.JobId}";
                    resultStatus = result.Status;
                    await SendAsync(
                        socket,
                        PhoenixProtocol.JobResult(
                            joinReference,
                            resultReference,
                            topic,
                            result),
                        sendLock,
                        cancellationToken);
                    resultAcknowledgementTimeout = Task.Delay(options.ConnectTimeout, cancellationToken);
                    jobTask = null;
                    continue;
                }

                if (completed == syncTask)
                {
                    try
                    {
                        await syncTask;
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception exception)
                    {
                        LogSyncRequestFailed(logger, exception.GetType().Name);
                    }

                    syncTask = null;
                    while (queuedSyncs.TryDequeue(out var queued))
                    {
                        var stillOpen = stateStore.Current.Companies.FirstOrDefault(candidate =>
                            SyncCoordinator.MatchesCompany(candidate, SyncCoordinator.CompanyIdentity(queued.Company)));
                        if (stillOpen is null)
                        {
                            await syncCoordinator.FailRequestedAsync(
                                queued.Run.Id,
                                "The requested company is no longer open in Tally.",
                                cancellationToken);
                            continue;
                        }

                        syncTask = syncCoordinator.SyncRequestedAsync(stillOpen, queued.Run, cancellationToken);
                        break;
                    }

                    continue;
                }

                if (completed == resultAcknowledgementTimeout)
                {
                    await resultAcknowledgementTimeout;
                    throw new TimeoutException("Nila did not acknowledge the Tally job result.");
                }

                var message = await receiveTask;
                receiveTask = null;
                if (message.Topic == "phoenix" &&
                    message.Reference is { } reference &&
                    message.IsSuccessfulReplyTo(reference))
                {
                    _ = heartbeatAcknowledgements.TryWrite(reference);
                }
                else if (resultReference is not null &&
                         message.Topic == topic &&
                         message.Reference == resultReference &&
                         message.Event == "phx_reply")
                {
                    if (!message.IsSuccessfulReplyTo(resultReference))
                    {
                        throw new InvalidDataException("Nila rejected the Tally job result.");
                    }

                    LogResultAcknowledged(logger, resultJobId, resultStatus ?? "unknown");
                    resultJobId = 0;
                    resultReference = null;
                    resultStatus = null;
                    resultAcknowledgementTimeout = null;
                }
                else if (message.Topic == topic && message.Event == "job")
                {
                    if (jobTask is not null || resultReference is not null)
                    {
                        throw new InvalidDataException("Nila delivered overlapping Tally jobs.");
                    }

                    jobTask = jobRunner.ExecuteAsync(message.Payload, cancellationToken);
                }
                else if (message.Topic == topic && message.Event == "sync_requested")
                {
                    var request = DecodeSyncRequest(message.Payload);
                    var company = stateStore.Current.Companies.FirstOrDefault(candidate =>
                        SyncCoordinator.MatchesCompany(candidate, request.CompanyIdentity));
                    if (company is null)
                    {
                        LogSyncRequestIgnored(logger, "requested company is not open in Tally");
                        await syncCoordinator.FailRequestedAsync(
                            request.Id,
                            "The requested company is not open in Tally.",
                            cancellationToken);
                        continue;
                    }

                    var run = new SyncRunResponse(
                            request.Id,
                            request.PeriodStart,
                            request.PeriodEnd,
                            "syncing",
                            "pending",
                            "pending",
                            null,
                            null);
                    if (syncTask is not null)
                    {
                        queuedSyncs.Enqueue((company, run));
                    }
                    else
                    {
                        syncTask = syncCoordinator.SyncRequestedAsync(company, run, cancellationToken);
                    }
                }
            }
        }
        finally
        {
            heartbeatAcknowledgements.TryComplete();
        }

        throw new IOException("The Nila WebSocket closed.");
    }

    private async Task HeartbeatLoopAsync(
        ClientWebSocket socket,
        SemaphoreSlim sendLock,
        ChannelReader<string> heartbeatAcknowledgements,
        CancellationToken cancellationToken)
    {
        var reference = 1;

        while (true)
        {
            await Task.Delay(options.HeartbeatInterval, cancellationToken);
            reference++;
            await SendAsync(
                socket,
                PhoenixProtocol.Heartbeat(reference.ToString(CultureInfo.InvariantCulture)),
                sendLock,
                cancellationToken);

            using var acknowledgementTimeout =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            acknowledgementTimeout.CancelAfter(options.HeartbeatAcknowledgementTimeout);

            try
            {
                var expectedReference = reference.ToString(CultureInfo.InvariantCulture);
                string receivedReference;
                do
                {
                    receivedReference = await heartbeatAcknowledgements.ReadAsync(
                        acknowledgementTimeout.Token);
                }
                while (receivedReference != expectedReference);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException("Nila did not acknowledge the WebSocket heartbeat.");
            }
            catch (ChannelClosedException exception)
            {
                throw new IOException("The Nila WebSocket receive loop ended.", exception);
            }
        }
    }

    private async Task<PhoenixMessage> ReceiveAsync(
        ClientWebSocket socket,
        CancellationToken cancellationToken)
    {
        var writer = new ArrayBufferWriter<byte>();

        while (true)
        {
            var memory = writer.GetMemory(4096);
            var result = await socket.ReceiveAsync(memory, cancellationToken);

            if (result.MessageType == WebSocketMessageType.Close)
            {
                throw new IOException($"Nila closed the WebSocket ({socket.CloseStatus}).");
            }

            if (result.MessageType != WebSocketMessageType.Text)
            {
                throw new InvalidDataException("Nila returned a non-text WebSocket message.");
            }

            writer.Advance(result.Count);
            if (writer.WrittenCount > options.MaxMessageBytes)
            {
                throw new InvalidDataException("Nila returned an oversized WebSocket message.");
            }

            if (result.EndOfMessage)
            {
                return PhoenixProtocol.Decode(writer.WrittenSpan);
            }
        }
    }

    private static async Task SendAsync(
        ClientWebSocket socket,
        ReadOnlyMemory<byte> message,
        SemaphoreSlim sendLock,
        CancellationToken cancellationToken)
    {
        await sendLock.WaitAsync(cancellationToken);
        try
        {
            await socket.SendAsync(message, WebSocketMessageType.Text, true, cancellationToken);
        }
        finally
        {
            sendLock.Release();
        }
    }

    private static bool ValidCredential(DeviceCredential? credential) =>
        credential is
        {
            DeviceId: > 0,
            DeviceToken.Length: > 0,
            WebSocketEndpoint.IsAbsoluteUri: true,
        } &&
        (credential.WebSocketEndpoint.Scheme == "wss" ||
         (credential.WebSocketEndpoint.Scheme == "ws" && credential.WebSocketEndpoint.IsLoopback));

    private static SyncRequestedMessage DecodeSyncRequest(System.Text.Json.JsonElement payload)
    {
        if (payload.ValueKind != System.Text.Json.JsonValueKind.Object ||
            !payload.TryGetProperty("id", out var id) || !id.TryGetInt64(out var runId) || runId <= 0 ||
            !payload.TryGetProperty("company_identity", out var identity) || identity.GetString() is not { Length: > 0 } companyIdentity ||
            !payload.TryGetProperty("period_start", out var start) || !DateOnly.TryParse(start.GetString(), out var periodStart) ||
            !payload.TryGetProperty("period_end", out var end) || !DateOnly.TryParse(end.GetString(), out var periodEnd) ||
            periodEnd < periodStart)
        {
            throw new InvalidDataException("Nila sent an invalid Tally sync request.");
        }

        return new SyncRequestedMessage(runId, companyIdentity, periodStart, periodEnd);
    }

    private static TimeSpan ReconnectDelay(int attempt)
    {
        var seconds = attempt switch
        {
            1 => 1,
            2 => 2,
            3 => 5,
            4 => 10,
            _ => 30,
        };

        return TimeSpan.FromMilliseconds((seconds * 1000) + Random.Shared.Next(0, 251));
    }

    [LoggerMessage(EventId = 3000, Level = LogLevel.Information, Message = "Nila device {DeviceId} connected")]
    private static partial void LogConnected(ILogger logger, int deviceId);

    [LoggerMessage(EventId = 3001, Level = LogLevel.Warning, Message = "Nila disconnected: {Reason}")]
    private static partial void LogDisconnected(ILogger logger, string reason);

    [LoggerMessage(EventId = 3002, Level = LogLevel.Information, Message = "Nila device is not linked")]
    private static partial void LogNotLinked(ILogger logger);

    [LoggerMessage(EventId = 3003, Level = LogLevel.Information, Message = "Nila acknowledged Tally job {JobId} as {Status}")]
    private static partial void LogResultAcknowledged(ILogger logger, int jobId, string status);

    [LoggerMessage(EventId = 3004, Level = LogLevel.Warning, Message = "Could not run Nila sync request ({ExceptionType})")]
    private static partial void LogSyncRequestFailed(ILogger logger, string exceptionType);

    [LoggerMessage(EventId = 3005, Level = LogLevel.Information, Message = "Ignored Nila sync request: {Reason}")]
    private static partial void LogSyncRequestIgnored(ILogger logger, string reason);

    private sealed record SyncRequestedMessage(
        long Id,
        string CompanyIdentity,
        DateOnly PeriodStart,
        DateOnly PeriodEnd);
}
