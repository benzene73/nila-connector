using Microsoft.Extensions.Logging.Abstractions;
using NilaConnector.Core.Tally;
using NilaConnector.Desktop.Services;
using Xunit;

namespace NilaConnector.Desktop.Tests.Services;

public sealed class TallyConnectionSettingsStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        $"nila-connector-settings-tests-{Guid.NewGuid():N}");

    [Fact]
    public async Task SavedEndpointAppliesImmediatelyAndSurvivesRestart()
    {
        var path = Path.Combine(_directory, "settings.json");
        var options = new TallyClientOptions();
        var store = CreateStore(options, path);

        await store.SaveAsync("localhost", "9001", TestContext.Current.CancellationToken);

        Assert.Equal(new Uri("http://localhost:9001/"), options.Endpoint);

        var restartedOptions = new TallyClientOptions();
        _ = CreateStore(restartedOptions, path);
        Assert.Equal(new Uri("http://localhost:9001/"), restartedOptions.Endpoint);
    }

    [Theory]
    [InlineData("", "9000")]
    [InlineData("localhost/path", "9000")]
    [InlineData("localhost", "0")]
    [InlineData("localhost", "65536")]
    [InlineData("localhost", "not-a-port")]
    public void InvalidEndpointIsRejected(string host, string port)
    {
        Assert.Throws<ArgumentException>(() =>
            TallyConnectionSettingsStore.BuildEndpoint(host, port));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private static TallyConnectionSettingsStore CreateStore(TallyClientOptions options, string path) =>
        new(options, NullLogger<TallyConnectionSettingsStore>.Instance, path);
}
