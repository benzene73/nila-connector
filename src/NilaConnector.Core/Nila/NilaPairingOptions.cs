namespace NilaConnector.Core.Nila;

public sealed record NilaPairingOptions
{
    public Uri BaseUri { get; init; } = new("http://127.0.0.1:4000");

    public string ConnectorVersion { get; init; } = "0.1.0";

    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(10);
}
