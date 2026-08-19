using System.Net;
using System.Text;
using NilaConnector.Core.Nila;

namespace NilaConnector.Core.Tests.Nila;

public sealed class MachineLinkerTests
{
    [Fact]
    public void RejectsPlainHttpForRemotePairingServers()
    {
        var options = new NilaPairingOptions
        {
            BaseUri = new Uri("http://nila.test"),
        };

        var exception = Assert.Throws<ArgumentException>(() =>
            new NilaPairingClient(new HttpClient(new PairingHandler()), options));

        Assert.Contains("HTTPS", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OpensApprovalPageAndPersistsTheMatchedCredential()
    {
        var handler = new PairingHandler();
        var options = new NilaPairingOptions
        {
            BaseUri = new Uri("https://nila.test"),
        };
        var store = new MemoryCredentialStore();
        var browser = new RecordingBrowser();
        var linker = new MachineLinker(
            new NilaPairingClient(new HttpClient(handler), options),
            store,
            browser);

        var credential = await linker.LinkAsync("Accounts PC", CancellationToken.None);

        Assert.Equal(new Uri("https://nila.test/settings/connectors/tally/pair/approval"), browser.Opened);
        Assert.Equal(42, credential.DeviceId);
        Assert.Equal("private-device-code", credential.DeviceToken);
        Assert.Equal(new Uri("wss://nila.test/connector/websocket?vsn=2.0.0"), credential.WebSocketEndpoint);
        Assert.Equal(credential, store.Load());
        Assert.Equal(3, handler.ExchangeCount);
    }

    [Fact]
    public async Task UnlinkRevokesCloudCredentialBeforeClearingLocalStore()
    {
        var handler = new PairingHandler();
        var store = new MemoryCredentialStore();
        store.Save(new DeviceCredential(
            42,
            "private-device-code",
            new Uri("wss://nila.test/connector/websocket?vsn=2.0.0")));
        var linker = new MachineLinker(
            new NilaPairingClient(
                new HttpClient(handler),
                new NilaPairingOptions { BaseUri = new Uri("https://nila.test") }),
            store,
            new RecordingBrowser());

        await linker.UnlinkAsync(CancellationToken.None);

        Assert.Equal(1, handler.RevokeCount);
        Assert.Null(store.Load());
        Assert.False(linker.IsLinked);
    }

    private sealed class PairingHandler : HttpMessageHandler
    {
        public int ExchangeCount { get; private set; }
        public int RevokeCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Delete)
            {
                RevokeCount++;
                Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
                Assert.Equal("private-device-code", request.Headers.Authorization?.Parameter);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
            }

            if (request.RequestUri!.AbsolutePath.EndsWith("/pairings", StringComparison.Ordinal))
            {
                return Json(HttpStatusCode.Created, """
                    {
                      "device_code":"private-device-code",
                      "verification_url":"https://nila.test/settings/connectors/tally/pair/approval",
                      "expires_in":30,
                      "retry_after":1
                    }
                    """);
            }

            ExchangeCount++;
            if (ExchangeCount == 1)
            {
                return Json(HttpStatusCode.Accepted, """{"status":"pending","retry_after":1}""");
            }

            if (ExchangeCount == 2)
            {
                throw new HttpRequestException("Simulated lost exchange response.");
            }

            return Json(HttpStatusCode.OK, """
                    {
                      "status":"linked",
                      "device_id":42,
                      "device_token":"private-device-code",
                      "websocket_url":"wss://nila.test/connector/websocket?vsn=2.0.0"
                    }
                    """);
        }

        private static Task<HttpResponseMessage> Json(HttpStatusCode status, string json) =>
            Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            });
    }

    private sealed class RecordingBrowser : IExternalBrowser
    {
        public Uri? Opened { get; private set; }
        public void Open(Uri uri) => Opened = uri;
    }

    private sealed class MemoryCredentialStore : IDeviceCredentialStore
    {
        private DeviceCredential? _credential;
        public event EventHandler? Changed;
        public DeviceCredential? Load() => _credential;

        public void Save(DeviceCredential credential)
        {
            _credential = credential;
            Changed?.Invoke(this, EventArgs.Empty);
        }

        public void Clear()
        {
            _credential = null;
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }
}
