using System.Globalization;

namespace DataLoader.EnergyAspects;

/// <summary>
/// Builds the <c>/data/timeseries/</c> query and splits a mapping's dataset-id
/// list into as many requests as it takes to stay inside the server's
/// request-line limit.
///
/// <para>
/// The timeseries endpoint takes its dataset ids as a comma-separated query
/// parameter, and the two OilX mappings carry thousands of them — mapping 375
/// has 7,092 ids (49,643 characters), mapping 371 has 2,496 (17,471). Both are
/// far past what the server accepts, so both answered <c>414 Request-URI Too
/// Large</c> on every window, every run.
/// </para>
///
/// <para>
/// The limit was measured against the live API rather than assumed: a 16,312-
/// character request succeeds and a 16,592-character one returns 414, so the
/// server cuts off at 16,384 (see <see cref="ServerRequestUriLimit"/>). There is
/// no way around it from the client side — <c>POST</c> to the same endpoint
/// answers <c>405 Method Not Allowed</c> for both form-encoded and JSON bodies,
/// so the id list has to travel in the URL and the only fix is to send fewer
/// ids per request.
/// </para>
///
/// <para>
/// Splitting is done on <em>measured URI length</em>, not on a count of ids,
/// because that is the constraint that actually failed and because it stays
/// correct as ids grow in digit count. It also happens to bound response time:
/// the API's latency tracks id count rather than window width (1,000 ids over
/// 30 days returns in ~12s; 2,310 ids over 2 days takes ~34s), so a budget that
/// keeps chunks near a thousand ids also keeps them inside
/// <see cref="EnergyAspectsSettings.HttpTimeoutSeconds"/>.
/// </para>
/// </summary>
internal static class EnergyAspectsRequest
{
    /// <summary>
    /// Longest request URI (path + query) the Energy Aspects API accepts, in
    /// characters. Measured 2026-10-13: 16,312 → 200, 16,592 → 414.
    /// <see cref="EnergyAspectsSettings.MaxRequestUriLength"/> is a budget to
    /// sit below this, never at or above it.
    /// </summary>
    public const int ServerRequestUriLimit = 16384;

    /// <summary>
    /// The relative request URI for one (dataset-id chunk × date window).
    /// Ids are formatted invariantly so the string the chunker measures is
    /// byte-for-byte the string the source reader sends.
    /// </summary>
    public static string BuildTimeseriesUri(
        EnergyAspectsSettings settings, IEnumerable<int> datasetIds, DateTime from, DateTime to)
    {
        var ids = string.Join(",", datasetIds.Select(id => id.ToString(CultureInfo.InvariantCulture)));
        return $"{settings.TimeseriesEndpoint}?api_key={settings.ApiKey}&dataset_id={ids}" +
               $"&date_from={from.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}" +
               $"&date_to={to.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}";
    }

    /// <summary>
    /// Characters a request costs before any dataset id is added — endpoint,
    /// api key and the two dates. The dates are a fixed 10 characters whatever
    /// they are, so any pair measures the same; the placeholders below are only
    /// there to make the call compile.
    /// </summary>
    public static int FixedUriLength(EnergyAspectsSettings settings) =>
        BuildTimeseriesUri(settings, Array.Empty<int>(), DateTime.UnixEpoch, DateTime.UnixEpoch).Length;

    /// <summary>
    /// Splits <paramref name="datasetIds"/> into the fewest consecutive chunks
    /// whose rendered request URI each stays within <paramref name="maxUriLength"/>.
    ///
    /// Order is preserved and never re-sorted: <c>usp_GetActiveMappings</c>
    /// already returns the ids ascending, and chunk membership has to be
    /// reproducible run to run or the per-part resume keys would not line up.
    /// </summary>
    public static IReadOnlyList<IReadOnlyList<int>> ChunkDatasetIds(
        IReadOnlyList<int> datasetIds, int fixedUriLength, int maxUriLength)
    {
        if (maxUriLength >= ServerRequestUriLimit)
            throw new ArgumentOutOfRangeException(nameof(maxUriLength), maxUriLength,
                $"MaxRequestUriLength must be below the API's {ServerRequestUriLimit}-character " +
                "request-line limit — it is the budget to stay under it, not the limit itself.");

        var budget = maxUriLength - fixedUriLength;
        if (budget <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxUriLength), maxUriLength,
                $"MaxRequestUriLength leaves no room for dataset ids: the endpoint, api key and " +
                $"dates alone take {fixedUriLength} characters.");

        if (datasetIds.Count == 0)
            return Array.Empty<IReadOnlyList<int>>();

        var chunks = new List<IReadOnlyList<int>>();
        var current = new List<int>();
        var currentLength = 0;

        foreach (var id in datasetIds)
        {
            // Every id after the first in a chunk also pays for its comma.
            var cost = DigitCount(id) + (current.Count > 0 ? 1 : 0);

            if (current.Count > 0 && currentLength + cost > budget)
            {
                chunks.Add(current);
                current = new List<int>();
                currentLength = 0;
                cost = DigitCount(id);
            }

            if (current.Count == 0 && cost > budget)
                throw new InvalidOperationException(
                    $"Dataset id {id} does not fit in a {budget}-character budget on its own.");

            current.Add(id);
            currentLength += cost;
        }

        chunks.Add(current);
        return chunks;
    }

    private static int DigitCount(int value)
    {
        // Dataset ids are positive, but a negative one would still need its
        // sign counted rather than silently under-measured.
        var digits = value < 0 ? 2 : 1;
        var remaining = Math.Abs((long)value);
        while (remaining >= 10)
        {
            remaining /= 10;
            digits++;
        }
        return digits;
    }
}
