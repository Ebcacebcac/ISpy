using System.Globalization;
using System.Text;
using ISpy.Core.Model;

namespace ISpy.Core.Protocol;

/// <summary>
/// Builds the RTSP and ISAPI URLs used by Hikvision-family devices (including the OEM badges
/// Guarding Vision ships for: Annke Vision, LTS, Vikylin and friends).
/// </summary>
/// <remarks>
/// The stream id encoding is the part everyone gets wrong: it is <c>channel * 100 + profile</c>,
/// so channel 1 main stream is 101, channel 1 sub stream is 102, and channel 10 main is 1001.
/// </remarks>
public static class HikvisionUrls
{
    /// <summary>Hikvision timestamps are basic-format UTC, e.g. 20260910T143000Z.</summary>
    public const string TimeFormat = "yyyyMMdd'T'HHmmss'Z'";

    public static int StreamId(int channel, StreamProfile profile)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(channel, 1);
        return channel * 100 + (int)profile;
    }

    public static string FormatTime(DateTimeOffset value) =>
        value.ToUniversalTime().ToString(TimeFormat, CultureInfo.InvariantCulture);

    public static DateTimeOffset ParseTime(string value) =>
        DateTimeOffset.ParseExact(value, TimeFormat, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);

    /// <summary>Live stream URL, without credentials.</summary>
    public static string Live(string host, int rtspPort, int channel, StreamProfile profile) =>
        $"rtsp://{FormatHost(host)}:{rtspPort}/Streaming/Channels/{StreamId(channel, profile)}";

    /// <summary>
    /// Playback URL for a recorded span. The device seeks to <paramref name="start"/> and stops at
    /// <paramref name="end"/>; seeking within a running session is done with an RTSP Range header.
    /// </summary>
    public static string Playback(
        string host, int rtspPort, int channel, DateTimeOffset start, DateTimeOffset end,
        StreamProfile profile = StreamProfile.Main)
    {
        if (end <= start)
            throw new ArgumentException("Playback end must be after start.", nameof(end));

        return $"rtsp://{FormatHost(host)}:{rtspPort}/Streaming/tracks/{StreamId(channel, profile)}" +
               $"?starttime={FormatTime(start)}&endtime={FormatTime(end)}";
    }

    /// <summary>
    /// Injects credentials into an rtsp:// URL, percent-encoding them so that passwords containing
    /// '@', ':' or '/' cannot corrupt the authority section.
    /// </summary>
    public static string WithCredentials(string rtspUrl, string username, string password)
    {
        const string scheme = "rtsp://";
        if (!rtspUrl.StartsWith(scheme, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Expected an rtsp:// URL.", nameof(rtspUrl));

        var user = Uri.EscapeDataString(username);
        var pass = Uri.EscapeDataString(password);
        return $"{scheme}{user}:{pass}@{rtspUrl[scheme.Length..]}";
    }

    /// <summary>Base ISAPI endpoint, e.g. http://192.168.1.64:80/ISAPI.</summary>
    public static string IsapiBase(string host, int httpPort) =>
        $"http://{FormatHost(host)}:{httpPort}/ISAPI";

    /// <summary>Wraps a bare IPv6 literal in brackets so it is valid inside a URL authority.</summary>
    public static string FormatHost(string host)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        return host.Contains(':') && !host.StartsWith('[') ? $"[{host}]" : host;
    }

    /// <summary>Redacts credentials so a URL can be written to a log file safely.</summary>
    public static string Redact(string url)
    {
        var schemeEnd = url.IndexOf("://", StringComparison.Ordinal);
        if (schemeEnd < 0) return url;

        var authorityStart = schemeEnd + 3;
        var at = url.IndexOf('@', authorityStart);
        if (at < 0) return url;

        var slash = url.IndexOf('/', authorityStart);
        if (slash >= 0 && at > slash) return url;

        return new StringBuilder(url, 0, authorityStart, url.Length)
            .Append("***:***")
            .Append(url, at, url.Length - at)
            .ToString();
    }
}
