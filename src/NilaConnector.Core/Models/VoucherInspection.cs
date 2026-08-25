using System.Globalization;
using System.Xml.Linq;

namespace NilaConnector.Core.Models;

public sealed record VoucherInspection(
    long MasterId,
    string? TallyGuid,
    string? RemoteId,
    DateOnly Date,
    string VoucherType,
    string VoucherNumber,
    string? PartyLedgerName,
    string? PartyName,
    string? PartyGstin,
    string? PlaceOfSupply,
    string? RegistrationType,
    string? PersistedView,
    bool? IsInvoice,
    string? Narration,
    IReadOnlyList<VoucherLedgerInspection> LedgerEntries,
    IReadOnlyList<VoucherInventoryInspection> InventoryEntries);

public sealed record VoucherLedgerInspection(
    string LedgerName,
    decimal? Amount,
    bool? IsDeemedPositive,
    bool? IsPartyLedger,
    string? GstTaxability,
    string? GstSupplyType,
    string? HsnSac,
    string? HsnDescription,
    decimal? AssessableValue,
    IReadOnlyList<VoucherGstRateInspection> Rates);

public sealed record VoucherGstRateInspection(
    string? DutyHead,
    string? ValuationType,
    decimal? Rate);

public sealed record VoucherInventoryInspection(
    string StockItemName,
    decimal? Amount,
    string? ActualQuantity,
    string? BilledQuantity,
    string? Rate,
    string? Description);

internal static class VoucherInspectionBuilder
{
    public static VoucherInspection FromVoucher(XElement voucher)
    {
        var date = Date(Required(voucher, "DATE"));
        var masterId = Long(Required(voucher, "MASTERID"), "MASTERID");

        return new VoucherInspection(
            masterId,
            Text(Direct(voucher, "GUID")),
            Text(Direct(voucher, "REMOTEID")) ?? Text(voucher.Attribute("REMOTEID")?.Value),
            date,
            Required(voucher, "VOUCHERTYPENAME"),
            Required(voucher, "VOUCHERNUMBER"),
            Text(Direct(voucher, "PARTYLEDGERNAME")),
            Text(Direct(voucher, "PARTYNAME")),
            Text(Direct(voucher, "PARTYGSTIN")),
            Text(Direct(voucher, "PLACEOFSUPPLY")),
            Text(Direct(voucher, "GSTREGISTRATIONTYPE")),
            Text(Direct(voucher, "PERSISTEDVIEW")),
            Logical(Direct(voucher, "ISINVOICE")),
            Text(Direct(voucher, "NARRATION")),
            voucher.Elements()
                .Where(element => IsList(element, "LEDGERENTRIES.LIST"))
                .Select(Ledger)
                .Where(entry => entry is not null)
                .Cast<VoucherLedgerInspection>()
                .ToArray(),
            voucher.Elements()
                .Where(element => IsList(element, "INVENTORYENTRIES.LIST"))
                .Select(Inventory)
                .Where(entry => entry is not null)
                .Cast<VoucherInventoryInspection>()
                .ToArray());
    }

    private static VoucherLedgerInspection? Ledger(XElement entry)
    {
        var name = Text(Direct(entry, "LEDGERNAME"));
        if (name is null)
        {
            return null;
        }

        return new VoucherLedgerInspection(
            name,
            Decimal(Direct(entry, "AMOUNT")),
            Logical(Direct(entry, "ISDEEMEDPOSITIVE")),
            Logical(Direct(entry, "ISPARTYLEDGER")),
            Text(Direct(entry, "GSTOVRDNTAXABILITY")),
            Text(Direct(entry, "GSTOVRDNTYPEOFSUPPLY")),
            Text(Direct(entry, "GSTHSNNAME")),
            Text(Direct(entry, "GSTHSNDESCRIPTION")),
            Decimal(Direct(entry, "GSTOVRDNASSESSABLEVALUE")),
            entry.Elements()
                .Where(element => NameIs(element, "RATEDETAILS.LIST"))
                .Select(rate => new VoucherGstRateInspection(
                    Text(Direct(rate, "GSTRATEDUTYHEAD")),
                    Text(Direct(rate, "GSTRATEVALUATIONTYPE")),
                    Decimal(Direct(rate, "GSTRATE"))))
                .ToArray());
    }

    private static VoucherInventoryInspection? Inventory(XElement entry)
    {
        var name = Text(Direct(entry, "STOCKITEMNAME"));
        if (name is null)
        {
            return null;
        }

        return new VoucherInventoryInspection(
            name,
            Decimal(Direct(entry, "AMOUNT")),
            Text(Direct(entry, "ACTUALQTY")),
            Text(Direct(entry, "BILLEDQTY")),
            Text(Direct(entry, "RATE")),
            Text(entry.Descendants()
                .FirstOrDefault(element => NameIs(element, "BASICUSERDESCRIPTION"))?.Value));
    }

    private static DateOnly Date(string value) =>
        DateOnly.TryParseExact(value, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date
            : throw new InvalidDataException("The voucher inspection has an invalid DATE value.");

    private static long Long(string value, string name) =>
        long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number) && number > 0
            ? number
            : throw new InvalidDataException($"The voucher inspection has an invalid {name} value.");

    private static decimal? Decimal(string? value) =>
        decimal.TryParse(
            value,
            NumberStyles.Number | NumberStyles.AllowLeadingSign,
            CultureInfo.InvariantCulture,
            out var number)
                ? number
                : null;

    private static bool? Logical(string? value) => Text(value)?.ToUpperInvariant() switch
    {
        "YES" or "TRUE" => true,
        "NO" or "FALSE" => false,
        _ => null,
    };

    private static bool IsList(XElement element, string suffix) =>
        element.Name.LocalName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase);

    private static bool NameIs(XElement element, string name) =>
        element.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase);

    private static string Required(XElement element, string name) =>
        Direct(element, name) is { Length: > 0 } value
            ? value
            : throw new InvalidDataException($"The voucher inspection is missing {name}.");

    private static string? Direct(XElement element, string name) =>
        element.Elements().FirstOrDefault(child => NameIs(child, name))?.Value.Trim();

    private static string? Text(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
