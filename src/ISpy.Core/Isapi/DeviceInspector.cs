using ISpy.Core.Model;

namespace ISpy.Core.Isapi;

/// <summary>What a device told us about itself once we could authenticate.</summary>
public sealed record DeviceInspection(DeviceInfo? Info, IReadOnlyList<Channel> Channels);

/// <summary>
/// Asks a device who it is and what cameras it has.
/// </summary>
public sealed class DeviceInspector
{
    /// <summary>
    /// Enumerates the device's cameras.
    /// </summary>
    /// <remarks>
    /// Only <c>/ISAPI/Streaming/channels</c> is required - it is present on every device in this
    /// family and yields camera numbers, names and per-profile codecs in a single request. The
    /// InputProxy and PTZ endpoints are best-effort: an NVR has them, a standalone camera does not,
    /// and a missing one is information rather than a failure.
    /// </remarks>
    public async Task<DeviceInspection> InspectAsync(
        IsapiClient client, string deviceId, CancellationToken cancellationToken = default)
    {
        // Fired together: four sequential round trips to a busy recorder is a visible delay,
        // and only the streaming list is allowed to fail the whole inspection.
        var infoTask = client.TryGetAsync("/System/deviceInfo", cancellationToken);
        var streamsTask = client.GetAsync("/Streaming/channels", cancellationToken);
        var proxyTask = client.TryGetAsync("/ContentMgmt/InputProxy/channels", cancellationToken);
        var ptzTask = client.TryGetAsync("/PTZCtrl/channels", cancellationToken);

        var streamsXml = await streamsTask.ConfigureAwait(false);
        var infoXml = await infoTask.ConfigureAwait(false);
        var proxyXml = await proxyTask.ConfigureAwait(false);
        var ptzXml = await ptzTask.ConfigureAwait(false);

        var channels = IsapiParsers.Enrich(
            IsapiParsers.ParseStreamingChannels(deviceId, streamsXml),
            proxyXml is null ? null : IsapiParsers.ParseInputProxyNames(proxyXml),
            ptzXml is null ? null : IsapiParsers.ParsePtzChannels(ptzXml));

        return new DeviceInspection(
            infoXml is null ? null : IsapiParsers.ParseDeviceInfo(infoXml), channels);
    }
}
