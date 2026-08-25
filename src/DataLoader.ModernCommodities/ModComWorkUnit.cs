using System.Globalization;
using System.Text;
using DataLoader.Core.Abstractions;
using Microsoft.Extensions.Logging;

namespace DataLoader.ModernCommodities;

/// <summary>
/// One work unit = one endpoint × one window chunk = <b>one HTTP request</b> = one
/// <c>arm.FileLog</c> row (design §3.1).
///
/// <para>There is <b>no</b> per-day, per-product or per-id work unit: one request returns the
/// complete result set for its range (the API has no paging, no cursor and no id batching), so the
/// only fan-out axis is the date range.</para>
/// </summary>
public sealed class ModComWorkUnit : WorkUnit
{
    /// <summary><c>AllTrades</c> | <c>MyTrades</c> | <c>Settlements</c> (from the descriptor).</summary>
    public required string EndpointId { get; init; }

    /// <summary>The literal <c>startDate</c> query value — <b>inclusive</b>.</summary>
    public required DateOnly WindowStart { get; init; }

    /// <summary>The literal <c>endDate</c> query value — <b>inclusive</b> (it covers its whole day).</summary>
    public required DateOnly WindowEnd { get; init; }

    /// <summary>The <c>myTrades</c> scope, or <c>null</c> when the parameter is omitted (the default).</summary>
    public string? LegalEntityName { get; init; }

    /// <summary>
    /// Sanitised relative descriptor for logging and <c>arm.FileLog</c>, e.g.
    /// <c>allTrades/v1?startDate=2026-07-25&amp;endDate=2026-08-24</c> — no host, no credential.
    /// (The ModCom credential is a <b>header</b>, so the URI is inherently safe to log; the path is
    /// kept sanitised on principle.)
    /// </summary>
    public required string RequestPath { get; init; }

    /// <summary>The <c>{hot}</c> token — also the <c>arm.FileLog</c> natural-key slot (design §6.2).</summary>
    public required string RunToken { get; init; }

    /// <summary>Precomputed literal resume key (design §3.3).</summary>
    public required string KeyValue { get; init; }

    public override string Key => KeyValue;

    /// <summary>Rendered <b>invariant</b> — it is persisted verbatim into <c>core.LoadLog</c>.</summary>
    public override string DisplayName =>
        LegalEntityName is null
            ? $"{EndpointId} {ModComTime.Iso(WindowStart)}..{ModComTime.Iso(WindowEnd)}"
            : $"{EndpointId} {ModComTime.Iso(WindowStart)}..{ModComTime.Iso(WindowEnd)} [{LegalEntityName}]";
}

/// <summary>
/// Enumerates one endpoint's work units from the run's UTC clock, the endpoint's effective
/// <c>DaysBack</c>/<c>ChunkDays</c> and its <c>HistoryLimited</c> flag — <b>and nothing else</b>
/// (design §2). One descriptor-parameterised class, three instances.
///
/// <para><b>It touches no database table, holds no reference cache and has no
/// fail-fast-if-empty guard.</b> That is the deliberate divergence from AGSI, whose fact work units
/// <i>are</i> a discovered country list read back through a <c>usp_Get…</c> proc behind a hard
/// barrier. Here every endpoint takes dates alone, so any subset of the three is a valid run and the
/// pipelines could be reordered or parallelised without changing correctness (design §1.2).</para>
/// </summary>
public sealed class ModComWorkUnitProvider : IWorkUnitProvider<ModComWorkUnit>
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private readonly ModComEndpointDescriptor _d;
    private readonly ModComSettings _settings;
    private readonly ILogger _logger;

    public ModComWorkUnitProvider(ModComEndpointDescriptor descriptor, ModComSettings settings, ILogger logger)
    {
        _d = descriptor;
        _settings = settings;
        _logger = logger;
    }

    public Task<IReadOnlyList<ModComWorkUnit>> GetWorkUnitsAsync(LoaderRunContext context)
    {
        var daysBack = _settings.EffectiveDaysBack(_d.EndpointId);
        var window = ModComTime.ResolveWindow(context.StartedAtUtc, daysBack, _d.HistoryLimited);
        var hot = ModComTime.HotToken(_settings.HotKeyStrategy, context);
        var chunkDays = _settings.EffectiveChunkDays(_d.EndpointId);

        // The myTrades scope, or null everywhere else. Omitting it returns BOTH ARM legal entities
        // (verified), which is why the shipped default omits it.
        var scope = _d.SupportsLegalEntityName && !string.IsNullOrWhiteSpace(_settings.LegalEntityName)
            ? _settings.LegalEntityName!.Trim()
            : null;

        var units = new List<ModComWorkUnit>();

        if (chunkDays <= 0)
        {
            // The shipped default: the whole window in ONE request (3 requests per run).
            units.Add(Build(window.Start, window.End, scope, hot));
        }
        else
        {
            var cursor = window.Start;
            while (cursor <= window.End)
            {
                // chunkDays counts INCLUSIVE days, so the chunk end is cursor + (chunkDays - 1)...
                var chunkEnd = cursor.AddDays(chunkDays - 1);
                if (chunkEnd > window.End) chunkEnd = window.End;

                units.Add(Build(cursor, chunkEnd, scope, hot));

                // ...and the next chunk starts the DAY AFTER. *** Chunks must not share a boundary
                // date: *** both ends are inclusive (design §3.2 #1), so an overlap would
                // double-request rows - the MERGE would absorb it, but the row-cap arithmetic and the
                // FileLog row counts would both lie.
                cursor = chunkEnd.AddDays(1);
            }
        }

        if (window.Clamped)
            _logger.LogInformation(
                "[ModCom {Endpoint}] requested start was clamped by the 6-calendar-month history limit: " +
                "DaysBack={DaysBack} would begin {Requested}, clamped to {Clamped}",
                _d.EndpointId, daysBack, ModComTime.Iso(window.End.AddDays(-daysBack)), ModComTime.Iso(window.Start));

        _logger.LogDebug(
            "[ModCom {Endpoint}] enumerated {Count} work unit(s) over {From}..{To} ({Days} inclusive days, chunkDays={ChunkDays})",
            _d.EndpointId, units.Count, ModComTime.Iso(window.Start), ModComTime.Iso(window.End),
            window.LengthDays, chunkDays);

        return Task.FromResult<IReadOnlyList<ModComWorkUnit>>(units);
    }

    private ModComWorkUnit Build(DateOnly start, DateOnly end, string? scope, string hot) => new()
    {
        EndpointId = _d.EndpointId,
        WindowStart = start,
        WindowEnd = end,
        LegalEntityName = scope,
        RequestPath = BuildRequestPath(_d, start, end, scope),
        RunToken = hot,
        KeyValue = BuildKey(_d.EndpointId, start, end, scope, hot)
    };

    /// <summary>
    /// The sanitised relative request descriptor: path + the two (or three) query parameters, with
    /// both dates <b>always sent explicitly</b> rather than relying on the vendor's "defaults to
    /// today" (which is computed in the vendor's own zone). <c>legalEntityName</c> is URL-encoded —
    /// the valid values contain spaces <b>and a comma</b>.
    /// </summary>
    internal static string BuildRequestPath(ModComEndpointDescriptor d, DateOnly start, DateOnly end, string? scope)
    {
        var sb = new StringBuilder(d.Path)
            .Append("?startDate=").Append(ModComTime.Iso(start))
            .Append("&endDate=").Append(ModComTime.Iso(end));
        if (d.SupportsLegalEntityName && !string.IsNullOrWhiteSpace(scope))
            sb.Append("&legalEntityName=").Append(Uri.EscapeDataString(scope!));
        return sb.ToString();
    }

    /// <summary>
    /// The literal resume key (design §3.3). <c>core.LoadLog</c> skips a unit only when this exact
    /// string is already recorded <b>successful</b>, so these formats <i>are</i> the idempotency
    /// contract:
    /// <code>
    /// modcom:{endpointId}:{start:yyyyMMdd}-{end:yyyyMMdd}:run={hot}
    /// modcom:{endpointId}:{start:yyyyMMdd}-{end:yyyyMMdd}:entity={slug}:run={hot}
    /// </code>
    ///
    /// <para><c>{hot}</c> is <b>always present — there is no settled variant</b> (Rationale A).
    /// The dates are the <b>actual</b> ones requested after clamping and chunking, so a change to
    /// <c>DaysBack</c>/<c>ChunkDays</c> yields NEW keys rather than colliding with a differently
    /// scoped previous success.</para>
    /// </summary>
    internal static string BuildKey(string endpointId, DateOnly start, DateOnly end, string? scope, string hot)
    {
        var span = $"{start.ToString("yyyyMMdd", Inv)}-{end.ToString("yyyyMMdd", Inv)}";
        return string.IsNullOrWhiteSpace(scope)
            ? $"modcom:{endpointId}:{span}:run={hot}"
            : $"modcom:{endpointId}:{span}:entity={Slug(scope!)}:run={hot}";
    }

    /// <summary>
    /// Lowercases and collapses every non-alphanumeric run to a single <c>-</c>.
    ///
    /// <para><b>This is not cosmetic.</b> A scoped <c>myTrades</c> pull returns a <i>subset</i>, so
    /// without the slug in the key, switching <c>LegalEntityName</c> would make the run idempotently
    /// skip and quietly keep the old, wider data. The raw value never enters the key — it contains
    /// spaces and a comma.</para>
    /// </summary>
    internal static string Slug(string value)
    {
        var sb = new StringBuilder(value.Length);
        var pendingDash = false;
        foreach (var ch in value)
        {
            if (char.IsLetterOrDigit(ch))
            {
                if (pendingDash && sb.Length > 0) sb.Append('-');
                pendingDash = false;
                sb.Append(char.ToLowerInvariant(ch));
            }
            else
            {
                pendingDash = true;
            }
        }
        return sb.ToString();
    }
}
