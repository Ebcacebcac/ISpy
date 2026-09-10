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
        ShowVersion();
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
        PopulateLayoutPicker();
    }

    private void StartStreams()
    {
        if (_grid is null || _store is null) return;

        try
        {
            _grid.Start();
        }
        catch (Exception ex)
        {
            Logs.Append("crash.log", $"starting streams: {ex}");
            StatusText.Text = $"Could not start the cameras: {ex.Message}";
            return;
        }

        StartupTimeline.Mark("streams started");

        CanvasHint.Visibility = _grid.CameraCount == 0 ? Visibility.Visible : Visibility.Collapsed;
        ShowStreamStatus();
    }

    private void ShowStreamStatus()
    {
        if (_grid is null || _grid.CameraCount == 0) return;

        StatusText.Text = _grid.StatusSummary();
    }

    // ---- layout picker ---------------------------------------------------

    private const string CustomLayoutsSetting = "layouts.custom";

    /// <summary>
    /// Fills the picker panel: built-in presets as thumbnails grouped Standard/Wide, saved custom
    /// layouts, and an Add tile that opens the designer - the arrangement people know from the
    /// vendor clients, drawn from the same geometry the live grid uses.
    /// </summary>
    private void PopulateLayoutPicker()
    {
        if (_store is null || _grid is null) return;

        StandardLayouts.Children.Clear();
        WideLayouts.Children.Clear();
        CustomLayouts.Children.Clear();

        foreach (var spec in LayoutSpec.BuiltIn)
        {
            var panel = spec.Category == "Wide" ? WideLayouts : StandardLayouts;
            panel.Children.Add(CreateLayoutTile(spec, isCustom: false));
        }

        foreach (var spec in LayoutSpec.ListFromJson(_store.GetSetting(CustomLayoutsSetting)))
            CustomLayouts.Children.Add(CreateLayoutTile(spec, isCustom: true));

        CustomLayouts.Children.Add(CreateAddTile());
    }

    private System.Windows.Controls.Button CreateLayoutTile(LayoutSpec spec, bool isCustom)
    {
        var stack = new System.Windows.Controls.StackPanel();

        stack.Children.Add(new LayoutThumbnail
        {
            Spec = spec,
            Width = 62,
            Height = 42,
        });

        stack.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = spec.Name,
            FontSize = 11,
            Margin = new Thickness(0, 4, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Center,
            Foreground = (System.Windows.Media.Brush)FindResource(
                _grid?.Layout.Name == spec.Name ? "Accent" : "TextMuted"),
            MaxWidth = 62,
            TextTrimming = System.Windows.TextTrimming.CharacterEllipsis,
        });

        var tile = new System.Windows.Controls.Button
        {
            Content = stack,
            Margin = new Thickness(2),
            Padding = new Thickness(6, 6, 6, 4),
            ToolTip = $"{spec.Name} · {spec.TileCount} tiles",
        };

        if (_grid?.Layout.Name == spec.Name)
            tile.BorderBrush = (System.Windows.Media.Brush)FindResource("Accent");

        tile.Click += (_, _) =>
        {
            _grid?.SetLayout(spec);
            LayoutPopup.IsOpen = false;
            PopulateLayoutPicker();
        };

        if (isCustom)
        {
            var menu = new System.Windows.Controls.ContextMenu();

            var edit = new System.Windows.Controls.MenuItem { Header = "Edit…" };
            edit.Click += (_, _) => EditCustomLayout(spec);

            var delete = new System.Windows.Controls.MenuItem { Header = "Delete" };
            delete.Click += (_, _) => DeleteCustomLayout(spec);

            menu.Items.Add(edit);
            menu.Items.Add(delete);
            tile.ContextMenu = menu;
        }

        return tile;
    }

    private System.Windows.Controls.Button CreateAddTile()
    {
        var tile = new System.Windows.Controls.Button
        {
            Content = new System.Windows.Controls.TextBlock
            {
                Text = "+ Add",
                Foreground = (System.Windows.Media.Brush)FindResource("Accent"),
            },
            Margin = new Thickness(2),
            MinWidth = 74,
            MinHeight = 62,
            ToolTip = "Design a custom layout",
        };

        tile.Click += (_, _) =>
        {
            LayoutPopup.IsOpen = false;
            EditCustomLayout(existing: null);
        };

        return tile;
    }

    private void EditCustomLayout(LayoutSpec? existing)
    {
        if (_store is null || _grid is null) return;

        var editor = new LayoutEditorWindow(existing) { Owner = this };

        if (editor.ShowDialog() == true && editor.Result is { } saved)
        {
            var customs = LayoutSpec.ListFromJson(_store.GetSetting(CustomLayoutsSetting));

            // Same-named layouts are replaced; a rename during edit removes the original too.
            customs.RemoveAll(spec => spec.Name == saved.Name);
            if (existing is not null) customs.RemoveAll(spec => spec.Name == existing.Name);
            customs.Add(saved);

            _store.SetSetting(CustomLayoutsSetting, LayoutSpec.ListToJson(customs));
            _grid.SetLayout(saved);
        }

        PopulateLayoutPicker();
    }

    private void DeleteCustomLayout(LayoutSpec spec)
    {
        if (_store is null) return;

        var customs = LayoutSpec.ListFromJson(_store.GetSetting(CustomLayoutsSetting));
        customs.RemoveAll(candidate => candidate.Name == spec.Name);
        _store.SetSetting(CustomLayoutsSetting, LayoutSpec.ListToJson(customs));

        // The layout being deleted may be on screen; fall back to a sensible preset.
        if (_grid is not null && _grid.Layout.Name == spec.Name)
            _grid.SetLayout(LayoutSpec.FitFor(_grid.CameraCount));

        PopulateLayoutPicker();
    }

    // ---- mouse on the video canvas ---------------------------------------
    // Raised by the canvas's own window in device pixels, which is the layout's coordinate space.

    private int? _dragSource;
    private (int X, int Y) _dragStart;
    private bool _dragging;

    private void OnCanvasPressed(int x, int y)
    {
        if (_grid is null) return;

        _dragSource = _grid.HitTest(x, y);
        _dragStart = (x, y);
        _dragging = false;
    }

    private void OnCanvasMoved(int x, int y)
    {
        if (_grid is null || _dragSource is null) return;

        // A real drag, not a wobbly click: highlight the source tile so the user can see what
        // they are about to move.
        if (!_dragging && (Math.Abs(x - _dragStart.X) > 8 || Math.Abs(y - _dragStart.Y) > 8))
        {
            _dragging = true;
            _grid.HighlightedIndex = _dragSource;
        }
    }

    private void OnCanvasReleased(int x, int y)
    {
        if (_grid is null) return;

        var source = _dragSource;
        _dragSource = null;
        _grid.HighlightedIndex = null;

        if (!_dragging || source is null) return;
        _dragging = false;

        if (_grid.HitTest(x, y) is { } target && target != source.Value)
            _grid.SwapTiles(source.Value, target);
    }

    /// <summary>Double-click maximises a tile, which also switches it to the main stream.</summary>
    private void OnCanvasDoubleClicked(int x, int y)
    {
        if (_grid is null) return;

        if (_grid.HitTest(x, y) is { } index) _grid.ToggleMaximized(index);
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

    private void OnOpenSettings(object sender, RoutedEventArgs e)
    {
        if (_store is null) return;

        var settings = new SettingsWindow(
            _store,
            _updates,
            beforeUpdateRestart: () => Dispatcher.Invoke(() => _grid?.Dispose()))
        {
            Owner = this,
        };

        settings.ShowDialog();

        // A removed recorder means the grid is showing streams that no longer exist.
        if (settings.DevicesChanged)
        {
            RenderInventory();
            StartStreams();
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
        catch (Exception ex)
        {
            // An exception out of an async void handler ends the process; report it instead.
            StatusText.Text = $"Refresh failed: {ex.Message}";
        }
        finally
        {
            RefreshButton.IsEnabled = true;
        }
    }

    private void ShowVersion()
    {
        var version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
        VersionText.Text = version is null ? "ISpy" : $"ISpy {version.ToString(3)}";
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
