namespace DataLoader.Core.Abstractions;

/// <summary>
/// The Extract → Transform → Load pipeline for one loader.
///
/// Loaders typically use <see cref="Pipeline.LoaderPipelineBase{TUnit,TItem,TRow}"/>,
/// which implements the standard loop:
///
///     get work units → for each unit (parallel, bounded):
///         already done? → skip
///         else → open load log → read → transform → sink → close load log
///
/// A loader can implement <see cref="ILoaderPipeline"/> directly if it needs
/// a non-standard shape (multi-stage, dependency between units, etc).
/// </summary>
public interface ILoaderPipeline
{
    Task<LoaderRunResult> ExecuteAsync(LoaderRunContext context);
}
