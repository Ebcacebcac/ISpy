using ISpy.Core.Isapi;
using ISpy.Core.Model;
using Xunit;

namespace ISpy.Tests;

public class IsapiParserTests
{
    // Real firmware namespaces the response; the parsers must not depend on which one.
    private const string StreamingChannels = """
        <?xml version="1.0" encoding="UTF-8"?>
        <StreamingChannelList xmlns="http://www.hikvision.com/ver20/XMLSchema">
          <StreamingChannel>
            <id>101</id>
            <channelName>Driveway</channelName>
            <enabled>true</enabled>
            <Video><videoCodecType>H.265</videoCodecType><videoResolutionWidth>2688</videoResolutionWidth></Video>
          </StreamingChannel>
          <StreamingChannel>
            <id>102</id>
            <channelName>Driveway</channelName>
            <enabled>true</enabled>
            <Video><videoCodecType>H.264</videoCodecType><videoResolutionWidth>704</videoResolutionWidth></Video>
          </StreamingChannel>
          <StreamingChannel>
            <id>201</id>
            <channelName></channelName>
            <enabled>true</enabled>
            <Video><videoCodecType>H.264</videoCodecType></Video>
          </StreamingChannel>
          <StreamingChannel>
            <id>202</id>
            <channelName></channelName>
            <enabled>true</enabled>
            <Video><videoCodecType>H.264</videoCodecType></Video>
          </StreamingChannel>
        </StreamingChannelList>
        """;

    [Fact]
    public void Stream_entries_fold_into_one_channel_per_camera()
    {
        var channels = IsapiParsers.ParseStreamingChannels("dev", StreamingChannels);

        Assert.Equal(2, channels.Count);
        Assert.Equal([1, 2], channels.Select(c => c.Number));
    }

    [Fact]
    public void Main_and_sub_codecs_are_recorded_separately()
    {
        var channel = IsapiParsers.ParseStreamingChannels("dev", StreamingChannels)[0];

        Assert.Equal("Driveway", channel.Name);
        Assert.Equal("H.265", channel.MainCodec);
        Assert.Equal("H.264", channel.SubCodec);
    }

    [Fact]
    public void Parser_ignores_the_xml_namespace()
    {
        var bare = StreamingChannels.Replace(" xmlns=\"http://www.hikvision.com/ver20/XMLSchema\"", "");
        var psia = StreamingChannels.Replace(
            "http://www.hikvision.com/ver20/XMLSchema", "urn:psialliance-org");

        Assert.Equal(2, IsapiParsers.ParseStreamingChannels("dev", bare).Count);
        Assert.Equal(2, IsapiParsers.ParseStreamingChannels("dev", psia).Count);
    }

    [Fact]
    public void Double_digit_channels_are_decoded()
    {
        const string xml = """
            <StreamingChannelList>
              <StreamingChannel><id>1201</id><channelName>Shed</channelName></StreamingChannel>
              <StreamingChannel><id>1202</id><channelName>Shed</channelName></StreamingChannel>
            </StreamingChannelList>
            """;

        var channel = Assert.Single(IsapiParsers.ParseStreamingChannels("dev", xml));
        Assert.Equal(12, channel.Number);
    }

    [Fact]
    public void Disabled_channel_is_kept_but_flagged()
    {
        const string xml = """
            <StreamingChannelList>
              <StreamingChannel><id>101</id><channelName>Live</channelName><enabled>true</enabled></StreamingChannel>
              <StreamingChannel><id>201</id><channelName>Unplugged</channelName><enabled>false</enabled></StreamingChannel>
            </StreamingChannelList>
            """;

        var channels = IsapiParsers.ParseStreamingChannels("dev", xml);

        Assert.True(channels.Single(c => c.Number == 1).Enabled);
        Assert.False(channels.Single(c => c.Number == 2).Enabled);
    }

    [Fact]
    public void Nameless_channel_gets_a_fallback_display_name()
    {
        var channel = IsapiParsers.ParseStreamingChannels("dev", StreamingChannels)[1];

        Assert.Equal("", channel.Name);
        Assert.Equal("Camera 2", channel.DisplayName);
    }

    [Fact]
    public void Malformed_or_empty_responses_yield_nothing()
    {
        Assert.Empty(IsapiParsers.ParseStreamingChannels("dev", "<broken"));
        Assert.Empty(IsapiParsers.ParseStreamingChannels("dev", ""));
        Assert.Empty(IsapiParsers.ParseStreamingChannels("dev", "<StreamingChannelList/>"));
    }

    [Fact]
    public void Nonsense_stream_ids_are_skipped()
    {
        const string xml = """
            <StreamingChannelList>
              <StreamingChannel><id>0</id></StreamingChannel>
              <StreamingChannel><id>abc</id></StreamingChannel>
              <StreamingChannel><id>101</id><channelName>Good</channelName></StreamingChannel>
            </StreamingChannelList>
            """;

        var channel = Assert.Single(IsapiParsers.ParseStreamingChannels("dev", xml));
        Assert.Equal("Good", channel.Name);
    }

    [Fact]
    public void InputProxy_supplies_names_the_streaming_response_left_blank()
    {
        const string proxy = """
            <InputProxyChannelList xmlns="http://www.hikvision.com/ver20/XMLSchema">
              <InputProxyChannel><id>1</id><name>Should Not Win</name></InputProxyChannel>
              <InputProxyChannel><id>2</id><name>Back Door</name></InputProxyChannel>
            </InputProxyChannelList>
            """;

        var channels = IsapiParsers.Enrich(
            IsapiParsers.ParseStreamingChannels("dev", StreamingChannels),
            IsapiParsers.ParseInputProxyNames(proxy));

        // Channel 1 already had a name from the streaming response and keeps it.
        Assert.Equal("Driveway", channels[0].Name);
        Assert.Equal("Back Door", channels[1].Name);
    }

    [Fact]
    public void Ptz_capability_is_applied_per_channel()
    {
        const string ptz = """
            <PTZChannelList><PTZChannel><id>2</id></PTZChannel></PTZChannelList>
            """;

        var channels = IsapiParsers.Enrich(
            IsapiParsers.ParseStreamingChannels("dev", StreamingChannels),
            ptzChannels: IsapiParsers.ParsePtzChannels(ptz));

        Assert.False(channels[0].SupportsPtz);
        Assert.True(channels[1].SupportsPtz);
    }

    [Fact]
    public void Enrich_with_nothing_available_leaves_channels_untouched()
    {
        var original = IsapiParsers.ParseStreamingChannels("dev", StreamingChannels);
        var enriched = IsapiParsers.Enrich(original);

        Assert.Equal(original.Select(c => c.Name), enriched.Select(c => c.Name));
    }

    [Fact]
    public void DeviceInfo_is_parsed()
    {
        const string xml = """
            <DeviceInfo xmlns="http://www.hikvision.com/ver20/XMLSchema">
              <deviceName>Embedded Net DVR</deviceName>
              <model>DS-7608NI-K2/8P</model>
              <serialNumber>DS-7608NI-K20120190101CCRRJ123</serialNumber>
              <macAddress>44-19-b6-1a-2b-3c</macAddress>
              <firmwareVersion>V4.30.085</firmwareVersion>
            </DeviceInfo>
            """;

        var info = IsapiParsers.ParseDeviceInfo(xml);

        Assert.NotNull(info);
        Assert.Equal("DS-7608NI-K2/8P", info.Model);
        Assert.Equal("V4.30.085", info.FirmwareVersion);
        Assert.Equal("44:19:B6:1A:2B:3C", info.MacAddress);
    }

    [Fact]
    public void DeviceInfo_survives_missing_fields()
    {
        var info = IsapiParsers.ParseDeviceInfo("<DeviceInfo><model>X</model></DeviceInfo>");

        Assert.NotNull(info);
        Assert.Equal("X", info.Model);
        Assert.Null(info.SerialNumber);
    }
}
