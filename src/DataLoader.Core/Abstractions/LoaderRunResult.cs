namespace DataLoader.Core.Abstractions;

/// <summary>
/// Summary of what a single loader did during one run.
/// Returned by <see cref="ILoaderModule.RunAsync"/>.
/// </summary>
public sealed class LoaderRunResult
{
    public required string LoaderId { get; init; }
    public required bool Success { get; init; }
    public int WorkUnitsTotal { get; init; }
    public int WorkUnitsSucceeded { get; init; }
    public int WorkUnitsSkipped { get; init; }
    public int WorkUnitsFailed { get; init; }
    public int RecordsProcessed { get; init; }
    public string? ErrorMessage { get; init; }
    public TimeSpan Duration { get; init; }

    public static LoaderRunResult Failed(string loaderId, string message, TimeSpan duration) => new()
    {
        LoaderId = loaderId,
        Success = false,
        ErrorMessage = message,
        Duration = duration
    };
}
