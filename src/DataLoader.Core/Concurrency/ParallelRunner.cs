using System.Collections.Concurrent;

namespace DataLoader.Core.Concurrency;

/// <summary>
/// Runs an async function over a collection of items with bounded concurrency.
///
/// This is a single, tested implementation of the pattern the original
/// <c>MappingOrchestrator</c> open-coded with <c>SemaphoreSlim</c>. Pulling
/// it out means every loader gets the same well-behaved concurrency, and
/// none of them have to reinvent it.
///
/// Failures in individual items do not stop the others. Cancellation is
/// observed and propagated.
/// </summary>
public static class ParallelRunner
{
    public sealed record ItemResult<T>(T Item, bool Success, Exception? Error);

    public static async Task<IReadOnlyList<ItemResult<T>>> RunAsync<T>(
        IEnumerable<T> items,
        int maxConcurrency,
        Func<T, CancellationToken, Task> action,
        CancellationToken cancellationToken)
    {
        if (maxConcurrency < 1) maxConcurrency = 1;

        using var semaphore = new SemaphoreSlim(maxConcurrency);
        var results = new ConcurrentBag<ItemResult<T>>();

        var tasks = items.Select(async item =>
        {
            await semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await action(item, cancellationToken).ConfigureAwait(false);
                results.Add(new ItemResult<T>(item, Success: true, Error: null));
            }
            catch (OperationCanceledException)
            {
                // Bubble up — the whole run is cancelled
                throw;
            }
            catch (Exception ex)
            {
                results.Add(new ItemResult<T>(item, Success: false, Error: ex));
            }
            finally
            {
                semaphore.Release();
            }
        });

        await Task.WhenAll(tasks).ConfigureAwait(false);
        return results.ToList();
    }
}
