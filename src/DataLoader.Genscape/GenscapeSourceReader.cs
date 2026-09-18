using System.Net;
using DataLoader.Core.Abstractions;
using Microsoft.Extensions.Logging;

namespace DataLoader.Genscape;

/// <summary>
/// Reads one work unit from Genscape's oil-fundamentals REST API.
///
/// <para>
/// Deliberately NOT built on <c>HttpJsonSourceReaderBase</c>. That base calls
/// <c>EnsureSuccessStatusCode</c> and deserialises straight into
/// <c>List&lt;TItem&gt;</c> with a snake_case naming policy — three assumptions this
/// endpoint breaks: the payload is an object envelope, the field names are camelCase,
/// and a <c>400</c> here carries a diagnosable RFC 7807 document that
/// <c>EnsureSuccessStatusCode</c> would throw away.
/// </para>
///
/// <para><b>THE STATUS MATRIX</b> — every row observed live on 2026-09-08:</para>
/// <list type="table">
///   <item>
///     <term><c>200</c> + <c>{"data":[…]}</c></term>
///     <description>Rows. Merged.</description>
///   </item>
///   <item>
///     <term><c>200</c> + <c>{"data":[]}</c></term>
///     <description>
///       A LEGITIMATE EMPTY READ, not a failure: a window entirely before the first
///       report (history starts ~2010 for storage, ~2014 for transportation) or after
///       the latest published one. Succeeds with 0 rows.
///     </description>
///   </item>
///   <item>
///     <term><c>400</c></term>
///     <description>
///       MALFORMED REQUEST — an unknown region, a <c>revision</c> other than
///       <c>revised</c>, a non-RFC-3339 date, or <c>endDate</c> not after
///       <c>startDate</c>. Throws, and never retries: the same request will be rejected
///       the same way forever. The <c>invalidParameters</c> array is summarised into
///       the message.
///     </description>
///   </item>
///   <item>
///     <term><c>401</c></term>
///     <description>
///       Missing or wrong <c>Gen-Api-Key</c>. The body is EMPTY, so the message has to
///       supply the diagnosis. Throws without retrying — a bad key does not heal — and
///       never logs the key itself.
///     </description>
///   </item>
///   <item>
///     <term><c>404</c></term>
///     <description>
///       Returned when a REQUIRED query parameter is absent (dropping <c>region</c>
///       yields <c>{"statusCode":404,"message":"Resource not found"}</c>), not only for
///       a wrong path. Throws: it means this class built a malformed URL.
///     </description>
///   </item>
///   <item>
///     <term><c>408</c> / <c>429</c> / <c>5xx</c> / socket errors</term>
///     <description>Transient. Retried by the Polly policy before reaching this class.</description>
///   </item>
/// </list>
///
/// <para>
/// <b>⚠ THE 5,000-ROW CAP.</b> The API silently truncates any response to 5,000 rows.
/// There is no cap field, no paging cursor, no warning header — and because rows come
/// back NEWEST FIRST, what disappears is the OLDEST end of the window. A 2000-2026
/// request returns 5,000 rows starting at 2015-11-13 and looks perfectly healthy while
/// missing five years. <see cref="ReadWindowAsync"/> therefore treats a response AT the
/// cap as untrustworthy and BISECTS the window rather than accepting it. A single day
/// that still hits the cap cannot be split further and throws, because the alternative
/// is silently loading partial history.
/// </para>
/// </summary>
public sealed class GenscapeSourceReader : ISourceReader<GenscapeWorkUnit, GenscapeRow>
{
    /// <summary>
    /// How many times a window may be halved before giving up. 16 allows a 65,000-day
    /// chunk to reach single days; it exists only so a logic error cannot recurse
    /// forever, not as a real limit.
    /// </summary>
    private const int MaxBisectionDepth = 16;

    private readonly GenscapeFeedDescriptor _feed;
    private readonly HttpClient _http;
    private readonly GenscapeSettings _settings;
    private readonly ILogger _logger;

    public GenscapeSourceReader(
        GenscapeFeedDescriptor feed, HttpClient http, GenscapeSettings settings, ILogger logger)
    {
        _feed = feed;
        _http = http;
        _settings = settings;
        _logger = logger;
    }

    public async Task<IReadOnlyList<GenscapeRow>> ReadAsync(
        GenscapeWorkUnit unit, CancellationToken cancellationToken)
    {
        var stats = new GenscapeReadStats();
        var rows = new List<GenscapeRow>();

        await ReadWindowAsync(unit, unit.Start, unit.End, depth: 0, rows, stats, cancellationToken)
            .ConfigureAwait(false);

        Report(unit, rows.Count, stats);
        return rows;
    }

    /// <summary>
    /// Reads <c>[start, end]</c> (both inclusive in loader terms), bisecting when the
    /// response comes back at the row cap.
    ///
    /// <para>
    /// The halves are <c>[start, mid]</c> and <c>[mid+1, end]</c> — DISJOINT, so
    /// bisection cannot duplicate a row, and their union is exactly the original
    /// window, so it cannot drop one either.
    /// </para>
    /// </summary>
    private async Task ReadWindowAsync(
        GenscapeWorkUnit unit,
        DateOnly start,
        DateOnly end,
        int depth,
        List<GenscapeRow> rows,
        GenscapeReadStats stats,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var body = await GetAsync(unit, start, end, cancellationToken).ConfigureAwait(false);
        stats.Requests++;

        // Parsed into a FRESH stats object, not the unit's. Two reasons: this reader
        // instance is shared by every concurrently running unit of its feed, so it can
        // hold no per-request state of its own; and a response that turns out to be
        // truncated is discarded whole, so its dropped-row and truncation counts must
        // not survive into the unit's totals.
        var requestStats = new GenscapeReadStats();
        var parsed = _feed.Parse(body, requestStats);

        var cap = Math.Max(1, _settings.MaxRowsPerResponse);

        // Compared against the cap is the count the API RETURNED, not the count that
        // survived parsing — a record dropped for a blank key still occupied one of the
        // 5,000 slots.
        var returned = requestStats.SourceRecords;

        if (returned < cap)
        {
            rows.AddRange(parsed);
            stats.SourceRecords += requestStats.SourceRecords;
            stats.DroppedRequired += requestStats.DroppedRequired;
            stats.Truncated += requestStats.Truncated;
            return;
        }

        stats.CapHits++;

        if (start >= end)
            throw new InvalidOperationException(
                $"Genscape {_feed.FeedId} {unit.Region}: the single day {GenscapeTime.Iso(start)} returned " +
                $"{returned} row(s), at or above the API's {cap}-row response cap, and cannot be split " +
                "further. The response is silently truncated (oldest rows dropped first), so it is not safe " +
                "to load. Raise MaxRowsPerResponse only if the vendor has confirmed a higher cap.");

        if (depth >= MaxBisectionDepth)
            throw new InvalidOperationException(
                $"Genscape {_feed.FeedId} {unit.Region}: window {GenscapeTime.Iso(start)}.." +
                $"{GenscapeTime.Iso(end)} still hits the {cap}-row cap after {MaxBisectionDepth} " +
                "bisections. Lower WindowChunkDays.");

        // Split on the day count, so an odd-length window puts the extra day in the
        // first half and both halves stay non-empty.
        var mid = start.AddDays((end.DayNumber - start.DayNumber) / 2);

        _logger.LogWarning(
            "Genscape {Feed} {Region}: {Start}..{End} returned {Rows} row(s), at the API's {Cap}-row cap. " +
            "The response is SILENTLY TRUNCATED (newest first, so the oldest rows are lost) — splitting into " +
            "{AStart}..{AEnd} and {BStart}..{BEnd}. Lower WindowChunkDays to avoid this",
            _feed.FeedId, unit.Region, GenscapeTime.Iso(start), GenscapeTime.Iso(end), returned, cap,
            GenscapeTime.Iso(start), GenscapeTime.Iso(mid),
            GenscapeTime.Iso(mid.AddDays(1)), GenscapeTime.Iso(end));

        // The truncated response is discarded outright rather than merged and topped
        // up: its contents are the NEWEST rows of the window, and the two halves below
        // return the whole window in full.
        await ReadWindowAsync(unit, start, mid, depth + 1, rows, stats, cancellationToken).ConfigureAwait(false);
        await ReadWindowAsync(unit, mid.AddDays(1), end, depth + 1, rows, stats, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Issues one request and returns the body, applying the status matrix.
    ///
    /// <para>
    /// <c>endDate</c> is sent as <c>end + 1 day</c>. The API's window is
    /// <c>[startDate, endDate)</c> — verified live: <c>2026-08-14..2026-08-28</c>
    /// returns the 14th and 21st but NOT the 28th, while <c>..2026-08-29</c> returns
    /// all three. Sending the window's last day as <c>endDate</c> would silently drop
    /// the most recent report every single run.
    /// </para>
    /// </summary>
    private async Task<string> GetAsync(
        GenscapeWorkUnit unit, DateOnly start, DateOnly end, CancellationToken cancellationToken)
    {
        var uri = BuildUri(unit.Region, start, end);

        // The path only. The query carries the window and the region, which are
        // harmless, but keeping the log to the path matches every other loader here and
        // means a parameter added later cannot leak by default.
        _logger.LogDebug("Genscape {Feed}: GET {Path} region={Region} {Start}..{End}",
            _feed.FeedId, uri.GetLeftPart(UriPartial.Path), unit.Region,
            GenscapeTime.Iso(start), GenscapeTime.Iso(end));

        using var response = await _http.GetAsync(uri, cancellationToken).ConfigureAwait(false);

        if (response.IsSuccessStatusCode)
            return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        var body = await SafeReadBodyAsync(response, cancellationToken).ConfigureAwait(false);

        throw response.StatusCode switch
        {
            HttpStatusCode.BadRequest => new InvalidOperationException(
                $"Genscape {_feed.FeedId} {unit.Region} {GenscapeTime.Iso(start)}..{GenscapeTime.Iso(end)}: " +
                $"the API rejected the request (400) — {GenscapeProblemDocument.Describe(body)}. This is a " +
                "malformed request, not a transient failure: check Regions (NorthAmerica | GulfCoast) and " +
                "Revision (revised)."),

            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => new InvalidOperationException(
                $"Genscape {_feed.FeedId}: the API rejected the credentials ({(int)response.StatusCode}). " +
                "The Gen-Api-Key header is missing, empty or wrong — it resolves from " +
                "core.Param(LoaderName='Genscape', ParamName='ApiKey') or from " +
                "DATALOADER_Loaders__Genscape__ApiKey. Note that a 401 here carries an EMPTY body, so there " +
                "is nothing further the API will tell us."),

            HttpStatusCode.NotFound => new InvalidOperationException(
                $"Genscape {_feed.FeedId}: the API returned 404 for {uri.GetLeftPart(UriPartial.Path)}. " +
                "These endpoints answer 404 when a REQUIRED query parameter is absent as well as for an " +
                "unknown path, so check BaseUrl and that the region was supplied."),

            _ => new HttpRequestException(
                $"Genscape {_feed.FeedId} {unit.Region}: the API returned {(int)response.StatusCode} " +
                $"{response.ReasonPhrase}. {GenscapeProblemDocument.Describe(body)}",
                inner: null,
                statusCode: response.StatusCode)
        };
    }

    /// <summary>
    /// Builds the request URI.
    ///
    /// <para>
    /// The API key is NOT here — it travels in the <c>Gen-Api-Key</c> header configured
    /// on the client, so no URI this class builds, logs or puts in an exception can
    /// carry it.
    /// </para>
    /// <para>
    /// Dates are formatted with <see cref="GenscapeTime.Iso"/>, i.e. under the invariant
    /// culture. The API validates them as RFC 3339 full-dates and answers 400 for
    /// anything else, so an ambient culture that renders a different calendar would fail
    /// every request.
    /// </para>
    /// </summary>
    internal Uri BuildUri(string region, DateOnly start, DateOnly end)
    {
        var baseUrl = _settings.BaseUrl.TrimEnd('/');

        var query =
            $"?region={Uri.EscapeDataString(region)}" +
            $"&revision={Uri.EscapeDataString(_settings.Revision)}" +
            $"&startDate={GenscapeTime.Iso(start)}" +
            $"&endDate={GenscapeTime.Iso(end.AddDays(1))}" +
            "&format=json";

        return new Uri($"{baseUrl}/{_feed.EndpointPath}{query}");
    }

    /// <summary>
    /// Reads an error body without letting a second failure mask the first. A 401 here
    /// legitimately has no body at all.
    /// </summary>
    private static async Task<string> SafeReadBodyAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            return await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    /// <summary>
    /// One line per work unit describing what the read cost, plus a warning for anything
    /// that lost data.
    /// </summary>
    private void Report(GenscapeWorkUnit unit, int rowCount, GenscapeReadStats stats)
    {
        if (stats.DroppedRequired > 0)
            _logger.LogWarning(
                "Genscape {Unit}: dropped {Dropped} of {Total} record(s) missing a primary-key component " +
                "(reportDate, region, or the feed's product/field-type/type) — they cannot be merged under " +
                "a blank key",
                unit.DisplayName, stats.DroppedRequired, stats.SourceRecords);

        if (stats.Truncated > 0)
            _logger.LogWarning(
                "Genscape {Unit}: truncated {Count} string value(s) to fit VARCHAR(50). The vendor may have " +
                "widened a label — check docs/apis/Genscape.md §6 against GenscapeDescriptors.cs",
                unit.DisplayName, stats.Truncated);

        // Bisection is correct but not free — it means the window was too wide for the
        // API's cap, and the operator can make it stop by lowering the chunk size.
        if (stats.CapHits > 0)
            _logger.LogInformation(
                "Genscape {Unit}: hit the response row cap {Hits} time(s) and split the window into " +
                "{Requests} request(s) in total. The result is complete, but lowering WindowChunkDays " +
                "would avoid the re-reads",
                unit.DisplayName, stats.CapHits, stats.Requests);

        // An empty window is a first-class non-error outcome (history starts in 2010 /
        // 2014, and the newest week may not be published yet) but is still worth a line,
        // because a run where EVERY unit is empty means something else is wrong.
        if (rowCount == 0)
        {
            _logger.LogInformation(
                "Genscape {Unit}: the API returned no rows for this window — normal before the feed's first " +
                "report or after the latest published one",
                unit.DisplayName);
            return;
        }

        _logger.LogDebug(
            "Genscape {Unit}: {Records} record(s) -> {Rows} target row(s) in {Requests} request(s)",
            unit.DisplayName, stats.SourceRecords, rowCount, stats.Requests);
    }
}
