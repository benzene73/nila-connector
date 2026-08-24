using NilaConnector.Core.Models;

namespace NilaConnector.Core.Tally;

public interface ITallyGatewayClient
{
    Task<TallyVoucherResult> CreateVoucherAsync(
        CreateVoucherCommand command,
        CancellationToken cancellationToken);

    Task<TallyVoucherBatchResult> CreateLedgerAsync(
        CreateLedgerCommand command,
        CancellationToken cancellationToken) =>
        Task.FromException<TallyVoucherBatchResult>(new NotSupportedException());

    Task<TallyVoucherBatchResult> ImportVouchersAsync(
        ImportVouchersCommand command,
        CancellationToken cancellationToken);
}
