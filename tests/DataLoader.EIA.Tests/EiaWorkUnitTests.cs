using Xunit;

namespace DataLoader.EIA.Tests;

/// <summary>
/// Resume-key semantics.
///
/// <para>
/// This is the loader's idempotency contract. A SETTLED window's key must be a pure
/// function of (series, frequency, window boundaries) — if anything run-varying leaks
/// into it, the ~90-million-row backfill repeats on every run. A HOT window's key must
/// vary at the intended cadence, or revisions are never picked up. Both failures are
/// silent, which is why they are pinned here.
/// </para>
/// </summary>
public sealed class EiaWorkUnitTests
{
    private static readonly EiaSeriesPlan Coal = TestHelpers.CoalPlan();
    private static readonly EiaFrequency Annual = TestHelpers.Frequency();

    // ------------------------------------------------------- settled keys

    [Fact]
    public void A_settled_key_names_the_series_frequency_and_window()
    {
        var unit = TestHelpers.Unit(Coal, Annual, "2001", "2010");

        Assert.Equal(
            "eia:series=coal/market-sales-price;freq=annual;from=2001;to=2010",
            unit.Key);
    }

    [Fact]
    public void A_settled_key_is_stable_across_runs()
    {
        var first = TestHelpers.Unit(Coal, Annual, "2001", "2010");
        var second = TestHelpers.Unit(Coal, Annual, "2001", "2010");

        Assert.Equal(first.Key, second.Key);
        Assert.DoesNotContain("run=", first.Key, StringComparison.Ordinal);
    }

    [Fact]
    public void Different_windows_of_the_same_frequency_have_different_keys()
    {
        var a = TestHelpers.Unit(Coal, Annual, "2001", "2010");
        var b = TestHelpers.Unit(Coal, Annual, "2011", "2020");

        Assert.NotEqual(a.Key, b.Key);
    }

    [Fact]
    public void The_same_window_under_different_frequencies_has_different_keys()
    {
        var monthly = TestHelpers.Frequency("monthly", "YYYY-MM");

        var annual = TestHelpers.Unit(Coal, Annual, "2001", "2010");
        var month = TestHelpers.Unit(Coal, monthly, "2001-01", "2005-12");

        Assert.NotEqual(annual.Key, month.Key);
        Assert.Contains("freq=annual", annual.Key, StringComparison.Ordinal);
        Assert.Contains("freq=monthly", month.Key, StringComparison.Ordinal);
    }

    // ----------------------------------------------------------- hot keys

    [Fact]
    public void A_hot_key_carries_the_run_token()
    {
        var unit = TestHelpers.Unit(Coal, Annual, "2021", "2030", settled: false, hotToken: "20261016");

        Assert.Equal(
            "eia:series=coal/market-sales-price;freq=annual;from=2021;to=2030;run=20261016",
            unit.Key);
    }

    [Fact]
    public void A_hot_key_differs_from_the_settled_key_for_the_same_window()
    {
        var settled = TestHelpers.Unit(Coal, Annual, "2021", "2030");
        var hot = TestHelpers.Unit(Coal, Annual, "2021", "2030", settled: false, hotToken: "20261016");

        Assert.NotEqual(settled.Key, hot.Key);
        Assert.StartsWith(settled.Key, hot.Key, StringComparison.Ordinal);
    }

    // ------------------------------------------------------- hot tokens

    private static readonly DateTime StartedAt = new(2026, 10, 16, 14, 30, 0, DateTimeKind.Utc);
    private static readonly Guid RunId = Guid.Parse("11112222-3333-4444-5555-666677778888");

    [Fact]
    public void RunDate_tokens_change_once_a_day()
    {
        var morning = EiaWorkUnitProvider.BuildHotToken(EiaHotKeyStrategy.RunDate, StartedAt, RunId);
        var evening = EiaWorkUnitProvider.BuildHotToken(
            EiaHotKeyStrategy.RunDate, StartedAt.AddHours(8), RunId);
        var tomorrow = EiaWorkUnitProvider.BuildHotToken(
            EiaHotKeyStrategy.RunDate, StartedAt.AddDays(1), RunId);

        Assert.Equal("20261016", morning);
        Assert.Equal(morning, evening);
        Assert.NotEqual(morning, tomorrow);
    }

    [Fact]
    public void RunHour_tokens_change_once_an_hour()
    {
        var now = EiaWorkUnitProvider.BuildHotToken(EiaHotKeyStrategy.RunHour, StartedAt, RunId);
        var later = EiaWorkUnitProvider.BuildHotToken(
            EiaHotKeyStrategy.RunHour, StartedAt.AddHours(1), RunId);

        Assert.Equal("2026101614", now);
        Assert.NotEqual(now, later);
    }

    [Fact]
    public void RunId_tokens_change_every_invocation()
    {
        var a = EiaWorkUnitProvider.BuildHotToken(EiaHotKeyStrategy.RunId, StartedAt, Guid.NewGuid());
        var b = EiaWorkUnitProvider.BuildHotToken(EiaHotKeyStrategy.RunId, StartedAt, Guid.NewGuid());

        Assert.NotEqual(a, b);
    }

    /// <summary>
    /// An unmapped strategy must THROW. Falling back to a stable key would freeze the
    /// hot zone and silently stop picking up the revisions it exists to catch — a
    /// failure that looks exactly like "the vendor published nothing".
    /// </summary>
    [Fact]
    public void An_unmapped_hot_key_strategy_throws_rather_than_freezing_the_hot_zone()
    {
        Assert.Throws<NotSupportedException>(() =>
            EiaWorkUnitProvider.BuildHotToken((EiaHotKeyStrategy)999, StartedAt, RunId));
    }

    // -------------------------------------------------------- display name

    [Fact]
    public void Display_name_identifies_the_window_for_a_log_line()
    {
        var unit = TestHelpers.Unit(Coal, Annual, "2001", "2010");

        Assert.Equal("coal/market-sales-price annual 2001..2010", unit.DisplayName);
    }

    // ------------------------------------------- the data-field name set

    [Fact]
    public void Data_field_names_use_ordinal_comparison()
    {
        var names = EiaWorkUnitProvider.BuildDataFieldNames(new[] { TestHelpers.DataType("price") });

        Assert.Contains("price", names);
        // Case matters: the partition decides the primary key and must not vary with
        // culture or casing rules.
        Assert.DoesNotContain("Price", names);
        Assert.DoesNotContain("PRICE-UNITS", names);
    }
}
