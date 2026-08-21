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
        LogStarting(logger, jobId, command.Company);

        if (IsVerificationOnly(envelope))
        {
            return await VerifyVoucherImportAsync(jobId, command, cancellationToken);
        }

        TallyVoucherBatchResult result;

        try
        {
            result = await tallyClient.ImportVouchersAsync(command, cancellationToken);
        }
        catch (TallyClientException exception)
            when (exception.Kind is TallyErrorKind.Timeout or TallyErrorKind.Unreachable)
        {
            var recovered = await TryRecoverVoucherImportAsync(command, cancellationToken);
            if (recovered is null)
            {
                throw;
            }

            LogRecovered(logger, jobId, recovered.MasterId);
            return new ConnectorJobResult(jobId, "succeeded", new Dictionary<string, object?>
            {
                ["recovered"] = true,
                ["master_id"] = recovered.MasterId,
                ["tally_guid"] = recovered.TallyGuid,
                ["voucher_number"] = recovered.VoucherNumber,
            });
        }

        LogBatchSucceeded(logger, jobId, command.ExpectedCount, result.LastVoucherId);

        return new ConnectorJobResult(jobId, "succeeded", new Dictionary<string, object?>
        {
            ["created"] = result.Created,
            ["altered"] = result.Altered,
            ["combined"] = result.Combined,
            ["ignored"] = result.Ignored,
            ["errors"] = result.Errors,
            ["exceptions"] = result.Exceptions,
            ["last_voucher_id"] = result.LastVoucherId,
        });
    }

    private async Task<ConnectorJobResult> VerifyVoucherImportAsync(
        int jobId,
        ImportVouchersCommand command,
        CancellationToken cancellationToken)
    {
        VoucherImportFingerprint fingerprint =
            TallyRequestValidator.ReadSingleVoucherFingerprint(command);

        IReadOnlyList<TallySyncedVoucher> vouchers;
        try
        {
            vouchers = await tallyClient.GetVouchersAsync(
                command.Company,
                fingerprint.Date,
                fingerprint.Date,
                cancellationToken);
        }
        catch (TallyClientException exception)
        {
            var message = Truncate(exception.Message, 300);
            LogFailed(logger, jobId, exception.Kind.ToString(), message);
            return new ConnectorJobResult(jobId, "indeterminate", new Dictionary<string, object?>
            {
                ["kind"] = exception.Kind.ToString(),
                ["message"] = message,
            });
        }

        var matches = MatchingVouchers(vouchers, fingerprint);
        if (matches.Length == 0)
        {
            return new ConnectorJobResult(jobId, "failed", new Dictionary<string, object?>
            {
                ["kind"] = "VerifiedAbsent",
                ["message"] = "The voucher was not found in Tally and is safe to retry.",
            });
        }

        var match = matches[0];
        LogRecovered(logger, jobId, match.MasterId);
        return new ConnectorJobResult(jobId, "succeeded", new Dictionary<string, object?>
        {
            ["verified_existing"] = true,
            ["master_id"] = match.MasterId,
            ["tally_guid"] = match.TallyGuid,
            ["voucher_number"] = match.VoucherNumber,
        });
    }

    private async Task<TallySyncedVoucher?> TryRecoverVoucherImportAsync(
        ImportVouchersCommand command,
        CancellationToken cancellationToken)
    {
        VoucherImportFingerprint fingerprint;
        try
        {
            fingerprint = TallyRequestValidator.ReadSingleVoucherFingerprint(command);
        }
        catch (InvalidDataException)
        {
            return null;
        }

        IReadOnlyList<TallySyncedVoucher> vouchers;
        try
        {
            vouchers = await tallyClient.GetVouchersAsync(
                command.Company,
                fingerprint.Date,
                fingerprint.Date,
                cancellationToken);
        }
        catch (TallyClientException)
        {
            return null;
        }

        var matches = MatchingVouchers(vouchers, fingerprint);

        return matches.Length == 1 ? matches[0] : null;
    }

    private static TallySyncedVoucher[] MatchingVouchers(
        IReadOnlyList<TallySyncedVoucher> vouchers,
        VoucherImportFingerprint fingerprint) =>
        vouchers.Where(voucher =>
            (string.Equals(voucher.TallyGuid, fingerprint.Guid, StringComparison.OrdinalIgnoreCase) ||
             string.Equals(voucher.RemoteId, fingerprint.Guid, StringComparison.OrdinalIgnoreCase)) &&
            voucher.Date == fingerprint.Date &&
            string.Equals(voucher.VoucherType, fingerprint.VoucherType, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(voucher.VoucherNumber, fingerprint.VoucherNumber, StringComparison.Ordinal)).ToArray();

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
