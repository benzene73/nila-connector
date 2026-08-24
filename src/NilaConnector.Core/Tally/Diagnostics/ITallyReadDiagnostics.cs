using NilaConnector.Core.Models;

namespace NilaConnector.Core.Tally.Diagnostics;

public interface ITallyReadDiagnostics
{
    Task<IReadOnlyList<TallyCompany>> GetCompaniesAsync(CancellationToken cancellationToken);

    Task<TallyCompanyRef> ResolveCompanyAsync(
        string companyName,
        CancellationToken cancellationToken);

    Task<TallyVoucherQuery> GetVouchersAsync(
        TallyCompanyRef company,
        DateRange period,
        CancellationToken cancellationToken);

    Task<VoucherLookupResult> GetVoucherAsync(
        TallyCompanyRef company,
        DateOnly voucherDate,
        long masterId,
        CancellationToken cancellationToken);
}
