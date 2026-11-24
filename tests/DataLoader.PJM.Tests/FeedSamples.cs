using System.Text.Json;

namespace DataLoader.PJM.Tests;

/// <summary>
/// Synthetic items for the six feeds added by design §14 (API doc §12 shapes, field names mirrored
/// exactly — trap 7: the forecast feeds disagree on names). EPT comes from the independent
/// <see cref="Samples.Ept"/> oracle, never from <see cref="PjmTime"/>. Pass <c>fields</c> to
/// override or remove (<see cref="Samples.Remove"/>) any field.
/// </summary>
internal static class FeedSamples
{
    private static string Iso(DateTime t) => Samples.Iso(t);
    private static string Ept(DateTime t) => Samples.Iso(Samples.Ept(t));

    private static JsonElement Build(Dictionary<string, object?> d, Dictionary<string, object?>? fields)
    {
        if (fields is not null)
            foreach (var (k, v) in fields)
            {
                if (ReferenceEquals(v, Samples.Remove)) d.Remove(k);
                else d[k] = v;
            }
        return Samples.Element(d);
    }

    public static JsonElement Metered(DateTime utc, string area = "AECO", Dictionary<string, object?>? fields = null) => Build(new()
    {
        ["datetime_beginning_utc"] = Iso(utc),
        ["datetime_beginning_ept"] = Ept(utc),
        ["nerc_region"] = "RFC",
        ["mkt_region"] = "MIDATL",
        ["zone"] = "AE",
        ["load_area"] = area,
        ["mw"] = 781.725,
        ["is_verified"] = false,
    }, fields);

    public static JsonElement Prelim(DateTime utc, string area = "AE", Dictionary<string, object?>? fields = null) => Build(new()
    {
        ["datetime_beginning_utc"] = Iso(utc),
        ["datetime_beginning_ept"] = Ept(utc),
        ["datetime_ending_utc"] = Iso(utc.AddHours(1)),
        ["datetime_ending_ept"] = Ept(utc.AddHours(1)),
        ["load_area"] = area,
        ["prelim_load_avg_hourly"] = 1000.5,
    }, fields);

    public static JsonElement Estimated(DateTime utc, string area = "AE", Dictionary<string, object?>? fields = null) => Build(new()
    {
        ["datetime_beginning_utc"] = Iso(utc),
        ["datetime_beginning_ept"] = Ept(utc),
        ["datetime_ending_utc"] = Iso(utc.AddHours(1)),
        ["datetime_ending_ept"] = Ept(utc.AddHours(1)),
        ["load_area"] = area,
        ["estimated_load_hourly"] = 990.25,
    }, fields);

    public static JsonElement Hist(DateTime evaluated, DateTime forecastHour, string area = "RTO_COMBINED", Dictionary<string, object?>? fields = null) => Build(new()
    {
        ["evaluated_at_utc"] = Iso(evaluated),
        ["evaluated_at_ept"] = Ept(evaluated),
        ["forecast_hour_beginning_utc"] = Iso(forecastHour),
        ["forecast_hour_beginning_ept"] = Ept(forecastHour),
        ["forecast_area"] = area,
        ["forecast_load_mw"] = 85000,
    }, fields);

    public static JsonElement VeryShort(DateTime evaluated, DateTime forecastBegin, string area = "RTO_COMBINED", Dictionary<string, object?>? fields = null) => Build(new()
    {
        ["evaluated_at_utc"] = Iso(evaluated),
        ["evaluated_at_ept"] = Ept(evaluated),
        ["forecast_datetime_beginning_utc"] = Iso(forecastBegin),
        ["forecast_datetime_beginning_ept"] = Ept(forecastBegin),
        ["forecast_datetime_ending_utc"] = Iso(forecastBegin.AddMinutes(5)),
        ["forecast_datetime_ending_ept"] = Ept(forecastBegin.AddMinutes(5)),
        ["forecast_area"] = area,
        ["forecast_load_mw"] = 84000,
    }, fields);

    public static JsonElement SevenDay(DateTime evaluated, DateTime forecastBegin, string area = "RTO_COMBINED", Dictionary<string, object?>? fields = null) => Build(new()
    {
        ["evaluated_at_datetime_utc"] = Iso(evaluated),
        ["evaluated_at_datetime_ept"] = Ept(evaluated),
        ["forecast_datetime_beginning_utc"] = Iso(forecastBegin),
        ["forecast_datetime_beginning_ept"] = Ept(forecastBegin),
        ["forecast_datetime_ending_utc"] = Iso(forecastBegin.AddHours(1)),
        ["forecast_datetime_ending_ept"] = Ept(forecastBegin.AddHours(1)),
        ["forecast_area"] = area,
        ["forecast_load_mw"] = 90000,
    }, fields);

    /// <summary>A full 168-row snapshot for one area from one evaluation.</summary>
    public static List<JsonElement> SevenDayArea(DateTime evaluated, string area, int rows = 168)
    {
        var first = new DateTime(evaluated.Year, evaluated.Month, evaluated.Day, 0, 0, 0).AddDays(1);
        return Enumerable.Range(0, rows).Select(h => SevenDay(evaluated, first.AddHours(h), area)).ToList();
    }

    /// <summary>A block unit (UnitHours &lt; 24) or a day unit of any feed, at an explicit age and zone.</summary>
    public static PjmDayWorkUnit Unit(PjmFeed feed, DateOnly day, int age, PjmZone zone = PjmZone.Hot, int blockStartHour = 0, DateTime? runStartedUtc = null) => new()
    {
        Feed = feed,
        Day = day,
        RunDay = day.AddDays(age),
        Zone = zone,
        BlockStartHour = blockStartHour,
        RunStartedUtc = runStartedUtc ?? day.AddDays(age).ToDateTime(new TimeOnly(14, 20)),
        KeyValue = feed.UnitHours >= 24 ? $"pjm:{feed.Name}:{day:yyyyMMdd}" : $"pjm:{feed.Name}:{day:yyyyMMdd}T{blockStartHour:00}",
    };

    /// <summary>The snapshot unit as the provider builds it for <paramref name="startedUtc"/>.</summary>
    public static PjmDayWorkUnit Snapshot(PjmFeed feed, DateTime startedUtc) =>
        PjmWorkUnitProvider.BuildSnapshot(feed, DateTime.SpecifyKind(startedUtc, DateTimeKind.Utc), DateOnly.FromDateTime(startedUtc));

    public static PjmDayReader<TRow> Reader<TRow>(IPjmPageSource src, IPjmRowMapper<TRow> mapper, CapturingLoggerFactory logs, PjmSettings? s = null, TimeProvider? time = null)
        where TRow : IPjmRow =>
        new(src, mapper, s ?? Samples.Settings(), logs.CreateLogger("t"), time);

    /// <summary>A clock frozen at <paramref name="utc"/>: the snapshot future/stale checks compare against the RESPONSE time.</summary>
    public static TimeProvider Clock(DateTime utc) => new ManualTimeProvider(new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)));

    public static FakePageSource Page(IReadOnlyList<JsonElement> items) => FakePageSource.Paged(items, 50000);
}
