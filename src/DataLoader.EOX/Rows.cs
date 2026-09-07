namespace DataLoader.EOX;

/// <summary>
/// One parsed row, positionally aligned to its feed's
/// <see cref="EoxFeedDescriptor.Columns"/>.
///
/// <para>
/// Deliberately NOT three per-feed POCOs. The three feeds differ only in their
/// column list, so three row classes would mean three <c>BuildTable</c> methods —
/// three independent chances for the DataTable/TVP position drift that
/// <c>tvp-contract-check</c> exists to catch. Here the descriptor is the single
/// source of truth: the reader fills this array in descriptor order and the sink
/// reads it back in descriptor order, so the two cannot disagree.
/// </para>
/// <para>
/// <see cref="Values"/> never contains a CLR <c>null</c> — an absent value is
/// <see cref="DBNull.Value"/>, which is what <c>DataTable.Rows.Add</c> wants.
/// </para>
/// </summary>
public sealed class EoxRow
{
    public EoxRow(object[] values) => Values = values;

    /// <summary>Values in descriptor column order. Length always equals the column count.</summary>
    public object[] Values { get; }
}
