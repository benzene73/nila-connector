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
    public async Task TreatsAmbiguousCreateVoucherFailuresAsIndeterminate(TallyErrorKind kind)
    {
        var runner = new JobRunner(new CreateFailureClient(kind), NullLogger<JobRunner>.Instance);
        using var document = CreateVoucherJob();

        var result = await runner.ExecuteAsync(document.RootElement, CancellationToken.None);

        Assert.Equal("indeterminate", result.Status);
        Assert.Equal(kind.ToString(), result.Details["kind"]);
    }

    [Fact]
    public async Task TreatsTallyApplicationRejectionsAsFailed()
    {
        var runner = new JobRunner(
            new CreateFailureClient(TallyErrorKind.ApplicationError),
            NullLogger<JobRunner>.Instance);
        using var document = CreateVoucherJob();

        var result = await runner.ExecuteAsync(document.RootElement, CancellationToken.None);

        Assert.Equal("failed", result.Status);
    }

    [Fact]
    public async Task DoesNotWriteWhenPreflightCannotReadTally()
    {
        var client = new ScriptedTallyClient(
            reads: [new TallyClientException(TallyErrorKind.Timeout, "Tally is busy.")]);
        var runner = new JobRunner(client, NullLogger<JobRunner>.Instance);
        using var document = ImportJob();

        var result = await runner.ExecuteAsync(document.RootElement, CancellationToken.None);

        Assert.Equal("failed", result.Status);
        Assert.Equal("PreflightUnavailable", result.Details["kind"]);
        Assert.Equal(0, client.ImportCalls);
    }

    [Fact]
    public async Task PreflightRecoversEquivalentVoucherUsingTallyAssignedIdentity()
    {
        var client = new ScriptedTallyClient(
            reads: [Candidates(ExistingVoucher())],
            actualFingerprint: ExpectedFingerprint());
        var runner = new JobRunner(client, NullLogger<JobRunner>.Instance);
        using var document = ImportJob();

        var result = await runner.ExecuteAsync(document.RootElement, CancellationToken.None);

        Assert.Equal("succeeded", result.Status);
        Assert.Equal(true, result.Details["verified_existing"]);
        Assert.Equal(42L, result.Details["master_id"]);
        Assert.Equal("tally-generated-guid", result.Details["tally_guid"]);
        Assert.Equal(0, client.ImportCalls);
        Assert.Equal(1, client.FingerprintCalls);
        Assert.Equal(new DateOnly(2026, 6, 25), client.LastFingerprintDate);
    }

    [Fact]
    public async Task PreflightRejectsSameKeyWithDifferentMaterialContents()
    {
        var different = ExpectedFingerprint() with { CanonicalHash = new string('0', 64) };
        var client = new ScriptedTallyClient(
            reads: [Candidates(ExistingVoucher())],
            actualFingerprint: different);
        var runner = new JobRunner(client, NullLogger<JobRunner>.Instance);
        using var document = ImportJob();

        var result = await runner.ExecuteAsync(document.RootElement, CancellationToken.None);

        Assert.Equal("failed", result.Status);
        Assert.Equal("VoucherConflict", result.Details["kind"]);
        Assert.Equal(0, client.ImportCalls);
    }

    [Fact]
    public async Task PreflightRejectsDuplicateCandidateKey()
    {
        var client = new ScriptedTallyClient(
            reads: [Candidates(ExistingVoucher(42), ExistingVoucher(43))]);
        var runner = new JobRunner(client, NullLogger<JobRunner>.Instance);
        using var document = ImportJob();

        var result = await runner.ExecuteAsync(document.RootElement, CancellationToken.None);

        Assert.Equal("failed", result.Status);
        Assert.Equal("DuplicateConflict", result.Details["kind"]);
        Assert.Equal(0, client.ImportCalls);
        Assert.Equal(0, client.FingerprintCalls);
    }

    [Fact]
    public async Task RecoversTimeoutOnlyAfterReadBackProvesEquivalence()
    {
        var client = new ScriptedTallyClient(
            reads: [Candidates(), Candidates(ExistingVoucher())],
            actualFingerprint: ExpectedFingerprint(),
            importException: new TallyClientException(TallyErrorKind.Timeout, "Response lost."));
        var runner = new JobRunner(client, NullLogger<JobRunner>.Instance);
        using var document = ImportJob();

        var result = await runner.ExecuteAsync(document.RootElement, CancellationToken.None);

        Assert.Equal("succeeded", result.Status);
        Assert.Equal(true, result.Details["recovered"]);
        Assert.Equal(1, client.ImportCalls);
        Assert.Equal(2, client.VoucherReadCalls);
    }

    [Fact]
    public async Task DoesNotBlindlyRetryWhenTimeoutReadBackIsStillAbsent()
    {
        var client = new ScriptedTallyClient(
            reads: [Candidates(), Candidates()],
            importException: new TallyClientException(TallyErrorKind.Timeout, "Response lost."));
        var runner = new JobRunner(client, NullLogger<JobRunner>.Instance);
        using var document = ImportJob();

        var result = await runner.ExecuteAsync(document.RootElement, CancellationToken.None);

        Assert.Equal("indeterminate", result.Status);
        Assert.Equal("OutcomeUnknown", result.Details["kind"]);
        Assert.Equal(1, client.ImportCalls);
    }

    [Fact]
    public async Task VerificationOnlyMakesProvenAbsenceSafelyRetryable()
    {
        var client = new ScriptedTallyClient(reads: [Candidates()]);
        var runner = new JobRunner(client, NullLogger<JobRunner>.Instance);
        using var document = ImportJob(verifyOnly: true);

        var result = await runner.ExecuteAsync(document.RootElement, CancellationToken.None);

        Assert.Equal("failed", result.Status);
        Assert.Equal("VerifiedAbsent", result.Details["kind"]);
        Assert.Equal(0, client.ImportCalls);
    }

    [Fact]
    public async Task DefiniteImportIsSucceededOnlyAfterIdentityReadBack()
    {
        var client = new ScriptedTallyClient(
            reads: [Candidates(), Candidates(ExistingVoucher())],
            actualFingerprint: ExpectedFingerprint());
        var runner = new JobRunner(client, NullLogger<JobRunner>.Instance);
        using var document = ImportJob();

        var result = await runner.ExecuteAsync(document.RootElement, CancellationToken.None);

        Assert.Equal("succeeded", result.Status);
        Assert.Equal(42L, result.Details["master_id"]);
        Assert.Equal("tally-generated-guid", result.Details["tally_guid"]);
        Assert.Equal(1, result.Details["created"]);
        Assert.Equal(1, client.ImportCalls);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(null, null)]
    [InlineData(true, true)]
    public async Task AcceptedVoucherWithUnresolvedGstIsNotReportedAsSuccess(bool? included, bool? uncertain)
    {
        var client = new ScriptedTallyClient(
            reads: [Candidates(), Candidates(ExistingVoucher())],
            actualFingerprint: ExpectedFingerprint() with { GstIncluded = included, GstUncertain = uncertain });
        var runner = new JobRunner(client, NullLogger<JobRunner>.Instance);
        using var document = ImportJob();
        var result = await runner.ExecuteAsync(document.RootElement, CancellationToken.None);
        Assert.Equal("indeterminate", result.Status);
        Assert.Equal("GstReportingUnresolved", result.Details["kind"]);
        Assert.Contains("do not post it again", result.Details["message"]?.ToString());
        Assert.Equal(1, client.ImportCalls);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(null)]
    public async Task GeneralGstInclusionDoesNotOverrideUnresolvedOptionalChecks(bool? optionalUncertain)
    {
        var client = new ScriptedTallyClient(
            reads: [Candidates(ExistingVoucher())],
            actualFingerprint: ExpectedFingerprint() with { GstOptionalUncertain = optionalUncertain });
        var runner = new JobRunner(client, NullLogger<JobRunner>.Instance);
        using var document = ImportJob();
        var result = await runner.ExecuteAsync(document.RootElement, CancellationToken.None);
        Assert.Equal("indeterminate", result.Status);
        Assert.Equal("GstReportingUnresolved", result.Details["kind"]);
        Assert.Equal(0, client.ImportCalls);
    }

    [Fact]
    public async Task GeneralGstInclusionDoesNotOverrideMissingStoredNature()
    {
        var client = new ScriptedTallyClient(
            reads: [Candidates(ExistingVoucher())],
            actualFingerprint: ExpectedFingerprint() with { HasStoredGstNature = false });
        var runner = new JobRunner(client, NullLogger<JobRunner>.Instance);
        using var document = ImportJob();
        var result = await runner.ExecuteAsync(document.RootElement, CancellationToken.None);
        Assert.Equal("indeterminate", result.Status);
        Assert.Equal("GstReportingUnresolved", result.Details["kind"]);
        Assert.Equal(0, client.ImportCalls);
    }

    [Fact]
    public async Task ExistingVoucherWithGstExceptionNeverGetsImportedAgain()
    {
        var client = new ScriptedTallyClient(
            reads: [Candidates(ExistingVoucher())],
            actualFingerprint: ExpectedFingerprint() with { GstIncluded = false, GstUncertain = true });
        var runner = new JobRunner(client, NullLogger<JobRunner>.Instance);
        using var document = ImportJob();
        var result = await runner.ExecuteAsync(document.RootElement, CancellationToken.None);
        Assert.Equal("indeterminate", result.Status);
        Assert.Equal(0, client.ImportCalls);
    }

    [Fact]
    public async Task DefiniteApplicationRejectionRemainsFailed()
    {
        var client = new ScriptedTallyClient(
            reads: [Candidates()],
            importException: new TallyClientException(
                TallyErrorKind.ApplicationError,
                "Ledger name not found."));
        var runner = new JobRunner(client, NullLogger<JobRunner>.Instance);
        using var document = ImportJob();

        var result = await runner.ExecuteAsync(document.RootElement, CancellationToken.None);

        Assert.Equal("failed", result.Status);
        Assert.Equal("ApplicationError", result.Details["kind"]);
    }

    [Fact]
    public async Task TallyNumberedPreflightRecoversByRemoteAltGuidWithoutSourceVoucherNumber()
    {
        var client = new ScriptedTallyClient(
            reads: [Candidates(ExistingBankVoucher())],
            actualFingerprint: ExpectedBankFingerprint());
        var runner = new JobRunner(client, NullLogger<JobRunner>.Instance);
        using var document = ImportJob(
            voucherNumberMode: "tally",
            xml: BankVoucherXml);

        var result = await runner.ExecuteAsync(document.RootElement, CancellationToken.None);

        Assert.Equal("succeeded", result.Status);
        Assert.Equal(true, result.Details["verified_existing"]);
        Assert.Equal(
            "d852c969-e235-8279-8a28-6604139d9010",
            result.Details["remote_alt_guid"]);
        Assert.Equal("87", result.Details["voucher_number"]);
        Assert.Equal(0, client.ImportCalls);
        Assert.Equal(1, client.FingerprintCalls);
    }

    [Fact]
    public async Task TallyNumberedImportRequiresRemoteAltGuid()
    {
        var runner = new JobRunner(
            new ScriptedTallyClient(reads: []),
            NullLogger<JobRunner>.Instance);
        using var document = ImportJob(
            voucherNumberMode: "tally",
            xml: BankVoucherXml.Replace(
                "<REMOTEALTGUID>d852c969-e235-8279-8a28-6604139d9010</REMOTEALTGUID>",
                "",
                StringComparison.Ordinal));

        var result = await runner.ExecuteAsync(document.RootElement, CancellationToken.None);

        Assert.Equal("failed", result.Status);
        Assert.Equal("InvalidJob", result.Details["kind"]);
    }

    private static JsonDocument CreateVoucherJob() => JsonDocument.Parse("""
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
        """);

    private static JsonDocument ImportJob(
        bool verifyOnly = false,
        string voucherNumberMode = "source",
        string? xml = null)
    {
        var payload = new Dictionary<string, object?>
        {
            ["company"] = "Caltech",
            ["expected_count"] = 1,
            ["xml"] = xml ?? VoucherXml,
            ["voucher_number_mode"] = voucherNumberMode,
        };
        if (verifyOnly)
        {
            payload["mode"] = "verify_only";
        }

        return JsonDocument.Parse(JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["job_id"] = 14,
            ["job_key"] = "d45f7537-00d5-4463-9d70-f246257d88ef",
            ["kind"] = "import_vouchers",
            ["payload"] = payload,
        }));
    }

    private static VoucherSemanticFingerprint ExpectedFingerprint() =>
        TallyRequestValidator.ReadSingleVoucherFingerprint(
            new ImportVouchersCommand("Caltech", VoucherXml, 1)).Semantic with { GstIncluded = true, GstUncertain = false, GstOptionalUncertain = false, HasStoredGstNature = true };

    private static VoucherSemanticFingerprint ExpectedBankFingerprint() =>
        TallyRequestValidator.ReadSingleVoucherFingerprint(
            new ImportVouchersCommand("Caltech", BankVoucherXml, 1),
            VoucherNumberMode.Tally).Semantic;

    private static TallySyncedVoucher[] Candidates(
        params TallySyncedVoucher[] vouchers) => vouchers;

    private static TallySyncedVoucher ExistingVoucher(long masterId = 42) => new(
        $"guid:tally-generated-{masterId}",
        masterId,
        "tally-generated-guid",
        null,
        new DateOnly(2026, 6, 25),
        "Sales",
        "38/2026-27",
        "Imported from Nila",
        new Dictionary<string, object?>());

    private static TallySyncedVoucher ExistingBankVoucher(long masterId = 87) => new(
        $"guid:tally-generated-{masterId}",
        masterId,
        "tally-generated-bank-guid",
        "d852c969-e235-8279-8a28-6604139d9010",
        new DateOnly(2026, 6, 1),
        "Payment",
        "87",
        "Bank fee",
        new Dictionary<string, object?>());

    private const string VoucherXml = """
        <ENVELOPE><HEADER><TALLYREQUEST>Import</TALLYREQUEST><TYPE>Data</TYPE><ID>Vouchers</ID></HEADER><BODY><DESC><STATICVARIABLES><SVCURRENTCOMPANY>Caltech</SVCURRENTCOMPANY></STATICVARIABLES></DESC><DATA><TALLYMESSAGE><VOUCHER><DATE>20260625</DATE><GUID>75096e6e-23d6-43f0-a98a-54879c893e3c</GUID><VOUCHERTYPENAME>Sales</VOUCHERTYPENAME><VOUCHERNUMBER>38/2026-27</VOUCHERNUMBER><PARTYLEDGERNAME>Siemens Healthcare Private Limited</PARTYLEDGERNAME><PARTYGSTIN>29AAACI0995H1ZQ</PARTYGSTIN><PERSISTEDVIEW>Invoice Voucher View</PERSISTEDVIEW><ISINVOICE>Yes</ISINVOICE><NARRATION>Imported from Nila</NARRATION><LEDGERENTRIES.LIST><LEDGERNAME>Siemens Healthcare Private Limited</LEDGERNAME><ISDEEMEDPOSITIVE>Yes</ISDEEMEDPOSITIVE><ISPARTYLEDGER>Yes</ISPARTYLEDGER><AMOUNT>-118.00</AMOUNT><BILLALLOCATIONS.LIST><NAME>38/2026-27</NAME><BILLTYPE>New Ref</BILLTYPE><AMOUNT>-118.00</AMOUNT></BILLALLOCATIONS.LIST></LEDGERENTRIES.LIST><LEDGERENTRIES.LIST><LEDGERNAME>Sales</LEDGERNAME><ISDEEMEDPOSITIVE>No</ISDEEMEDPOSITIVE><ISPARTYLEDGER>No</ISPARTYLEDGER><AMOUNT>100.00</AMOUNT><GSTHSNNAME>9022</GSTHSNNAME><GSTOVRDNASSESSABLEVALUE>100.00</GSTOVRDNASSESSABLEVALUE></LEDGERENTRIES.LIST><LEDGERENTRIES.LIST><LEDGERNAME>Output IGST</LEDGERNAME><ISDEEMEDPOSITIVE>No</ISDEEMEDPOSITIVE><ISPARTYLEDGER>No</ISPARTYLEDGER><AMOUNT>18.00</AMOUNT></LEDGERENTRIES.LIST></VOUCHER></TALLYMESSAGE></DATA></BODY></ENVELOPE>
        """;

    private const string BankVoucherXml = """
        <ENVELOPE><HEADER><TALLYREQUEST>Import</TALLYREQUEST><TYPE>Data</TYPE><ID>Vouchers</ID></HEADER><BODY><DESC><STATICVARIABLES><SVCURRENTCOMPANY>Caltech</SVCURRENTCOMPANY></STATICVARIABLES></DESC><DATA><TALLYMESSAGE><VOUCHER VCHTYPE="Payment"><REMOTEALTGUID>d852c969-e235-8279-8a28-6604139d9010</REMOTEALTGUID><DATE>20260601</DATE><VOUCHERTYPENAME>Payment</VOUCHERTYPENAME><PARTYLEDGERNAME>HDFC Bank</PARTYLEDGERNAME><PERSISTEDVIEW>Accounting Voucher View</PERSISTEDVIEW><ISINVOICE>No</ISINVOICE><NARRATION>Bank fee</NARRATION><LEDGERENTRIES.LIST><LEDGERNAME>Bank Charges</LEDGERNAME><ISDEEMEDPOSITIVE>Yes</ISDEEMEDPOSITIVE><ISPARTYLEDGER>No</ISPARTYLEDGER><AMOUNT>-413.00</AMOUNT></LEDGERENTRIES.LIST><LEDGERENTRIES.LIST><LEDGERNAME>HDFC Bank</LEDGERNAME><ISDEEMEDPOSITIVE>No</ISDEEMEDPOSITIVE><ISPARTYLEDGER>Yes</ISPARTYLEDGER><AMOUNT>413.00</AMOUNT></LEDGERENTRIES.LIST></VOUCHER></TALLYMESSAGE></DATA></BODY></ENVELOPE>
        """;

    private sealed class CreateFailureClient(TallyErrorKind kind) : ITallyClient
    {
        public Task<IReadOnlyList<TallyCompany>> GetOpenCompaniesAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<TallyCompany>>([]);

        public Task<IReadOnlyList<TallyMaster>> GetMastersAsync(
            string company,
            CancellationToken cancellationToken) => throw new NotImplementedException();

        public Task<IReadOnlyList<TallySyncedVoucher>> GetVouchersAsync(
            string company,
            DateOnly fromDate,
            DateOnly toDate,
            CancellationToken cancellationToken) => throw new NotImplementedException();

        public Task<VoucherSemanticFingerprint?> GetVoucherFingerprintAsync(
            string company,
            DateOnly voucherDate,
            long masterId,
            CancellationToken cancellationToken) => throw new NotImplementedException();

        public Task<TallyVoucherResult> CreateVoucherAsync(
            CreateVoucherCommand command,
            CancellationToken cancellationToken) =>
            Task.FromException<TallyVoucherResult>(
                new TallyClientException(kind, "Safe diagnostic message."));

        public Task<TallyMasterWriteResult> CreateLedgerAsync(
            CreateLedgerCommand command,
            CancellationToken cancellationToken) => throw new NotImplementedException();

        public Task<TallyVoucherBatchResult> ImportVouchersAsync(
            ImportVouchersCommand command,
            CancellationToken cancellationToken) => throw new NotImplementedException();
    }

    private sealed class ScriptedTallyClient(
        IEnumerable<object> reads,
        VoucherSemanticFingerprint? actualFingerprint = null,
        TallyClientException? importException = null) : ITallyClient
    {
        private readonly Queue<object> _reads = new(reads);

        public int VoucherReadCalls { get; private set; }
        public int FingerprintCalls { get; private set; }
        public int ImportCalls { get; private set; }
        public DateOnly? LastFingerprintDate { get; private set; }

        public Task<IReadOnlyList<TallyCompany>> GetOpenCompaniesAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<TallyCompany>>([]);

        public Task<IReadOnlyList<TallyMaster>> GetMastersAsync(
            string company,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<TallyMaster>>([]);

        public Task<IReadOnlyList<TallySyncedVoucher>> GetVouchersAsync(
            string company,
            DateOnly fromDate,
            DateOnly toDate,
            CancellationToken cancellationToken)
        {
            VoucherReadCalls++;
            var next = _reads.Dequeue();
            return next is Exception exception
                ? Task.FromException<IReadOnlyList<TallySyncedVoucher>>(exception)
                : Task.FromResult((IReadOnlyList<TallySyncedVoucher>)next);
        }

        public Task<VoucherSemanticFingerprint?> GetVoucherFingerprintAsync(
            string company,
            DateOnly voucherDate,
            long masterId,
            CancellationToken cancellationToken)
        {
            FingerprintCalls++;
            LastFingerprintDate = voucherDate;
            return Task.FromResult(actualFingerprint);
        }

        public Task<TallyVoucherResult> CreateVoucherAsync(
            CreateVoucherCommand command,
            CancellationToken cancellationToken) => throw new NotImplementedException();

        public Task<TallyMasterWriteResult> CreateLedgerAsync(
            CreateLedgerCommand command,
            CancellationToken cancellationToken) =>
            Task.FromResult(new TallyMasterWriteResult("Ledger", command.Name, 42, "guid", "remote-id"));

        public Task<TallyVoucherBatchResult> ImportVouchersAsync(
            ImportVouchersCommand command,
            CancellationToken cancellationToken)
        {
            ImportCalls++;
            return importException is not null
                ? Task.FromException<TallyVoucherBatchResult>(importException)
                : Task.FromResult(new TallyVoucherBatchResult(1, 0, 0, 0, 0, 0, 42));
        }
    }
}
