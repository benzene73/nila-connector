namespace NilaConnector.Core.Nila;

public sealed record NilaConnectionOptions
{
    public string ConnectorVersion { get; init; } = "0.1.0";

    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(10);

    public TimeSpan HeartbeatInterval { get; init; } = TimeSpan.FromSeconds(25);

    public TimeSpan HeartbeatAcknowledgementTimeout { get; init; } = TimeSpan.FromSeconds(10);

    public int MaxMessageBytes { get; init; } = 256 * 1024;
}
