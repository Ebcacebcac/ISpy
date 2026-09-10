using ISpy.Core.Model;
using ISpy.Core.Protocol;
using Xunit;

namespace ISpy.Tests;

public class HikvisionUrlTests
{
    [Theory]
    [InlineData(1, StreamProfile.Main, 101)]
    [InlineData(1, StreamProfile.Sub, 102)]
    [InlineData(9, StreamProfile.Sub, 902)]
    [InlineData(10, StreamProfile.Main, 1001)]
    [InlineData(16, StreamProfile.Sub, 1602)]
    public void StreamId_encodes_channel_and_profile(int channel, StreamProfile profile, int expected) =>
        Assert.Equal(expected, HikvisionUrls.StreamId(channel, profile));

    [Fact]
    public void StreamId_rejects_channel_zero() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => HikvisionUrls.StreamId(0, StreamProfile.Main));

    [Fact]
    public void Live_builds_substream_url_for_grid_tiles() =>
        Assert.Equal(
            "rtsp://192.168.1.64:554/Streaming/Channels/302",
            HikvisionUrls.Live("192.168.1.64", 554, 3, StreamProfile.Sub));

    [Fact]
    public void Playback_encodes_utc_window()
    {
        var start = new DateTimeOffset(2026, 9, 10, 14, 30, 0, TimeSpan.Zero);
        var end = start.AddHours(1);

        Assert.Equal(
            "rtsp://nvr.local:554/Streaming/tracks/101" +
            "?starttime=20260910T143000Z&endtime=20260910T153000Z",
            HikvisionUrls.Playback("nvr.local", 554, 1, start, end));
    }

    [Fact]
    public void Playback_converts_local_time_to_utc()
    {
        var start = new DateTimeOffset(2026, 9, 10, 14, 30, 0, TimeSpan.FromHours(2));
        var url = HikvisionUrls.Playback("nvr", 554, 1, start, start.AddMinutes(5));

        Assert.Contains("starttime=20260910T123000Z", url);
    }

    [Fact]
    public void Playback_rejects_inverted_window()
    {
        var start = DateTimeOffset.UtcNow;
        Assert.Throws<ArgumentException>(() => HikvisionUrls.Playback("nvr", 554, 1, start, start));
    }

    [Fact]
    public void Time_round_trips()
    {
        var value = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);
        Assert.Equal(value, HikvisionUrls.ParseTime(HikvisionUrls.FormatTime(value)));
    }

    [Fact]
    public void WithCredentials_escapes_password_punctuation()
    {
        // A password containing '@' and ':' would otherwise break the URL authority apart.
        var url = HikvisionUrls.WithCredentials(
            "rtsp://192.168.1.64:554/Streaming/Channels/101", "admin", "p@ss:w/rd");

        Assert.Equal(
            "rtsp://admin:p%40ss%3Aw%2Frd@192.168.1.64:554/Streaming/Channels/101", url);
    }

    [Fact]
    public void WithCredentials_rejects_non_rtsp() =>
        Assert.Throws<ArgumentException>(() =>
            HikvisionUrls.WithCredentials("http://host/x", "a", "b"));

    [Fact]
    public void IPv6_hosts_are_bracketed() =>
        Assert.Equal(
            "rtsp://[fe80::1]:554/Streaming/Channels/101",
            HikvisionUrls.Live("fe80::1", 554, 1, StreamProfile.Main));

    [Fact]
    public void Redact_hides_credentials_for_logging() =>
        Assert.Equal(
            "rtsp://***:***@192.168.1.64:554/Streaming/Channels/101",
            HikvisionUrls.Redact("rtsp://admin:hunter2@192.168.1.64:554/Streaming/Channels/101"));

    [Fact]
    public void Redact_leaves_credential_free_urls_alone()
    {
        const string url = "rtsp://192.168.1.64:554/Streaming/Channels/101";
        Assert.Equal(url, HikvisionUrls.Redact(url));
    }

    [Fact]
    public void Redact_ignores_an_at_sign_in_the_path()
    {
        const string url = "rtsp://192.168.1.64:554/Streaming/tracks/101?name=a@b";
        Assert.Equal(url, HikvisionUrls.Redact(url));
    }
}
