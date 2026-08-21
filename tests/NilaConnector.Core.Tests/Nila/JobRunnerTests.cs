using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using NilaConnector.Core.Models;
using NilaConnector.Core.Nila;
using NilaConnector.Core.Tally;

namespace NilaConnector.Core.Tests.Nila;

public sealed class JobRunnerTests
{
    [Theory]
    [InlineData(TallyErrorKind.Timeout)]
    [InlineData(TallyErrorKind.Unreachable)]
    public async Task TreatsAmbiguousTallyFailuresAsIndeterminate(TallyErrorKind kind)
    {
        var runner = new JobRunner(new ThrowingTallyClient(kind), NullLogger<JobRunner>.Instance);
        using var document = JsonDocument.Parse(JobJson);

        var result = await runner.ExecuteAsync(document.RootElement, CancellationToken.None);

        Assert.Equal("indeterminate", result.Status);
        Assert.Equal(kind.ToString(), result.Details["kind"]);
    }

    [Fact]
    public async Task TreatsTallyApplicationRejectionsAsFailed()
    {
        var runner = new JobRunner(
            new ThrowingTallyClient(TallyErrorKind.ApplicationError),
            NullLogger<JobRunner>.Instance);
        using var document = JsonDocument.Parse(JobJson);

        var result = await runner.ExecuteAsync(document.RootElement, CancellationToken.None);

        Assert.Equal("failed", result.Status);
    }

    [Fact]
    public async Task TreatsVoucherImportApplicationFailureAsFailed()
    {
        var runner = new JobRunner(
            new ThrowingTallyClient(TallyErrorKind.ApplicationError),
            NullLogger<JobRunner>.Instance);
        using var document = JsonDocument.Parse(BatchJobJson);

        var result = await runner.ExecuteAsync(document.RootElement, CancellationToken.None);

        Assert.Equal("failed", result.Status);
        Assert.Equal("ApplicationError", result.Details["kind"]);
    }

    [Fact]
    public async Task RecoversAnAmbiguousSingleVoucherImportByMatchingItsFingerprint()
    {
        var client = new RecoveringTallyClient();
        var runner = new JobRunner(client, NullLogger<JobRunner>.Instance);
        using var document = JsonDocument.Parse(RecoverableBatchJobJson);

        var result = await runner.ExecuteAsync(document.RootElement, CancellationToken.None);

        Assert.Equal("succeeded", result.Status);
        Assert.Equal(true, result.Details["recovered"]);
        Assert.Equal(42L, result.Details["master_id"]);
        Assert.Equal(1, client.ReadBackCalls);
    }

    [Fact]
    public async Task LeavesAnAmbiguousImportUnknownWhenTheReadBackFingerprintDoesNotMatch()
    {
        var client = new RecoveringTallyClient(voucherNumber: "DIFFERENT");
        var runner = new JobRunner(client, NullLogger<JobRunner>.Instance);
        using var document = JsonDocument.Parse(RecoverableBatchJobJson);

        var result = await runner.ExecuteAsync(document.RootElement, CancellationToken.None);

        Assert.Equal("indeterminate", result.Status);
        Assert.Equal(1, client.ReadBackCalls);
    }

    private const string JobJson = """
        {
          "job_id": 12,
          "job_key": "d45f7537-00d5-4463-9d70-f246257d88ef",
          "kind": "create_voucher",
          "payload": {
            "company": "Nila",
            "date": "2026-08-01",
            "voucher_type": "Payment",
            "debit_ledger": "NILA Playground Customer",
            "credit_ledger": "Cash",
            "amount": "1.00",
            "narration": "Nila connector test"
          }
        }
        """;

    private const string BatchJobJson = """
        {
          "job_id": 13,
          "job_key": "d45f7537-00d5-4463-9d70-f246257d88ef",
          "kind": "import_vouchers",
          "payload": {
            "company": "Nila",
            "expected_count": 1,
            "xml": "<ENVELOPE />"
          }
        }
        """;

    private const string RecoverableBatchJobJson = """
        {
          "job_id": 14,
          "job_key": "d45f7537-00d5-4463-9d70-f246257d88ef",
          "kind": "import_vouchers",
          "payload": {
            "company": "Caltech",
            "expected_count": 1,
            "xml": "<ENVELOPE><HEADER><TALLYREQUEST>Import</TALLYREQUEST><TYPE>Data</TYPE><ID>Vouchers</ID></HEADER><BODY><DESC><STATICVARIABLES><SVCURRENTCOMPANY>Caltech</SVCURRENTCOMPANY></STATICVARIABLES></DESC><DATA><TALLYMESSAGE><VOUCHER><DATE>20260625</DATE><GUID>nila-test-guid</GUID><VOUCHERTYPENAME>Sales</VOUCHERTYPENAME><VOUCHERNUMBER>38/2026-27</VOUCHERNUMBER></VOUCHER></TALLYMESSAGE></DATA></BODY></ENVELOPE>"
          }
        }
        """;

    private sealed class ThrowingTallyClient(TallyErrorKind kind) : ITallyClient
    {
        public Task<IReadOnlyList<TallyCompany>> GetOpenCompaniesAsync(
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<TallyCompany>>([]);

        public Task<TallyVoucherResult> CreateVoucherAsync(
            CreateVoucherCommand command,
            CancellationToken cancellationToken) =>
            Task.FromException<TallyVoucherResult>(
                new TallyClientException(kind, "Safe diagnostic message."));

        public Task<TallyVoucherBatchResult> ImportVouchersAsync(
            ImportVouchersCommand command,
            CancellationToken cancellationToken) =>
            Task.FromException<TallyVoucherBatchResult>(
                new TallyClientException(kind, "Safe diagnostic message."));

        public Task<IReadOnlyList<TallyMaster>> GetMastersAsync(string company, CancellationToken cancellationToken)
        {
            throw new NotImplementedException();
        }

        public Task<IReadOnlyList<TallySyncedVoucher>> GetVouchersAsync(string company, DateOnly from, DateOnly to, CancellationToken cancellationToken)
        {
            throw new NotImplementedException();
        }
    }

    private sealed class RecoveringTallyClient(string voucherNumber = "38/2026-27") : ITallyClient
    {
        public int ReadBackCalls { get; private set; }

        public Task<IReadOnlyList<TallyCompany>> GetOpenCompaniesAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<TallyCompany>>([]);

        public Task<IReadOnlyList<TallyMaster>> GetMastersAsync(
            string company,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<TallyMaster>>([]);

        public Task<IReadOnlyList<TallySyncedVoucher>> GetVouchersAsync(
            string company,
            DateOnly from,
            DateOnly to,
            CancellationToken cancellationToken)
        {
            ReadBackCalls++;
            return Task.FromResult<IReadOnlyList<TallySyncedVoucher>>([
                new(
                    "guid:nila-test-guid",
                    42,
                    "nila-test-guid",
                    "nila-test-guid",
                    new DateOnly(2026, 6, 25),
                    "Sales",
                    voucherNumber,
                    "Imported from Nila",
                    new Dictionary<string, object?>())
            ]);
        }

        public Task<TallyVoucherResult> CreateVoucherAsync(
            CreateVoucherCommand command,
            CancellationToken cancellationToken) =>
            throw new NotImplementedException();

        public Task<TallyVoucherBatchResult> ImportVouchersAsync(
            ImportVouchersCommand command,
            CancellationToken cancellationToken) =>
            Task.FromException<TallyVoucherBatchResult>(
                new TallyClientException(TallyErrorKind.Timeout, "The response was lost."));
    }
}
