using System.Net;
using System.Text;
using NilaConnector.Core.Models;

namespace NilaConnector.Core.Tally;

public sealed class TallyClient : ITallyClient, IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly TallyClientOptions _options;
    private readonly SemaphoreSlim _requestLock = new(1, 1);
    private bool _disposed;

    public TallyClient(HttpClient httpClient, TallyClientOptions options)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(options);

        if (options.Endpoint.Scheme is not ("http" or "https"))
        {
            throw new ArgumentException("The Tally endpoint must use HTTP or HTTPS.", nameof(options));
        }

        if (options.MaxResponseBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "The response limit must be positive.");
        }

        _httpClient = httpClient;
        _options = options;
    }

    public Task<IReadOnlyList<TallyCompany>> GetOpenCompaniesAsync(CancellationToken cancellationToken) =>
        SendAsync(TallyRequests.OpenCompanies, TallyResponseParser.ParseOpenCompaniesAsync, cancellationToken);

    public Task<TallyVoucherResult> CreateVoucherAsync(
        CreateVoucherCommand command,
        CancellationToken cancellationToken) =>
        SendAsync(
            TallyRequests.CreateVoucher(command),
            TallyResponseParser.ParseVoucherImportAsync,
            cancellationToken);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _requestLock.Dispose();
        _httpClient.Dispose();
        _disposed = true;
    }

    private async Task<T> SendAsync<T>(
        string requestXml,
        Func<Stream, int, CancellationToken, Task<T>> parse,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _requestLock.WaitAsync(cancellationToken);

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(_options.RequestTimeout);

            using var request = new HttpRequestMessage(HttpMethod.Post, _options.Endpoint)
            {
                Content = new StringContent(requestXml, Encoding.UTF8, "text/xml"),
            };

            using var response = await SendHttpRequestAsync(request, cancellationToken, timeout.Token);
            ValidateStatus(response.StatusCode);
            ValidateContentLength(response.Content.Headers.ContentLength);

            await using var responseStream = await response.Content.ReadAsStreamAsync(timeout.Token);
            await using var boundedStream = await ReadBoundedAsync(responseStream, timeout.Token);

            return await parse(boundedStream, _options.MaxResponseBytes, timeout.Token);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TallyClientException(TallyErrorKind.Timeout, "Tally did not respond in time.", exception);
        }
        catch (HttpRequestException exception)
        {
            throw new TallyClientException(TallyErrorKind.Unreachable, "Tally is not reachable.", exception);
        }
        catch (IOException exception)
        {
            throw new TallyClientException(
                TallyErrorKind.Unreachable,
                "The connection to Tally ended before its response was complete.",
                exception);
        }
        finally
        {
            _requestLock.Release();
        }
    }

    private async Task<HttpResponseMessage> SendHttpRequestAsync(
        HttpRequestMessage request,
        CancellationToken callerToken,
        CancellationToken timeoutToken)
    {
        try
        {
            return await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeoutToken);
        }
        catch (OperationCanceledException exception) when (!callerToken.IsCancellationRequested)
        {
            throw new TallyClientException(TallyErrorKind.Timeout, "Tally did not respond in time.", exception);
        }
        catch (HttpRequestException exception)
        {
            throw new TallyClientException(TallyErrorKind.Unreachable, "Tally is not reachable.", exception);
        }
    }

    private void ValidateContentLength(long? contentLength)
    {
        if (contentLength > _options.MaxResponseBytes)
        {
            throw new TallyClientException(
                TallyErrorKind.ResponseTooLarge,
                $"Tally returned more than {_options.MaxResponseBytes} bytes.");
        }
    }

    private static void ValidateStatus(HttpStatusCode statusCode)
    {
        if ((int)statusCode is >= 200 and <= 299)
        {
            return;
        }

        throw new TallyClientException(
            TallyErrorKind.HttpError,
            $"Tally returned HTTP status {(int)statusCode}.");
    }

    private async Task<MemoryStream> ReadBoundedAsync(Stream source, CancellationToken cancellationToken)
    {
        var destination = new MemoryStream();
        var buffer = new byte[16 * 1024];

        try
        {
            while (true)
            {
                var read = await source.ReadAsync(buffer, cancellationToken);
                if (read == 0)
                {
                    destination.Position = 0;
                    return destination;
                }

                if (destination.Length + read > _options.MaxResponseBytes)
                {
                    throw new TallyClientException(
                        TallyErrorKind.ResponseTooLarge,
                        $"Tally returned more than {_options.MaxResponseBytes} bytes.");
                }

                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            }
        }
        catch
        {
            await destination.DisposeAsync();
            throw;
        }
    }
}
