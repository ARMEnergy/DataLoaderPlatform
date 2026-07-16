using DataLoader.Core.Abstractions;

namespace DataLoader.Core.Transforms;

/// <summary>
/// No-op transformer for loaders where the source items are already in the
/// shape the sink wants.
/// </summary>
public sealed class IdentityTransformer<T> : ITransformer<T, T>
{
    public IReadOnlyList<T> Transform(IReadOnlyList<T> items, WorkUnit unit) => items;
}
