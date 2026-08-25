using NilaConnector.Core.Models;

namespace NilaConnector.Core.Tally.Diagnostics;

public sealed record TallyCompanyQueryContext(
    Uri Endpoint,
    string RequestedCompany,
    string ResolvedCompany,
    string CompanyGuid,
    string Query);

public sealed record TallyMasterQuery(
    TallyCompanyQueryContext Context,
    IReadOnlyList<TallyMaster> Masters);

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

public abstract record VoucherInspectionLookupResult(TallyQueryContext Context)
{
    public sealed record Found(
        TallyQueryContext QueryContext,
        VoucherInspection Inspection) : VoucherInspectionLookupResult(QueryContext);

    public sealed record NotFound(TallyQueryContext QueryContext) : VoucherInspectionLookupResult(QueryContext);
}

public sealed record TallyGstIssue(
    DateOnly Date,
    long MasterId,
    string VoucherType,
    string? VoucherNumber,
    string LedgerName,
    string Code,
    string Message);

public sealed record TallyGstIssueQuery(
    TallyQueryContext Context,
    IReadOnlyList<TallyGstIssue> Issues);
