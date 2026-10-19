namespace DataLoader.EIA;

/// <summary>
/// The 32-bit string hash behind <c>arm.SeriesData.FacetKey</c> and
/// <c>arm.SeriesData.DataHash</c>.
///
/// <para>
/// <b>This is not a free choice.</b> Both columns already exist, populated, in the
/// incumbent <c>dbo.SeriesData</c> (~94 million rows), and the loader request supplies
/// a worked example whose values must be reproduced exactly:
/// </para>
/// <code>
///   {"marketTypeDescription":"Open Market","marketTypeId":"OM",
///    "stateRegionDescription":"Missouri","stateRegionId":"MO"}        -> -1622649321
///   {"price":"w","price-units":"dollars per short ton",
///    "sales":44163,"sales-units":"short tons"}                        ->  -764207628
/// </code>
/// <para>
/// The algorithm was recovered by testing candidates against those stored values and
/// is the <b>non-randomised .NET Framework (x64) <c>String.GetHashCode</c></b>. It was
/// then verified on <b>20,000 live rows sampled across the whole incumbent table:
/// 20,000/20,000 exact on FacetKey and 20,000/20,000 exact on DataHash</b>, with zero
/// mismatches (<c>docs/design/EIA.md</c> §6). SQL's <c>CHECKSUM</c> and
/// <c>BINARY_CHECKSUM</c>, Java's <c>h*31+c</c>, FNV-1a, CRC32 and MD5-truncation were
/// all tested and all ruled out.
/// </para>
///
/// <para>
/// <b>⚠ WHY THIS IS HAND-WRITTEN AND NOT <see cref="string.GetHashCode()"/>.</b> The
/// platform targets .NET 8, where string hashing is <b>Marvin with a per-process random
/// seed</b>. Calling <c>GetHashCode()</c> here would produce a different FacetKey on
/// every run: every row would miss its merge target, and the table would grow a fresh
/// copy of all ~90 million rows per run instead of updating in place. The repo already
/// bans seed-dependent hashing for persisted values (see
/// <c>src/DataLoader.OilX/OilXChecksum.cs</c>); this file is the one place where the
/// required algorithm <i>happens</i> to be an old runtime's internal one, so it is
/// reimplemented in full, pinned by test, and never delegated to the runtime.
/// </para>
///
/// <para>
/// <b>Collision posture.</b> 32 bits over a facet object is a key, not a digest.
/// Within one (SeriesId, FrequencyId, Period) there are at most a few thousand distinct
/// facet combinations, so a collision inside a single key group is remote; and
/// <c>arm.usp_BulkMergeSeriesData</c> re-writes <c>FacetJSON</c> whenever the stored
/// text disagrees with the incoming text under an equal key, so a collision shows up as
/// a row that flip-flops rather than as silent corruption. This matches the incumbent's
/// behaviour exactly, which is the requirement.
/// </para>
/// </summary>
internal static class EiaLegacyHash
{
    /// <summary>
    /// The hash of <paramref name="value"/>.
    ///
    /// <para>
    /// The original ran over a NUL-terminated UTF-16 buffer and consumed two chars per
    /// iteration, folding even-indexed chars into <c>hash1</c> and odd-indexed ones into
    /// <c>hash2</c>. An embedded NUL therefore TERMINATED the walk — that behaviour is
    /// reproduced below rather than "fixed", because the goal is byte-identical output,
    /// not a better hash. (No EIA value contains a NUL: the canonical writer escapes
    /// every control character, so a literal U+0000 cannot reach here.)
    /// </para>
    ///
    /// <para>
    /// <c>unchecked</c> is explicit so the arithmetic keeps wrapping even if the project
    /// is ever built with <c>CheckForOverflowUnderflow</c>; an <c>OverflowException</c>
    /// here would take out a loader that is otherwise correct.
    /// </para>
    /// </summary>
    public static int Compute(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        unchecked
        {
            var hash1 = 5381;
            var hash2 = hash1;

            var i = 0;
            while (i < value.Length)
            {
                int c = value[i];
                if (c == 0) break;                       // embedded NUL ends the walk
                hash1 = ((hash1 << 5) + hash1) ^ c;

                if (i + 1 >= value.Length) break;        // odd length: the terminator
                int c2 = value[i + 1];
                if (c2 == 0) break;                      // embedded NUL ends the walk
                hash2 = ((hash2 << 5) + hash2) ^ c2;

                i += 2;
            }

            return hash1 + (hash2 * 1566083941);
        }
    }
}
