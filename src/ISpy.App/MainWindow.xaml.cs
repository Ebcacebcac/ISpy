using System.IO;
using System.Windows;
using System.Windows.Threading;
using ISpy.Core;
using ISpy.Core.Isapi;
using ISpy.Core.Model;
using ISpy.Core.Security;
using ISpy.Core.Storage;

namespace ISpy.App;

public partial class MainWindow : Window
{
    private InventoryStore? _store;

    public MainWindow()
    {
        InitializeComponent();

        // Loading happens strictly after the first frame is on screen. Background priority
        // guarantees WPF has finished rendering before we do any I/O.
        Dispatcher.BeginInvoke(DispatcherPriority.Background, LoadInventory);
    }

    private void LoadInventory()
    {
        StartupTimeline.Mark("first paint");

        try
        {
            AppPaths.EnsureCreated();
            _store = InventoryStore.Open(AppPaths.InventoryDatabase, SecretProtector.CreateDefault());
            RenderInventory();
        }
        catch (Exception ex)
        {
            StatusText.Text = "Could not open the local database.";
            EmptyHint.Visibility = Visibility.Visible;
            EmptyHint.Text = ex.Message;
        }

        StartupTimeline.Mark("inventory loaded");
        ShowStartupTimings();
    }

    private void RenderInventory()
    {
        if (_store is null) return;

        var devices = _store.GetDevices();
        var rows = new List<string>();

        foreach (var device in devices)
        {
            var channels = _store.GetChannels(device.Id);

            // Only label rows with the recorder name when there is more than one, otherwise the
            // camera list reads as "NVR · Driveway" for every single row and the names get lost.
            rows.AddRange(channels.Select(c =>
                devices.Count > 1 ? $"{device.DisplayName} · {c.DisplayName}" : c.DisplayName));
        }

        ChannelList.ItemsSource = rows;
        EmptyHint.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        StatusText.Text = devices.Count switch
        {
            0 => "No recorder configured",
            1 => $"{devices[0].DisplayName} · {rows.Count} cameras",
            _ => $"{devices.Count} recorders · {rows.Count} cameras",
        };
    }

    private void OnAddDevice(object sender, RoutedEventArgs e)
    {
        if (_store is null) return;

        var dialog = new AddDeviceWindow(_store) { Owner = this };

        if (dialog.ShowDialog() == true) RenderInventory();
    }

    /// <summary>
    /// Re-reads every saved recorder's channel list, so a camera added to the NVR appears without
    /// the user having to enter credentials again.
    /// </summary>
    private async void OnRefresh(object sender, RoutedEventArgs e)
    {
        if (_store is null) return;

        RefreshButton.IsEnabled = false;
        StatusText.Text = "Refreshing…";

        try
        {
            var onboarding = new DeviceOnboarding(_store);
            var failures = new List<string>();

            foreach (var device in _store.GetDevices())
            {
                var result = await onboarding.RefreshAsync(device);
                if (!result.IsSuccess) failures.Add($"{device.DisplayName}: {result.Message}");
            }

            RenderInventory();

            if (failures.Count > 0) StatusText.Text = string.Join("   ", failures);
        }
        finally
        {
            RefreshButton.IsEnabled = true;
        }
    }

    private void ShowStartupTimings()
    {
        var marks = StartupTimeline.Snapshot();
        StartupText.Text = string.Join("   ",
            marks.Select(m => $"{m.Stage} {m.At.TotalMilliseconds:F0}ms"));
    }

    protected override void OnClosed(EventArgs e)
    {
        _store?.Dispose();
        base.OnClosed(e);
    }
}
