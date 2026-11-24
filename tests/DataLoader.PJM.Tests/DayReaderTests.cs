using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Xunit;

namespace DataLoader.PJM.Tests;

/// <summary>
/// Design §12.2 tests 12-29: paging and reconciliation, field contract, value parsing, echo
/// guards (incl. DST), duplicate keys, completeness and the empty-read rule. All through
/// <see cref="PjmDayReader{TRow}"/> over a scripted <see cref="IPjmPageSource"/>.
/// </summary>
[Collection(ExtraFieldCollection.Name)]
public sealed class DayReaderTests
{
    private static readonly DateOnly Day = new(2026, 11, 20);

    private static (PjmDayReader<PjmFuelMixRow> Reader, CapturingLoggerFactory Logs) GenReader(IPjmPageSource src, int pageSize = 50000)
    {
        var logs = new CapturingLoggerFactory();
        var s = Samples.Settings(x => x.PageSize = pageSize);
        return (new PjmDayReader<PjmFuelMixRow>(src, new PjmFuelMixRowMapper(), s, logs.CreateLogger("t")), logs);
    }

    private static (PjmDayReader<PjmLoadRow> Reader, CapturingLoggerFactory Logs) LoadReader(IPjmPageSource src, int pageSize = 50000)
    {
        var logs = new CapturingLoggerFactory();
        var s = Samples.Settings(x => x.PageSize = pageSize);
        return (new PjmDayReader<PjmLoadRow>(src, new PjmLoadRowMapper(), s, logs.CreateLogger("t")), logs);
    }

    private static PjmDayWorkUnit Gen(DateOnly? day = null, int age = 3) => ClientFactory.GenUnit(day ?? Day, age);
    private static PjmDayWorkUnit Ld(DateOnly? day = null, int age = 3) => ClientFactory.LoadUnit(day ?? Day, age);

    private static FakePageSource Single(params JsonElement[] items) => FakePageSource.Paged(items, 50000);

    private static DateTime H(int hour, int minute = 0) => Day.ToDateTime(new TimeOnly(hour, minute));

    // ---- 12 -----------------------------------------------------------------------

    [Fact]
    public async Task T12_Single_page_maps_rows_and_reconciles()
    {
        var items = Samples.FuelDay(Day);
        var src = FakePageSource.Paged(items, 50000);
        var (reader, logs) = GenReader(src);

        var rows = await reader.ReadDayAsync(Gen(), CancellationToken.None);

        Assert.Equal(24, rows.Count);
        Assert.Equal(new[] { 1 }, src.StartRows);
        var r = rows[5];
        Assert.Equal(H(5), r.DatetimeBeginningUtc);
        Assert.Equal(H(0), rows[5].DatetimeBeginningEpt);   // 05:00Z in November = 00:00 EST
        Assert.Equal("Gas", r.FuelType);
        Assert.Equal(1234.5m, r.Mw);
        Assert.False(r.IsRenewable);
        Assert.Null(r.FuelPercentageOfTotal);
        Assert.Empty(logs.OfLevel(LogLevel.Warning));
    }

    // ---- 13 -----------------------------------------------------------------------

    [Fact]
    public async Task T13_Multi_page_uses_explicit_1_based_startRow_and_stops_at_totalRows()
    {
        var items = Samples.FuelDay(Day, hours: 5);
        var src = FakePageSource.Paged(items, 2);
        var (reader, _) = GenReader(src, pageSize: 2);

        var rows = await reader.ReadDayAsync(Gen(), CancellationToken.None);

        Assert.Equal(5, rows.Count);
        Assert.Equal(new[] { 1, 3, 5 }, src.StartRows);
    }

    [Fact]
    public async Task T13_links_next_is_never_followed_and_startRow_is_explicit_on_the_wire()
    {
        var items = Samples.FuelDay(Day, hours: 5);
        var stub = new StubHttpMessageHandler((req, _) =>
        {
            var (_, _, startRow) = Samples.Parse(req.RequestUri!);
            var page = items.Skip(startRow - 1).Take(2);
            return StubHttpMessageHandler.Json(HttpStatusCode.OK,
                Samples.Envelope(page, totalRows: 5, nextLink: "https://api.pjm.com/api/v1/gen_by_fuel?startRow=999&rowCount=1"));
        });
        var s = Samples.Settings(x => x.PageSize = 2);
        var (client, _, _, _) = ClientFactory.Build(stub, new FakeKeyProvider("k"), s);
        var reader = new PjmDayReader<PjmFuelMixRow>(client, new PjmFuelMixRowMapper(), s, ClientFactory.Null);

        var rows = await reader.ReadDayAsync(Gen(), CancellationToken.None);

        Assert.Equal(5, rows.Count);
        var uris = stub.Requests.Select(r => r.Uri).ToList();
        Assert.Equal(3, uris.Count);
        Assert.All(uris, u => Assert.Equal("/api/v1/gen_by_fuel", u.AbsolutePath));
        Assert.Equal(new[] { 1, 3, 5 }, uris.Select(u => Samples.Parse(u).StartRow));
        Assert.All(uris, u => Assert.DoesNotContain("startRow=999", u.Query));
        Assert.All(uris, u => Assert.Contains("sort=datetime_beginning_utc&order=Asc", u.Query));
        Assert.All(uris, u => Assert.Contains("rowCount=2", u.Query));
    }

    // ---- 14 -----------------------------------------------------------------------

    [Fact]
    public async Task T14_totalRows_change_triggers_one_reread_which_succeeds_when_consistent()
    {
        var items = Samples.FuelDay(Day, hours: 4);
        // call 0: page1 total 3; call 1: page2 total 4 (mismatch) -> re-read: calls 2,3 both total 4.
        var src = new FakePageSource((startRow, call) => call switch
        {
            0 => new PjmPage(items.Take(2).ToList(), 3),
            1 => new PjmPage(items.Skip(2).Take(2).ToList(), 4),
            _ => new PjmPage(items.Skip(startRow - 1).Take(2).ToList(), 4),
        });
        var (reader, logs) = GenReader(src, pageSize: 2);

        var rows = await reader.ReadDayAsync(Gen(), CancellationToken.None);

        Assert.Equal(4, rows.Count);
        Assert.Equal(new[] { 1, 3, 1, 3 }, src.StartRows);
        Assert.Single(logs.OfLevel(LogLevel.Warning), e => e.Message.Contains("snapshot mismatch"));
    }

    [Fact]
    public async Task T14_A_second_totalRows_mismatch_fails_the_unit()
    {
        var items = Samples.FuelDay(Day, hours: 4);
        var src = new FakePageSource((startRow, _) =>
            new PjmPage(items.Skip(startRow - 1).Take(2).ToList(), startRow == 1 ? 3 : 4));
        var (reader, _) = GenReader(src, pageSize: 2);

        var ex = await Assert.ThrowsAsync<PjmDataException>(() => reader.ReadDayAsync(Gen(), CancellationToken.None));
        Assert.Contains("did not reconcile twice", ex.Message);
        Assert.Equal(4, src.Calls);
    }

    // ---- 15 -----------------------------------------------------------------------

    [Fact]
    public async Task T15_Shortfall_fails_after_exactly_one_reread()
    {
        var items = Samples.FuelDay(Day, hours: 2);
        // totalRows says 5, but page 2 (startRow 3) is empty.
        var src = new FakePageSource((startRow, _) =>
            new PjmPage(startRow == 1 ? items.ToList() : new List<JsonElement>(), 5));
        var (reader, _) = GenReader(src, pageSize: 2);

        var ex = await Assert.ThrowsAsync<PjmDataException>(() => reader.ReadDayAsync(Gen(), CancellationToken.None));
        Assert.Contains("shortfall", ex.Message);
        Assert.Equal(new[] { 1, 3, 1, 3 }, src.StartRows);
    }

    [Fact]
    public async Task T15_Shortfall_then_consistent_reread_succeeds()
    {
        var items = Samples.FuelDay(Day, hours: 3);
        var src = new FakePageSource((startRow, call) => call == 1
            ? new PjmPage(new List<JsonElement>(), 3)
            : new PjmPage(items.Skip(startRow - 1).Take(2).ToList(), 3));
        var (reader, _) = GenReader(src, pageSize: 2);

        var rows = await reader.ReadDayAsync(Gen(), CancellationToken.None);
        Assert.Equal(3, rows.Count);
    }

    // ---- 16: envelope contract (through the client's parser) ----------------------

    private static async Task<Exception?> EnvelopeOutcome(string body)
    {
        var stub = StubHttpMessageHandler.Always(HttpStatusCode.OK, body);
        var (client, _, _, _) = ClientFactory.Build(stub, new FakeKeyProvider("k"));
        return await Record.ExceptionAsync(() => client.GetPageAsync(Gen(), 1, CancellationToken.None));
    }

    [Theory]
    [InlineData("{\"totalRows\":0}")]                      // items absent
    [InlineData("{\"items\":[]}")]                        // totalRows absent
    [InlineData("<html>gateway error</html>")]            // not JSON
    [InlineData("[]")]                                    // not an object
    [InlineData("{\"items\":{},\"totalRows\":0}")]        // items not an array
    [InlineData("{\"items\":[],\"totalRows\":\"x\"}")]    // totalRows not an integer
    public async Task T16_Envelope_contract_violations_are_contract_failures(string body)
    {
        var ex = await EnvelopeOutcome(body);
        Assert.IsType<PjmContractException>(ex);
    }

    [Fact]
    public async Task T16_PascalCase_TotalRows_and_Items_are_accepted()
    {
        var item = Samples.Fuel(H(1));
        var body = "{\"Items\":[" + item.GetRawText() + "],\"TotalRows\":1}";
        var stub = StubHttpMessageHandler.Always(HttpStatusCode.OK, body);
        var (client, _, _, _) = ClientFactory.Build(stub, new FakeKeyProvider("k"));

        var page = await client.GetPageAsync(Gen(), 1, CancellationToken.None);
        Assert.Equal(1, page.TotalRows);
        Assert.Single(page.Items);
    }

    [Fact]
    public async Task T16_Empty_items_with_totalRows_zero_is_a_valid_envelope()
    {
        var ex = await EnvelopeOutcome("{\"items\":[],\"totalRows\":0}");
        Assert.Null(ex);
    }

    // ---- 17 -----------------------------------------------------------------------

    [Fact]
    public async Task T17_Missing_required_field_fails_naming_the_field_but_never_a_value()
    {
        const string sentinel = "SENTINEL-VALUE-17";
        var bad = Samples.Fuel(H(2), fuel: sentinel, fields: new() { ["mw"] = Samples.Remove });
        var (reader, logs) = GenReader(Single(Samples.Fuel(H(1)), bad));

        var ex = await Assert.ThrowsAsync<PjmContractException>(() => reader.ReadDayAsync(Gen(), CancellationToken.None));
        Assert.Contains("'mw'", ex.Message);
        Assert.Contains("gen_by_fuel", ex.Message);
        Secrets.AssertAbsent(Secrets.ExceptionText(ex).Concat(logs.AllText()), sentinel);
    }

    [Theory]
    [InlineData("datetime_beginning_utc")]
    [InlineData("datetime_beginning_ept")]
    [InlineData("area")]
    [InlineData("instantaneous_load")]
    public async Task T17_inst_load_required_fields(string field)
    {
        var bad = Samples.Load(H(2), fields: new() { [field] = Samples.Remove });
        var (reader, _) = LoadReader(Single(bad));
        var ex = await Assert.ThrowsAsync<PjmContractException>(() => reader.ReadDayAsync(Ld(), CancellationToken.None));
        Assert.Contains($"'{field}'", ex.Message);
    }

    [Fact]
    public async Task T17_Field_names_match_case_insensitively()
    {
        var item = Samples.ElementFromJson(
            "{\"DATETIME_BEGINNING_UTC\":\"2026-11-20T05:00:00\",\"Datetime_Beginning_Ept\":\"2026-11-20T00:00:00\"," +
            "\"Area\":\"PJM RTO\",\"Instantaneous_Load\":1.5}");
        var (reader, logs) = LoadReader(Single(item));
        var rows = await reader.ReadDayAsync(Ld(), CancellationToken.None);
        Assert.Equal(1.5m, Assert.Single(rows).InstantaneousLoad);
        Assert.DoesNotContain(logs.OfLevel(LogLevel.Warning), e => e.Message.Contains("unexpected field"));
    }

    // ---- 18 -----------------------------------------------------------------------

    [Fact]
    public async Task T18_Optional_percentage_absent_is_null_with_no_warning()
    {
        var (reader, logs) = GenReader(FakePageSource.Paged(Samples.FuelDay(Day), 50000));
        var rows = await reader.ReadDayAsync(Gen(), CancellationToken.None);
        Assert.All(rows, r => Assert.Null(r.FuelPercentageOfTotal));
        Assert.Empty(logs.OfLevel(LogLevel.Warning));
    }

    [Theory]
    [InlineData(0.25, "0.25")]
    [InlineData(25.0, "25.0")]
    public async Task T18_Optional_percentage_present_is_stored_as_received(double value, string expected)
    {
        var items = Samples.FuelDay(Day).Select((e, i) => i == 0
            ? Samples.Fuel(H(0), fields: new() { ["fuel_percentage_of_total"] = value })
            : e).ToArray();
        var (reader, _) = GenReader(Single(items));
        var rows = await reader.ReadDayAsync(Gen(), CancellationToken.None);
        Assert.Equal(decimal.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), rows[0].FuelPercentageOfTotal);
    }

    // ---- 19 -----------------------------------------------------------------------

    [Fact]
    public async Task T19_Unexpected_field_warns_once_per_feed_per_process_with_name_not_value()
    {
        PjmDayReader<PjmFuelMixRow>.ResetExtraFieldWarningsForTests();
        const string sentinel = "SENTINEL-EXTRA-VALUE-19";
        try
        {
            var items = Enumerable.Range(0, 3)
                .Select(h => Samples.Fuel(H(h), fields: new() { ["zz_new_column"] = sentinel, ["aa_other"] = sentinel }))
                .ToArray();

            var (reader1, logs1) = GenReader(Single(items));
            await reader1.ReadDayAsync(Gen(), CancellationToken.None);
            var (reader2, logs2) = GenReader(Single(items));
            await reader2.ReadDayAsync(Gen(), CancellationToken.None);

            var warnings = logs1.OfLevel(LogLevel.Warning).Concat(logs2.OfLevel(LogLevel.Warning))
                .Where(e => e.Message.Contains("unexpected field")).ToList();
            var w = Assert.Single(warnings);
            Assert.Contains("aa_other, zz_new_column", w.Message);
            Secrets.AssertAbsent(logs1.AllText().Concat(logs2.AllText()), sentinel);

            // A different feed gets its own single warning.
            var loadItem = Samples.Load(H(1), fields: new() { ["zz_new_column"] = sentinel });
            var (reader3, logs3) = LoadReader(Single(loadItem));
            await reader3.ReadDayAsync(Ld(), CancellationToken.None);
            Assert.Single(logs3.OfLevel(LogLevel.Warning), e => e.Message.Contains("unexpected field"));
        }
        finally
        {
            PjmDayReader<PjmFuelMixRow>.ResetExtraFieldWarningsForTests();
        }
    }

    // ---- 20 -----------------------------------------------------------------------

    [Theory]
    [InlineData("2026-11-20T05:00:00", true)]
    [InlineData("2026-11-20T05:00:00Z", true)]
    [InlineData("2026-11-20T05:00:00+01:00", false)]
    [InlineData("2026-11-20T05:00:00-05:00", false)]
    [InlineData("2026-11-20T05:00:00.500", false)]
    [InlineData("2026-11-20T05:00:00.5Z", false)]
    [InlineData("2026-11-20 05:00:00", false)]
    [InlineData("11/20/2026 5:00:00 AM", false)]
    public async Task T20_UTC_timestamp_forms(string utc, bool accepted)
    {
        var item = Samples.Fuel(H(5), fields: new() { ["datetime_beginning_utc"] = utc });
        var (reader, _) = GenReader(Single(item));
        var ex = await Record.ExceptionAsync(() => reader.ReadDayAsync(Gen(), CancellationToken.None));
        if (accepted) Assert.Null(ex);
        else
        {
            var c = Assert.IsType<PjmContractException>(ex);
            Assert.Contains("datetime_beginning_utc", c.Message);
        }
    }

    [Fact]
    public async Task T20_Z_is_rejected_on_the_EPT_field()
    {
        var item = Samples.Fuel(H(5), fields: new() { ["datetime_beginning_ept"] = "2026-11-20T00:00:00Z" });
        var (reader, _) = GenReader(Single(item));
        var ex = await Assert.ThrowsAsync<PjmContractException>(() => reader.ReadDayAsync(Gen(), CancellationToken.None));
        Assert.Contains("datetime_beginning_ept", ex.Message);
    }

    // ---- 21 -----------------------------------------------------------------------

    public static IEnumerable<object?[]> RenewableCases() => new[]
    {
        new object?[] { true, true },
        new object?[] { "False", false },
        new object?[] { "TRUE", true },
        new object?[] { 1, true },
        new object?[] { 0, false },
        new object?[] { null, null },
    };

    [Theory]
    [MemberData(nameof(RenewableCases))]
    public async Task T21_is_renewable_accepted_forms(object? json, bool? expected)
    {
        var item = Samples.Fuel(H(1), fields: new() { ["is_renewable"] = json });
        var (reader, _) = GenReader(Single(item));
        var rows = await reader.ReadDayAsync(Gen(), CancellationToken.None);
        Assert.Equal(expected, Assert.Single(rows).IsRenewable);
    }

    [Theory]
    [InlineData("Y")]
    [InlineData("yes")]
    [InlineData(2)]
    public async Task T21_is_renewable_rejected_forms(object json)
    {
        var item = Samples.Fuel(H(1), fields: new() { ["is_renewable"] = json });
        var (reader, _) = GenReader(Single(item));
        var ex = await Assert.ThrowsAsync<PjmContractException>(() => reader.ReadDayAsync(Gen(), CancellationToken.None));
        Assert.Contains("is_renewable", ex.Message);
    }

    // ---- 22 -----------------------------------------------------------------------

    [Fact]
    public async Task T22_Numeric_string_mw_is_accepted()
    {
        var item = Samples.Fuel(H(1), fields: new() { ["mw"] = "123.4" });
        var (reader, _) = GenReader(Single(item));
        Assert.Equal(123.4m, Assert.Single(await reader.ReadDayAsync(Gen(), CancellationToken.None)).Mw);
    }

    [Fact]
    public async Task T22_Null_mw_is_null()
    {
        var item = Samples.Fuel(H(1), fields: new() { ["mw"] = null });
        var (reader, _) = GenReader(Single(item));
        Assert.Null(Assert.Single(await reader.ReadDayAsync(Gen(), CancellationToken.None)).Mw);
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("100000000000000")]   // 10^14: beyond DECIMAL(18,4)'s 14 integer digits
    [InlineData(true)]
    public async Task T22_Bad_or_overflowing_mw_fails(object mw)
    {
        var item = Samples.Fuel(H(1), fields: new() { ["mw"] = mw });
        var (reader, _) = GenReader(Single(item));
        var ex = await Assert.ThrowsAsync<PjmContractException>(() => reader.ReadDayAsync(Gen(), CancellationToken.None));
        Assert.Contains("'mw'", ex.Message);
    }

    [Fact]
    public async Task T22_Overflowing_JSON_number_fails()
    {
        var item = Samples.ElementFromJson(Samples.Fuel(H(1)).GetRawText().Replace("1234.5", "1e30"));
        var (reader, _) = GenReader(Single(item));
        var ex = await Assert.ThrowsAsync<PjmContractException>(() => reader.ReadDayAsync(Gen(), CancellationToken.None));
        Assert.Contains("'mw'", ex.Message);
    }

    // ---- 23 -----------------------------------------------------------------------

    [Fact]
    public async Task T23_Area_longer_than_50_fails_with_the_field_name()
    {
        var item = Samples.Load(H(1), area: new string('A', 51));
        var (reader, _) = LoadReader(Single(item));
        var ex = await Assert.ThrowsAsync<PjmContractException>(() => reader.ReadDayAsync(Ld(), CancellationToken.None));
        Assert.Contains("'area'", ex.Message);
        Assert.DoesNotContain(new string('A', 51), ex.Message);
    }

    [Fact]
    public async Task T23_Area_of_exactly_50_is_stored_as_received_untrimmed()
    {
        var area = new string('B', 49) + " ";
        var (reader, _) = LoadReader(Single(Samples.Load(H(1), area: area)));
        Assert.Equal(area, Assert.Single(await reader.ReadDayAsync(Ld(), CancellationToken.None)).Area);
    }

    // ---- 24 -----------------------------------------------------------------------

    [Fact]
    public async Task T24_Row_stamped_next_midnight_fails_the_window_echo()
    {
        var item = Samples.Fuel(Day.AddDays(1).ToDateTime(TimeOnly.MinValue));
        var (reader, _) = GenReader(Single(Samples.Fuel(H(1)), item));
        var ex = await Assert.ThrowsAsync<PjmDataException>(() => reader.ReadDayAsync(Gen(), CancellationToken.None));
        Assert.Contains("outside the requested UTC day", ex.Message);
    }

    [Fact]
    public async Task T24_Row_from_the_previous_day_fails_the_window_echo()
    {
        var item = Samples.Fuel(Day.AddDays(-1).ToDateTime(new TimeOnly(23, 0)));
        var (reader, _) = GenReader(Single(item));
        await Assert.ThrowsAsync<PjmDataException>(() => reader.ReadDayAsync(Gen(), CancellationToken.None));
    }

    // ---- 25: EPT echo incl. DST ---------------------------------------------------

    [Fact]
    public async Task T25_EPT_echo_passes_on_a_normal_day()
    {
        var (reader, _) = GenReader(FakePageSource.Paged(Samples.FuelDay(Day), 50000));
        Assert.Equal(24, (await reader.ReadDayAsync(Gen(), CancellationToken.None)).Count);
    }

    [Fact]
    public async Task T25_Fall_back_2026_11_01_both_EPT_0100_copies_pass_for_gen_by_fuel()
    {
        var day = new DateOnly(2026, 11, 1);
        var items = Samples.FuelDay(day);
        var (reader, _) = GenReader(FakePageSource.Paged(items, 50000));

        var rows = await reader.ReadDayAsync(Gen(day), CancellationToken.None);

        Assert.Equal(24, rows.Count);
        // Hard-coded oracle: 05:00Z = 01:00 EDT and 06:00Z = 01:00 EST — the repeated wall clock.
        var oneAm = rows.Where(r => r.DatetimeBeginningEpt == new DateTime(2026, 11, 1, 1, 0, 0)).Select(r => r.DatetimeBeginningUtc).ToList();
        Assert.Equal(new[] { new DateTime(2026, 11, 1, 5, 0, 0), new DateTime(2026, 11, 1, 6, 0, 0) }, oneAm);
        Assert.Equal(new DateTime(2026, 10, 31, 20, 0, 0), rows[0].DatetimeBeginningEpt);   // 00:00Z = 20:00 EDT
        Assert.Equal(new DateTime(2026, 11, 1, 18, 0, 0), rows[23].DatetimeBeginningEpt);   // 23:00Z = 18:00 EST
    }

    [Fact]
    public async Task T25_Fall_back_2026_11_01_all_288_inst_load_intervals_pass_including_the_repeated_0100_hour()
    {
        var day = new DateOnly(2026, 11, 1);
        var items = Samples.LoadDay(day);
        var (reader, logs) = LoadReader(FakePageSource.Paged(items, 50000));

        var rows = await reader.ReadDayAsync(Ld(day), CancellationToken.None);

        Assert.Equal(288, rows.Count);
        // EPT 01:00..01:55 appears twice (24 rows), from 24 distinct UTC instants.
        var repeated = rows.Where(r => r.DatetimeBeginningEpt.Date == new DateTime(2026, 11, 1) && r.DatetimeBeginningEpt.Hour == 1).ToList();
        Assert.Equal(24, repeated.Count);
        Assert.Equal(24, repeated.Select(r => r.DatetimeBeginningUtc).Distinct().Count());
        Assert.Equal(12, repeated.Select(r => r.DatetimeBeginningEpt).Distinct().Count());
        Assert.Empty(logs.OfLevel(LogLevel.Warning));
    }

    [Fact]
    public async Task T25_Spring_forward_2026_03_08_passes()
    {
        var day = new DateOnly(2026, 3, 8);
        var (reader, _) = LoadReader(FakePageSource.Paged(Samples.LoadDay(day), 50000));
        var rows = await reader.ReadDayAsync(Ld(day), CancellationToken.None);

        Assert.Equal(288, rows.Count);
        // 06:55Z = 01:55 EST, 07:00Z = 03:00 EDT: no row lands in the skipped 02:xx hour.
        Assert.DoesNotContain(rows, r => r.DatetimeBeginningEpt.Date == new DateTime(2026, 3, 8) && r.DatetimeBeginningEpt.Hour == 2);
        Assert.Equal(new DateTime(2026, 3, 8, 3, 0, 0), rows.Single(r => r.DatetimeBeginningUtc == new DateTime(2026, 3, 8, 7, 0, 0)).DatetimeBeginningEpt);
    }

    [Theory]
    [InlineData(2026, 11, 20, 5, -1)]   // normal day, EPT an hour early
    [InlineData(2026, 11, 20, 5, 1)]    // normal day, EPT an hour late
    [InlineData(2026, 11, 1, 6, 1)]     // fall-back: 06:00Z stamped as 02:00 (would be the EDT reading)
    [InlineData(2026, 11, 1, 5, -1)]    // fall-back: 05:00Z stamped as 00:00
    [InlineData(2026, 3, 8, 7, -1)]     // spring-forward: 07:00Z stamped as 02:00 (a non-existent wall clock)
    [InlineData(2026, 3, 8, 6, 1)]      // spring-forward: 06:00Z stamped as 02:00
    public async Task T25_EPT_off_by_an_hour_fails(int y, int m, int d, int utcHour, int shiftHours)
    {
        var day = new DateOnly(y, m, d);
        var utc = day.ToDateTime(new TimeOnly(utcHour, 0));
        var item = Samples.Fuel(utc, fields: new() { ["datetime_beginning_ept"] = Samples.Iso(Samples.Ept(utc).AddHours(shiftHours)) });
        var (reader, _) = GenReader(Single(item));

        var ex = await Assert.ThrowsAsync<PjmDataException>(() => reader.ReadDayAsync(Gen(day), CancellationToken.None));
        Assert.Contains("datetime_beginning_ept", ex.Message);
    }

    [Fact]
    public async Task T25_EPT_equal_to_UTC_fails()
    {
        var item = Samples.Fuel(H(5), fields: new() { ["datetime_beginning_ept"] = Samples.Iso(H(5)) });
        var (reader, _) = GenReader(Single(item));
        await Assert.ThrowsAsync<PjmDataException>(() => reader.ReadDayAsync(Gen(), CancellationToken.None));
    }

    // ---- 26 is pipeline-level (sink never called): see DuplicateKeyPipelineTests ----

    // ---- 27 -----------------------------------------------------------------------

    [Fact]
    public async Task T27_gen_by_fuel_23_of_24_hours_warns_and_succeeds()
    {
        var (reader, logs) = GenReader(FakePageSource.Paged(Samples.FuelDay(Day, hours: 23), 50000));
        var rows = await reader.ReadDayAsync(Gen(), CancellationToken.None);
        Assert.Equal(23, rows.Count);
        var w = Assert.Single(logs.OfLevel(LogLevel.Warning));
        Assert.Contains("23/24 hours", w.Message);
    }

    [Fact]
    public async Task T27_inst_load_area_with_287_intervals_warns_once_and_succeeds()
    {
        var items = Samples.LoadDay(Day, "AEP", 287)
            .Concat(Samples.LoadDay(Day, "DOM", 286))
            .Concat(Samples.LoadDay(Day, "PJM RTO", 288)).ToList();
        var (reader, logs) = LoadReader(FakePageSource.Paged(items, 50000));
        var rows = await reader.ReadDayAsync(Ld(), CancellationToken.None);

        Assert.Equal(items.Count, rows.Count);
        var w = Assert.Single(logs.OfLevel(LogLevel.Warning));
        Assert.Contains("AEP", w.Message);
        Assert.Contains("DOM", w.Message);
        Assert.DoesNotContain("PJM RTO", w.Message);
    }

    [Fact]
    public async Task T27_No_completeness_warning_for_today()
    {
        var (reader, logs) = GenReader(FakePageSource.Paged(Samples.FuelDay(Day, hours: 3), 50000));
        await reader.ReadDayAsync(Gen(age: 0), CancellationToken.None);
        var (lreader, llogs) = LoadReader(FakePageSource.Paged(Samples.LoadDay(Day, intervals: 10), 50000));
        await lreader.ReadDayAsync(Ld(age: 0), CancellationToken.None);

        Assert.Empty(logs.OfLevel(LogLevel.Warning));
        Assert.Empty(llogs.OfLevel(LogLevel.Warning));
    }

    // ---- 28 / 29: empty-read rule -------------------------------------------------

    private static PjmDayWorkUnit UnitAtAge(PjmFeed feed, int age)
    {
        var units = PjmWorkUnitProvider.Build(feed, new PjmSettings(), Samples.Started);
        return units.Single(u => u.Age == age);
    }

    private static FakePageSource Empty() => new((_, _) => new PjmPage(new List<JsonElement>(), 0));

    [Theory]
    [InlineData(0, true)]
    [InlineData(1, false)]
    [InlineData(10, false)]
    [InlineData(30, false)]
    public async Task T28_gen_by_fuel_empty_read_by_age(int age, bool success)
    {
        var (reader, _) = GenReader(Empty());
        var ex = await Record.ExceptionAsync(() => reader.ReadDayAsync(UnitAtAge(PjmFeeds.GenByFuel(new PjmSettings()), age), CancellationToken.None));
        if (success) Assert.Null(ex);
        else Assert.Contains("0 rows for a past day", Assert.IsType<PjmDataException>(ex).Message);
    }

    [Theory]
    [InlineData(0, true, false)]
    [InlineData(1, false, false)]
    [InlineData(25, false, false)]
    [InlineData(26, true, true)]
    [InlineData(28, true, true)]
    public async Task T29_inst_load_empty_read_by_age(int age, bool success, bool warns)
    {
        var (reader, logs) = LoadReader(Empty());
        var ex = await Record.ExceptionAsync(() => reader.ReadDayAsync(UnitAtAge(PjmFeeds.InstLoad(new PjmSettings()), age), CancellationToken.None));

        if (success) Assert.Null(ex);
        else
        {
            var d = Assert.IsType<PjmDataException>(ex);
            Assert.Contains("0 rows inside retention", d.Message);
            Assert.Contains($"age {age}", d.Message);
            Assert.Contains("25", d.Message);
        }

        Assert.Equal(warns ? 1 : 0, logs.OfLevel(LogLevel.Warning).Count());
    }
}

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ExtraFieldCollection
{
    public const string Name = "PJM extra-field once-flag (process-static)";
}
