using System.Net;
using DataLoader.EnergyAspects.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace DataLoader.EnergyAspects.Tests;

/// <summary>
/// HTTP status matrix for /data/timeseries/.
///
/// The load-bearing distinction, measured live 2026-10-14: a 417 means NOT ONE
/// of the requested dataset ids is licensed — if even one is, the answer is 200
/// and the unlicensed ids are silently omitted. So a 417 on PART of a mapping is
/// a legitimate empty read, while a 417 on a WHOLE mapping means entitlement
/// really has gone and must still fail.
/// </summary>
public class StatusMatrixTests
{
    private static EnergyAspectsSettings Settings() => new()
    {
        ApiKey = new string('k', 36),
        TimeseriesEndpoint = "/data/timeseries/",
        MaxRequestUriLength = 8000
    };

    private static EnergyAspectsWorkUnit Unit(
        int part, int partCount, int ids = 1126, string licensed = "yes") => new()
    {
        Mapping = new DatasetMapping
        {
            MappingId = 515,
            Name = "North America planned refinery outages",
            Category = "oil",
            Licensed = licensed,
            DatasetIds = Enumerable.Range(579252, ids).ToList()
        },
        DatasetIds = Enumerable.Range(579252, ids).ToList(),
        WindowStart = new DateTime(2026, 9, 14),
        WindowEnd = new DateTime(2026, 10, 13),
        PartNumber = part,
        Settled = true,
        PartCount = partCount
    };

    private static (EnergyAspectsApiSource Source, FakeApiLogger Api, ListLogger<EnergyAspectsApiSource> Log)
        Build(HttpStatusCode status, string body = "")
    {
        var handler = StubHttpMessageHandler.Respond(status, body);
        var api = new FakeApiLogger();
        var log = new ListLogger<EnergyAspectsApiSource>();
        var source = new EnergyAspectsApiSource(
            handler.NewClient(), Options.Create(Settings()), api, log);
        return (source, api, log);
    }

    // ---- 417: the whole point of this change ----

    [Fact]
    public async Task PartOfAMapping_417_IsALegitimateEmptyRead()
    {
        var (source, _, _) = Build((HttpStatusCode)417, "{\"detail\":\"User is not licensed to data.\"}");

        var items = await source.ReadAsync(Unit(part: 1, partCount: 2), CancellationToken.None);

        Assert.Empty(items);
    }

    [Fact]
    public async Task PartOfAMapping_417_IsLoggedNamingThePart()
    {
        var (source, _, log) = Build((HttpStatusCode)417, "{\"detail\":\"User is not licensed to data.\"}");

        await source.ReadAsync(Unit(part: 1, partCount: 2), CancellationToken.None);

        var entry = Assert.Single(log.OfLevel(LogLevel.Information));
        Assert.Contains("515", entry.Message);
        Assert.Contains("1/2", entry.Message);
    }

    // ---- the four-way matrix: Licensed is a hint, not a gate ----
    //
    // usp_GetActiveMappings no longer filters on Licensed, so an unlicensed
    // mapping reaching the reader is normal and its 417 is an empty read. The
    // one error case is the vendor contradicting itself: flagged 'yes', serves
    // nothing for the WHOLE mapping.

    [Fact]
    public async Task WholeMapping_FlaggedLicensed_417_Throws()
    {
        var (source, _, _) = Build((HttpStatusCode)417, "{\"detail\":\"User is not licensed to data.\"}");

        var ex = await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => source.ReadAsync(Unit(part: 1, partCount: 1, licensed: "yes"), CancellationToken.None));

        Assert.Contains("515", ex.Message);
        Assert.Contains("entire", ex.Message);
    }

    [Fact]
    public async Task WholeMapping_FlaggedUnlicensed_417_IsAnEmptyRead()
    {
        // The common case once the filter is gone: ~248 of 265 Licensed='no'
        // mappings are single-part, and many are wholly unentitled. Throwing
        // here would fail a hundred units a run.
        var (source, _, _) = Build((HttpStatusCode)417, "{\"detail\":\"User is not licensed to data.\"}");

        var items = await source.ReadAsync(
            Unit(part: 1, partCount: 1, licensed: "no"), CancellationToken.None);

        Assert.Empty(items);
    }

    [Fact]
    public async Task PartOfALicensedMapping_417_DoesNotThrow()
    {
        // Mapping 515 itself: flagged 'yes', 8 of 1,470 ids licensed, so the
        // part holding none of them 417s legitimately.
        var (source, _, _) = Build((HttpStatusCode)417, "{\"detail\":\"User is not licensed to data.\"}");

        Assert.Empty(await source.ReadAsync(
            Unit(part: 1, partCount: 2, licensed: "yes"), CancellationToken.None));
    }

    [Fact]
    public async Task PartOfAnUnlicensedMapping_417_DoesNotThrow()
    {
        var (source, _, _) = Build((HttpStatusCode)417, "{\"detail\":\"User is not licensed to data.\"}");

        Assert.Empty(await source.ReadAsync(
            Unit(part: 3, partCount: 7, licensed: "no"), CancellationToken.None));
    }

    [Fact]
    public async Task UnlicensedMappingStillLoadsTheDatasetsItIsEntitledTo()
    {
        // The whole point of dropping the filter: mapping 13 is flagged 'no'
        // yet serves data, and that data must flow through normally.
        const string body = """
            [{"dataset_id":396,"metadata":{"country":"United States","unit":"kb/d"},
              "data":{"2026-10-13":41.0}}]
            """;
        var (source, _, _) = Build(HttpStatusCode.OK, body);

        var item = Assert.Single(await source.ReadAsync(
            Unit(part: 1, partCount: 1, licensed: "no"), CancellationToken.None));

        Assert.Equal(396, item.DatasetId);
    }

    [Fact]
    public async Task Every417_IsStillRecordedInTheApiResponseLog()
    {
        // Tolerating the status must not make it invisible — ApiResponseLog is
        // how "this slice is unlicensed" stays auditable.
        var (source, api, _) = Build((HttpStatusCode)417, "{\"detail\":\"User is not licensed to data.\"}");

        await source.ReadAsync(Unit(part: 2, partCount: 2), CancellationToken.None);

        Assert.Equal((515, 417), Assert.Single(api.Calls));
    }

    // ---- everything else must keep failing ----

    [Fact]
    public async Task UriTooLong_414_ThrowsWithActionableGuidance()
    {
        var (source, _, _) = Build((HttpStatusCode)414);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => source.ReadAsync(Unit(part: 1, partCount: 7), CancellationToken.None));

        Assert.Contains("MaxRequestUriLength", ex.Message);
        Assert.Contains("414", ex.Message);
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound)]
    public async Task OtherFailureStatuses_StillThrow(HttpStatusCode status)
    {
        var (source, _, _) = Build(status);

        await Assert.ThrowsAsync<HttpRequestException>(
            () => source.ReadAsync(Unit(part: 1, partCount: 2), CancellationToken.None));
    }

    [Fact]
    public async Task Success_DeserialisesItems()
    {
        const string body = """
            [{"dataset_id":581252,"metadata":{"country":"United States","unit":"kb/d"},
              "data":{"2026-10-13":12.5}}]
            """;
        var (source, api, _) = Build(HttpStatusCode.OK, body);

        var items = await source.ReadAsync(Unit(part: 2, partCount: 2), CancellationToken.None);

        var item = Assert.Single(items);
        Assert.Equal(581252, item.DatasetId);
        Assert.Equal(12.5, item.Data["2026-10-13"]);
        Assert.Equal((515, 200), Assert.Single(api.Calls));
    }

    [Fact]
    public async Task EmptyArray_IsAnEmptyReadNotAnError()
    {
        var (source, _, _) = Build(HttpStatusCode.OK, "[]");

        Assert.Empty(await source.ReadAsync(Unit(part: 1, partCount: 2), CancellationToken.None));
    }

    // ---- the request itself ----

    [Fact]
    public async Task RequestAsksOnlyForThisUnitsChunk()
    {
        var handler = StubHttpMessageHandler.Respond(HttpStatusCode.OK, "[]");
        var source = new EnergyAspectsApiSource(
            handler.NewClient(), Options.Create(Settings()), new FakeApiLogger(),
            new ListLogger<EnergyAspectsApiSource>());

        var unit = Unit(part: 1, partCount: 2, ids: 3);
        await source.ReadAsync(unit, CancellationToken.None);

        var uri = Assert.Single(handler.Requests).ToString();
        Assert.Contains("dataset_id=579252,579253,579254", uri);
        Assert.Contains("date_from=2026-09-14", uri);
        Assert.Contains("date_to=2026-10-13", uri);
    }
}
