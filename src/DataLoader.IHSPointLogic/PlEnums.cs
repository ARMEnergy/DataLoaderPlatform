namespace DataLoader.IHSPointLogic;

/// <summary>The two API families (design §2). Informational — the reader treats both uniformly.</summary>
public enum PlFamily
{
    /// <summary>PointLogic — <c>cs/v1/pointlogic/*</c> (lookups + supplyDemand).</summary>
    P,

    /// <summary>PLView retrieve — <c>cs/v1/plview/retrieve/*</c> (bulk fact snapshots).</summary>
    V
}

/// <summary>
/// The five work-unit archetypes (design §4). The archetype selects which provider
/// fills a <see cref="PlWorkUnit"/> and how its resume key is built.
/// </summary>
public enum PlArchetype
{
    /// <summary>A — Tier-0 dimension: one hot unit/run, id-PK MERGE.</summary>
    LatestLookup,

    /// <summary>B — Tier-0 fact snapshot: one hot unit/run keyed by the run's UTC date.</summary>
    GoForwardSnapshot,

    /// <summary>C — Tier-1 discovery-fed lookup: one hot unit per parent id.</summary>
    DiscoveryLookup,

    /// <summary>D — Tier-2 dated fact: one unit per (parent id × reportDate), two-zone key.</summary>
    DiscoveryDatedFact,

    /// <summary>E — Tier-2 batched fact: one hot unit per ≤N-id batch (PointVolume).</summary>
    BatchedFact
}

/// <summary>The path parameter a descriptor's <c>{id}</c> substitution carries (design §2).</summary>
public enum PlPathParam
{
    None,
    StateId,
    PointTypeId,
    RegionId,
    SubRegionId,
    PointBatch
}

/// <summary>How a row's report/forecast date is sourced (design §2 / §7).</summary>
public enum PlReportDateBasis
{
    /// <summary>No report-date column participates.</summary>
    None,

    /// <summary>The payload carries the date(s) — used as-is.</summary>
    PayloadCarried,

    /// <summary>The payload lacks a report date — stamp the run's UTC calendar date (demandforecast_*).</summary>
    StampUtcRunDate,

    /// <summary>The date is injected from the <c>?reportDate=</c> query param (archetype D).</summary>
    ParamInjected
}

/// <summary>How the hot-zone / snapshot resume key varies between runs (design §4 / §B.4). Mirrors CWG/AGSI.</summary>
public enum PlHotKeyStrategy
{
    /// <summary>Suffix the key with the UTC run date (<c>yyyyMMdd</c>) → one re-pull per calendar day.</summary>
    RunDate,

    /// <summary>Suffix the key with the run id → every invocation re-pulls.</summary>
    RunId,

    /// <summary>
    /// Suffix the key with the UTC run hour (<c>yyyyMMddHH</c>) → one re-pull per clock hour, while a
    /// same-hour re-run still idempotently skips (design §B.4). The default, matching the hourly host schedule.
    /// </summary>
    RunHour
}

/// <summary>
/// Informational envelope hint (design §2). The reader auto-detects and pages BOTH
/// shapes (flat root array vs a <c>PagingInfo</c>/<c>Data</c> wrapper) regardless.
/// </summary>
public enum PlEnvelope
{
    /// <summary>Flat JSON array at the root.</summary>
    FlatArray,

    /// <summary>Object wrapper with <c>Data</c> array + <c>PagingInfo</c>.</summary>
    Wrapper
}
