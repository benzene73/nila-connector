using NilaConnector.Core.Tally;
using NilaConnector.Core.Tally.Diagnostics;
using NilaConnector.Diagnostics;

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    cancellation.Cancel();
};

var exitCode = await DiagnosticsCli.RunAsync(
    args,
    endpoint =>
    {
        var options = new TallyClientOptions { Endpoint = endpoint };
        return new TallyReadDiagnostics(new TallyConnectorReadClient(options), options);
    },
    Console.Out,
    Console.Error,
    cancellation.Token);

return exitCode;
