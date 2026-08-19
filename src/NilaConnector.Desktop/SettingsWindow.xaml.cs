using System.Diagnostics;
using System.IO;
using System.Windows;

namespace NilaConnector.Desktop;

public partial class SettingsWindow : Window
{
    private readonly Func<Task> _unlinkAsync;

    public SettingsWindow(bool isLinked, Func<Task> unlinkAsync)
    {
        InitializeComponent();
        _unlinkAsync = unlinkAsync;
        DeviceStatusText.Text = isLinked ? "Linked" : "Not linked";
        DeviceStatusText.Foreground = isLinked
            ? System.Windows.Media.Brushes.SeaGreen
            : System.Windows.Media.Brushes.DimGray;
        UnlinkButton.IsEnabled = isLinked;
    }

    private void OpenLogs_Click(object sender, RoutedEventArgs e)
    {
        var path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Nila",
            "Connector",
            "logs");
        _ = Directory.CreateDirectory(path);
        _ = Process.Start(new ProcessStartInfo("explorer.exe", path) { UseShellExecute = true });
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

    private void Done_Click(object sender, RoutedEventArgs e) => Close();
}
