using System.Text;
using System.Xml.Linq;
using NilaConnector.Core.Models;
using NilaConnector.Core.Tally;

namespace NilaConnector.Core.Tests.Tally;

public sealed class VoucherFingerprintTests
{
    [Fact]
    public void MasterIdRequestUsesTheWorkingSingularTdlFilter()
    {
        var xml = TallyRequests.VoucherFingerprint("Caltech", new DateOnly(2026, 6, 25), 42);
        var document = XDocument.Parse(xml);

        Assert.Contains(
            document.Descendants(),
            element => element.Name.LocalName == "FILTER" &&
                       element.Value == "NilaMasterIdMatch");
        Assert.DoesNotContain(
            document.Descendants(),
            element => element.Name.LocalName == "FILTERS");
        Assert.DoesNotContain("AllLedgerEntries.*", xml, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("$MasterID = 42", xml, StringComparison.Ordinal);
        Assert.Equal(
            ["20260625"],
            document.Descendants("SVFROMDATE").Select(element => element.Value));
        Assert.Equal(
            ["Date"],
            document.Descendants("SVFROMDATE").Select(element => element.Attribute("TYPE")?.Value));
        Assert.Equal(
            ["20260625"],
            document.Descendants("SVTODATE").Select(element => element.Value));
        Assert.Equal(
            ["Date"],
            document.Descendants("SVTODATE").Select(element => element.Attribute("TYPE")?.Value));
    }

    [Fact]
    public async Task ParserIgnoresCmpInfoVoucherCountAndReadsTheMaterialVoucher()
    {
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(ResponseXml));

        var fingerprint = await TallyResponseParser.ParseVoucherFingerprintAsync(
            stream,
            100_000,
            CancellationToken.None);

        Assert.NotNull(fingerprint);
        Assert.Equal(new DateOnly(2026, 6, 25), fingerprint.Date);
        Assert.Equal("Sales", fingerprint.VoucherType);
        Assert.Equal("38/2026-27", fingerprint.VoucherNumber);
        Assert.Equal(64, fingerprint.CanonicalHash.Length);
    }

    [Fact]
    public void FingerprintChangesWhenAMaterialLedgerAmountChanges()
    {
        var original = XElement.Parse(VoucherXml);
        var changed = XElement.Parse(VoucherXml.Replace("-118.00", "-119.00", StringComparison.Ordinal));

        var originalFingerprint = VoucherSemanticFingerprintBuilder.FromVoucher(original);
        var changedFingerprint = VoucherSemanticFingerprintBuilder.FromVoucher(changed);

        Assert.NotEqual(originalFingerprint.CanonicalHash, changedFingerprint.CanonicalHash);
    }

    [Fact]
    public void FingerprintIgnoresEmptyListContainersAddedByTally()
    {
        var intended = XElement.Parse(VoucherXml);
        var readBack = XElement.Parse(VoucherXml);
        readBack.Add(new XElement("ALLINVENTORYENTRIES.LIST"));
        readBack.Add(new XElement("ALLLEDGERENTRIES.LIST"));

        var intendedFingerprint = VoucherSemanticFingerprintBuilder.FromVoucher(intended);
        var readBackFingerprint = VoucherSemanticFingerprintBuilder.FromVoucher(readBack);

        Assert.Equal(intendedFingerprint.CanonicalHash, readBackFingerprint.CanonicalHash);
    }

    private const string VoucherXml = """
        <VOUCHER>
          <DATE>20260625</DATE>
          <VOUCHERTYPENAME>Sales</VOUCHERTYPENAME>
          <VOUCHERNUMBER>38/2026-27</VOUCHERNUMBER>
          <PARTYLEDGERNAME>Siemens Healthcare Private Limited</PARTYLEDGERNAME>
          <PARTYNAME>Siemens Healthcare Private Limited</PARTYNAME>
          <PARTYGSTIN>29AAACI0995H1ZQ</PARTYGSTIN>
          <PLACEOFSUPPLY>Karnataka</PLACEOFSUPPLY>
          <GSTREGISTRATIONTYPE>Regular</GSTREGISTRATIONTYPE>
          <PERSISTEDVIEW>Invoice Voucher View</PERSISTEDVIEW>
          <ISINVOICE>Yes</ISINVOICE>
          <NARRATION>Imported from Nila</NARRATION>
          <LEDGERENTRIES.LIST>
            <LEDGERNAME>Siemens Healthcare Private Limited</LEDGERNAME>
            <ISDEEMEDPOSITIVE>Yes</ISDEEMEDPOSITIVE>
            <ISPARTYLEDGER>Yes</ISPARTYLEDGER>
            <AMOUNT>-118.00</AMOUNT>
            <BILLALLOCATIONS.LIST>
              <NAME>38/2026-27</NAME>
              <BILLTYPE>New Ref</BILLTYPE>
              <AMOUNT>-118.00</AMOUNT>
            </BILLALLOCATIONS.LIST>
          </LEDGERENTRIES.LIST>
          <LEDGERENTRIES.LIST>
            <LEDGERNAME>Sales</LEDGERNAME>
            <ISDEEMEDPOSITIVE>No</ISDEEMEDPOSITIVE>
            <ISPARTYLEDGER>No</ISPARTYLEDGER>
            <GSTOVRDNTAXABILITY>Taxable</GSTOVRDNTAXABILITY>
            <GSTOVRDNTYPEOFSUPPLY>Goods</GSTOVRDNTYPEOFSUPPLY>
            <GSTHSNNAME>9022</GSTHSNNAME>
            <GSTHSNDESCRIPTION>Medical equipment</GSTHSNDESCRIPTION>
            <GSTOVRDNASSESSABLEVALUE>100.00</GSTOVRDNASSESSABLEVALUE>
            <RATEDETAILS.LIST><GSTRATEDUTYHEAD>IGST</GSTRATEDUTYHEAD><GSTRATEVALUATIONTYPE>Based on Value</GSTRATEVALUATIONTYPE><GSTRATE>18</GSTRATE></RATEDETAILS.LIST>
            <AMOUNT>100.00</AMOUNT>
          </LEDGERENTRIES.LIST>
          <LEDGERENTRIES.LIST>
            <LEDGERNAME>Output IGST</LEDGERNAME>
            <ISDEEMEDPOSITIVE>No</ISDEEMEDPOSITIVE>
            <AMOUNT>18.00</AMOUNT>
          </LEDGERENTRIES.LIST>
        </VOUCHER>
        """;

    private const string ResponseXml = """
        <ENVELOPE>
          <HEADER><VERSION>1</VERSION><STATUS>1</STATUS></HEADER>
          <BODY>
            <DESC><CMPINFO><VOUCHER>13</VOUCHER></CMPINFO></DESC>
            <DATA><COLLECTION>
              <VOUCHER REMOTEID="tally-generated" VCHTYPE="Sales">
                <MASTERID>42</MASTERID>
                <DATE>20260625</DATE>
                <VOUCHERTYPENAME>Sales</VOUCHERTYPENAME>
                <VOUCHERNUMBER>38/2026-27</VOUCHERNUMBER>
                <PARTYLEDGERNAME>Siemens Healthcare Private Limited</PARTYLEDGERNAME>
                <PARTYNAME>Siemens Healthcare Private Limited</PARTYNAME>
                <PARTYGSTIN>29AAACI0995H1ZQ</PARTYGSTIN>
                <PLACEOFSUPPLY>Karnataka</PLACEOFSUPPLY>
                <GSTREGISTRATIONTYPE>Regular</GSTREGISTRATIONTYPE>
                <PERSISTEDVIEW>Invoice Voucher View</PERSISTEDVIEW>
                <ISINVOICE>Yes</ISINVOICE>
                <NARRATION>Imported from Nila</NARRATION>
                <LEDGERENTRIES.LIST><LEDGERNAME>Siemens Healthcare Private Limited</LEDGERNAME><ISDEEMEDPOSITIVE>Yes</ISDEEMEDPOSITIVE><ISPARTYLEDGER>Yes</ISPARTYLEDGER><AMOUNT>-118.00</AMOUNT><GSTCLASS>&#4; Not Applicable</GSTCLASS><UDF:USERDESCRIPTION.LIST><UDF:USERDESCRIPTION>ignored</UDF:USERDESCRIPTION></UDF:USERDESCRIPTION.LIST></LEDGERENTRIES.LIST>
              </VOUCHER>
            </COLLECTION></DATA>
          </BODY>
        </ENVELOPE>
        """;
}
