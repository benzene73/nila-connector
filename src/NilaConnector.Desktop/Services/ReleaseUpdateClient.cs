using System.IO;
using System.Net.Http;
using System.Text.Json;

namespace NilaConnector.Desktop.Services;

public sealed class ReleaseUpdateClient(HttpClient httpClient) : IDisposable
{
    public const string ReleaseNotesUrl = "https://github.com/KaguyaHQ/nila-downloads/releases/latest";
    private const string InstallerName = "NilaConnector-win-x64-setup.exe";

    public async Task<ConnectorRelease> GetLatestAsync(CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get,
            "https://api.github.com/repos/KaguyaHQ/nila-downloads/releases/latest");
        request.Headers.UserAgent.ParseAdd("NilaConnector");
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        request.Headers.Add("X-GitHub-Api-Version", "2026-03-10");
        using var response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var json = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        return ParseRelease(json.RootElement);
    }

    internal static ConnectorRelease ParseRelease(JsonElement release)
    {
        if (release.GetProperty("draft").GetBoolean() || release.GetProperty("prerelease").GetBoolean())
        {
            throw new InvalidDataException("Expected a published stable release.");
        }

        var tag = release.GetProperty("tag_name").GetString();
        if (tag is null || !tag.StartsWith('v') || !Version.TryParse(tag.AsSpan(1), out var version)
            || version.Build < 0 || version.Revision >= 0 || tag != $"v{version}")
        {
            throw new InvalidDataException("Expected a vMAJOR.MINOR.PATCH release tag.");
        }

        var downloadUrl = $"https://github.com/KaguyaHQ/nila-downloads/releases/download/{tag}/{InstallerName}";
        var hasInstaller = release.GetProperty("assets").EnumerateArray().Any(asset =>
            asset.GetProperty("name").GetString() == InstallerName
            && asset.GetProperty("state").GetString() == "uploaded"
            && asset.GetProperty("size").GetInt64() > 0
            && asset.GetProperty("browser_download_url").GetString() == downloadUrl);
        if (!hasInstaller)
        {
            throw new InvalidDataException("The release does not have a completed Windows installer.");
        }

        return new ConnectorRelease(version, new Uri(downloadUrl),
            new Uri($"https://github.com/KaguyaHQ/nila-downloads/releases/tag/{tag}"));
    }

    public void Dispose() => httpClient.Dispose();
}

public sealed record ConnectorRelease(Version Version, Uri DownloadUri, Uri NotesUri)
{
    public bool IsNewerThan(string installedVersion) =>
        System.Version.TryParse(installedVersion, out var installed)
            ? Version > installed
            : throw new InvalidDataException("The installed release version is invalid.");
}
