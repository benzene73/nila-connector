using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;

namespace NilaConnector.Core.Nila;

public sealed class NilaPairingClient
{
    private readonly HttpClient _httpClient;
    private readonly NilaPairingOptions _options;

    public NilaPairingClient(HttpClient httpClient, NilaPairingOptions options)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(options);

        if (!options.BaseUri.IsAbsoluteUri ||
            (options.BaseUri.Scheme != Uri.UriSchemeHttps &&
             !(options.BaseUri.Scheme == Uri.UriSchemeHttp && options.BaseUri.IsLoopback)))
        {
            throw new ArgumentException(
                "Nila must use HTTPS except for loopback development.",
                nameof(options));
        }

        _httpClient = httpClient;
        _options = options;
    }

    public async Task<PairingStartResponse> StartAsync(
        string deviceName,
        CancellationToken cancellationToken)
    {
        using var timeout = CreateTimeout(cancellationToken);
        using var response = await _httpClient.PostAsJsonAsync(
            new Uri(_options.BaseUri, "/api/tally/pairings"),
            new PairingStartRequest(deviceName, _options.ConnectorVersion),
            timeout.Token);

        response.EnsureSuccessStatusCode();
        var pairing = await response.Content.ReadFromJsonAsync<PairingStartResponse>(timeout.Token)
            ?? throw new InvalidDataException("Nila returned an empty pairing response.");

        if (!ExpectedHttpEndpoint(pairing.VerificationUrl) ||
            !pairing.VerificationUrl.AbsolutePath.StartsWith(
                "/settings/connectors/tally/pair/",
                StringComparison.Ordinal))
        {
            throw new InvalidDataException("Nila returned an invalid verification URL.");
        }

        return pairing;
    }

    public async Task<PairingExchangeResponse> ExchangeAsync(
        string deviceCode,
        CancellationToken cancellationToken)
    {
        using var timeout = CreateTimeout(cancellationToken);
        using var response = await _httpClient.PostAsJsonAsync(
            new Uri(_options.BaseUri, "/api/tally/pairings/exchange"),
            new PairingExchangeRequest(deviceCode),
            timeout.Token);

        if (response.StatusCode is not (HttpStatusCode.OK or HttpStatusCode.Accepted))
        {
            response.EnsureSuccessStatusCode();
        }

        var exchange = await response.Content.ReadFromJsonAsync<PairingExchangeResponse>(timeout.Token)
            ?? throw new InvalidDataException("Nila returned an empty pairing response.");

        if (exchange.WebSocketUrl is { } endpoint && !ExpectedWebSocketEndpoint(endpoint))
        {
            throw new InvalidDataException("Nila returned an invalid WebSocket endpoint.");
        }

        return exchange;
    }

    public async Task RevokeAsync(DeviceCredential credential, CancellationToken cancellationToken)
    {
        using var timeout = CreateTimeout(cancellationToken);
        using var request = new HttpRequestMessage(
            HttpMethod.Delete,
            new Uri(_options.BaseUri, "/api/tally/device"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential.DeviceToken);

        using var response = await _httpClient.SendAsync(request, timeout.Token);
        response.EnsureSuccessStatusCode();
    }

    private CancellationTokenSource CreateTimeout(CancellationToken cancellationToken)
    {
        var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_options.RequestTimeout);
        return timeout;
    }

    private bool ExpectedHttpEndpoint(Uri endpoint) =>
        endpoint.IsAbsoluteUri &&
        (endpoint.Scheme == Uri.UriSchemeHttps ||
         (endpoint.Scheme == Uri.UriSchemeHttp && endpoint.IsLoopback)) &&
        SameAuthority(endpoint);

    private bool ExpectedWebSocketEndpoint(Uri endpoint) =>
        endpoint.IsAbsoluteUri &&
        (endpoint.Scheme == "wss" || (endpoint.Scheme == "ws" && endpoint.IsLoopback)) &&
        endpoint.AbsolutePath == "/connector/websocket" &&
        SameAuthority(endpoint);

    private bool SameAuthority(Uri endpoint) =>
        endpoint.Port == _options.BaseUri.Port &&
        ((endpoint.IsLoopback && _options.BaseUri.IsLoopback) ||
         string.Equals(endpoint.IdnHost, _options.BaseUri.IdnHost, StringComparison.OrdinalIgnoreCase));
}
