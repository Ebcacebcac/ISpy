using System.IO;
using System.Diagnostics;
using System.Text;
using ISpy.Core;

namespace ISpy.App;

/// <summary>
/// Records how long each stage of startup took, measured from process start rather than from
/// managed entry so that runtime and assembly-load time is included and cannot hide.
/// Phase 6 asserts against these numbers; until then they are recorded so regressions are visible.
/// </summary>
public static class StartupTimeline
{
    /// <summary>The window must be on screen within this budget.</summary>
    public static readonly TimeSpan WindowVisibleBudget = TimeSpan.FromMilliseconds(300);

    /// <summary>The first live video frame must be drawn within this budget.</summary>
    public static readonly TimeSpan FirstFrameBudget = TimeSpan.FromMilliseconds(1500);

    private static readonly List<(string Stage, TimeSpan At)> Marks = [];
    private static readonly Lock Gate = new();
    private static readonly DateTime ProcessStart = ResolveProcessStart();

    private static DateTime ResolveProcessStart()
    {
        try
        {
            return Process.GetCurrentProcess().StartTime.ToUniversalTime();
        }
        catch
        {
            // Some hardened environments deny StartTime; fall back to now so we still get deltas.
            return DateTime.UtcNow;
        }
    }

    public static TimeSpan Elapsed => DateTime.UtcNow - ProcessStart;

    public static void Mark(string stage)
    {
        lock (Gate) Marks.Add((stage, Elapsed));
    }

    public static IReadOnlyList<(string Stage, TimeSpan At)> Snapshot()
    {
        lock (Gate) return Marks.ToArray();
    }

    /// <summary>Appends the run's timings to the log directory. Best effort - never throws.</summary>
    public static void Flush()
    {
        try
        {
            var report = new StringBuilder()
                .Append("startup ").Append(DateTimeOffset.UtcNow.ToString("O")).AppendLine();

            foreach (var (stage, at) in Snapshot())
                report.Append("  ").Append(at.TotalMilliseconds.ToString("F1"))
                      .Append("ms  ").AppendLine(stage);

            AppPaths.EnsureCreated();
            File.AppendAllText(Path.Combine(AppPaths.LogDirectory, "startup.log"), report.ToString());
        }
        catch (Exception)
        {
            // Diagnostics must never be able to break the app.
        }
    }
}
