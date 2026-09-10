using FFmpeg.AutoGen;
using ISpy.Core.Media;
using ISpy.Media.Rendering;

namespace ISpy.Media;

/// <summary>
/// One camera, kept playing.
/// </summary>
/// <remarks>
/// Owns a decode thread and reconnects on its own with backoff. A tile never has to be restarted by
/// hand: a recorder reboot, a switch losing power or a Wi-Fi camera dropping out all resolve
/// themselves, which is the behaviour that makes a wall of cameras usable unattended.
/// </remarks>
public sealed unsafe class CameraStream : IVideoTarget, IDisposable
{
    private readonly GpuDevice _gpu;
    private readonly AVBufferRef* _hardwareDevice;
    private readonly ReconnectPolicy _reconnect = new();
    private readonly Lock _gate = new();

    private CancellationTokenSource? _cancellation;
    private Thread? _worker;
    private VideoStreamOptions _options;

    public CameraStream(VideoStreamOptions options, GpuDevice gpu, SharedHardwareDevice? hardwareDevice = null)
    {
        _options = options;
        _gpu = gpu;
        _hardwareDevice = hardwareDevice is null ? null : hardwareDevice.Reference;
        Surface = new VideoSurface(gpu);
    }

    /// <summary>The GPU picture for this camera, drawn by the presenter.</summary>
    public VideoSurface Surface { get; }

    public StreamState State { get; private set; } = StreamState.Idle;

    public string? StatusMessage { get; private set; }

    /// <summary>Frames decoded since the stream last connected. Used to spot a stalled camera.</summary>
    public long FrameCount { get; private set; }

    public DateTimeOffset? LastFrameUtc { get; private set; }

    /// <summary>Raised whenever <see cref="State"/> changes. Fired from the decode thread.</summary>
    public event Action<CameraStream>? StateChanged;

    public void Start()
    {
        lock (_gate)
        {
            if (_worker is not null) return;

            _cancellation = new CancellationTokenSource();
            var token = _cancellation.Token;

            _worker = new Thread(() => RunUntilStopped(token))
            {
                IsBackground = true,
                Name = $"ISpy stream {_options.SafeUrl}",
            };

            _worker.Start();
        }
    }

    /// <summary>
    /// Points the stream at a different URL, used when a tile is maximised and switches from the
    /// sub-stream to the main one. Restarts the connection.
    /// </summary>
    public void SwitchTo(VideoStreamOptions options)
    {
        if (options.Url == _options.Url) return;

        Stop();
        _options = options;
        _reconnect.Reset();
        Start();
    }

    public void Stop()
    {
        Thread? worker;

        lock (_gate)
        {
            _cancellation?.Cancel();
            worker = _worker;
            _worker = null;
        }

        // The interrupt callback unblocks a stalled read, so this join is bounded.
        worker?.Join(TimeSpan.FromSeconds(5));

        lock (_gate)
        {
            _cancellation?.Dispose();
            _cancellation = null;
        }

        SetState(StreamState.Idle, null);
    }

    private void RunUntilStopped(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            SetState(_reconnect.Attempt == 0 ? StreamState.Connecting : StreamState.Reconnecting, null);

            try
            {
                using var decoder = new RtspVideoDecoder(_options, this, _hardwareDevice);
                decoder.Run(cancellationToken);

                // A clean end of stream still means reconnecting: a live camera should not stop.
                _reconnect.Reset();
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                SetState(StreamState.Reconnecting, ex.Message);
            }

            if (cancellationToken.IsCancellationRequested) return;

            var delay = _reconnect.NextDelay();

            // WaitHandle rather than Task.Delay so cancellation ends the wait immediately.
            if (cancellationToken.WaitHandle.WaitOne(delay)) return;
        }
    }

    void IVideoTarget.OnFrame(AVFrame* frame, VideoFrameInfo info)
    {
        Surface.Update(frame, info);

        FrameCount++;
        LastFrameUtc = DateTimeOffset.UtcNow;

        // A stream that reconnected and is now delivering frames has recovered; clear the backoff
        // so the next unrelated outage starts from a short delay rather than a long one.
        if (State != StreamState.Playing)
        {
            _reconnect.Reset();
            SetState(StreamState.Playing, null);
        }
    }

    void IVideoTarget.OnStateChanged(StreamState state, string? message) => SetState(state, message);

    private void SetState(StreamState state, string? message)
    {
        if (State == state && StatusMessage == message) return;

        State = state;
        StatusMessage = message;
        StateChanged?.Invoke(this);
    }

    public void Dispose()
    {
        Stop();
        Surface.Dispose();
    }
}
