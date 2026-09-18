using DataLoader.Core.Abstractions;

namespace DataLoader.Genscape.Tests;

/// <summary>Shared fixtures. Nothing here opens a socket.</summary>
internal static class TestHelpers
{
    public static GenscapeSettings Settings(Action<GenscapeSettings>? configure = null)
    {
        var settings = new GenscapeSettings
        {
            ConnectionString = "Server=(local);Database=Genscape;Integrated Security=SSPI;",
            ApiKey = "test-key"
        };

        configure?.Invoke(settings);
        return settings;
    }

    public static LoaderRunContext Context(DateTime? startedAtUtc = null, Guid? runId = null) => new()
    {
        RunId = runId ?? Guid.Parse("11111111-2222-3333-4444-555555555555"),
        StartedAtUtc = startedAtUtc ?? new DateTime(2026, 9, 8, 12, 0, 0, DateTimeKind.Utc),
        CancellationToken = CancellationToken.None
    };

    /// <summary>Index of a descriptor column by name — tests read row values positionally.</summary>
    public static int Ordinal(this GenscapeTableDescriptor table, string columnName)
    {
        for (var i = 0; i < table.Columns.Count; i++)
            if (table.Columns[i].Name.Equals(columnName, StringComparison.OrdinalIgnoreCase))
                return i;

        throw new ArgumentOutOfRangeException(nameof(columnName), columnName, $"Not a column of {table.TableName}.");
    }

    public static object Value(this GenscapeRow row, GenscapeTableDescriptor table, string columnName) =>
        row.Values[table.Ordinal(columnName)];
}

/// <summary>
/// Payloads captured VERBATIM from the live Genscape API on 2026-09-08 with the issued
/// key. Field names, casing, value scales and — crucially — the wrong week numbers are
/// the vendor's own, not invented.
///
/// <para>
/// Using real payloads is what makes the parser tests evidence that the mapping matches
/// production rather than evidence that it is self-consistent.
/// </para>
/// </summary>
internal static class Samples
{
    /// <summary>
    /// <c>crude-storage/weekly?region=NorthAmerica</c>.
    ///
    /// <para>
    /// ⚠ Note <c>"week": 1</c> on <b>2025-12-05</b>. That is the bug: the storage
    /// endpoint returns a week-of-MONTH (5 December is in the first week of December).
    /// The calendar week-of-year is <b>49</b> — which is exactly what the transportation
    /// endpoint returns for the same date, see <see cref="TransportationJson"/>.
    /// </para>
    /// </summary>
    public const string StorageJson =
        """
        {
          "data": [
            {
              "reportDate": "2025-12-05",
              "region": "Canada",
              "year": 2025,
              "week": 1,
              "storageAmount": 23813919,
              "capacityUtilization": 38.07,
              "product": "Crude",
              "storageFieldType": "Storage Terminal"
            },
            {
              "reportDate": "2025-12-05",
              "region": "Canada",
              "year": 2025,
              "week": 1,
              "storageAmount": 1846227,
              "capacityUtilization": 40.12,
              "product": "Diluent",
              "storageFieldType": "Storage Terminal"
            },
            {
              "reportDate": "2026-01-02",
              "region": "Cushing",
              "year": 2026,
              "week": 1,
              "storageAmount": 26867678,
              "capacityUtilization": 31.26,
              "product": "Crude",
              "storageFieldType": "Storage Terminal"
            }
          ]
        }
        """;

    /// <summary>
    /// <c>crude-transportation/weekly?region=NorthAmerica</c>.
    ///
    /// <para>
    /// <c>"week": 49</c> on 2025-12-05 — this endpoint already agrees with
    /// <c>DATEPART(week, …)</c>, which is how the derivation was validated.
    /// </para>
    /// </summary>
    public const string TransportationJson =
        """
        {
          "data": [
            {
              "type": "Pipeline",
              "reportDate": "2025-12-05",
              "region": "Canada to PADD 2",
              "year": 2025,
              "week": 49,
              "flowBPD": 3640786.0
            },
            {
              "type": "Pipeline",
              "reportDate": "2025-12-05",
              "region": "Canada to PADD 4",
              "year": 2025,
              "week": 49,
              "flowBPD": 383319.0
            },
            {
              "type": "Rail",
              "reportDate": "2026-01-02",
              "region": "Canada to PADD 2",
              "year": 2026,
              "week": 1,
              "flowBPD": 78123.0
            }
          ]
        }
        """;

    /// <summary>
    /// A window entirely outside the published history. <b>200 OK</b> with an empty
    /// array — a legitimate empty read, not an error. Verified live for
    /// <c>2027-01-01..2027-02-01</c>.
    /// </summary>
    public const string EmptyJson = """{ "data": [] }""";

    /// <summary>
    /// The RFC 7807 body a <c>400</c> carries. Verbatim from
    /// <c>startDate=2026-09-08&amp;endDate=2026-08-01</c>.
    /// </summary>
    public const string ReversedWindowProblemJson =
        """
        {"type":"https://developer.genscape.com/invalid-parameters","title":"Invalid parameters","status":400,"detail":"Your request parameters didn't validate","invalidParameters":[{"name":"startDate & endDate","reason":"[endDate] must be after [startDate]"}]}
        """;

    /// <summary>Verbatim from <c>region=Atlantis</c>.</summary>
    public const string BadRegionProblemJson =
        """
        {"type":"https://developer.genscape.com/invalid-parameters","title":"Invalid parameters","status":400,"detail":"Your request parameters didn't validate","invalidParameters":[{"name":"region","reason":"Must have a value specified"}]}
        """;

    /// <summary>Verbatim from omitting <c>region</c> altogether — note it is a 404, not a 400.</summary>
    public const string MissingParameterJson = """{ "statusCode": 404, "message": "Resource not found" }""";

    /// <summary>Builds a syntactically real envelope of <paramref name="count"/> storage rows.</summary>
    public static string StorageEnvelope(int count, DateOnly firstDate)
    {
        var rows = Enumerable.Range(0, count).Select(i =>
            $$"""
              {"reportDate":"{{firstDate.AddDays(-7 * (i / 3)):yyyy-MM-dd}}","region":"Region{{i % 3}}",
               "year":2026,"week":9,"storageAmount":{{1000 + i}},"capacityUtilization":50.5,
               "product":"Crude","storageFieldType":"Storage Terminal"}
              """);

        return $$"""{"data":[{{string.Join(",", rows)}}]}""";
    }
}
