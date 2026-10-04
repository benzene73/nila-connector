using System.Reflection;

namespace NilaConnector.Desktop;

internal static class AppVersion
{
    private static readonly Assembly Assembly = typeof(App).Assembly;

    public static AppBuildInfo Build { get; } = new(
        Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown",
        ReadFlag("ConnectorRelease"),
        ReadFlag("ConnectorHasLocalChanges"));

    public static string Current => Build.InformationalVersion;

    private static bool ReadFlag(string key) => Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
        .Any(attribute => attribute.Key == key && attribute.Value == "true");
}

internal sealed record AppBuildInfo(string InformationalVersion, bool IsRelease, bool HasLocalChanges)
{
    public string Version => InformationalVersion.Split('+', 2)[0];

    public string Commit => InformationalVersion.Split('+', 2) is [_, { Length: > 0 } commit]
        ? commit
        : "unknown";

    public string DisplayName => IsRelease
        ? $"Version {Version}"
        : $"Development build · {Commit[..Math.Min(7, Commit.Length)]}{(HasLocalChanges ? " · modified" : "")}";

    public string Details => $"{DisplayName}\nCommit: {Commit}"
        + (HasLocalChanges ? "\nIncludes local changes that have not been committed." : "");
}
