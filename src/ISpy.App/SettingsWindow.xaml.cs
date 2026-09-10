using System.Diagnostics;
using System.Windows;
using ISpy.Core;
using ISpy.Core.Model;
using ISpy.Core.Storage;

namespace ISpy.App;

/// <summary>A saved recorder as shown in the settings list.</summary>
internal sealed record DeviceRow(Device Device)
{
    public override string ToString() => $"{Device.DisplayName}   ·   {Device.Host}";
}

public partial class SettingsWindow : Window
{
    private readonly InventoryStore _store;
    private readonly UpdateService? _updates;
    private readonly Action _beforeUpdateRestart;

    /// <summary>Set when a recorder was removed, so the caller knows to rebuild the grid.</summary>
    public bool DevicesChanged { get; private set; }

    public SettingsWindow(InventoryStore store, UpdateService? updates, Action beforeUpdateRestart)
    {
        _store = store;
        _updates = updates;
        _beforeUpdateRestart = beforeUpdateRestart;

        InitializeComponent();

        VersionText.Text = $"ISpy {_updates?.CurrentVersion ?? "development build"}";
        AutoUpdateBox.IsChecked = _updates?.IsEnabled ?? true;
        LogPathText.Text = AppPaths.LogDirectory;

        LoadDevices();
    }

    // ---- updates ---------------------------------------------------------

    private async void OnCheckForUpdates(object sender, RoutedEventArgs e)
    {
        if (_updates is null)
        {
            UpdateStatusText.Text = "Updates are unavailable in this session.";
            return;
        }

        CheckButton.IsEnabled = false;
        InstallButton.Visibility = Visibility.Collapsed;
        UpdateStatusText.Text = "Checking…";

        try
        {
            var status = await _updates.CheckAsync();

            if (status.IsAvailable)
            {
                UpdateStatusText.Text = $"ISpy {status.Version} is available.";
                InstallButton.Visibility = Visibility.Visible;
            }
            else
            {
                UpdateStatusText.Text = status.Error ?? "You're on the latest version.";
            }
        }
        catch (Exception ex)
        {
            // CheckAsync is designed not to throw, but a settings dialog is the wrong place to
            // find out that guarantee slipped.
            UpdateStatusText.Text = ex.Message;
        }
        finally
        {
            CheckButton.IsEnabled = true;
        }
    }

    private async void OnInstallUpdate(object sender, RoutedEventArgs e)
    {
        if (_updates is null) return;

        InstallButton.IsEnabled = false;

        try
        {
            var progress = new Progress<int>(percent =>
                UpdateStatusText.Text = percent >= 100 ? "Installing…" : $"Downloading… {percent}%");

            var error = await _updates.ApplyAsync(progress, _beforeUpdateRestart);

            // On success the app restarts and this line is never reached.
            if (error is not null) UpdateStatusText.Text = error;
        }
        finally
        {
            InstallButton.IsEnabled = true;
        }
    }

    private void OnAutoUpdateToggled(object sender, RoutedEventArgs e) =>
        _updates?.SetEnabled(AutoUpdateBox.IsChecked == true);

    // ---- recorders -------------------------------------------------------

    private void LoadDevices()
    {
        var devices = _store.GetDevices();

        DeviceList.ItemsSource = devices.Select(device => new DeviceRow(device)).ToList();
        NoDevicesText.Visibility = devices.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        RemoveButton.IsEnabled = devices.Count > 0;
    }

    private void OnRemoveDevice(object sender, RoutedEventArgs e)
    {
        if (DeviceList.SelectedItem is not DeviceRow row)
        {
            NoDevicesText.Visibility = Visibility.Visible;
            NoDevicesText.Text = "Select a recorder to remove first.";
            return;
        }

        var answer = MessageBox.Show(
            this,
            $"Remove {row.Device.DisplayName} and forget its cameras and stored password?",
            "Remove recorder",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (answer != MessageBoxResult.Yes) return;

        _store.DeleteDevice(row.Device.Id);
        DevicesChanged = true;
        LoadDevices();
    }

    // ---- diagnostics -----------------------------------------------------

    private void OnOpenLogs(object sender, RoutedEventArgs e)
    {
        try
        {
            AppPaths.EnsureCreated();
            Process.Start(new ProcessStartInfo
            {
                FileName = AppPaths.LogDirectory,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            LogPathText.Text = $"{AppPaths.LogDirectory}  ({ex.Message})";
        }
    }
}
