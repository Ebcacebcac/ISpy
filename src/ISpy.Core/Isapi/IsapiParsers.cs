using ISpy.Core.Model;
using ISpy.Core.Protocol;

namespace ISpy.Core.Isapi;

/// <summary>Identity reported by <c>/ISAPI/System/deviceInfo</c>.</summary>
public sealed record DeviceInfo
{
    public string? DeviceName { get; init; }
    public string? Model { get; init; }
    public string? SerialNumber { get; init; }
    public string? FirmwareVersion { get; init; }
    public string? MacAddress { get; init; }
}

/// <summary>
/// Parsers for the ISAPI responses ISpy depends on. Kept free of I/O so they can be tested against
/// captured payloads from real firmware, which is where the OEM variation actually lives.
/// </summary>
public static class IsapiParsers
{
    public static DeviceInfo? ParseDeviceInfo(string xml)
    {
        var root = Xml.TryParse(xml);
        if (root is null) return null;

        return new DeviceInfo
        {
            DeviceName = root.Value("deviceName"),
            Model = root.Value("model"),
            SerialNumber = root.Value("serialNumber"),
            FirmwareVersion = root.Value("firmwareVersion"),
            MacAddress = Discovery.SadpMessages.NormalizeMac(root.Value("macAddress")),
        };
    }

    /// <summary>
    /// Turns <c>/ISAPI/Streaming/channels</c> into one <see cref="Channel"/> per camera.
    /// </summary>
    /// <remarks>
    /// The response lists one entry per *stream*, not per camera: ids 101 and 102 are the main and
    /// sub streams of camera 1. We fold them back together so the grid sees cameras, and record the
    /// codec of each profile because that decides whether the tile needs an H.265 decoder.
    /// </remarks>
    public static IReadOnlyList<Channel> ParseStreamingChannels(string deviceId, string xml)
    {
        var root = Xml.TryParse(xml);
        if (root is null) return [];

        var byChannel = new SortedDictionary<int, Channel>();

        foreach (var entry in root.DescendantsLocal("StreamingChannel"))
        {
            var streamId = entry.Int("id");
            if (streamId is null or < 100) continue;

            var channelNumber = streamId.Value / 100;
            var profile = streamId.Value % 100;
            var codec = entry.Value("videoCodecType");

            byChannel.TryGetValue(channelNumber, out var channel);
            channel ??= new Channel { DeviceId = deviceId, Number = channelNumber };

            // A device that reports the channel disabled still lists it; keep it but mark it, so the
            // user can see the camera exists rather than wondering why it vanished.
            var enabled = entry.Bool("enabled");

            byChannel[channelNumber] = channel with
            {
                Name = FirstNonEmpty(channel.Name, entry.Value("channelName")),
                MainCodec = profile == (int)StreamProfile.Main ? codec : channel.MainCodec,
                SubCodec = profile == (int)StreamProfile.Sub ? codec : channel.SubCodec,
                Enabled = profile == (int)StreamProfile.Main ? enabled ?? channel.Enabled : channel.Enabled,
            };
        }

        return byChannel.Values.ToArray();
    }

    /// <summary>
    /// Names from <c>/ISAPI/ContentMgmt/InputProxy/channels</c>, which an NVR uses for the cameras
    /// plugged into it. Some firmware leaves channelName blank in the streaming response but fills
    /// it in here, so this is used to fill the gaps rather than replace what we already have.
    /// </summary>
    public static IReadOnlyDictionary<int, string> ParseInputProxyNames(string xml)
    {
        var root = Xml.TryParse(xml);
        if (root is null) return new Dictionary<int, string>();

        var names = new Dictionary<int, string>();

        foreach (var entry in root.DescendantsLocal("InputProxyChannel"))
        {
            var id = entry.Int("id");
            var name = entry.Value("name");
            if (id is > 0 && !string.IsNullOrWhiteSpace(name)) names[id.Value] = name;
        }

        return names;
    }

    /// <summary>Channel numbers that expose PTZ control.</summary>
    public static IReadOnlySet<int> ParsePtzChannels(string xml)
    {
        var root = Xml.TryParse(xml);
        if (root is null) return new HashSet<int>();

        var channels = new HashSet<int>();

        foreach (var entry in root.DescendantsLocal("PTZChannel"))
        {
            if (entry.Int("id") is > 0 and var id) channels.Add(id);
        }

        return channels;
    }

    /// <summary>
    /// Merges supplementary names and PTZ capability into a channel list.
    /// </summary>
    public static IReadOnlyList<Channel> Enrich(
        IReadOnlyList<Channel> channels,
        IReadOnlyDictionary<int, string>? names = null,
        IReadOnlySet<int>? ptzChannels = null) =>
        channels
            .Select(channel => channel with
            {
                Name = FirstNonEmpty(
                    channel.Name,
                    names is not null && names.TryGetValue(channel.Number, out var name) ? name : null),
                SupportsPtz = ptzChannels?.Contains(channel.Number) ?? channel.SupportsPtz,
            })
            .ToArray();

    private static string FirstNonEmpty(string? a, string? b) =>
        !string.IsNullOrWhiteSpace(a) ? a : b ?? "";
}
