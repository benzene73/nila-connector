using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using NilaConnector.Core.Nila;
using NilaConnector.Desktop.Security;
using Xunit;

namespace NilaConnector.Desktop.Tests.Security;

public sealed class DpapiDeviceCredentialStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        $"nila-connector-tests-{Guid.NewGuid():N}");

    [Fact]
    public void CredentialSurvivesRestartWithoutPlaintextOnDisk()
    {
        var path = Path.Combine(_directory, "device.credential");
        var credential = new DeviceCredential(
            42,
            "super-secret-device-token",
            new Uri("wss://nila.tax/connector/websocket?vsn=2.0.0"));

        var firstStore = CreateStore(path);
        firstStore.Save(credential);

        var bytes = File.ReadAllBytes(path);
        Assert.DoesNotContain(credential.DeviceToken, Encoding.UTF8.GetString(bytes));

        var restartedStore = CreateStore(path);
        Assert.Equal(credential, restartedStore.Load());

        restartedStore.Clear();
        Assert.Null(restartedStore.Load());
        Assert.False(File.Exists(path));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, true);
        }
    }

    private static DpapiDeviceCredentialStore CreateStore(string path) =>
        new(NullLogger<DpapiDeviceCredentialStore>.Instance, path);
}
