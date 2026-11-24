using System.Data;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DataLoader.PJM.Tests;

/// <summary>
/// Design §12.2 tests 48-51 and the tvp-contract-check. A TVP binds BY POSITION, so a DataTable
/// and a <c>CREATE TYPE</c> that disagree on order or type write every row into the wrong columns
/// without an error. These tests PARSE THE REAL sql/PJM files, so drift on either side fails the build.
/// </summary>
public sealed class SqlContractTests
{
    private static readonly string Schema = TvpParser.StripComments(File.ReadAllText(RepoPaths.Sql("001_CreatePjmSchema.sql")));
    private static readonly string Tvp = TvpParser.StripComments(File.ReadAllText(RepoPaths.Sql("002_CreatePjmTvpTypes.sql")));
    private static readonly string Procs = TvpParser.StripComments(File.ReadAllText(RepoPaths.Sql("003_CreatePjmProcedures.sql")));
    private static readonly string Drop = TvpParser.StripComments(File.ReadAllText(RepoPaths.Sql("999_DropPjmObjects.sql")));

    /// <summary>Design §7.4 literal copy: (name, SQL type, NOT NULL, CLR type).</summary>
    private static readonly (string Name, string Sql, bool NotNull, Type Clr)[] FuelMixContract =
    {
        ("datetime_beginning_utc", "DATETIME2(0)", true, typeof(DateTime)),
        ("datetime_beginning_ept", "DATETIME2(0)", true, typeof(DateTime)),
        ("fuel_type", "VARCHAR(50)", true, typeof(string)),
        ("mw", "DECIMAL(18,4)", false, typeof(decimal)),
        ("fuel_percentage_of_total", "DECIMAL(9,6)", false, typeof(decimal)),
        ("is_renewable", "BIT", false, typeof(bool)),
    };

    private static readonly (string Name, string Sql, bool NotNull, Type Clr)[] LoadContract =
    {
        ("datetime_beginning_utc", "DATETIME2(0)", true, typeof(DateTime)),
        ("datetime_beginning_ept", "DATETIME2(0)", true, typeof(DateTime)),
        ("area", "VARCHAR(50)", true, typeof(string)),
        ("instantaneous_load", "DECIMAL(18,4)", false, typeof(decimal)),
    };

    /// <summary>The only SQL→CLR mapping the TVP may use (SqlClient's DataTable binding).</summary>
    private static Type ClrFor(string sqlType)
    {
        var t = sqlType.ToUpperInvariant();
        if (t.StartsWith("DATETIME2")) return typeof(DateTime);
        if (t.StartsWith("VARCHAR") || t.StartsWith("NVARCHAR")) return typeof(string);
        if (t.StartsWith("DECIMAL")) return typeof(decimal);
        if (t == "BIT") return typeof(bool);
        throw new InvalidOperationException($"unmapped SQL type {sqlType}");
    }

    private static DataTable FuelTable(IReadOnlyList<PjmFuelMixRow>? rows = null) =>
        new PjmFuelMixSqlSink(Samples.Settings(), NullLogger<PjmFuelMixSqlSink>.Instance).BuildTableForTests(rows ?? Array.Empty<PjmFuelMixRow>());

    private static DataTable LoadTable(IReadOnlyList<PjmLoadRow>? rows = null) =>
        new PjmLoadSqlSink(Samples.Settings(), NullLogger<PjmLoadSqlSink>.Instance).BuildTableForTests(rows ?? Array.Empty<PjmLoadRow>());

    // ---- §14.9 literal copies for the six added sinks (test 69) -------------------

    private static readonly (string Name, string Sql, bool NotNull, Type Clr)[] MeteredContract =
    {
        ("datetime_beginning_utc", "DATETIME2(0)", true, typeof(DateTime)),
        ("datetime_beginning_ept", "DATETIME2(0)", true, typeof(DateTime)),
        ("nerc_region", "VARCHAR(50)", false, typeof(string)),
        ("mkt_region", "VARCHAR(50)", false, typeof(string)),
        ("zone", "VARCHAR(50)", false, typeof(string)),
        ("load_area", "VARCHAR(50)", true, typeof(string)),
        ("mw", "DECIMAL(18,4)", false, typeof(decimal)),
        ("is_verified", "BIT", false, typeof(bool)),
    };

    private static readonly (string Name, string Sql, bool NotNull, Type Clr)[] PrelimContract =
    {
        ("datetime_beginning_utc", "DATETIME2(0)", true, typeof(DateTime)),
        ("datetime_beginning_ept", "DATETIME2(0)", true, typeof(DateTime)),
        ("datetime_ending_utc", "DATETIME2(0)", true, typeof(DateTime)),
        ("datetime_ending_ept", "DATETIME2(0)", true, typeof(DateTime)),
        ("load_area", "VARCHAR(50)", true, typeof(string)),
        ("prelim_load_avg_hourly", "DECIMAL(18,4)", false, typeof(decimal)),
    };

    private static readonly (string Name, string Sql, bool NotNull, Type Clr)[] EstimatedContract =
    {
        ("datetime_beginning_utc", "DATETIME2(0)", true, typeof(DateTime)),
        ("datetime_beginning_ept", "DATETIME2(0)", true, typeof(DateTime)),
        ("datetime_ending_utc", "DATETIME2(0)", true, typeof(DateTime)),
        ("datetime_ending_ept", "DATETIME2(0)", true, typeof(DateTime)),
        ("load_area", "VARCHAR(50)", true, typeof(string)),
        ("estimated_load_hourly", "DECIMAL(18,4)", false, typeof(decimal)),
    };

    private static readonly (string Name, string Sql, bool NotNull, Type Clr)[] HistContract =
    {
        ("evaluated_at_utc", "DATETIME2(0)", true, typeof(DateTime)),
        ("evaluated_at_ept", "DATETIME2(0)", true, typeof(DateTime)),
        ("forecast_hour_beginning_utc", "DATETIME2(0)", true, typeof(DateTime)),
        ("forecast_hour_beginning_ept", "DATETIME2(0)", true, typeof(DateTime)),
        ("forecast_area", "VARCHAR(50)", true, typeof(string)),
        ("forecast_load_mw", "DECIMAL(18,4)", false, typeof(decimal)),
    };

    private static readonly (string Name, string Sql, bool NotNull, Type Clr)[] VeryShortContract =
    {
        ("evaluated_at_utc", "DATETIME2(0)", true, typeof(DateTime)),
        ("evaluated_at_ept", "DATETIME2(0)", true, typeof(DateTime)),
        ("forecast_datetime_beginning_utc", "DATETIME2(0)", true, typeof(DateTime)),
        ("forecast_datetime_beginning_ept", "DATETIME2(0)", true, typeof(DateTime)),
        ("forecast_datetime_ending_utc", "DATETIME2(0)", true, typeof(DateTime)),
        ("forecast_datetime_ending_ept", "DATETIME2(0)", true, typeof(DateTime)),
        ("forecast_area", "VARCHAR(50)", true, typeof(string)),
        ("forecast_load_mw", "DECIMAL(18,4)", false, typeof(decimal)),
    };

    private static readonly (string Name, string Sql, bool NotNull, Type Clr)[] SevenDayContract =
    {
        ("evaluated_at_datetime_utc", "DATETIME2(0)", true, typeof(DateTime)),
        ("evaluated_at_datetime_ept", "DATETIME2(0)", true, typeof(DateTime)),
        ("forecast_datetime_beginning_utc", "DATETIME2(0)", true, typeof(DateTime)),
        ("forecast_datetime_beginning_ept", "DATETIME2(0)", true, typeof(DateTime)),
        ("forecast_datetime_ending_utc", "DATETIME2(0)", true, typeof(DateTime)),
        ("forecast_datetime_ending_ept", "DATETIME2(0)", true, typeof(DateTime)),
        ("forecast_area", "VARCHAR(50)", true, typeof(string)),
        ("forecast_load_mw", "DECIMAL(18,4)", false, typeof(decimal)),
    };

    private static PjmSettings Cfg => Samples.Settings();

    /// <summary>One entry per sink: TVP type, proc, table, key columns (key order), literal contract, empty DataTable, sink instance.</summary>
    private static readonly Dictionary<string, (string Tvp, string Proc, string Table, string[] Key, (string Name, string Sql, bool NotNull, Type Clr)[] Contract, Func<DataTable> Empty, Func<object> Sink)> All = new()
    {
        ["fuel"] = ("dbo.PjmFuelMixType", "dbo.usp_MergePjmFuelMix", "dbo.pjm_fuel_mix", new[] { "datetime_beginning_utc", "fuel_type" }, FuelMixContract,
            () => FuelTable(), () => new PjmFuelMixSqlSink(Cfg, NullLogger<PjmFuelMixSqlSink>.Instance)),
        ["load"] = ("dbo.PjmLoadType", "dbo.usp_MergePjmLoad", "dbo.pjm_load", new[] { "datetime_beginning_utc", "area" }, LoadContract,
            () => LoadTable(), () => new PjmLoadSqlSink(Cfg, NullLogger<PjmLoadSqlSink>.Instance)),
        ["metered"] = ("dbo.PjmLoadMeteredType", "dbo.usp_MergePjmLoadMetered", "dbo.pjm_load_metered", new[] { "datetime_beginning_utc", "load_area" }, MeteredContract,
            () => new PjmLoadMeteredSqlSink(Cfg, NullLogger<PjmLoadMeteredSqlSink>.Instance).BuildTableForTests(Array.Empty<PjmLoadMeteredRow>()),
            () => new PjmLoadMeteredSqlSink(Cfg, NullLogger<PjmLoadMeteredSqlSink>.Instance)),
        ["prelim"] = ("dbo.PjmLoadPrelimType", "dbo.usp_MergePjmLoadPrelim", "dbo.pjm_load_prelim", new[] { "datetime_beginning_utc", "load_area" }, PrelimContract,
            () => new PjmLoadPrelimSqlSink(Cfg, NullLogger<PjmLoadPrelimSqlSink>.Instance).BuildTableForTests(Array.Empty<PjmLoadPrelimRow>()),
            () => new PjmLoadPrelimSqlSink(Cfg, NullLogger<PjmLoadPrelimSqlSink>.Instance)),
        ["estimated"] = ("dbo.PjmLoadEstimatedType", "dbo.usp_MergePjmLoadEstimated", "dbo.pjm_load_estimated", new[] { "datetime_beginning_utc", "load_area" }, EstimatedContract,
            () => new PjmLoadEstimatedSqlSink(Cfg, NullLogger<PjmLoadEstimatedSqlSink>.Instance).BuildTableForTests(Array.Empty<PjmLoadEstimatedRow>()),
            () => new PjmLoadEstimatedSqlSink(Cfg, NullLogger<PjmLoadEstimatedSqlSink>.Instance)),
        ["hist"] = ("dbo.PjmLoadForecastHistType", "dbo.usp_MergePjmLoadForecastHist", "dbo.pjm_load_forecast_hist",
            new[] { "evaluated_at_utc", "forecast_hour_beginning_utc", "forecast_area" }, HistContract,
            () => new PjmLoadForecastHistSqlSink(Cfg, NullLogger<PjmLoadForecastHistSqlSink>.Instance).BuildTableForTests(Array.Empty<PjmLoadForecastHistRow>()),
            () => new PjmLoadForecastHistSqlSink(Cfg, NullLogger<PjmLoadForecastHistSqlSink>.Instance)),
        ["veryshort"] = ("dbo.PjmLoadForecastVeryShortType", "dbo.usp_MergePjmLoadForecastVeryShort", "dbo.pjm_load_forecast_very_short",
            new[] { "evaluated_at_utc", "forecast_datetime_beginning_utc", "forecast_area" }, VeryShortContract,
            () => new PjmLoadForecastVeryShortSqlSink(Cfg, NullLogger<PjmLoadForecastVeryShortSqlSink>.Instance).BuildTableForTests(Array.Empty<PjmLoadForecastVeryShortRow>()),
            () => new PjmLoadForecastVeryShortSqlSink(Cfg, NullLogger<PjmLoadForecastVeryShortSqlSink>.Instance)),
        ["7day"] = ("dbo.PjmLoadForecast7DayType", "dbo.usp_MergePjmLoadForecast7Day", "dbo.pjm_load_forecast_7day",
            new[] { "evaluated_at_datetime_utc", "forecast_datetime_beginning_utc", "forecast_area" }, SevenDayContract,
            () => new PjmLoadForecast7DaySqlSink(Cfg, NullLogger<PjmLoadForecast7DaySqlSink>.Instance).BuildTableForTests(Array.Empty<PjmLoadForecast7DayRow>()),
            () => new PjmLoadForecast7DaySqlSink(Cfg, NullLogger<PjmLoadForecast7DaySqlSink>.Instance)),
    };

    public static IEnumerable<object[]> Pairs() => All.Select(kv => new object[] { kv.Value.Tvp, kv.Key });
    public static IEnumerable<object[]> Sinks() => All.Keys.Select(k => new object[] { k });
    public static IEnumerable<object[]> NewSinks() => All.Keys.Where(k => k is not ("fuel" or "load")).Select(k => new object[] { k });

    private static DataTable TableFor(string which) => All[which].Empty();
    private static (string Name, string Sql, bool NotNull, Type Clr)[] ContractFor(string which) => All[which].Contract;

    // ---- 48: DataTable vs parsed 002 vs literal design list -----------------------

    [Theory]
    [MemberData(nameof(Pairs))]
    public void T48_DataTable_matches_the_parsed_TVP_by_name_order_type_and_nullability(string tvpType, string which)
    {
        var sql = TvpParser.Parse(Tvp, tvpType);
        var table = TableFor(which);

        Assert.NotEmpty(sql);
        Assert.Equal(sql.Count, table.Columns.Count);
        for (var i = 0; i < sql.Count; i++)
        {
            var col = table.Columns[i];
            Assert.Equal(sql[i].Name, col.ColumnName);                     // NAME + ORDER
            Assert.Equal(ClrFor(sql[i].SqlType), col.DataType);           // TYPE
            Assert.Equal(!sql[i].NotNull, col.AllowDBNull);                // NULLABILITY
        }
    }

    [Theory]
    [MemberData(nameof(Pairs))]
    public void T48_Parsed_TVP_and_DataTable_equal_the_design_literal_list(string tvpType, string which)
    {
        var sql = TvpParser.Parse(Tvp, tvpType);
        var table = TableFor(which);
        var contract = ContractFor(which);

        Assert.Equal(contract.Select(c => (c.Name, c.Sql.Replace(" ", ""), c.NotNull)),
            sql.Select(c => (c.Name, c.SqlType.Replace(" ", "").ToUpperInvariant(), c.NotNull)));
        Assert.Equal(contract.Select(c => (c.Name, c.Clr, !c.NotNull)),
            table.Columns.Cast<DataColumn>().Select(c => (c.ColumnName, c.DataType, c.AllowDBNull)));
    }

    [Fact]
    public void T48_002_declares_exactly_the_eight_PJM_types()
    {
        // Design §14.10: the six §14.9 types were added to 002 in place.
        Assert.Equal(new[]
        {
            "dbo.PjmFuelMixType", "dbo.PjmLoadType",
            "dbo.PjmLoadMeteredType", "dbo.PjmLoadPrelimType", "dbo.PjmLoadEstimatedType",
            "dbo.PjmLoadForecastHistType", "dbo.PjmLoadForecastVeryShortType", "dbo.PjmLoadForecast7DayType",
        }, TvpParser.DeclaredTypeNames(Tvp));
    }

    [Theory]
    [MemberData(nameof(Sinks))]
    public void T48_001_table_source_columns_equal_the_TVP_then_the_two_audit_columns(string which)
    {
        var (tvpType, _, table, _, _, _, _) = All[which];
        var tableCols = TvpParser.ParseTable(Schema, table);
        var tvpCols = TvpParser.Parse(Tvp, tvpType);

        Assert.Equal(tvpCols.Count + 2, tableCols.Count);
        Assert.Equal(tvpCols, tableCols.Take(tvpCols.Count));
        Assert.Equal(new[] { "CreatedAtUtc", "ModifiedAtUtc" }, tableCols.Skip(tvpCols.Count).Select(c => c.Name));
        Assert.DoesNotContain(tvpCols, c => c.Name is "CreatedAtUtc" or "ModifiedAtUtc");
    }

    [Theory]
    [MemberData(nameof(Sinks))]
    public void T48_003_INSERT_and_VALUES_lists_follow_the_TVP_order(string which)
    {
        var (tvpType, proc, _, _, _, _, _) = All[which];
        var body = TvpParser.ProcedureBody(Procs, proc);
        var names = TvpParser.Parse(Tvp, tvpType).Select(c => c.Name).ToList();

        var insert = Regex.Match(body, @"INSERT\s*\((?<cols>[^)]*)\)\s*VALUES\s*\((?<vals>[^)]*)\)", RegexOptions.IgnoreCase);
        Assert.True(insert.Success);
        Assert.Equal(names, insert.Groups["cols"].Value.Split(',').Select(s => s.Trim()));
        Assert.Equal(names.Select(n => "s." + n), insert.Groups["vals"].Value.Split(',').Select(s => s.Trim()));
        Assert.Contains($"@Records {tvpType} READONLY", Regex.Replace(body, @"\s+", " "));
    }

    [Fact]
    public void T48_Rows_land_in_the_right_columns()
    {
        var utc = new DateTime(2026, 11, 1, 6, 0, 0);
        var t = FuelTable(new[]
        {
            new PjmFuelMixRow { DatetimeBeginningUtc = utc, DatetimeBeginningEpt = utc.AddHours(-5), FuelType = "Wind", Mw = 1.5m, FuelPercentageOfTotal = null, IsRenewable = true },
        });
        var r = t.Rows[0];
        Assert.Equal(utc, r["datetime_beginning_utc"]);
        Assert.Equal(utc.AddHours(-5), r["datetime_beginning_ept"]);
        Assert.Equal("Wind", r["fuel_type"]);
        Assert.Equal(1.5m, r["mw"]);
        Assert.Equal(DBNull.Value, r["fuel_percentage_of_total"]);
        Assert.Equal(true, r["is_renewable"]);

        var l = LoadTable(new[] { new PjmLoadRow { DatetimeBeginningUtc = utc, DatetimeBeginningEpt = utc, Area = "RECO ", InstantaneousLoad = null } }).Rows[0];
        Assert.Equal("RECO ", l["area"]);
        Assert.Equal(DBNull.Value, l["instantaneous_load"]);
    }

    // ---- 49 -----------------------------------------------------------------------

    [Theory]
    [InlineData("001_CreatePjmSchema.sql")]
    [InlineData("002_CreatePjmTvpTypes.sql")]
    [InlineData("003_CreatePjmProcedures.sql")]
    [InlineData("999_DropPjmObjects.sql")]
    public void T49_First_statement_is_USE_Misc_then_GO(string file)
    {
        var code = TvpParser.StripComments(File.ReadAllText(RepoPaths.Sql(file))).TrimStart();
        Assert.Matches(new Regex(@"^USE\s+\[?Misc\]?\s*;\s*\r?\n\s*GO\b", RegexOptions.IgnoreCase), code);
    }

    [Fact]
    public void T49_Every_sql_PJM_file_is_covered_and_001_creates_no_database()
    {
        var files = Directory.GetFiles(RepoPaths.SqlDir, "*.sql", SearchOption.AllDirectories).Select(Path.GetFileName).OrderBy(f => f).ToList();
        Assert.Equal(new[] { "001_CreatePjmSchema.sql", "002_CreatePjmTvpTypes.sql", "003_CreatePjmProcedures.sql", "999_DropPjmObjects.sql" }, files);
        foreach (var f in Directory.GetFiles(RepoPaths.SqlDir, "*.sql", SearchOption.AllDirectories))
            Assert.DoesNotMatch(new Regex(@"\bCREATE\s+DATABASE\b", RegexOptions.IgnoreCase), TvpParser.StripComments(File.ReadAllText(f)));
    }

    // ---- 50 -----------------------------------------------------------------------

    [Fact]
    public void T50_999_drops_only_the_24_PJM_objects_each_dbo_qualified()
    {
        var targets = Regex.Matches(Drop, @"\bDROP\s+(?<kind>PROCEDURE|PROC|TYPE|TABLE|VIEW|FUNCTION|SCHEMA|INDEX|SYNONYM|DATABASE)\s+(IF\s+EXISTS\s+)?(?<name>[^\s;]+)", RegexOptions.IgnoreCase)
            .Select(m => (Kind: m.Groups["kind"].Value.ToUpperInvariant(), Name: m.Groups["name"].Value.Replace("[", "").Replace("]", "")))
            .ToList();

        // Design §14.10: 8 procs → 8 types → 8 tables, in dependency order.
        var stems = new[] { "FuelMix", "Load", "LoadMetered", "LoadPrelim", "LoadEstimated", "LoadForecastHist", "LoadForecastVeryShort", "LoadForecast7Day" };
        var tables = new[] { "pjm_fuel_mix", "pjm_load", "pjm_load_metered", "pjm_load_prelim", "pjm_load_estimated", "pjm_load_forecast_hist", "pjm_load_forecast_very_short", "pjm_load_forecast_7day" };
        var expected = stems.Select(s => ("PROCEDURE", $"dbo.usp_MergePjm{s}"))
            .Concat(stems.Select(s => ("TYPE", $"dbo.Pjm{s}Type")))
            .Concat(tables.Select(t => ("TABLE", $"dbo.{t}")))
            .ToList();
        Assert.Equal(expected, targets);

        // No other DROP of any kind, and no dynamic SQL that could drop something else.
        Assert.Equal(24, Regex.Matches(Drop, @"\bDROP\b", RegexOptions.IgnoreCase).Count);
        Assert.DoesNotMatch(new Regex(@"\b(EXEC|EXECUTE|sp_executesql|TRUNCATE|DELETE)\b", RegexOptions.IgnoreCase), Drop);
    }

    // ---- 51 (generalised to all eight procs; 70) -------------------------------

    [Theory]
    [MemberData(nameof(Sinks))]
    public void T51_MERGE_UPDATE_list_excludes_CreatedAtUtc_and_the_key_columns(string which)
    {
        var (tvp, proc, _, key, _, _, _) = All[which];
        var body = TvpParser.ProcedureBody(Procs, proc);
        var update = Regex.Match(body, @"UPDATE\s+SET(?<set>.*?)WHEN\s+NOT\s+MATCHED", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        Assert.True(update.Success, "no UPDATE SET ... WHEN NOT MATCHED found");

        var assigned = Regex.Matches(update.Groups["set"].Value, @"t\.(?<col>\w+)\s*=", RegexOptions.IgnoreCase)
            .Select(m => m.Groups["col"].Value).ToList();

        Assert.DoesNotContain("CreatedAtUtc", assigned, StringComparer.OrdinalIgnoreCase);
        foreach (var k in key)
            Assert.DoesNotContain(k, assigned, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("ModifiedAtUtc", assigned);

        // Every NON-key TVP column (EPT and ending columns included) is updated, so ModifiedAtUtc tracks any change.
        var nonKey = TvpParser.Parse(Tvp, tvp).Select(c => c.Name).Except(key).ToList();
        Assert.Equal(nonKey.Append("ModifiedAtUtc").OrderBy(x => x), assigned.OrderBy(x => x));

        // MERGE ON the full key, with a NULL-safe EXCEPT change test over all non-key columns.
        var on = Regex.Replace(Regex.Match(body, @"\bUSING\b.*?\bON\b(?<on>.*?)WHEN\s+MATCHED", RegexOptions.IgnoreCase | RegexOptions.Singleline).Groups["on"].Value, @"\s+", " ");
        foreach (var k in key)
            Assert.Matches(new Regex($@"t\.{k} = s\.{k}\b"), on);
        var except = Regex.Match(body, @"WHEN\s+MATCHED\s+AND\s+EXISTS\s*\(\s*SELECT(?<t>.*?)EXCEPT\s+SELECT(?<s>.*?)\)", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        Assert.True(except.Success, "no NULL-safe EXCEPT change test");
        Assert.Equal(nonKey.Select(c => "t." + c), except.Groups["t"].Value.Split(',').Select(x => x.Trim()));
        Assert.Equal(nonKey.Select(c => "s." + c), except.Groups["s"].Value.Split(',').Select(x => x.Trim()));
        Assert.DoesNotMatch(new Regex(@"NOT\s+MATCHED\s+BY\s+SOURCE", RegexOptions.IgnoreCase), body);
    }

    [Theory]
    [MemberData(nameof(Sinks))]
    public void T51_Procs_THROW_on_a_duplicate_key_before_the_MERGE(string which)
    {
        var (_, proc, _, key, _, _, _) = All[which];
        var body = TvpParser.ProcedureBody(Procs, proc);
        var flat = Regex.Replace(body, @"\s+", " ");

        Assert.Contains($"GROUP BY {string.Join(", ", key.Select(k => "r." + k))} HAVING COUNT(*) > 1", flat);
        var throwAt = flat.IndexOf("THROW 50001", StringComparison.OrdinalIgnoreCase);
        // Find MERGE generally: the three forecast procs MERGE into a scoped CTE (MERGE tgt AS t), not MERGE dbo.
        var merges = Regex.Matches(flat, @"\bMERGE\s", RegexOptions.IgnoreCase);
        Assert.True(throwAt > 0, "no THROW 50001");
        Assert.Single(merges);
        Assert.True(throwAt < merges[0].Index, "THROW must precede the MERGE");
        Assert.Contains("SET XACT_ABORT ON", flat);
        Assert.Contains("SELECT CAST(@@ROWCOUNT AS INT) AS RecordsProcessed", flat);
        Assert.DoesNotMatch(new Regex(@"#\w+"), flat);   // table variables only
    }

    [Theory]
    [InlineData("hist", "dbo.pjm_load_forecast_hist", "evaluated_at_utc")]
    [InlineData("veryshort", "dbo.pjm_load_forecast_very_short", "evaluated_at_utc")]
    [InlineData("7day", "dbo.pjm_load_forecast_7day", "evaluated_at_datetime_utc")]
    public void T51_Forecast_procs_scope_the_MERGE_target_to_the_batch_evaluation_range(string which, string table, string evalCol)
    {
        var flat = Regex.Replace(TvpParser.ProcedureBody(Procs, All[which].Proc), @"\s+", " ");
        var cte = Regex.Match(flat, @"WITH tgt AS \((?<cte>.*?)\) MERGE tgt AS t", RegexOptions.IgnoreCase);
        Assert.True(cte.Success, "the forecast proc should MERGE into the scoped CTE tgt");
        Assert.Contains($"FROM {table} AS f", cte.Groups["cte"].Value);
        Assert.Contains($"f.{evalCol} >= @MinEval", cte.Groups["cte"].Value);
        Assert.Contains($"f.{evalCol} <= @MaxEval", cte.Groups["cte"].Value);
        Assert.Contains($"@MinEval = MIN(r.{evalCol})", flat);
        Assert.Contains($"@MaxEval = MAX(r.{evalCol})", flat);
        // The CTE must expose every TVP column plus ModifiedAtUtc, or the UPDATE/INSERT could not target them.
        foreach (var c in TvpParser.Parse(Tvp, All[which].Tvp).Select(c => c.Name).Append("ModifiedAtUtc"))
            Assert.Contains("f." + c, cte.Groups["cte"].Value);
    }

    [Theory]
    [MemberData(nameof(NewSinks))]
    public void T70_001_names_each_new_table_PK_and_DF_constraint_and_keys_it_in_order(string which)
    {
        var (_, _, table, key, _, _, _) = All[which];
        var stem = table["dbo.".Length..];
        var flat = Regex.Replace(Schema, @"\s+", " ");
        Assert.Contains($"CONSTRAINT PK_{stem} PRIMARY KEY CLUSTERED ({string.Join(", ", key)})", flat);
        Assert.Contains($"CONSTRAINT DF_{stem}_CreatedAtUtc DEFAULT", flat);
        Assert.Contains($"CONSTRAINT DF_{stem}_ModifiedAtUtc DEFAULT", flat);
        Assert.Contains($"IF OBJECT_ID(N'{table}', N'U') IS NULL", flat);
        Assert.DoesNotMatch(new Regex(@"\bCREATE\s+DATABASE\b", RegexOptions.IgnoreCase), Schema);
    }

    [Fact]
    public void T70_001_declares_exactly_the_eight_PJM_tables()
    {
        var tables = Regex.Matches(Schema, @"CREATE\s+TABLE\s+([A-Za-z0-9_\.\[\]]+)", RegexOptions.IgnoreCase)
            .Select(m => m.Groups[1].Value.Replace("[", "").Replace("]", "")).ToList();
        Assert.Equal(All.Values.Select(v => v.Table), tables);
    }

    [Theory]
    [MemberData(nameof(Sinks))]
    public void Sinks_target_the_procs_and_types_the_SQL_declares(string which)
    {
        // Pinned against 002/003 so a rename on either side fails here.
        var (tvp, proc, _, _, _, _, sinkFactory) = All[which];
        Assert.Matches(new Regex($@"CREATE OR ALTER PROCEDURE {Regex.Escape(proc)}\s+@Records {Regex.Escape(tvp)} READONLY"), Regex.Replace(Procs, @"[ \t]+", " "));
        var sink = sinkFactory();
        const System.Reflection.BindingFlags np = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
        Assert.Equal(proc, sink.GetType().GetProperty("StoredProcedureName", np)!.GetValue(sink));
        Assert.Equal(tvp, sink.GetType().GetProperty("TableValuedParameterType", np)!.GetValue(sink));
    }

    [Fact]
    public void Rows_of_the_new_sinks_land_in_the_right_columns()
    {
        var t0 = new DateTime(2026, 11, 1, 5, 55, 0);
        var vs = new PjmLoadForecastVeryShortSqlSink(Cfg, NullLogger<PjmLoadForecastVeryShortSqlSink>.Instance).BuildTableForTests(new[]
        {
            new PjmLoadForecastVeryShortRow
            {
                EvaluatedAtUtc = t0, EvaluatedAtEpt = t0.AddHours(-4), ForecastDatetimeBeginningUtc = t0.AddMinutes(1),
                ForecastDatetimeBeginningEpt = t0.AddMinutes(2), ForecastDatetimeEndingUtc = t0.AddMinutes(3),
                ForecastDatetimeEndingEpt = t0.AddMinutes(4), ForecastArea = "AE ", ForecastLoadMw = null,
            },
        }).Rows[0];
        Assert.Equal(new object[] { t0, t0.AddHours(-4), t0.AddMinutes(1), t0.AddMinutes(2), t0.AddMinutes(3), t0.AddMinutes(4), "AE ", DBNull.Value }, vs.ItemArray);

        var m = new PjmLoadMeteredSqlSink(Cfg, NullLogger<PjmLoadMeteredSqlSink>.Instance).BuildTableForTests(new[]
        {
            new PjmLoadMeteredRow { DatetimeBeginningUtc = t0, DatetimeBeginningEpt = t0, NercRegion = null, MktRegion = "MIDATL", Zone = null, LoadArea = "AECO", Mw = 1.5m, IsVerified = false },
        }).Rows[0];
        Assert.Equal(new object[] { t0, t0, DBNull.Value, "MIDATL", DBNull.Value, "AECO", 1.5m, false }, m.ItemArray);

        var seven = new PjmLoadForecast7DaySqlSink(Cfg, NullLogger<PjmLoadForecast7DaySqlSink>.Instance).BuildTableForTests(new[]
        {
            new PjmLoadForecast7DayRow
            {
                EvaluatedAtDatetimeUtc = new DateTime(2026, 11, 23, 22, 47, 12), EvaluatedAtDatetimeEpt = new DateTime(2026, 11, 23, 17, 47, 12),
                ForecastDatetimeBeginningUtc = t0, ForecastDatetimeBeginningEpt = t0, ForecastDatetimeEndingUtc = t0, ForecastDatetimeEndingEpt = t0,
                ForecastArea = "RTO", ForecastLoadMw = 9.25m,
            },
        }).Rows[0];
        Assert.Equal(new DateTime(2026, 11, 23, 22, 47, 12), seven["evaluated_at_datetime_utc"]);   // seconds preserved into the TVP
        Assert.Equal(9.25m, seven["forecast_load_mw"]);
    }
}
