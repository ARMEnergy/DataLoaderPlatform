using System.Globalization;
using System.Text;

namespace DataLoader.EvolutionMarkets;

/// <summary>
/// Computes <c>arm.MarketData.Checksum</c> — a change-detection hash over a row's 22 payload
/// columns, so the MERGE can skip the UPDATE when nothing actually changed (design §8.4).
///
/// <para><b>Why the column exists at all.</b> The API has no checksum, revision or version field of
/// any kind. The whole 30-day window is re-pulled every run (the all-hot default), and on a typical
/// run almost every row comes back byte-identical. Without a change guard the MERGE would re-stamp
/// <c>ModifiedAtUtc</c> on ~4,400 unchanged rows daily and the column would degrade into "time of
/// last run", answering nothing. With it, <c>ModifiedAtUtc</c> means <b>"when this price last
/// actually changed"</b>, which is the question anyone querying a revision would ask.</para>
///
/// <para><b>⚠ Why NOT <see cref="string.GetHashCode()"/>.</b> .NET Core randomises string hashing
/// <i>per process</i>: the same row would hash differently on every run, every row would look
/// changed, and the guard would be silently useless (worse than absent — it would look like it was
/// working). <b>Never</b> use <c>GetHashCode</c>, <c>HashCode.Combine</c>, or anything else
/// seed-dependent for a persisted value. FNV-1a below is a fixed, documented, seedless algorithm
/// with no runtime dependency.</para>
///
/// <para><b>⚠ Why NOT SQL's <c>BINARY_CHECKSUM</c>/<c>CHECKSUM</c>.</b> Neither is guaranteed stable
/// across SQL Server versions or collations, both are documented as collision-prone, and
/// <c>CHECKSUM</c> ignores trailing whitespace differences. Computing in C# also makes the value
/// reproducible in a unit test, which is the only way to pin it.</para>
///
/// <para><b>Collision posture.</b> 32 bits over ~4,400 rows/window is ample for a
/// <i>change detector</i>: a collision means one row's update is skipped for one run and is corrected
/// on the next run in which any of its values change again. It is NOT an integrity checksum and must
/// never be used as one. The column is <c>INT</c> because the target DDL specifies <c>INT</c>.</para>
/// </summary>
internal static class EvoChecksum
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>
    /// Field separator (ASCII US, <c>U+001F</c>), written as an ESCAPE and never as a literal
    /// control character, so the value cannot be mangled by an editor or a file-encoding round trip.
    ///
    /// <para>A separator is required for correctness, not tidiness: without one the field pairs
    /// <c>("ab", "c")</c> and <c>("a", "bc")</c> would produce an identical byte stream and hash
    /// alike.</para>
    /// </summary>
    private const char Sep = '\u001F';

    /// <summary>
    /// The NULL marker (ASCII NUL, <c>U+0000</c>), likewise an escape. Distinct from the empty
    /// string on purpose: <c>Tenor = null</c> and <c>Tenor = ""</c> must not hash alike, or a value
    /// being CLEARED by the vendor would not register as a change. Neither character occurs in any
    /// observed field value.
    /// </summary>
    private const string NullToken = "\u0000";

    private const uint FnvOffsetBasis = 2166136261;
    private const uint FnvPrime = 16777619;

    /// <summary>
    /// The canonical string a row hashes to. Exposed <c>internal</c> so a unit test can assert the
    /// exact canonical form rather than just the resulting number — the number alone would let a
    /// field-order change slip through as "some other valid hash".
    ///
    /// <para><b>Field order here is part of the contract</b> and mirrors the TVP column order
    /// (design §8.3). <c>MarketDataId</c> is deliberately EXCLUDED: it is the merge key, so it is
    /// identical on both sides of every MATCHED comparison and contributes nothing. <c>FileLogId</c>
    /// is EXCLUDED because it is provenance, not payload — a re-pull that changes nothing but the
    /// hub row must NOT count as a change.</para>
    /// </summary>
    internal static string Canonical(MarketDataRow r)
    {
        var sb = new StringBuilder(256);
        Append(sb, r.Market);
        Append(sb, r.Term);
        Append(sb, r.Term2);
        Append(sb, r.Tenor);
        Append(sb, r.InstrumentSourceName);
        Append(sb, r.InstrumentId);
        Append(sb, r.InstrumentName);
        Append(sb, r.PriceTs);
        Append(sb, r.BusinessDate);
        Append(sb, r.PriceType);
        Append(sb, r.Size);
        Append(sb, r.Depth);
        Append(sb, r.Price);
        Append(sb, r.Ask);
        Append(sb, r.AskSize);
        Append(sb, r.Bid);
        Append(sb, r.BidSize);
        Append(sb, r.Mid);
        Append(sb, r.MidSize);
        Append(sb, r.Change);
        Append(sb, r.PctRetDaily);
        Append(sb, r.Currency);
        return sb.ToString();
    }

    /// <summary>
    /// The row's checksum: FNV-1a/32 over <see cref="Canonical"/>, reinterpreted (not clamped) into
    /// the signed range so the full 32 bits survive the <c>INT</c> column.
    /// </summary>
    public static int Compute(MarketDataRow row)
    {
        var hash = FnvOffsetBasis;
        foreach (var b in Encoding.UTF8.GetBytes(Canonical(row)))
        {
            hash ^= b;
            hash *= FnvPrime;
        }

        // unchecked reinterpret: preserves all 32 bits (a cast would overflow-check in some contexts).
        return unchecked((int)hash);
    }

    private static void Append(StringBuilder sb, string? value) =>
        sb.Append(value is null ? NullToken : value).Append(Sep);

    private static void Append(StringBuilder sb, int? value) =>
        sb.Append(value?.ToString(Inv) ?? NullToken).Append(Sep);

    private static void Append(StringBuilder sb, Guid? value) =>
        sb.Append(value?.ToString("D", Inv) ?? NullToken).Append(Sep);

    private static void Append(StringBuilder sb, DateOnly? value) =>
        sb.Append(value?.ToString("yyyy-MM-dd", Inv) ?? NullToken).Append(Sep);

    /// <summary>
    /// <c>PriceTs</c> is a <c>DATETIME2(0)</c> column, so the canonical form is rendered at
    /// SECOND precision. Hashing sub-second digits the column cannot store would make a row look
    /// changed on every run.
    /// </summary>
    private static void Append(StringBuilder sb, DateTime? value) =>
        sb.Append(value?.ToString("yyyy-MM-dd HH:mm:ss", Inv) ?? NullToken).Append(Sep);

    /// <summary>
    /// Decimals are normalised to a FIXED 8-decimal-place rendering matching
    /// <c>DECIMAL(18,8)</c>.
    ///
    /// <para><b>⚠ This normalisation is required, not cosmetic.</b> <see cref="decimal"/> preserves
    /// trailing zeros in its scale, so <c>0.5m.ToString()</c> is <c>"0.5"</c> while
    /// <c>0.50m.ToString()</c> is <c>"0.50"</c> — two equal numbers, two different strings, two
    /// different hashes. The API returns JSON numbers whose scale varies row to row
    /// (<c>0</c>, <c>0.3</c>, <c>-0.0263</c>), so an un-normalised render would report phantom
    /// changes on rows whose value never moved.</para>
    ///
    /// <para>8 dp is exactly the target column's scale, so the hash sees precisely what the database
    /// will store — never more precision than survives the write (which would flag a change the
    /// column cannot actually represent) and never less (which would hide a real one).</para>
    /// </summary>
    private static void Append(StringBuilder sb, decimal? value) =>
        sb.Append(value?.ToString("F8", Inv) ?? NullToken).Append(Sep);
}
