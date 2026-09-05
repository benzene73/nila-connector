using System.Globalization;
using System.Xml.Linq;

namespace NilaConnector.Core.Models;

public enum OriginalSaleCategory { Small, Large }
public enum GstTaxability { Taxable, NilRated, Exempt, NonGst }

/// <summary>Accounting-invoice XML fields; the caller supplies the accounting classification.</summary>
public sealed record GstSalesVoucher(
    string Number, DateOnly Date, string VoucherType, string SellerGstin,
    string SellerState, string RegistrationName, string PartyLedger, string PartyName,
    string? PartyGstin, string PlaceOfSupply, string SalesLedger, string HsnSac,
    string HsnDescription, bool Services, GstTaxability Taxability,
    decimal Value, decimal Rate, string? OriginalNumber = null, DateOnly? OriginalDate = null,
    OriginalSaleCategory? OriginalCategory = null,
    string CgstLedger = "Output CGST", string SgstLedger = "Output SGST", string IgstLedger = "Output IGST")
{
    public ImportVouchersCommand ToImportCommand(string company)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(company);
        foreach (var required in new[] { Number, SellerGstin, SellerState, RegistrationName, PartyLedger,
                     PartyName, PlaceOfSupply, SalesLedger, HsnSac, CgstLedger, SgstLedger, IgstLedger })
            ArgumentException.ThrowIfNullOrWhiteSpace(required);
        if (VoucherType is not ("Sales" or "Credit Note" or "Debit Note") || Value <= 0 || Value != decimal.Round(Value, 2) || Rate < 0 || Rate > 100)
            throw new ArgumentException("Unsupported voucher type, value or rate.");
        if (!Enum.IsDefined(Taxability) || (Taxability != GstTaxability.Taxable && Rate != 0))
            throw new ArgumentException("Non-taxable supplies must have zero GST rate.");
        if ((OriginalNumber is null) != (OriginalDate is null) || (OriginalDate is not null && OriginalDate > Date))
            throw new ArgumentException("Original invoice number and date must be supplied together, no later than the note.");
        if (VoucherType == "Sales" && (OriginalNumber is not null || OriginalCategory is not null))
            throw new ArgumentException("Original-sale fields belong to notes.");
        if (OriginalCategory is not null && (!Enum.IsDefined(OriginalCategory.Value) || !string.IsNullOrEmpty(PartyGstin)))
            throw new ArgumentException("Original B2C category requires an unregistered recipient.");

        var sign = VoucherType == "Credit Note" ? -1m : 1m;
        var local = PlaceOfSupply == SellerState;
        var tax = decimal.Round(Value * Rate / 100, 2, MidpointRounding.AwayFromZero);
        var halfTax = decimal.Round(Value * Rate / 200, 2, MidpointRounding.AwayFromZero);
        var totalTax = local ? halfTax * 2 : tax;
        var date = Date.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        var voucher = new XElement("VOUCHER", new XAttribute("VCHTYPE", VoucherType),
            new XAttribute("ACTION", "Create"), new XAttribute("OBJVIEW", "Invoice Voucher View"));
        void Field(string name, object? value) { if (value is not null) voucher.Add(new XElement(name, value)); }
        Field("DATE", date); Field("EFFECTIVEDATE", date); Field("VCHSTATUSDATE", date);
        Field("VOUCHERTYPENAME", VoucherType); Field("VOUCHERNUMBER", Number);
        Field("NUMBERINGSTYLE", "Manual"); Field("PERSISTEDVIEW", "Invoice Voucher View");
        Field("VCHENTRYMODE", "Accounting Invoice"); Field("ISINVOICE", "Yes");
        Field("ISGSTOVERRIDDEN", "No"); Field("ISREVERSECHARGEAPPLICABLE", "No");
        Field("CMPGSTIN", SellerGstin); Field("CMPGSTSTATE", SellerState); Field("CMPGSTREGISTRATIONTYPE", "Regular");
        voucher.Add(new XElement("GSTREGISTRATION", new XAttribute("TAXTYPE", "GST"),
            new XAttribute("TAXREGISTRATION", SellerGstin), RegistrationName));
        Field("PARTYLEDGERNAME", PartyLedger); Field("PARTYNAME", PartyName); Field("BASICBUYERNAME", PartyName);
        Field("PARTYMAILINGNAME", PartyName); Field("CONSIGNEEMAILINGNAME", PartyName);
        Field("PARTYGSTIN", PartyGstin); Field("CONSIGNEEGSTIN", PartyGstin);
        Field("STATENAME", PlaceOfSupply); Field("PLACEOFSUPPLY", PlaceOfSupply); Field("CONSIGNEESTATENAME", PlaceOfSupply);
        Field("COUNTRYOFRESIDENCE", "India"); Field("CONSIGNEECOUNTRYNAME", "India");
        Field("GSTREGISTRATIONTYPE", string.IsNullOrEmpty(PartyGstin) ? "Unregistered/Consumer" : "Regular");
        Field("REFERENCE", OriginalNumber);
        Field("REFERENCEDATE", OriginalDate?.ToString("yyyyMMdd", CultureInfo.InvariantCulture));
        if (OriginalCategory is not null)
            Field("URDORIGINALSALEVALUE", OriginalCategory == OriginalSaleCategory.Large ? "B2C (Large)" : "B2C (Small)");
        if (OriginalNumber is not null)
        {
            Field("GSTNATUREOFRETURN", "04-Correction in Invoice");
            voucher.Add(new XElement("ORIGINVOICEDETAILS.LIST", new XElement("ORIGINVOICENUMBER", OriginalNumber),
                new XElement("ORIGINVOICEDATE", OriginalDate!.Value.ToString("yyyyMMdd", CultureInfo.InvariantCulture))));
        }
        voucher.Add(Entry(PartyLedger, -sign * (Value + totalTax), true));
        var sales = Entry(SalesLedger, sign * Value, false);
        sales.Add(new XElement("GSTSOURCETYPE", "Ledger"), new XElement("GSTLEDGERSOURCE", SalesLedger),
            new XElement("HSNSOURCETYPE", "Ledger"), new XElement("HSNLEDGERSOURCE", SalesLedger),
            new XElement("GSTOVRDNTAXABILITY", TaxabilityText),
            new XElement("GSTOVRDNTYPEOFSUPPLY", Services ? "Services" : "Goods"),
            new XElement("GSTRATEINFERAPPLICABILITY", "As per Masters/Company"),
            new XElement("GSTHSNNAME", HsnSac), new XElement("GSTHSNDESCRIPTION", HsnDescription),
            new XElement("GSTHSNINFERAPPLICABILITY", "As per Masters/Company"),
            new XElement("GSTOVERRIDDEN", "No"));
        foreach (var (head, rate) in new[] { ("CGST", Rate / 2), ("SGST/UTGST", Rate / 2), ("IGST", Rate), ("Cess", 0m), ("State Cess", 0m) })
            sales.Add(new XElement("RATEDETAILS.LIST", new XElement("GSTRATEDUTYHEAD", head),
                new XElement("GSTRATEVALUATIONTYPE", head == "Cess" ? "&#4; Not Applicable" : "Based on Value"), new XElement("GSTRATE", rate)));
        voucher.Add(sales);
        if (Rate != 0)
        {
            if (local) { voucher.Add(Entry(CgstLedger, sign * halfTax, false)); voucher.Add(Entry(SgstLedger, sign * halfTax, false)); }
            else voucher.Add(Entry(IgstLedger, sign * tax, false));
        }
        var envelope = new XElement("ENVELOPE", new XElement("HEADER", new XElement("VERSION", 1), new XElement("TALLYREQUEST", "Import"),
            new XElement("TYPE", "Data"), new XElement("ID", "Vouchers")),
            new XElement("BODY", new XElement("DESC", new XElement("STATICVARIABLES", new XElement("SVCURRENTCOMPANY", company))),
                new XElement("DATA", new XElement("TALLYMESSAGE", voucher))));
        // Tally's system-name sentinel is a numeric reference outside XML 1.0.
        return new(company, envelope.ToString(SaveOptions.DisableFormatting).Replace("&amp;#4; Not Applicable", "&#4; Not Applicable", StringComparison.Ordinal), 1);
    }

    public string TaxabilityText => Taxability switch
    { GstTaxability.Taxable => "Taxable", GstTaxability.NilRated => "Nil Rated", GstTaxability.Exempt => "Exempt", GstTaxability.NonGst => "Non-GST", _ => throw new ArgumentOutOfRangeException() };

    private static XElement Entry(string ledger, decimal amount, bool party) => new("LEDGERENTRIES.LIST",
        new XElement("LEDGERNAME", ledger),
        new XElement("ISDEEMEDPOSITIVE", amount < 0 ? "Yes" : "No"),
        new XElement("ISLASTDEEMEDPOSITIVE", amount < 0 ? "Yes" : "No"),
        new XElement("ISPARTYLEDGER", party ? "Yes" : "No"), new XElement("LEDGERFROMITEM", "No"),
        new XElement("AMOUNT", amount.ToString("0.00", CultureInfo.InvariantCulture)));
}
