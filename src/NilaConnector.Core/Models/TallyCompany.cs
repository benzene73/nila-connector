namespace NilaConnector.Core.Models;

public sealed record TallyCompany(
    string Name,
    string? TallyGuid = null,
    string? Gstin = null);
