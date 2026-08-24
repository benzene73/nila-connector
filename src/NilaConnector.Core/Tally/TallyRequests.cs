using System.Globalization;
using System.Xml.Linq;
using NilaConnector.Core.Models;

namespace NilaConnector.Core.Tally;

internal static class TallyRequests
{
    public static string CreateLedger(CreateLedgerCommand command)
    {
        var document = new XDocument(
            new XElement("ENVELOPE",
                new XElement("HEADER",
                    new XElement("VERSION", "1"),
                    new XElement("TALLYREQUEST", "Import"),
                    new XElement("TYPE", "Data"),
                    new XElement("ID", "All Masters")),
                new XElement("BODY",
                    new XElement("DESC",
                        new XElement("STATICVARIABLES",
                            new XElement("SVCURRENTCOMPANY", command.Company))),
                    new XElement("DATA",
                        new XElement("TALLYMESSAGE",
                            new XElement("LEDGER",
                                new XAttribute("NAME", command.Name),
                                new XAttribute("ACTION", "Create"),
                                new XElement("NAME", command.Name),
                                new XElement("PARENT", command.Parent),
                                new XElement("ISBILLWISEON", "Yes"),
                                new XElement("AFFECTSSTOCK", "No"),
                                new XElement("COUNTRYNAME", command.Country),
                                new XElement("STATENAME", command.State),
                                new XElement("GSTREGISTRATIONTYPE", command.RegistrationType),
                                new XElement("PARTYGSTIN", command.Gstin)))))));

        return document.ToString(SaveOptions.DisableFormatting);
    }

    public static string VoucherNumberingMethods(string company)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(company);

        return new XDocument(
            new XElement("ENVELOPE",
                new XElement("HEADER",
                    new XElement("VERSION", "1"),
                    new XElement("TALLYREQUEST", "Export"),
                    new XElement("TYPE", "Collection"),
                    new XElement("ID", "Nila Voucher Numbering")),
                new XElement("BODY",
                    new XElement("DESC",
                        new XElement("STATICVARIABLES",
                            new XElement("SVEXPORTFORMAT", "$$SysName:XML"),
                            new XElement("SVCURRENTCOMPANY", company)),
                        new XElement("TDL",
                            new XElement("TDLMESSAGE",
                                new XElement("COLLECTION",
                                    new XAttribute("NAME", "Nila Voucher Numbering"),
                                    new XAttribute("ISINITIALIZE", "Yes"),
                                    new XElement("TYPE", "VoucherType"),
                                    new XElement("NATIVEMETHOD", "Name"),
                                    new XElement("NATIVEMETHOD", "VoucherNumberSeries.*"))))))))
            .ToString(SaveOptions.DisableFormatting);
    }

    public static string VoucherFingerprint(string company, DateOnly voucherDate, long masterId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(company);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(masterId);

        return new XDocument(
            new XElement("ENVELOPE",
                new XElement("HEADER",
                    new XElement("VERSION", "1"),
                    new XElement("TALLYREQUEST", "Export"),
                    new XElement("TYPE", "Collection"),
                    new XElement("ID", "Nila Voucher Fingerprint")),
                new XElement("BODY",
                    new XElement("DESC",
                        new XElement("STATICVARIABLES",
                            new XElement("SVEXPORTFORMAT", "$$SysName:XML"),
                            new XElement("SVCURRENTCOMPANY", company),
                            new XElement(
                                "SVFROMDATE",
                                new XAttribute("TYPE", "Date"),
                                voucherDate.ToString("yyyyMMdd", CultureInfo.InvariantCulture)),
                            new XElement(
                                "SVTODATE",
                                new XAttribute("TYPE", "Date"),
                                voucherDate.ToString("yyyyMMdd", CultureInfo.InvariantCulture))),
                        new XElement("TDL",
                            new XElement("TDLMESSAGE",
                                new XElement("COLLECTION",
                                    new XAttribute("NAME", "Nila Voucher Fingerprint"),
                                    new XAttribute("ISINITIALIZE", "Yes"),
                                    new XElement("TYPE", "Voucher"),
                                    new XElement("FILTER", "NilaMasterIdMatch"),
                                    new XElement("NATIVEMETHOD", "Date"),
                                    new XElement("NATIVEMETHOD", "VoucherTypeName"),
                                    new XElement("NATIVEMETHOD", "VoucherNumber"),
                                    new XElement("NATIVEMETHOD", "MasterID"),
                                    new XElement("NATIVEMETHOD", "GUID"),
                                    new XElement("NATIVEMETHOD", "RemoteID"),
                                    new XElement("NATIVEMETHOD", "PartyLedgerName"),
                                    new XElement("NATIVEMETHOD", "PartyName"),
                                    new XElement("NATIVEMETHOD", "PartyGSTIN"),
                                    new XElement("NATIVEMETHOD", "PlaceOfSupply"),
                                    new XElement("NATIVEMETHOD", "GSTRegistrationType"),
                                    new XElement("NATIVEMETHOD", "PersistedView"),
                                    new XElement("NATIVEMETHOD", "IsInvoice"),
                                    new XElement("NATIVEMETHOD", "Narration"),
                                    new XElement("NATIVEMETHOD", "LedgerEntries.*"),
                                    new XElement("NATIVEMETHOD", "InventoryEntries.*")),
                                new XElement("SYSTEM",
                                    new XAttribute("TYPE", "Formulae"),
                                    new XAttribute("NAME", "NilaMasterIdMatch"),
                                    $"$MasterID = {masterId.ToString(CultureInfo.InvariantCulture)}")))))))
            .ToString(SaveOptions.DisableFormatting);
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

}
