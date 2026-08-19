using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using NilaConnector.Core.Nila;

namespace NilaConnector.Desktop.Security;

public sealed partial class DpapiDeviceCredentialStore : IDeviceCredentialStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("NilaConnector.DeviceCredential.v1");
    private readonly object _gate = new();
    private readonly string _path;
    private readonly ILogger<DpapiDeviceCredentialStore> _logger;
    private DeviceCredential? _credential;

    public DpapiDeviceCredentialStore(ILogger<DpapiDeviceCredentialStore> logger)
        : this(logger, DefaultPath())
    {
    }

    internal DpapiDeviceCredentialStore(
        ILogger<DpapiDeviceCredentialStore> logger,
        string path)
    {
        _logger = logger;
        _path = path;
        _credential = LoadInitialCredential();
    }

    public event EventHandler? Changed;

    public DeviceCredential? Load()
    {
        lock (_gate)
        {
            return _credential;
        }
    }

    public void Save(DeviceCredential credential)
    {
        ArgumentNullException.ThrowIfNull(credential);

        var plaintext = JsonSerializer.SerializeToUtf8Bytes(credential);
        var protectedBytes = ProtectedData.Protect(plaintext, Entropy, DataProtectionScope.CurrentUser);
        var directory = Path.GetDirectoryName(_path)!;
        Directory.CreateDirectory(directory);
        var temporaryPath = _path + ".tmp";

        try
        {
            File.WriteAllBytes(temporaryPath, protectedBytes);
            File.Move(temporaryPath, _path, true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }

        lock (_gate)
        {
            _credential = credential;
        }

        Changed?.Invoke(this, EventArgs.Empty);
        LogSaved(_logger, credential.DeviceId);
    }

    public void Clear()
    {
        if (File.Exists(_path))
        {
            File.Delete(_path);
        }

        lock (_gate)
        {
            _credential = null;
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    private DeviceCredential? LoadInitialCredential()
    {
        try
        {
            if (File.Exists(_path))
            {
                var protectedBytes = File.ReadAllBytes(_path);
                var plaintext = ProtectedData.Unprotect(
                    protectedBytes,
                    Entropy,
                    DataProtectionScope.CurrentUser);
                return JsonSerializer.Deserialize<DeviceCredential>(plaintext)
                    ?? throw new InvalidDataException("The saved device credential is empty.");
            }

            return LoadEnvironmentCredential();
        }
        catch (Exception exception) when (exception is CryptographicException or IOException or JsonException)
        {
            LogLoadFailed(_logger, exception);
            return null;
        }
    }

    private static DeviceCredential? LoadEnvironmentCredential()
    {
        var endpointValue = Environment.GetEnvironmentVariable("NILA_CONNECTOR_URL");
        var endpoint = Uri.TryCreate(endpointValue, UriKind.Absolute, out var configuredEndpoint)
            ? configuredEndpoint
            : new Uri("ws://127.0.0.1:4000/connector/websocket?vsn=2.0.0");

        return int.TryParse(Environment.GetEnvironmentVariable("NILA_DEVICE_ID"), out var deviceId) &&
               Environment.GetEnvironmentVariable("NILA_DEVICE_TOKEN") is { Length: > 0 } token
            ? new DeviceCredential(deviceId, token, endpoint)
            : null;
    }

    private static string DefaultPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Nila",
        "Connector",
        "device.credential");

    [LoggerMessage(EventId = 4000, Level = LogLevel.Information, Message = "Saved Nila device {DeviceId} using Windows user protection")]
    private static partial void LogSaved(ILogger logger, int deviceId);

    [LoggerMessage(EventId = 4001, Level = LogLevel.Error, Message = "Could not load the Windows-protected Nila device credential")]
    private static partial void LogLoadFailed(ILogger logger, Exception exception);
}
