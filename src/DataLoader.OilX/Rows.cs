namespace DataLoader.OilX;

/// <summary>
/// One target row, as a positional value array in
/// <see cref="OilXFeedDescriptor.Columns"/> order.
///
/// <para>
/// A positional array rather than eight hand-written POCOs: the feeds differ only in
/// their column lists, and the TVP binds BY POSITION anyway. Because
/// <see cref="OilXSourceReader"/> fills the array from the descriptor's column list and
/// <see cref="OilXTableSink.BuildTable"/> reads it back from the same list, the two
/// sides cannot drift from one another — the drift that remains possible is descriptor
/// versus <c>.sql</c>, which <c>OilXTvpContractTests</c> catches at build time.
/// </para>
/// <para>
/// Entries are already-converted CLR values or <see cref="DBNull.Value"/> — never raw
/// CSV text.
/// </para>
/// </summary>
/// <param name="Values">One entry per descriptor column, in descriptor order.</param>
internal sealed record OilXRow(object[] Values);
