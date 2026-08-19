using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using NilaConnector.Core.Models;
using NilaConnector.Core.Nila;
using NilaConnector.Core.State;

namespace NilaConnector.Desktop.ViewModels;

public sealed class MainWindowViewModel : INotifyPropertyChanged
{
    private ConnectorState _state;
    private readonly IDeviceCredentialStore _credentialStore;
    private readonly SyncCoordinator _syncCoordinator;
    private readonly Dictionary<string, CompanyItemViewModel> _companyItems = new(StringComparer.OrdinalIgnoreCase);
    private bool _linking;
    private string? _pairingMessage;

    public MainWindowViewModel(
        ConnectorStateStore stateStore,
        IDeviceCredentialStore credentialStore,
        SyncCoordinator syncCoordinator)
    {
        _state = stateStore.Current;
        _credentialStore = credentialStore;
        _syncCoordinator = syncCoordinator;
        RebuildCompanies(_state.Companies);
        stateStore.Changed += (_, state) => Dispatch(() => Update(state));
        credentialStore.Changed += (_, _) => Dispatch(() => OnPropertyChanged(string.Empty));
        syncCoordinator.ProgressChanged += (_, progress) => Dispatch(() => UpdateProgress(progress));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string NilaStatus => Label(_state.Nila);

    public string TallyStatus => Label(_state.Tally);

    public string NilaColor => Color(_state.Nila);

    public string TallyColor => Color(_state.Tally);

    public IReadOnlyList<CompanyItemViewModel> Companies => _companyItems.Values
        .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
        .ToArray();

    public bool HasCompanies => _state.Companies.Count > 0;

    public string? TallyError => _state.TallyError;

    public string? NilaError => _state.NilaError;

    public bool CanLink => !_linking;

    public string LinkButtonText => _credentialStore.Load() is not null
        ? "Unlink"
        : _linking
            ? "Linking…"
            : "Link to Nila";

    public string? PairingMessage => _pairingMessage;

    public string CompanySummary => _state.Companies.Count == 0
        ? _state.Tally == ConnectionStatus.Connected
            ? "Tally is connected, but no company is open"
            : "Start TallyPrime to discover open companies"
        : $"{_state.Companies.Count} open {(_state.Companies.Count == 1 ? "company" : "companies")}";

    private void Update(ConnectorState state)
    {
        _state = state;
        RebuildCompanies(state.Companies);
        OnPropertyChanged(string.Empty);
    }

    private void RebuildCompanies(IReadOnlyList<TallyCompany> companies)
    {
        var identities = companies.Select(Identity).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var stale in _companyItems.Keys.Where(key => !identities.Contains(key)).ToArray())
        {
            _companyItems.Remove(stale);
        }

        foreach (var company in companies)
        {
            var identity = Identity(company);
            if (!_companyItems.ContainsKey(identity))
            {
                _companyItems[identity] = new CompanyItemViewModel(company);
            }
        }
    }

    private void UpdateProgress(CompanySyncProgress progress)
    {
        if (_companyItems.TryGetValue(Identity(progress.Company), out var item))
        {
            item.Update(progress);
        }
    }

    public Task SyncAsync(CompanyItemViewModel item, CancellationToken cancellationToken) =>
        _syncCoordinator.SyncAsync(item.Company, cancellationToken);

    private static string Identity(TallyCompany company) =>
        company.TallyGuid ?? company.Name;

    public void BeginLinking()
    {
        _linking = true;
        _pairingMessage = "Approve this computer in the browser window that opens.";
        OnPropertyChanged(string.Empty);
    }

    public void CompleteLinking()
    {
        _linking = false;
        _pairingMessage = "This computer is securely linked to Nila.";
        OnPropertyChanged(string.Empty);
    }

    public void FailLinking()
    {
        _linking = false;
        _pairingMessage = "Nila could not link this computer. Try again.";
        OnPropertyChanged(string.Empty);
    }

    public void BeginUnlinking()
    {
        _linking = true;
        _pairingMessage = "Removing this computer's Nila access…";
        OnPropertyChanged(string.Empty);
    }

    public void CompleteUnlinking()
    {
        _linking = false;
        _pairingMessage = "This computer is no longer linked to Nila.";
        OnPropertyChanged(string.Empty);
    }

    private static void Dispatch(Action action)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            action();
            return;
        }

        _ = dispatcher.BeginInvoke(action);
    }

    private static string Label(ConnectionStatus status) => status switch
    {
        ConnectionStatus.Connected => "Connected",
        ConnectionStatus.Connecting => "Connecting",
        ConnectionStatus.Error => "Needs attention",
        _ => "Not connected",
    };

    private static string Color(ConnectionStatus status) => status switch
    {
        ConnectionStatus.Connected => "#1F9D68",
        ConnectionStatus.Connecting => "#D28B16",
        ConnectionStatus.Error => "#C2413B",
        _ => "#929292",
    };

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

public sealed class CompanyItemViewModel : INotifyPropertyChanged
{
    private string _status = "Ready";
    private string _masters = "Not synced";
    private string _vouchers = "Not synced";
    private string? _error;
    private bool _isSyncing;

    public CompanyItemViewModel(TallyCompany company) => Company = company;

    public event PropertyChangedEventHandler? PropertyChanged;
    public TallyCompany Company { get; }
    public string Name => Company.Name;
    public string Status => _status;
    public string Masters => _masters;
    public string Vouchers => _vouchers;
    public string? Error => _error;
    public bool CanSync => !_isSyncing;
    public string SyncButtonText => _isSyncing ? "Syncing…" : "Sync now";

    public void Update(CompanySyncProgress progress)
    {
        _status = progress.Status;
        _masters = progress.Masters;
        _vouchers = progress.Vouchers;
        _error = progress.Error;
        _isSyncing = progress.Status is "Starting" or "Syncing";
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
    }
}
