using System.Runtime.InteropServices;
using FFmpeg.AutoGen;

namespace ISpy.Media;

/// <summary>
/// Locates and initialises the native FFmpeg libraries.
/// </summary>
/// <remarks>
/// Loading is deferred until the first stream starts rather than done at startup: the native
/// libraries are tens of megabytes and mapping them is easily a third of the startup budget, which
/// would be paid even by a launch where the user only wanted to look at yesterday's recording.
/// </remarks>
public static class FFmpegRuntime
{
    private static readonly Lock Gate = new();
    private static bool _initialised;
    private static string? _failure;

    /// <summary>Folder searched for avcodec/avformat/avutil, relative to the app directory.</summary>
    public const string NativeFolder = "ffmpeg";

    /// <summary>Null when FFmpeg loaded, otherwise why it did not.</summary>
    public static string? Failure
    {
        get { lock (Gate) return _failure; }
    }

    /// <summary>
    /// Loads FFmpeg once. Safe to call from any thread and on every stream start; subsequent calls
    /// are a lock and a bool check.
    /// </summary>
    public static bool EnsureInitialised()
    {
        lock (Gate)
        {
            if (_initialised) return _failure is null;
            _initialised = true;

            try
            {
                ffmpeg.RootPath = ResolveNativePath();
                DynamicallyLoadedBindings.Initialize();

                // Required before any network protocol is used; without it RTSP fails opaquely.
                ffmpeg.avformat_network_init();

                // FFmpeg's default log level writes a great deal to stderr; we take warnings and up.
                ffmpeg.av_log_set_level(ffmpeg.AV_LOG_WARNING);
            }
            catch (Exception ex)
            {
                _failure =
                    $"FFmpeg could not be loaded from '{ffmpeg.RootPath}'. {ex.Message}";
            }

            return _failure is null;
        }
    }

    /// <summary>
    /// Prefers the copy shipped beside the app, falling back to whatever is on PATH so a developer
    /// with FFmpeg already installed does not have to stage the DLLs by hand.
    /// </summary>
    private static string ResolveNativePath()
    {
        var beside = Path.Combine(AppContext.BaseDirectory, NativeFolder);
        if (Directory.Exists(beside)) return beside;

        return AppContext.BaseDirectory;
    }

    /// <summary>Turns an FFmpeg negative error code into something a human can read.</summary>
    public static unsafe string DescribeError(int error)
    {
        const int bufferSize = 256;
        var buffer = stackalloc byte[bufferSize];

        return ffmpeg.av_strerror(error, buffer, bufferSize) == 0
            ? Marshal.PtrToStringAnsi((IntPtr)buffer) ?? $"FFmpeg error {error}"
            : $"FFmpeg error {error}";
    }
}
