using NilaConnector.Core.Models;
using NilaConnector.Core.Tally;
using NilaConnector.Core.Tests.Support;

namespace NilaConnector.Core.Tests.Tally;

public sealed class TallyClientTests
{
    [Fact]
    public async Task CreatesOnePaymentVoucherInTheExplicitCompanyAndParsesMasterId()
    {
        const string response = """
            <RESPONSE>
              <CREATED>1</CREATED><ALTERED>0</ALTERED><ERRORS>0</ERRORS>
              <LASTVCHID> 42</LASTVCHID><LASTMID>0</LASTMID>
            </RESPONSE>
            """;
        await using var server = new FakeTallyServer(response);
        using var client = CreateClient(server.Endpoint);
        var command = new CreateVoucherCommand(
            "d45f7537-00d5-4463-9d70-f246257d88ef",
            "Nila",
            new DateOnly(2026, 8, 19),
            "Payment",
            "NILA Playground Customer",
            "Cash",
            1.00m,
            "Nila connector <test>");

        var result = await client.CreateVoucherAsync(command, CancellationToken.None);
        var request = await server.RequestBody;

        Assert.Equal(new TallyVoucherResult(1, 0, 42), result);
        Assert.Contains("<SVCURRENTCOMPANY>Nila</SVCURRENTCOMPANY>", request, StringComparison.Ordinal);
        Assert.Contains("REMOTEID=\"nila-job-d45f7537-00d5-4463-9d70-f246257d88ef\"", request, StringComparison.Ordinal);
        Assert.Contains("OBJVIEW=\"Accounting Voucher View\"", request, StringComparison.Ordinal);
        Assert.Contains("<NARRATION>Nila connector &lt;test&gt;</NARRATION>", request, StringComparison.Ordinal);
        Assert.Contains("<LEDGERNAME>NILA Playground Customer</LEDGERNAME>", request, StringComparison.Ordinal);
        Assert.Contains("<AMOUNT>-1.00</AMOUNT>", request, StringComparison.Ordinal);
        Assert.Contains("<LEDGERNAME>Cash</LEDGERNAME>", request, StringComparison.Ordinal);
        Assert.Contains("<AMOUNT>1.00</AMOUNT>", request, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RejectsAnAmbiguousVoucherImportResponse()
    {
        const string response = """
            <RESPONSE><CREATED>0</CREATED><ERRORS>1</ERRORS><LASTVCHID>0</LASTVCHID></RESPONSE>
            """;
        await using var server = new FakeTallyServer(response);
        using var client = CreateClient(server.Endpoint);

        var exception = await Assert.ThrowsAsync<TallyClientException>(() =>
            client.CreateVoucherAsync(
                new CreateVoucherCommand(
                    "d45f7537-00d5-4463-9d70-f246257d88ef",
                    "Nila",
                    new DateOnly(2026, 8, 19),
                    "Payment",
                    "NILA Playground Customer",
                    "Cash",
                    1.00m,
                    "Nila connector test"),
                CancellationToken.None));

        Assert.Equal(TallyErrorKind.ApplicationError, exception.Kind);
    }

    [Fact]
    public async Task DiscoversOpenCompaniesThroughARealHttpRequest()
    {
        var response = await File.ReadAllTextAsync(Fixture("open-companies.xml"));
        await using var server = new FakeTallyServer(response);
        using var client = CreateClient(server.Endpoint);

        var companies = await client.GetOpenCompaniesAsync(CancellationToken.None);
        var request = await server.RequestBody;

        Assert.Collection(
            companies,
            company => Assert.Equal(("ABC Pvt Ltd", "abc-guid"), (company.Name, company.TallyGuid)),
            company => Assert.Equal(("XYZ & Co", "xyz-guid"), (company.Name, company.TallyGuid)));
        Assert.Contains("<TYPE>Company</TYPE>", request, StringComparison.Ordinal);
        Assert.DoesNotContain("Company on Disk", request, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("<NATIVEMETHOD>GUID</NATIVEMETHOD>", request, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RejectsXmlWithADocumentType()
    {
        const string response = """
            <!DOCTYPE ENVELOPE [<!ENTITY unsafe "value">]>
            <ENVELOPE><HEADER><STATUS>1</STATUS></HEADER><BODY>&unsafe;</BODY></ENVELOPE>
            """;
        await using var server = new FakeTallyServer(response);
        using var client = CreateClient(server.Endpoint);

        var exception = await Assert.ThrowsAsync<TallyClientException>(() =>
            client.GetOpenCompaniesAsync(CancellationToken.None));

        Assert.Equal(TallyErrorKind.InvalidResponse, exception.Kind);
    }

    [Fact]
    public async Task RejectsTallyApplicationErrors()
    {
        const string response = """
            <ENVELOPE><HEADER><STATUS>0</STATUS></HEADER><BODY><DATA>
            <LINEERROR>Unknown collection</LINEERROR>
            </DATA></BODY></ENVELOPE>
            """;
        await using var server = new FakeTallyServer(response);
        using var client = CreateClient(server.Endpoint);

        var exception = await Assert.ThrowsAsync<TallyClientException>(() =>
            client.GetOpenCompaniesAsync(CancellationToken.None));

        Assert.Equal(TallyErrorKind.ApplicationError, exception.Kind);
        Assert.Contains("Unknown collection", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RejectsWellFormedXmlWithoutATallyStatus()
    {
        await using var server = new FakeTallyServer("<html><body>not Tally</body></html>");
        using var client = CreateClient(server.Endpoint);

        var exception = await Assert.ThrowsAsync<TallyClientException>(() =>
            client.GetOpenCompaniesAsync(CancellationToken.None));

        Assert.Equal(TallyErrorKind.InvalidResponse, exception.Kind);
    }

    [Fact]
    public async Task ClassifiesATimeoutWhileReadingTheResponseBody()
    {
        using var client = new TallyClient(
            new HttpClient(new DelayedBodyHandler(TimeSpan.FromSeconds(5)))
            {
                Timeout = Timeout.InfiniteTimeSpan,
            },
            new TallyClientOptions
            {
                RequestTimeout = TimeSpan.FromMilliseconds(50),
            });

        var exception = await Assert.ThrowsAsync<TallyClientException>(() =>
            client.GetOpenCompaniesAsync(CancellationToken.None));

        Assert.Equal(TallyErrorKind.Timeout, exception.Kind);
    }

    [Fact]
    public async Task RejectsResponsesOverTheConfiguredLimit()
    {
        var response = $"<ENVELOPE><HEADER><STATUS>1</STATUS></HEADER><BODY>{new string('x', 512)}</BODY></ENVELOPE>";
        await using var server = new FakeTallyServer(response);
        using var client = CreateClient(server.Endpoint, maxResponseBytes: 128);

        var exception = await Assert.ThrowsAsync<TallyClientException>(() =>
            client.GetOpenCompaniesAsync(CancellationToken.None));

        Assert.Equal(TallyErrorKind.ResponseTooLarge, exception.Kind);
    }

    [Fact]
    public async Task ClassifiesItsOwnRequestTimeout()
    {
        using var client = new TallyClient(
            new HttpClient(new DelayedHandler(TimeSpan.FromSeconds(5)))
            {
                Timeout = Timeout.InfiniteTimeSpan,
            },
            new TallyClientOptions
            {
                RequestTimeout = TimeSpan.FromMilliseconds(50),
            });

        var exception = await Assert.ThrowsAsync<TallyClientException>(() =>
            client.GetOpenCompaniesAsync(CancellationToken.None));

        Assert.Equal(TallyErrorKind.Timeout, exception.Kind);
    }

    [Fact]
    public async Task SerializesConcurrentTallyRequests()
    {
        var handler = new ConcurrencyTrackingHandler();
        using var client = new TallyClient(
            new HttpClient(handler)
            {
                Timeout = Timeout.InfiniteTimeSpan,
            },
            new TallyClientOptions());

        await Task.WhenAll(
            client.GetOpenCompaniesAsync(CancellationToken.None),
            client.GetOpenCompaniesAsync(CancellationToken.None));

        Assert.Equal(1, handler.MaximumConcurrency);
    }

    private static TallyClient CreateClient(Uri endpoint, int maxResponseBytes = 1024 * 1024) => new(
        new HttpClient
        {
            Timeout = Timeout.InfiniteTimeSpan,
        },
        new TallyClientOptions
        {
            Endpoint = endpoint,
            MaxResponseBytes = maxResponseBytes,
        });

    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

    private sealed class DelayedHandler(TimeSpan delay) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            await Task.Delay(delay, cancellationToken);
            return SuccessfulResponse();
        }
    }

    private sealed class DelayedBodyHandler(TimeSpan delay) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage
            {
                StatusCode = System.Net.HttpStatusCode.OK,
                Content = new StreamContent(new DelayedReadStream(delay)),
            });
    }

    private sealed class DelayedReadStream(TimeSpan delay) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            await Task.Delay(delay, cancellationToken);
            return 0;
        }

        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class ConcurrencyTrackingHandler : HttpMessageHandler
    {
        private int _concurrency;
        private int _maximumConcurrency;

        public int MaximumConcurrency => _maximumConcurrency;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var concurrency = Interlocked.Increment(ref _concurrency);
            InterlockedExtensions.Max(ref _maximumConcurrency, concurrency);

            try
            {
                await Task.Delay(30, cancellationToken);
                return SuccessfulResponse();
            }
            finally
            {
                _ = Interlocked.Decrement(ref _concurrency);
            }
        }
    }

    private static HttpResponseMessage SuccessfulResponse() => new()
    {
        StatusCode = System.Net.HttpStatusCode.OK,
        Content = new StringContent(
            "<ENVELOPE><HEADER><STATUS>1</STATUS></HEADER><BODY><DATA><COLLECTION /></DATA></BODY></ENVELOPE>"),
    };

    private static class InterlockedExtensions
    {
        public static void Max(ref int location, int value)
        {
            var current = Volatile.Read(ref location);

            while (value > current)
            {
                var observed = Interlocked.CompareExchange(ref location, value, current);
                if (observed == current)
                {
                    return;
                }

                current = observed;
            }
        }
    }
}
