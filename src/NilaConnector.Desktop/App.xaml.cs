using System.Drawing;
using System.IO;
using System.Net.Http;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NilaConnector.Core.Nila;
using NilaConnector.Core.State;
using NilaConnector.Core.Tally;
using NilaConnector.Desktop.Logging;
using NilaConnector.Desktop.Security;
using NilaConnector.Desktop.Services;
using NilaConnector.Desktop.ViewModels;
using Forms = System.Windows.Forms;

namespace NilaConnector.Desktop;

public partial class App : System.Windows.Application
{
    private IHost? _host;
    private Forms.NotifyIcon? _trayIcon;
    private MainWindow? _mainWindow;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        try
        {
            _host = BuildHost();
            await _host.StartAsync();

            _mainWindow = _host.Services.GetRequiredService<MainWindow>();
            _mainWindow.HideRequested += (_, _) => _mainWindow.Hide();

            _trayIcon = CreateTrayIcon(_mainWindow);
            _mainWindow.Show();

            var logger = _host.Services.GetRequiredService<ILogger<App>>();
            LogStarted(logger);
        }
        catch (Exception exception)
        {
            if (_host is not null)
            {
                var logger = _host.Services.GetService<ILogger<App>>();
                if (logger is not null)
                {
                    LogStartupFailed(logger, exception);
                }
            }

            System.Windows.MessageBox.Show(
                $"Nila Connector could not start.\n\n{exception.Message}",
                "Nila Connector",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _trayIcon?.Dispose();

        if (_host is not null)
        {
            _host.StopAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
            _host.Dispose();
        }

        base.OnExit(e);
    }

    private static IHost BuildHost()
    {
        var builder = Host.CreateApplicationBuilder();
        var logDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Nila",
            "Connector",
            "logs");

        builder.Logging.ClearProviders();
        builder.Logging.SetMinimumLevel(LogLevel.Information);
        builder.Logging.AddProvider(new JsonLineFileLoggerProvider(logDirectory));

        var tallyOptions = new TallyClientOptions();
        builder.Services.AddSingleton(tallyOptions);
        builder.Services.AddSingleton<ConnectorStateStore>();
        builder.Services.AddSingleton<ITallyClient>(_ => new TallyClient(
            new HttpClient
            {
                Timeout = Timeout.InfiniteTimeSpan,
            },
            tallyOptions));
        builder.Services.AddHostedService<TallyMonitor>();

        builder.Services.AddSingleton(new NilaConnectionOptions());
        builder.Services.AddSingleton(BuildPairingOptions());
        builder.Services.AddSingleton<DpapiDeviceCredentialStore>();
        builder.Services.AddSingleton<IDeviceCredentialStore>(services =>
            services.GetRequiredService<DpapiDeviceCredentialStore>());
        builder.Services.AddSingleton<IExternalBrowser, WindowsExternalBrowser>();
        builder.Services.AddSingleton(services => new NilaPairingClient(
            new HttpClient
            {
                Timeout = Timeout.InfiniteTimeSpan,
            },
            services.GetRequiredService<NilaPairingOptions>()));
        builder.Services.AddSingleton<MachineLinker>();
        builder.Services.AddSingleton<JobRunner>();
        builder.Services.AddHostedService<NilaConnectionMonitor>();
        builder.Services.AddSingleton<MainWindowViewModel>();
        builder.Services.AddSingleton<MainWindow>();

        return builder.Build();
    }

    private static NilaPairingOptions BuildPairingOptions()
    {
        var baseUrl = Environment.GetEnvironmentVariable("NILA_BASE_URL");
        var baseUri = Uri.TryCreate(baseUrl, UriKind.Absolute, out var configuredBaseUri)
            ? configuredBaseUri
            : new Uri("http://127.0.0.1:4000");

        return new NilaPairingOptions
        {
            BaseUri = baseUri,
        };
    }

    private Forms.NotifyIcon CreateTrayIcon(MainWindow window)
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Open Nila Connector", null, (_, _) => ShowWindow(window));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Quit", null, (_, _) => ExitApplication());

        var icon = new Forms.NotifyIcon
        {
            ContextMenuStrip = menu,
            Icon = SystemIcons.Application,
            Text = "Nila Connector",
            Visible = true,
        };
        icon.DoubleClick += (_, _) => ShowWindow(window);
        return icon;
    }

    private static void ShowWindow(MainWindow window)
    {
        window.Show();
        window.WindowState = System.Windows.WindowState.Normal;
        window.Activate();
    }

    private void ExitApplication()
    {
        if (_mainWindow is not null)
        {
            _mainWindow.AllowClose = true;
            _mainWindow.Close();
        }

        Shutdown();
    }

    [LoggerMessage(EventId = 1000, Level = LogLevel.Information, Message = "Nila Connector started")]
    private static partial void LogStarted(ILogger logger);

    [LoggerMessage(EventId = 1001, Level = LogLevel.Critical, Message = "Nila Connector failed to start")]
    private static partial void LogStartupFailed(ILogger logger, Exception exception);
}
