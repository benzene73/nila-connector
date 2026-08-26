using System.IO;

namespace NilaConnector.Desktop.Services;

public sealed record ConnectorDataPaths(string RootDirectory)
{
    private const string ProfileEnvironmentVariable = "NILA_CONNECTOR_PROFILE";

    public string CredentialPath => Path.Combine(RootDirectory, "device.credential");

    public string LogDirectory => Path.Combine(RootDirectory, "logs");

    public string TallySettingsPath => Path.Combine(RootDirectory, "settings.json");

    public static ConnectorDataPaths FromEnvironment() => Create(
        Environment.GetEnvironmentVariable(ProfileEnvironmentVariable),
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));

    internal static ConnectorDataPaths Create(string? profile, string localApplicationData)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(localApplicationData);

        var folderName = profile?.Trim().ToLowerInvariant() switch
        {
            null or "" or "production" => "Connector",
            "local" => "Connector.Local",
            _ => throw new InvalidOperationException(
                $"{ProfileEnvironmentVariable} must be either 'production' or 'local'."),
        };

        return new ConnectorDataPaths(Path.Combine(localApplicationData, "Nila", folderName));
    }
}
