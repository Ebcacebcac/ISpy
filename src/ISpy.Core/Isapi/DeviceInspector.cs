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
    /// <c>/ISAPI/Streaming/channels</c> is preferred - it yields camera numbers, names and
    /// per-profile codecs in a single request. It is not universal though: 2016-2017 NVR firmware
    /// answers it with 403 notSupport while happily serving the InputProxy camera list, so when the
    /// streaming list is unavailable the channels are built from InputProxy instead. Every endpoint
    /// here is fetched with TryGet: an unsupported endpoint is information, while a genuine
    /// authentication failure still propagates because TryGet only swallows non-auth errors.
    /// </remarks>
    public async Task<DeviceInspection> InspectAsync(
        IsapiClient client, string deviceId, CancellationToken cancellationToken = default)
    {
        // Fired together: four sequential round trips to a busy recorder is a visible delay.
        var infoTask = client.TryGetAsync("/System/deviceInfo", cancellationToken);
        var streamsTask = client.TryGetAsync("/Streaming/channels", cancellationToken);
        var proxyTask = client.TryGetAsync("/ContentMgmt/InputProxy/channels", cancellationToken);
        var ptzTask = client.TryGetAsync("/PTZCtrl/channels", cancellationToken);

        // WhenAll observes every task, so a wrong password - which faults all four at once with
        // an authentication exception - surfaces one clean failure instead of leaving stragglers
        // unobserved. TryGet already swallowed everything that is not an auth failure.
        await Task.WhenAll(infoTask, streamsTask, proxyTask, ptzTask).ConfigureAwait(false);

        var streamsXml = await streamsTask.ConfigureAwait(false);
        var infoXml = await infoTask.ConfigureAwait(false);
        var proxyXml = await proxyTask.ConfigureAwait(false);
        var ptzXml = await ptzTask.ConfigureAwait(false);

        var proxyNames = proxyXml is null ? null : IsapiParsers.ParseInputProxyNames(proxyXml);

        var channels = streamsXml is null
            ? []
            : IsapiParsers.ParseStreamingChannels(deviceId, streamsXml);

        if (channels.Count == 0 && proxyNames is { Count: > 0 })
            channels = IsapiParsers.FromInputProxyNames(deviceId, proxyNames);

        channels = IsapiParsers.Enrich(
            channels,
            proxyNames,
            ptzXml is null ? null : IsapiParsers.ParsePtzChannels(ptzXml));

        return new DeviceInspection(
            infoXml is null ? null : IsapiParsers.ParseDeviceInfo(infoXml), channels);
    }
}
