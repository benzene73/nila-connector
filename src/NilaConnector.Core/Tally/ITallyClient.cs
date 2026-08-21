using NilaConnector.Core.Models;

namespace NilaConnector.Core.Tally;

public interface ITallyClient
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
        long masterId,
        CancellationToken cancellationToken);

    Task<TallyVoucherResult> CreateVoucherAsync(
        CreateVoucherCommand command,
        CancellationToken cancellationToken);

    Task<TallyVoucherBatchResult> ImportVouchersAsync(
        ImportVouchersCommand command,
        CancellationToken cancellationToken);
}
