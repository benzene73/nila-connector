using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using NilaConnector.Core.Models;
using NilaConnector.Core.Tally;

namespace NilaConnector.Core.Nila;

public sealed partial class JobRunner(ITallyClient tallyClient, ILogger<JobRunner> logger)
{
    public async Task<ConnectorJobResult> ExecuteAsync(
        JsonElement envelope,
        CancellationToken cancellationToken)
    {
        var jobId = RequiredPositiveInt(envelope, "job_id");
        string? jobKind = null;

        try
        {
            jobKind = RequiredString(envelope, "kind");
            return jobKind switch
            {
                "create_voucher" => await ExecuteCreateVoucherAsync(jobId, envelope, cancellationToken),
                "import_vouchers" => await ExecuteVoucherImportAsync(jobId, envelope, cancellationToken),
                _ => throw new InvalidDataException("The connector received an unsupported job kind."),
            };
        }
        catch (Exception exception) when (exception is TallyClientException or InvalidDataException)
        {
            var kind = exception is TallyClientException tallyException
                ? tallyException.Kind.ToString()
                : "InvalidJob";
            var message = Truncate(exception.Message, 300);
            var status = exception is TallyClientException tallyFailure &&
                         tallyFailure.Kind is TallyErrorKind.Timeout or TallyErrorKind.Unreachable
                    ? "indeterminate"
                    : "failed";
            LogFailed(logger, jobId, kind, message);
            return new ConnectorJobResult(jobId, status, new Dictionary<string, object?>
            {
                ["kind"] = kind,
                ["message"] = message,
            });
        }
    }

    private async Task<ConnectorJobResult> ExecuteCreateVoucherAsync(
        int jobId,
        JsonElement envelope,
        CancellationToken cancellationToken)
    {
        var command = ParseCreateVoucher(envelope);
        LogStarting(logger, jobId, command.Company);
        var result = await tallyClient.CreateVoucherAsync(command, cancellationToken);
        LogSucceeded(logger, jobId, result.MasterId);

        return new ConnectorJobResult(jobId, "succeeded", new Dictionary<string, object?>
        {
            ["created"] = result.Created,
            ["errors"] = result.Errors,
            ["master_id"] = result.MasterId,
        });
    }

    private async Task<ConnectorJobResult> ExecuteVoucherImportAsync(
        int jobId,
        JsonElement envelope,
        CancellationToken cancellationToken)
    {
        var command = ParseVoucherImport(envelope);
        var expected = TallyRequestValidator.ReadSingleVoucherFingerprint(command);
        LogStarting(logger, jobId, command.Company);

        if (IsVerificationOnly(envelope))
        {
            return await VerifyVoucherImportAsync(jobId, command, expected, cancellationToken);
        }

        CandidateEvaluation preflight;
        try
        {
            preflight = await EvaluateCandidateAsync(command, expected, cancellationToken);
        }
        catch (TallyClientException exception)
        {
            return Failure(
                jobId,
                "PreflightUnavailable",
                $"Nila could not verify whether this voucher already exists in Tally: {exception.Message}");
        }

        if (preflight.State != CandidateState.Absent)
        {
            return CandidateResult(jobId, preflight, verifiedExisting: true);
        }

        var partyLedger = ParseOptionalPartyLedger(envelope, command.Company);
        var ledgerCreated = false;
        if (partyLedger is not null)
        {
            var masters = await tallyClient.GetMastersAsync(command.Company, cancellationToken);
            var exists = masters.Any(master =>
                string.Equals(master.Kind, "ledger", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(master.Name, partyLedger.Name, StringComparison.OrdinalIgnoreCase));

            if (!exists)
            {
                await tallyClient.CreateLedgerAsync(partyLedger, cancellationToken);
                ledgerCreated = true;
            }
        }

        TallyVoucherBatchResult result;

        try
        {
            result = await tallyClient.ImportVouchersAsync(command, cancellationToken);
        }
        catch (TallyClientException exception)
            when (exception.Kind is TallyErrorKind.Timeout or TallyErrorKind.Unreachable)
        {
            CandidateEvaluation recovery;
            try
            {
                recovery = await EvaluateCandidateAsync(command, expected, cancellationToken);
            }
            catch (TallyClientException readBackException)
            {
                return Indeterminate(
                    jobId,
                    "ReadbackUnavailable",
                    $"Tally did not return a trustworthy import response, and Nila could not verify the outcome: {readBackException.Message}");
            }

            if (recovery.State == CandidateState.Equivalent)
            {
                return CandidateResult(jobId, recovery, recovered: true);
            }

            if (recovery.State == CandidateState.Absent)
            {
                return Indeterminate(
                    jobId,
                    "OutcomeUnknown",
                    "Tally did not return a trustworthy import response. No voucher is visible yet; verify again before retrying the create.");
            }

            return Indeterminate(
                jobId,
                recovery.State == CandidateState.Duplicate ? "DuplicateConflict" : "VoucherConflict",
                recovery.Message);
        }

        CandidateEvaluation readBack;
        try
        {
            readBack = await EvaluateCandidateAsync(command, expected, cancellationToken);
        }
        catch (TallyClientException exception)
        {
            return Indeterminate(
                jobId,
                "ReadbackUnavailable",
                $"Tally accepted the import, but Nila could not read back its authoritative identity: {exception.Message}");
        }

        if (readBack.State != CandidateState.Equivalent)
        {
            return Indeterminate(
                jobId,
                readBack.State == CandidateState.Duplicate
                    ? "PostImportDuplicate"
                    : "PostImportConflict",
                readBack.State == CandidateState.Absent
                    ? "Tally accepted the import, but the voucher was not found during read-back."
                    : readBack.Message);
        }

        LogBatchSucceeded(logger, jobId, command.ExpectedCount, result.LastVoucherId);
        var succeeded = CandidateResult(jobId, readBack);
        var details = new Dictionary<string, object?>(succeeded.Details)
        {
            ["created"] = result.Created,
            ["altered"] = result.Altered,
            ["combined"] = result.Combined,
            ["ignored"] = result.Ignored,
            ["errors"] = result.Errors,
            ["exceptions"] = result.Exceptions,
            ["last_voucher_id"] = result.LastVoucherId,
            ["ledger_created"] = ledgerCreated,
        };
        return succeeded with { Details = details };
    }

    private async Task<ConnectorJobResult> VerifyVoucherImportAsync(
        int jobId,
        ImportVouchersCommand command,
        VoucherImportFingerprint expected,
        CancellationToken cancellationToken)
    {
        CandidateEvaluation evaluation;
        try
        {
            evaluation = await EvaluateCandidateAsync(command, expected, cancellationToken);
        }
        catch (TallyClientException exception)
        {
            return Indeterminate(
                jobId,
                "VerificationUnavailable",
                $"Nila could not verify the voucher in Tally: {exception.Message}");
        }

        if (evaluation.State == CandidateState.Absent)
        {
            return Failure(
                jobId,
                "VerifiedAbsent",
                "The voucher was not found in Tally and is safe to retry.");
        }

        return CandidateResult(jobId, evaluation, verifiedExisting: true);
    }

    private async Task<CandidateEvaluation> EvaluateCandidateAsync(
        ImportVouchersCommand command,
        VoucherImportFingerprint expected,
        CancellationToken cancellationToken)
    {
        var vouchers = await tallyClient.GetVouchersAsync(
            command.Company,
            expected.Date,
            expected.Date,
            cancellationToken);

        var candidates = vouchers.Where(voucher =>
            voucher.Date == expected.Date &&
            string.Equals(
                voucher.VoucherType,
                expected.VoucherType,
                StringComparison.OrdinalIgnoreCase) &&
            string.Equals(
                voucher.VoucherNumber,
                expected.VoucherNumber,
                StringComparison.Ordinal)).ToArray();

        if (candidates.Length == 0)
        {
            return new CandidateEvaluation(
                CandidateState.Absent,
                null,
                "No voucher with the candidate key exists in Tally.");
        }

        if (candidates.Length > 1)
        {
            return new CandidateEvaluation(
                CandidateState.Duplicate,
                null,
                $"Tally contains {candidates.Length} vouchers with the same company, type, number, and date. Nila will not choose one automatically.");
        }

        var candidate = candidates[0];
        if (candidate.MasterId is not > 0)
        {
            return new CandidateEvaluation(
                CandidateState.Conflict,
                candidate,
                "Tally returned a candidate voucher without a usable MasterID.");
        }

        var actual = await tallyClient.GetVoucherFingerprintAsync(
            command.Company,
            expected.Semantic.Date,
            candidate.MasterId.Value,
            cancellationToken);

        if (actual is null)
        {
            return new CandidateEvaluation(
                CandidateState.Conflict,
                candidate,
                "The candidate voucher disappeared before Nila could verify its contents.");
        }

        return string.Equals(
            actual.CanonicalHash,
            expected.Semantic.CanonicalHash,
            StringComparison.Ordinal)
                ? new CandidateEvaluation(
                    CandidateState.Equivalent,
                    candidate,
                    "The existing Tally voucher has the same material contents.")
                : new CandidateEvaluation(
                    CandidateState.Conflict,
                    candidate,
                    "A Tally voucher has the same type, number, and date but different material contents. Nila will not overwrite or duplicate it.");
    }

    private ConnectorJobResult CandidateResult(
        int jobId,
        CandidateEvaluation evaluation,
        bool recovered = false,
        bool verifiedExisting = false)
    {
        if (evaluation.State == CandidateState.Equivalent && evaluation.Voucher is { } voucher)
        {
            LogRecovered(logger, jobId, voucher.MasterId);
            return new ConnectorJobResult(jobId, "succeeded", new Dictionary<string, object?>
            {
                ["recovered"] = recovered,
                ["verified_existing"] = verifiedExisting,
                ["master_id"] = voucher.MasterId,
                ["tally_guid"] = voucher.TallyGuid,
                ["voucher_number"] = voucher.VoucherNumber,
            });
        }

        return Failure(
            jobId,
            evaluation.State == CandidateState.Duplicate ? "DuplicateConflict" : "VoucherConflict",
            evaluation.Message);
    }

    private ConnectorJobResult Failure(int jobId, string kind, string message) =>
        TerminalResult(jobId, "failed", kind, message);

    private ConnectorJobResult Indeterminate(int jobId, string kind, string message) =>
        TerminalResult(jobId, "indeterminate", kind, message);

    private ConnectorJobResult TerminalResult(
        int jobId,
        string status,
        string kind,
        string message)
    {
        message = Truncate(message, 300);
        LogFailed(logger, jobId, kind, message);
        return new ConnectorJobResult(jobId, status, new Dictionary<string, object?>
        {
            ["kind"] = kind,
            ["message"] = message,
        });
    }

    private enum CandidateState
    {
        Absent,
        Equivalent,
        Conflict,
        Duplicate,
    }

    private sealed record CandidateEvaluation(
        CandidateState State,
        TallySyncedVoucher? Voucher,
        string Message);

    private static bool IsVerificationOnly(JsonElement envelope) =>
        envelope.TryGetProperty("payload", out var payload) &&
        payload.ValueKind == JsonValueKind.Object &&
        payload.TryGetProperty("mode", out var mode) &&
        mode.ValueKind == JsonValueKind.String &&
        string.Equals(mode.GetString(), "verify_only", StringComparison.Ordinal);

    private static CreateVoucherCommand ParseCreateVoucher(JsonElement envelope)
    {
        var jobKeyValue = RequiredString(envelope, "job_key");
        if (!Guid.TryParse(jobKeyValue, out var jobKey))
        {
            throw new InvalidDataException("The voucher job key is invalid.");
        }

        if (!envelope.TryGetProperty("payload", out var payload) ||
            payload.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("The voucher job payload is missing.");
        }

        var company = BoundedString(payload, "company", 120);
        var voucherType = BoundedString(payload, "voucher_type", 60);
        var debitLedger = BoundedString(payload, "debit_ledger", 200);
        var creditLedger = BoundedString(payload, "credit_ledger", 200);
        var narration = BoundedString(payload, "narration", 500);

        if (voucherType != "Payment")
        {
            throw new InvalidDataException("Only Payment voucher jobs are supported.");
        }

        if (string.Equals(debitLedger, creditLedger, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Voucher ledgers must be different.");
        }

        if (!DateOnly.TryParseExact(
                RequiredString(payload, "date"),
                "yyyy-MM-dd",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var date))
        {
            throw new InvalidDataException("The voucher date is invalid.");
        }

        if (!decimal.TryParse(
                RequiredString(payload, "amount"),
                NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture,
                out var amount) ||
            amount <= 0 ||
            amount > 1_000_000_000_000m ||
            decimal.Round(amount, 2) != amount)
        {
            throw new InvalidDataException("The voucher amount is invalid.");
        }

        return new CreateVoucherCommand(
            jobKey.ToString("D"),
            company,
            date,
            voucherType,
            debitLedger,
            creditLedger,
            amount,
            narration);
    }

    private static ImportVouchersCommand ParseVoucherImport(JsonElement envelope)
    {
        if (!envelope.TryGetProperty("payload", out var payload) ||
            payload.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("The job payload is missing.");
        }

        var company = BoundedString(payload, "company", 120);
        var xml = BoundedString(payload, "xml", 750_000, trim: false);
        var expectedCount = RequiredPositiveInt(payload, "expected_count");
        return new ImportVouchersCommand(company, xml, expectedCount);
    }

    private static CreateLedgerCommand? ParseOptionalPartyLedger(JsonElement envelope, string company)
    {
        if (!envelope.TryGetProperty("payload", out var payload) ||
            !payload.TryGetProperty("party_ledger", out var ledger) ||
            ledger.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (ledger.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("The party ledger payload is invalid.");
        }

        return new CreateLedgerCommand(
            company,
            BoundedString(ledger, "name", 200),
            BoundedString(ledger, "parent", 100),
            BoundedString(ledger, "gstin", 15).ToUpperInvariant(),
            BoundedString(ledger, "registration_type", 40),
            BoundedString(ledger, "state", 100),
            BoundedString(ledger, "country", 100));
    }

    private static int RequiredPositiveInt(JsonElement element, string property)
    {
        if (element.TryGetProperty(property, out var value) &&
            value.TryGetInt32(out var result) &&
            result > 0)
        {
            return result;
        }

        throw new InvalidDataException($"The job {property} is invalid.");
    }

    private static string BoundedString(
        JsonElement element,
        string property,
        int maxLength,
        bool trim = true)
    {
        var value = RequiredString(element, property);
        value = trim ? value.Trim() : value;
        if (value.Length == 0 || value.Length > maxLength)
        {
            throw new InvalidDataException($"The job {property} is invalid.");
        }

        return value;
    }

    private static string RequiredString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) &&
        value.ValueKind == JsonValueKind.String &&
        value.GetString() is { } result
            ? result
            : throw new InvalidDataException($"The job {property} is invalid.");

    private static string Truncate(string value, int maximum) =>
        value.Length <= maximum ? value : value[..maximum];

    [LoggerMessage(EventId = 5000, Level = LogLevel.Information, Message = "Starting Tally job {JobId} for company {Company}")]
    private static partial void LogStarting(ILogger logger, int jobId, string company);

    [LoggerMessage(EventId = 5001, Level = LogLevel.Information, Message = "Completed Tally job {JobId} with MasterID {MasterId}")]
    private static partial void LogSucceeded(ILogger logger, int jobId, long masterId);

    [LoggerMessage(EventId = 5003, Level = LogLevel.Information, Message = "Completed Tally batch job {JobId} with {VoucherCount} vouchers and last MasterID {LastVoucherId}")]
    private static partial void LogBatchSucceeded(
        ILogger logger,
        int jobId,
        int voucherCount,
        long lastVoucherId);

    [LoggerMessage(EventId = 5002, Level = LogLevel.Warning, Message = "Tally job {JobId} failed ({Kind}): {Reason}")]
    private static partial void LogFailed(ILogger logger, int jobId, string kind, string reason);

    [LoggerMessage(EventId = 5004, Level = LogLevel.Information, Message = "Recovered Tally job {JobId} by read-back with MasterID {MasterId}")]
    private static partial void LogRecovered(ILogger logger, int jobId, long? masterId);
}

public sealed record ConnectorJobResult(
    int JobId,
    string Status,
    IReadOnlyDictionary<string, object?> Details);
