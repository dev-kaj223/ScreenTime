using TimeGuard.Models;
using TimeGuard.Services;
using Xunit;

namespace TimeGuard.Tests;

public class UsageHistoryTests
{
    [Fact]
    public void BoundedDailyAndMonthlyHistory_UsesOnlyEnabledRules_AndDoesNotMutateUsage()
    {
        using var profile = new TempProfile();
        var db = new DatabaseService(profile.Runtime.Paths);
        db.SaveRule(new AppRule { ProcessName = "apex", DisplayName = "Apex", Enabled = true });
        db.SaveRule(new AppRule { ProcessName = "photoshop", DisplayName = "Photoshop", Enabled = true });
        db.SaveRule(new AppRule { ProcessName = "disabled", Enabled = false });
        var today = new DateOnly(2026, 9, 25);
        void Add(int months, int days, string app, double minutes) =>
            db.UpsertUsageEntry(today.AddMonths(months).AddDays(days),
                new UsageEntry { ProcessName = app, UsageMinutes = minutes });
        Add(0, 0, "apex", 10); Add(0, -6, "apex", 5); Add(0, -7, "apex", 99);
        Add(0, -29, "photoshop", 8); Add(0, -30, "photoshop", 99);
        Add(-11, 0, "apex", 7); Add(-12, 0, "apex", 99);
        Add(0, 0, "disabled", 55); Add(0, 0, "unrelated", 55);
        var before = db.LoadLog(today).GetOrCreate("apex").UsageMinutes;

        var week = db.LoadUsageHistory(today.AddDays(-6), today, false);
        Assert.Equal(2, week.Count); Assert.Contains(week, r => r.Bucket == today && r.UsageMins == 10);
        var month = db.LoadUsageHistory(today.AddDays(-29), today, false);
        Assert.DoesNotContain(month, r => r.Bucket == today.AddDays(-30));
        Assert.Equal(4, month.Count);
        var yearStart = new DateOnly(today.Year, today.Month, 1).AddMonths(-11);
        var year = db.LoadUsageHistory(yearStart, today, true);
        Assert.Contains(year, r => r.Bucket == yearStart && r.ProcessName == "apex" && r.UsageMins == 7);
        Assert.Contains(year, r => r.Bucket == new DateOnly(2026, 9, 1) && r.ProcessName == "apex" && r.UsageMins == 114);
        Assert.DoesNotContain(year, r => r.Bucket == new DateOnly(2025, 9, 1));
        Assert.DoesNotContain(year, r => r.ProcessName is "disabled" or "unrelated");
        Assert.Equal(before, db.LoadLog(today).GetOrCreate("apex").UsageMinutes);
        Assert.Empty(db.LoadUsageHistory(today.AddYears(1), today.AddYears(1), true));
    }
}
