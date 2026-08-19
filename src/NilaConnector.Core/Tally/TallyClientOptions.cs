namespace NilaConnector.Core.Tally;

public sealed class TallyClientOptions
{
    private Uri _endpoint = new("http://127.0.0.1:9000/");

    public Uri Endpoint
    {
        get => Volatile.Read(ref _endpoint);
        init => _endpoint = ValidateEndpoint(value);
    }

    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(5);

    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(5);

    public int MaxResponseBytes { get; init; } = 16 * 1024 * 1024;

    public void UpdateEndpoint(Uri endpoint) =>
        Volatile.Write(ref _endpoint, ValidateEndpoint(endpoint));

    private static Uri ValidateEndpoint(Uri endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        if (!endpoint.IsAbsoluteUri || endpoint.Scheme is not "http")
        {
            throw new ArgumentException("The Tally endpoint must be an absolute HTTP URL.", nameof(endpoint));
        }

        return endpoint;
    }
}
