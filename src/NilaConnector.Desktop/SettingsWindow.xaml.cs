using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using NilaConnector.Desktop.Services;

namespace NilaConnector.Desktop;

public partial class SettingsWindow : Window
{
    private readonly Func<Task> _unlinkAsync;
    private readonly TallyConnectionSettingsStore _tallySettings;
    private readonly string _logDirectory;

    public SettingsWindow(
        bool isLinked,
        TallyConnectionSettingsStore tallySettings,
        string logDirectory,
        Func<Task> unlinkAsync)
    {
        InitializeComponent();
        BuildDetailsText.Text = AppVersion.Build.DisplayName;
        BuildDetailsText.ToolTip = AppVersion.Build.Details;
        _tallySettings = tallySettings;
        _logDirectory = logDirectory;
        _unlinkAsync = unlinkAsync;
        TallyHostTextBox.Text = tallySettings.Host;
        TallyPortTextBox.Text = tallySettings.Port.ToString(CultureInfo.InvariantCulture);
        DeviceStatusText.Text = isLinked ? "Linked" : "Not linked";
        DeviceStatusText.Foreground = isLinked
            ? System.Windows.Media.Brushes.SeaGreen
            : System.Windows.Media.Brushes.DimGray;
        UnlinkButton.IsEnabled = isLinked;
    }

    private async void SaveTally_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            SaveTallyButton.IsEnabled = false;
            TallySaveStatusText.Foreground = System.Windows.Media.Brushes.DimGray;
            TallySaveStatusText.Text = "Saving…";
            await _tallySettings.SaveAsync(TallyHostTextBox.Text, TallyPortTextBox.Text);
            TallySaveStatusText.Foreground = System.Windows.Media.Brushes.SeaGreen;
            TallySaveStatusText.Text = "Saved — reconnecting automatically";
        }
        catch (ArgumentException exception)
        {
            TallySaveStatusText.Foreground = System.Windows.Media.Brushes.Firebrick;
            TallySaveStatusText.Text = exception.Message;
        }
        catch (Exception)
        {
            TallySaveStatusText.Foreground = System.Windows.Media.Brushes.Firebrick;
            TallySaveStatusText.Text = "Could not save the Tally settings.";
        }
        finally
        {
            SaveTallyButton.IsEnabled = true;
        }
    }

    private void OpenLogs_Click(object sender, RoutedEventArgs e)
    {
        _ = Directory.CreateDirectory(_logDirectory);
        _ = Process.Start(new ProcessStartInfo("explorer.exe", _logDirectory) { UseShellExecute = true });
    }

    private async void Unlink_Click(object sender, RoutedEventArgs e)
    {
        var answer = System.Windows.MessageBox.Show(
            this,
            "Unlink this computer from Nila? Tally data will not be changed, but this connector will stop receiving Nila jobs until it is linked again.",
            "Unlink Nila Connector",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);

        if (answer != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            UnlinkButton.IsEnabled = false;
            await _unlinkAsync();
            DialogResult = true;
        }
        catch
        {
            UnlinkButton.IsEnabled = true;
            _ = System.Windows.MessageBox.Show(
                this,
                "Nila could not unlink this device. Check the connection and try again.",
                "Unlink failed",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private void DownloadRelease_Click(object sender, RoutedEventArgs e) =>
        _ = Process.Start(new ProcessStartInfo("https://github.com/KaguyaHQ/nila-downloads/releases/latest")
        {
            UseShellExecute = true,
        });

    private void CopyBuildInfo_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            System.Windows.Clipboard.SetText(AppVersion.Build.Details);
            CopyBuildInfoButton.Content = "Copied";
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            CopyBuildInfoButton.Content = "Try copying again";
        }
    }

    private void Done_Click(object sender, RoutedEventArgs e) => Close();
}
