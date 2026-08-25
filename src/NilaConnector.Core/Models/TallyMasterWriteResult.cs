namespace NilaConnector.Core.Models;

public sealed record TallyMasterWriteResult(
    string ObjectType,
    string Name,
    long MasterId,
    string? TallyGuid,
    string? RemoteId);
