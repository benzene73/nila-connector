using System.Text;
using System.Text.Json;

namespace NilaConnector.Core.Nila;

public static class PhoenixProtocol
{
    private const string AuthPrefix = "base64url.bearer.phx.";

    public static string AuthSubprotocol(string token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(token)).TrimEnd('=');
        return AuthPrefix + encoded;
    }

    public static byte[] Join(string reference, string topic, string connectorVersion) =>
        Encode(reference, reference, topic, "phx_join", new Dictionary<string, object?>
        {
            ["connector_version"] = connectorVersion,
        });

    public static byte[] Heartbeat(string reference) =>
        Encode(null, reference, "phoenix", "heartbeat", new Dictionary<string, object?>());

    public static byte[] JobResult(
        string joinReference,
        string reference,
        string topic,
        ConnectorJobResult result) =>
        Encode(joinReference, reference, topic, "job_result", new Dictionary<string, object?>
        {
            ["job_id"] = result.JobId,
            ["status"] = result.Status,
            ["details"] = result.Details,
        });

    public static PhoenixMessage Decode(ReadOnlySpan<byte> json)
    {
        using var document = JsonDocument.Parse(json.ToArray());
        var root = document.RootElement;

        if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() != 5)
        {
            throw new InvalidDataException("Nila returned an invalid Phoenix message.");
        }

        return new PhoenixMessage(
            OptionalString(root[0]),
            OptionalString(root[1]),
            RequiredString(root[2], "topic"),
            RequiredString(root[3], "event"),
            root[4].Clone());
    }

    private static byte[] Encode(
        string? joinReference,
        string reference,
        string topic,
        string eventName,
        object payload) =>
        JsonSerializer.SerializeToUtf8Bytes(new object?[]
        {
            joinReference,
            reference,
            topic,
            eventName,
            payload,
        });

    private static string? OptionalString(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Null => null,
        JsonValueKind.String => value.GetString(),
        _ => throw new InvalidDataException("Nila returned an invalid Phoenix reference."),
    };

    private static string RequiredString(JsonElement value, string field) =>
        value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } result
            ? result
            : throw new InvalidDataException($"Nila returned an invalid Phoenix {field}.");
}
