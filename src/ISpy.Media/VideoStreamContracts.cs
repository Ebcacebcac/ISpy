using FFmpeg.AutoGen;

namespace ISpy.Media;

/// <summary>What a stream is currently doing, as shown on the tile.</summary>
public enum StreamState
{
    Idle,
    Connecting,
    Playing,
    Reconnecting,
    Failed,
}

/// <summary>Describes the video being delivered.</summary>
public readonly record struct VideoFrameInfo(
    int Width,
    int Height,
    bool IsHardware,
    AVPixelFormat PixelFormat,
    TimeSpan Timestamp);

/// <summary>
/// Receives decoded frames.
/// </summary>
/// <remarks>
/// <see cref="OnFrame"/> is called on the decode thread and the frame is only valid for the
/// duration of the call - the implementation must copy what it needs and return promptly.
/// Handing out borrowed frames rather than pooled copies is deliberate: it removes frame lifetime
/// ownership from the design entirely, which is where this kind of pipeline usually leaks.
/// </remarks>
public unsafe interface IVideoTarget
{
    void OnFrame(AVFrame* frame, VideoFrameInfo info);

    void OnStateChanged(StreamState state, string? message);
}

/// <summary>Connection settings for one RTSP stream.</summary>
public sealed record VideoStreamOptions
{
    public required string Url { get; init; }

    /// <summary>
    /// TCP by default. UDP has lower overhead but loses packets over Wi-Fi, which shows up as
    /// smearing and green blocks - the artefacts people blame on the camera.
    /// </summary>
    public bool UseTcp { get; init; } = true;

    /// <summary>Give up on a stalled socket after this long and let the reconnect loop take over.</summary>
    public TimeSpan SocketTimeout { get; init; } = TimeSpan.FromSeconds(6);

    /// <summary>Try D3D11VA first, falling back to software decoding if it is unavailable.</summary>
    public bool PreferHardwareDecode { get; init; } = true;

    /// <summary>Redacted form of the URL, safe for logs and error messages.</summary>
    public string SafeUrl => Core.Protocol.HikvisionUrls.Redact(Url);
}
