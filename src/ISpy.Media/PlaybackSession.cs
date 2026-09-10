using ISpy.Core.Media;
using ISpy.Core.Model;
using ISpy.Core.Protocol;
using ISpy.Media.Rendering;

namespace ISpy.Media;

/// <summary>Where the playback URL and its credentials come from.</summary>
public sealed record PlaybackTarget(
    string Host, int RtspPort, int Channel, string Username, string Password);

/// <summary>
/// Plays recorded footage from a recorder, with seek and faster-than-real-time playback.
/// </summary>
/// <remarks>
/// Seeking is done by reopening the stream at the new moment rather than by issuing an RTSP Range
/// request mid-session. Reopening is what every firmware in this family handles identically, and
/// the recorder starts sending within a few hundred milliseconds - whereas mid-session seek support
/// varies by firmware and fails in ways that are hard to detect.
/// </remarks>
public sealed class PlaybackSession : IDisposable
{
    /// <summary>How much footage each opened span covers before it is extended.</summary>
    private static readonly TimeSpan SpanLength = TimeSpan.FromHours(1);

    private readonly PlaybackTarget _target;
    private readonly CameraStream _stream;

    private DateTimeOffset _spanStart;
    private DateTimeOffset _limit;

    public PlaybackSession(
        PlaybackTarget target, GpuDevice gpu, SharedHardwareDevice? hardware, DateTimeOffset start)
    {
        _target = target;
        _spanStart = start;
        _limit = start.Add(SpanLength);

        _stream = new CameraStream(BuildOptions(start), gpu, hardware);
        _stream.StateChanged += _ => StateChanged?.Invoke(this);
    }

    public VideoSurface Surface => _stream.Surface;

    public StreamState State => _stream.State;

    public string? StatusMessage => _stream.StatusMessage;

    public bool IsPaused { get; private set; }

    /// <summary>Playback rate. Above 1x the playhead skims forward rather than decoding everything.</summary>
    public double Speed { get; private set; } = 1.0;

    /// <summary>Wall-clock moment currently on screen.</summary>
    public DateTimeOffset Position => _spanStart + _stream.StreamPosition;

    public event Action<PlaybackSession>? StateChanged;

    public void Play()
    {
        IsPaused = false;
        _stream.Start();
    }

    /// <summary>Stops decoding and leaves the last frame on screen.</summary>
    public void Pause()
    {
        if (IsPaused) return;

        // Capture where we are before stopping, so resuming continues from here rather than
        // restarting the span.
        var resumeAt = Position;
        IsPaused = true;
        _stream.Stop();
        _spanStart = resumeAt;
    }

    public void TogglePause()
    {
        if (IsPaused) SeekTo(_spanStart);
        else Pause();
    }

    /// <summary>Jumps to a moment and resumes playing from it.</summary>
    public void SeekTo(DateTimeOffset moment)
    {
        _spanStart = moment;
        _limit = moment.Add(SpanLength);
        IsPaused = false;

        _stream.SwitchTo(BuildOptions(moment));
    }

    public void Nudge(TimeSpan offset) => SeekTo(Position + offset);

    /// <summary>
    /// Sets the playback rate. 1x decodes normally; above that the session advances by skipping
    /// ahead, which is how a recorder can be scrubbed quickly without it having to re-encode.
    /// </summary>
    public void SetSpeed(double speed)
    {
        Speed = Math.Clamp(speed, 1.0, 64.0);
        if (Math.Abs(Speed - 1.0) < 0.001) return;

        SeekTo(Position);
    }

    /// <summary>
    /// Advances the playhead when running faster than real time. Called once per UI tick with the
    /// elapsed wall time; skipping is done in seek steps so any firmware can keep up.
    /// </summary>
    public void AdvanceSkim(TimeSpan elapsed)
    {
        if (IsPaused || Speed <= 1.0) return;

        // Only skip once the current span has delivered something, so we do not seek past footage
        // faster than the recorder can start sending it.
        if (_stream.State != StreamState.Playing) return;

        var extra = elapsed * (Speed - 1.0);
        if (extra < TimeSpan.FromMilliseconds(500)) return;

        SeekTo(Position + extra);
    }

    /// <summary>True once playback has run past the span it was opened for.</summary>
    public bool HasReachedSpanEnd => Position >= _limit;

    /// <summary>Opens the next span, so continuous playback runs past the first hour.</summary>
    public void ContinuePastSpan()
    {
        if (!HasReachedSpanEnd) return;

        SeekTo(_limit);
    }

    private VideoStreamOptions BuildOptions(DateTimeOffset start)
    {
        var url = HikvisionUrls.Playback(
            _target.Host, _target.RtspPort, _target.Channel, start, start.Add(SpanLength),
            StreamProfile.Main);

        return new VideoStreamOptions
        {
            Url = HikvisionUrls.WithCredentials(url, _target.Username, _target.Password),

            // Playback tolerates a longer wait than live: the recorder has to locate the footage on
            // disk first, and giving up early looks like "no recording here".
            SocketTimeout = TimeSpan.FromSeconds(12),
        };
    }

    public void Dispose() => _stream.Dispose();
}
