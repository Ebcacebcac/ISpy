using ISpy.Core.Model;

namespace ISpy.Core.Discovery;

/// <summary>A device found on the LAN, before the user has supplied credentials for it.</summary>
public sealed record DiscoveredDevice
{
    public required string Host { get; init; }
    public int HttpPort { get; init; } = 80;
    public int RtspPort { get; init; } = 554;
    public string? Model { get; init; }
    public string? SerialNumber { get; init; }
    public string? FirmwareVersion { get; init; }
    public string? MacAddress { get; init; }

    /// <summary>False when the device is still in Hikvision's factory "not activated" state.</summary>
    public bool? IsActivated { get; init; }

    public DiscoverySource Source { get; init; }

    public string Id => Device.MakeId(SerialNumber, Host);

    /// <summary>Best available human label, falling back to the address.</summary>
    public string DisplayName => Model ?? Host;

    public Device ToDevice() => new()
    {
        Id = Id,
        Host = Host,
        DisplayName = DisplayName,
        HttpPort = HttpPort,
        RtspPort = RtspPort,
        Model = Model,
        SerialNumber = SerialNumber,
        FirmwareVersion = FirmwareVersion,
        Source = Source,
        LastSeenUtc = DateTimeOffset.UtcNow,
    };
}
