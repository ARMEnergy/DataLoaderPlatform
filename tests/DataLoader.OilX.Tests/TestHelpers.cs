using System.Net;
using System.Text;
using DataLoader.Core.Abstractions;

namespace DataLoader.OilX.Tests;

/// <summary>Shared fixtures. Nothing here opens a socket.</summary>
internal static class TestHelpers
{
    public static OilXSettings Settings(Action<OilXSettings>? configure = null)
    {
        var settings = new OilXSettings
        {
            ConnectionString = "Server=(local);Database=OilX;Integrated Security=SSPI;",
            ApiKey = "test-key",
            BatchSize = 2,
            MaxRowErrorsLogged = 10
        };

        configure?.Invoke(settings);
        return settings;
    }

    public static LoaderRunContext Context(DateTime? startedAtUtc = null, Guid? runId = null) => new()
    {
        RunId = runId ?? Guid.Parse("11111111-2222-3333-4444-555555555555"),
        StartedAtUtc = startedAtUtc ?? new DateTime(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc),
        CancellationToken = CancellationToken.None
    };

    public static OilXFeedDescriptor Feed(string feedId) =>
        OilXDescriptors.Find(feedId) ?? throw new ArgumentOutOfRangeException(nameof(feedId), feedId, "Unknown feed.");

    /// <summary>Index of a descriptor column by name — tests read row values positionally.</summary>
    public static int Ordinal(this OilXFeedDescriptor feed, string columnName)
    {
        for (var i = 0; i < feed.Columns.Count; i++)
            if (feed.Columns[i].Name.Equals(columnName, StringComparison.OrdinalIgnoreCase))
                return i;

        throw new ArgumentOutOfRangeException(nameof(columnName), columnName, $"Not a column of {feed.FeedId}.");
    }

    public static object Value(this OilXRow row, OilXFeedDescriptor feed, string columnName) =>
        row.Values[feed.Ordinal(columnName)];

    /// <summary>
    /// Drive <see cref="OilXSourceReader.MergeRecordsAsync"/> over a literal CSV document
    /// and collect every row it produced, in batch order.
    /// </summary>
    public static async Task<(List<OilXRow> Rows, List<int> BatchSizes, OilXFileResult Result)> ReadAsync(
        OilXFeedDescriptor feed, string csv, string fileName = "Sample.2026-09-30T03-39.csv",
        OilXSettings? settings = null)
    {
        var reader = new OilXSourceReader(
            feed, new HttpClient(), settings ?? Settings(), Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);

        var rows = new List<OilXRow>();
        var batchSizes = new List<int>();

        using var text = new StringReader(csv);

        var result = await reader.MergeRecordsAsync(
            OilXCsv.ReadRecords(text),
            fileName,
            (batch, _) =>
            {
                batchSizes.Add(batch.Count);
                rows.AddRange(batch);
                return Task.FromResult(batch.Count);
            },
            CancellationToken.None);

        return (rows, batchSizes, result);
    }
}

/// <summary>
/// An <see cref="HttpMessageHandler"/> that answers from a queue of canned responses,
/// so the manifest's status matrix can be exercised without a network.
/// </summary>
internal sealed class StubHandler : HttpMessageHandler
{
    private readonly Queue<HttpResponseMessage> _responses = new();

    public List<string> RequestedUris { get; } = new();

    public StubHandler Enqueue(HttpStatusCode status, string body)
    {
        _responses.Enqueue(new HttpResponseMessage(status)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        });
        return this;
    }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        RequestedUris.Add(request.RequestUri!.ToString());

        if (_responses.Count == 0)
            throw new InvalidOperationException("StubHandler ran out of canned responses.");

        return Task.FromResult(_responses.Dequeue());
    }
}

/// <summary>
/// Content captured VERBATIM from the live OilX files published for <b>2026-09-30</b>
/// and from the live manifest endpoint on <b>2026-10-01</b>.
///
/// <para>
/// Headers, casing, value scales, the parenthesised <c>LoadQuantity(KT)</c> spelling,
/// the lower-case <c>GroupbyDateIndicator</c>, the <c>-1</c>-for-absent STS IMO and the
/// quoted country name containing a comma are all the vendor's own, not invented.
/// Using real payloads is what makes these tests evidence that the mapping matches
/// production rather than evidence that it is self-consistent.
/// </para>
/// </summary>
internal static class Samples
{
    // ---------------------------------------------------------------- CargoTracking

    /// <summary>
    /// The real 52-column CargoTracking header. Note it carries 20 columns the target
    /// table does not use, and publishes KT BEFORE KBBL — the reverse of the table.
    /// </summary>
    public const string CargoTrackingHeader =
        "IMO,VesselName,VesselClass,LoadDate,LoadArea,LoadCountry,LoadSubcountryArea,LoadPort," +
        "LoadSTSIndicator,LoadSTSIMO,LoadQuantity(KT),LoadQuantity(KBBL),OriginCountry," +
        "OriginCountryGroup,GradeName,APIGravity,SulphurContent,DischargeDate,DischargeArea," +
        "DischargeCountry,DischargeSubcountryArea,DischargePort,DischargeSTSIndicator," +
        "DischargeSTSIMO,DestinationCountry,DestinationCountryGroup,Charterer,Supplier,Buyer," +
        "LastUpdateDate,LoadGeoAsset,DischargeGeoAsset,GradeSource,CargoType,CargoTypeSource," +
        "SanctionEntities,LoadSTSID,DischargeSTSID,LoadGeoAssetSource,LoadPortSource," +
        "LoadCountrySource,LoadAreaSource,DischargeGeoAssetSource,DischargePortSource," +
        "DischargeCountrySource,DischargeAreaSource,VoyageID,FlowID,RunDate,RunDateTime," +
        "IsExporter,IsImporter";

    /// <summary>Row 2 of <c>CargoTracking.2026-09-30T03-39.csv</c>, unquoted.</summary>
    public const string CargoTrackingRow1 =
        "9405540,Zevulun,MR2,2015-12-11 01:10:30.714,Central Mediterranean,Albania,,Vlore,0,-1," +
        "30.86,195,Albania,,Albania Crude Oil,11.0,5.40,2015-12-13 11:58:43.000," +
        "Central Mediterranean,Italy,,Augusta,1,9341079,Bahamas,,,,,2026-05-20,," +
        "Sicily Lightering Zone,Estimation,Crude Oil,Market Info,,,49CF7760C7EEA412FB3E9FEFA83," +
        "Realized,Realized,Realized,Realized,Realized,Realized,Realized,Realized,63," +
        "1c5f5b6538f59493841f0af4105c4ecb1a53be11bcd55ddacc9f575074b5fc5a,2026-09-30," +
        "2026-09-30 03:00:01.237916,True,False";

    /// <summary>
    /// A real row carrying <c>"Bonaire, Sint Eustatius and Saba"</c> — a QUOTED field
    /// with an embedded comma, in two different columns. 3,447 of the file's 396,866
    /// rows look like this; splitting on ',' shifts every later column one place left.
    /// </summary>
    public const string CargoTrackingQuotedRow =
        "9913511,Lahore,Aframax,2025-10-13 06:37:11.000,West Mediterranean,Algeria,,Skikda,0,-1," +
        "84.45,681,Algeria,OPEC,Algeria Crude Oil,49.8,0.08,2025-11-02 07:59:01.000,Caribs," +
        "\"Bonaire, Sint Eustatius and Saba\",,St Eustatius,0,-1," +
        "\"Bonaire, Sint Eustatius and Saba\",,Lukoil,Sonatrach,,2026-05-20," +
        "Skikda Refinery (RA1.K),St. Eustatius Terminal (Statia),Estimation,Crude Oil," +
        "Market Info,,,,Realized,Realized,Realized,Realized,Realized,Realized,Realized,Realized," +
        "37,ab8122f46016ce3c1dfb857aade40e0f7efcedd60e2d597e85023e0a36ee96f8,2026-09-30," +
        "2026-09-30 03:00:01.237916,True,True";

    public static string CargoTrackingCsv =>
        string.Join("\r\n", CargoTrackingHeader, CargoTrackingRow1, CargoTrackingQuotedRow) + "\r\n";

    // -------------------------------------------------------------- FloatingStorage

    public const string FloatingStorageHeader =
        "IMO,VesselName,VesselClass,ReferenceDate,StartDate,EndDate,QuantityKiloBarrels,Area," +
        "RunDate,OriginCountry,QuantityKiloTonnes,RunDateTime";

    public static string FloatingStorageCsv =>
        string.Join("\r\n",
            FloatingStorageHeader,
            "9387578,Nushen,VLCC,2015-01-01,2015-01-28 03:56:20,2015-03-31 01:50:36,1945," +
            "Singapore / Malaysia,2026-09-30,Nigeria,261.56,2026-09-30 20:33:45.887052",
            "9042427,Mtc Ledang,MR2,2015-01-01,2014-06-02 02:13:00,2016-05-03 19:16:00,293," +
            "South East Asia,2026-09-30,Unknown,40.14,2026-09-30 20:33:45.887052") + "\r\n";

    // -------------------------------------------------------------------------- Flow

    /// <summary>⚠ <c>GroupbyDateIndicator</c> — the vendor spells it with a lower-case b.</summary>
    public const string FlowHeader =
        "OriginCountryName,DestinationCountryName,GroupbyDateIndicator,OriginSubCountry," +
        "DestinationSubCountry,ReferenceDate,GradeName,QuantityKBD,QuantityKBBL,QuantityKT," +
        "ApiGravity,SulphurContent,GradeCategory,RunDate,RunDateTime";

    /// <summary>
    /// The same origin/destination/grade/date under both <c>Exports</c> and
    /// <c>Imports</c> — which is why GroupByDateIndicator is a key component.
    /// </summary>
    public static string FlowCsv =>
        string.Join("\r\n",
            FlowHeader,
            "Albania,Bahamas,Exports,,,2015-12-01,Albania Crude Oil,6.29,195,30.86,11.0,5.40," +
            "Heavy & Sour,2026-09-30,2026-09-30 21:00:08.596625",
            "Albania,Bahamas,Imports,,,2016-01-01,Albania Crude Oil,6.29,195,30.86,11.0,5.40," +
            "Heavy & Sour,2026-09-30,2026-09-30 21:00:08.596625") + "\r\n";

    // ----------------------------------------------------------------- the balances

    public const string GlobalBalanceHeader =
        "GroupName,ReferenceDate,FlowBreakdown,UnitMeasure,ObservedValue,RunDate";

    public static string GlobalBalanceCsv =>
        string.Join("\r\n",
            GlobalBalanceHeader,
            "OPEC,2010-01-01,Crude Production,KBD,24534.0,2026-09-30",
            "OPEC,2010-02-01,Crude Production,KBD,24700.0,2026-09-30") + "\r\n";

    public const string RegionalBalanceHeader =
        "GroupName,ReferenceDate,FlowBreakdown,UnitMeasure,ObservedValue,GeneralizedSource,RunDate";

    public static string RegionalBalanceCsv =>
        string.Join("\r\n",
            RegionalBalanceHeader,
            "Africa,2010-01-01,CLOSTLV,KBBL,130664.0,OilX,2026-09-30",
            "Africa,2010-02-01,CLOSTLV,KBBL,122865.0,OilX,2026-09-30") + "\r\n";

    public const string SupplyDemandHeader =
        "CountryISOCode,CountryName,ReferenceDate,FlowBreakdown,UnitMeasure,ObservedValue," +
        "GeneralizedSource,RunDate";

    public static string SupplyDemandCsv =>
        string.Join("\r\n",
            SupplyDemandHeader,
            "AF,Afghanistan,2010-01-01,CLOSTLV,KBBL,0.0,OilX,2026-09-30",
            "AF,Afghanistan,2010-02-01,CLOSTLV,KBBL,0.0,OilX,2026-09-30") + "\r\n";

    public const string OilFieldProductionHeader =
        "OilFieldName,PortName,CountryName,ReferenceDate,UnitMeasure,ObservedValue,RunDate";

    public static string OilFieldProductionCsv =>
        string.Join("\r\n",
            OilFieldProductionHeader,
            "Abana,FPSO Massongo,Cameroon,2019-01-01,Events,3,2026-09-30",
            "Abana,FPSO Massongo,Cameroon,2019-01-01,MWatts,0,2026-09-30") + "\r\n";

    /// <summary>⚠ Carries <c>IsForwardFilled</c>, which the target table does not use.</summary>
    public const string TerminalHeader =
        "TerminalName,ReferenceDate,FlowBreakdown,UnitMeasure,ObservedValue,RunDate,CountryName," +
        "IsForwardFilled";

    public static string TerminalCsv =>
        string.Join("\r\n",
            TerminalHeader,
            "Ain Sukhna,2017-01-01,Total Stocks,KBBL,11916.0,2026-09-30,Egypt,False",
            "Ain Sukhna,2017-01-01,Capacity,KBBL,18541.0,2026-09-30,Egypt,False") + "\r\n";

    // -------------------------------------------------------------------- manifests

    /// <summary>
    /// A real two-file manifest, with the URLs' query strings shortened. Note the
    /// entries are given NEWEST FIRST so a test can prove the client re-orders them.
    /// </summary>
    public const string ManifestTwoFiles = """
        {"data":[
          {"uploaded_at":"2026-09-30T09:23:54.905000+00:00",
           "url":"https://oilx-csvs.s3.amazonaws.com/CargoTracking.2026-09-30T09-23.csv?AWSAccessKeyId=ASIA&Signature=abc%3D&x-amz-security-token=TOKEN"},
          {"uploaded_at":"2026-09-30T03:40:23.462000+00:00",
           "url":"https://oilx-csvs.s3.amazonaws.com/CargoTracking.2026-09-30T03-39.csv?AWSAccessKeyId=ASIA&Signature=def%3D&x-amz-security-token=TOKEN"}
        ],"success":true}
        """;

    /// <summary>HTTP 422 — the vendor's answer for a day it published nothing on.</summary>
    public const string ErrorNoData =
        """{"error":"No data for files 'globalbalance'","success":false}""";

    /// <summary>HTTP 422 — the vendor's answer for a feed name it does not recognise.</summary>
    public const string ErrorUnavailable =
        """{"error":"Unavailable files 'notarealfeed'","success":false}""";

    /// <summary>HTTP 401 — a bad or unresolved API key.</summary>
    public const string ErrorBadKey =
        """{"error":"Authorization key is invalid","success":false}""";
}
