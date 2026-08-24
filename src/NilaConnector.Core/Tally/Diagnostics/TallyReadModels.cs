using NilaConnector.Core.Models;

namespace NilaConnector.Core.Tally.Diagnostics;

public sealed record TallyQueryContext(
    Uri Endpoint,
    string RequestedCompany,
    string ResolvedCompany,
    string CompanyGuid,
    DateRange Period,
    string Query);

public sealed record TallyVoucherQuery(
    TallyQueryContext Context,
    IReadOnlyList<TallySyncedVoucher> Vouchers);

public abstract record VoucherLookupResult(TallyQueryContext Context)
{
    public sealed record Found(
        TallyQueryContext QueryContext,
        TallySyncedVoucher Voucher) : VoucherLookupResult(QueryContext);

    public sealed record NotFound(TallyQueryContext QueryContext) : VoucherLookupResult(QueryContext);

    public sealed record Conflict(
        TallyQueryContext QueryContext,
        IReadOnlyList<TallySyncedVoucher> Matches) : VoucherLookupResult(QueryContext);
}
