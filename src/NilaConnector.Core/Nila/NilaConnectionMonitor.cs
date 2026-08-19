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
        await SendAsync(
            socket,
            PhoenixProtocol.Join(joinReference, topic, options.ConnectorVersion),
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
        var completed = await Task.WhenAny(receive, heartbeat);
        await session.CancelAsync();
        await completed;
    }

    private async Task ReceiveLoopAsync(
        ClientWebSocket socket,
        string topic,
        string joinReference,
        SemaphoreSlim sendLock,
        ChannelWriter<string> heartbeatAcknowledgements,
        CancellationToken cancellationToken)
    {
        try
        {
            while (socket.State == WebSocketState.Open)
            {
                var message = await ReceiveAsync(socket, cancellationToken);
                if (message.Topic == "phoenix" &&
                    message.Reference is { } reference &&
                    message.IsSuccessfulReplyTo(reference))
                {
                    _ = heartbeatAcknowledgements.TryWrite(reference);
                }
                else if (message.Topic == topic && message.Event == "job")
                {
                    var result = await jobRunner.ExecuteAsync(message.Payload, cancellationToken);
                    await SendJobResultAsync(
                        socket,
                        topic,
                        joinReference,
                        result,
                        sendLock,
                        heartbeatAcknowledgements,
                        cancellationToken);
                }
            }
        }
        finally
        {
            heartbeatAcknowledgements.TryComplete();
        }

        throw new IOException("The Nila WebSocket closed.");
    }

    private async Task SendJobResultAsync(
        ClientWebSocket socket,
        string topic,
        string joinReference,
        ConnectorJobResult result,
        SemaphoreSlim sendLock,
        ChannelWriter<string> heartbeatAcknowledgements,
        CancellationToken cancellationToken)
    {
        var reference = $"result-{result.JobId}";
        await SendAsync(
            socket,
            PhoenixProtocol.JobResult(joinReference, reference, topic, result),
            sendLock,
            cancellationToken);

        using var acknowledgementTimeout =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        acknowledgementTimeout.CancelAfter(options.ConnectTimeout);

        while (true)
        {
            PhoenixMessage reply;
            try
            {
                reply = await ReceiveAsync(socket, acknowledgementTimeout.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException($"Nila did not acknowledge Tally job {result.JobId}.");
            }

            if (reply.Topic == topic && reply.Reference == reference && reply.Event == "phx_reply")
            {
                if (!reply.IsSuccessfulReplyTo(reference))
                {
                    throw new InvalidDataException($"Nila rejected the result for Tally job {result.JobId}.");
                }

                LogResultAcknowledged(logger, result.JobId, result.Status);
                return;
            }

            if (reply.Topic == "phoenix" &&
                reply.Reference is { } heartbeatReference &&
                reply.IsSuccessfulReplyTo(heartbeatReference))
            {
                _ = heartbeatAcknowledgements.TryWrite(heartbeatReference);
                continue;
            }

            throw new InvalidDataException("Nila sent an unexpected message while acknowledging a job result.");
        }
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
}
