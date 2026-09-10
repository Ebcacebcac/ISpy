using ISpy.Core.Model;

namespace ISpy.Core.Media;

/// <summary>
/// The recorded footage for one camera over a window of time, and the arithmetic the timeline UI
/// needs to draw and seek within it.
/// </summary>
public sealed class RecordingTimeline
{
    private readonly List<RecordingSegment> _segments;

    public RecordingTimeline(
        DateTimeOffset windowStart, DateTimeOffset windowEnd, IEnumerable<RecordingSegment> segments)
    {
        if (windowEnd <= windowStart)
            throw new ArgumentException("Timeline end must be after start.", nameof(windowEnd));

        WindowStart = windowStart;
        WindowEnd = windowEnd;
        _segments = Normalize(segments, windowStart, windowEnd);
    }

    public DateTimeOffset WindowStart { get; }
    public DateTimeOffset WindowEnd { get; }
    public TimeSpan WindowDuration => WindowEnd - WindowStart;

    public IReadOnlyList<RecordingSegment> Segments => _segments;

    public bool IsEmpty => _segments.Count == 0;

    /// <summary>Total footage available in the window, gaps excluded.</summary>
    public TimeSpan RecordedDuration =>
        _segments.Aggregate(TimeSpan.Zero, (total, segment) => total + segment.Duration);

    /// <summary>
    /// Clips segments to the window, drops anything outside it, and merges overlapping or touching
    /// spans of the same trigger.
    /// </summary>
    /// <remarks>
    /// Recorders routinely return segments that overlap by a second or that abut exactly, one per
    /// underlying file. Drawn raw they produce a striped timeline with hairline gaps that look like
    /// missing footage; merged, the bar reads as the continuous recording it actually is.
    /// </remarks>
    public static List<RecordingSegment> Normalize(
        IEnumerable<RecordingSegment> segments, DateTimeOffset windowStart, DateTimeOffset windowEnd)
    {
        var clipped = segments
            .Select(segment => segment with
            {
                Start = segment.Start < windowStart ? windowStart : segment.Start,
                End = segment.End > windowEnd ? windowEnd : segment.End,
            })
            .Where(segment => segment.End > segment.Start)
            .OrderBy(segment => segment.Start)
            .ToList();

        var merged = new List<RecordingSegment>();

        foreach (var segment in clipped)
        {
            if (merged.Count > 0)
            {
                var previous = merged[^1];

                if (segment.Start <= previous.End && segment.Trigger == previous.Trigger)
                {
                    merged[^1] = previous with
                    {
                        End = segment.End > previous.End ? segment.End : previous.End,

                        // The merged span is no longer one file, so a single download handle would
                        // be misleading.
                        PlaybackUri = null,
                        SizeBytes = null,
                    };

                    continue;
                }
            }

            merged.Add(segment);
        }

        return merged;
    }

    /// <summary>Fraction along the window, 0 to 1, for a moment in time.</summary>
    public double PositionOf(DateTimeOffset moment) =>
        Math.Clamp((moment - WindowStart) / WindowDuration, 0, 1);

    /// <summary>The moment at a fraction along the window.</summary>
    public DateTimeOffset MomentAt(double position) =>
        WindowStart + WindowDuration * Math.Clamp(position, 0, 1);

    public bool HasFootageAt(DateTimeOffset moment) =>
        _segments.Any(segment => segment.Contains(moment));

    /// <summary>
    /// The nearest moment that actually has footage, so clicking a gap plays the closest recording
    /// rather than dropping the user into silence.
    /// </summary>
    public DateTimeOffset? SnapToFootage(DateTimeOffset moment)
    {
        if (_segments.Count == 0) return null;
        if (HasFootageAt(moment)) return moment;

        DateTimeOffset? best = null;
        var bestDistance = TimeSpan.MaxValue;

        foreach (var segment in _segments)
        {
            // The end of a segment is exclusive, so the last playable instant is just inside it.
            var candidate = moment < segment.Start
                ? segment.Start
                : segment.End - TimeSpan.FromSeconds(1);

            var distance = candidate > moment ? candidate - moment : moment - candidate;

            if (distance >= bestDistance) continue;

            bestDistance = distance;
            best = candidate;
        }

        return best;
    }

    /// <summary>The segment covering a moment, if any.</summary>
    public RecordingSegment? SegmentAt(DateTimeOffset moment) =>
        _segments.FirstOrDefault(segment => segment.Contains(moment));

    /// <summary>Stretches of the window with no recording, which the UI draws as empty.</summary>
    public IReadOnlyList<(DateTimeOffset Start, DateTimeOffset End)> Gaps()
    {
        var gaps = new List<(DateTimeOffset, DateTimeOffset)>();
        var cursor = WindowStart;

        foreach (var segment in _segments)
        {
            if (segment.Start > cursor) gaps.Add((cursor, segment.Start));
            if (segment.End > cursor) cursor = segment.End;
        }

        if (cursor < WindowEnd) gaps.Add((cursor, WindowEnd));

        return gaps;
    }
}
