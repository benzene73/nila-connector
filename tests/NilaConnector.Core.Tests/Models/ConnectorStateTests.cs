using NilaConnector.Core.Models;

namespace NilaConnector.Core.Tests.Models;

public sealed class ConnectorStateTests
{
    [Fact]
    public void InitialStateIsDisconnectedAndHasNoCompanies()
    {
        var state = ConnectorState.Initial;

        Assert.Equal(ConnectionStatus.Disconnected, state.Nila);
        Assert.Equal(ConnectionStatus.Disconnected, state.Tally);
        Assert.Empty(state.Companies);
    }
}
