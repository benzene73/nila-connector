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
}
