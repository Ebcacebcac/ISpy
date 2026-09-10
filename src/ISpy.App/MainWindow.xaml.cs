using System.Windows;
using System.Windows.Threading;
using ISpy.Core;
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

            var devices = _store.GetDevices();
            var channels = devices.SelectMany(device => _store.GetChannels(device.Id)).ToList();

            Render(devices, channels);
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

    private void Render(IReadOnlyList<Device> devices, IReadOnlyList<Channel> channels)
    {
        ChannelList.ItemsSource = channels.Select(c => c.DisplayName).ToList();
        EmptyHint.Visibility = channels.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        StatusText.Text = devices.Count switch
        {
            0 => "No recorder configured",
            1 => $"{devices[0].DisplayName} · {channels.Count} cameras",
            _ => $"{devices.Count} recorders · {channels.Count} cameras",
        };
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
