using System.Globalization;
using System.Text.Json;
using NilaConnector.Core.Models;
using NilaConnector.Core.Tally.Diagnostics;

namespace NilaConnector.Diagnostics;

internal static class DiagnosticsCli
{
    private static readonly Uri DefaultEndpoint = new("http://127.0.0.1:9000/");
    private static readonly JsonSerializerOptions ExportJsonOptions = new() { WriteIndented = true };

    public static async Task<int> RunAsync(
        string[] args,
        Func<Uri, ITallyReadDiagnostics> diagnosticsFactory,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(diagnosticsFactory);

        try
        {
            var command = Parse(args);
            var diagnostics = diagnosticsFactory(command.Endpoint);

            return command.Name switch
            {
                "companies" => await CompaniesAsync(command, diagnostics, output, cancellationToken),
                "vouchers" => await VouchersAsync(command, diagnostics, output, cancellationToken),
                "voucher" => await VoucherAsync(command, diagnostics, output, false, cancellationToken),
                "export-voucher" => await VoucherAsync(command, diagnostics, output, true, cancellationToken),
                _ => Usage(error, $"Unknown command: {command.Name}"),
            };
        }
        catch (OperationCanceledException)
        {
            await error.WriteLineAsync("The Tally read was cancelled.");
            return 1;
        }
        catch (Exception exception) when (
            exception is ArgumentException or InvalidOperationException or FormatException)
        {
            await error.WriteLineAsync(exception.Message);
            return 2;
        }
        catch (Exception exception)
        {
            await error.WriteLineAsync($"Tally query failed: {exception.Message}");
            return 1;
        }
    }

    private static async Task<int> CompaniesAsync(
        Command command,
        ITallyReadDiagnostics diagnostics,
        TextWriter output,
        CancellationToken cancellationToken)
    {
        RejectOptions(command, "endpoint");
        var companies = await diagnostics.GetCompaniesAsync(cancellationToken);
        await output.WriteLineAsync($"Endpoint: {command.Endpoint}");
        await output.WriteLineAsync($"Open companies: {companies.Count}");

        foreach (var company in companies)
        {
            await output.WriteLineAsync(
                $"- {company.Name} | GUID: {company.TallyGuid ?? "MISSING"} | GSTIN: {company.Gstin ?? "-"}");
        }

        return 0;
    }

    private static async Task<int> VouchersAsync(
        Command command,
        ITallyReadDiagnostics diagnostics,
        TextWriter output,
        CancellationToken cancellationToken)
    {
        RejectOptions(command, "endpoint", "company", "from", "to");
        var company = await ResolveCompanyAsync(command, diagnostics, cancellationToken);
        var period = new DateRange(Date(command, "from"), Date(command, "to"));
        var result = await diagnostics.GetVouchersAsync(company, period, cancellationToken);
        await WriteContextAsync(output, result.Context, result.Vouchers.Count);

        foreach (var voucher in result.Vouchers)
        {
            await output.WriteLineAsync(
                $"{voucher.Date:yyyy-MM-dd} | {voucher.VoucherType} | " +
                $"{voucher.VoucherNumber ?? "-"} | MasterID {voucher.MasterId?.ToString(CultureInfo.InvariantCulture) ?? "-"}");
        }

        return 0;
    }

    private static async Task<int> VoucherAsync(
        Command command,
        ITallyReadDiagnostics diagnostics,
        TextWriter output,
        bool export,
        CancellationToken cancellationToken)
    {
        RejectOptions(command, "endpoint", "company", "date", "master-id");
        var company = await ResolveCompanyAsync(command, diagnostics, cancellationToken);
        var result = await diagnostics.GetVoucherAsync(
            company,
            Date(command, "date"),
            PositiveLong(command, "master-id"),
            cancellationToken);

        switch (result)
        {
            case VoucherLookupResult.Found found:
                if (export)
                {
                    await output.WriteLineAsync(JsonSerializer.Serialize(
                        new { context = found.Context, voucher = found.Voucher },
                        ExportJsonOptions));
                }
                else
                {
                    await WriteContextAsync(output, found.Context, 1);
                    await output.WriteLineAsync(
                        $"Found: {found.Voucher.Date:yyyy-MM-dd} | {found.Voucher.VoucherType} | " +
                        $"{found.Voucher.VoucherNumber ?? "-"} | MasterID {found.Voucher.MasterId}");
                }

                return 0;

            case VoucherLookupResult.NotFound notFound:
                await WriteContextAsync(output, notFound.Context, 0);
                await output.WriteLineAsync("Result: NotFound");
                return 3;

            case VoucherLookupResult.Conflict conflict:
                await WriteContextAsync(output, conflict.Context, conflict.Matches.Count);
                await output.WriteLineAsync("Result: Conflict");
                return 4;

            default:
                throw new InvalidOperationException("The voucher lookup returned an unknown result.");
        }
    }

    private static Task<TallyCompanyRef> ResolveCompanyAsync(
        Command command,
        ITallyReadDiagnostics diagnostics,
        CancellationToken cancellationToken) =>
        diagnostics.ResolveCompanyAsync(Required(command, "company"), cancellationToken);

    private static async Task WriteContextAsync(
        TextWriter output,
        TallyQueryContext context,
        int matches)
    {
        await output.WriteLineAsync($"Endpoint: {context.Endpoint}");
        await output.WriteLineAsync($"Company asked: {context.RequestedCompany}");
        await output.WriteLineAsync($"Company resolved: {context.ResolvedCompany}");
        await output.WriteLineAsync($"Company GUID: {context.CompanyGuid}");
        await output.WriteLineAsync($"Period: {context.Period.From:yyyy-MM-dd} -> {context.Period.To:yyyy-MM-dd}");
        await output.WriteLineAsync($"Query: {context.Query}");
        await output.WriteLineAsync($"Matches: {matches}");
    }

    private static Command Parse(string[] args)
    {
        if (args.Length == 0)
        {
            throw new ArgumentException(UsageText);
        }

        var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 1; index < args.Length; index += 2)
        {
            var option = args[index];
            if (!option.StartsWith("--", StringComparison.Ordinal) || index + 1 >= args.Length)
            {
                throw new ArgumentException($"Invalid option near '{option}'.\n{UsageText}");
            }

            if (!options.TryAdd(option[2..], args[index + 1]))
            {
                throw new ArgumentException($"Option was supplied more than once: {option}");
            }
        }

        var endpoint = options.TryGetValue("endpoint", out var value)
            ? ParseEndpoint(value)
            : Environment.GetEnvironmentVariable("NILA_TALLY_ENDPOINT") is { Length: > 0 } configured
                ? ParseEndpoint(configured)
                : DefaultEndpoint;

        return new Command(args[0].ToLowerInvariant(), endpoint, options);
    }

    private static Uri ParseEndpoint(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var endpoint) || endpoint.Scheme != "http")
        {
            throw new ArgumentException("--endpoint must be an absolute HTTP URL.");
        }

        return endpoint;
    }

    private static DateOnly Date(Command command, string name)
    {
        var value = Required(command, name);
        return DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date
            : throw new FormatException($"--{name} must use YYYY-MM-DD.");
    }

    private static long PositiveLong(Command command, string name)
    {
        var value = Required(command, name);
        return long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number) && number > 0
            ? number
            : throw new FormatException($"--{name} must be a positive integer.");
    }

    private static string Required(Command command, string name) =>
        command.Options.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new ArgumentException($"Missing required option: --{name}");

    private static void RejectOptions(Command command, params string[] allowed)
    {
        var allowedSet = allowed.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var unknown = command.Options.Keys.FirstOrDefault(option => !allowedSet.Contains(option));
        if (unknown is not null)
        {
            throw new ArgumentException($"Unknown option for {command.Name}: --{unknown}");
        }
    }

    private static int Usage(TextWriter error, string message)
    {
        error.WriteLine(message);
        error.WriteLine(UsageText);
        return 2;
    }

    private const string UsageText = """
        Read-only Tally diagnostics:
          nila-tally companies [--endpoint URL]
          nila-tally vouchers --company NAME --from YYYY-MM-DD --to YYYY-MM-DD [--endpoint URL]
          nila-tally voucher --company NAME --date YYYY-MM-DD --master-id ID [--endpoint URL]
          nila-tally export-voucher --company NAME --date YYYY-MM-DD --master-id ID [--endpoint URL]
        """;

    private sealed record Command(
        string Name,
        Uri Endpoint,
        IReadOnlyDictionary<string, string> Options);
}
