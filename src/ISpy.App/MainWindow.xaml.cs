using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using ISpy.Core;
using ISpy.Core.Media;
using ISpy.Core.Isapi;
using ISpy.Core.Model;
using ISpy.Core.Security;
using ISpy.Core.Storage;

namespace ISpy.App;

public partial class MainWindow : Window
{
    private InventoryStore? _store;
    private LiveGrid? _grid;
    private UpdateService? _updates;
    private IntPtr _canvasHandle;
    private UpdateStatus? _pendingUpdate;

    public MainWindow()
    {
        InitializeComponent();

        VideoHost.SurfaceReady += handle =>
        {
            _canvasHandle = handle;
            TryStartVideo();
        };
        VideoHost.SurfaceResized += (width, height) => _grid?.Resize(width, height);

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
            TryStartVideo();
        }
        catch (Exception ex)
        {
            StatusText.Text = "Could not open the local database.";
            EmptyHint.Visibility = Visibility.Visible;
            EmptyHint.Text = ex.Message;
        }

        StartupTimeline.Mark("inventory loaded");
        ShowStartupTimings();
        StartUpdateChecks();
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

    /// <summary>
    /// Creates the Direct3D device and starts streaming, once both prerequisites exist: the canvas
    /// window (built when WPF realises the visual tree) and the inventory (loaded after first
    /// paint). Either can happen first, so whichever arrives last does the work.
    /// </summary>
    private void TryStartVideo()
    {
        if (_grid is not null || _store is null || _canvasHandle == IntPtr.Zero) return;

        var (width, height) = VideoHost.PixelSize;

        var grid = new LiveGrid(_store);
        grid.StateChanged += () => Dispatcher.BeginInvoke(ShowStreamStatus);

        try
        {
            grid.Attach(_canvasHandle, width, height);
        }
        catch (Exception ex)
        {
            // No Direct3D means no video, but the rest of the app must stay usable.
            CanvasHint.Text = $"Video could not start: {ex.Message}";
            grid.Dispose();
            return;
        }

        _grid = grid;
        StartStreams();
    }

    private void StartStreams()
    {
        if (_grid is null || _store is null) return;

        _grid.Start();
        StartupTimeline.Mark("streams started");

        CanvasHint.Visibility = _grid.CameraCount == 0 ? Visibility.Visible : Visibility.Collapsed;
        ShowStreamStatus();
    }

    private void ShowStreamStatus()
    {
        if (_grid is null || _grid.CameraCount == 0) return;

        StatusText.Text = _grid.StatusSummary();
    }

    private void OnSetLayout(object sender, RoutedEventArgs e)
    {
        if (_grid is null || sender is not FrameworkElement { Tag: string tag }) return;
        if (!int.TryParse(tag, out var cells)) return;

        _grid.SetLayout((GridLayout)cells);
    }

    /// <summary>Double-click maximises a tile, which also switches it to the main stream.</summary>
    private void OnCanvasClick(object sender, MouseButtonEventArgs e)
    {
        if (_grid is null || e.ClickCount < 2) return;

        var scale = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
        var point = e.GetPosition(VideoHost);

        if (_grid.HitTest((int)(point.X * scale), (int)(point.Y * scale)) is { } index)
            _grid.ToggleMaximized(index);
    }

    /// <summary>
    /// Starts checking for updates in the background, once the window is up. Nothing here may
    /// delay startup, so it is deliberately the last thing the load path does.
    /// </summary>
    private void StartUpdateChecks()
    {
        _updates = new UpdateService(_store);

        _updates.StatusChanged += status => Dispatcher.BeginInvoke(() =>
        {
            if (!status.IsAvailable) return;

            _pendingUpdate = status;
            UpdateIndicator.Text = $"Update to {status.Version}";
            UpdateIndicator.Visibility = Visibility.Visible;
        });

        _updates.Start();
    }

    /// <summary>
    /// Offers the update. Non-modal until clicked, so a check finding something can never
    /// interrupt someone watching their cameras.
    /// </summary>
    private async void OnUpdateClicked(object sender, MouseButtonEventArgs e)
    {
        if (_updates is null || _pendingUpdate is null) return;

        var notes = string.IsNullOrWhiteSpace(_pendingUpdate.ReleaseNotes)
            ? ""
            : $"\n\n{_pendingUpdate.ReleaseNotes.Trim()}";

        var answer = MessageBox.Show(
            this,
            $"ISpy {_pendingUpdate.Version} is available. Install it and restart now?{notes}",
            "Update available",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Information);

        if (answer != MessageBoxResult.OK) return;

        UpdateIndicator.Text = "Downloading…";

        var progress = new Progress<int>(percent =>
            UpdateIndicator.Text = percent >= 100 ? "Installing…" : $"Downloading… {percent}%");

        // Streams are stopped before the restart so nothing is half-written on the way out.
        var error = await _updates.ApplyAsync(progress, () => Dispatcher.Invoke(() => _grid?.Dispose()));

        if (error is not null)
        {
            UpdateIndicator.Text = "Update failed";
            MessageBox.Show(this, error, "Update failed", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnOpenPlayback(object sender, RoutedEventArgs e)
    {
        if (_store is null) return;

        new PlaybackWindow(_store) { Owner = this }.Show();
    }

    private void OnAddDevice(object sender, RoutedEventArgs e)
    {
        if (_store is null) return;

        var dialog = new AddDeviceWindow(_store) { Owner = this };

        if (dialog.ShowDialog() != true) return;

        RenderInventory();
        StartStreams();
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
            StartStreams();

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
        _updates?.Dispose();
        _grid?.Dispose();
        _store?.Dispose();
        base.OnClosed(e);
    }
}
