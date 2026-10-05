using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace DataLoader.OilX;

/// <summary>One file the vendor published for a (feed, day).</summary>
/// <param name="FileName">
/// The bare S3 object name, e.g. <c>CargoTracking.2026-09-30T03-39.csv</c>. This is the
/// only feed label the manifest carries and it is what lands in the <c>FileName</c>
/// provenance column.
/// </param>
/// <param name="UploadedAt">
/// The vendor's <c>uploaded_at</c>, in UTC. <b>The ordering key.</b> Note it is NOT the
/// timestamp embedded in <paramref name="FileName"/> (the job's start minute) nor the
/// in-file <c>RunDateTime</c> — three near-but-unequal stamps per file.
/// </param>
/// <param name="Url">
/// 🔒 The presigned download URL. Valid for FIVE HOURS and carrying
/// <c>AWSAccessKeyId</c>, <c>Signature</c> and <c>x-amz-security-token</c> — never log
/// it.
/// </param>
internal sealed record OilXManifestFile(string FileName, DateTimeOffset UploadedAt, string Url);

/// <summary>
/// What a manifest request resolved to. The vendor expresses "nothing published" as an
/// HTTP <b>422</b> rather than an empty success, so this type exists to keep that
/// distinction explicit all the way to the pipeline instead of smuggling it through an
/// empty list.
/// </summary>
internal sealed record OilXManifestResult(IReadOnlyList<OilXManifestFile> Files, bool EmptyDay)
{
    public static OilXManifestResult Empty { get; } =
        new(Array.Empty<OilXManifestFile>(), EmptyDay: true);
}

/// <summary>
/// Reads <c>GET {BaseUrl}/csv/?api_key=&amp;day=&amp;files=</c> and turns it into an
/// ordered file list for one (feed, day).
///
/// <para>
/// <b>One feed per request, never several.</b> Batching all eight feeds into one call
/// would be fewer requests and is the obvious thing to do — and it is wrong: when only
/// SOME requested feeds have data that day the API answers <b>200</b> and silently
/// omits the rest (<c>docs/apis/OilX.md</c> §2 Behaviour 5), so a feed that published
/// nothing is indistinguishable from one that was never asked for, and could not be
/// attributed to its own work unit. Asking for one feed makes the empty case an
/// unambiguous 422 against that feed alone.
/// </para>
/// <para>
/// <b>Called at the moment a day is processed, not up front.</b> The URLs it returns
/// expire in five hours, and a full backfill moves ~6.5 GB — easily longer than that.
/// Enumerating the window once with <c>range=</c> and downloading from those URLs hours
/// later is exactly the failure this avoids.
/// </para>
/// </summary>
internal sealed class OilXManifestClient
{
    /// <summary>
    /// The 422 message prefix meaning "this feed published nothing on this day" — a
    /// LEGITIMATE EMPTY READ, not an error.
    /// </summary>
    private const string NoDataPrefix = "No data for files";

    /// <summary>
    /// The 422 message prefix meaning "that feed name does not exist" — a MALFORMED
    /// REQUEST, which must fail loudly or a typo in <c>EnabledFeeds</c> reports clean,
    /// empty runs forever.
    /// </summary>
    private const string UnavailablePrefix = "Unavailable files";

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString
    };

    private readonly HttpClient _http;
    private readonly OilXSettings _settings;
    private readonly ILogger _logger;

    public OilXManifestClient(HttpClient http, OilXSettings settings, ILogger logger)
    {
        _http = http;
        _settings = settings;
        _logger = logger;
    }

    /// <summary>
    /// List the files one feed published on one day, ordered oldest-published first.
    ///
    /// <para>
    /// The ordering is a CORRECTNESS requirement, not a nicety: all of a day's
    /// snapshots carry the same in-file <c>RunDate</c> and therefore collide on
    /// <c>(RunDate, RowId)</c>, so they must merge oldest-to-newest for the newest
    /// published value to win (<c>docs/design/OilX.md</c> §4). Ties on
    /// <c>uploaded_at</c> break by file name so the order is total and a re-run
    /// reproduces it.
    /// </para>
    /// </summary>
    public async Task<OilXManifestResult> ListAsync(
        OilXFeedDescriptor feed, DateOnly day, CancellationToken cancellationToken)
    {
        var url = $"{_settings.BaseUrl.TrimEnd('/')}/csv/" +
                  $"?api_key={Uri.EscapeDataString(_settings.ApiKey)}" +
                  $"&day={OilXTime.DayToken(day)}" +
                  $"&files={Uri.EscapeDataString(feed.FeedId)}";

        using var response = await _http
            .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        // ---- the status matrix (docs/design/OilX.md S6) -------------------------
        if (response.StatusCode == HttpStatusCode.UnprocessableEntity)
        {
            var error = ReadError(body);

            if (error.StartsWith(NoDataPrefix, StringComparison.OrdinalIgnoreCase))
            {
                // A publication gap. Normal history -- the unit succeeds with 0 rows.
                _logger.LogInformation(
                    "OilX {Feed} {Day}: vendor published nothing (HTTP 422 '{Prefix}')",
                    feed.FeedId, OilXTime.DayToken(day), NoDataPrefix);
                return OilXManifestResult.Empty;
            }

            if (error.StartsWith(UnavailablePrefix, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    $"OilX {feed.FeedId}: the vendor does not recognise this feed name " +
                    $"(HTTP 422 '{OilXHttp.Redact(error)}'). Check EnabledFeeds against " +
                    $"{_settings.BaseUrl.TrimEnd('/')}/csv/list.");

            // An unrecognised 422. Do NOT guess which of the two it is -- guessing
            // "empty" would hide a broken request behind clean, empty runs.
            throw new InvalidOperationException(
                $"OilX {feed.FeedId} {OilXTime.DayToken(day)}: unrecognised HTTP 422 " +
                $"'{OilXHttp.Redact(error)}'. Expected '{NoDataPrefix}...' or '{UnavailablePrefix}...'.");
        }

        if (response.StatusCode == HttpStatusCode.Unauthorized)
            throw new InvalidOperationException(
                $"OilX: the API key was rejected (HTTP 401 '{OilXHttp.Redact(ReadError(body))}'). " +
                "Check core.Param(LoaderName='OilX', ParamName='ApiKey').");

        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(
                $"OilX {feed.FeedId} {OilXTime.DayToken(day)}: HTTP {(int)response.StatusCode} " +
                $"'{OilXHttp.Redact(ReadError(body))}'.");

        // ---- 200 ----------------------------------------------------------------
        OilXManifestEnvelope? envelope;
        try
        {
            envelope = JsonSerializer.Deserialize<OilXManifestEnvelope>(body, Json);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                $"OilX {feed.FeedId} {OilXTime.DayToken(day)}: manifest was not valid JSON " +
                $"({OilXHttp.Redact(ex.Message)}).", ex);
        }

        if (envelope is null || !envelope.Success)
            throw new InvalidOperationException(
                $"OilX {feed.FeedId} {OilXTime.DayToken(day)}: manifest reported success=false " +
                $"'{OilXHttp.Redact(envelope?.Error)}'.");

        var entries = envelope.Data ?? new List<OilXManifestEntry>();

        if (entries.Count == 0)
            // 200 with nothing in it. The vendor's empty answer is a 422, so this means
            // the contract moved -- reporting it as an empty day would hide that.
            throw new InvalidOperationException(
                $"OilX {feed.FeedId} {OilXTime.DayToken(day)}: HTTP 200 with an empty data[]. " +
                "The vendor signals an empty day with HTTP 422 'No data for files', so this " +
                "is an unexpected contract change rather than a publication gap.");

        var files = new List<OilXManifestFile>(entries.Count);

        foreach (var entry in entries)
        {
            if (string.IsNullOrWhiteSpace(entry.Url)) continue;

            var fileName = FileNameFrom(entry.Url);

            // The manifest has no feed field, so attribution is by file-name prefix.
            // A request for one feed should only ever return that feed's files; anything
            // else is dropped rather than loaded into the wrong table.
            if (!feed.Owns(fileName))
            {
                _logger.LogWarning(
                    "OilX {Feed} {Day}: manifest returned '{File}', which is not this feed — ignored",
                    feed.FeedId, OilXTime.DayToken(day), fileName);
                continue;
            }

            files.Add(new OilXManifestFile(fileName, entry.UploadedAt, entry.Url));
        }

        if (files.Count == 0)
            throw new InvalidOperationException(
                $"OilX {feed.FeedId} {OilXTime.DayToken(day)}: manifest returned {entries.Count} " +
                "entry/entries, none of them belonging to this feed.");

        // ⚠ Oldest first. See the method remarks -- this is what makes the newest
        // snapshot win on a key all of a day's snapshots share.
        return new OilXManifestResult(
            files.OrderBy(f => f.UploadedAt)
                 .ThenBy(f => f.FileName, StringComparer.Ordinal)
                 .ToList(),
            EmptyDay: false);
    }

    /// <summary>
    /// The bare object name from a presigned URL — everything after the last <c>/</c>
    /// and before the query string.
    ///
    /// <para>
    /// Parsed off the raw string rather than via <see cref="Uri"/> so that a malformed
    /// URL yields a harmless name instead of throwing, and so nothing ever holds the
    /// query string (which carries the AWS signature) in a variable that might be
    /// logged.
    /// </para>
    /// </summary>
    internal static string FileNameFrom(string url)
    {
        var path = url;

        var query = path.IndexOf('?');
        if (query >= 0) path = path[..query];

        var slash = path.LastIndexOf('/');
        if (slash >= 0 && slash + 1 < path.Length) path = path[(slash + 1)..];

        return Uri.UnescapeDataString(path);
    }

    /// <summary>
    /// Pull the <c>error</c> field out of a failure body, falling back to a truncated,
    /// redacted copy of the body when it is not the expected JSON.
    /// </summary>
    private static string ReadError(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return string.Empty;

        try
        {
            var envelope = JsonSerializer.Deserialize<OilXManifestEnvelope>(body, Json);
            if (!string.IsNullOrWhiteSpace(envelope?.Error)) return envelope!.Error!;
        }
        catch (JsonException)
        {
            // fall through to the raw body
        }

        var trimmed = body.Trim();
        return trimmed.Length > 300 ? trimmed[..300] + "..." : trimmed;
    }

    private sealed class OilXManifestEnvelope
    {
        [JsonPropertyName("data")] public List<OilXManifestEntry>? Data { get; set; }
        [JsonPropertyName("success")] public bool Success { get; set; }
        [JsonPropertyName("error")] public string? Error { get; set; }
    }

    private sealed class OilXManifestEntry
    {
        [JsonPropertyName("uploaded_at")] public DateTimeOffset UploadedAt { get; set; }
        [JsonPropertyName("url")] public string? Url { get; set; }
    }
}
