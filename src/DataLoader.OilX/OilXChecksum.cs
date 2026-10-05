using System.Text;

namespace DataLoader.OilX;

/// <summary>
/// Computes the <c>Checksum</c> column — a change-detection hash over a row's VALUE
/// columns, so each merge proc can skip the UPDATE when nothing actually changed
/// (<c>docs/design/OilX.md</c> §7).
///
/// <para><b>Why the column matters more here than anywhere else in this repo.</b> The
/// CSVs carry no revision, version or status field, every daily file is a FULL snapshot
/// of all history, and the busiest feeds publish FOUR of those a day. On a typical run
/// almost every one of ~2 million rows per day comes back byte-identical to the one
/// before it. Without a change guard the MERGE would re-stamp <c>ModifiedAtUtc</c> on
/// all of them, and the column would degrade into "time of last run" — answering
/// nothing. With it, <c>ModifiedAtUtc</c> means <b>"when this row's values last
/// actually changed"</b>, which is the only way to see a vendor revision.</para>
///
/// <para><b>⚠ Why NOT <see cref="string.GetHashCode()"/>.</b> .NET Core randomises
/// string hashing <i>per process</i>: the same row would hash differently on every run,
/// every row would look changed, and the guard would be silently useless — worse than
/// absent, because it would look like it was working. <b>Never</b> use
/// <c>GetHashCode</c>, <c>HashCode.Combine</c>, or anything else seed-dependent for a
/// persisted value. FNV-1a below is a fixed, documented, seedless algorithm.</para>
///
/// <para><b>⚠ Why NOT SQL's <c>BINARY_CHECKSUM</c>/<c>CHECKSUM</c>.</b> Neither is
/// guaranteed stable across SQL Server versions or collations, both are documented as
/// collision-prone, and <c>CHECKSUM</c> ignores trailing-whitespace differences.
/// Computing in C# also makes the value reproducible in a unit test, which is the only
/// way to pin it.</para>
///
/// <para><b>Collision posture.</b> 32 bits is ample for a <i>change detector</i>: a
/// collision means one row's update is skipped for one run and is corrected on the next
/// run in which any of its values change again. It is NOT an integrity checksum and
/// must never be used as one. The column is <c>INT</c> because the supplied DDL
/// specifies <c>INT</c>.</para>
/// </summary>
internal static class OilXChecksum
{
    private const uint FnvOffsetBasis = 2166136261;
    private const uint FnvPrime = 16777619;

    /// <summary>
    /// The canonical string a row hashes to: every column that is neither part of the
    /// primary key, nor the business key, nor provenance — in descriptor order.
    ///
    /// <para><b>What is excluded, and why each exclusion is deliberate:</b></para>
    /// <list type="bullet">
    ///   <item><c>RunDate</c> and <c>RowId</c> — the primary key. Identical on both
    ///         sides of every MATCHED comparison, so they contribute nothing.</item>
    ///   <item>the business key columns (<see cref="OilXColumn.IsKey"/>) — likewise
    ///         identical on both sides, since <c>RowId</c> is derived from exactly
    ///         them.</item>
    ///   <item><c>FileName</c> — PROVENANCE, not payload. A later snapshot that changed
    ///         nothing must not count as a change, or the guard would fire four times a
    ///         day on every row and the column would mean nothing again. It is still
    ///         UPDATEd on match, so it always names the snapshot that last wrote the
    ///         row.</item>
    ///   <item><c>Checksum</c> itself.</item>
    /// </list>
    ///
    /// <para>
    /// Exposed <c>internal</c> so a test can assert the exact canonical form rather than
    /// just the resulting number — the number alone would let a field-order change slip
    /// through as "some other valid hash".
    /// </para>
    /// </summary>
    internal static string Canonical(OilXFeedDescriptor feed, IReadOnlyDictionary<string, object?> values)
    {
        var sb = new StringBuilder(256);

        foreach (var column in feed.Columns)
        {
            if (!IsValueColumn(column)) continue;

            values.TryGetValue(column.Name, out var value);
            OilXCanonical.Append(sb, value);
        }

        return sb.ToString();
    }

    /// <summary>True when a column contributes to the checksum. See <see cref="Canonical"/>.</summary>
    internal static bool IsValueColumn(OilXColumn column) =>
        column.FromCsv
        && !column.IsKey
        && !string.Equals(column.Name, "RunDate", StringComparison.Ordinal);

    /// <summary>
    /// The row's checksum: FNV-1a/32 over <see cref="Canonical"/>, reinterpreted (not
    /// clamped) into the signed range so the full 32 bits survive the <c>INT</c> column.
    /// </summary>
    public static int Compute(OilXFeedDescriptor feed, IReadOnlyDictionary<string, object?> values)
    {
        var hash = FnvOffsetBasis;

        foreach (var b in Encoding.UTF8.GetBytes(Canonical(feed, values)))
        {
            hash ^= b;
            hash *= FnvPrime;
        }

        // unchecked reinterpret: preserves all 32 bits (a plain cast would
        // overflow-check in a checked context).
        return unchecked((int)hash);
    }
}
