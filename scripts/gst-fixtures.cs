#!/usr/bin/env dotnet
#:project ../src/NilaConnector.Core/NilaConnector.Core.csproj
#:property PublishAot=false

using System.Text.Json;
using System.Text.Json.Serialization;
using System.Xml.Linq;
using NilaConnector.Core.Models;
using NilaConnector.Core.Tally;
using NilaConnector.Core.Tally.Diagnostics;

// File-based app: no additional package, solution or diagnostics write command.
if (args.Length != 5 || args[0] is not ("--verify" or "--apply-disposable"))
{
    Console.Error.WriteLine("Usage: dotnet run --file scripts/gst-fixtures.cs -- --verify|--apply-disposable COMPANY GUID CASES_JSON OUTPUT_DIRECTORY");
    return 2;
}
try
{
    var json = new JsonSerializerOptions { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };
    var cases = JsonSerializer.Deserialize<GstSalesVoucher[]>(File.ReadAllText(args[3]), json) ?? throw new InvalidDataException("Missing cases.");
    if (cases.Length == 0 || cases.Select(c => (c.Date, c.VoucherType, c.Number)).Distinct().Count() != cases.Length)
        throw new InvalidDataException("Cases must be nonempty and unique.");
    var output = Path.GetFullPath(args[4]);
    Directory.CreateDirectory(output);
    using var runLock = new FileStream(Path.Combine(output, "run.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    var options = new TallyClientOptions();
    using var read = new TallyConnectorReadClient(options);
    var diagnostics = new TallyReadDiagnostics(read, options);
    var company = await diagnostics.ResolveCompanyAsync(args[1], default);
    if (!string.Equals(company.TallyGuid, args[2], StringComparison.OrdinalIgnoreCase))
        throw new InvalidDataException("Company GUID differs; refusing the fixture run.");
    var loaded = (await diagnostics.GetCompaniesAsync(default)).Single(c => c.TallyGuid == company.TallyGuid);
    if (cases.Any(c => c.SellerGstin != loaded.Gstin))
        throw new InvalidDataException("Fixture seller GSTIN differs from the loaded company.");
    var masters = await diagnostics.GetMastersAsync(company, default);
    var required = cases.SelectMany(c => new[] { c.PartyLedger, c.SalesLedger }
        .Concat(c.Rate == 0 || !c.CollectsTax ? [] : c.Treatment == GstSalesTreatment.Domestic && c.PlaceOfSupply == c.SellerState ? [c.CgstLedger, c.SgstLedger] : [c.IgstLedger])).Distinct();
    foreach (var name in required)
        if (!masters.Masters.Any(m => m.Kind == "ledger" && m.Name == name))
            throw new InvalidDataException($"Missing prerequisite ledger: {name}. See the runbook; no masters are altered automatically.");
    var period = new DateRange(cases.Min(c => c.Date), cases.Max(c => c.Date));
    var observed = await diagnostics.GetVouchersAsync(company, period, default);
    var pending = new List<GstSalesVoucher>();
    foreach (var item in cases)
    {
        _ = item.ToImportCommand(company.Name); // Validate the complete plan before any write.
        var matches = observed.Vouchers.Where(v => v.VoucherNumber == item.Number && v.VoucherType == item.VoucherType).ToArray();
        if (matches.Length > 1 || matches.Any(v => v.Date != item.Date)) throw new InvalidDataException($"Conflicting identity: {item.Number}");
        if (matches.Length == 0)
        {
            if (File.Exists(Journal(item))) throw new InvalidDataException($"An earlier attempt for {item.Number} has an unknown outcome. Inspect Tally; do not retry blindly.");
            pending.Add(item);
        }
        else await Verify(item, matches[0].MasterId!.Value);
    }
    if (pending.Count > 0 && args[0] == "--verify") throw new InvalidDataException($"Missing {pending.Count} fixtures; verify never writes.");
    using var writer = new TallyClient(new HttpClient(new ImportCapture(output)), options);
    foreach (var item in pending)
    {
        // Resolve again immediately before each write, detecting company replacement.
        var current = await diagnostics.ResolveCompanyAsync(company.Name, default);
        if (current.TallyGuid != company.TallyGuid) throw new InvalidDataException("Company changed during fixture run.");
        var command = item.ToImportCommand(company.Name);
        File.WriteAllText(Journal(item), JsonSerializer.Serialize(new { company, item, state = "attempted; outcome unknown" }, json));
        var imported = await writer.ImportVouchersAsync(command, default);
        if (imported.Created != 1 || imported.Altered != 0 || imported.Errors != 0 || imported.Exceptions != 0)
            throw new InvalidDataException($"Unexpected import outcome for {item.Number}; inspect before retrying.");
        await Verify(item, imported.LastVoucherId);
    }
    Console.WriteLine($"Verified {cases.Length}; created {pending.Count}; unchanged {cases.Length - pending.Count}. Native GSTR-1/PDF verification is a separate runbook step.");
    return 0;

    string Journal(GstSalesVoucher item) => Path.Combine(output, Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
        System.Text.Encoding.UTF8.GetBytes($"{company.TallyGuid}|{item.Date:yyyy-MM-dd}|{item.VoucherType}|{item.Number}"))) + ".json");

    async Task Verify(GstSalesVoucher item, long id)
    {
        var found = await diagnostics.GetVoucherInspectionAsync(company, item.Date, id, default);
        if (found is not VoucherInspectionLookupResult.Found hit) throw new InvalidDataException($"Readback missing: {item.Number}");
        var actual = hit.Inspection;
        var expected = XDocument.Parse(item.ToImportCommand(company.Name).Xml.Replace("&#4; Not Applicable", "Not Applicable", StringComparison.Ordinal).Replace("&#4; Applicable", "Applicable", StringComparison.Ordinal).Replace("&#4; System Inferred", "System Inferred", StringComparison.Ordinal).Replace("&#4; Unknown", "Unknown", StringComparison.Ordinal)).Descendants("VOUCHER").Single();
        var amounts = expected.Elements("LEDGERENTRIES.LIST").ToDictionary(e => e.Element("LEDGERNAME")!.Value,
            e => decimal.Parse(e.Element("AMOUNT")!.Value, System.Globalization.CultureInfo.InvariantCulture));
        var sale = actual.LedgerEntries.SingleOrDefault(e => e.LedgerName == item.SalesLedger);
        var rates = new Dictionary<string, decimal> { ["CGST"] = item.Rate / 2, ["SGST/UTGST"] = item.Rate / 2,
            ["IGST"] = item.Rate, ["Cess"] = 0, ["State Cess"] = 0 };
        var category = item.OriginalCategory is null ? null : item.OriginalCategory == OriginalSaleCategory.Large ? "B2C (Large)" : "B2C (Small)";
        if (actual.VoucherNumber != item.Number || actual.VoucherType != item.VoucherType || actual.PlaceOfSupply != item.PlaceOfSupply
            || actual.ReverseCharge != (item.Treatment == GstSalesTreatment.ReverseCharge)
            || (item.IsSez && actual.RegistrationType != "Regular - SEZ")
            || (item.IsExport && actual.Country != item.Country)
            || actual.PartyGstin != item.PartyGstin || actual.PartyLedgerName != item.PartyLedger
            || actual.LedgerEntries.Count != amounts.Count || actual.LedgerEntries.Any(e => !amounts.TryGetValue(e.LedgerName, out var value) || e.Amount != value)
            || sale?.GstTaxability != item.TaxabilityText || sale.HsnSac != item.HsnSac
            || sale.GstSupplyType != (item.Services ? "Services" : "Goods")
            || rates.Any(rate => !sale.Rates.Any(r => r.DutyHead == rate.Key && r.Rate == rate.Value))
            || (category is not null && actual.OriginalSaleCategory != category)
            || (item.OriginalNumber is not null && (actual.Reference != item.OriginalNumber || actual.ReferenceDate != item.OriginalDate
                || !actual.OriginalInvoices.Any(i => i.Number == item.OriginalNumber && i.Date == item.OriginalDate))))
            throw new InvalidDataException($"Fixture differs from intended accounting: {item.Number}. No alteration attempted.");
        File.WriteAllText(Journal(item), JsonSerializer.Serialize(new { company, state = "readback verified", voucher = actual }, json));
        Console.WriteLine($"Verified {item.Number} / {id}");
    }
}

catch (Exception error)
{
    Console.Error.WriteLine(error.Message);
    return 1;
}


sealed class ImportCapture(string output) : DelegatingHandler(new HttpClientHandler())
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var key = DateTime.UtcNow.ToString("yyyyMMddTHHmmssfffffff", System.Globalization.CultureInfo.InvariantCulture);
        File.WriteAllText(Path.Combine(output, key + "-request.xml"), await request.Content!.ReadAsStringAsync(cancellationToken));
        var response = await base.SendAsync(request, cancellationToken);
        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        File.WriteAllBytes(Path.Combine(output, key + "-response.xml"), bytes);
        response.Content = new ByteArrayContent(bytes);
        return response;
    }
}
