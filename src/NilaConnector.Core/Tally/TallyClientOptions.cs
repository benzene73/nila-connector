namespace NilaConnector.Core.Tally;

public sealed record TallyClientOptions
{
    public Uri Endpoint { get; init; } = new("http://127.0.0.1:9000/");

    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(5);

    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(5);

    public int MaxResponseBytes { get; init; } = 2 * 1024 * 1024;
}
