using NilaConnector.Core.Models;

namespace NilaConnector.Core.Tally;

public interface ITallyClient
{
    Task<IReadOnlyList<TallyCompany>> GetOpenCompaniesAsync(CancellationToken cancellationToken);

    Task<TallyVoucherResult> CreateVoucherAsync(
        CreateVoucherCommand command,
        CancellationToken cancellationToken);
}
