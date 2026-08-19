using System.Text;
using System.Text.Json;
using NilaConnector.Core.Nila;

namespace NilaConnector.Core.Tests.Nila;

public sealed class PhoenixProtocolTests
{
    [Fact]
    public void EncodesThePhoenixTransportAuthSubprotocol()
    {
        var protocol = PhoenixProtocol.AuthSubprotocol("device-token");

        Assert.StartsWith("base64url.bearer.phx.", protocol, StringComparison.Ordinal);
        var encoded = protocol["base64url.bearer.phx.".Length..];
        var padded = encoded.PadRight(encoded.Length + ((4 - (encoded.Length % 4)) % 4), '=');
        Assert.Equal("device-token", Encoding.UTF8.GetString(Convert.FromBase64String(padded)));
    }

    [Fact]
    public void EncodesADeviceJoinInPhoenixV2WireFormat()
    {
        var json = PhoenixProtocol.Join("1", "tally_device:42", "0.1.0");
        using var document = JsonDocument.Parse(json);
        var message = document.RootElement;

        Assert.Equal("1", message[0].GetString());
        Assert.Equal("1", message[1].GetString());
        Assert.Equal("tally_device:42", message[2].GetString());
        Assert.Equal("phx_join", message[3].GetString());
        Assert.Equal("0.1.0", message[4].GetProperty("connector_version").GetString());
    }

    [Fact]
    public void RecognizesASuccessfulJoinReply()
    {
        var message = PhoenixProtocol.Decode(
            """["1","1","tally_device:42","phx_reply",{"status":"ok","response":{"device_id":42}}]"""u8);

        Assert.True(message.IsSuccessfulReplyTo("1"));
    }

    [Fact]
    public void EncodesJobResultsWithTheActiveChannelJoinReference()
    {
        var result = new ConnectorJobResult(
            42,
            "failed",
            new Dictionary<string, object?>
            {
                ["kind"] = "ApplicationError",
                ["message"] = "Tally rejected the voucher.",
            });

        var json = PhoenixProtocol.JobResult("1", "result-42", "tally_device:7", result);
        using var document = JsonDocument.Parse(json);
        var message = document.RootElement;

        Assert.Equal("1", message[0].GetString());
        Assert.Equal("result-42", message[1].GetString());
        Assert.Equal("job_result", message[3].GetString());
        Assert.Equal(42, message[4].GetProperty("job_id").GetInt32());
        Assert.Equal("ApplicationError", message[4].GetProperty("details").GetProperty("kind").GetString());
    }
}
