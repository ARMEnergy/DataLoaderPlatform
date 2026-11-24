using System.Data;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DataLoader.MISO.Tests;

/// <summary>
/// tvp-contract-check. A TVP binds BY POSITION, so a DataTable and a <c>CREATE TYPE</c> that disagree
/// on order or type write every row into the wrong columns without an error. These tests PARSE THE
/// REAL sql/MISO files and compare them with the DataTable each sink builds and with design §2's
/// literal lists, so drift on either side fails the build.
/// </summary>
public sealed class SqlContractTests
{
    private static string Load(string file) => TvpParser.StripComments(File.ReadAllText(RepoPaths.Sql(file)));

    private static readonly Lazy<string> Schema = new(() => Load("001_CreateMisoSchema.sql"));
    private static readonly Lazy<string> Tvp = new(() => Load("002_CreateMisoTvpTypes.sql"));
    private static readonly Lazy<string> Procs = new(() => Load("003_CreateMisoProcedures.sql"));

    private const string Dt = "DATETIME2(0)";
    private const string Dec = "DECIMAL(18,4)";

    private static (string, string, bool, Type)[] Interval() => new[]
    {
        ("interval_start_local", Dt, true, typeof(DateTime)),
        ("interval_start_utc", Dt, true, typeof(DateTime)),
        ("interval_end_local", Dt, true, typeof(DateTime)),
        ("interval_end_utc", Dt, true, typeof(DateTime)),
    };

    private static IEnumerable<(string, string, bool, Type)> Measures(params string[] names) =>
        names.Select(n => (n, Dec, false, typeof(decimal)));

    /// <summary>Design §2 literal lists: (name, SQL type, NOT NULL, CLR type), in TVP order.</summary>
    private static readonly Dictionary<string, (string Tvp, string Proc, string Table, string[] Key, (string Name, string Sql, bool NotNull, Type Clr)[] Contract, Func<object> Sink)> All = new()
    {
        ["zonal"] = ("miso.MisoZonalLoadHourlyType", "miso.usp_MergeMisoZonalLoadHourly", "miso.miso_zonal_load_hourly", new[] { "interval_start_utc" },
            Interval().Concat(Measures("lrz1", "lrz2_7", "lrz3_5", "lrz4", "lrz6", "lrz8_9_10", "miso")).ToArray(),
            () => new MisoZonalLoadHourlySqlSink(Samples.Settings(), NullLogger<MisoZonalLoadHourlySqlSink>.Instance)),
        ["load"] = ("miso.MisoLoadHourlyType", "miso.usp_MergeMisoLoadHourly", "miso.miso_load_hourly", new[] { "interval_start_utc" },
            Interval().Concat(Measures("north", "central", "south", "miso")).ToArray(),
            () => new MisoLoadHourlySqlSink(Samples.Settings(), NullLogger<MisoLoadHourlySqlSink>.Instance)),
        ["fuel"] = ("miso.MisoFuelMixHourlyType", "miso.usp_MergeMisoFuelMixHourly", "miso.miso_fuel_mix_hourly", new[] { "interval_start_utc", "region" },
            Interval().Append(("region", "VARCHAR(20)", true, typeof(string)))
                .Concat(Measures("coal", "gas", "nuclear", "water", "wind", "solar", "other", "storage", "total_mw")).ToArray(),
            () => new MisoFuelMixHourlySqlSink(Samples.Settings(), NullLogger<MisoFuelMixHourlySqlSink>.Instance)),
        ["mtlf"] = ("miso.MisoLoadForecastMtlfType", "miso.usp_MergeMisoLoadForecastMtlf", "miso.miso_load_forecast_mtlf",
            new[] { "init_date", "interval_start_utc", "local_resource_zone" },
            new[] { ("init_date", "DATE", true, typeof(DateTime)) }.Concat(Interval())
                .Append(("region", "VARCHAR(20)", true, typeof(string)))
                .Append(("local_resource_zone", "VARCHAR(10)", true, typeof(string)))
                .Concat(Measures("load_forecast")).ToArray(),
            () => new MisoLoadForecastMtlfSqlSink(Samples.Settings(), NullLogger<MisoLoadForecastMtlfSqlSink>.Instance)),
    };

    public static IEnumerable<object[]> Sinks() => All.Keys.Select(k => new object[] { k });

    /// <summary>The only SQL→CLR mapping the TVP may use (SqlClient's DataTable binding).</summary>
    private static Type ClrFor(string sqlType)
    {
        var t = sqlType.ToUpperInvariant();
        if (t.StartsWith("DATETIME2") || t == "DATE") return typeof(DateTime);
        if (t.StartsWith("VARCHAR") || t.StartsWith("NVARCHAR")) return typeof(string);
        if (t.StartsWith("DECIMAL")) return typeof(decimal);
        if (t == "BIT") return typeof(bool);
        throw new InvalidOperationException($"unmapped SQL type {sqlType}");
    }

    private static DataTable EmptyTable(string which) => All[which].Sink() switch
    {
        MisoZonalLoadHourlySqlSink s => s.BuildTableForTests(Array.Empty<MisoZonalLoadHourlyRow>()),
        MisoLoadHourlySqlSink s => s.BuildTableForTests(Array.Empty<MisoLoadHourlyRow>()),
        MisoFuelMixHourlySqlSink s => s.BuildTableForTests(Array.Empty<MisoFuelMixHourlyRow>()),
        MisoLoadForecastMtlfSqlSink s => s.BuildTableForTests(Array.Empty<MisoLoadForecastMtlfRow>()),
        var other => throw new InvalidOperationException(other.GetType().Name),
    };

    [Theory]
    [MemberData(nameof(Sinks))]
    public void DataTable_matches_the_parsed_002_TVP_by_name_order_type_and_nullability(string which)
    {
        var sql = TvpParser.Parse(Tvp.Value, All[which].Tvp);
        var table = EmptyTable(which);

        Assert.NotEmpty(sql);
        Assert.Equal(sql.Count, table.Columns.Count);
        for (var i = 0; i < sql.Count; i++)
        {
            var col = table.Columns[i];
            Assert.Equal(sql[i].Name, col.ColumnName);              // NAME + ORDER
            Assert.Equal(ClrFor(sql[i].SqlType), col.DataType);     // TYPE
            Assert.Equal(!sql[i].NotNull, col.AllowDBNull);         // NULLABILITY
        }
    }

    [Theory]
    [MemberData(nameof(Sinks))]
    public void Parsed_TVP_and_DataTable_equal_the_design_literal_list(string which)
    {
        var sql = TvpParser.Parse(Tvp.Value, All[which].Tvp);
        var contract = All[which].Contract;

        Assert.Equal(contract.Select(c => (c.Name, c.Sql.Replace(" ", ""), c.NotNull)),
            sql.Select(c => (c.Name, c.SqlType.Replace(" ", "").ToUpperInvariant(), c.NotNull)));
        Assert.Equal(contract.Select(c => (c.Name, c.Clr, !c.NotNull)),
            EmptyTable(which).Columns.Cast<DataColumn>().Select(c => (c.ColumnName, c.DataType, c.AllowDBNull)));
    }

    [Fact]
    public void Exactly_the_four_MISO_types_are_declared()
    {
        Assert.Equal(All.Values.Select(v => v.Tvp), TvpParser.DeclaredTypeNames(Tvp.Value));
    }

    [Theory]
    [MemberData(nameof(Sinks))]
    public void Table_columns_are_the_TVP_columns_then_the_two_audit_columns(string which)
    {
        var tableCols = TvpParser.ParseTable(Schema.Value, All[which].Table);
        var tvpCols = TvpParser.Parse(Tvp.Value, All[which].Tvp);

        Assert.Equal(tvpCols.Count + 2, tableCols.Count);
        Assert.Equal(tvpCols, tableCols.Take(tvpCols.Count));
        Assert.Equal(new[] { "CreatedAtUtc", "ModifiedAtUtc" }, tableCols.Skip(tvpCols.Count).Select(c => c.Name));
        Assert.DoesNotContain(tvpCols, c => c.Name is "CreatedAtUtc" or "ModifiedAtUtc");

        var flat = Regex.Replace(Schema.Value, @"\s+", " ");
        var stem = All[which].Table["miso.".Length..];
        Assert.Contains($"CONSTRAINT PK_{stem} PRIMARY KEY CLUSTERED ({string.Join(", ", All[which].Key)})", flat);
    }

    [Theory]
    [MemberData(nameof(Sinks))]
    public void Proc_INSERT_and_VALUES_lists_follow_the_TVP_order(string which)
    {
        var (tvp, proc, _, _, _, _) = All[which];
        var body = TvpParser.ProcedureBody(Procs.Value, proc);
        var names = TvpParser.Parse(Tvp.Value, tvp).Select(c => c.Name).ToList();

        var insert = Regex.Match(body, @"INSERT\s*\((?<cols>[^)]*)\)\s*VALUES\s*\((?<vals>[^)]*)\)", RegexOptions.IgnoreCase);
        Assert.True(insert.Success);
        Assert.Equal(names, insert.Groups["cols"].Value.Split(',').Select(s => s.Trim()));
        Assert.Equal(names.Select(n => "s." + n), insert.Groups["vals"].Value.Split(',').Select(s => s.Trim()));
        Assert.Contains("SELECT CAST(@@ROWCOUNT AS INT) AS RecordsProcessed", Regex.Replace(body, @"\s+", " "));
    }

    [Theory]
    [MemberData(nameof(Sinks))]
    public void Sinks_target_the_procs_and_types_the_SQL_declares(string which)
    {
        var (tvp, proc, _, _, _, sinkFactory) = All[which];
        Assert.Matches(new Regex($@"CREATE OR ALTER PROCEDURE {Regex.Escape(proc)}\s+@Records {Regex.Escape(tvp)} READONLY"),
            Regex.Replace(Procs.Value, @"[ \t]+", " "));

        var sink = sinkFactory();
        const System.Reflection.BindingFlags np = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
        Assert.Equal(proc, sink.GetType().GetProperty("StoredProcedureName", np)!.GetValue(sink));
        Assert.Equal(tvp, sink.GetType().GetProperty("TableValuedParameterType", np)!.GetValue(sink));
    }

    [Theory]
    [InlineData("001_CreateMisoSchema.sql")]
    [InlineData("002_CreateMisoTvpTypes.sql")]
    [InlineData("003_CreateMisoProcedures.sql")]
    [InlineData("999_DropMisoObjects.sql")]
    public void Every_script_starts_with_USE_Misc_then_GO(string file)
    {
        Assert.Matches(new Regex(@"^USE\s+\[?Misc\]?\s*;\s*\r?\n\s*GO\b", RegexOptions.IgnoreCase), Load(file).TrimStart());
    }

    [Fact]
    public void Rows_land_in_the_right_columns()
    {
        var s = new DateTime(2026, 11, 18, 0, 0, 0);
        var zonal = new MisoZonalLoadHourlySqlSink(Samples.Settings(), NullLogger<MisoZonalLoadHourlySqlSink>.Instance).BuildTableForTests(new[]
        {
            new MisoZonalLoadHourlyRow
            {
                IntervalStartLocal = s, IntervalStartUtc = s.AddHours(5), IntervalEndLocal = s.AddHours(1), IntervalEndUtc = s.AddHours(6),
                Lrz1 = 1m, Lrz2_7 = 2m, Lrz3_5 = 3m, Lrz4 = 4m, Lrz6 = null, Lrz8_9_10 = 6m, Miso = null,
            },
        }).Rows[0];
        Assert.Equal(new object[] { s, s.AddHours(5), s.AddHours(1), s.AddHours(6), 1m, 2m, 3m, 4m, DBNull.Value, 6m, DBNull.Value }, zonal.ItemArray);

        var fuel = new MisoFuelMixHourlySqlSink(Samples.Settings(), NullLogger<MisoFuelMixHourlySqlSink>.Instance).BuildTableForTests(new[]
        {
            new MisoFuelMixHourlyRow
            {
                IntervalStartLocal = s, IntervalStartUtc = s, IntervalEndLocal = s, IntervalEndUtc = s, Region = "NORTH",
                Coal = 1m, Gas = 2m, Nuclear = 3m, Water = 4m, Wind = 5m, Solar = null, Other = 7m, Storage = 8m, TotalMw = 9m,
            },
        }).Rows[0];
        Assert.Equal(new object[] { s, s, s, s, "NORTH", 1m, 2m, 3m, 4m, 5m, DBNull.Value, 7m, 8m, 9m }, fuel.ItemArray);

        var mtlf = new MisoLoadForecastMtlfSqlSink(Samples.Settings(), NullLogger<MisoLoadForecastMtlfSqlSink>.Instance).BuildTableForTests(new[]
        {
            new MisoLoadForecastMtlfRow
            {
                InitDate = new DateOnly(2026, 11, 17), IntervalStartLocal = s, IntervalStartUtc = s.AddHours(5),
                IntervalEndLocal = s.AddHours(1), IntervalEndUtc = s.AddHours(6), Region = "CENTRAL", LocalResourceZone = "Z4", LoadForecast = 6092m,
            },
        }).Rows[0];
        Assert.Equal(new object[] { new DateTime(2026, 11, 17), s, s.AddHours(5), s.AddHours(1), s.AddHours(6), "CENTRAL", "Z4", 6092m }, mtlf.ItemArray);
    }
}
