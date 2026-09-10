using System.Diagnostics;
using System.Text;

namespace ISpy.Core;

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

    /// <summary>Clears recorded marks. For tests.</summary>
    public static void Reset()
    {
        lock (Gate) Marks.Clear();
    }

    /// <summary>
    /// Stages that missed their budget, so a regression shows up in the log rather than only being
    /// noticed as "it feels slower than it used to".
    /// </summary>
    public static IReadOnlyList<string> BudgetBreaches(
        IReadOnlyList<(string Stage, TimeSpan At)> marks)
    {
        var breaches = new List<string>();

        foreach (var (stage, at) in marks)
        {
            var budget = BudgetFor(stage);
            if (budget is null || at <= budget) continue;

            breaches.Add(
                $"{stage} took {at.TotalMilliseconds:F0}ms, over its {budget.Value.TotalMilliseconds:F0}ms budget");
        }

        return breaches;
    }

    /// <summary>The budget a stage is held to, or null when it is only recorded for context.</summary>
    public static TimeSpan? BudgetFor(string stage) => stage switch
    {
        "window shown" => WindowVisibleBudget,
        "first frame" => FirstFrameBudget,
        _ => null,
    };

    /// <summary>Appends the run's timings to the log directory. Best effort - never throws.</summary>
    public static void Flush()
    {
        try
        {
            var report = new StringBuilder()
                .Append("startup ").Append(DateTimeOffset.UtcNow.ToString("O")).AppendLine();

            var marks = Snapshot();

            foreach (var (stage, at) in marks)
                report.Append("  ").Append(at.TotalMilliseconds.ToString("F1"))
                      .Append("ms  ").AppendLine(stage);

            foreach (var breach in BudgetBreaches(marks))
                report.Append("  OVER BUDGET: ").AppendLine(breach);

            AppPaths.EnsureCreated();
            File.AppendAllText(Path.Combine(AppPaths.LogDirectory, "startup.log"), report.ToString());
        }
        catch (Exception)
        {
            // Diagnostics must never be able to break the app.
        }
    }
}
