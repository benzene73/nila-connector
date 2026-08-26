using NilaConnector.Desktop.Services;
using Xunit;

namespace NilaConnector.Desktop.Tests.Services;

public sealed class ConnectorDataPathsTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("production")]
    public void CreateUsesExistingProductionDirectory(string? profile)
    {
        var paths = ConnectorDataPaths.Create(profile, @"C:\LocalAppData");

        Assert.Equal(@"C:\LocalAppData\Nila\Connector", paths.RootDirectory);
        Assert.Equal(@"C:\LocalAppData\Nila\Connector\device.credential", paths.CredentialPath);
        Assert.Equal(@"C:\LocalAppData\Nila\Connector\settings.json", paths.TallySettingsPath);
        Assert.Equal(@"C:\LocalAppData\Nila\Connector\logs", paths.LogDirectory);
    }

    [Fact]
    public void CreateIsolatesLocalProfile()
    {
        var paths = ConnectorDataPaths.Create(" LOCAL ", @"C:\LocalAppData");

        Assert.Equal(@"C:\LocalAppData\Nila\Connector.Local", paths.RootDirectory);
        Assert.Equal(@"C:\LocalAppData\Nila\Connector.Local\device.credential", paths.CredentialPath);
        Assert.Equal(@"C:\LocalAppData\Nila\Connector.Local\settings.json", paths.TallySettingsPath);
        Assert.Equal(@"C:\LocalAppData\Nila\Connector.Local\logs", paths.LogDirectory);
    }

    [Fact]
    public void CreateRejectsUnknownProfile()
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            ConnectorDataPaths.Create("staging", @"C:\LocalAppData"));

        Assert.Contains("NILA_CONNECTOR_PROFILE", exception.Message, StringComparison.Ordinal);
    }
}
