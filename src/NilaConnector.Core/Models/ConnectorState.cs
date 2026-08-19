namespace NilaConnector.Core.Models;

public sealed record ConnectorState(
    ConnectionStatus Nila,
    ConnectionStatus Tally,
    IReadOnlyList<TallyCompany> Companies,
    string? TallyError,
    string? NilaError)
{
    public static ConnectorState Initial { get; } = new(
        ConnectionStatus.Disconnected,
        ConnectionStatus.Disconnected,
        [],
        null,
        null);
}
