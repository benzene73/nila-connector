using NilaConnector.Core.Models;

namespace NilaConnector.Core.Tally.Diagnostics;

public sealed class TallyReadDiagnostics(
    ITallyReadClient readClient,
    TallyClientOptions options) : ITallyReadDiagnostics
{
    public Task<IReadOnlyList<TallyCompany>> GetCompaniesAsync(CancellationToken cancellationToken) =>
        readClient.GetOpenCompaniesAsync(cancellationToken);

    public async Task<TallyCompanyRef> ResolveCompanyAsync(
        string companyName,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(companyName);
        var matches = (await GetCompaniesAsync(cancellationToken))
            .Where(company => string.Equals(
                company.Name,
                companyName.Trim(),
                StringComparison.OrdinalIgnoreCase))
            .ToArray();

        return matches switch
        {
            [] => throw new TallyClientException(
                TallyErrorKind.InvalidResponse,
                $"The requested company is not open in Tally: {companyName.Trim()}"),
            [var company] when !string.IsNullOrWhiteSpace(company.TallyGuid) =>
                new TallyCompanyRef(companyName, company.Name, company.TallyGuid),
            [var company] => throw new TallyClientException(
                TallyErrorKind.InvalidResponse,
                $"Tally returned the company without a GUID: {company.Name}"),
            _ => throw new TallyClientException(
                TallyErrorKind.InvalidResponse,
                $"Tally returned more than one open company named: {companyName.Trim()}"),
        };
    }

    public async Task<TallyMasterQuery> GetMastersAsync(
        TallyCompanyRef company,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(company);
        var resolved = await VerifyCompanyAsync(company, cancellationToken);
        var masters = await readClient.GetMastersAsync(resolved.Name, cancellationToken);
        return new TallyMasterQuery(
            CompanyContext(resolved, "Master collection"),
            masters);
    }

    public async Task<TallyVoucherQuery> GetVouchersAsync(
        TallyCompanyRef company,
        DateRange period,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(company);
        ArgumentNullException.ThrowIfNull(period);
        var resolved = await VerifyCompanyAsync(company, cancellationToken);
        var vouchers = await readClient.GetVouchersAsync(
            resolved.Name,
            period.From,
            period.To,
            cancellationToken);

        return new TallyVoucherQuery(
            Context(resolved, period, "Voucher collection"),
            vouchers);
    }

    public async Task<VoucherLookupResult> GetVoucherAsync(
        TallyCompanyRef company,
        DateOnly voucherDate,
        long masterId,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(masterId);
        var query = await GetVouchersAsync(
            company,
            new DateRange(voucherDate, voucherDate),
            cancellationToken);
        var context = query.Context with { Query = $"Voucher MasterID {masterId}" };
        var matches = query.Vouchers.Where(voucher => voucher.MasterId == masterId).ToArray();

        return matches switch
        {
            [] => new VoucherLookupResult.NotFound(context),
            [var voucher] => new VoucherLookupResult.Found(context, voucher),
            _ => new VoucherLookupResult.Conflict(context, matches),
        };
    }

    public async Task<VoucherInspectionLookupResult> GetVoucherInspectionAsync(
        TallyCompanyRef company,
        DateOnly voucherDate,
        long masterId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(company);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(masterId);
        var resolved = await VerifyCompanyAsync(company, cancellationToken);
        var period = new DateRange(voucherDate, voucherDate);
        var context = Context(resolved, period, $"Voucher inspection MasterID {masterId}");
        var inspection = await readClient.GetVoucherInspectionAsync(
            resolved.Name,
            voucherDate,
            masterId,
            cancellationToken);

        return inspection is null
            ? new VoucherInspectionLookupResult.NotFound(context)
            : new VoucherInspectionLookupResult.Found(context, inspection);
    }

    public async Task<TallyGstIssueQuery> GetGstFieldIssuesAsync(
        TallyCompanyRef company,
        DateRange period,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(company);
        ArgumentNullException.ThrowIfNull(period);
        var resolved = await VerifyCompanyAsync(company, cancellationToken);
        var vouchers = await readClient.GetVouchersAsync(
            resolved.Name,
            period.From,
            period.To,
            cancellationToken);
        var issues = new List<TallyGstIssue>();

        foreach (var voucher in vouchers.Where(IsSalesVoucher))
        {
            var inspection = await readClient.GetVoucherInspectionAsync(
                resolved.Name,
                voucher.Date,
                voucher.MasterId!.Value,
                cancellationToken);
            if (inspection is null)
            {
                issues.Add(Issue(voucher, "voucher_not_found", "(voucher)",
                    "The date-scoped inspection could not find this voucher."));
                continue;
            }

            AddGstFieldIssues(inspection, issues);
        }

        return new TallyGstIssueQuery(
            Context(resolved, period, "Nila GST structural inspection"),
            issues);
    }

    private async Task<TallyCompanyRef> VerifyCompanyAsync(
        TallyCompanyRef requested,
        CancellationToken cancellationToken)
    {
        var resolved = await ResolveCompanyAsync(requested.Name, cancellationToken);
        if (!string.Equals(resolved.TallyGuid, requested.TallyGuid, StringComparison.OrdinalIgnoreCase))
        {
            throw new TallyClientException(
                TallyErrorKind.InvalidResponse,
                $"The loaded company GUID changed for {requested.Name}. " +
                $"Requested {requested.TallyGuid}; resolved {resolved.TallyGuid}.");
        }

        return new TallyCompanyRef(requested.RequestedName, resolved.Name, resolved.TallyGuid);
    }

    private TallyQueryContext Context(
        TallyCompanyRef company,
        DateRange period,
        string query) =>
        new(
            options.Endpoint,
            company.RequestedName,
            company.Name,
            company.TallyGuid,
            period,
            query);

    private TallyCompanyQueryContext CompanyContext(
        TallyCompanyRef company,
        string query) =>
        new(
            options.Endpoint,
            company.RequestedName,
            company.Name,
            company.TallyGuid,
            query);

    private static bool IsSalesVoucher(TallySyncedVoucher voucher) =>
        voucher.MasterId is > 0 &&
        string.Equals(voucher.VoucherType, "Sales", StringComparison.OrdinalIgnoreCase);

    private static void AddGstFieldIssues(
        VoucherInspection voucher,
        List<TallyGstIssue> issues)
    {
        var gstLedgers = voucher.LedgerEntries.Where(HasGstMaterial).ToArray();
        var missingTaxability = gstLedgers.FirstOrDefault(ledger =>
            string.IsNullOrWhiteSpace(ledger.GstTaxability));
        if (missingTaxability is not null)
        {
            issues.Add(Issue(voucher, missingTaxability, "missing_taxability",
                "GST taxability type is missing on one or more GST ledger lines."));
        }

        var missingHsn = gstLedgers.FirstOrDefault(ledger => string.IsNullOrWhiteSpace(ledger.HsnSac));
        if (missingHsn is not null)
        {
            issues.Add(Issue(voucher, missingHsn, "missing_hsn_sac",
                "HSN/SAC is missing on one or more GST ledger lines; " +
                "classification mismatches require Tally's native GST report."));
        }

        foreach (var ledger in gstLedgers)
        {
            var missingHeads = ledger.Rates
                .Where(IsCessWithoutValuation)
                .Select(rate => rate.DutyHead!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (missingHeads.Length > 0)
            {
                issues.Add(Issue(voucher, ledger, "missing_cess_valuation",
                    $"Cess valuation type is missing ({string.Join(", ", missingHeads)})."));
            }
        }
    }

    private static bool HasGstMaterial(VoucherLedgerInspection ledger) =>
        ledger.AssessableValue is not null ||
        !string.IsNullOrWhiteSpace(ledger.GstTaxability) ||
        !string.IsNullOrWhiteSpace(ledger.GstSupplyType) ||
        !string.IsNullOrWhiteSpace(ledger.HsnSac) ||
        ledger.Rates.Any(rate => !string.IsNullOrWhiteSpace(rate.DutyHead));

    private static bool IsCessWithoutValuation(VoucherGstRateInspection rate) =>
        (string.Equals(rate.DutyHead, "Cess", StringComparison.OrdinalIgnoreCase) ||
         string.Equals(rate.DutyHead, "State Cess", StringComparison.OrdinalIgnoreCase)) &&
        string.IsNullOrWhiteSpace(rate.ValuationType);

    private static TallyGstIssue Issue(
        TallySyncedVoucher voucher,
        string code,
        string ledgerName,
        string message) =>
        new(
            voucher.Date,
            voucher.MasterId!.Value,
            voucher.VoucherType,
            voucher.VoucherNumber,
            ledgerName,
            code,
            message);

    private static TallyGstIssue Issue(
        VoucherInspection voucher,
        VoucherLedgerInspection ledger,
        string code,
        string message) =>
        new(
            voucher.Date,
            voucher.MasterId,
            voucher.VoucherType,
            voucher.VoucherNumber,
            ledger.LedgerName,
            code,
            message);
}
