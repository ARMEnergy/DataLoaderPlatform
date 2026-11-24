using Xunit;

namespace DataLoader.PJM.Tests;

/// <summary>Design §12.2 test 45: the sliding-window-log limiter (§10.1) on a manual clock.</summary>
public sealed class RateLimiterTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 23, 14, 20, 0, TimeSpan.Zero);

    private static async Task<bool> CompletesSoon(Task t) =>
        await Task.WhenAny(t, Task.Delay(TimeSpan.FromSeconds(5))) == t;

    private static async Task<bool> StillPending(Task t)
    {
        await Task.Delay(100);   // give a wrongly-released continuation a chance to run
        return !t.IsCompleted;
    }

    /// <summary>Waits (real time, bounded) until the limiter has parked on its fake timer.</summary>
    private static async Task WaitForTimer(ManualTimeProvider clock)
    {
        for (var i = 0; i < 200 && clock.PendingTimers == 0; i++) await Task.Delay(10);
        Assert.True(clock.PendingTimers > 0, "the 6th request never parked on a timer");
    }

    [Fact]
    public async Task T45_Six_concurrent_requests_the_sixth_waits_until_the_first_leaves_the_60s_window()
    {
        var clock = new ManualTimeProvider(T0);
        var limiter = new PjmRateLimiter(5, clock);

        var tasks = Enumerable.Range(0, 6).Select(_ => limiter.WaitAsync(CancellationToken.None)).ToList();

        // Five are admitted at once; exactly one is parked.
        await Task.WhenAll(tasks.Where(t => t.IsCompleted));
        Assert.Equal(5, tasks.Count(t => t.IsCompletedSuccessfully));
        var sixth = tasks.Single(t => !t.IsCompleted);
        await WaitForTimer(clock);

        clock.Advance(TimeSpan.FromSeconds(59));
        Assert.True(await StillPending(sixth));
        clock.Advance(TimeSpan.FromSeconds(1));           // exactly 60s: still inside the window (edge margin)
        Assert.True(await StillPending(sixth));

        clock.Advance(PjmRateLimiter.EdgeMargin);           // strictly past 60s after the 1st
        Assert.True(await CompletesSoon(sixth));
        Assert.Equal(6, limiter.AdmittedCount);
    }

    [Fact]
    public async Task T45_Staggered_requests_never_put_six_sends_inside_any_60s_window()
    {
        // The ICE/AGSI spacer failure mode: t = 0, 12, 24, 36, 48, 60 would be SIX in one closed window.
        var clock = new ManualTimeProvider(T0);
        var limiter = new PjmRateLimiter(5, clock);
        var admitted = new List<DateTimeOffset>();

        for (var i = 0; i < 5; i++)
        {
            await limiter.WaitAsync(CancellationToken.None);
            admitted.Add(clock.GetUtcNow());
            clock.Advance(TimeSpan.FromSeconds(12));
        }

        // now = T0 + 60s: the 6th must wait.
        var sixth = limiter.WaitAsync(CancellationToken.None);
        await WaitForTimer(clock);
        Assert.True(await StillPending(sixth));
        clock.Advance(PjmRateLimiter.EdgeMargin);
        Assert.True(await CompletesSoon(sixth));
        admitted.Add(clock.GetUtcNow());

        // Then a burst of 10 more, driving the clock forward whenever the limiter parks.
        for (var i = 0; i < 10; i++)
        {
            var t = limiter.WaitAsync(CancellationToken.None);
            while (!t.IsCompleted)
            {
                await Task.Delay(5);
                if (clock.PendingTimers > 0) clock.Advance(TimeSpan.FromSeconds(1));
            }
            await t;
            admitted.Add(clock.GetUtcNow());
        }

        for (var i = 5; i < admitted.Count; i++)
            Assert.True(admitted[i] - admitted[i - 5] > TimeSpan.FromSeconds(60),
                $"sends {i - 5} and {i} are only {(admitted[i] - admitted[i - 5]).TotalSeconds}s apart");
    }

    [Fact]
    public async Task T45_Cancellation_releases_a_parked_waiter()
    {
        var clock = new ManualTimeProvider(T0);
        var limiter = new PjmRateLimiter(1, clock);
        await limiter.WaitAsync(CancellationToken.None);

        using var cts = new CancellationTokenSource();
        var parked = limiter.WaitAsync(cts.Token);
        await WaitForTimer(clock);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => parked);
        Assert.Equal(1, limiter.AdmittedCount);
    }
}
