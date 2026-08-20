using NilaConnector.Core.Models;

namespace NilaConnector.Core.Tally;

public sealed class CompositeTallyClient : ITallyClient, IDisposable
{
    private readonly ITallyGatewayClient _gatewayClient;
    private readonly ITallyReadClient _readClient;
    private bool _disposed;

    public CompositeTallyClient(ITallyGatewayClient gatewayClient, ITallyReadClient readClient)
    {
        _gatewayClient = gatewayClient ?? throw new ArgumentNullException(nameof(gatewayClient));
        _readClient = readClient ?? throw new ArgumentNullException(nameof(readClient));
    }

    public Task<IReadOnlyList<TallyCompany>> GetOpenCompaniesAsync(CancellationToken cancellationToken) =>
        _gatewayClient.GetOpenCompaniesAsync(cancellationToken);

    public Task<IReadOnlyList<TallyMaster>> GetMastersAsync(
        string company,
        CancellationToken cancellationToken) =>
        _readClient.GetMastersAsync(company, cancellationToken);

    public Task<IReadOnlyList<TallySyncedVoucher>> GetVouchersAsync(
        string company,
        DateOnly fromDate,
        DateOnly toDate,
        CancellationToken cancellationToken) =>
        _readClient.GetVouchersAsync(company, fromDate, toDate, cancellationToken);

    public Task<TallyVoucherResult> CreateVoucherAsync(
        CreateVoucherCommand command,
        CancellationToken cancellationToken) =>
        _gatewayClient.CreateVoucherAsync(command, cancellationToken);

    public Task<TallyVoucherBatchResult> ImportVouchersAsync(
        ImportVouchersCommand command,
        CancellationToken cancellationToken) =>
        _gatewayClient.ImportVouchersAsync(command, cancellationToken);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        if (_gatewayClient is IDisposable gatewayDisposable)
        {
            gatewayDisposable.Dispose();
        }

        if (_readClient is IDisposable readDisposable && !ReferenceEquals(_readClient, _gatewayClient))
        {
            readDisposable.Dispose();
        }

        _disposed = true;
    }
}
