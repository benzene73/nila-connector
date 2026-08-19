using System.Globalization;
using System.Xml.Linq;
using NilaConnector.Core.Models;

namespace NilaConnector.Core.Tally;

internal static class TallyRequests
{
    public const string OpenCompanies = """
        <ENVELOPE>
          <HEADER>
            <VERSION>1</VERSION>
            <TALLYREQUEST>Export</TALLYREQUEST>
            <TYPE>Collection</TYPE>
            <ID>Nila Open Companies</ID>
          </HEADER>
          <BODY>
            <DESC>
              <STATICVARIABLES>
                <SVEXPORTFORMAT>$$SysName:XML</SVEXPORTFORMAT>
              </STATICVARIABLES>
              <TDL>
                <TDLMESSAGE>
                  <COLLECTION NAME="Nila Open Companies" ISINITIALIZE="Yes">
                    <TYPE>Company</TYPE>
                    <NATIVEMETHOD>Name</NATIVEMETHOD>
                    <NATIVEMETHOD>GUID</NATIVEMETHOD>
                  </COLLECTION>
                </TDLMESSAGE>
              </TDL>
            </DESC>
          </BODY>
        </ENVELOPE>
        """;

    public static string Ledgers(string company) => MasterCollection(
        company,
        "Nila Ledgers",
        "Ledger",
        "Name", "GUID", "AlterID", "Parent", "IsBillWiseOn");

    public static string VoucherTypes(string company) => MasterCollection(
        company,
        "Nila Voucher Types",
        "VoucherType",
        "Name", "GUID", "AlterID", "Parent", "NumberingMethod", "IsActive");

    public static string StockItems(string company) => MasterCollection(
        company,
        "Nila Stock Items",
        "StockItem",
        "Name", "GUID", "AlterID", "Parent", "BaseUnits");

    public static string Vouchers(string company, DateOnly from, DateOnly to)
    {
        var collection = new XElement("COLLECTION",
            new XAttribute("NAME", "Nila Vouchers"),
            new XAttribute("ISINITIALIZE", "Yes"),
            new XElement("TYPE", "Voucher"),
            NativeMethods(
                "Date",
                "VoucherTypeName",
                "VoucherNumber",
                "MasterID",
                "GUID",
                "RemoteID",
                "Narration",
                "AlterID",
                "AllLedgerEntries.*"));

        return ExportCollection(company, from, to, collection);
    }

    public static string CreateVoucher(CreateVoucherCommand command)
    {
        var deterministicId = $"nila-job-{command.JobKey}";
        var amount = command.Amount.ToString("0.00", CultureInfo.InvariantCulture);
        var date = command.Date.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        XNamespace udf = "TallyUDF";

        var document = new XDocument(
            new XElement("ENVELOPE",
                new XElement("HEADER",
                    new XElement("VERSION", "1"),
                    new XElement("TALLYREQUEST", "Import"),
                    new XElement("TYPE", "Data"),
                    new XElement("ID", "Vouchers")),
                new XElement("BODY",
                    new XElement("DESC",
                        new XElement("STATICVARIABLES",
                            new XElement("SVCURRENTCOMPANY", command.Company),
                            new XElement("SVOVERWRITEIMPVCH", "Yes"))),
                    new XElement("DATA",
                        new XElement("TALLYMESSAGE",
                            new XAttribute(XNamespace.Xmlns + "UDF", udf),
                            new XElement("VOUCHER",
                                new XAttribute("REMOTEID", deterministicId),
                                new XAttribute("VCHTYPE", command.VoucherType),
                                new XAttribute("ACTION", "Create"),
                                new XAttribute("OBJVIEW", "Accounting Voucher View"),
                                new XElement("DATE", date),
                                new XElement("GUID", deterministicId),
                                new XElement("VOUCHERTYPENAME", command.VoucherType),
                                new XElement("NARRATION", command.Narration),
                                new XElement("EFFECTIVEDATE", date),
                                new XElement("PERSISTEDVIEW", "Accounting Voucher View"),
                                new XElement("ISINVOICE", "No"),
                                LedgerEntry(
                                    command.DebitLedger,
                                    isDeemedPositive: true,
                                    isPartyLedger: true,
                                    amount: $"-{amount}",
                                    billName: $"NILA-{command.JobKey[..8]}"),
                                LedgerEntry(
                                    command.CreditLedger,
                                    isDeemedPositive: false,
                                    isPartyLedger: false,
                                    amount: amount,
                                    billName: null)))))));

        return document.ToString(SaveOptions.DisableFormatting);
    }

    private static XElement LedgerEntry(
        string ledgerName,
        bool isDeemedPositive,
        bool isPartyLedger,
        string amount,
        string? billName)
    {
        var entry = new XElement("ALLLEDGERENTRIES.LIST",
            new XElement("LEDGERNAME", ledgerName),
            new XElement("ISDEEMEDPOSITIVE", isDeemedPositive ? "Yes" : "No"),
            new XElement("ISPARTYLEDGER", isPartyLedger ? "Yes" : "No"),
            new XElement("AMOUNT", amount));

        if (billName is not null)
        {
            entry.Add(new XElement("BILLALLOCATIONS.LIST",
                new XElement("NAME", billName),
                new XElement("BILLTYPE", "On Account"),
                new XElement("AMOUNT", amount)));
        }

        return entry;
    }

    private static IEnumerable<XElement> NativeMethods(params string[] methods) =>
        methods.Select(method => new XElement("NATIVEMETHOD", method));

    private static string MasterCollection(
        string company,
        string collectionName,
        string type,
        params string[] methods) =>
        ExportCollection(
            company,
            null,
            null,
            new XElement("COLLECTION",
                new XAttribute("NAME", collectionName),
                new XAttribute("ISINITIALIZE", "Yes"),
                new XElement("TYPE", type),
                NativeMethods(methods)));

    private static string ExportCollection(
        string company,
        DateOnly? from,
        DateOnly? to,
        params XElement[] collections)
    {
        var variables = new XElement("STATICVARIABLES",
            new XElement("SVEXPORTFORMAT", "$$SysName:XML"),
            new XElement("SVCURRENTCOMPANY", company));

        if (from is not null && to is not null)
        {
            variables.Add(
                new XElement("SVFROMDATE", from.Value.ToString("yyyyMMdd", CultureInfo.InvariantCulture)),
                new XElement("SVTODATE", to.Value.ToString("yyyyMMdd", CultureInfo.InvariantCulture)));
        }

        var document = new XDocument(
            new XElement("ENVELOPE",
                new XElement("HEADER",
                    new XElement("VERSION", "1"),
                    new XElement("TALLYREQUEST", "Export"),
                    new XElement("TYPE", "Collection"),
                    new XElement("ID", collections[0].Attribute("NAME")!.Value)),
                new XElement("BODY",
                    new XElement("DESC",
                        variables,
                        new XElement("TDL",
                            new XElement("TDLMESSAGE", collections))))));

        return document.ToString(SaveOptions.DisableFormatting);
    }
}
