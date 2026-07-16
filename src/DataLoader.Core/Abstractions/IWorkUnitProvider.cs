namespace DataLoader.Core.Abstractions;

/// <summary>
/// Produces the work units the pipeline should process on a run.
///
/// This is where loaders differ most:
///   - EnergyAspects fetches the current mapping list from its API and
///     produces (mapping × date-window) units.
///   - An FTP loader lists remote files and produces one unit per file.
///   - A CSV-drop loader scans a directory.
///
/// Implementations may do any preparatory work — refreshing metadata,
/// reconciling source vs. destination, fetching schemas — before yielding
/// the unit list. The pipeline calls this once at the start of a run.
/// </summary>
public interface IWorkUnitProvider<TUnit> where TUnit : WorkUnit
{
    Task<IReadOnlyList<TUnit>> GetWorkUnitsAsync(LoaderRunContext context);
}
