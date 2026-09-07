#!/usr/bin/env dotnet
#:project ../src/NilaConnector.Core/NilaConnector.Core.csproj
#:property PublishAot=false

using System.Text.Json;
using System.Text.Json.Serialization;
using NilaConnector.Core.Models;
using NilaConnector.Core.Tally;
using NilaConnector.Core.Tally.Diagnostics;

// Provision only uniquely named fixture ledgers. Never alter existing masters.
if (args.Length != 5 || args[0] != "--apply-disposable")
    throw new ArgumentException("--apply-disposable COMPANY GUID CASES_JSON OUTPUT_DIRECTORY");
var json = new JsonSerializerOptions { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };
var cases = JsonSerializer.Deserialize<GstSalesVoucher[]>(File.ReadAllText(args[3]), json)!;
var output = Path.GetFullPath(args[4]);
Directory.CreateDirectory(output);
using var runLock = new FileStream(Path.Combine(output, "masters.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
var options = new TallyClientOptions();
using var client = new TallyConnectorReadClient(options);
var diagnostics = new TallyReadDiagnostics(client, options);
var company = await diagnostics.ResolveCompanyAsync(args[1], default);
if (company.TallyGuid != args[2]) throw new InvalidDataException("Company GUID differs.");
var loaded = (await diagnostics.GetCompaniesAsync(default)).Single(c => c.TallyGuid == company.TallyGuid);
if (cases.Length == 0 || cases.Any(c => c.SellerGstin != loaded.Gstin)) throw new InvalidDataException("Seller GSTIN differs.");
var plans = cases.SelectMany(c => new[]
{
    new CreateLedgerCommand(company.Name, c.PartyLedger, "Sundry Debtors", c.PartyGstin ?? "", c.IsSez ? "Regular - SEZ" : c.IsExport ? "Unregistered" : "Regular", c.PlaceOfSupply, c.Country, c.Date),
    new CreateLedgerCommand(company.Name, c.SalesLedger, "Sales Accounts", "", "Unregistered", c.SellerState, "India", c.Date,
        new GstSalesLedgerSettings(c.HsnSac, c.HsnDescription, c.Services, c.Rate, c.Treatment))
}).Distinct().ToArray();
if (plans.Any(p => !p.Name.StartsWith("SDK3-", StringComparison.Ordinal)) || plans.GroupBy(p => p.Name).Any(g => g.Count() != 1))
    throw new InvalidDataException("Fixtures require unique SDK3- master names and consistent definitions.");
foreach (var plan in plans)
{
    var current = await diagnostics.ResolveCompanyAsync(company.Name, default);
    if (current.TallyGuid != company.TallyGuid) throw new InvalidDataException("Company changed.");
    var masters = await diagnostics.GetMastersAsync(company, default);
    var existing = masters.Masters.SingleOrDefault(m => m.Kind == "ledger" && m.Name == plan.Name);
    var journal = Path.Combine(output, Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(plan.Name))) + ".master.json");
    if (existing is not null)
    {
        if (!File.Exists(journal)) throw new InvalidDataException($"Existing master without our journal: {plan.Name}");
        var saved = JsonSerializer.Deserialize<CreateLedgerCommand>(File.ReadAllText(journal), json);
        if (saved != plan) throw new InvalidDataException($"Master plan changed: {plan.Name}");
        Console.WriteLine($"Existing {plan.Name}; inspect readback before using.");
        continue;
    }
    if (File.Exists(journal)) throw new InvalidDataException($"Unresolved prior master attempt: {plan.Name}");
    File.WriteAllText(journal, JsonSerializer.Serialize(plan, json));
    var result = await client.CreateLedgerAsync(plan, default);
    Console.WriteLine($"Created {result.Name} / {result.MasterId}");
}
File.WriteAllText(Path.Combine(output, "masters-readback.json"), JsonSerializer.Serialize(await diagnostics.GetMastersAsync(company, default), json));
