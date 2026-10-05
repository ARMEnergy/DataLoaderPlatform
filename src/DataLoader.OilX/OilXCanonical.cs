using System.Text;

namespace DataLoader.OilX;

/// <summary>
/// The canonical text form of a row's values — the single rendering shared by
/// <see cref="OilXRowId"/> (which hashes the KEY columns) and
/// <see cref="OilXChecksum"/> (which hashes the VALUE columns).
///
/// <para>
/// Both must render identically forever: a change to how a <see cref="decimal"/> or a
/// <see cref="DateTime"/> is written would re-key every row in
/// <c>arm.*</c> — the merge would stop matching and the tables would silently fork
/// into old-derivation and new-derivation copies. Keeping one implementation means
/// there is one thing to pin in a test rather than two that can drift apart.
/// </para>
/// </summary>
internal static class OilXCanonical
{
    /// <summary>
    /// Field separator (ASCII US, <c>U+001F</c>), written as an ESCAPE and never as a
    /// literal control character so it cannot be mangled by an editor or an encoding
    /// round trip.
    ///
    /// <para>
    /// A separator is required for correctness, not tidiness: without one the field
    /// pairs <c>("ab", "c")</c> and <c>("a", "bc")</c> produce an identical byte stream
    /// and hash alike. That is not hypothetical here — Flow's key is seven mostly-text
    /// columns, several of them usually empty.
    /// </para>
    /// </summary>
    public const char Separator = '\u001F';

    /// <summary>
    /// The NULL marker (ASCII NUL, <c>U+0000</c>). Distinct from the empty string on
    /// purpose: a column the vendor CLEARS must register as a change, and an absent
    /// key component must not collide with a present-but-empty one. Neither character
    /// occurs in any observed field value.
    /// </summary>
    public const string NullToken = "\u0000";

    /// <summary>
    /// Append one already-converted cell value in canonical form, followed by
    /// <see cref="Separator"/>.
    ///
    /// <para>
    /// The input is what <see cref="OilXCsv.TryConvert"/> produced — a CLR value or
    /// <see cref="DBNull.Value"/> — so the rendering below is keyed to the CLR types
    /// the descriptors declare, and an unexpected type throws rather than falling back
    /// on <c>ToString()</c> (which would be culture-dependent and silently wrong).
    /// </para>
    /// </summary>
    public static void Append(StringBuilder sb, object? value)
    {
        switch (value)
        {
            case null:
            case DBNull:
                sb.Append(NullToken);
                break;

            case string s:
                sb.Append(s);
                break;

            // ⚠ FIXED 8-dp rendering, matching DECIMAL(p,8) and OilXCsv.DecimalScale.
            // decimal preserves trailing zeros in its scale, so 0.5m renders "0.5" and
            // 0.50m renders "0.50" — two equal numbers, two different strings, two
            // different hashes. The feeds publish varying scales row to row ("195",
            // "5.40", "11.0"), so an un-normalised render would report phantom changes
            // on rows whose value never moved.
            case decimal m:
                sb.Append(m.ToString("F8", OilXTime.Inv));
                break;

            // "R" round-trips a double exactly, so two doubles that are equal render
            // alike and two that differ in the last bit do not. F-formatting a FLOAT
            // column would quietly call 78.01 and 78.01000000000001 the same row.
            case double d:
                sb.Append(d.ToString("R", OilXTime.Inv));
                break;

            // Full DATETIME2(7) precision. Rendering less would hide a real change in
            // the sub-second digits CargoTracking's LoadDate actually carries; more is
            // not available.
            case DateTime dt:
                sb.Append(dt.ToString("yyyy-MM-dd HH:mm:ss.fffffff", OilXTime.Inv));
                break;

            case Guid g:
                sb.Append(g.ToString("D", OilXTime.Inv));
                break;

            case int i:
                sb.Append(i.ToString(OilXTime.Inv));
                break;

            default:
                throw new NotSupportedException(
                    $"OilXCanonical cannot render a value of type '{value.GetType().Name}'. " +
                    "Every descriptor column maps to string, decimal, double, DateTime, Guid or int.");
        }

        sb.Append(Separator);
    }
}
