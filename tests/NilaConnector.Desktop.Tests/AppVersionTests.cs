using Xunit;

namespace NilaConnector.Desktop.Tests;

public sealed class AppVersionTests
{
    private const string Commit = "0123456789abcdef0123456789abcdef01234567";

    [Fact]
    public void ReleaseShowsTagVersionAndKeepsFullCommitInDetails()
    {
        var build = new AppBuildInfo($"0.5.2+{Commit}", true, false);

        Assert.Equal("Version 0.5.2", build.DisplayName);
        Assert.Equal(Commit, build.Commit);
        Assert.Equal($"Version 0.5.2\nCommit: {Commit}", build.Details);
    }

    [Theory]
    [InlineData(false, "Development build · 0123456")]
    [InlineData(true, "Development build · 0123456 · modified")]
    public void LocalBuildCannotLookLikeARelease(bool modified, string expected)
    {
        var build = new AppBuildInfo($"0.0.0-dev+{Commit}", false, modified);

        Assert.Equal(expected, build.DisplayName);
        Assert.Equal(modified, build.Details.Contains("not been committed", StringComparison.Ordinal));
    }

    [Fact]
    public void MissingSourceInformationIsExplicit()
    {
        var build = new AppBuildInfo("unknown", false, false);

        Assert.Equal("Development build · unknown", build.DisplayName);
        Assert.Equal("unknown", build.Commit);
    }

    [Fact]
    public void RunningAssemblyHasBuildMetadata()
    {
        Assert.NotEqual("unknown", AppVersion.Current);
        Assert.Contains(typeof(App).Assembly.GetCustomAttributesData(), attribute =>
            attribute.AttributeType == typeof(System.Reflection.AssemblyMetadataAttribute)
            && Equals(attribute.ConstructorArguments[0].Value, "ConnectorRelease"));
    }
}
