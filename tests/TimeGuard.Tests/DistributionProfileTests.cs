using TimeGuard.Services;
using Xunit;

namespace TimeGuard.Tests;

public class DistributionProfileTests
{
    [Theory] [InlineData(false)] [InlineData(true)]
    public void BuildDefault_SelectsSeparatePurePaths_WithoutStartup(bool distribution)
    {
        using var p = new TempProfile();
        var options = RuntimeOptions.Resolve([], distributionBuild: distribution, appDataRoot: p.Runtime.Paths.Root);
        Assert.Equal(distribution ? RuntimeProfile.Production : RuntimeProfile.Development, options.Profile);
        Assert.Equal(Path.Combine(p.Runtime.Paths.Root, distribution ? "ScreenTime" : "ScreenTime-Dev", "screentime.db"), options.Paths.DatabasePath);
        Assert.False(options.AllowsStartup);
        Assert.False(Directory.Exists(p.Runtime.Paths.Root));
        Assert.NotEqual(RuntimeOptions.LegacyProduction(p.Runtime.Paths.Root).MutexName, options.MutexName);
        Assert.NotEqual(RuntimeOptions.Development(p.Runtime.Paths.Root).MutexName, RuntimeOptions.Production(p.Runtime.Paths.Root).MutexName);
    }

    [Fact]
    public void ExplicitTest_OverridesDistribution_AndCannotOpenReservedUserProfiles()
    {
        using var p = new TempProfile();
        var options = RuntimeOptions.Resolve(["--test-profile", p.Runtime.Paths.Root], distributionBuild: true);
        Assert.Equal(RuntimeProfile.Test, options.Profile);
        Assert.False(options.AllowsStartup); Assert.False(options.AllowsGlobalHotkey);
        foreach (var root in new[] { RuntimeOptions.Development().Paths.Root, RuntimeOptions.Production().Paths.Root, RuntimeOptions.LegacyProduction().Paths.Root })
        {
            Assert.Throws<ArgumentException>(() => RuntimeOptions.Test(root));
            Assert.Throws<ArgumentException>(() => RuntimeOptions.Test(Path.Combine(root, "nested")));
        }
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public void NoLaunchArgument_SelectsLegacy(bool distribution)
    {
        Assert.Throws<ArgumentException>(() => RuntimeOptions.Resolve(["--profile", "legacy-production"], distributionBuild: distribution));
        if (!distribution) Assert.Throws<ArgumentException>(() => RuntimeOptions.Resolve(["--profile", "production"]));
        Assert.Equal(RuntimeProfile.Development, RuntimeOptions.Resolve(["--profile", "development"], distributionBuild: distribution).Profile);
    }
}
