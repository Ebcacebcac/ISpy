using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using ISpy.Core.Isapi;
using ISpy.Core.Media;
using ISpy.Core.Model;
using ISpy.Core.Protocol;
using ISpy.Core.Storage;
using ISpy.Media;
using ISpy.Media.Rendering;
using Microsoft.Win32;

namespace ISpy.App;

/// <summary>A camera as listed in the playback sidebar.</summary>
internal sealed record CameraChoice(Device Device, Channel Channel)
{
    public string Label => Channel.DisplayName;
}

public partial class PlaybackWindow : Window
{
    private readonly InventoryStore _store;
    private readonly DispatcherTimer _ticker = new() { Interval = TimeSpan.FromMilliseconds(200) };

    private GpuDevice? _gpu;
    private SharedHardwareDevice? _hardware;
    private VideoPresenter? _presenter;
    private PlaybackSession? _session;
    private RecordingTimeline? _timeline;
    private CameraChoice? _camera;
    private IntPtr _canvasHandle;
    private DateTime _lastTick = DateTime.UtcNow;

    public PlaybackWindow(InventoryStore store)
    {
        _store = store;
        InitializeComponent();

        DayPicker.SelectedDate = DateTime.Today;

        VideoHost.SurfaceReady += handle =>
        {
            _canvasHandle = handle;
            InitialiseVideo();
        };

        VideoHost.SurfaceResized += (width, height) => _presenter?.Resize(width, height);
        Timeline.Scrubbed += OnScrubbed;

        _ticker.Tick += OnTick;
        Loaded += (_, _) => LoadCameras();
    }

    private void LoadCameras()
    {
        var cameras = _store.GetDevices()
            .SelectMany(device => _store.GetChannels(device.Id)
                .Where(channel => channel.Enabled)
                .Select(channel => new CameraChoice(device, channel)))
            .ToList();

        CameraList.ItemsSource = cameras;

        if (cameras.Count > 0) CameraList.SelectedIndex = 0;
        else StatusText.Text = "No cameras saved yet.";
    }

    private void InitialiseVideo()
    {
        if (_presenter is not null || _canvasHandle == IntPtr.Zero) return;

        var (width, height) = VideoHost.PixelSize;

        try
        {
            _gpu = GpuDevice.Create();
            _hardware = SharedHardwareDevice.Create(_gpu);
            _presenter = new VideoPresenter(_gpu, _canvasHandle, width, height);
            _ticker.Start();
        }
        catch (Exception ex)
        {
            CanvasHint.Text = $"Video could not start: {ex.Message}";
        }
    }

    private void OnCameraSelected(object sender, SelectionChangedEventArgs e)
    {
        _camera = CameraList.SelectedItem as CameraChoice;
        if (_camera is null) return;

        StatusText.Text = $"{_camera.Label} — choose a day and load it.";
    }

    private async void OnLoadDay(object sender, RoutedEventArgs e)
    {
        if (_camera is null || DayPicker.SelectedDate is not { } day) return;

        var password = _store.GetPassword(_camera.Device.Id);
        if (password is null || _camera.Device.Username is null)
        {
            StatusText.Text = "No saved credentials for that recorder.";
            return;
        }

        // The recorder indexes recordings in its own local time, which is the user's too; convert
        // to an absolute window so the search and the playback URL agree.
        var from = new DateTimeOffset(day.Date, TimeZoneInfo.Local.GetUtcOffset(day.Date));
        var to = from.AddDays(1);

        LoadButton.IsEnabled = false;
        StatusText.Text = "Searching the recorder…";

        try
        {
            using var client = new IsapiClient(
                _camera.Device.Host, _camera.Device.HttpPort, _camera.Device.Username, password);

            _timeline = await new RecordingBrowser(client)
                .BrowseAsync(_camera.Channel.Number, from, to);

            Timeline.SetTimeline(_timeline);

            if (_timeline.IsEmpty)
            {
                StatusText.Text = "No recordings found on that day.";
                ExportButton.IsEnabled = false;
                return;
            }

            StatusText.Text =
                $"{_timeline.Segments.Count} recordings · {_timeline.RecordedDuration:hh\\:mm} of footage";

            ExportButton.IsEnabled = true;
            StartPlayback(_timeline.Segments[0].Start);
        }
        catch (Exception ex)
        {
            StatusText.Text = ex.Message;
        }
        finally
        {
            LoadButton.IsEnabled = true;
        }
    }

    private void StartPlayback(DateTimeOffset moment)
    {
        if (_camera is null || _gpu is null) return;

        var password = _store.GetPassword(_camera.Device.Id);
        if (password is null || _camera.Device.Username is null) return;

        _session?.Dispose();

        _session = new PlaybackSession(
            new PlaybackTarget(
                _camera.Device.Host, _camera.Device.RtspPort, _camera.Channel.Number,
                _camera.Device.Username, password),
            _gpu, _hardware, moment);

        _session.Play();

        CanvasHint.Visibility = Visibility.Collapsed;
        PlayButton.IsEnabled = true;
        PlayButton.Content = "Pause";
    }

    private void OnScrubbed(DateTimeOffset moment)
    {
        if (_session is null) StartPlayback(moment);
        else _session.SeekTo(moment);

        Timeline.SetPlayhead(moment);
    }

    private void OnTogglePlay(object sender, RoutedEventArgs e)
    {
        if (_session is null) return;

        _session.TogglePause();
        PlayButton.Content = _session.IsPaused ? "Play" : "Pause";
    }

    private void OnNudge(object sender, RoutedEventArgs e)
    {
        if (_session is null || sender is not FrameworkElement { Tag: string tag }) return;
        if (!int.TryParse(tag, out var seconds)) return;

        _session.Nudge(TimeSpan.FromSeconds(seconds));
    }

    private void OnSetSpeed(object sender, RoutedEventArgs e)
    {
        if (_session is null || sender is not FrameworkElement { Tag: string tag }) return;
        if (!double.TryParse(tag, out var speed)) return;

        _session.SetSpeed(speed);
    }

    /// <summary>Draws a frame, advances the playhead, and drives skimming when running above 1x.</summary>
    private void OnTick(object? sender, EventArgs e)
    {
        var now = DateTime.UtcNow;
        var elapsed = now - _lastTick;
        _lastTick = now;

        if (_presenter is null) return;

        if (_session is not null)
        {
            _session.AdvanceSkim(elapsed);

            // Playback opens an hour at a time; roll straight into the next one so a long watch
            // does not stop on the hour.
            if (_session.HasReachedSpanEnd) _session.ContinuePastSpan();

            var position = _session.Position;
            Timeline.SetPlayhead(position);
            PositionText.Text = position.ToLocalTime().ToString("ddd HH:mm:ss");

            var (width, height) = _presenter.Size;

            _presenter.Present(
            [
                new TileVisual(
                    _session.Surface,
                    new TileRect(0, 0, width, height),
                    _camera?.Label ?? "",
                    _session.State,
                    _session.StatusMessage,
                    IsSelected: false),
            ]);
        }
        else
        {
            _presenter.Present([]);
        }
    }

    private async void OnExport(object sender, RoutedEventArgs e)
    {
        if (_camera is null || _session is null || _timeline is null) return;

        var password = _store.GetPassword(_camera.Device.Id);
        if (password is null || _camera.Device.Username is null) return;

        var start = _session.Position;
        var end = start.AddMinutes(5);

        // Do not run past the end of the recording that is playing; the recorder would just stall.
        if (_timeline.SegmentAt(start) is { } segment && segment.End < end) end = segment.End;

        var dialog = new SaveFileDialog
        {
            Filter = "MP4 video|*.mp4",
            FileName = $"{Sanitise(_camera.Label)}-{start.ToLocalTime():yyyyMMdd-HHmmss}.mp4",
        };

        if (dialog.ShowDialog(this) != true) return;

        var url = HikvisionUrls.WithCredentials(
            HikvisionUrls.Playback(
                _camera.Device.Host, _camera.Device.RtspPort, _camera.Channel.Number, start, end),
            _camera.Device.Username, password);

        ExportButton.IsEnabled = false;
        StatusText.Text = "Saving clip…";

        try
        {
            var progress = new Progress<ExportProgress>(report =>
                StatusText.Text = $"Saving clip… {report.Fraction:P0}");

            await new ClipExporter().ExportAsync(url, dialog.FileName, end - start, progress);

            StatusText.Text = $"Saved {Path.GetFileName(dialog.FileName)}";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Could not save the clip: {ex.Message}";
        }
        finally
        {
            ExportButton.IsEnabled = true;
        }
    }

    /// <summary>Strips characters Windows will not accept in a file name.</summary>
    private static string Sanitise(string name) =>
        string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '-' : c));

    protected override void OnClosed(EventArgs e)
    {
        _ticker.Stop();
        _session?.Dispose();
        _presenter?.Dispose();
        _hardware?.Dispose();
        _gpu?.Dispose();
        base.OnClosed(e);
    }
}
