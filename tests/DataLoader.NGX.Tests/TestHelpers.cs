using System.Text;
using System.Text.RegularExpressions;
using DataLoader.Core.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DataLoader.NGX.Tests;

/// <summary>Shared fixtures. Nothing here opens a socket.</summary>
internal static class TestHelpers
{
    public static ILogger Log => NullLogger.Instance;

    public static NgxSettings Settings(Action<NgxSettings>? configure = null)
    {
        var settings = new NgxSettings
        {
            ConnectionString = "Server=(local);Database=NGX;Integrated Security=SSPI;",
            Username = "test-user",
            Password = "test-password"
        };

        configure?.Invoke(settings);
        return settings;
    }

    public static LoaderRunContext Context(DateTime? startedAtUtc = null, Guid? runId = null) => new()
    {
        RunId = runId ?? Guid.Parse("11111111-2222-3333-4444-555555555555"),

        // 2026-09-18 17:00 UTC is 12:00 US Central on the same day — comfortably away
        // from a midnight boundary, so a test that is not ABOUT the boundary is not
        // accidentally testing it.
        StartedAtUtc = startedAtUtc ?? new DateTime(2026, 9, 18, 17, 0, 0, DateTimeKind.Utc),
        CancellationToken = CancellationToken.None
    };

    public static NgxIndexPriceWorkUnit IndexUnit(
        DateOnly? start = null, DateOnly? end = null, params int[] ids) => new()
    {
        IndexIds = ids.Length > 0 ? ids : new[] { 350 },
        Start = start ?? new DateOnly(2026, 6, 1),
        End = end ?? new DateOnly(2027, 3, 1),
        ExecutionDate = new DateOnly(2026, 9, 18),
        KeySuffix = ":run=20260918"
    };

    public static NgxStripWorkUnit StripUnit(DateOnly? start = null, DateOnly? end = null) => new()
    {
        Start = start ?? new DateOnly(2026, 9, 1),
        End = end ?? new DateOnly(2026, 9, 1),
        KeySuffix = ":run=20260918"
    };

    public static Stream AsStream(this string xml) => new MemoryStream(Encoding.UTF8.GetBytes(xml));

    /// <summary>Index of a descriptor column by name — tests read row values positionally.</summary>
    public static int Ordinal(this NgxTableDescriptor table, string columnName)
    {
        for (var i = 0; i < table.Columns.Count; i++)
            if (table.Columns[i].Name.Equals(columnName, StringComparison.OrdinalIgnoreCase))
                return i;

        throw new ArgumentOutOfRangeException(nameof(columnName), columnName, $"Not a column of {table.TableName}.");
    }

    public static object Value(this NgxRow row, NgxTableDescriptor table, string columnName) =>
        row.Values[table.Ordinal(columnName)];
}

/// <summary>
/// Payloads captured VERBATIM from the live ICE NGX endpoints on 2026-09-18 with the
/// issued web-service credentials. Element names, nesting, whitespace, the THOUSANDS
/// SEPARATORS in the amounts and the Mountain UTC offsets are the vendor's own, not
/// invented.
///
/// <para>
/// Using real payloads is what makes the parser tests evidence that the mapping matches
/// production rather than evidence that it is self-consistent. Several of these
/// records were additionally cross-checked field-by-field against the incumbent
/// <c>dbo.IndexPrice</c> / <c>dbo.StripTradingSummary</c> rows for the same keys — see
/// <c>NgxParseTests</c>.
/// </para>
/// </summary>
internal static class Samples
{
    /// <summary>
    /// <c>indexPrice.xml?indexId=350&amp;effectiveStart=1-September-2026&amp;effectiveEnd=18-September-2026</c>,
    /// trimmed to two records and the envelope.
    ///
    /// <para>The first record HAS a <c>quantityTraded</c> block (note
    /// <c>313,100</c> — a thousands separator, not a decimal point). The second, taken
    /// from index 1, has NEITHER <c>quantityTraded</c> NOR <c>numberOfTrades</c>: the
    /// two are emitted together or not at all, and ~45% of rows lack both.</para>
    /// </summary>
    public const string IndexPriceXml =
        """
        <?xml version="1.0" encoding="UTF-8"?>
        <indexPriceList xmlns="http://www.ngx.com/Clearing"
                        xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance"
                        xsi:schemaLocation="http://www.ngx.com/Clearing NGXClearing.xsd">
          <listSize>2</listSize>
          <truncated>false</truncated>
          <pageSize>20000</pageSize>
          <pageNumber>1</pageNumber>
          <fullListSize>2</fullListSize>

          <indexPrices>

              <indexPriceSummary>
                <id>3086005</id>
                <uri>/ngxcs/indexPrice/3086005.xml</uri>
                <index>
                  <id>350</id>
                  <name>ICE NGX AB-NIT - TCPL-Empress Transport Day Ahead Index</name>
                  <uri>/ngxcs/priceIndex/350.xml</uri>
                </index>
                <priceEffectiveStart>2026-09-18</priceEffectiveStart>
                <priceEffectiveEnd>2026-09-18</priceEffectiveEnd>
                <sourceDataDeliveryStart>2026-09-18</sourceDataDeliveryStart>
                <sourceDataDeliveryEnd>2026-09-18</sourceDataDeliveryEnd>
                <price>
                  <amount>-0.0046</amount>
                  <currency>CAD</currency>
                </price>
                <duration>1</duration>

                  <quantityTraded>
                    <amount>313,100</amount>
                    <unit>GJ</unit>
                    <contractUnit>Day</contractUnit>
                    <totalAmount>313,100</totalAmount>
                  </quantityTraded>

                  <numberOfTrades>27</numberOfTrades>

                <settlementState>Settled</settlementState>
                <lastUpdateDate>2026-09-18T02:15:41-06:00</lastUpdateDate>
              </indexPriceSummary>

              <indexPriceSummary>
                <id>2842551</id>
                <uri>/ngxcs/indexPrice/2842551.xml</uri>
                <index>
                  <id>1</id>
                  <name>ICE NGX AB-NIT Month Ahead Index (7A)</name>
                  <uri>/ngxcs/priceIndex/1.xml</uri>
                </index>
                <priceEffectiveStart>2026-12-01</priceEffectiveStart>
                <priceEffectiveEnd>2026-12-31</priceEffectiveEnd>
                <sourceDataDeliveryStart>2026-12-01</sourceDataDeliveryStart>
                <sourceDataDeliveryEnd>2026-12-31</sourceDataDeliveryEnd>
                <price>
                  <amount>1.3087</amount>
                  <currency>CAD</currency>
                </price>
                <duration>31</duration>

                <settlementState>Projected</settlementState>
                <lastUpdateDate>2026-09-18T12:06:43-06:00</lastUpdateDate>
              </indexPriceSummary>

          </indexPrices>
          <indexPriceAggregates>
            <indexPriceAggregate>
              <weightedAveragePrice><amount>-0.0051</amount><currency>CAD</currency></weightedAveragePrice>
              <arithmeticAveragePrice><amount>-0.0051</amount><currency>CAD</currency></arithmeticAveragePrice>
              <total><amount>-0.0926</amount><currency>CAD</currency></total>
            </indexPriceAggregate>
          </indexPriceAggregates>
        </indexPriceList>
        """;

    /// <summary>
    /// The same document shape reporting itself TRUNCATED. This is what a caller that
    /// forgets <c>pageSize</c> gets: 50 of 1,237 rows, a 200 status, and nothing else to
    /// say the other 1,187 exist.
    /// </summary>
    public const string IndexPriceTruncatedXml =
        """
        <?xml version="1.0" encoding="UTF-8"?>
        <indexPriceList xmlns="http://www.ngx.com/Clearing">
          <listSize>1</listSize>
          <truncated>true</truncated>
          <pageSize>50</pageSize>
          <pageNumber>1</pageNumber>
          <fullListSize>1237</fullListSize>
          <indexPrices>
              <indexPriceSummary>
                <id>3086005</id>
                <index><id>350</id><name>ICE NGX AB-NIT - TCPL-Empress Transport Day Ahead Index</name></index>
                <priceEffectiveStart>2026-09-18</priceEffectiveStart>
                <priceEffectiveEnd>2026-09-18</priceEffectiveEnd>
                <price><amount>-0.0046</amount><currency>CAD</currency></price>
                <duration>1</duration>
                <settlementState>Settled</settlementState>
                <lastUpdateDate>2026-09-18T02:15:41-06:00</lastUpdateDate>
              </indexPriceSummary>
          </indexPrices>
        </indexPriceList>
        """;

    /// <summary>
    /// A window with no data. <b>200 OK</b> with a well-formed but empty list — a
    /// legitimate empty read, not an error. Verified live for 1-January-2030.
    /// </summary>
    public const string IndexPriceEmptyXml =
        """
        <?xml version="1.0" encoding="UTF-8"?>
        <indexPriceList xmlns="http://www.ngx.com/Clearing">
          <listSize>0</listSize>
          <truncated>false</truncated>
          <pageSize>20000</pageSize>
          <pageNumber>1</pageNumber>
          <fullListSize>0</fullListSize>
          <indexPrices></indexPrices>
        </indexPriceList>
        """;

    /// <summary>
    /// <c>stripTradingSummaryXml.xml?grouping=Hub&amp;tradeStartDate=1-September-2026&amp;tradeEndDate=01-September-2026</c>,
    /// trimmed to two of its 1,601 records. Note the SUMMER offset <c>-06:00</c>.
    /// </summary>
    public const string StripXml =
        """
        <?xml version="1.0" encoding="UTF-8"?>
        <stripTradingSummaries xsi:schemaLocation="http://www.ngx.com/Clearing NGXClearing.xsd" xmlns="http://www.ngx.com/Clearing" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance"><stripTradingSummary><hub><id>28</id><name>AB-NIT</name></hub><market><id>1</id><name>NGX Phys, FP (CA/GJ), AB-NIT</name></market><stripType>Yesterday</stripType><settlementTitle>1-September-2026 (31-August-2026)</settlementTitle><tradeDateTime>2026-09-01T06:37:06-06:00</tradeDateTime><exchangeReference>48000000003842</exchangeReference><cleared>true</cleared><beginDate>2026-08-31</beginDate><endDate>2026-08-31</endDate><tradedVolume><amount>2500</amount><unit>GJ</unit></tradedVolume><totalVolume><amount>2500</amount><unit>GJ</unit></totalVolume><totalVolumeInTJ>2.5</totalVolumeInTJ><price><amount>1.2000</amount><currency>CAD</currency></price><requestForQuoteIndicator>false</requestForQuoteIndicator><includeInIndexIndicator>true</includeInIndexIndicator></stripTradingSummary><stripTradingSummary><hub><id>28</id><name>AB-NIT</name></hub><market><id>1</id><name>NGX Phys, FP (CA/GJ), AB-NIT</name></market><stripType>Yesterday</stripType><settlementTitle>1-September-2026 (31-August-2026)</settlementTitle><tradeDateTime>2026-09-01T06:37:09-06:00</tradeDateTime><exchangeReference>48000000003846</exchangeReference><cleared>true</cleared><beginDate>2026-08-31</beginDate><endDate>2026-08-31</endDate><tradedVolume><amount>200</amount><unit>GJ</unit></tradedVolume><totalVolume><amount>200</amount><unit>GJ</unit></totalVolume><totalVolumeInTJ>0.2</totalVolumeInTJ><price><amount>1.2000</amount><currency>CAD</currency></price><requestForQuoteIndicator>false</requestForQuoteIndicator><includeInIndexIndicator>true</includeInIndexIndicator></stripTradingSummary></stripTradingSummaries>
        """;

    /// <summary>
    /// A WINTER trade, verbatim from 15-January-2026 — offset <c>-07:00</c>. Paired with
    /// <see cref="StripXml"/> this is what proves the timestamp conversion follows the
    /// stated offset across the DST boundary rather than adding a fixed hour.
    /// </summary>
    public const string StripWinterXml =
        """
        <?xml version="1.0" encoding="UTF-8"?>
        <stripTradingSummaries xmlns="http://www.ngx.com/Clearing"><stripTradingSummary><hub><id>28</id><name>AB-NIT</name></hub><market><id>4</id><name>NGX Phys, FP (CA/GJ), AB-NIT</name></market><stripType>Summer Block</stripType><settlementTitle>15-January-2026</settlementTitle><tradeDateTime>2026-01-15T14:14:54-07:00</tradeDateTime><exchangeReference>48000000007314</exchangeReference><cleared>true</cleared><beginDate>2026-04-01</beginDate><endDate>2026-10-31</endDate><tradedVolume><amount>5,000</amount><unit>GJ</unit></tradedVolume><totalVolume><amount>1,070,000</amount><unit>GJ</unit></totalVolume><totalVolumeInTJ>1070</totalVolumeInTJ><price><amount>2.4500</amount><currency>CAD</currency></price><requestForQuoteIndicator>false</requestForQuoteIndicator><includeInIndexIndicator>false</includeInIndexIndicator></stripTradingSummary></stripTradingSummaries>
        """;

    /// <summary>A trade day with no trades. 200, well-formed, zero records.</summary>
    public const string StripEmptyXml =
        """
        <?xml version="1.0" encoding="UTF-8"?>
        <stripTradingSummaries xmlns="http://www.ngx.com/Clearing"></stripTradingSummaries>
        """;

    /// <summary>
    /// What a request with missing or rejected Basic credentials ACTUALLY yields if the
    /// 302 is followed: the ICE SSO login page, status 200, 33 KB of HTML. The loader
    /// must never mistake this for an empty result, which is why redirects are disabled
    /// and a missing root element throws.
    /// </summary>
    public const string SsoLoginHtml =
        """
        <!DOCTYPE html>
        <html lang="en"><head><title>ICE SSO Client</title></head>
        <body><noscript>WARNING: Please enable Javascript when using this web site.</noscript>
        <div id="root"></div></body></html>
        """;
}

/// <summary>
/// Minimal T-SQL slicing, enough to pull a <c>CREATE TYPE</c> / <c>CREATE TABLE</c>
/// body or one procedure out of a script. Deliberately not a real parser — it only has
/// to handle the shapes this repo's own scripts use.
/// </summary>
internal static class TvpParser
{
    internal readonly record struct Column(string Name, string SqlType, bool NotNull);

    public static IReadOnlyList<Column> Parse(string sql, string typeName)
    {
        var header = new Regex(
            $@"CREATE\s+TYPE\s+{Regex.Escape(typeName)}\s+AS\s+TABLE\s*\(",
            RegexOptions.IgnoreCase);

        var match = header.Match(sql);
        if (!match.Success) return Array.Empty<Column>();

        var open = match.Index + match.Length - 1;   // index of the '('
        return SplitTopLevel(ReadBalanced(sql, open)).Select(ParseColumn).ToList();
    }

    public static IReadOnlyList<string> DeclaredTypeNames(string sql) =>
        Regex.Matches(sql, @"CREATE\s+TYPE\s+([A-Za-z0-9_\.\[\]]+)\s+AS\s+TABLE", RegexOptions.IgnoreCase)
             .Select(m => m.Groups[1].Value.Replace("[", string.Empty).Replace("]", string.Empty))
             .ToList();

    /// <summary>Body of a <c>CREATE TABLE</c>, for order checks against the descriptor.</summary>
    public static string CreateTableBody(string sql, string tableName)
    {
        var header = new Regex($@"CREATE\s+TABLE\s+{Regex.Escape(tableName)}\s*\(", RegexOptions.IgnoreCase);
        var match = header.Match(sql);

        if (!match.Success)
            throw new InvalidOperationException($"No CREATE TABLE found for {tableName}.");

        return ReadBalanced(sql, match.Index + match.Length - 1);
    }

    /// <summary>Text of one procedure, from its CREATE to the next one (or end of file).</summary>
    public static string ProcedureBody(string sql, string procName)
    {
        var header = new Regex(
            $@"CREATE\s+OR\s+ALTER\s+PROCEDURE\s+{Regex.Escape(procName)}\b",
            RegexOptions.IgnoreCase);

        var match = header.Match(sql);
        if (!match.Success)
            throw new InvalidOperationException($"No procedure found named {procName}.");

        var next = Regex.Match(
            sql[(match.Index + match.Length)..],
            @"CREATE\s+OR\s+ALTER\s+PROCEDURE\s+", RegexOptions.IgnoreCase);

        var length = next.Success ? next.Index : sql.Length - match.Index - match.Length;

        return sql.Substring(match.Index, match.Length + length);
    }

    /// <summary>
    /// Removes <c>--</c> line comments and <c>/* */</c> blocks. The procs in 003 discuss
    /// their own column names at length, so a Contains-style assertion has to look at
    /// code rather than prose.
    /// </summary>
    public static string StripComments(string sql)
    {
        var withoutBlocks = Regex.Replace(sql, @"/\*.*?\*/", " ", RegexOptions.Singleline);
        return Regex.Replace(withoutBlocks, @"--[^\r\n]*", " ");
    }

    /// <summary>Content between <paramref name="openIndex"/>'s paren and its match.</summary>
    private static string ReadBalanced(string sql, int openIndex)
    {
        var depth = 0;
        for (var i = openIndex; i < sql.Length; i++)
        {
            if (sql[i] == '(') depth++;
            else if (sql[i] == ')')
            {
                depth--;
                if (depth == 0) return sql[(openIndex + 1)..i];
            }
        }

        throw new InvalidOperationException("Unbalanced parentheses in body.");
    }

    /// <summary>Split on commas at depth 0, so DECIMAL(18,8) stays intact.</summary>
    private static IEnumerable<string> SplitTopLevel(string body)
    {
        var depth = 0;
        var start = 0;

        for (var i = 0; i < body.Length; i++)
        {
            if (body[i] == '(') depth++;
            else if (body[i] == ')') depth--;
            else if (body[i] == ',' && depth == 0)
            {
                yield return body[start..i];
                start = i + 1;
            }
        }

        if (start < body.Length) yield return body[start..];
    }

    private static Column ParseColumn(string declaration)
    {
        var text = StripComments(declaration).Trim();

        var notNull = Regex.IsMatch(text, @"\bNOT\s+NULL\b", RegexOptions.IgnoreCase);

        // Strip the nullability tail so what remains is "<name> <type>".
        text = Regex.Replace(text, @"\b(NOT\s+)?NULL\b", string.Empty, RegexOptions.IgnoreCase).Trim();

        var match = Regex.Match(text, @"^\[?(?<name>[A-Za-z0-9_]+)\]?\s+(?<type>.+)$", RegexOptions.Singleline);
        if (!match.Success)
            throw new InvalidOperationException($"Could not parse TVP column declaration: '{declaration}'.");

        return new Column(
            match.Groups["name"].Value.Trim(),
            Regex.Replace(match.Groups["type"].Value, @"\s+", " ").Trim(),
            notNull);
    }
}

/// <summary>Locates <c>sql/NGX</c> by walking up from the test binaries.</summary>
internal static class RepoPaths
{
    public static string SqlDirectory { get; } = FindSqlDirectory();

    public static string SchemaScript => Path.Combine(SqlDirectory, "001_CreateNgxSchema.sql");
    public static string TvpScript => Path.Combine(SqlDirectory, "002_CreateNgxTvpTypes.sql");
    public static string ProceduresScript => Path.Combine(SqlDirectory, "003_CreateNgxProcedures.sql");
    public static string DropScript => Path.Combine(SqlDirectory, "999_DropNgxObjects.sql");

    private static string FindSqlDirectory()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);

        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "sql", "NGX");
            if (Directory.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException(
            $"Could not locate sql/NGX by walking up from '{AppContext.BaseDirectory}'.");
    }
}
