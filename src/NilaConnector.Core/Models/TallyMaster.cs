using System.Text.Json.Serialization;

namespace NilaConnector.Core.Models;

public sealed record TallyMaster(
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("identity")] string Identity,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("tally_guid")] string? TallyGuid,
    [property: JsonPropertyName("alter_id")] string? AlterId,
    [property: JsonPropertyName("data")] IReadOnlyDictionary<string, object?> Data);
