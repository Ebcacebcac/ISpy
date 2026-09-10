using ISpy.Core;
using Xunit;

namespace ISpy.Tests;

public class StartupTimelineTests
{
    [Fact]
    public void The_budgets_are_the_ones_the_app_promises()
    {
        Assert.Equal(300, StartupTimeline.WindowVisibleBudget.TotalMilliseconds);
        Assert.Equal(1500, StartupTimeline.FirstFrameBudget.TotalMilliseconds);
    }

    [Fact]
    public void Only_the_budgeted_stages_are_held_to_a_limit()
    {
        Assert.Equal(StartupTimeline.WindowVisibleBudget, StartupTimeline.BudgetFor("window shown"));
        Assert.Equal(StartupTimeline.FirstFrameBudget, StartupTimeline.BudgetFor("first frame"));
        Assert.Null(StartupTimeline.BudgetFor("inventory loaded"));
    }

    [Fact]
    public void A_fast_startup_reports_no_breaches()
    {
        var marks = new[]
        {
            ("window shown", TimeSpan.FromMilliseconds(180)),
            ("first frame", TimeSpan.FromMilliseconds(900)),
        };

        Assert.Empty(StartupTimeline.BudgetBreaches(marks));
    }

    [Fact]
    public void A_slow_stage_is_named_with_its_budget()
    {
        var marks = new[] { ("window shown", TimeSpan.FromMilliseconds(950)) };

        var breach = Assert.Single(StartupTimeline.BudgetBreaches(marks));
        Assert.Contains("window shown", breach);
        Assert.Contains("950ms", breach);
        Assert.Contains("300ms", breach);
    }

    [Fact]
    public void Every_breach_is_reported_not_just_the_first()
    {
        var marks = new[]
        {
            ("window shown", TimeSpan.FromMilliseconds(400)),
            ("inventory loaded", TimeSpan.FromMinutes(1)),
            ("first frame", TimeSpan.FromSeconds(9)),
        };

        // The unbudgeted stage is recorded for context but never counts as a breach.
        Assert.Equal(2, StartupTimeline.BudgetBreaches(marks).Count);
    }

    [Fact]
    public void A_stage_exactly_on_budget_passes()
    {
        var marks = new[] { ("window shown", StartupTimeline.WindowVisibleBudget) };

        Assert.Empty(StartupTimeline.BudgetBreaches(marks));
    }

    [Fact]
    public void Marks_are_recorded_in_order()
    {
        StartupTimeline.Reset();
        StartupTimeline.Mark("one");
        StartupTimeline.Mark("two");

        var marks = StartupTimeline.Snapshot();

        Assert.Equal(["one", "two"], marks.Select(m => m.Stage));
        Assert.True(marks[1].At >= marks[0].At);
    }
}
