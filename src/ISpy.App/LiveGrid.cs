using System.Windows.Media;
using ISpy.Core.Media;
using ISpy.Core.Model;
using ISpy.Core.Protocol;
using ISpy.Core.Storage;
using ISpy.Media;
using ISpy.Media.Rendering;

namespace ISpy.App;

/// <summary>One camera's place in the grid.</summary>
internal sealed record GridEntry(Device Device, Channel Channel, CameraStream Stream)
{
    public StreamProfile Profile { get; set; } = StreamProfile.Sub;
}

/// <summary>
/// Runs the live wall: opens a stream per camera, lays them out, and draws a frame per display
/// refresh.
/// </summary>
public sealed class LiveGrid : IDisposable
{
    private readonly List<GridEntry> _entries = [];
    private readonly InventoryStore _store;

    private GpuDevice? _gpu;
    private SharedHardwareDevice? _hardware;
    private VideoPresenter? _presenter;
    private bool _rendering;
    private readonly bool _layoutRestored;
    private int _width;
    private int _height;

    /// <summary>Settings key for the remembered grid shape.</summary>
    private const string LayoutSetting = "grid.layout";

    public LiveGrid(InventoryStore store)
    {
        _store = store;

        // Restoring the shape the user left the app in is part of it feeling instant: the grid is
        // already the right shape when the first frames land, with no visible reflow.
        if (int.TryParse(store.GetSetting(LayoutSetting), out var cells) &&
            Enum.IsDefined(typeof(GridLayout), cells))
        {
            Layout = (GridLayout)cells;
            _layoutRestored = true;
        }
    }

    /// <summary>Which tile is maximised, or null when the whole grid is shown.</summary>
    public int? MaximizedIndex { get; private set; }

    public GridLayout Layout { get; private set; } = GridLayout.TwoByTwo;

    public int CameraCount => _entries.Count;

    /// <summary>Raised when a stream's state changes, so the status bar can be refreshed.</summary>
    public event Action? StateChanged;

    /// <summary>Attaches to the canvas window. Called once the HWND exists.</summary>
    public void Attach(IntPtr windowHandle, int width, int height)
    {
        _width = Math.Max(1, width);
        _height = Math.Max(1, height);

        _gpu = GpuDevice.Create();
        _hardware = SharedHardwareDevice.Create(_gpu);
        _presenter = new VideoPresenter(_gpu, windowHandle, _width, _height);

        if (!_rendering)
        {
            CompositionTarget.Rendering += OnRendering;
            _rendering = true;
        }
    }

    public void Resize(int width, int height)
    {
        _width = Math.Max(1, width);
        _height = Math.Max(1, height);

        _presenter?.Resize(_width, _height);
    }

    /// <summary>Opens a stream for every saved camera and starts them.</summary>
    public void Start()
    {
        if (_gpu is null) return;

        StopStreams();

        foreach (var device in _store.GetDevices())
        {
            var password = _store.GetPassword(device.Id);
            if (device.Username is null || password is null) continue;

            foreach (var channel in _store.GetChannels(device.Id).Where(c => c.Enabled))
            {
                var profile = StreamSelection.Resolve(channel, StreamProfile.Sub);
                var stream = new CameraStream(
                    BuildOptions(device, channel, profile, password), _gpu, _hardware);

                stream.StateChanged += _ => StateChanged?.Invoke();

                _entries.Add(new GridEntry(device, channel, stream) { Profile = profile });
                stream.Start();
            }
        }

        // Only auto-size the grid when the user has not chosen a shape themselves.
        if (!_layoutRestored) Layout = TileLayout.FitFor(_entries.Count);

        UpdateStreamProfiles();
    }

    private VideoStreamOptions BuildOptions(
        Device device, Channel channel, StreamProfile profile, string password)
    {
        var url = HikvisionUrls.Live(device.Host, device.RtspPort, channel.Number, profile);

        return new VideoStreamOptions
        {
            Url = HikvisionUrls.WithCredentials(url, device.Username!, password),
        };
    }

    public void SetLayout(GridLayout layout)
    {
        Layout = layout;
        MaximizedIndex = null;

        _store.SetSetting(LayoutSetting, ((int)layout).ToString());
        UpdateStreamProfiles();
    }

    /// <summary>Maximises a tile, or restores the grid when it is already maximised.</summary>
    public void ToggleMaximized(int index)
    {
        if (index < 0 || index >= _entries.Count) return;

        MaximizedIndex = MaximizedIndex == index ? null : index;
        UpdateStreamProfiles();
    }

    /// <summary>Index of the camera under a point on the canvas, in device pixels.</summary>
    public int? HitTest(int x, int y)
    {
        if (MaximizedIndex is not null) return MaximizedIndex;

        var index = TileLayout.HitTest(TileLayout.Compute(Layout, _width, _height), x, y);
        return index is not null && index < _entries.Count ? index : null;
    }

    /// <summary>
    /// Moves each stream onto the profile its current tile size warrants. This is what makes a
    /// maximised camera sharpen: the tile is now large enough to justify the main stream.
    /// </summary>
    private void UpdateStreamProfiles()
    {
        var tiles = CurrentTiles();

        for (var i = 0; i < _entries.Count; i++)
        {
            var entry = _entries[i];
            var isMaximized = MaximizedIndex == i;
            var visible = isMaximized || MaximizedIndex is null;

            if (!visible) continue;

            var tileWidth = i < tiles.Count ? tiles[i].Width : 0;
            var wanted = StreamSelection.Resolve(
                entry.Channel, StreamSelection.ForTile(tileWidth, isMaximized));

            if (wanted == entry.Profile) continue;

            var password = _store.GetPassword(entry.Device.Id);
            if (password is null) continue;

            entry.Profile = wanted;
            entry.Stream.SwitchTo(BuildOptions(entry.Device, entry.Channel, wanted, password));
        }
    }

    private IReadOnlyList<TileRect> CurrentTiles() =>
        MaximizedIndex is not null
            ? [new TileRect(0, 0, _width, _height)]
            : TileLayout.Compute(Layout, _width, _height);

    /// <summary>Draws one frame. Bound to the compositor so we never render faster than the display.</summary>
    private void OnRendering(object? sender, EventArgs e)
    {
        if (_presenter is null) return;

        var tiles = CurrentTiles();
        var visuals = new List<TileVisual>(tiles.Count);

        if (MaximizedIndex is { } maximized && maximized < _entries.Count)
        {
            var entry = _entries[maximized];
            visuals.Add(ToVisual(entry, tiles[0], isSelected: false));
        }
        else
        {
            for (var i = 0; i < tiles.Count && i < _entries.Count; i++)
            {
                visuals.Add(ToVisual(_entries[i], tiles[i], isSelected: false));
            }
        }

        _presenter.Present(visuals);
    }

    private static TileVisual ToVisual(GridEntry entry, TileRect tile, bool isSelected) =>
        new(entry.Stream.Surface,
            tile,
            entry.Channel.DisplayName,
            entry.Stream.State,
            entry.Stream.StatusMessage,
            isSelected);

    /// <summary>A one-line summary for the status bar.</summary>
    public string StatusSummary()
    {
        if (_entries.Count == 0) return "No cameras";

        var playing = _entries.Count(e => e.Stream.State == StreamState.Playing);
        var hardware = _hardware is not null ? "GPU decode" : "software decode";

        return $"{playing}/{_entries.Count} live · {hardware}";
    }

    private void StopStreams()
    {
        foreach (var entry in _entries) entry.Stream.Dispose();
        _entries.Clear();
    }

    public void Dispose()
    {
        if (_rendering)
        {
            CompositionTarget.Rendering -= OnRendering;
            _rendering = false;
        }

        StopStreams();
        _presenter?.Dispose();
        _hardware?.Dispose();
        _gpu?.Dispose();
    }
}
