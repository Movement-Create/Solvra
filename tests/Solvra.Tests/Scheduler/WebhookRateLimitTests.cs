#nullable enable

using Solvra.Scheduler;
using Xunit;

namespace Solvra.Tests.Scheduler;

public class WebhookRateLimitTests
{
    [Fact]
    public void CronScheduler_ShouldRun_BasicCronMatch()
    {
        var now = new DateTime(2024, 6, 15, 10, 30, 0); // Saturday, 10:30

        // "30 10 * * *" = every day at 10:30
        Assert.True(CronScheduler.ShouldRun("30 10 * * *", now, null));

        // "0 10 * * *" = every day at 10:00 - should NOT match 10:30
        Assert.False(CronScheduler.ShouldRun("0 10 * * *", now, null));

        // "* * * * *" = every minute
        Assert.True(CronScheduler.ShouldRun("* * * * *", now, null));
    }

    [Fact]
    public void CronScheduler_ShouldRun_PreventsDoubleRun()
    {
        var now = new DateTime(2024, 6, 15, 10, 30, 0);
        var lastRun = now.AddSeconds(-30); // Ran 30 seconds ago

        // Should not run again within 55 seconds
        Assert.False(CronScheduler.ShouldRun("30 10 * * *", now, lastRun));

        // But should run if last run was > 55 seconds ago
        var oldLastRun = now.AddSeconds(-60);
        Assert.True(CronScheduler.ShouldRun("30 10 * * *", now, oldLastRun));
    }

    [Fact]
    public void CronScheduler_FieldMatches_Wildcards()
    {
        Assert.True(CronScheduler.FieldMatches("*", 5, 0, 59));
        Assert.True(CronScheduler.FieldMatches("*", 0, 0, 59));
    }

    [Fact]
    public void CronScheduler_FieldMatches_Steps()
    {
        Assert.True(CronScheduler.FieldMatches("*/5", 0, 0, 59));
        Assert.True(CronScheduler.FieldMatches("*/5", 15, 0, 59));
        Assert.False(CronScheduler.FieldMatches("*/5", 3, 0, 59));
    }

    [Fact]
    public void CronScheduler_FieldMatches_Ranges()
    {
        Assert.True(CronScheduler.FieldMatches("10-20", 15, 0, 59));
        Assert.True(CronScheduler.FieldMatches("10-20", 10, 0, 59));
        Assert.True(CronScheduler.FieldMatches("10-20", 20, 0, 59));
        Assert.False(CronScheduler.FieldMatches("10-20", 5, 0, 59));
        Assert.False(CronScheduler.FieldMatches("10-20", 25, 0, 59));
    }

    [Fact]
    public void CronScheduler_FieldMatches_CombinationExpressions()
    {
        // Fix 6i: Test comma-separated combinations
        Assert.True(CronScheduler.FieldMatches("5,10,15", 10, 0, 59));
        Assert.False(CronScheduler.FieldMatches("5,10,15", 7, 0, 59));

        // Range + single value
        Assert.True(CronScheduler.FieldMatches("1-5,10", 3, 0, 59));
        Assert.True(CronScheduler.FieldMatches("1-5,10", 10, 0, 59));
        Assert.False(CronScheduler.FieldMatches("1-5,10", 7, 0, 59));

        // Step + range combination
        Assert.True(CronScheduler.FieldMatches("*/5,10-20", 0, 0, 59));  // Matches */5
        Assert.True(CronScheduler.FieldMatches("*/5,10-20", 12, 0, 59)); // Matches 10-20
        Assert.False(CronScheduler.FieldMatches("*/5,10-20", 7, 0, 59)); // Matches neither
    }

    [Fact]
    public void CronScheduler_FieldMatches_SingleValue()
    {
        Assert.True(CronScheduler.FieldMatches("30", 30, 0, 59));
        Assert.False(CronScheduler.FieldMatches("30", 15, 0, 59));
    }

    [Fact]
    public void CronScheduler_ValidateCronExpression()
    {
        Assert.True(CronScheduler.ValidateCronExpression("* * * * *"));
        Assert.True(CronScheduler.ValidateCronExpression("*/5 * * * *"));
        Assert.True(CronScheduler.ValidateCronExpression("0 9 * * 1-5"));
        Assert.True(CronScheduler.ValidateCronExpression("30 10 15 6 *"));
        Assert.True(CronScheduler.ValidateCronExpression("5,10,15 * * * *"));

        Assert.False(CronScheduler.ValidateCronExpression("invalid"));
        Assert.False(CronScheduler.ValidateCronExpression("* * *")); // Too few fields
    }

    [Fact]
    public void CronScheduler_DayOfWeek_Match()
    {
        // Saturday = 6
        var saturday = new DateTime(2024, 6, 15, 10, 0, 0); // This is a Saturday
        Assert.True(CronScheduler.ShouldRun("0 10 * * 6", saturday, null));
        Assert.False(CronScheduler.ShouldRun("0 10 * * 1", saturday, null)); // Monday

        // Sunday = 0
        var sunday = new DateTime(2024, 6, 16, 10, 0, 0);
        Assert.True(CronScheduler.ShouldRun("0 10 * * 0", sunday, null));
    }

    [Fact]
    public void CronScheduler_ShouldRun_RejectsInvalidExpression()
    {
        var now = DateTime.UtcNow;
        Assert.False(CronScheduler.ShouldRun("invalid cron", now, null));
        Assert.False(CronScheduler.ShouldRun("", now, null));
    }
}
