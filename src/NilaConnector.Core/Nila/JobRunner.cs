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

        try
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
        catch (Exception exception) when (exception is TallyClientException or InvalidDataException)
        {
            var kind = exception is TallyClientException tallyException
                ? tallyException.Kind.ToString()
                : "InvalidJob";
            var message = Truncate(exception.Message, 300);
            var status = exception is TallyClientException
                {
                    Kind: TallyErrorKind.Timeout or TallyErrorKind.Unreachable,
                }
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

    private static CreateVoucherCommand ParseCreateVoucher(JsonElement envelope)
    {
        if (RequiredString(envelope, "kind") != "create_voucher")
        {
            throw new InvalidDataException("The connector received an unsupported job kind.");
        }

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

    private static string BoundedString(JsonElement element, string property, int maxLength)
    {
        var value = RequiredString(element, property).Trim();
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

    [LoggerMessage(EventId = 5002, Level = LogLevel.Warning, Message = "Tally job {JobId} failed ({Kind}): {Reason}")]
    private static partial void LogFailed(ILogger logger, int jobId, string kind, string reason);
}

public sealed record ConnectorJobResult(
    int JobId,
    string Status,
    IReadOnlyDictionary<string, object?> Details);
