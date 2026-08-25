using NilaConnector.Core.Models;

namespace NilaConnector.Core.Tally;

public interface ITallyMasterClient
{
    Task<TallyMasterWriteResult> CreateLedgerAsync(
        CreateLedgerCommand command,
        CancellationToken cancellationToken);
}
