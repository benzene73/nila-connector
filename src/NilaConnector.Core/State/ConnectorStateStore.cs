using NilaConnector.Core.Models;

namespace NilaConnector.Core.State;

public sealed class ConnectorStateStore
{
    private readonly object _gate = new();
    private ConnectorState _current = ConnectorState.Initial;

    public event EventHandler<ConnectorState>? Changed;

    public ConnectorState Current
    {
        get
        {
            lock (_gate)
            {
                return _current;
            }
        }
    }

    public void SetTally(
        ConnectionStatus status,
        IReadOnlyList<TallyCompany> companies,
        string? error = null)
    {
        ConnectorState next;

        lock (_gate)
        {
            var companySnapshot = companies.ToArray();

            if (_current.Tally == status &&
                string.Equals(_current.TallyError, error, StringComparison.Ordinal) &&
                _current.Companies.SequenceEqual(companySnapshot))
            {
                return;
            }

            next = _current with
            {
                Tally = status,
                Companies = companySnapshot,
                TallyError = error,
            };

            _current = next;
        }

        Changed?.Invoke(this, next);
    }

    public void SetNila(ConnectionStatus status, string? error = null)
    {
        ConnectorState next;

        lock (_gate)
        {
            if (_current.Nila == status &&
                string.Equals(_current.NilaError, error, StringComparison.Ordinal))
            {
                return;
            }

            next = _current with
            {
                Nila = status,
                NilaError = error,
            };

            _current = next;
        }

        Changed?.Invoke(this, next);
    }
}
