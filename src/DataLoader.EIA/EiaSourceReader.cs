using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DataLoader.EIA;

/// <summary>What one window's read did.</summary>
internal readonly record struct EiaWindowResult(
    int RowsRead,
    int RowsChanged,
    int RowsDropped,
    int DuplicateKeys,
    long ReportedTotal,
    int Requests,
    int Subdivisions);

/// <summary>
/// Reads one work unit's window: asks the API for it, SUBDIVIDES it if the vendor says it
/// holds more rows than one window should, and streams the rows to the sink in batches.
///
/// <para>
/// <b>Why subdivision rather than deeper paging.</b> EIA's only paging mechanism is
/// <c>offset</c>, and past a certain depth the vendor's backend simply cannot answer.
/// Measured on <c>electricity/rto/daily-fuel-type-data</c> in a controlled run
/// (2026-10-16, idle API, every URL unique so nothing was served from cache, identical
/// window and page size throughout):
/// </para>
/// <code>
///   offset       0 →  0% failed   median 13.4s
///   offset  15,000 →  0% failed   median  8.6s
///   offset 100,000 →  0% failed   median  9.5s
///   offset 400,000 → 100% FAILED  4/4 HTTP 503
/// </code>
/// <para>
/// ⚠ <b>It is a CLIFF, not a gradient.</b> There is no measurable latency penalty at all
/// below 100,000 — the medians there differ by less than the run-to-run noise. Somewhere
/// between 100,000 and 400,000 the query stops being answerable and fails every time. The
/// original 365-step daily window was 704,880 rows — 141 pages, reaching offset 700,000 —
/// so its later pages could never have succeeded at any timeout.
/// </para>
/// <para>
/// Narrowing the period range keeps every request far below the cliff.
/// <see cref="EiaSettings.MaxRowsPerWindow"/> at 25,000 caps offset at 20,000, which is an
/// order of magnitude of headroom.
/// </para>
/// <para>
/// The reader still pages when a range is a single period and cannot be narrowed further
/// — <c>international</c> annual is 87,342 rows in one year. That sits under the cliff
/// and is consistent with the table above rather than being an exception to it.
/// </para>
/// <para>
/// <b>Subdivision is internal to the unit.</b> The resume key names the TOP-LEVEL window,
/// and the unit still succeeds or fails as a whole, so nothing about idempotency changes.
/// </para>
/// <para>
/// <b>It streams rather than materialising the window</b>, flushing every
/// <see cref="EiaSettings.MergeBatchRows"/> rows.
/// </para>
/// </summary>
internal sealed class EiaSourceReader
{
    /// <summary>
    /// Hard stop on recursion. Halving a range cannot need more than ~26 levels even for
    /// the hourly series' whole history (68,000 periods), so hitting this means the split
    /// is not making progress — a bug, not a big series.
    /// </summary>
    private const int MaxSubdivisionDepth = 32;

    private readonly EiaApiClient _api;
    private readonly EiaSettings _settings;
    private readonly ILogger<EiaSourceReader> _logger;

    public EiaSourceReader(
        EiaApiClient api,
        IOptions<EiaSettings> settings,
        ILogger<EiaSourceReader> logger)
    {
        _api = api;
        _settings = settings.Value;
        _logger = logger;
    }

    /// <summary>
    /// Load one window.
    ///
    /// <para><b>The paging contract</b>, all measured against the live API
    /// (<c>docs/apis/EIA.md</c> §6–7):</para>
    /// <list type="bullet">
    ///   <item>the page ceiling is <b>5,000</b>. Asking for more returns 5,000 anyway
    ///         plus a <c>"parameter out of range: length"</c> warning, which the loader
    ///         treats as a configuration bug rather than running at a page size it did
    ///         not choose;</item>
    ///   <item><c>response.total</c> is the row count for the REQUESTED RANGE, which is
    ///         what makes subdivision possible — it is read from the first response and
    ///         costs no extra request;</item>
    ///   <item>an <c>offset</c> past the end returns <b>HTTP 200 with zero rows</b> —
    ///         <i>and still carries the "incomplete return" warning</i>. That warning is
    ///         therefore NOT a usable "there is more" signal; an EMPTY PAGE is the stop
    ///         condition;</item>
    ///   <item>paging was measured stable (3,739 rows over four pages: zero duplicates,
    ///         zero gaps). The loader still sends <c>sort[0][column]=period</c> and still
    ///         reconciles what it collected against <c>total</c>.</item>
    /// </list>
    /// </summary>
    public async Task<EiaWindowResult> LoadAsync(
        EiaWorkUnit unit,
        Func<IReadOnlyList<EiaRow>, CancellationToken, Task<int>> writeBatch,
        CancellationToken ct)
    {
        var pageSize = ResolvePageSize();
        var state = new ReadState(_settings.MergeBatchRows);

        await ReadRangeAsync(unit, unit.ReadStart, unit.WindowEnd, pageSize, 0, state, writeBatch, ct)
            .ConfigureAwait(false);

        await state.FlushAsync(writeBatch, ct).ConfigureAwait(false);

        ReportAnomalies(unit, state);

        return new EiaWindowResult(
            state.RowsRead, state.RowsChanged, state.RowsDropped,
            state.DuplicateKeys, state.TopLevelTotal, state.Requests, state.Subdivisions);
    }

    /// <summary>
    /// Read one period range, subdividing it if it is too big to page safely.
    /// </summary>
    private async Task ReadRangeAsync(
        EiaWorkUnit unit,
        EiaPeriod from,
        EiaPeriod to,
        int pageSize,
        int depth,
        ReadState state,
        Func<IReadOnlyList<EiaRow>, CancellationToken, Task<int>> writeBatch,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        if (depth > MaxSubdivisionDepth)
            throw new InvalidOperationException(
                $"EIA {unit.DisplayName}: subdivision reached depth {depth} at {from}..{to} without " +
                "resolving. Halving a range cannot need this many levels, so the split is not making " +
                "progress — this is a bug in EiaPeriod.Split, not a large series.");

        var page = await FetchAsync(unit, from, to, 0, pageSize, state, ct).ConfigureAwait(false);
        if (depth == 0) state.TopLevelTotal = page.Total;

        // ---- too big to page? narrow the range instead -------------------------------
        if (ShouldSubdivide(page.Total, pageSize))
        {
            var split = from.Split(to);
            if (split is not null)
            {
                var (leftEnd, rightStart) = split.Value;
                state.Subdivisions++;

                _logger.LogDebug(
                    "EIA {Unit}: {From}..{To} holds {Total} row(s), over the {Max} limit — splitting into " +
                    "{From}..{LeftEnd} and {RightStart}..{To}",
                    unit.DisplayName, from, to, page.Total, _settings.MaxRowsPerWindow,
                    from, leftEnd, rightStart, to);

                // The page just fetched is DISCARDED: the two halves re-request it between
                // them. That is one wasted request per split, paid only at the internal
                // nodes of the split tree, and it buys a `total` for the range without a
                // separate probe. With the shipped WindowSteps it should rarely fire at all.
                await ReadRangeAsync(unit, from, leftEnd, pageSize, depth + 1, state, writeBatch, ct)
                    .ConfigureAwait(false);
                await ReadRangeAsync(unit, rightStart, to, pageSize, depth + 1, state, writeBatch, ct)
                    .ConfigureAwait(false);
                return;
            }

            // A SINGLE period holding more than the limit. It cannot be narrowed, so page
            // it — `international` annual is 87,342 rows in one year, and that endpoint's
            // offset cost is flat, so this is fine. Said out loud because on an endpoint
            // where offset IS expensive it would be the thing to look at.
            _logger.LogInformation(
                "EIA {Unit}: period {From} alone holds {Total} row(s), above MaxRowsPerWindow={Max}, " +
                "and cannot be subdivided — paging it with {Pages} request(s)",
                unit.DisplayName, from, page.Total, _settings.MaxRowsPerWindow,
                (page.Total + pageSize - 1) / pageSize);
        }

        // ---- consume this range -------------------------------------------------------
        await ConsumeAsync(page, state, writeBatch, ct).ConfigureAwait(false);

        if (page.Rows.Count == 0)
        {
            if (depth == 0)
                _logger.LogDebug(
                    "EIA {Unit}: empty window (total={Total}) — a legitimate empty read, not a failure",
                    unit.DisplayName, page.Total);
            return;
        }

        if (page.Rows.Count < pageSize) return;     // short page: the server had no more

        for (var offset = pageSize; ; offset += pageSize)
        {
            ct.ThrowIfCancellationRequested();

            if (state.Requests >= Math.Max(1, _settings.MaxPagesPerWindow))
                throw new InvalidOperationException(
                    $"EIA {unit.DisplayName}: stopped after {state.Requests} request(s) " +
                    $"({state.RowsRead} rows) without finishing. Either MaxRowsPerWindow is too high " +
                    "for this series, or paging is not terminating. Check which before raising " +
                    "MaxPagesPerWindow.");

            var next = await FetchAsync(unit, from, to, offset, pageSize, state, ct).ConfigureAwait(false);

            // ⚠ THE STOP CONDITION. Not the warning -- see the method remarks.
            if (next.Rows.Count == 0) break;

            await ConsumeAsync(next, state, writeBatch, ct).ConfigureAwait(false);

            if (next.Rows.Count < pageSize) break;
        }
    }

    /// <summary>
    /// True when a range should be narrowed rather than paged. Disabled by
    /// <c>MaxRowsPerWindow = 0</c>, which restores unconditional offset paging.
    /// </summary>
    private bool ShouldSubdivide(long total, int pageSize)
    {
        var limit = _settings.MaxRowsPerWindow;
        if (limit <= 0) return false;

        // Never subdivide below a single page -- that would split a range the server can
        // already answer in one request.
        return total > Math.Max(limit, pageSize);
    }

    private async Task<EiaParsedPage> FetchAsync(
        EiaWorkUnit unit, EiaPeriod from, EiaPeriod to, int offset, int length,
        ReadState state, CancellationToken ct)
    {
        var json = await _api.GetDataPageAsync(unit, from, to, offset, length, ct).ConfigureAwait(false);
        state.Requests++;

        var page = EiaResponseParser.Parse(
            json, unit.Plan, unit.Frequency.FrequencyId, _settings.CoerceNumericStrings);

        if (page.ParameterOutOfRange)
            throw new InvalidOperationException(
                $"EIA {unit.DisplayName}: the API reports 'parameter out of range' and substituted its " +
                $"own value for one we sent (length={length}). The ceiling is 5,000; set " +
                "Loaders:EIA:PageSize to 5000 or below. Refusing to continue at a page size the loader " +
                "did not choose, because the offsets would no longer line up.");

        state.RowsDropped += page.RowsDropped;
        return page;
    }

    private static async Task ConsumeAsync(
        EiaParsedPage page, ReadState state,
        Func<IReadOnlyList<EiaRow>, CancellationToken, Task<int>> writeBatch, CancellationToken ct)
    {
        foreach (var row in page.Rows)
        {
            if (!state.Seen.Add((row.Period, row.FacetKey))) { state.DuplicateKeys++; continue; }

            state.RowsRead++;
            state.Batch.Add(row);

            if (state.Batch.Count >= state.BatchSize)
                await state.FlushAsync(writeBatch, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// The page size actually used. A configured value above the API's 5,000 ceiling is
    /// a bug the API would otherwise absorb silently, so it is refused here rather than
    /// discovered three pages later as a mysterious gap.
    /// </summary>
    private int ResolvePageSize()
    {
        const int ApiMaximum = 5000;

        if (_settings.PageSize is < 1 or > ApiMaximum)
            throw new InvalidOperationException(
                $"Loaders:EIA:PageSize is {_settings.PageSize}. The API's hard ceiling is {ApiMaximum} " +
                "rows per request — a larger value is silently clamped by the server (with a " +
                "'parameter out of range' warning), which would desynchronise offset paging.");

        return _settings.PageSize;
    }

    /// <summary>
    /// Everything worth saying about a window AFTER it loaded. All observational — none
    /// of it fails the unit, because every case has a legitimate explanation and the
    /// operator is better placed to judge.
    /// </summary>
    private void ReportAnomalies(EiaWorkUnit unit, ReadState state)
    {
        if (state.RowsDropped > 0)
            _logger.LogWarning(
                "EIA {Unit}: {Dropped} row(s) dropped for a missing or unusable 'period' — they cannot " +
                "be keyed, and merging them would collide every one of them onto a single primary key",
                unit.DisplayName, state.RowsDropped);

        if (state.DuplicateKeys > 0)
            _logger.LogWarning(
                "EIA {Unit}: the API served {Duplicates} row(s) with a (Period, FacetKey) already seen " +
                "in this window across {Requests} request(s). De-duplicated here; the merge proc guards " +
                "against them too. Repeated occurrences mean offset paging is not stable for this series",
                unit.DisplayName, state.DuplicateKeys, state.Requests);

        // Reconciling against the vendor's own count is the only way to notice that paging
        // silently skipped rows. Only meaningful when the window was NOT subdivided: once
        // it is, `total` describes the top-level range while the rows came from the
        // halves, and the two are only equal if nothing was lost -- which is precisely
        // what makes this check worth keeping even then.
        if (state.TopLevelTotal >= 0)
        {
            var accountedFor = (long)state.RowsRead + state.RowsDropped + state.DuplicateKeys;
            if (accountedFor < state.TopLevelTotal)
                _logger.LogWarning(
                    "EIA {Unit}: the API reported total={Total} but only {Accounted} row(s) were " +
                    "accounted for over {Requests} request(s) and {Splits} split(s) ({Read} read, " +
                    "{Dropped} dropped, {Dupes} duplicate). Rows are MISSING from this window",
                    unit.DisplayName, state.TopLevelTotal, accountedFor, state.Requests,
                    state.Subdivisions, state.RowsRead, state.RowsDropped, state.DuplicateKeys);
        }
    }

    /// <summary>
    /// Mutable per-unit accumulator. One instance spans the whole subdivision tree, so
    /// the de-dup set, the batch buffer and the counters are shared across every
    /// sub-range — which is what lets a batch straddle two halves instead of being
    /// flushed short at every boundary.
    /// </summary>
    private sealed class ReadState
    {
        public ReadState(int batchSize)
        {
            BatchSize = Math.Max(1, batchSize);
            Batch = new List<EiaRow>(BatchSize);
        }

        public int BatchSize { get; }
        public List<EiaRow> Batch { get; }
        public HashSet<(string Period, int FacetKey)> Seen { get; } = new();

        public int RowsRead;
        public int RowsChanged;
        public int RowsDropped;
        public int DuplicateKeys;
        public int Requests;
        public int Subdivisions;
        public long TopLevelTotal = -1;

        public async Task FlushAsync(
            Func<IReadOnlyList<EiaRow>, CancellationToken, Task<int>> writeBatch, CancellationToken ct)
        {
            if (Batch.Count == 0) return;

            RowsChanged += await writeBatch(Batch, ct).ConfigureAwait(false);
            Batch.Clear();
        }
    }
}
