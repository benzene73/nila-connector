using NilaConnector.Core.Models;
using NilaConnector.Core.Tally;
using NilaConnector.Core.Tests.Support;
using System.Reflection;
using System.Xml.Serialization;
using TallyConnector.Core.Models;
using TallyConnector.Core.Models.Response;
using TallyConnector.Models.Common;
using TallyConnector.Models.TallyPrime.V7.Masters;
using TallyConnector.Services;

namespace NilaConnector.Core.Tests.Tally;

public sealed class TallyConnectorLedgerClientTests
{
    [Fact]
    public void MapsTheTypedGstDutyHeadIntoTheSyncContract()
    {
        var master = TallyConnectorReadClient.MapLedger(new Ledger
        {
            Name = "Output IGST",
            TaxType = TaxType.GST,
            GSTDutyHead = GSTTaxType.IGST,
        });

        Assert.Equal("GST", master.Data["tax_type"]);
        Assert.Equal("IGST", master.Data["gst_duty_head"]);
    }

    [Fact]
    public async Task PostsTheTypedLedgerThroughTheSdkInTheExplicitCompany()
    {
        const string response = """
            <RESULTS>
              <RESULT>
                <OBJECTTYPE>Ledger</OBJECTTYPE>
                <NAME>Customer &amp; Co</NAME>
                <MASTERID>42</MASTERID>
                <GUID>tally-guid</GUID>
                <REMOTEID>remote-id</REMOTEID>
                <ERROR></ERROR>
              </RESULT>
            </RESULTS>
            """;
        await using var server = new FakeTallyServer(response);
        using var client = new TallyConnectorReadClient(new TallyClientOptions { Endpoint = server.Endpoint });
        SetLicenseInfo(client, server.Endpoint);

        var result = await client.CreateLedgerAsync(Command(), CancellationToken.None);
        var request = await server.RequestBody;

        Assert.Equal(42, result.MasterId);
        Assert.Contains("<SVCURRENTCOMPANY>Nila &amp; Co</SVCURRENTCOMPANY>", request, StringComparison.Ordinal);
        Assert.Contains("<LEDGER", request, StringComparison.Ordinal);
        Assert.Contains("ACTION=\"Create\"", request, StringComparison.Ordinal);
        Assert.Contains("<LEDMAILINGDETAILS.LIST>", request, StringComparison.Ordinal);
        Assert.Contains("<LEDGSTREGDETAILS.LIST>", request, StringComparison.Ordinal);
        var applicableDates = System.Text.RegularExpressions.Regex.Matches(
                request,
                "<APPLICABLEFROM>([^<]+)</APPLICABLEFROM>")
            .Select(match => match.Groups[1].Value)
            .ToArray();
        Assert.Equal(["15-07-2026", "15-07-2026"], applicableDates);
        Assert.Contains("<GSTIN>29AAVCS8021P1ZB</GSTIN>", request, StringComparison.Ordinal);
        Assert.Contains("<PLACEOFSUPPLY>Karnataka</PLACEOFSUPPLY>", request, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PostsSalesMasterSystemValuesAndGstRatesThroughTheSdk()
    {
        const string response = "<RESULTS><RESULT><OBJECTTYPE>Ledger</OBJECTTYPE><NAME>Service Sales</NAME><MASTERID>43</MASTERID><GUID>sales-guid</GUID></RESULT></RESULTS>";
        await using var server = new FakeTallyServer(response);
        using var client = new TallyConnectorReadClient(new TallyClientOptions { Endpoint = server.Endpoint });
        SetLicenseInfo(client, server.Endpoint);
        var command = Command() with
        {
            Name = "Service Sales", Parent = "Sales Accounts", Gstin = "",
            SalesGst = new GstSalesLedgerSettings("998719", "Repairs", true, 18, GstSalesTreatment.SezWithPayment)
        };

        var result = await client.CreateLedgerAsync(command, CancellationToken.None);
        var request = await server.RequestBody;

        Assert.Equal(43, result.MasterId);
        Assert.Contains("<SVCURRENTCOMPANY>Nila &amp; Co</SVCURRENTCOMPANY>", request);
        Assert.Contains("<GSTAPPLICABLE>&#4; Applicable</GSTAPPLICABLE>", request);
        Assert.Contains("<STATENAME>&#4; Any</STATENAME>", request);
        Assert.Contains("<GSTRATEVALUATIONTYPE>&#4; Not Applicable</GSTRATEVALUATIONTYPE>", request);
        Assert.DoesNotContain("&#x4;", request);
        Assert.Contains("<GSTRATE>18</GSTRATE>", request);
        Assert.Contains("<HSNCODE>998719</HSNCODE>", request);
        Assert.Contains("<GSTNATUREOFTRANSACTION>Sales to SEZ - Taxable</GSTNATUREOFTRANSACTION>", request);
    }

    [Fact]
    public void MapsSezRecipientRegistration()
    {
        var ledger = TallyConnectorReadClient.BuildLedger(Command() with { RegistrationType = "Regular - SEZ" });
        Assert.Equal("RegularSEZ", Assert.Single(ledger.GSTRegistrationDetails!).GSTRegistrationType.ToString());
    }

    [Fact]
    public void MapsRegisteredPartyLedgerToTheTypedSdkModel()
    {
        var command = Command();

        var ledger = TallyConnectorReadClient.BuildLedger(command);
        var mailing = Assert.Single(ledger.MailingDetails!);
        var registration = Assert.Single(ledger.GSTRegistrationDetails!);

        Assert.Equal(command.Name, ledger.Name);
        Assert.Equal(command.Parent, ledger.Group);
        Assert.True(ledger.IsBillWise);
        Assert.Equal(new DateTime(2026, 7, 15), mailing.ApplicableFrom);
        Assert.Equal(command.Name, mailing.MailingName);
        Assert.Equal(command.Country, mailing.Country);
        Assert.Equal(command.State, mailing.State);
        Assert.Equal(new DateTime(2026, 7, 15), registration.ApplicableFrom);
        Assert.Equal(command.Gstin, registration.GSTIN);
        Assert.Equal(command.State, registration.State);
        Assert.Equal(command.State, registration.PlaceOfSupply);
        Assert.Equal("Regular", registration.GSTRegistrationType.ToString());

        var dto = ledger.ToDTO();
        using var writer = new StringWriter();
        new XmlSerializer(dto.GetType()).Serialize(writer, dto);
        var xml = writer.ToString();
        Assert.Contains("<LEDMAILINGDETAILS.LIST>", xml, StringComparison.Ordinal);
        Assert.Contains("<LEDGSTREGDETAILS.LIST>", xml, StringComparison.Ordinal);
        Assert.Contains("<GSTIN>29AAVCS8021P1ZB</GSTIN>", xml, StringComparison.Ordinal);
        Assert.Contains("<PLACEOFSUPPLY>Karnataka</PLACEOFSUPPLY>", xml, StringComparison.Ordinal);
    }

    [Fact]
    public void MapsOneSuccessfulSdkResponseToAMasterResult()
    {
        var result = TallyConnectorReadClient.ParseLedgerResponse(
            Command(),
            [
                new PostResponse
                {
                    ObjectType = "Ledger",
                    Name = "Customer & Co",
                    MasterId = 42,
                    GUID = "tally-guid",
                    RemoteId = "remote-id",
                },
            ]);

        Assert.Equal(new TallyMasterWriteResult("Ledger", "Customer & Co", 42, "tally-guid", "remote-id"), result);
    }

    [Fact]
    public void RejectsAnSdkApplicationError()
    {
        var exception = Assert.Throws<TallyClientException>(() =>
            TallyConnectorReadClient.ParseLedgerResponse(
                Command(),
                [
                    new PostResponse
                    {
                        ObjectType = "Ledger",
                        Name = "Customer & Co",
                        RemoteId = "remote-id",
                        Error = "Invalid GST registration",
                    },
                ]));

        Assert.Equal(TallyErrorKind.ApplicationError, exception.Kind);
    }

    [Fact]
    public void RejectsAnUnidentifiedSdkResponse()
    {
        var exception = Assert.Throws<TallyClientException>(() =>
            TallyConnectorReadClient.ParseLedgerResponse(Command(), []));

        Assert.Equal(TallyErrorKind.InvalidResponse, exception.Kind);
    }

    private static CreateLedgerCommand Command() => new(
        "Nila & Co",
        "Customer & Co",
        "Sundry Debtors",
        "29AAVCS8021P1ZB",
        "Regular",
        "Karnataka",
        "India",
        new DateOnly(2026, 7, 15));

    private static void SetLicenseInfo(TallyConnectorReadClient client, Uri endpoint)
    {
        var serviceField = typeof(TallyConnectorReadClient).GetField(
            "_baseService",
            BindingFlags.Instance | BindingFlags.NonPublic);
        var service = Assert.IsType<BaseTallyService>(serviceField!.GetValue(client));
        service.Setup($"{endpoint.Scheme}://{endpoint.Host}", endpoint.Port);
        var licenseProperty = typeof(BaseTallyService).GetProperty(
            "_licenseInfo",
            BindingFlags.Instance | BindingFlags.NonPublic);

        licenseProperty!.SetValue(
            service,
            new LicenseInfo { TallyShortVersion = new ShortVersion(7, 1) });
    }
}
