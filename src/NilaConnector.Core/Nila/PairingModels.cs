using System.Text.Json.Serialization;

namespace NilaConnector.Core.Nila;

public sealed record PairingStartRequest(
    [property: JsonPropertyName("device_name")] string DeviceName,
    [property: JsonPropertyName("connector_version")] string ConnectorVersion);

public sealed record PairingStartResponse(
    [property: JsonPropertyName("device_code")] string DeviceCode,
    [property: JsonPropertyName("verification_url")] Uri VerificationUrl,
    [property: JsonPropertyName("expires_in")] int ExpiresIn,
    [property: JsonPropertyName("retry_after")] int RetryAfter);

public sealed record PairingExchangeRequest(
    [property: JsonPropertyName("device_code")] string DeviceCode);

public sealed record PairingExchangeResponse(
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("device_id")] int? DeviceId,
    [property: JsonPropertyName("device_token")] string? DeviceToken,
    [property: JsonPropertyName("websocket_url")] Uri? WebSocketUrl,
    [property: JsonPropertyName("retry_after")] int? RetryAfter);
