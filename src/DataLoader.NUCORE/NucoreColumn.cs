namespace DataLoader.NUCORE;

/// <summary>
/// The scalar shapes the NUCORE endpoints use. Drives BOTH the JSON-to-CLR conversion in
/// <see cref="NucoreRowBuilder"/> and the DataTable column type in <see cref="NucoreTableSink"/>,
/// so the two cannot disagree about what a column holds.
/// </summary>
internal enum NucoreColumnKind
{
    /// <summary><c>VARCHAR(n)</c> -&gt; <see cref="string"/>.</summary>
    String,

    /// <summary>
    /// <c>DATETIME2(7)</c> -&gt; <see cref="DateTime"/>, parsed with
    /// <see cref="System.Globalization.DateTimeStyles.None"/> so the wall-clock value the
    /// vendor wrote is stored unshifted. These are business dates
    /// (<c>2026-10-14T00:00:00</c>); giving one an offset moves it across a day boundary
    /// for anyone east or west of the host.
    /// </summary>
    DateTime,

    /// <summary>
    /// <c>DATETIMEOFFSET(7)</c> -&gt; <see cref="System.DateTimeOffset"/>. Only the three
    /// audit stamps the vendor sends WITH an offset (<c>createdAt</c>, <c>modifiedAt</c>,
    /// <c>originalModifiedAt</c>) use this; the offset is preserved rather than discarded.
    /// </summary>
    DateTimeOffset,

    /// <summary><c>INT</c> -&gt; <see cref="int"/>.</summary>
    Int32,

    /// <summary><c>BIGINT</c> -&gt; <see cref="long"/>.</summary>
    Int64,

    /// <summary>
    /// <c>DECIMAL(28,12)</c> -&gt; <see cref="decimal"/>, ROUNDED to
    /// <see cref="NucoreColumns.DecimalScale"/> on conversion.
    ///
    /// <para>
    /// The vendor declares these as <c>double</c>. They are money and risk numbers that get
    /// SUMmed downstream, where binary floating point drifts, so they land as exact decimal.
    /// The rounding happens here rather than on the server so that a value with more digits
    /// than the column holds is a visible, testable decision instead of a silent truncation
    /// inside SqlClient.
    /// </para>
    /// </summary>
    Decimal,

    /// <summary><c>BIT</c> -&gt; <see cref="bool"/>.</summary>
    Boolean,

    /// <summary><c>UNIQUEIDENTIFIER</c> -&gt; <see cref="System.Guid"/>.</summary>
    Guid
}

/// <summary>
/// One column of one endpoint. This single record drives four things that would otherwise
/// have to be kept in step by hand across 436 columns:
///
/// <list type="number">
///   <item>which JSON property the reader looks for (<see cref="JsonName"/>),</item>
///   <item>how the value is converted (<see cref="Kind"/>),</item>
///   <item>the DataTable column name, ORDER and CLR type in the sink,</item>
///   <item>the expected TVP column in <c>sql/NUCORE/002</c>, asserted by
///         <c>NucoreTvpContractTests</c> against <see cref="SqlType"/> and
///         <see cref="Nullable"/>.</item>
/// </list>
///
/// <para>
/// Because a TVP binds BY POSITION, the ORDER of an endpoint's column list IS the contract.
/// Both this list and the <c>.sql</c> are emitted by
/// <c>scripts/Generate-NucoreSchema.ps1</c> from the vendor's own OpenAPI document, and a
/// test re-runs the generator in <c>-Check</c> mode, so the two cannot drift apart.
/// </para>
/// </summary>
/// <param name="Name">TVP / DataTable / table column name (PascalCase).</param>
/// <param name="JsonName">The vendor's JSON property name (camelCase).</param>
/// <param name="SqlType">Exact SQL type text as it appears in 002, e.g. <c>VARCHAR(200)</c>.</param>
/// <param name="Kind">Conversion + CLR type.</param>
/// <param name="Nullable">
/// <c>true</c> =&gt; <c>NULL</c> in the TVP. False for every key column: a string key is
/// coalesced to <c>''</c> on the way in so the natural key can be a real PRIMARY KEY and
/// the MERGE join needs no NULL-safe comparison.
/// </param>
/// <param name="IsKey">
/// <c>true</c> =&gt; part of the table's natural primary key. Key membership was established
/// empirically against full live days, not assumed - see <c>docs/apis/NUCORE.md</c> §3.
/// </param>
/// <param name="KeyOrdinal">
/// 1-based position within the primary key, or 0 for a non-key column.
///
/// <para>
/// Carried separately because the key's order is NOT the column list's order. The column
/// list follows the vendor's spec, where the date sits wherever the vendor happened to put
/// it - <c>PositionDetails</c> declares <c>systemDate</c> as its 58th property. The primary
/// key deliberately LEADS with the date so the clustered index groups a day together and
/// day-range reads are a seek rather than a scan. Deriving the key order from the column
/// order instead would silently reverse that.
/// </para>
/// </param>
internal sealed record NucoreColumn(
    string Name,
    string JsonName,
    string SqlType,
    NucoreColumnKind Kind,
    bool Nullable,
    bool IsKey,
    int KeyOrdinal = 0)
{
    /// <summary>The CLR type this column's DataTable column is declared with.</summary>
    public Type ClrType => Kind switch
    {
        NucoreColumnKind.String         => typeof(string),
        NucoreColumnKind.DateTime       => typeof(DateTime),
        NucoreColumnKind.DateTimeOffset => typeof(DateTimeOffset),
        NucoreColumnKind.Int32          => typeof(int),
        NucoreColumnKind.Int64          => typeof(long),
        NucoreColumnKind.Decimal        => typeof(decimal),
        NucoreColumnKind.Boolean        => typeof(bool),
        NucoreColumnKind.Guid           => typeof(Guid),
        _ => throw new NotSupportedException($"Unmapped column kind {Kind}")
    };

    /// <summary>
    /// Declared length of a <c>VARCHAR(n)</c> column, or <c>null</c> for anything else.
    /// The row builder uses it to fail loudly on an over-long value rather than letting
    /// SqlClient raise a "String or binary data would be truncated" that names no column.
    /// </summary>
    public int? MaxLength =>
        Kind == NucoreColumnKind.String &&
        SqlType.StartsWith("VARCHAR(", StringComparison.Ordinal) &&
        int.TryParse(SqlType.AsSpan(8, SqlType.Length - 9), out var n)
            ? n
            : null;
}
