using System.Net;
using System.Net.Sockets;
using System.Text;

namespace NilaConnector.Core.Tests.Support;

internal sealed class FakeTallyServer : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _stopping = new();
    private readonly Task<IReadOnlyList<string>> _requestBodies;

    public FakeTallyServer(string responseBody, HttpStatusCode statusCode = HttpStatusCode.OK)
    {
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        var port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        Endpoint = new Uri($"http://127.0.0.1:{port}/");
        _requestBodies = HandleManyAsync([responseBody], statusCode, _stopping.Token);
    }

    public FakeTallyServer(params string[] responseBodies)
    {
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        var port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        Endpoint = new Uri($"http://127.0.0.1:{port}/");
        _requestBodies = HandleManyAsync(responseBodies, HttpStatusCode.OK, _stopping.Token);
    }

    public Uri Endpoint { get; }

    public Task<string> RequestBody => FirstRequestAsync();

    public Task<IReadOnlyList<string>> RequestBodies => _requestBodies;

    private async Task<string> FirstRequestAsync() => (await _requestBodies)[0];

    public async ValueTask DisposeAsync()
    {
        await _stopping.CancelAsync();
        _listener.Stop();
        _stopping.Dispose();
    }

    private async Task<IReadOnlyList<string>> HandleManyAsync(
        IReadOnlyList<string> responseBodies,
        HttpStatusCode statusCode,
        CancellationToken cancellationToken)
    {
        var requests = new List<string>();
        foreach (var responseBody in responseBodies)
        {
            using var client = await _listener.AcceptTcpClientAsync(cancellationToken);
            await using var stream = client.GetStream();
            var (headers, bodyPrefix) = await ReadHeadersAsync(stream, cancellationToken);
            var contentLength = ParseContentLength(headers);

            using var body = new MemoryStream();
            await body.WriteAsync(bodyPrefix, cancellationToken);
            var buffer = new byte[4096];

            while (body.Length < contentLength)
            {
                var remaining = Math.Min(buffer.Length, contentLength - (int)body.Length);
                var read = await stream.ReadAsync(buffer.AsMemory(0, remaining), cancellationToken);
                if (read == 0)
                {
                    break;
                }

                await body.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            }

            var responseBytes = Encoding.UTF8.GetBytes(responseBody);
            var reason = statusCode == HttpStatusCode.OK ? "OK" : "Error";
            var responseHeaders = Encoding.ASCII.GetBytes(
                $"HTTP/1.1 {(int)statusCode} {reason}\r\n" +
                "Content-Type: text/xml; charset=utf-8\r\n" +
                $"Content-Length: {responseBytes.Length}\r\n" +
                "Connection: close\r\n\r\n");

            await stream.WriteAsync(responseHeaders, cancellationToken);
            await stream.WriteAsync(responseBytes, cancellationToken);
            await stream.FlushAsync(cancellationToken);
            requests.Add(DecodeBody(headers, body.ToArray()));
        }

        return requests;
    }

    private static async Task<(string Headers, ReadOnlyMemory<byte> BodyPrefix)> ReadHeadersAsync(
        NetworkStream stream,
        CancellationToken cancellationToken)
    {
        using var received = new MemoryStream();
        var buffer = new byte[1024];

        while (received.Length < 64 * 1024)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken);
            if (read == 0)
            {
                break;
            }

            await received.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            var bytes = received.ToArray();
            var headerEnd = FindHeaderEnd(bytes);
            if (headerEnd >= 0)
            {
                return (
                    Encoding.ASCII.GetString(bytes, 0, headerEnd),
                    bytes.AsMemory(headerEnd + 4));
            }
        }

        throw new InvalidDataException("The fake Tally server received invalid HTTP headers.");
    }

    private static int FindHeaderEnd(byte[] bytes)
    {
        for (var index = 0; index <= bytes.Length - 4; index++)
        {
            if (bytes[index] == '\r' && bytes[index + 1] == '\n' &&
                bytes[index + 2] == '\r' && bytes[index + 3] == '\n')
            {
                return index;
            }
        }

        return -1;
    }

    private static int ParseContentLength(string headers)
    {
        var line = headers
            .Split("\r\n", StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault(value => value.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase));

        return line is not null && int.TryParse(line["Content-Length:".Length..].Trim(), out var length)
            ? length
            : 0;
    }

    private static string DecodeBody(string headers, byte[] body)
    {
        if (headers.Contains("charset=utf-16", StringComparison.OrdinalIgnoreCase) ||
            headers.Contains("charset=\"utf-16\"", StringComparison.OrdinalIgnoreCase))
        {
            return Encoding.Unicode.GetString(body);
        }

        return Encoding.UTF8.GetString(body);
    }
}
