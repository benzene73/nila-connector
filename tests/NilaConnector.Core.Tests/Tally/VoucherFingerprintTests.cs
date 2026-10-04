using System.Text;
using System.Xml.Linq;
using NilaConnector.Core.Models;
using NilaConnector.Core.Tally;

namespace NilaConnector.Core.Tests.Tally;

public sealed class VoucherFingerprintTests
{
    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("Not Applicable", false)]
    [InlineData("System Inferred", true)]
    [InlineData("Local Sales - Taxable", true)]
    [InlineData("Interstate Sales - Taxable", true)]
    public void TaxableEntriesNeedPersistedGstClassification(string? nature, bool expected)
    {
        var voucher = XElement.Parse(VoucherXml);
        var entry = voucher.Elements("LEDGERENTRIES.LIST")
            .Single(element => element.Element("GSTOVRDNTAXABILITY")?.Value == "Taxable");
        if (nature is not null) entry.Add(new XElement("GSTOVRDNSTOREDNATURE", nature));
        Assert.Equal(expected, VoucherSemanticFingerprintBuilder.FromVoucher(voucher).HasStoredGstNature);
    }

    [Fact]
    public void ReadsGstReportingStatusWithoutChangingMaterialHash()
    {
        var source = XElement.Parse(VoucherXml);
        var before = VoucherSemanticFingerprintBuilder.FromVoucher(source);
        source.Add(new XElement("VCHGSTSTATUSISINCLUDED", "Yes"),
            new XElement("VCHGSTSTATUSISUNCERTAIN", "No"));
        var after = VoucherSemanticFingerprintBuilder.FromVoucher(source);
        Assert.True(after.GstIncluded);
        Assert.False(after.GstUncertain);
        Assert.Equal(before.CanonicalHash, after.CanonicalHash);
        Assert.Null(before.GstIncluded);
        Assert.Null(before.GstUncertain);
    }

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

    [Fact]
    public void FingerprintIgnoresDescriptionWhitespaceDiscardedByTally()
    {
        var intended = XElement.Parse(VoucherXml.Replace(
            "Medical equipment",
            "Medical equipment\nContract period: 2026",
            StringComparison.Ordinal));
        var readBack = XElement.Parse(VoucherXml.Replace(
            "Medical equipment",
            "Medical equipmentContract period: 2026",
            StringComparison.Ordinal));

        var intendedFingerprint = VoucherSemanticFingerprintBuilder.FromVoucher(intended);
        var readBackFingerprint = VoucherSemanticFingerprintBuilder.FromVoucher(readBack);

        Assert.Equal(intendedFingerprint.CanonicalHash, readBackFingerprint.CanonicalHash);
    }

    [Fact]
    public void FingerprintStillDetectsMaterialDescriptionChanges()
    {
        var original = XElement.Parse(VoucherXml);
        var changed = XElement.Parse(VoucherXml.Replace(
            "Medical equipment",
            "Different service",
            StringComparison.Ordinal));

        var originalFingerprint = VoucherSemanticFingerprintBuilder.FromVoucher(original);
        var changedFingerprint = VoucherSemanticFingerprintBuilder.FromVoucher(changed);

        Assert.NotEqual(originalFingerprint.CanonicalHash, changedFingerprint.CanonicalHash);
    }

    [Fact]
    public void FingerprintTreatsDestinationAssignedVoucherNumberAsIdentityNotMaterial()
    {
        var withoutNumber = XElement.Parse(VoucherXml);
        withoutNumber.Element("VOUCHERNUMBER")!.Remove();
        var expected = VoucherSemanticFingerprintBuilder.FromVoucher(
            withoutNumber,
            requireVoucherNumber: false);
        var actual = VoucherSemanticFingerprintBuilder.FromVoucher(XElement.Parse(VoucherXml));

        Assert.Null(expected.VoucherNumber);
        Assert.Equal("38/2026-27", actual.VoucherNumber);
        Assert.Equal(expected.CanonicalHash, actual.CanonicalHash);
    }

    [Fact]
    public void FingerprintDetectsMaterialBankAllocationChanges()
    {
        var original = XElement.Parse(BankVoucherXml);
        var changed = XElement.Parse(BankVoucherXml.Replace(
            "REF-1",
            "REF-2",
            StringComparison.Ordinal));

        var originalFingerprint = VoucherSemanticFingerprintBuilder.FromVoucher(
            original,
            requireVoucherNumber: false);
        var changedFingerprint = VoucherSemanticFingerprintBuilder.FromVoucher(
            changed,
            requireVoucherNumber: false);

        Assert.NotEqual(originalFingerprint.CanonicalHash, changedFingerprint.CanonicalHash);
    }

    [Fact]
    public void FingerprintIgnoresPartyDefaultsTallyDerivesForAccountingVouchers()
    {
        var intended = XElement.Parse(BankVoucherXml);
        var readBack = XElement.Parse(BankVoucherXml);
        readBack.AddFirst(
            new XElement("PARTYLEDGERNAME", "Cash"),
            new XElement("GSTREGISTRATIONTYPE", "Regular"));
        var counterEntry = readBack.Elements("ALLLEDGERENTRIES.LIST").First();
        counterEntry.Element("ISPARTYLEDGER")!.Value = "Yes";
        counterEntry.Add(new XElement("BANKALLOCATIONS.LIST"));

        var intendedFingerprint = VoucherSemanticFingerprintBuilder.FromVoucher(
            intended,
            requireVoucherNumber: false);
        var readBackFingerprint = VoucherSemanticFingerprintBuilder.FromVoucher(
            readBack,
            requireVoucherNumber: false);

        Assert.Equal(intendedFingerprint.CanonicalHash, readBackFingerprint.CanonicalHash);
    }

    [Fact]
    public void InspectionPreservesEmptyCessValuationRecords()
    {
        var voucher = XElement.Parse(VoucherXml);
        voucher.AddFirst(new XElement("MASTERID", "42"));
        var sales = voucher.Elements("LEDGERENTRIES.LIST")
            .Single(entry => entry.Element("LEDGERNAME")?.Value == "Sales");
        sales.Add(new XElement("RATEDETAILS.LIST",
            new XElement("GSTRATEDUTYHEAD", "Cess")));

        var inspection = VoucherInspectionBuilder.FromVoucher(voucher);

        var cess = inspection.LedgerEntries
            .Single(entry => entry.LedgerName == "Sales")
            .Rates.Single(rate => rate.DutyHead == "Cess");
        Assert.Null(cess.ValuationType);
        Assert.Null(cess.Rate);
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
                <STATUS>No</STATUS>
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

    private const string BankVoucherXml = """
        <VOUCHER REMOTEID="nila-bank-1" VCHTYPE="Payment">
          <DATE>20260601</DATE>
          <VOUCHERTYPENAME>Payment</VOUCHERTYPENAME>
          <PARTYLEDGERNAME>HDFC Bank</PARTYLEDGERNAME>
          <PERSISTEDVIEW>Accounting Voucher View</PERSISTEDVIEW>
          <ISINVOICE>No</ISINVOICE>
          <ALLLEDGERENTRIES.LIST>
            <LEDGERNAME>Bank Charges</LEDGERNAME>
            <ISDEEMEDPOSITIVE>Yes</ISDEEMEDPOSITIVE>
            <ISPARTYLEDGER>No</ISPARTYLEDGER>
            <AMOUNT>-413.00</AMOUNT>
          </ALLLEDGERENTRIES.LIST>
          <ALLLEDGERENTRIES.LIST>
            <LEDGERNAME>HDFC Bank</LEDGERNAME>
            <ISDEEMEDPOSITIVE>No</ISDEEMEDPOSITIVE>
            <ISPARTYLEDGER>Yes</ISPARTYLEDGER>
            <AMOUNT>413.00</AMOUNT>
            <BANKALLOCATIONS.LIST>
              <DATE>20260601</DATE>
              <INSTRUMENTDATE>20260601</INSTRUMENTDATE>
              <TRANSACTIONTYPE>Cheque</TRANSACTIONTYPE>
              <PAYMENTFAVOURING>Bank Charges</PAYMENTFAVOURING>
              <INSTRUMENTNUMBER>REF-1</INSTRUMENTNUMBER>
              <PAYMENTMODE>Transacted</PAYMENTMODE>
              <BANKPARTYNAME>Bank Charges</BANKPARTYNAME>
              <AMOUNT>413.00</AMOUNT>
            </BANKALLOCATIONS.LIST>
          </ALLLEDGERENTRIES.LIST>
        </VOUCHER>
        """;
}
