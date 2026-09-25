using System.Globalization;
using TimeGuard.Helpers;
using TimeGuard.Models;
using TimeGuard.Services;
using TimeGuard.UI;
using TimeGuard.ViewModels;
using Xunit;

namespace TimeGuard.UITests;

public class Beta2PresentationTests
{
    [Fact]
    public async Task InitialConfiguration_IsOneShot_AndCannotAuthorizeLaterProtectedCommands()
    {
        var password = PasswordHelper.Hash("correct"); var prompts = 0; var settings = 0; var exits = 0;
        var access = new SettingsAccessService(() => password, () => { prompts++; return null; },
            () => settings++, () => { exits++; return Task.CompletedTask; }, () => false, true);
        access.OpenInitialConfiguration(); access.OpenInitialConfiguration();
        Assert.Equal(1, settings); Assert.Equal(0, prompts);
        access.OpenSettings(); await access.ExitAsync();
        Assert.Equal(2, prompts); Assert.Equal(1, settings); Assert.Equal(0, exits);
        new SettingsAccessService(() => password, () => null, () => settings++, () => Task.CompletedTask, () => false).OpenInitialConfiguration();
        Assert.Equal(1, settings);
    }

    [Theory]
    [InlineData(0, "12:00 AM")]
    [InlineData(12, "12:00 PM")]
    [InlineData(17, "5:00 PM")]
    public void Clock_IsExplicitAmPm_RegardlessOfCurrentCulture(int hour, string expected)
    {
        var before = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            var local = new DateTimeOffset(new DateTime(2026, 9, 25, hour, 0, 0, DateTimeKind.Local));
            Assert.Equal(expected, DisplayTime.Clock(local));
        }
        finally { CultureInfo.CurrentCulture = before; }
    }

    [Theory]
    [InlineData(0, "0 min")]
    [InlineData(30, "<1 min")]
    [InlineData(2520, "42 min")]
    [InlineData(3600, "1 hr")]
    [InlineData(10020, "2 hr 47 min")]
    public void Duration_IsReadable(long seconds, string expected) => Assert.Equal(expected, DisplayTime.Duration(seconds));

    [Fact]
    public void Downtime_UsesDirectFacts_QuotaMayDelayAvailability_UnknownIsNeverInvented()
    {
        var now = DateTimeOffset.Now;
        var facts = new PolicySnapshot("app", "App", true, DateOnly.FromDateTime(now.DateTime), 60, 3600, false, false,
            new(true, now.AddHours(2), now.AddDays(1)), now.AddDays(1));
        var row = new AppStatusRow(new(facts, new RulesEngine().Evaluate(facts), 3600, 0), now);
        Assert.Equal("Downtime until " + DisplayTime.Availability(now.AddHours(2), now), row.Primary);
        Assert.Equal("Available in 24 hr", row.Secondary);
        var unknown = facts with { NextAvailability = null };
        row.Refresh(new(unknown, new RulesEngine().Evaluate(unknown), 3600, 0), now);
        Assert.DoesNotContain("Available in", row.Secondary);
        Assert.Equal("Next availability unknown", row.Secondary);
        Assert.Equal("1 min", DisplayTime.Until(now.AddSeconds(1), now));
    }
}
