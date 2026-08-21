using System.Text;
using NilaConnector.Core.Tally;

namespace NilaConnector.Core.Tests.Tally;

public sealed class VoucherNumberingParserTests
{
    [Fact]
    public async Task ReadsTheEffectiveTallyPrime7NumberingSeriesInsteadOfTheLegacyField()
    {
        const string xml = """
            <ENVELOPE>
              <HEADER><STATUS>1</STATUS></HEADER>
              <BODY><DATA><COLLECTION>
                <VOUCHERTYPE NAME="Sales">
                  <NUMBERINGMETHOD>None</NUMBERINGMETHOD>
                  <VOUCHERNUMBERSERIES.LIST>
                    <NAME>Default</NAME>
                    <NUMBERINGMETHOD>Automatic (Manual Override)</NUMBERINGMETHOD>
                  </VOUCHERNUMBERSERIES.LIST>
                </VOUCHERTYPE>
              </COLLECTION></DATA></BODY>
            </ENVELOPE>
            """;
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(xml));

        var methods = await TallyResponseParser.ParseVoucherNumberingMethodsAsync(
            stream,
            1024 * 1024,
            CancellationToken.None);

        Assert.Equal("Automatic (Manual Override)", methods["Sales"]);
    }

    [Fact]
    public async Task ReportsMixedSeriesConservatively()
    {
        const string xml = """
            <ENVELOPE><HEADER><STATUS>1</STATUS></HEADER><BODY><DATA><COLLECTION>
              <VOUCHERTYPE NAME="Sales">
                <VOUCHERNUMBERSERIES.LIST><NUMBERINGMETHOD>Manual</NUMBERINGMETHOD></VOUCHERNUMBERSERIES.LIST>
                <VOUCHERNUMBERSERIES.LIST><NUMBERINGMETHOD>Automatic</NUMBERINGMETHOD></VOUCHERNUMBERSERIES.LIST>
              </VOUCHERTYPE>
            </COLLECTION></DATA></BODY></ENVELOPE>
            """;
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(xml));

        var methods = await TallyResponseParser.ParseVoucherNumberingMethodsAsync(
            stream,
            1024 * 1024,
            CancellationToken.None);

        Assert.Equal("Mixed: Manual, Automatic", methods["Sales"]);
    }
}
