namespace DataLoader.Core.Abstractions;

/// <summary>
/// Converts source items (what came back from the source) into target rows
/// (what gets written to the sink).
///
/// Many loaders need a real transform — flattening nested JSON into rows,
/// parsing dates, deriving fields. Some don't (TItem == TRow); in that case
/// register <see cref="Transforms.IdentityTransformer{T}"/>.
/// </summary>
public interface ITransformer<TItem, TRow>
{
    IReadOnlyList<TRow> Transform(IReadOnlyList<TItem> items, WorkUnit unit);
}
