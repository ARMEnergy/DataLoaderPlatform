using System.Text.Json;

namespace DataLoader.MISO;

// =============================================================================
// Per-feed mappers (design §4). They receive records already validated by
// MisoDayReader.Validate (24 complete intervals, known members, no duplicates), so
// a pivot always has every member. Measures are read as decimal straight from the
// JSON text — never through double — so 68,923.0 stays 68,923.0 (design §4).
// =============================================================================

internal static class MisoIntervals
{
    public static (DateTime StartLocal, DateTime StartUtc, DateTime EndLocal, DateTime EndUtc) Of(MisoRecord r) =>
        (r.Start, MisoTime.EstToUtc(r.Start), r.End, MisoTime.EstToUtc(r.End));

    public static string Where(MisoWorkUnit unit, MisoRecord r) =>
        $"MISO {unit.Feed.Id} {unit.Label}: {r.Start:yyyy-MM-ddTHH:mm:ss} {r.Member}";
}

/// <summary><c>zonal_load_hourly</c>: 6 LRZ rows per hour pivoted wide; <c>miso</c> = decimal sum of the six.</summary>
public sealed class MisoZonalLoadMapper : IMisoRowMapper<MisoZonalLoadHourlyRow>
{
    public IReadOnlyList<MisoZonalLoadHourlyRow> Map(IReadOnlyList<MisoRecord> records, MisoWorkUnit unit) =>
        records.GroupBy(r => r.Start).OrderBy(g => g.Key).Select(g =>
        {
            var v = g.ToDictionary(r => r.MemberKey, r => MisoMeasure.Read(r.Source, "load", MisoIntervals.Where(unit, r), required: true), StringComparer.Ordinal);
            var t = MisoIntervals.Of(g.First());
            return new MisoZonalLoadHourlyRow
            {
                IntervalStartLocal = t.StartLocal, IntervalStartUtc = t.StartUtc, IntervalEndLocal = t.EndLocal, IntervalEndUtc = t.EndUtc,
                Lrz1 = v["LRZ1"], Lrz2_7 = v["LRZ2_7"], Lrz3_5 = v["LRZ3_5"], Lrz4 = v["LRZ4"], Lrz6 = v["LRZ6"], Lrz8_9_10 = v["LRZ8_9_10"],
                Miso = MisoMeasure.SumOrNull(MisoFeeds.Lrz.Select(m => v[m])),
            };
        }).ToList();
}

/// <summary><c>load_hourly</c>: 3 region rows per hour pivoted wide; <c>miso</c> = decimal sum of the three.</summary>
public sealed class MisoLoadMapper : IMisoRowMapper<MisoLoadHourlyRow>
{
    public IReadOnlyList<MisoLoadHourlyRow> Map(IReadOnlyList<MisoRecord> records, MisoWorkUnit unit) =>
        records.GroupBy(r => r.Start).OrderBy(g => g.Key).Select(g =>
        {
            var v = g.ToDictionary(r => r.MemberKey, r => MisoMeasure.Read(r.Source, "load", MisoIntervals.Where(unit, r), required: true), StringComparer.Ordinal);
            var t = MisoIntervals.Of(g.First());
            return new MisoLoadHourlyRow
            {
                IntervalStartLocal = t.StartLocal, IntervalStartUtc = t.StartUtc, IntervalEndLocal = t.EndLocal, IntervalEndUtc = t.EndUtc,
                North = v["NORTH"], Central = v["CENTRAL"], South = v["SOUTH"],
                Miso = MisoMeasure.SumOrNull(MisoFeeds.Regions.Select(m => v[m])),
            };
        }).ToList();
}

/// <summary><c>fuel_mix_hourly</c>: one row per (hour, region); region UPPER; unknown fuel key fails, a missing known key is NULL.</summary>
public sealed class MisoFuelMixMapper : IMisoRowMapper<MisoFuelMixHourlyRow>
{
    private static readonly HashSet<string> Known = new(MisoFeeds.FuelKeys, StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<MisoFuelMixHourlyRow> Map(IReadOnlyList<MisoRecord> records, MisoWorkUnit unit) =>
        records.Select(r =>
        {
            var where = MisoIntervals.Where(unit, r);
            if (!MisoJson.TryGet(r.Source, "fuelTypes", out var fuel) || fuel.ValueKind != JsonValueKind.Object)
                throw new MisoDataException($"{where}: fuelTypes is missing or not an object");

            // Rule 5: every key known (case-insensitive) and not repeated under another casing.
            var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in fuel.EnumerateObject())
            {
                if (!Known.Contains(p.Name))
                    throw new MisoDataException($"{where}: unknown fuel type '{MisoDayReader<object>.Short(p.Name)}'; known: {string.Join(", ", MisoFeeds.FuelKeys)}");
                if (!keys.Add(p.Name))
                    throw new MisoDataException($"{where}: fuel type '{p.Name}' appears twice");
            }

            decimal? F(string name) => MisoMeasure.Read(fuel, name, where, required: false);
            var t = MisoIntervals.Of(r);
            return new MisoFuelMixHourlyRow
            {
                IntervalStartLocal = t.StartLocal, IntervalStartUtc = t.StartUtc, IntervalEndLocal = t.EndLocal, IntervalEndUtc = t.EndUtc,
                Region = r.MemberKey,
                Coal = F("coal"), Gas = F("gas"), Nuclear = F("nuclear"), Water = F("water"),
                Wind = F("wind"), Solar = F("solar"), Other = F("other"), Storage = F("storage"),
                TotalMw = MisoMeasure.Read(r.Source, "totalMw", where, required: true),
            };
        }).ToList();
}

/// <summary>
/// <c>load_forecast_mtlf</c>: one row per (hour, zone). The zone is stored in its canonical spelling
/// (<c>Z1</c>..<c>Z10</c>), the region upper-case, and the region must be the zone's own
/// (<see cref="MisoFeeds.MtlfZoneRegion"/>); a mismatch fails the unit.
/// </summary>
public sealed class MisoLoadForecastMtlfMapper : IMisoRowMapper<MisoLoadForecastMtlfRow>
{
    public IReadOnlyList<MisoLoadForecastMtlfRow> Map(IReadOnlyList<MisoRecord> records, MisoWorkUnit unit)
    {
        var init = unit.InitDate ?? throw new InvalidOperationException($"MISO {unit.Feed.Id} {unit.Label}: MTLF unit without an init date");
        return records.Select(r =>
        {
            var where = MisoIntervals.Where(unit, r);
            var zone = r.MemberKey;   // already validated against Z1..Z10; this is the canonical spelling
            var expected = MisoFeeds.MtlfZoneRegion[zone];
            var region = MisoDayReader<object>.Str(r.Source, "region")?.Trim().ToUpperInvariant();
            if (!string.Equals(region, expected, StringComparison.Ordinal))
                throw new MisoDataException(
                    $"{where}: region '{MisoDayReader<object>.Short(region)}' does not match zone {zone}, which belongs to {expected}");

            var t = MisoIntervals.Of(r);
            return new MisoLoadForecastMtlfRow
            {
                InitDate = init,
                IntervalStartLocal = t.StartLocal, IntervalStartUtc = t.StartUtc, IntervalEndLocal = t.EndLocal, IntervalEndUtc = t.EndUtc,
                Region = expected,
                LocalResourceZone = zone,
                LoadForecast = MisoMeasure.Read(r.Source, "loadForecast", where, required: true),
            };
        }).ToList();
    }
}
