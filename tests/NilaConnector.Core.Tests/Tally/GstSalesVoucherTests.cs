using System.Text;
using System.Xml.Linq;
using NilaConnector.Core.Models;
using NilaConnector.Core.Tally;
using NilaConnector.Core.Tests.Support;

namespace NilaConnector.Core.Tests.Tally;

public sealed class GstSalesVoucherTests
{
    [Fact]
    public void RejectsInconsistentOriginalReferencesAndUnexpectedControlReferences()
    {
        Assert.Throws<ArgumentException>(() => (Note with { OriginalDate = null }).ToImportCommand("Disposable"));
        Assert.Throws<ArgumentException>(() => (Note with { OriginalDate = new(2027, 1, 1) }).ToImportCommand("Disposable"));
        var command = Note.ToImportCommand("Disposable");
        Assert.Contains("&#4; Not Applicable", command.Xml, StringComparison.Ordinal);
        Assert.Throws<InvalidDataException>(() => TallyRequestValidator.ValidateVoucherImport(
            command with { Xml = command.Xml.Replace("&#4; Not Applicable", "&#5; Other", StringComparison.Ordinal) }));
    }

    [Theory]
    [InlineData(47, "20260515")]
    [InlineData(48, "20260415")]
    public async Task ObjectInspectionRejectsWrongIdentityOrDate(int returnedId, string returnedDate)
    {
        var response = $"<ENVELOPE><HEADER><STATUS>1</STATUS></HEADER><VOUCHER><MASTERID>{returnedId}</MASTERID><DATE>{returnedDate}</DATE><VOUCHERTYPENAME>Credit Note</VOUCHERTYPENAME><VOUCHERNUMBER>CN-1</VOUCHERNUMBER></VOUCHER></ENVELOPE>";
        await using var server = new FakeTallyServer(response);
        using var client = new TallyConnectorReadClient(new TallyClientOptions { Endpoint = server.Endpoint });
        if (returnedId == 47)
            Assert.Null(await client.GetVoucherInspectionAsync("Disposable", new(2026, 4, 15), 47, default));
        else
            await Assert.ThrowsAsync<TallyClientException>(() => client.GetVoucherInspectionAsync("Disposable", new(2026, 4, 15), 47, default));
        var request = XDocument.Parse(await server.RequestBody);
        Assert.Equal("ID:47", request.Descendants("ID").Single().Value);
        Assert.Equal("Disposable", request.Descendants("SVCURRENTCOMPANY").Single().Value);
        Assert.Equal("20260415", request.Descendants("SVFROMDATE").Single().Value);
    }

    private static GstSalesVoucher Note => new("CN-1", new(2026,4,15), "Credit Note", "33BPBPK1081A1ZO",
        "Tamil Nadu", "Tamil Nadu Registration", "Cash", "Consumer", null, "Karnataka", "Service Sales",
        "998719", "Repairs", true, GstTaxability.Taxable, 2000, 18, "INV-1", new(2026,1,15), OriginalSaleCategory.Large);

    [Theory]
    [InlineData("Credit Note", 2360, -2000, -360)]
    [InlineData("Debit Note", -2360, 2000, 360)]
    public void NotesBalanceAndKeepOriginalInvoiceEvidence(string type, decimal party, decimal sale, decimal tax)
    {
        var command = (Note with { VoucherType = type }).ToImportCommand("Disposable & Co");
        TallyRequestValidator.ValidateVoucherImport(command);
        var xml = XDocument.Parse(command.Xml.Replace("&#4; Not Applicable", "Not Applicable", StringComparison.Ordinal));
        var entries = xml.Descendants("LEDGERENTRIES.LIST").ToArray();
        Assert.Equal(new[] { party, sale, tax }, entries.Select(e => (decimal)e.Element("AMOUNT")!));
        Assert.All(entries, e => Assert.True(e.Elements().ToList().FindIndex(n => n.Name == "ISDEEMEDPOSITIVE")
            < e.Elements().ToList().FindIndex(n => n.Name == "AMOUNT")));
        Assert.Equal("B2C (Large)", xml.Descendants("URDORIGINALSALEVALUE").Single().Value);
        Assert.Equal("INV-1", xml.Descendants("ORIGINVOICENUMBER").Single().Value);
        Assert.Equal("20260115", xml.Descendants("REFERENCEDATE").Single().Value);
        Assert.Equal("Disposable & Co", xml.Descendants("SVCURRENTCOMPANY").Single().Value);
    }

    [Theory]
    [InlineData(GstTaxability.NilRated, "Nil Rated")]
    [InlineData(GstTaxability.Exempt, "Exempt")]
    [InlineData(GstTaxability.NonGst, "Non-GST")]
    public void Table8RetainsClassificationAndExplicitZeroRates(GstTaxability taxability, string native)
    {
        var invoice = Note with { VoucherType = "Sales", Taxability = taxability, Rate = 0,
            OriginalNumber = null, OriginalDate = null, OriginalCategory = null };
        var xml = XDocument.Parse(invoice.ToImportCommand("Disposable").Xml.Replace("&#4; Not Applicable", "Not Applicable", StringComparison.Ordinal));
        Assert.Equal(2, xml.Descendants("LEDGERENTRIES.LIST").Count());
        Assert.Equal(native, xml.Descendants("GSTOVRDNTAXABILITY").Single().Value);
        Assert.All(xml.Descendants("GSTRATE"), rate => Assert.Equal(0m, (decimal)rate));
        Assert.Equal(5, xml.Descendants("GSTRATE" ).Count());
        Assert.Equal("As per Masters/Company", xml.Descendants("GSTHSNINFERAPPLICABILITY").Single().Value);
        Assert.Throws<ArgumentException>(() => (invoice with { Rate = 18 }).ToImportCommand("Disposable"));
    }

    [Fact]
    public async Task InspectionReadsNativeCategoryAndCorrectOriginalNumberTag()
    {
        const string xml = """
        <ENVELOPE><HEADER><STATUS>1</STATUS></HEADER><VOUCHER><DATE>20260415</DATE><MASTERID>47</MASTERID><VOUCHERTYPENAME>Credit Note</VOUCHERTYPENAME>
        <VOUCHERNUMBER>CN-1</VOUCHERNUMBER><URDORIGINALSALEVALUE>B2C (Large)</URDORIGINALSALEVALUE>
        <REFERENCE>INV-1</REFERENCE><REFERENCEDATE>20260115</REFERENCEDATE>
        <ORIGINVOICEDETAILS.LIST><ORIGINVOICENUMBER>INV-1</ORIGINVOICENUMBER><ORIGINVOICEDATE>20260115</ORIGINVOICEDATE></ORIGINVOICEDETAILS.LIST>
        </VOUCHER></ENVELOPE>
        """;
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(xml));
        var result = await TallyResponseParser.ParseVoucherInspectionAsync(stream, 10000, default);
        Assert.Equal("B2C (Large)", result!.OriginalSaleCategory);
        Assert.Equal(new DateOnly(2026,1,15), result.ReferenceDate);
        Assert.Equal("INV-1", Assert.Single(result.OriginalInvoices).Number);
    }
}
