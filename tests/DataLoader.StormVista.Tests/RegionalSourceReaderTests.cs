using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DataLoader.StormVista.Tests;

/// <summary>
/// <see cref="RegionalSourceReader"/> — wide→long unpivot, header validation against the
/// seeded region set (reg3 and regiso): fail on unknown/extra or duplicate columns, but
/// TOLERATE missing seeded regions (region sets grow over time, so historical files carry a
/// subset). Also tolerant per-cell parse and the shared load flow. HTTP + FileLog + reference
/// are all in-memory fakes.
/// </summary>
public class RegionalSourceReaderTests
{
    private static StormVistaSettings Settings() => new()
    {
        BaseUrl = "https://api.stormvistawxmodels.com/v1",
        ApiKey = "SECRET-KEY-123"
    };

    // reg3 = { West, East, Producing }. regiso is seeded with the LATEST/full 21-region ISO
    // set (StormVista grew it from 18 → 21 by adding southwest/caisonorth/caisosouth), so a
    // historical 18-region file is a legitimate subset the reader must tolerate.
    private static readonly string[] IsoRegions21 =
    {
        "bpa", "miso", "nyiso", "west-pjm", "ercot", "spp", "caiso", "aeso", "ieso",
        "upmiso", "nepool", "wecc", "serc", "pjm", "east-pjm", "tva", "quebec", "lowmiso",
        "southwest", "caisonorth", "caisosouth"
    };

    private static StormVistaReference Reference() => new()
    {
        Models = new List<ModelRef> { new("ecmwf-weekly", false, true, false) },
        Cycles = new List<CycleRef> { new("00", true, true) },
        WddTypes = new List<string> { "ew_cdd", "gw_hdd", "pw_cdd", "pw_hdd" },
        RegionSets = new List<RegionSetRef> { new("3", "EIA"), new("iso", "ISO") },
        RegionSetTypes = new Dictionary<string, IReadOnlyList<string>>
        {
            ["3"] = new[] { "ew_cdd", "gw_hdd", "pw_cdd" },
            ["iso"] = new[] { "pw_cdd", "pw_hdd" }
        },
        Regions = new Dictionary<string, IReadOnlyList<string>>
        {
            ["3"] = new[] { "West", "East", "Producing" },
            ["iso"] = IsoRegions21
        }
    };

    private static RegionalWorkUnit Unit(string regionSetCode, string type = "ew_cdd") => new()
    {
        WkModel = "ecmwf-weekly",
        InitDate = new DateOnly(2024, 8, 4),
        Cycle = "00",
        WddType = type,
        RegionSetCode = regionSetCode,
        KeyValue = $"sv:regional:ecmwf-weekly:20240804:00:{type}:reg{regionSetCode}"
    };

    private static RegionalSourceReader NewReader(StubHttpMessageHandler handler, FakeStormVistaFileLog fileLog) =>
        new(handler.NewClient(), Settings(), fileLog, new FakeReferenceProvider(Reference()),
            NullLogger<RegionalSourceReader>.Instance);

    [Fact]
    public async Task Read_Reg3_UnpivotsWideToLong_OneRowPerRegionColumn()
    {
        var csv =
            "Date,West,East,Producing\n" +
            "2024-08-04,12.96,12.81,18.92\n" +
            "2024-08-05,13.10,12.90,19.00\n";

        var handler = StubHttpMessageHandler.Respond(HttpStatusCode.OK, csv);
        var fileLog = new FakeStormVistaFileLog { FileLogIdToReturn = 55 };
        var reader = NewReader(handler, fileLog);

        var rows = await reader.ReadAsync(Unit("3"), CancellationToken.None);

        Assert.Equal(6, rows.Count); // 2 dates x 3 regions
        Assert.All(rows, r => Assert.Equal(55, r.FileLogId)); // FileLogId stamped on every leaf row

        var day1 = rows.Where(r => r.ValidDate == new DateOnly(2024, 8, 4)).ToList();
        Assert.Equal(12.96m, day1.Single(r => r.RegionName == "West").Value);
        Assert.Equal(12.81m, day1.Single(r => r.RegionName == "East").Value);
        Assert.Equal(18.92m, day1.Single(r => r.RegionName == "Producing").Value);

        var call = Assert.Single(fileLog.Calls);
        Assert.Equal("Success", call.Status);
        Assert.Equal(6, call.RowCount);
        Assert.Equal("Regional", call.File.EndpointName);
        Assert.Equal("3", call.File.RegionSetCode);
    }

    [Fact]
    public async Task Read_RegIso_UnpivotsUsingSeededIsoCodes()
    {
        var csv =
            "Date,bpa,miso,nyiso\n" +
            "2024-08-04,5.1,6.2,7.3\n";

        var handler = StubHttpMessageHandler.Respond(HttpStatusCode.OK, csv);
        var reader = NewReader(handler, new FakeStormVistaFileLog());

        var rows = await reader.ReadAsync(Unit("iso", "pw_cdd"), CancellationToken.None);

        Assert.Equal(3, rows.Count);
        Assert.Equal(5.1m, rows.Single(r => r.RegionName == "bpa").Value);
        Assert.Equal(6.2m, rows.Single(r => r.RegionName == "miso").Value);
        Assert.Equal(7.3m, rows.Single(r => r.RegionName == "nyiso").Value);
    }

    [Fact]
    public async Task Read_ReorderedColumns_StillMapByHeaderName()
    {
        // Same set, different column order. The unit must NOT fail (set matches);
        // values must map by header NAME, not position.
        var csv =
            "Date,East,Producing,West\n" +
            "2024-08-04,12.81,18.92,12.96\n";

        var handler = StubHttpMessageHandler.Respond(HttpStatusCode.OK, csv);
        var reader = NewReader(handler, new FakeStormVistaFileLog());

        var rows = await reader.ReadAsync(Unit("3"), CancellationToken.None);

        Assert.Equal(12.96m, rows.Single(r => r.RegionName == "West").Value);
        Assert.Equal(12.81m, rows.Single(r => r.RegionName == "East").Value);
        Assert.Equal(18.92m, rows.Single(r => r.RegionName == "Producing").Value);
    }

    [Fact]
    public async Task Read_PerCellTolerantSkip_DropsBlankOrNaNCellsButKeepsOthers()
    {
        var csv =
            "Date,West,East,Producing\n" +
            "2024-08-04,12.96,,18.92\n" +     // East blank -> skip that cell only
            "2024-08-05,NaN,12.90,19.00\n";   // West NaN -> skip that cell only

        var handler = StubHttpMessageHandler.Respond(HttpStatusCode.OK, csv);
        var fileLog = new FakeStormVistaFileLog();
        var reader = NewReader(handler, fileLog);

        var rows = await reader.ReadAsync(Unit("3"), CancellationToken.None);

        Assert.Equal(4, rows.Count); // 6 cells minus 2 skipped
        Assert.DoesNotContain(rows, r => r.ValidDate == new DateOnly(2024, 8, 4) && r.RegionName == "East");
        Assert.DoesNotContain(rows, r => r.ValidDate == new DateOnly(2024, 8, 5) && r.RegionName == "West");
        Assert.Equal(4, Assert.Single(fileLog.Calls).RowCount);
    }

    [Fact]
    public async Task Read_BadRowDate_SkipsWholeRow()
    {
        var csv =
            "Date,West,East,Producing\n" +
            "bad-date,1,2,3\n" +           // whole row skipped
            "2024-08-05,4,5,6\n";

        var handler = StubHttpMessageHandler.Respond(HttpStatusCode.OK, csv);
        var reader = NewReader(handler, new FakeStormVistaFileLog());

        var rows = await reader.ReadAsync(Unit("3"), CancellationToken.None);

        Assert.Equal(3, rows.Count); // only the good row's 3 cells
        Assert.All(rows, r => Assert.Equal(new DateOnly(2024, 8, 5), r.ValidDate));
    }

    [Fact]
    public async Task Read_HeaderOnly_YieldsZeroRows_ButUpsertsSuccess()
    {
        var handler = StubHttpMessageHandler.Respond(HttpStatusCode.OK, "Date,West,East,Producing\n");
        var fileLog = new FakeStormVistaFileLog();
        var reader = NewReader(handler, fileLog);

        var rows = await reader.ReadAsync(Unit("3"), CancellationToken.None);

        Assert.Empty(rows);
        var call = Assert.Single(fileLog.Calls);
        Assert.Equal("Success", call.Status);
        Assert.Equal(0, call.RowCount);
    }

    // ---- header validation: unknown/extra + duplicate fail loudly; MISSING is tolerated ----

    [Fact]
    public async Task Read_SubsetOfSeededRegions_ParsesPresentColumnsOnly()
    {
        // Region sets grow over time: the seeded ISO set has 21 regions, but this historical
        // file predates southwest/caisonorth/caisosouth and carries only the original 18.
        // A subset is NOT drift — parse the present columns; emit no rows for the 3 absent ones.
        var csv =
            "Date,bpa,miso,nyiso,west-pjm,ercot,spp,caiso,aeso,ieso,upmiso,nepool,wecc,serc,pjm,east-pjm,tva,quebec,lowmiso\n" +
            "2024-08-04,1,2,3,4,5,6,7,8,9,10,11,12,13,14,15,16,17,18\n";

        var handler = StubHttpMessageHandler.Respond(HttpStatusCode.OK, csv);
        var fileLog = new FakeStormVistaFileLog();
        var reader = NewReader(handler, fileLog);

        var rows = await reader.ReadAsync(Unit("iso", "pw_cdd"), CancellationToken.None);

        Assert.Equal(18, rows.Count); // 1 date x 18 present regions (of 21 seeded)
        Assert.Equal(1m, rows.Single(r => r.RegionName == "bpa").Value);
        Assert.Equal(18m, rows.Single(r => r.RegionName == "lowmiso").Value);
        // No rows for the 3 seeded-but-absent regions the file predates.
        Assert.DoesNotContain(rows, r => r.RegionName == "southwest");
        Assert.DoesNotContain(rows, r => r.RegionName == "caisonorth");
        Assert.DoesNotContain(rows, r => r.RegionName == "caisosouth");

        var call = Assert.Single(fileLog.Calls);
        Assert.Equal("Success", call.Status);
        Assert.Equal(18, call.RowCount);
    }

    [Fact]
    public async Task Read_ExtraRegionColumn_ThrowsFormatException()
    {
        var csv = "Date,West,East,Producing,Surprise\n2024-08-04,1,2,3,4\n";

        var handler = StubHttpMessageHandler.Respond(HttpStatusCode.OK, csv);
        var reader = NewReader(handler, new FakeStormVistaFileLog());

        var ex = await Assert.ThrowsAsync<FormatException>(() => reader.ReadAsync(Unit("3"), CancellationToken.None));
        Assert.Contains("Surprise", ex.Message);
    }

    [Fact]
    public async Task Read_DuplicateRegionColumn_ThrowsFormatException()
    {
        var csv = "Date,West,West,Producing\n2024-08-04,1,2,3\n";

        var handler = StubHttpMessageHandler.Respond(HttpStatusCode.OK, csv);
        var reader = NewReader(handler, new FakeStormVistaFileLog());

        var ex = await Assert.ThrowsAsync<FormatException>(() => reader.ReadAsync(Unit("3"), CancellationToken.None));
        Assert.Contains("duplicate", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Read_WrongFirstColumnHeader_ThrowsFormatException()
    {
        var csv = "NotDate,West,East,Producing\n2024-08-04,1,2,3\n";

        var handler = StubHttpMessageHandler.Respond(HttpStatusCode.OK, csv);
        var reader = NewReader(handler, new FakeStormVistaFileLog());

        await Assert.ThrowsAsync<FormatException>(() => reader.ReadAsync(Unit("3"), CancellationToken.None));
    }
}
