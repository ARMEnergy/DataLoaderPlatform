using Xunit;

namespace DataLoader.PJM.Tests;

// Skeleton only: CODE_TESTER owns the bulk of the suite (design §12.2). This file pins the
// platform-default correction horizon on the class (design §10 / test 1, first half).
public sealed class PjmSettingsDefaultsTests
{
    [Fact]
    public void Correction_horizon_class_defaults_are_10_and_10()
    {
        var s = new PjmSettings();
        Assert.Equal(10, s.DaysBack);
        Assert.Equal(10, s.SettledAfterDays);
    }

    [Fact]
    public void Shipped_class_defaults_pass_startup_validation()
    {
        Assert.Empty(new PjmSettings().Validate());
    }
}
