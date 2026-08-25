using NilaConnector.Core.Models;

namespace NilaConnector.Core.Tally;

public interface ITallyReadClient
{
    Task<IReadOnlyList<TallyCompany>> GetOpenCompaniesAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<TallyMaster>> GetMastersAsync(
        string company,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<TallySyncedVoucher>> GetVouchersAsync(
        string company,
        DateOnly fromDate,
        DateOnly toDate,
        CancellationToken cancellationToken);

    Task<VoucherSemanticFingerprint?> GetVoucherFingerprintAsync(
        string company,
        DateOnly voucherDate,
        long masterId,
        CancellationToken cancellationToken);

    Task<VoucherInspection?> GetVoucherInspectionAsync(
        string company,
        DateOnly voucherDate,
        long masterId,
        CancellationToken cancellationToken);
}
