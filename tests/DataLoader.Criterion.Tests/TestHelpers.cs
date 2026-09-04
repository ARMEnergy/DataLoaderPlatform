using DataLoader.Core.Abstractions;

namespace DataLoader.Criterion.Tests;

/// <summary>Shared fixtures. Nothing here opens a socket.</summary>
internal static class TestHelpers
{
    public static CriterionSettings Settings(Action<CriterionSettings>? configure = null)
    {
        var settings = new CriterionSettings
        {
            ConnectionString = "Server=(local);Database=Criterion;Integrated Security=SSPI;",
            SourceUsername = "test-user",
            SourcePassword = "test-password"
        };

        configure?.Invoke(settings);
        return settings;
    }

    public static LoaderRunContext Context(DateTime? startedAtUtc = null, Guid? runId = null) => new()
    {
        RunId = runId ?? Guid.Parse("11111111-2222-3333-4444-555555555555"),
        StartedAtUtc = startedAtUtc ?? new DateTime(2026, 9, 3, 12, 0, 0, DateTimeKind.Utc),
        CancellationToken = CancellationToken.None
    };

    /// <summary>Index of a descriptor column by name — tests read row values positionally.</summary>
    public static int Ordinal(this CriterionTableDescriptor table, string columnName)
    {
        for (var i = 0; i < table.Columns.Count; i++)
            if (table.Columns[i].Name.Equals(columnName, StringComparison.OrdinalIgnoreCase))
                return i;

        throw new ArgumentOutOfRangeException(nameof(columnName), columnName, $"Not a column of {table.TableName}.");
    }

    public static object Value(this CriterionRow row, CriterionTableDescriptor table, string columnName) =>
        row.Values[table.Ordinal(columnName)];
}

/// <summary>
/// Serves canned page keys and rows instead of hitting PostgreSQL, so the work-unit
/// provider and the reader can be tested end to end with no database.
/// </summary>
internal sealed class FakeCriterionSource : ICriterionSource
{
    private readonly Dictionary<string, IReadOnlyList<Guid>> _keysByDay = new();

    public List<CriterionWorkUnit> ReadCalls { get; } = new();
    public CriterionReadResult NextResult { get; set; } =
        new(Array.Empty<CriterionRow>(), 0, 0, 0, 0, 0);

    public void SetKeys(DateOnly day, params Guid[] keys) =>
        _keysByDay[CriterionTime.Iso(day)] = keys;

    public Task<IReadOnlyList<Guid>> GetPageKeysAsync(
        CriterionFeedDescriptor feed, DateOnly day, CancellationToken cancellationToken) =>
        Task.FromResult(_keysByDay.TryGetValue(CriterionTime.Iso(day), out var keys)
            ? keys
            : Array.Empty<Guid>());

    public Task<CriterionReadResult> ReadAsync(CriterionWorkUnit unit, CancellationToken cancellationToken)
    {
        ReadCalls.Add(unit);
        return Task.FromResult(NextResult);
    }
}

/// <summary>
/// Real payloads captured from the live source on 2026-09-03. Using the actual shapes
/// — rather than invented ones — is what makes the parser tests evidence that the
/// mapping matches production, not just that it is self-consistent.
/// </summary>
internal static class Samples
{
    /// <summary>
    /// The DAILY shape, ~97% of live rows. Values and dates are verbatim from the
    /// requester's own sample of <c>data_series.financial_json_partitioned</c>, which
    /// is also where the quoted-string value form comes from.
    /// </summary>
    public const string DailyArray =
        """
        [{"date":"10/5/2021","value":"21453.5943776865"},
         {"date":"10/6/2021","value":"21307.8482012404"},
         {"date":"10/7/2021","value":"21177.4490738948"},
         {"date":"10/8/2021","value":"21126.3882993794"},
         {"date":"10/9/2021","value":"21125.4122354563"}]
        """;

    /// <summary>
    /// The INTRADAY shape, ~3% of live rows — five-minute observations with a
    /// timestamp and a time zone. Trimmed from ERCOT Real-time Fuel Mix (17,496
    /// elements across 61 dates); the field names, formats and value scale are
    /// verbatim.
    ///
    /// <para>
    /// Six elements across TWO dates, three each, ascending in value so the
    /// "last wins" assertion has a distinguishable winner.
    /// </para>
    /// </summary>
    public const string IntradayArray =
        """
        [{"date":"07/05/2026","timestamp":"07/05/2026 00:04:57","time_zone":"Central Time","value":4966.60},
         {"date":"07/05/2026","timestamp":"07/05/2026 00:09:57","time_zone":"Central Time","value":4967.90},
         {"date":"07/05/2026","timestamp":"07/05/2026 00:14:57","time_zone":"Central Time","value":4968.30},
         {"date":"07/06/2026","timestamp":"07/06/2026 00:04:57","time_zone":"Central Time","value":5001.10},
         {"date":"07/06/2026","timestamp":"07/06/2026 00:09:57","time_zone":"Central Time","value":5002.20},
         {"date":"07/06/2026","timestamp":"07/06/2026 00:14:57","time_zone":"Central Time","value":5003.30}]
        """;

    /// <summary>The ISO-8601 date form, live in the same table as the two above.</summary>
    public const string IsoDateArray =
        """
        [{"date":"2016-08-21T00:00:00.000Z","value":1.5},
         {"date":"2016-08-22T00:00:00.000Z","value":2.5}]
        """;
}
