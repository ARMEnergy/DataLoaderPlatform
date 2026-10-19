namespace DataLoader.EIA;

/// <summary>
/// One row of <c>arm.Series</c>, as returned by <c>arm.usp_GetActiveSeries</c>.
/// </summary>
internal sealed class EiaSeries
{
    public required string SeriesId { get; init; }
    public string? Name { get; init; }
    public string? ParentSeriesId { get; init; }

    /// <summary>
    /// First period of the series, written in <see cref="DefaultDateFormat"/>.
    /// <b>The loader never writes this back</b> — the request is explicit that it must
    /// not change.
    /// </summary>
    public string? StartPeriod { get; init; }

    /// <summary>
    /// Latest period available, written in <see cref="DefaultDateFormat"/>. Stamped
    /// advance-only from the API's metadata route by
    /// <c>arm.usp_UpdateSeriesEndPeriod</c>.
    /// </summary>
    public string? EndPeriod { get; init; }

    public string? DefaultDateFormat { get; init; }
    public string? DefaultFrequency { get; init; }
    public long? TotalRecordCount { get; init; }

    /// <summary>
    /// NULL on every row of the live catalog. NULL means "use
    /// <see cref="EiaSettings.DefaultAllowIncremental"/>", never <c>false</c> —
    /// reading it as false would put all 95 series into full-reload mode.
    /// </summary>
    public bool? AllowIncremental { get; init; }
}

/// <summary>
/// One row of <c>arm.SeriesFrequency</c>, as returned by
/// <c>arm.usp_GetSeriesFrequencies</c>.
/// </summary>
internal sealed class EiaFrequency
{
    public required string SeriesId { get; init; }
    public required string FrequencyId { get; init; }

    /// <summary>The vendor's short code (<c>A</c>, <c>M</c>, <c>D</c>, …). Not used in requests.</summary>
    public string? Query { get; init; }

    /// <summary>The period mask. See <see cref="EiaPeriod.TryParseMask"/>.</summary>
    public string? Format { get; init; }

    /// <summary>NULL on every row of the live catalog; see <c>sql/EIA/003</c>.</summary>
    public bool? Enabled { get; init; }
}

/// <summary>
/// One row of <c>arm.SeriesDataType</c>, as returned by
/// <c>arm.usp_GetSeriesDataTypes</c>.
/// </summary>
internal sealed class EiaDataType
{
    public required string SeriesId { get; init; }

    /// <summary>What becomes <c>data[n]=</c> in the request URL.</summary>
    public required string DataTypeId { get; init; }

    public string? Units { get; init; }
    public string? Alias { get; init; }

    /// <summary>NULL on every row of the live catalog; see <c>sql/EIA/003</c>.</summary>
    public bool? Enabled { get; init; }
}

/// <summary>
/// A series together with everything needed to request it: its frequencies, its data
/// types, and the period bounds resolved for this run.
/// </summary>
internal sealed class EiaSeriesPlan
{
    public required EiaSeries Series { get; init; }
    public required IReadOnlyList<EiaFrequency> Frequencies { get; init; }
    public required IReadOnlyList<EiaDataType> DataTypes { get; init; }

    /// <summary>
    /// The set of field names that belong in <c>DataJSON</c> rather than
    /// <c>FacetJSON</c>: every <c>DataTypeId</c>, every <c>&lt;DataTypeId&gt;-units</c>,
    /// and the bare <c>units</c>.
    ///
    /// <para>
    /// <b>Verified, not assumed.</b> Checked against one sampled row from each of the
    /// 134 series present in the incumbent <c>dbo.SeriesData</c>: in 134/134 every
    /// DataJSON key fell inside this set, and in 137/138 no FacetJSON key did. The one
    /// exception is a Conduit defect, not a rule —
    /// <c>electricity/rto/daily-region-data</c> has rows refreshed on 2026-06-27 that
    /// put <c>value-units</c> in the FACET object while rows refreshed on 2026-03-30
    /// correctly put it in the data object. The API sends it as a data-units field, so
    /// this loader places it there for every row and does not reproduce the defect.
    /// (That defect is itself a source of duplicate rows in the incumbent: moving a
    /// field between the two objects changes FacetJSON, which changes FacetKey, which
    /// is part of the primary key.)
    /// </para>
    /// <para>
    /// <c>unit</c> and <c>unitName</c> SINGULAR are NOT in the set and are facets — the
    /// <c>international</c> series carries both as facets while its only data field is
    /// <c>value</c>.
    /// </para>
    /// </summary>
    public required IReadOnlySet<string> DataFieldNames { get; init; }

    /// <summary>
    /// Latest period the API says it has, in the series' own default mask. Read from
    /// the metadata route (<c>/v2/&lt;SeriesId&gt;</c>, no <c>/data</c>), which is the
    /// authority for it. Null when the metadata call failed.
    /// </summary>
    public string? ApiEndPeriod { get; init; }

    /// <summary>
    /// First period the API says it has. Recorded for logging only — the catalog's
    /// <c>StartPeriod</c> is what the loader actually tiles from, because the request
    /// says it must not change.
    /// </summary>
    public string? ApiStartPeriod { get; init; }
}

/// <summary>
/// One row bound for <c>arm.SeriesData</c>.
///
/// <para>
/// Field order mirrors the table and the TVP, which is the contract a table-valued
/// parameter binds by. <c>ModifiedAtUtc</c> is absent on purpose: it is stamped by the
/// merge proc and is never a TVP column.
/// </para>
/// </summary>
internal sealed record EiaRow(
    string SeriesId,
    string FrequencyId,
    string Period,
    int FacetKey,
    string FacetJson,
    string DataJson,
    int DataHash);
