using System.Globalization;
using DataLoader.Core.Abstractions;
using Microsoft.Extensions.Logging;

namespace DataLoader.CWG;

/// <summary>
/// One CWG request/file = one work unit, shared by all 15 endpoints (design §3).
/// Carries the fully-substituted <see cref="Filename"/> (no host/query), the
/// natural-key fields the FileLog needs (<see cref="Region"/>,
/// <see cref="Variant"/>, <see cref="RepresentativeDate"/>) and the precomputed
/// resume <see cref="KeyValue"/> (stable for dated files, hot for undated).
/// </summary>
public sealed class CwgWorkUnit : WorkUnit
{
    public required string EndpointId { get; init; }
    public string? Region { get; init; }
    public string? Variant { get; init; }
    public DateOnly? RepresentativeDate { get; init; }
    public required string Filename { get; init; }
    public required string KeyValue { get; init; }

    public override string Key => KeyValue;

    public override string DisplayName => RepresentativeDate.HasValue
        ? $"{EndpointId} {Region ?? "-"} {RepresentativeDate.Value:yyyy-MM-dd} ({Filename})"
        : $"{EndpointId} {Region ?? "-"} latest ({Filename})";
}

/// <summary>
/// DB-free work-unit provider for one bound descriptor (design §3). Dated endpoints
/// enumerate the trailing <c>DaysBack</c> window back from
/// <c>runDate + DateOffsetDays</c> and resolve each unit's resume key with the
/// StormVista TWO-ZONE rule: a represented date older than <c>SettledAfterDays</c>
/// gets a STABLE key (loaded once, then skipped forever via <c>core.LoadLog</c>),
/// while a recent one (age ≤ <c>SettledAfterDays</c>) gets a run-varying HOT key
/// (<c>:run=&lt;token&gt;</c>) so the hot zone is re-pulled every run — catching
/// not-yet-published / late / revised files and upserting idempotently via the
/// natural-key MERGE. Undated endpoints enumerate the single latest file, always
/// HOT. Geography descriptors intersect their regions with the <c>Geographies</c>
/// setting; None/ISO descriptors are not filtered.
/// </summary>
public sealed class CwgWorkUnitProvider : IWorkUnitProvider<CwgWorkUnit>
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>
    /// US Eastern time zone. CWG's dated files are published on the Eastern calendar, so the
    /// "current data date" is derived here (not from the UTC date) to avoid targeting a
    /// not-yet-existent future date when a run straddles the UTC midnight boundary (Fix 4).
    /// </summary>
    private static readonly TimeZoneInfo EasternTz = ResolveEastern();

    private static TimeZoneInfo ResolveEastern()
    {
        foreach (var id in new[] { "America/New_York", "Eastern Standard Time" })
        {
            try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
            catch (TimeZoneNotFoundException) { }
            catch (InvalidTimeZoneException) { }
        }
        return TimeZoneInfo.Utc; // last-resort fallback; keeps enumeration working on an exotic host
    }

    private readonly CwgEndpointDescriptor _d;
    private readonly CwgSettings _settings;
    private readonly ILogger _logger;

    public CwgWorkUnitProvider(CwgEndpointDescriptor descriptor, CwgSettings settings, ILogger logger)
    {
        _d = descriptor;
        _settings = settings;
        _logger = logger;
    }

    public Task<IReadOnlyList<CwgWorkUnit>> GetWorkUnitsAsync(LoaderRunContext context)
    {
        // Current data date in US Eastern time (Fix 4) — not the UTC date.
        var runDate = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(
            DateTime.SpecifyKind(context.StartedAtUtc, DateTimeKind.Utc), EasternTz));

        var regions = ResolveRegions();
        if (regions.Count == 0)
        {
            _logger.LogInformation("[CWG {Endpoint}] no regions after the Geographies filter; nothing to enumerate", _d.EndpointId);
            return Task.FromResult<IReadOnlyList<CwgWorkUnit>>(Array.Empty<CwgWorkUnit>());
        }

        var extras = BuildExtras(_d.ExtraPlaceholders);

        // Hot-zone run token. RunDate → one re-pull per calendar day; RunId → every run.
        // Undated units and the hot dated zone carry this; settled dated days omit it.
        var hot = _settings.HotZoneKeyStrategy == HotKeyStrategy.RunDate
            ? runDate.ToString("yyyyMMdd", Inv)
            : context.RunId.ToString("N");

        var units = new List<CwgWorkUnit>();

        if (_d.Dated)
        {
            var newest = runDate.AddDays(_d.DateOffsetDays);
            var daysBack = Math.Max(1, _settings.DaysBack);
            for (var k = 0; k < daysBack; k++)
            {
                var d = newest.AddDays(-k);
                // Two-zone resume key (StormVista rule): a represented date older than
                // SettledAfterDays SETTLES (stable base key → loaded once, then a cheap
                // LoadLog skip forever); a recent one stays HOT (base + :run=token →
                // re-pulled every run so late/revised recent files are caught).
                var ageDays = runDate.DayNumber - d.DayNumber;
                foreach (var region in regions)
                foreach (var extra in extras)
                {
                    var variant = Variant(extra);
                    var baseKey = $"cwg:{_d.EndpointId}:{region ?? "-"}:{variant ?? "-"}:{d.ToString("yyyyMMdd", Inv)}";
                    units.Add(new CwgWorkUnit
                    {
                        EndpointId = _d.EndpointId,
                        Region = region,
                        Variant = variant,
                        RepresentativeDate = d,
                        Filename = Substitute(region, d, extra),
                        KeyValue = ageDays > _settings.SettledAfterDays ? baseKey : $"{baseKey}:run={hot}"
                    });
                }
            }
        }
        else
        {
            foreach (var region in regions)
            foreach (var extra in extras)
            {
                var variant = Variant(extra);
                units.Add(new CwgWorkUnit
                {
                    EndpointId = _d.EndpointId,
                    Region = region,
                    Variant = variant,
                    RepresentativeDate = null,
                    Filename = Substitute(region, null, extra),
                    KeyValue = $"cwg:{_d.EndpointId}:{region ?? "-"}:{variant ?? "-"}:run={hot}"
                });
            }
        }

        _logger.LogDebug("[CWG {Endpoint}] enumerated {Count} work unit(s)", _d.EndpointId, units.Count);
        return Task.FromResult<IReadOnlyList<CwgWorkUnit>>(units);
    }

    /// <summary>
    /// The region list to iterate. Geography descriptors are filtered by the
    /// <c>Geographies</c> setting; None/ISO descriptors with a fixed <c>Regions</c> list
    /// (e.g. NationalDegreeDays' <c>northamerica</c>) return it verbatim — unfiltered
    /// (Fix 2); a descriptor with no regions yields a single <c>null</c> region.
    /// </summary>
    private IReadOnlyList<string?> ResolveRegions()
    {
        if (_d.RegionKind == CwgRegionKind.Geography)
        {
            var geoSet = new HashSet<string>(_settings.Geographies ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
            return _d.Regions.Where(geoSet.Contains).Cast<string?>().ToList();
        }
        if (_d.Regions.Length > 0)
            return _d.Regions.Cast<string?>().ToList();
        return new List<string?> { null };
    }

    private static string? Variant(IReadOnlyDictionary<string, string> extra) =>
        extra.TryGetValue("subregion", out var sub) ? sub : null;

    private string Substitute(string? region, DateOnly? date, IReadOnlyDictionary<string, string> extra)
    {
        var s = _d.FilenameTemplate;

        if (_d.RegionPlaceholder is not null && region is not null)
            s = s.Replace("{" + _d.RegionPlaceholder + "}", region);

        if (date.HasValue)
        {
            var token = _d.DateToken switch
            {
                CwgDateToken.Ymd => date.Value.ToString("yyyyMMdd", Inv),
                CwgDateToken.Mdyyyy => date.Value.ToString("MMddyyyy", Inv),
                _ => string.Empty
            };
            // Only one of these tokens exists in a given template; the other Replace is a no-op.
            s = s.Replace("{date}", token).Replace("{datemmddyyyy}", token);
        }

        foreach (var kv in extra)
            s = s.Replace("{" + kv.Key + "}", kv.Value);

        return s;
    }

    /// <summary>Cartesian product of the extra placeholders; a single empty map when there are none.</summary>
    private static List<Dictionary<string, string>> BuildExtras((string Name, string[] Values)[] extras)
    {
        var result = new List<Dictionary<string, string>> { new() };
        foreach (var (name, values) in extras)
        {
            var next = new List<Dictionary<string, string>>();
            foreach (var acc in result)
            foreach (var v in values)
                next.Add(new Dictionary<string, string>(acc) { [name] = v });
            result = next;
        }
        return result;
    }
}
