using NilaConnector.Core.Models;
using NilaConnector.Core.Tally;

namespace NilaConnector.Core.Tests.Tally;

public sealed class CompositeTallyClientTests
{
    [Fact]
    public async Task RoutesDiscoveryAndWritesToGatewayAndSyncReadsToTypedReader()
    {
        var gateway = new RecordingGatewayClient();
        var reader = new RecordingReadClient();
        using var client = new CompositeTallyClient(gateway, reader);
        var create = new CreateVoucherCommand(
            "job-1",
            "Nila",
            new DateOnly(2026, 8, 20),
            "Payment",
            "Expense",
            "Cash",
            1m,
            "test");
        var import = new ImportVouchersCommand("Nila", "<ENVELOPE />", 1);

        Assert.Same(gateway.Companies, await client.GetOpenCompaniesAsync(CancellationToken.None));
        Assert.Same(reader.Masters, await client.GetMastersAsync("Nila", CancellationToken.None));
        Assert.Same(
            reader.Vouchers,
            await client.GetVouchersAsync(
                "Nila",
                new DateOnly(2026, 8, 1),
                new DateOnly(2026, 8, 20),
                CancellationToken.None));
        Assert.Equal(
            reader.Fingerprint,
            await client.GetVoucherFingerprintAsync("Nila", 1, CancellationToken.None));
        Assert.Equal(gateway.CreateResult, await client.CreateVoucherAsync(create, CancellationToken.None));
        Assert.Equal(gateway.ImportResult, await client.ImportVouchersAsync(import, CancellationToken.None));

        Assert.Equal(1, gateway.DiscoveryCalls);
        Assert.Equal(1, gateway.CreateCalls);
        Assert.Equal(1, gateway.ImportCalls);
        Assert.Equal(1, reader.MasterCalls);
        Assert.Equal(1, reader.VoucherCalls);
        Assert.Equal(1, reader.FingerprintCalls);
        Assert.Equal("Nila", reader.LastCompany);
    }

    [Fact]
    public async Task SerializesWritesAndReadsAcrossTheSharedTallyConnection()
    {
        var gateway = new BlockingGatewayClient();
        var reader = new RecordingReadClient();
        using var client = new CompositeTallyClient(gateway, reader);
        var command = new ImportVouchersCommand("Nila", "<ENVELOPE />", 1);

        var importTask = client.ImportVouchersAsync(command, CancellationToken.None);
        await gateway.ImportEntered.Task.WaitAsync(TimeSpan.FromSeconds(1));

        var readTask = client.GetMastersAsync("Nila", CancellationToken.None);

        Assert.False(readTask.IsCompleted);
        Assert.Equal(0, reader.MasterCalls);

        gateway.ReleaseImport.TrySetResult(true);
        await importTask;
        await readTask;

        Assert.Equal(1, reader.MasterCalls);
    }

    private sealed class RecordingGatewayClient : ITallyGatewayClient
    {
        public IReadOnlyList<TallyCompany> Companies { get; } = [new("Nila", "guid")];
        public TallyVoucherResult CreateResult { get; } = new(1, 0, 42);
        public TallyVoucherBatchResult ImportResult { get; } = new(1, 0, 0, 0, 0, 0, 42);
        public int DiscoveryCalls { get; private set; }
        public int CreateCalls { get; private set; }
        public int ImportCalls { get; private set; }

        public Task<IReadOnlyList<TallyCompany>> GetOpenCompaniesAsync(CancellationToken cancellationToken)
        {
            DiscoveryCalls++;
            return Task.FromResult(Companies);
        }

        public Task<TallyVoucherResult> CreateVoucherAsync(
            CreateVoucherCommand command,
            CancellationToken cancellationToken)
        {
            CreateCalls++;
            return Task.FromResult(CreateResult);
        }

        public Task<TallyVoucherBatchResult> ImportVouchersAsync(
            ImportVouchersCommand command,
            CancellationToken cancellationToken)
        {
            ImportCalls++;
            return Task.FromResult(ImportResult);
        }
    }

    private sealed class RecordingReadClient : ITallyReadClient
    {
        public IReadOnlyList<TallyMaster> Masters { get; } =
        [
            new(
                "ledger",
                "guid:ledger",
                "Cash",
                "ledger",
                "1",
                new Dictionary<string, object?>()),
        ];

        public IReadOnlyList<TallySyncedVoucher> Vouchers { get; } =
        [
            new(
                "guid:voucher",
                1,
                "voucher",
                null,
                new DateOnly(2026, 8, 20),
                "Payment",
                "1",
                null,
                new Dictionary<string, object?>()),
        ];

        public int MasterCalls { get; private set; }
        public int VoucherCalls { get; private set; }
        public int FingerprintCalls { get; private set; }
        public string? LastCompany { get; private set; }
        public VoucherSemanticFingerprint Fingerprint { get; } =
            new(new DateOnly(2026, 8, 20), "Payment", "1", new string('a', 64));

        public Task<IReadOnlyList<TallyMaster>> GetMastersAsync(
            string company,
            CancellationToken cancellationToken)
        {
            MasterCalls++;
            LastCompany = company;
            return Task.FromResult(Masters);
        }

        public Task<IReadOnlyList<TallySyncedVoucher>> GetVouchersAsync(
            string company,
            DateOnly fromDate,
            DateOnly toDate,
            CancellationToken cancellationToken)
        {
            VoucherCalls++;
            LastCompany = company;
            return Task.FromResult(Vouchers);
        }

        public Task<VoucherSemanticFingerprint?> GetVoucherFingerprintAsync(
            string company,
            long masterId,
            CancellationToken cancellationToken)
        {
            FingerprintCalls++;
            LastCompany = company;
            return Task.FromResult<VoucherSemanticFingerprint?>(Fingerprint);
        }
    }

    private sealed class BlockingGatewayClient : ITallyGatewayClient
    {
        public TaskCompletionSource<bool> ImportEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<bool> ReleaseImport { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<IReadOnlyList<TallyCompany>> GetOpenCompaniesAsync(
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<TallyCompany>>([]);

        public Task<TallyVoucherResult> CreateVoucherAsync(
            CreateVoucherCommand command,
            CancellationToken cancellationToken) =>
            Task.FromResult(new TallyVoucherResult(1, 0, 42));

        public async Task<TallyVoucherBatchResult> ImportVouchersAsync(
            ImportVouchersCommand command,
            CancellationToken cancellationToken)
        {
            ImportEntered.TrySetResult(true);
            await ReleaseImport.Task.WaitAsync(cancellationToken);
            return new TallyVoucherBatchResult(1, 0, 0, 0, 0, 0, 42);
        }
    }
}
