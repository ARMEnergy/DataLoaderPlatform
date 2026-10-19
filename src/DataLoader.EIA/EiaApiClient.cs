using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DataLoader.EIA;

/// <summary>
/// A failure the EIA API reported, already redacted. <see cref="Permanent"/> marks the
/// ones that will not fix themselves.
/// </summary>
internal sealed class EiaApiException : Exception
{
    public EiaApiException(HttpStatusCode status, bool permanent, string message)
        : base(message)
    {
        Status = status;
        Permanent = permanent;
    }

    public HttpStatusCode Status { get; }
    public bool Permanent { get; }
}

/// <summary>
/// What the metadata route reports about a series.
/// </summary>
internal readonly record struct EiaSeriesMetadata(
    string? StartPeriod,
    string? EndPeriod,
    string? DefaultDateFormat,
    string? DefaultFrequency);

/// <summary>
/// Every HTTP call this loader makes: URL construction, the account-wide throttle, and
/// the status matrix.
///
/// <para>
/// 🔒 Nothing in this class logs a URI. The <c>api_key</c> is a query parameter on every
/// request — EIA offers no header form — so a single logged URL leaks the
/// organisation's credential. The named <see cref="HttpClient"/> additionally has
/// <c>RemoveAllLoggers()</c> applied in <see cref="EiaModule"/>, because the HTTP
/// factory's own handlers log the raw URI at Information level underneath anything
/// this class does.
/// </para>
/// </summary>
internal sealed class EiaApiClient
{
    private readonly HttpClient _http;
    private readonly EiaSettings _settings;
    private readonly EiaRateLimiter _limiter;
    private readonly ILogger<EiaApiClient> _logger;

    public EiaApiClient(
        HttpClient http,
        IOptions<EiaSettings> settings,
        EiaRateLimiter limiter,
        ILogger<EiaApiClient> logger)
    {
        _http = http;
        _settings = settings.Value;
        _limiter = limiter;
        _logger = logger;
    }

    // ------------------------------------------------------------- metadata ----

    /// <summary>
    /// <c>GET /v2/&lt;SeriesId&gt;</c> — the route WITHOUT <c>/data</c>, which answers
    /// with the series' metadata instead of its rows.
    ///
    /// <para>
    /// This is the authority for <c>startPeriod</c> / <c>endPeriod</c>, and it is why
    /// the loader does not have to infer "how far does this series go" from the rows it
    /// happened to load. It matters for forecast series: <c>steo</c>'s newest quarterly
    /// period is <c>2027-Q4</c>, so a window upper bound of "today" would silently drop
    /// six quarters of published forecast.
    /// </para>
    /// </summary>
    public async Task<EiaSeriesMetadata> GetSeriesMetadataAsync(string seriesId, CancellationToken ct)
    {
        var uri = BuildMetadataUri(seriesId);
        var json = await SendAsync(uri, $"metadata for series '{seriesId}'", ct).ConfigureAwait(false);

        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("response", out var response) ||
            response.ValueKind != JsonValueKind.Object)
            return default;

        return new EiaSeriesMetadata(
            ReadString(response, "startPeriod"),
            ReadString(response, "endPeriod"),
            ReadString(response, "defaultDateFormat"),
            ReadString(response, "defaultFrequency"));
    }

    // ----------------------------------------------------------------- data ----

    /// <summary>
    /// <c>GET /v2/&lt;SeriesId&gt;/data/</c> for one page of one period range.
    ///
    /// <para>
    /// The range is passed EXPLICITLY rather than taken from the unit, because
    /// <see cref="EiaSourceReader"/> subdivides a window that turns out to be too big
    /// and then requests the halves. The unit still supplies the series, the frequency
    /// and the data columns.
    /// </para>
    /// </summary>
    public async Task<string> GetDataPageAsync(
        EiaWorkUnit unit, EiaPeriod from, EiaPeriod to, int offset, int length, CancellationToken ct)
    {
        var uri = BuildDataUri(unit, from, to, offset, length);
        var what = $"{unit.Plan.Series.SeriesId} {unit.Frequency.FrequencyId} " +
                   $"{from}..{to} offset {offset}";

        return await SendAsync(uri, what, ct).ConfigureAwait(false);
    }

    // ------------------------------------------------------------------ URLs ----

    /// <summary>
    /// The metadata URL. <paramref name="seriesId"/> is a ROUTE, not a value — ids such
    /// as <c>natural-gas/cons/heat</c> carry path separators that must stay separators,
    /// so each segment is escaped individually and the slashes are preserved.
    /// </summary>
    internal Uri BuildMetadataUri(string seriesId) =>
        new($"{EscapeRoute(seriesId)}?api_key={Uri.EscapeDataString(_settings.ApiKey)}", UriKind.Relative);

    /// <summary>
    /// The data URL for one page of one window.
    ///
    /// <para>
    /// Parameter order is fixed so the same window always produces the same string —
    /// which makes a captured URL comparable between runs when debugging (after
    /// redaction), and makes the tests able to assert on it.
    /// </para>
    /// <para>
    /// <c>start</c>/<c>end</c> come from <see cref="EiaPeriod.FormatRequestBound"/>,
    /// which is what puts the mandatory <c>-00</c> zone on a <c>local-hourly</c> bound.
    /// Omitting it is an HTTP 500, not a 400 — see that method's remarks.
    /// </para>
    /// </summary>
    internal Uri BuildDataUri(EiaWorkUnit unit, EiaPeriod from, EiaPeriod to, int offset, int length)
    {
        var sb = new StringBuilder(256);
        sb.Append(EscapeRoute(unit.Plan.Series.SeriesId)).Append("/data/?frequency=")
          .Append(Uri.EscapeDataString(unit.Frequency.FrequencyId));

        var dataTypes = unit.Plan.DataTypes;
        for (var i = 0; i < dataTypes.Count; i++)
            sb.Append("&data[").Append(i).Append("]=").Append(Uri.EscapeDataString(dataTypes[i].DataTypeId));

        sb.Append("&start=").Append(Uri.EscapeDataString(from.FormatRequestBound()))
          .Append("&end=").Append(Uri.EscapeDataString(to.FormatRequestBound()));

        if (_settings.SortByPeriod)
            sb.Append("&sort[0][column]=period&sort[0][direction]=asc");

        sb.Append("&offset=").Append(offset)
          .Append("&length=").Append(length)
          .Append("&api_key=").Append(Uri.EscapeDataString(_settings.ApiKey));

        return new Uri(sb.ToString(), UriKind.Relative);
    }

    /// <summary>
    /// Escape a series id for use as a URL PATH. Splitting on '/' keeps the route
    /// structure while escaping anything unusual inside a segment;
    /// <c>Uri.EscapeDataString</c> on the whole id would turn the separators into
    /// <c>%2F</c> and every request would 404.
    /// </summary>
    internal static string EscapeRoute(string seriesId) =>
        string.Join('/', seriesId.Split('/', StringSplitOptions.RemoveEmptyEntries)
                                 .Select(Uri.EscapeDataString));

    // ------------------------------------------------------- the status matrix ----

    /// <summary>
    /// Issue one request and return the body, classifying the response.
    ///
    /// <para>
    /// <b>The status matrix</b> (every row measured against the live API on
    /// 2026-10-16 — <c>docs/apis/EIA.md</c> §6):
    /// </para>
    /// <list type="table">
    ///   <item><term>200</term><description>body is parsed; an empty <c>data[]</c> with
    ///     <c>total: 0</c> is a LEGITIMATE EMPTY WINDOW, not a failure — most series
    ///     have frequencies whose early windows simply have no rows.</description></item>
    ///   <item><term>400</term><description><c>{"error":"Invalid data 'x' provided...",
    ///     "code":400}</c>. A malformed request: the catalog named a data column or a
    ///     frequency the route does not have. PERMANENT — the body names the valid
    ///     values and no retry can help.</description></item>
    ///   <item><term>403</term><description><c>{"error":{"code":"API_KEY_INVALID"...}}</c>.
    ///     PERMANENT, and deliberately not retried: the gateway limits per key and
    ///     hammering it is the one thing that makes a key problem worse. The body does
    ///     NOT contain the key.</description></item>
    ///   <item><term>404</term><description><b>EMPTY BODY.</b> The route does not
    ///     exist. For this loader that means the catalog has drifted away from the API,
    ///     so the message says so rather than reporting a bare status.</description></item>
    ///   <item><term>429</term><description>rate limited. Retried with backoff by the
    ///     Polly policy before reaching here.</description></item>
    ///   <item><term>5xx</term><description>retried by the policy. One EIA 5xx is NOT
    ///     transient — a <c>local-hourly</c> bound without its <c>±HH</c> suffix — so
    ///     a 500 on a zoned frequency names that trap explicitly.</description></item>
    /// </list>
    /// </summary>
    private async Task<string> SendAsync(Uri uri, string what, CancellationToken ct)
    {
        await _limiter.WaitAsync(ct).ConfigureAwait(false);

        // 🔒 The URI is never logged -- it carries api_key. `what` is a safe description.
        _logger.LogDebug("EIA GET {What}", what);

        using var response = await _http.GetAsync(uri, ct).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (response.IsSuccessStatusCode) return body;

        var status = response.StatusCode;
        var snippet = EiaHttp.Redact(Truncate(body, 400));

        throw status switch
        {
            HttpStatusCode.BadRequest => new EiaApiException(status, true,
                $"EIA rejected the request for {what} as malformed (400). The catalog and the API " +
                $"disagree about this route's frequencies or data columns; the response names the " +
                $"valid ones: {snippet}"),

            HttpStatusCode.Forbidden => new EiaApiException(status, true,
                $"EIA refused the request for {what} (403). This is an API-key problem — check " +
                $"core.Param(LoaderName='EIA', ParamName='ApiKey'). Not retried, because the " +
                $"gateway rate-limits per key: {snippet}"),

            HttpStatusCode.NotFound => new EiaApiException(status, true,
                $"EIA has no such route for {what} (404, empty body). arm.Series lists a SeriesId " +
                "the API does not serve — the catalog has drifted. Disable the series or correct " +
                "its id."),

            HttpStatusCode.InternalServerError when unitUsesZonedBound(what) => new EiaApiException(status, false,
                $"EIA answered 500 for {what}. For a local-hourly frequency this is usually NOT " +
                "transient: start/end must carry a two-digit zone suffix such as '-00'. An absent " +
                $"suffix, 'Z', or a four-digit offset all answer 500. Body: {snippet}"),

            _ => new EiaApiException(status, false,
                $"EIA answered HTTP {(int)status} for {what}: {snippet}")
        };

        // Local, so the big switch above stays readable.
        static bool unitUsesZonedBound(string description) =>
            description.Contains("local-hourly", StringComparison.OrdinalIgnoreCase);
    }

    private static string Truncate(string? text, int max)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        return text.Length <= max ? text : text[..max] + "…";
    }

    private static string? ReadString(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;
}
