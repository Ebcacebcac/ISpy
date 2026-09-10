namespace ISpy.Core.Model;

/// <summary>Which encoded stream to pull. Grid tiles use Sub; a maximized tile switches to Main.</summary>
public enum StreamProfile
{
    Main = 1,
    Sub = 2,
    Third = 3,
}

/// <summary>One camera on a device. For an NVR this is a channel; for a standalone camera, channel 1.</summary>
public sealed record Channel
{
    public required string DeviceId { get; init; }

    /// <summary>1-based channel number as the device numbers it.</summary>
    public required int Number { get; init; }

    public string Name { get; init; } = "";
    public bool Enabled { get; init; } = true;
    public bool SupportsPtz { get; init; }
    public string? MainCodec { get; init; }
    public string? SubCodec { get; init; }

    /// <summary>True when the device reported this channel has no camera attached.</summary>
    public bool IsOffline { get; init; }

    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? $"Camera {Number}" : Name;
}
