namespace ISpy.Core.Model;

/// <summary>How a device was discovered. Kept so the UI can explain why a device is in the list.</summary>
public enum DiscoverySource
{
    Manual = 0,
    Sadp = 1,
    Onvif = 2,
}

/// <summary>
/// An NVR/DVR (or a standalone camera, which we model as a device with a single channel).
/// The password is never held here - it lives encrypted in the vault, keyed by <see cref="Id"/>.
/// </summary>
public sealed record Device
{
    public required string Id { get; init; }
    public required string Host { get; init; }
    public string DisplayName { get; init; } = "";
    public int HttpPort { get; init; } = 80;
    public int RtspPort { get; init; } = 554;
    public string? Model { get; init; }
    public string? SerialNumber { get; init; }
    public string? FirmwareVersion { get; init; }
    public string? Username { get; init; }
    public DiscoverySource Source { get; init; } = DiscoverySource.Manual;
    public DateTimeOffset AddedUtc { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LastSeenUtc { get; init; }

    /// <summary>Stable id for a discovered device: serial when the device reports one, else host.</summary>
    public static string MakeId(string? serialNumber, string host) =>
        string.IsNullOrWhiteSpace(serialNumber) ? $"host:{host}" : $"sn:{serialNumber.Trim()}";
}
