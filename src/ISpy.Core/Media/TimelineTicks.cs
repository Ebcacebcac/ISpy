namespace ISpy.Core.Media;

/// <summary>Tick spacing for the scrub bar.</summary>
public static class TimelineTicks
{
    /// <summary>Round intervals a person reads easily; anything else makes the bar hard to scan.</summary>
    private static readonly TimeSpan[] Candidates =
    [
        TimeSpan.FromMinutes(1),
        TimeSpan.FromMinutes(5),
        TimeSpan.FromMinutes(15),
        TimeSpan.FromMinutes(30),
        TimeSpan.FromHours(1),
        TimeSpan.FromHours(2),
        TimeSpan.FromHours(3),
        TimeSpan.FromHours(6),
        TimeSpan.FromHours(12),
    ];

    /// <summary>
    /// Smallest round interval that keeps the tick count at or below <paramref name="targetTicks"/>,
    /// so labels never collide as the window is zoomed.
    /// </summary>
    public static TimeSpan ChooseStep(TimeSpan window, int targetTicks)
    {
        if (window <= TimeSpan.Zero) return TimeSpan.FromHours(1);

        var ideal = window / Math.Max(1, targetTicks);
        return Candidates.FirstOrDefault(candidate => candidate >= ideal, TimeSpan.FromHours(24));
    }
}
