using System.Windows.Media;
using ISpy.Core;
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

    /// <summary>Stable identity for persisting the user's tile arrangement.</summary>
    public string OrderKey => $"{Device.Id}|{Channel.Number}";
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

    private const string LayoutSetting = "grid.layout.spec";
    private const string LegacyLayoutSetting = "grid.layout";
    private const string OrderSetting = "grid.order";

    public LiveGrid(InventoryStore store)
    {
        _store = store;

        // Restoring the shape the user left the app in is part of it feeling instant: the grid is
        // already the right shape when the first frames land, with no visible reflow.
        var restored = LayoutSpec.FromJson(store.GetSetting(LayoutSetting));

        // Older builds stored the uniform grid as an enum number; honour it once, then the spec
        // setting takes over.
        if (restored is null &&
            int.TryParse(store.GetSetting(LegacyLayoutSetting), out var legacy))
        {
            restored = legacy switch
            {
                1 => LayoutSpec.Single,
                4 => LayoutSpec.TwoByTwo,
                9 => LayoutSpec.ThreeByThree,
                16 => LayoutSpec.FourByFour,
                _ => null,
            };
        }

        if (restored is not null)
        {
            Layout = restored;
            _layoutRestored = true;
        }
    }

    /// <summary>Which tile is maximised, or null when the whole grid is shown.</summary>
    public int? MaximizedIndex { get; private set; }

    /// <summary>Tile the user is currently dragging, drawn with a highlight border.</summary>
    public int? HighlightedIndex { get; set; }

    public LayoutSpec Layout { get; private set; } = LayoutSpec.TwoByTwo;

    public int CameraCount => _entries.Count;

    /// <summary>Raised when a stream's state changes, so the status bar can be refreshed.</summary>
    public event Action? StateChanged;

    private bool _firstFrameMarked;

    private void OnStreamStateChanged(CameraStream stream)
    {
        // The first camera to deliver a picture is what the first-frame budget is measured against.
        if (!_firstFrameMarked && stream.State == StreamState.Playing)
        {
            _firstFrameMarked = true;
            StartupTimeline.Mark("first frame");
        }

        StateChanged?.Invoke();
    }

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

                stream.StateChanged += OnStreamStateChanged;

                _entries.Add(new GridEntry(device, channel, stream) { Profile = profile });

                // Paint the last known frame before the connection is even attempted, so the grid
                // is never a wall of black rectangles while RTSP negotiates.
                if (PosterStore.Load(device.Id, channel.Number) is { } poster)
                    stream.Surface.SetPoster(poster.Bgra, poster.Width, poster.Height);

                stream.Start();
            }
        }

        RestoreOrder();

        // Only auto-size the grid when the user has not chosen a shape themselves.
        if (!_layoutRestored) Layout = LayoutSpec.FitFor(_entries.Count);

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

    public void SetLayout(LayoutSpec layout)
    {
        Layout = layout;
        MaximizedIndex = null;

        _store.SetSetting(LayoutSetting, layout.ToJson());
        UpdateStreamProfiles();
    }

    /// <summary>
    /// Swaps two tiles' cameras - how a camera is moved into the hero tile. Persisted, so the
    /// arrangement survives restarts.
    /// </summary>
    public void SwapTiles(int first, int second)
    {
        if (first == second) return;
        if (first < 0 || second < 0 || first >= _entries.Count || second >= _entries.Count) return;

        (_entries[first], _entries[second]) = (_entries[second], _entries[first]);

        _store.SetSetting(OrderSetting,
            System.Text.Json.JsonSerializer.Serialize(_entries.Select(e => e.OrderKey).ToArray()));

        // The two cameras now sit in different-sized tiles, so their stream profiles may flip.
        UpdateStreamProfiles();
    }

    private void RestoreOrder()
    {
        string[]? saved = null;

        try
        {
            var json = _store.GetSetting(OrderSetting);
            if (json is not null)
                saved = System.Text.Json.JsonSerializer.Deserialize<string[]>(json);
        }
        catch (System.Text.Json.JsonException)
        {
            // A corrupt setting means natural order, nothing worse.
        }

        if (saved is null) return;

        var ordered = TileOrder.Apply(_entries, entry => entry.OrderKey, saved);
        _entries.Clear();
        _entries.AddRange(ordered);
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
    /// maximised camera sharpen: the tile is now large enough to justify the main stream. It is
    /// also what puts the hero tile of a 1+7 layout on the main stream while the small tiles
    /// around it stay on the cheap sub-stream.
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

            var tileWidth = isMaximized
                ? _width
                : i < tiles.Count ? tiles[i].Width : 0;

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

    private int _consecutiveRenderFailures;

    /// <summary>Draws one frame. Bound to the compositor so we never render faster than the display.</summary>
    private void OnRendering(object? sender, EventArgs e)
    {
        if (_presenter is null) return;

        try
        {
            RenderFrame();
            _consecutiveRenderFailures = 0;
        }
        catch (Exception ex)
        {
            // A lost device (driver reset, remote session change) fails every frame; the streams
            // keep decoding regardless, and endless dialog boxes help nobody. Log the first, and
            // stop presenting after a sustained run of failures rather than spinning.
            if (_consecutiveRenderFailures++ == 0)
                Logs.Append("crash.log", $"render: {ex}");

            if (_consecutiveRenderFailures >= 120 && _rendering)
            {
                CompositionTarget.Rendering -= OnRendering;
                _rendering = false;
                Logs.Append("crash.log", "render: giving up after sustained failures; restart ISpy to recover video.");
            }
        }
    }

    private void RenderFrame()
    {
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
                visuals.Add(ToVisual(_entries[i], tiles[i], isSelected: HighlightedIndex == i));
            }
        }

        _presenter!.Present(visuals);
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

    /// <summary>
    /// Saves each tile's current frame for the next launch. A readback stalls the GPU, which is why
    /// it happens here on the way out rather than periodically.
    /// </summary>
    public void SavePosters()
    {
        foreach (var entry in _entries)
        {
            if (entry.Stream.FrameCount == 0) continue;

            try
            {
                if (entry.Stream.Surface.ReadBack() is not { } pixels) continue;

                PosterStore.Save(
                    entry.Device.Id, entry.Channel.Number, pixels,
                    entry.Stream.Surface.Width, entry.Stream.Surface.Height);
            }
            catch (Exception)
            {
                // Never let a thumbnail stop the app closing.
            }
        }
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

        SavePosters();
        StopStreams();
        _presenter?.Dispose();
        _hardware?.Dispose();
        _gpu?.Dispose();
    }
}
