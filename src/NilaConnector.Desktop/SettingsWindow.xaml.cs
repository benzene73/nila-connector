using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using NilaConnector.Desktop.Services;

namespace NilaConnector.Desktop;

public partial class SettingsWindow : Window
{
    private readonly Func<Task> _unlinkAsync;
    private readonly TallyConnectionSettingsStore _tallySettings;
    private readonly string _logDirectory;
    private readonly ReleaseUpdateClient _releaseUpdates;
    private ConnectorRelease? _latestRelease;

    public SettingsWindow(
        bool isLinked,
        TallyConnectionSettingsStore tallySettings,
        string logDirectory,
        Func<Task> unlinkAsync,
        ReleaseUpdateClient releaseUpdates)
    {
        InitializeComponent();
        BuildDetailsText.Text = AppVersion.Build.DisplayName;
        BuildDetailsText.ToolTip = AppVersion.Build.Details;
        CheckUpdatesButton.Visibility = AppVersion.Build.IsRelease ? Visibility.Visible : Visibility.Collapsed;
        _tallySettings = tallySettings;
        _logDirectory = logDirectory;
        _unlinkAsync = unlinkAsync;
        _releaseUpdates = releaseUpdates;
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

    private async void CheckUpdates_Click(object sender, RoutedEventArgs e)
    {
        if (!AppVersion.Build.IsRelease) { return; }
        using var cancellation = new CancellationTokenSource();
        void CancelCheck(object? closedSender, EventArgs closedArgs) => cancellation.Cancel();
        Closed += CancelCheck;
        CheckUpdatesButton.IsEnabled = false;
        DownloadUpdateButton.Visibility = Visibility.Collapsed;
        _latestRelease = null;
        UpdateStatusRow.Visibility = Visibility.Visible;
        UpdateStatusText.Text = "Checking for updates…";
        try
        {
            var release = await _releaseUpdates.GetLatestAsync(cancellation.Token);
            if (!IsLoaded) { return; }
            _latestRelease = release;
            var newer = release.IsNewerThan(AppVersion.Build.Version);
            UpdateStatusText.Text = newer ? $"Version {release.Version} available" : "You're up to date.";
            DownloadUpdateButton.Visibility = newer ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException
            or JsonException or InvalidDataException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            if (IsLoaded) { UpdateStatusText.Text = "Couldn't check for updates. Try again."; }
        }
        finally
        {
            Closed -= CancelCheck;
            if (IsLoaded) { CheckUpdatesButton.IsEnabled = true; }
        }
    }

    private void DownloadUpdate_Click(object sender, RoutedEventArgs e)
    {
        if (_latestRelease is not null && OpenLink(_latestRelease.DownloadUri))
        {
            UpdateStatusText.Text = "Download opened in your browser. Run the installer to update.";
        }
    }

    private void ReleaseNotes_Click(object sender, RoutedEventArgs e) =>
        _ = OpenLink(_latestRelease?.NotesUri ?? new Uri(ReleaseUpdateClient.ReleaseNotesUrl));

    private bool OpenLink(Uri uri)
    {
        try
        {
            _ = Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
            return true;
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            UpdateStatusRow.Visibility = Visibility.Visible;
            UpdateStatusText.Text = "Couldn't open your browser. Try again.";
            return false;
        }
    }

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
