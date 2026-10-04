using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using NilaConnector.Desktop.Services;
using Xunit;

namespace NilaConnector.Desktop.Tests.Services;

public sealed class ReleaseUpdateClientTests
{
    [Theory]
    [InlineData("0.6.9", true)]
    [InlineData("0.6.10", false)]
    [InlineData("0.7.0", false)]
    public async Task ChecksPublishedVersionAndUsesItsExactInstaller(string installed, bool newer)
    {
        using var client = CreateClient((request, _) =>
        {
            Assert.Equal("https://api.github.com/repos/KaguyaHQ/nila-downloads/releases/latest", request.RequestUri?.AbsoluteUri);
            Assert.Contains("NilaConnector", request.Headers.UserAgent.ToString(), StringComparison.Ordinal);
            return Task.FromResult(Response(ReleaseJson()));
        });

        var release = await client.GetLatestAsync(CancellationToken.None);

        Assert.Equal(newer, release.IsNewerThan(installed));
        Assert.Equal(new Uri("https://github.com/KaguyaHQ/nila-downloads/releases/download/v0.6.10/NilaConnector-win-x64-setup.exe"), release.DownloadUri);
        Assert.Equal(new Uri("https://github.com/KaguyaHQ/nila-downloads/releases/tag/v0.6.10"), release.NotesUri);
    }

    [Theory]
    [InlineData("v0.6.10-beta")]
    [InlineData("v0.6")]
    [InlineData("v0.6.10.1")]
    [InlineData("v00.6.10")]
    [InlineData("0.6.10")]
    public async Task RejectsUnsupportedVersionTags(string tag)
    {
        using var client = CreateClient((_, _) => Task.FromResult(Response(ReleaseJson(tag: tag))));

        await Assert.ThrowsAsync<InvalidDataException>(() => client.GetLatestAsync(CancellationToken.None));
    }

    [Theory]
    [InlineData(true, false, "uploaded", 10, null)]
    [InlineData(false, true, "uploaded", 10, null)]
    [InlineData(false, false, "open", 10, null)]
    [InlineData(false, false, "uploaded", 0, null)]
    [InlineData(false, false, "uploaded", 10, "https://example.com/installer.exe")]
    public async Task RejectsUnpublishedOrIncompleteInstallers(bool draft, bool prerelease, string state, long size, string? url)
    {
        using var client = CreateClient((_, _) => Task.FromResult(Response(
            ReleaseJson(draft: draft, prerelease: prerelease, state: state, size: size, url: url))));

        await Assert.ThrowsAsync<InvalidDataException>(() => client.GetLatestAsync(CancellationToken.None));
    }

    [Fact]
    public async Task MissingInstallerCannotBeOfferedAsAnUpdate()
    {
        using var client = CreateClient((_, _) => Task.FromResult(Response(
            "{\"tag_name\":\"v0.6.10\",\"draft\":false,\"prerelease\":false,\"assets\":[]}")));

        await Assert.ThrowsAsync<InvalidDataException>(() => client.GetLatestAsync(CancellationToken.None));
    }

    [Fact]
    public async Task FailedCheckCannotBeReportedAsUpToDate()
    {
        using var client = CreateClient((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden)));

        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetLatestAsync(CancellationToken.None));
    }

    [Fact]
    public async Task ClosingSettingsCanCancelTheRequest()
    {
        using var cancellation = new CancellationTokenSource();
        using var client = CreateClient(async (_, token) =>
        {
            await cancellation.CancelAsync();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return Response(ReleaseJson());
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.GetLatestAsync(cancellation.Token));
    }

    private static string ReleaseJson(string tag = "v0.6.10", bool draft = false, bool prerelease = false,
        string state = "uploaded", long size = 10, string? url = null) => JsonSerializer.Serialize(new
        {
            tag_name = tag,
            draft,
            prerelease,
            assets = new[]
            {
                new
                {
                    name = "NilaConnector-win-x64-setup.exe",
                    state,
                    size,
                    browser_download_url = url ?? $"https://github.com/KaguyaHQ/nila-downloads/releases/download/{tag}/NilaConnector-win-x64-setup.exe",
                },
            },
        });

    private static HttpResponseMessage Response(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
    };

    private static ReleaseUpdateClient CreateClient(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) =>
        new(new HttpClient(new StubHandler(send)));

    private sealed class StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            send(request, cancellationToken);
    }
}
