namespace DataLoader.ICE;

/// <summary>
/// One parsed row, positionally aligned to its table's
/// <see cref="IceTableDescriptor.Columns"/>.
///
/// <para>
/// This is deliberately NOT a per-table POCO. There are 12 target tables whose only
/// difference is their column list, so 12 row classes would mean 12
/// <c>BuildTable</c> methods — 12 independent chances for the DataTable/TVP
/// position drift that <c>tvp-contract-check</c> exists to catch. Here the
/// descriptor is the single source of truth: the reader fills this array in
/// descriptor order and the sink reads it back in descriptor order, so the two
/// cannot disagree.
/// </para>
/// <para>
/// <see cref="Values"/> never contains a CLR <c>null</c> — an absent value is
/// <see cref="DBNull.Value"/>, which is what <c>DataTable.Rows.Add</c> wants.
/// </para>
/// </summary>
public sealed class IceRow
{
    public IceRow(object[] values) => Values = values;

    /// <summary>Values in descriptor column order. Length always equals the column count.</summary>
    public object[] Values { get; }
}
