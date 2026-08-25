using NilaConnector.Core.Models;
using NilaConnector.Core.Tally.Diagnostics;
using NilaConnector.Diagnostics;

namespace NilaConnector.Core.Tests.Tally;

public sealed class DiagnosticsCliTests
{
    [Fact]
    public async Task VoucherListRequiresAnExplicitPeriod()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = await DiagnosticsCli.RunAsync(
            ["vouchers", "--company", "Caltech"],
            _ => new FakeDiagnostics(),
            output,
            error,
            CancellationToken.None);

        Assert.Equal(2, exitCode);
        Assert.Contains("--from", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task VoucherLookupPrintsVerifiedContext()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = await DiagnosticsCli.RunAsync(
            [
                "voucher",
                "--company", "Caltech",
                "--date", "2025-12-04",
                "--master-id", "118",
            ],
            _ => new FakeDiagnostics { Lookup = Found() },
            output,
            error,
            CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Contains("Company asked: Caltech", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("Company GUID: company-guid", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("Period: 2025-12-04 -> 2025-12-04", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("Matches: 1", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task WriteLikeCommandsAreUnavailable()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = await DiagnosticsCli.RunAsync(
            ["delete-voucher"],
            _ => new FakeDiagnostics(),
            output,
            error,
            CancellationToken.None);

        Assert.Equal(2, exitCode);
        Assert.Contains("Unknown command", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExportVoucherUsesTheFullStructuredInspection()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var inspection = new VoucherInspection(
            118,
            "voucher-guid",
            null,
            new DateOnly(2025, 12, 4),
            "Sales",
            "118/2025-26",
            "Siemens",
            "Siemens",
            null,
            null,
            null,
            "Invoice Voucher View",
            true,
            null,
            [new VoucherLedgerInspection(
                "Sales", 100m, false, false, "Taxable", "Goods", "29141100",
                "Formic Acid", 100m, [new VoucherGstRateInspection("Cess", null, 0m)])],
            []);
        var context = new TallyQueryContext(
            new Uri("http://127.0.0.1:9000/"),
            "Caltech",
            "Caltech",
            "company-guid",
            new DateRange(inspection.Date, inspection.Date),
            "Voucher inspection MasterID 118");

        var exitCode = await DiagnosticsCli.RunAsync(
            [
                "export-voucher",
                "--company", "Caltech",
                "--date", "2025-12-04",
                "--master-id", "118",
            ],
            _ => new FakeDiagnostics
            {
                InspectionLookup = new VoucherInspectionLookupResult.Found(context, inspection),
            },
            output,
            error,
            CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Contains("\"DutyHead\": \"Cess\"", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("\"ValuationType\": null", output.ToString(), StringComparison.Ordinal);
    }

    private static VoucherLookupResult.Found Found()
    {
        var period = new DateRange(new DateOnly(2025, 12, 4), new DateOnly(2025, 12, 4));
        var context = new TallyQueryContext(
            new Uri("http://127.0.0.1:9000/"),
            "Caltech",
            "Caltech",
            "company-guid",
            period,
            "Voucher MasterID 118");
        var voucher = new TallySyncedVoucher(
            "voucher-118",
            118,
            "voucher-guid",
            null,
            period.From,
            "Sales",
            "118/2025-26",
            null,
            new Dictionary<string, object?>());
        return new VoucherLookupResult.Found(context, voucher);
    }

    private sealed class FakeDiagnostics : ITallyReadDiagnostics
    {
        public VoucherLookupResult Lookup { get; init; } =
            new VoucherLookupResult.NotFound(new TallyQueryContext(
                new Uri("http://127.0.0.1:9000/"),
                "Caltech",
                "Caltech",
                "company-guid",
                new DateRange(new DateOnly(2025, 12, 4), new DateOnly(2025, 12, 4)),
                "Voucher MasterID 118"));

        public VoucherInspectionLookupResult InspectionLookup { get; init; } =
            new VoucherInspectionLookupResult.NotFound(new TallyQueryContext(
                new Uri("http://127.0.0.1:9000/"),
                "Caltech",
                "Caltech",
                "company-guid",
                new DateRange(new DateOnly(2025, 12, 4), new DateOnly(2025, 12, 4)),
                "Voucher inspection MasterID 118"));

        public Task<IReadOnlyList<TallyCompany>> GetCompaniesAsync(
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<TallyCompany>>([new("Caltech", "company-guid")]);

        public Task<TallyCompanyRef> ResolveCompanyAsync(
            string companyName,
            CancellationToken cancellationToken) =>
            Task.FromResult(new TallyCompanyRef(companyName, "company-guid"));

        public Task<TallyMasterQuery> GetMastersAsync(
            TallyCompanyRef company,
            CancellationToken cancellationToken) =>
            Task.FromResult(new TallyMasterQuery(
                new TallyCompanyQueryContext(
                    new Uri("http://127.0.0.1:9000/"),
                    company.RequestedName,
                    company.Name,
                    company.TallyGuid,
                    "Master collection"),
                []));

        public Task<TallyVoucherQuery> GetVouchersAsync(
            TallyCompanyRef company,
            DateRange period,
            CancellationToken cancellationToken) =>
            Task.FromResult(new TallyVoucherQuery(
                new TallyQueryContext(
                    new Uri("http://127.0.0.1:9000/"),
                    company.Name,
                    company.Name,
                    company.TallyGuid,
                    period,
                    "Voucher collection"),
                []));

        public Task<VoucherLookupResult> GetVoucherAsync(
            TallyCompanyRef company,
            DateOnly voucherDate,
            long masterId,
            CancellationToken cancellationToken) =>
            Task.FromResult(Lookup);

        public Task<VoucherInspectionLookupResult> GetVoucherInspectionAsync(
            TallyCompanyRef company,
            DateOnly voucherDate,
            long masterId,
            CancellationToken cancellationToken) =>
            Task.FromResult(InspectionLookup);

        public Task<TallyGstIssueQuery> GetGstFieldIssuesAsync(
            TallyCompanyRef company,
            DateRange period,
            CancellationToken cancellationToken) =>
            Task.FromResult(new TallyGstIssueQuery(
                new TallyQueryContext(
                    new Uri("http://127.0.0.1:9000/"),
                    company.RequestedName,
                    company.Name,
                    company.TallyGuid,
                    period,
                    "Nila GST structural inspection"),
                []));
    }
}
