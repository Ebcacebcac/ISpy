namespace ISpy.Core.Model;

/// <summary>Why the recorder was writing during a segment. Drives the timeline's colour coding.</summary>
public enum RecordingTrigger
{
    Unknown = 0,
    Continuous = 1,
    Motion = 2,
    Alarm = 3,
    Manual = 4,
}

/// <summary>A span of footage held on the recorder for one camera.</summary>
public sealed record RecordingSegment
{
    public required DateTimeOffset Start { get; init; }
    public required DateTimeOffset End { get; init; }
    public RecordingTrigger Trigger { get; init; } = RecordingTrigger.Unknown;

    /// <summary>Device-side handle used to download the original file, when the device gave one.</summary>
    public string? PlaybackUri { get; init; }

    public long? SizeBytes { get; init; }

    public TimeSpan Duration => End - Start;

    public bool Contains(DateTimeOffset moment) => moment >= Start && moment < End;
}
