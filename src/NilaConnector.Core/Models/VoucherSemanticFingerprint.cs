using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;

namespace NilaConnector.Core.Models;

public sealed record VoucherSemanticFingerprint(
    DateOnly Date,
    string VoucherType,
    string VoucherNumber,
    string CanonicalHash);

internal static class VoucherSemanticFingerprintBuilder
{
    public static VoucherSemanticFingerprint FromVoucher(XElement voucher)
    {
        var dateText = RequiredDirectValue(voucher, "DATE");
        if (!DateOnly.TryParseExact(
                dateText,
                "yyyyMMdd",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var date))
        {
            throw new InvalidDataException("The voucher fingerprint has an invalid DATE value.");
        }

        var voucherType = RequiredDirectValue(voucher, "VOUCHERTYPENAME");
        var voucherNumber = RequiredDirectValue(voucher, "VOUCHERNUMBER");

        var material = new SortedDictionary<string, object?>(StringComparer.Ordinal)
        {
            ["date"] = date.ToString("yyyyMMdd", CultureInfo.InvariantCulture),
            ["voucher_type"] = Text(voucherType),
            ["voucher_number"] = Text(voucherNumber),
            ["party_ledger_name"] = Text(DirectValue(voucher, "PARTYLEDGERNAME")),
            ["party_name"] = Text(DirectValue(voucher, "PARTYNAME")),
            ["party_gstin"] = Text(DirectValue(voucher, "PARTYGSTIN")),
            ["place_of_supply"] = Text(DirectValue(voucher, "PLACEOFSUPPLY")),
            ["registration_type"] = Text(DirectValue(voucher, "GSTREGISTRATIONTYPE")),
            ["persisted_view"] = Text(DirectValue(voucher, "PERSISTEDVIEW")),
            ["is_invoice"] = Boolean(DirectValue(voucher, "ISINVOICE")),
            ["narration"] = Text(DirectValue(voucher, "NARRATION")),
            ["ledger_entries"] = voucher.Elements()
                .Where(element =>
                    IsList(element, "LEDGERENTRIES.LIST") &&
                    DirectValue(element, "LEDGERNAME") is { Length: > 0 })
                .Select(LedgerEntry)
                .Order(StringComparer.Ordinal)
                .ToArray(),
            ["inventory_entries"] = voucher.Elements()
                .Where(element =>
                    IsList(element, "INVENTORYENTRIES.LIST") &&
                    DirectValue(element, "STOCKITEMNAME") is { Length: > 0 })
                .Select(InventoryEntry)
                .Order(StringComparer.Ordinal)
                .ToArray(),
        };

        var canonicalJson = JsonSerializer.Serialize(material);
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalJson)));
        return new VoucherSemanticFingerprint(date, voucherType.Trim(), voucherNumber.Trim(), hash);
    }

    private static string LedgerEntry(XElement entry)
    {
        var material = new SortedDictionary<string, object?>(StringComparer.Ordinal)
        {
            ["ledger_name"] = Text(DirectValue(entry, "LEDGERNAME")),
            ["amount"] = Number(DirectValue(entry, "AMOUNT")),
            ["is_deemed_positive"] = Boolean(DirectValue(entry, "ISDEEMEDPOSITIVE")),
            ["is_party_ledger"] = Boolean(DirectValue(entry, "ISPARTYLEDGER")) ?? "NO",
            ["gst_taxability"] = Text(DirectValue(entry, "GSTOVRDNTAXABILITY")),
            ["gst_supply_type"] = Text(DirectValue(entry, "GSTOVRDNTYPEOFSUPPLY")),
            ["hsn_sac"] = Text(DirectValue(entry, "GSTHSNNAME")),
            ["description"] = Text(DirectValue(entry, "GSTHSNDESCRIPTION")),
            ["assessable_value"] = Number(DirectValue(entry, "GSTOVRDNASSESSABLEVALUE")),
            ["rates"] = entry.Elements()
                .Where(element => NameIs(element, "RATEDETAILS.LIST"))
                .Select(RateDetail)
                .Where(value => value is not null)
                .Cast<string>()
                .Order(StringComparer.Ordinal)
                .ToArray(),
        };

        return JsonSerializer.Serialize(material);
    }

    private static string InventoryEntry(XElement entry)
    {
        var material = new SortedDictionary<string, object?>(StringComparer.Ordinal)
        {
            ["stock_item_name"] = Text(DirectValue(entry, "STOCKITEMNAME")),
            ["amount"] = Number(DirectValue(entry, "AMOUNT")),
            ["actual_quantity"] = Text(DirectValue(entry, "ACTUALQTY")),
            ["billed_quantity"] = Text(DirectValue(entry, "BILLEDQTY")),
            ["rate"] = Text(DirectValue(entry, "RATE")),
            ["description"] = Text(entry.Descendants()
                .FirstOrDefault(element => NameIs(element, "BASICUSERDESCRIPTION"))?.Value),
        };

        return JsonSerializer.Serialize(material);
    }

    private static string? RateDetail(XElement entry)
    {
        var rate = Number(DirectValue(entry, "GSTRATE"));
        if (rate is null or "0")
        {
            return null;
        }

        return JsonSerializer.Serialize(new SortedDictionary<string, object?>(StringComparer.Ordinal)
        {
            ["head"] = Text(DirectValue(entry, "GSTRATEDUTYHEAD")),
            ["valuation"] = Text(DirectValue(entry, "GSTRATEVALUATIONTYPE")),
            ["rate"] = rate,
        });
    }

    private static bool IsList(XElement element, string suffix) =>
        element.Name.LocalName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase);

    private static bool NameIs(XElement element, string name) =>
        element.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase);

    private static string RequiredDirectValue(XElement element, string name) =>
        DirectValue(element, name) is { Length: > 0 } value
            ? value
            : throw new InvalidDataException($"The voucher fingerprint is missing {name}.");

    private static string? DirectValue(XElement element, string name) =>
        element.Elements().FirstOrDefault(child => NameIs(child, name))?.Value.Trim();

    private static string? Text(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim().ToUpperInvariant();

    private static string? Boolean(string? value) => Text(value) switch
    {
        "YES" or "TRUE" => "YES",
        "NO" or "FALSE" => "NO",
        _ => null,
    };

    private static string? Number(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return decimal.TryParse(
            value,
            NumberStyles.Number | NumberStyles.AllowLeadingSign,
            CultureInfo.InvariantCulture,
            out var number)
                ? number.ToString("0.############################", CultureInfo.InvariantCulture)
                : Text(value);
    }
}
