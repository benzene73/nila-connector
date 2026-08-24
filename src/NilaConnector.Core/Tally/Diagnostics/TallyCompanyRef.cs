namespace NilaConnector.Core.Tally.Diagnostics;

public sealed record TallyCompanyRef
{
    public TallyCompanyRef(string name, string tallyGuid)
        : this(name, name, tallyGuid)
    {
    }

    public TallyCompanyRef(string requestedName, string resolvedName, string tallyGuid)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requestedName);
        ArgumentException.ThrowIfNullOrWhiteSpace(resolvedName);
        ArgumentException.ThrowIfNullOrWhiteSpace(tallyGuid);
        RequestedName = requestedName.Trim();
        Name = resolvedName.Trim();
        TallyGuid = tallyGuid.Trim();
    }

    public string RequestedName { get; }

    public string Name { get; }

    public string TallyGuid { get; }
}
