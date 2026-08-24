using NilaConnector.Core.Models;
using NilaConnector.Core.Tally;
using NilaConnector.Core.Tally.Diagnostics;

namespace NilaConnector.Core.Tests.Tally;

public sealed class TallyReadDiagnosticsTests
{
    [Fact]
    public void DateRangeRejectsAnInvertedPeriod()
    {
        Assert.Throws<ArgumentException>(() =>
            new DateRange(new DateOnly(2026, 1, 2), new DateOnly(2026, 1, 1)));
    }

    [Fact]
    public async Task VoucherQueryVerifiesCompanyGuidAndPassesRequiredPeriod()
    {
        var client = new FakeReadClient
        {
            Companies = [new("Caltech", "company-guid")],
            Vouchers = [Voucher(1)],
        };
        var diagnostics = Diagnostics(client);
        var period = new DateRange(new DateOnly(2025, 12, 1), new DateOnly(2025, 12, 31));

        var result = await diagnostics.GetVouchersAsync(
            new TallyCompanyRef("caltech", "Caltech", "company-guid"),
            period,
            CancellationToken.None);

        Assert.Single(result.Vouchers);
        Assert.Equal(period.From, client.LastFromDate);
        Assert.Equal(period.To, client.LastToDate);
        Assert.Equal("caltech", result.Context.RequestedCompany);
        Assert.Equal("Caltech", result.Context.ResolvedCompany);
        Assert.Equal("company-guid", result.Context.CompanyGuid);
    }

    [Fact]
    public async Task VoucherQueryFailsBeforeReadingWhenCompanyGuidChanged()
    {
        var client = new FakeReadClient { Companies = [new("Caltech", "new-guid")] };
        var diagnostics = Diagnostics(client);

        var exception = await Assert.ThrowsAsync<TallyClientException>(() =>
            diagnostics.GetVouchersAsync(
                new TallyCompanyRef("Caltech", "old-guid"),
                new DateRange(new DateOnly(2025, 12, 1), new DateOnly(2025, 12, 31)),
                CancellationToken.None));

        Assert.Contains("GUID changed", exception.Message, StringComparison.Ordinal);
        Assert.Equal(0, client.VoucherReads);
    }

    [Fact]
    public async Task MissingVoucherIsAnExplicitContextualResult()
    {
        var client = new FakeReadClient { Companies = [new("Caltech", "company-guid")] };
        var diagnostics = Diagnostics(client);

        var result = await diagnostics.GetVoucherAsync(
            new TallyCompanyRef("Caltech", "company-guid"),
            new DateOnly(2025, 12, 4),
            118,
            CancellationToken.None);

        var notFound = Assert.IsType<VoucherLookupResult.NotFound>(result);
        Assert.Equal("Voucher MasterID 118", notFound.Context.Query);
        Assert.Equal(new DateOnly(2025, 12, 4), notFound.Context.Period.From);
        Assert.Equal(notFound.Context.Period.From, notFound.Context.Period.To);
    }

    [Fact]
    public async Task DuplicateMasterIdIsAnExplicitConflict()
    {
        var client = new FakeReadClient
        {
            Companies = [new("Caltech", "company-guid")],
            Vouchers = [Voucher(118), Voucher(118)],
        };
        var diagnostics = Diagnostics(client);

        var result = await diagnostics.GetVoucherAsync(
            new TallyCompanyRef("Caltech", "company-guid"),
            new DateOnly(2025, 12, 4),
            118,
            CancellationToken.None);

        var conflict = Assert.IsType<VoucherLookupResult.Conflict>(result);
        Assert.Equal(2, conflict.Matches.Count);
    }

    private static TallyReadDiagnostics Diagnostics(FakeReadClient client) =>
        new(client, new TallyClientOptions { Endpoint = new Uri("http://127.0.0.1:9000/") });

    private static TallySyncedVoucher Voucher(long masterId) =>
        new(
            $"voucher-{masterId}",
            masterId,
            $"guid-{masterId}",
            null,
            new DateOnly(2025, 12, 4),
            "Sales",
            $"{masterId}/2025-26",
            null,
            new Dictionary<string, object?>());

    private sealed class FakeReadClient : ITallyReadClient
    {
        public IReadOnlyList<TallyCompany> Companies { get; init; } = [];

        public IReadOnlyList<TallySyncedVoucher> Vouchers { get; init; } = [];

        public int VoucherReads { get; private set; }

        public DateOnly? LastFromDate { get; private set; }

        public DateOnly? LastToDate { get; private set; }

        public Task<IReadOnlyList<TallyCompany>> GetOpenCompaniesAsync(
            CancellationToken cancellationToken) =>
            Task.FromResult(Companies);

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
            VoucherReads++;
            LastFromDate = fromDate;
            LastToDate = toDate;
            return Task.FromResult(Vouchers);
        }

        public Task<VoucherSemanticFingerprint?> GetVoucherFingerprintAsync(
            string company,
            DateOnly voucherDate,
            long masterId,
            CancellationToken cancellationToken) =>
            Task.FromResult<VoucherSemanticFingerprint?>(null);
    }
}
