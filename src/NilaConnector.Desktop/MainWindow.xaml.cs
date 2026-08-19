using System.ComponentModel;
using System.Windows;
using Microsoft.Extensions.Logging;
using NilaConnector.Core.Nila;
using NilaConnector.Desktop.ViewModels;

namespace NilaConnector.Desktop;

public partial class MainWindow : Window
{
    private readonly MainWindowViewModel _viewModel;
    private readonly MachineLinker _machineLinker;
    private readonly ILogger<MainWindow> _logger;

    public MainWindow(
        MainWindowViewModel viewModel,
        MachineLinker machineLinker,
        ILogger<MainWindow> logger)
    {
        InitializeComponent();
        _viewModel = viewModel;
        _machineLinker = machineLinker;
        _logger = logger;
        DataContext = viewModel;
    }

    public event EventHandler? HideRequested;

    public bool AllowClose { get; set; }

    private async void LinkNila_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_machineLinker.IsLinked)
            {
                _viewModel.BeginUnlinking();
                await _machineLinker.UnlinkAsync(CancellationToken.None);
                _viewModel.CompleteUnlinking();
            }
            else
            {
                _viewModel.BeginLinking();
                _ = await _machineLinker.LinkAsync(Environment.MachineName, CancellationToken.None);
                _viewModel.CompleteLinking();
            }
        }
        catch (Exception exception)
        {
            LogLinkActionFailed(_logger, exception.GetType().Name);
            _viewModel.FailLinking();
        }
    }

    private async void SyncNow_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: CompanyItemViewModel company })
        {
            return;
        }

        try
        {
            await _viewModel.SyncAsync(company, CancellationToken.None);
        }
        catch (Exception exception)
        {
            LogSyncFailed(_logger, company.Name, exception.GetType().Name);
        }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!AllowClose)
        {
            e.Cancel = true;
            HideRequested?.Invoke(this, EventArgs.Empty);
        }

        base.OnClosing(e);
    }

    [LoggerMessage(
        EventId = 4100,
        Level = LogLevel.Warning,
        Message = "Nila link action failed ({ExceptionType})")]
    private static partial void LogLinkActionFailed(ILogger logger, string exceptionType);

    [LoggerMessage(
        EventId = 4101,
        Level = LogLevel.Warning,
        Message = "Company sync failed for {Company} ({ExceptionType})")]
    private static partial void LogSyncFailed(ILogger logger, string company, string exceptionType);
}
