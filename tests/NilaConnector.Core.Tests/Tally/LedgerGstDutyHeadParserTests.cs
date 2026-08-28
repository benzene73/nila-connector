using System.Text;
using NilaConnector.Core.Tally;

namespace NilaConnector.Core.Tests.Tally;

public sealed class LedgerGstDutyHeadParserTests
{
    [Fact]
    public void RequestsTheStructuredLedgerDutyHead()
    {
        var xml = TallyRequests.LedgerGstDutyHeads("Breezetech Manual");

        Assert.Contains("<SVCURRENTCOMPANY>Breezetech Manual</SVCURRENTCOMPANY>", xml);
        Assert.Contains("<NATIVEMETHOD>GSTDutyHead</NATIVEMETHOD>", xml);
    }

    [Fact]
    public async Task ReadsOnlyLedgersWithADeclaredGstDutyHead()
    {
        const string xml = """
            <ENVELOPE>
              <HEADER><STATUS>1</STATUS></HEADER>
              <BODY><DATA><COLLECTION>
                <LEDGER NAME="Output IGST"><GSTDUTYHEAD>Integrated Tax</GSTDUTYHEAD></LEDGER>
                <LEDGER><NAME>Output CGST</NAME><GSTDUTYHEAD>Central Tax</GSTDUTYHEAD></LEDGER>
                <LEDGER NAME="Round Off"><GSTDUTYHEAD></GSTDUTYHEAD></LEDGER>
              </COLLECTION></DATA></BODY>
            </ENVELOPE>
            """;
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(xml));

        var dutyHeads = await TallyResponseParser.ParseLedgerGstDutyHeadsAsync(
            stream,
            1024 * 1024,
            CancellationToken.None);

        Assert.Equal("Integrated Tax", dutyHeads["Output IGST"]);
        Assert.Equal("Central Tax", dutyHeads["Output CGST"]);
        Assert.DoesNotContain("Round Off", dutyHeads);
    }
}
