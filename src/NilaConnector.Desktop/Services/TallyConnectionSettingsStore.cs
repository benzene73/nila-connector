using System.Globalization;
using System.IO;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using NilaConnector.Core.Tally;

namespace NilaConnector.Desktop.Services;

public sealed partial class TallyConnectionSettingsStore
{
    private const string DefaultHost = "127.0.0.1";
    private const int DefaultPort = 9000;

    private readonly TallyClientOptions _options;
    private readonly ILogger<TallyConnectionSettingsStore> _logger;
    private readonly string _path;

    public TallyConnectionSettingsStore(
        TallyClientOptions options,
        ILogger<TallyConnectionSettingsStore> logger,
        string? path = null)
    {
        _options = options;
        _logger = logger;
        _path = path ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Nila",
            "Connector",
            "settings.json");

        Load();
    }

    public string Host => _options.Endpoint.Host;

    public int Port => _options.Endpoint.Port;

    public Uri Endpoint => _options.Endpoint;

    public async Task SaveAsync(string host, string portText, CancellationToken cancellationToken = default)
    {
        var endpoint = BuildEndpoint(host, portText);
        var directory = Path.GetDirectoryName(_path)
            ?? throw new InvalidOperationException("The settings path has no directory.");
        Directory.CreateDirectory(directory);

        var temporaryPath = $"{_path}.{Guid.NewGuid():N}.tmp";
        try
        {
            var payload = JsonSerializer.Serialize(new StoredSettings(endpoint.Host, endpoint.Port));
            await File.WriteAllTextAsync(temporaryPath, payload, cancellationToken);
            File.Move(temporaryPath, _path, overwrite: true);
            _options.UpdateEndpoint(endpoint);
            LogSaved(_logger, endpoint.Host, endpoint.Port);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    internal static Uri BuildEndpoint(string host, string portText)
    {
        var normalizedHost = host.Trim();
        if (normalizedHost.Length == 0 || normalizedHost.Length > 253 ||
            normalizedHost.Contains('/') || normalizedHost.Contains('\\') ||
            normalizedHost.Contains(':'))
        {
            throw new ArgumentException("Enter a valid Tally host, such as localhost or 127.0.0.1.", nameof(host));
        }

        if (!int.TryParse(portText.Trim(), out var port) || port is < 1 or > 65535)
        {
            throw new ArgumentException("Enter a port between 1 and 65535.", nameof(portText));
        }

        return new UriBuilder(Uri.UriSchemeHttp, normalizedHost, port, "/").Uri;
    }

    private void Load()
    {
        if (!File.Exists(_path))
        {
            return;
        }

        try
        {
            var stored = JsonSerializer.Deserialize<StoredSettings>(File.ReadAllText(_path));
            if (stored is null)
            {
                throw new JsonException("The settings file was empty.");
            }

            _options.UpdateEndpoint(BuildEndpoint(
                stored.Host,
                stored.Port.ToString(CultureInfo.InvariantCulture)));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            _options.UpdateEndpoint(new Uri($"http://{DefaultHost}:{DefaultPort}/"));
            LogInvalid(_logger, exception.GetType().Name);
        }
    }

    private sealed record StoredSettings(string Host, int Port);

    [LoggerMessage(EventId = 5000, Level = LogLevel.Information,
        Message = "Tally endpoint saved as {Host}:{Port}")]
    private static partial void LogSaved(ILogger logger, string host, int port);

    [LoggerMessage(EventId = 5001, Level = LogLevel.Warning,
        Message = "Tally settings could not be loaded ({ExceptionType}); using 127.0.0.1:9000")]
    private static partial void LogInvalid(ILogger logger, string exceptionType);
}
