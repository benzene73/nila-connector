using System.Text.Json;

namespace NilaConnector.Core.Nila;

public sealed record PhoenixMessage(
    string? JoinReference,
    string? Reference,
    string Topic,
    string Event,
    JsonElement Payload)
{
    public bool IsSuccessfulReplyTo(string reference) =>
        Event == "phx_reply" &&
        Reference == reference &&
        Payload.TryGetProperty("status", out var status) &&
        status.ValueKind == JsonValueKind.String &&
        status.GetString() == "ok";
}
