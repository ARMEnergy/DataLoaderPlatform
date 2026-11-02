using Polly;
using Polly.Timeout;

namespace DataLoader.Core.Resilience;

/// <summary>
/// The per-attempt HTTP timeout seam every loader's retry policy should be built on.
///
/// <para>
/// ⚠ <b>The trap this exists to stop loaders from reintroducing.</b> A policy registered
/// with <c>HttpClientBuilder.AddPolicyHandler</c> runs as a <see cref="DelegatingHandler"/>
/// <i>inside</i> the client's own pipeline, and <see cref="HttpClient.Timeout"/> bounds
/// that WHOLE invocation — every retry attempt and every backoff wait together, not one
/// attempt. So <c>client.Timeout = 60s</c> with <c>RetryCount: 3</c> does not give each of
/// the four attempts 60 seconds; it gives all four 60 seconds BETWEEN them. Against an
/// endpoint whose paging loop makes several calls per work unit, the second or third call
/// blows the shared budget and the failure surfaces as a generic client-timeout
/// <see cref="TaskCanceledException"/>, hiding whatever the vendor actually did. EIA hit
/// this on its first full run (see <c>CLAUDE.md</c>); IHSPointLogic hit it independently
/// (~11 failures/10 days, always the same paging work unit).
/// </para>
/// <para>
/// <b>The fix has three parts, and all three are required together:</b>
/// </para>
/// <list type="number">
///   <item>The registered <see cref="HttpClient"/> gets <c>client.Timeout =
///     Timeout.InfiniteTimeSpan</c> — nothing may bound the client-level call any more.</item>
///   <item>The retry policy wraps a per-attempt timeout built by <see cref="Build"/>, placed
///     INSIDE the retry (<c>retry.WrapAsync(perAttempt)</c>, or <see cref="Wrap"/>) so the
///     budget restarts on every attempt.</item>
///   <item>The retry predicate must treat the timeout's own
///     <see cref="TimeoutRejectedException"/> as transient. Without this a per-attempt
///     timeout fires once and the operation simply fails — nothing retries it, which is a
///     silent regression dressed up as "safer".</item>
/// </list>
/// <para>
/// This type deliberately does NOT decide what else is transient (which status codes, which
/// exceptions, whether a client-side <see cref="TaskCanceledException"/> wrapping a
/// <see cref="TimeoutException"/> should also be caught) — that varies per vendor and stays
/// in the loader's own policy. It also does not pick retry counts, delays or backoff shape.
/// Keeping it to the one genuinely shared piece — the timeout/retry nesting order — is what
/// makes it safe to reuse without dragging loader-specific behaviour along with it.
/// </para>
/// <para>
/// ⚠ <c>DataLoader.EIA</c> and <c>DataLoader.NUCORE</c> hand-roll this exact
/// shape in their own <c>EiaHttp.BuildResiliencePipeline</c> / <c>NucoreHttp.Build</c> — they
/// predate this helper (both fixed 2026-10-16/18, before the pattern was pulled out here) and
/// are intentionally NOT migrated to it. They are shipped, tested and working; churning them
/// to call through here buys nothing today and only risks the two live loaders for no
/// behavioural change. New and newly-fixed loaders should use this instead of adding a third
/// hand-rolled copy.
/// </para>
/// </summary>
public static class PerAttemptHttpTimeout
{
    /// <summary>
    /// Builds the per-attempt timeout policy alone — one <see cref="HttpResponseMessage"/>
    /// attempt gets <paramref name="perAttemptTimeoutSeconds"/> before it throws
    /// <see cref="TimeoutRejectedException"/>.
    ///
    /// <para>
    /// <see cref="TimeoutStrategy.Optimistic"/> — it cancels through the
    /// <see cref="CancellationToken"/> that HttpClient already flows into the handler
    /// pipeline, rather than abandoning the request on a background thread
    /// (<see cref="TimeoutStrategy.Pessimistic"/>), which would leak a connection every time
    /// an attempt times out.
    /// </para>
    /// </summary>
    public static IAsyncPolicy<HttpResponseMessage> Build(int perAttemptTimeoutSeconds) =>
        Policy.TimeoutAsync<HttpResponseMessage>(
            TimeSpan.FromSeconds(Math.Max(1, perAttemptTimeoutSeconds)),
            TimeoutStrategy.Optimistic);

    /// <summary>
    /// Nests a caller-built retry policy OUTSIDE the per-attempt timeout, so the budget
    /// restarts on every attempt instead of being shared across all of them.
    ///
    /// <para>
    /// The retry policy passed in must already treat <see cref="TimeoutRejectedException"/>
    /// (and anything else this loader considers a timeout, such as a
    /// <see cref="TaskCanceledException"/> wrapping a <see cref="TimeoutException"/>) as
    /// retryable — this method only fixes the NESTING order, not the predicate. Getting the
    /// order right but leaving the timeout out of the predicate reproduces defect #2 from the
    /// IHSPointLogic incident: the timeout fires once and nothing retries it.
    /// </para>
    /// </summary>
    public static IAsyncPolicy<HttpResponseMessage> Wrap(
        IAsyncPolicy<HttpResponseMessage> retry, int perAttemptTimeoutSeconds) =>
        retry.WrapAsync(Build(perAttemptTimeoutSeconds));

    /// <summary>
    /// Worst-case wall-clock seconds one logical request can occupy: every attempt at its
    /// full per-attempt budget, plus whatever backoff the caller's own policy inserts between
    /// attempts.
    /// <para>
    /// <paramref name="backoffAt"/> is the same delay function the retry policy itself uses
    /// for attempt <c>1..retryCount</c> (jitter included, if any — pass a function that
    /// returns its maximum if the real one is randomised, so the bound stays an upper bound
    /// and the test cannot flake).
    /// </para>
    /// <para>
    /// This is the number a per-loader test compares against
    /// <c>LoaderSettingsBase.WorkUnitTimeoutSeconds</c>: if the worst case meets or exceeds
    /// the work-unit budget, a unit that genuinely needs its retries gets cancelled mid-retry
    /// and the real vendor error is replaced by a generic "work unit timed out" — the
    /// regression raising <c>HttpTimeoutSeconds</c> alone (without also raising
    /// <c>WorkUnitTimeoutSeconds</c>) would cause.
    /// </summary>
    public static double WorstCaseSeconds(int retryCount, int perAttemptTimeoutSeconds, Func<int, TimeSpan> backoffAt)
    {
        var attempts = Math.Max(0, retryCount) + 1;
        var backoffSeconds = 0d;

        for (var attempt = 1; attempt <= Math.Max(0, retryCount); attempt++)
            backoffSeconds += backoffAt(attempt).TotalSeconds;

        return (attempts * Math.Max(1, perAttemptTimeoutSeconds)) + backoffSeconds;
    }
}
