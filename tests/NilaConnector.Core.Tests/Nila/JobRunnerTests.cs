using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using NilaConnector.Core.Models;
using NilaConnector.Core.Nila;
using NilaConnector.Core.Tally;

namespace NilaConnector.Core.Tests.Nila;

public sealed class JobRunnerTests
{
    [Theory]
    [InlineData(TallyErrorKind.Timeout)]
    [InlineData(TallyErrorKind.Unreachable)]
    public async Task TreatsAmbiguousTallyFailuresAsIndeterminate(TallyErrorKind kind)
    {
        var runner = new JobRunner(new ThrowingTallyClient(kind), NullLogger<JobRunner>.Instance);
        using var document = JsonDocument.Parse(JobJson);

        var result = await runner.ExecuteAsync(document.RootElement, CancellationToken.None);

        Assert.Equal("indeterminate", result.Status);
        Assert.Equal(kind.ToString(), result.Details["kind"]);
    }

    [Fact]
    public async Task TreatsTallyApplicationRejectionsAsFailed()
    {
        var runner = new JobRunner(
            new ThrowingTallyClient(TallyErrorKind.ApplicationError),
            NullLogger<JobRunner>.Instance);
        using var document = JsonDocument.Parse(JobJson);

        var result = await runner.ExecuteAsync(document.RootElement, CancellationToken.None);

        Assert.Equal("failed", result.Status);
    }

    private const string JobJson = """
        {
          "job_id": 12,
          "job_key": "d45f7537-00d5-4463-9d70-f246257d88ef",
          "kind": "create_voucher",
          "payload": {
            "company": "Nila",
            "date": "2026-08-01",
            "voucher_type": "Payment",
            "debit_ledger": "NILA Playground Customer",
            "credit_ledger": "Cash",
            "amount": "1.00",
            "narration": "Nila connector test"
          }
        }
        """;

    private sealed class ThrowingTallyClient(TallyErrorKind kind) : ITallyClient
    {
        public Task<IReadOnlyList<TallyCompany>> GetOpenCompaniesAsync(
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<TallyCompany>>([]);

        public Task<TallyVoucherResult> CreateVoucherAsync(
            CreateVoucherCommand command,
            CancellationToken cancellationToken) =>
            Task.FromException<TallyVoucherResult>(
                new TallyClientException(kind, "Safe diagnostic message."));
    }
}
