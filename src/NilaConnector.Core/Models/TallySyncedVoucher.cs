using System.Text.Json.Serialization;

namespace NilaConnector.Core.Models;

public sealed record TallySyncedVoucher(
    [property: JsonPropertyName("identity")] string Identity,
    [property: JsonPropertyName("master_id")] long? MasterId,
    [property: JsonPropertyName("tally_guid")] string? TallyGuid,
    [property: JsonPropertyName("remote_id")] string? RemoteId,
    [property: JsonPropertyName("date")] DateOnly Date,
    [property: JsonPropertyName("voucher_type")] string VoucherType,
    [property: JsonPropertyName("voucher_number")] string? VoucherNumber,
    [property: JsonPropertyName("narration")] string? Narration,
    [property: JsonPropertyName("data")] IReadOnlyDictionary<string, object?> Data);
