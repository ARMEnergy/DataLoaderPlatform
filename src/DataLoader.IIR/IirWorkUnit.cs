using System.Globalization;
using DataLoader.Core.Abstractions;
using Microsoft.Extensions.Logging;

namespace DataLoader.IIR;

/// <summary>
/// US-Central time-zone resolution (design §5.1). ARM Energy operates on the US-Central business
/// day, so the OfflineEvent daily-snapshot partition boundary flips on that boundary (not UTC). Try
/// the Windows id then the IANA id, cache once, and last-resort fall back to UTC on an exotic host.
/// </summary>
internal static class IirTime
{
    private static readonly TimeZoneInfo CentralTz;

    /// <summary>True when neither Central id resolved and enumeration fell back to UTC (log a warning).</summary>
    public static readonly bool UsingUtcFallback;

    static IirTime()
    {
        foreach (var id in new[] { "Central Standard Time", "America/Chicago" })
        {
            try
            {
                CentralTz = TimeZoneInfo.FindSystemTimeZoneById(id);
                UsingUtcFallback = false;
                return;
            }
            catch (TimeZoneNotFoundException) { }
            catch (InvalidTimeZoneException) { }
        }
        CentralTz = TimeZoneInfo.Utc;
        UsingUtcFallback = true;
    }

    /// <summary>The US-Central calendar date for a UTC instant (design §5.1).</summary>
    public static DateOnly CentralDate(DateTime startedAtUtc) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(
            DateTime.SpecifyKind(startedAtUtc, DateTimeKind.Utc), CentralTz));
}

/// <summary>
/// One IIR endpoint pull = one work unit (design §5), shared by all three endpoints (CWG-style).
/// Because all three pulls are full-snapshot / current-state, each endpoint materializes exactly ONE
/// unit per run that pages the whole catalogue internally. Carries the Central <see cref="RunDate"/>
/// (the OfflineEvent PK part and the FileLog RepresentativeDate), the endpoint's fully-built filter
/// <see cref="QueryString"/> and the precomputed resume <see cref="KeyValue"/>.
/// </summary>
public sealed class IirWorkUnit : WorkUnit
{
    public required string EndpointId { get; init; }
    public required DateOnly RunDate { get; init; }
    public required string QueryString { get; init; }
    public required string KeyValue { get; init; }
    public required string DisplayNameValue { get; init; }

    public override string Key => KeyValue;
    public override string DisplayName => DisplayNameValue;
}

/// <summary>
/// DB-free work-unit provider for one bound descriptor (design §5). Emits exactly ONE
/// <see cref="IirWorkUnit"/> per run: the Central run date, the endpoint's status-scoped filter query
/// (OfflineEvent only) and the two-zone-free undated resume key. The single unit pages the whole
/// catalogue inside the source reader (§4) — one-unit-per-page is rejected because <c>totalCount</c> is
/// unknown until the first call.
/// </summary>
public sealed class IirWorkUnitProvider : IWorkUnitProvider<IirWorkUnit>
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private readonly IirEndpointDescriptor _d;
    private readonly IirSettings _settings;
    private readonly ILogger _logger;

    public IirWorkUnitProvider(IirEndpointDescriptor descriptor, IirSettings settings, ILogger logger)
    {
        _d = descriptor;
        _settings = settings;
        _logger = logger;
    }

    public Task<IReadOnlyList<IirWorkUnit>> GetWorkUnitsAsync(LoaderRunContext context)
    {
        var runDate = IirTime.CentralDate(context.StartedAtUtc);
        var query = BuildSummaryQuery();

        var baseKey = $"iir:{_d.EndpointId}:{runDate.ToString("yyyyMMdd", Inv)}";
        var key = _settings.HotKeyStrategy == IirHotKeyStrategy.RunId
            ? $"{baseKey}:run={context.RunId:N}"
            : baseKey;

        var unit = new IirWorkUnit
        {
            EndpointId = _d.EndpointId,
            RunDate = runDate,
            QueryString = query,
            KeyValue = key,
            DisplayNameValue = $"IIR {_d.EndpointId} {runDate:yyyy-MM-dd}"
        };

        _logger.LogDebug("[IIR {Endpoint}] enumerated 1 work unit ({Key})", _d.EndpointId, key);
        return Task.FromResult<IReadOnlyList<IirWorkUnit>>(new[] { unit });
    }

    /// <summary>
    /// The STEP-1 summary query (design §4/§5.2), built for ALL three endpoints: each
    /// <c>PhysicalAddressCountryNames</c> value as a repeated <c>physicalAddressCountryName=</c> key.
    /// For OfflineEvent, the OPTIONAL <c>OfflineEventKinds</c>/<c>OfflineEventStatuses</c> narrowing is
    /// appended as repeated <c>eventKind=</c>/<c>eventStatusDesc=</c> keys ONLY when configured
    /// non-empty (default is the country-only pull). Not a date-window pull; the daily snapshot is the
    /// mechanism that accretes OfflineEvent history (§5.4).
    /// </summary>
    private string BuildSummaryQuery()
    {
        var parts = new List<string>();

        // Country filter — applied on ALL three summary calls.
        foreach (var country in _settings.PhysicalAddressCountryNames ?? Array.Empty<string>())
            if (!string.IsNullOrWhiteSpace(country))
                parts.Add($"physicalAddressCountryName={Uri.EscapeDataString(country.Trim())}");

        // Optional OfflineEvent narrowing — appended only when configured non-empty.
        if (_d.StatusScoped)
        {
            foreach (var kind in _settings.OfflineEventKinds ?? Array.Empty<string>())
                if (!string.IsNullOrWhiteSpace(kind))
                    parts.Add($"eventKind={Uri.EscapeDataString(kind.Trim())}");
            foreach (var status in _settings.OfflineEventStatuses ?? Array.Empty<string>())
                if (!string.IsNullOrWhiteSpace(status))
                    parts.Add($"eventStatusDesc={Uri.EscapeDataString(status.Trim())}");
        }

        return string.Join("&", parts);
    }
}
