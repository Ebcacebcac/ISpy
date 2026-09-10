namespace ISpy.Core.Discovery;

/// <summary>
/// Finds recorders and cameras on the local network by running SADP and ONVIF probes together and
/// merging what comes back.
/// </summary>
public sealed class LanDiscovery
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(3);

    /// <summary>
    /// Runs both probes concurrently. <paramref name="onFound"/> fires as each device answers so the
    /// UI can fill in live rather than waiting for the whole window to elapse; it may be raised from
    /// a background thread.
    /// </summary>
    public async Task<IReadOnlyList<DiscoveredDevice>> DiscoverAsync(
        TimeSpan? timeout = null,
        Action<DiscoveredDevice>? onFound = null,
        CancellationToken cancellationToken = default)
    {
        var window = timeout ?? DefaultTimeout;
        var found = new Dictionary<string, DiscoveredDevice>(StringComparer.OrdinalIgnoreCase);
        var gate = new Lock();

        void Accept(DiscoveredDevice device)
        {
            bool isNew;
            DiscoveredDevice merged;

            lock (gate)
            {
                isNew = !found.TryGetValue(device.Id, out var existing);
                merged = isNew ? device : Merge(existing!, device);
                found[device.Id] = merged;
            }

            if (isNew) onFound?.Invoke(merged);
        }

        var sadp = UdpProbe.BroadcastAsync(
            SadpMessages.MulticastAddress, SadpMessages.Port,
            SadpMessages.BuildProbe(Guid.NewGuid()), window,
            reply =>
            {
                if (SadpMessages.ParseProbeMatch(reply.Payload) is { } device) Accept(device);
            },
            cancellationToken);

        var onvif = UdpProbe.BroadcastAsync(
            OnvifMessages.MulticastAddress, OnvifMessages.Port,
            OnvifMessages.BuildProbe(Guid.NewGuid()), window,
            reply =>
            {
                foreach (var device in OnvifMessages.ParseProbeMatches(reply.Payload)) Accept(device);
            },
            cancellationToken);

        await Task.WhenAll(sadp, onvif).ConfigureAwait(false);

        lock (gate)
        {
            return found.Values
                .OrderBy(d => d.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
    }

    /// <summary>
    /// Combines two sightings of one device. SADP wins on identity fields because it reports the
    /// real serial and firmware, while ONVIF scopes are often a generic model string.
    /// </summary>
    public static DiscoveredDevice Merge(DiscoveredDevice existing, DiscoveredDevice update) =>
        existing with
        {
            Model = Prefer(existing.Model, update.Model),
            SerialNumber = Prefer(existing.SerialNumber, update.SerialNumber),
            FirmwareVersion = Prefer(existing.FirmwareVersion, update.FirmwareVersion),
            MacAddress = Prefer(existing.MacAddress, update.MacAddress),
            IsActivated = existing.IsActivated ?? update.IsActivated,
            Source = existing.Source == Model.DiscoverySource.Sadp ? existing.Source : update.Source,
        };

    private static string? Prefer(string? existing, string? update) =>
        string.IsNullOrWhiteSpace(existing) ? update : existing;
}
