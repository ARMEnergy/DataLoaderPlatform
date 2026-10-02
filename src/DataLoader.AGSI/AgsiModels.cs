using System.Text.Json;
using System.Text.Json.Serialization;

namespace DataLoader.AGSI;

/// <summary>
/// Endpoint-2 (<c>GET /api?country=&amp;date=</c>) response envelope. Only
/// <c>gas_day</c> and <c>data[]</c> are persisted; <c>last_page</c>/<c>total</c>/
/// <c>dataset</c> drive the no-data / multi-page tolerance (design §4.2) but are
/// not stored. Numeric envelope fields are read with
/// <see cref="JsonNumberHandling.AllowReadingFromString"/> so a string-encoded
/// <c>"1"</c> binds as robustly as a JSON <c>1</c>.
/// </summary>
public sealed class AgsiStorageEnvelope
{
    [JsonPropertyName("last_page")] public int? LastPage { get; set; }
    [JsonPropertyName("total")] public int? Total { get; set; }
    [JsonPropertyName("dataset")] public string? Dataset { get; set; }
    [JsonPropertyName("gas_day")] public string? GasDay { get; set; }
    [JsonPropertyName("data")] public List<AgsiStorageRecord>? Data { get; set; }

    // Body-level failure channel. AGSI answers a missing / invalid / revoked x-key with
    // HTTP **200** and `{"total":0,"dataset":"storage ERROR","error":"access denied",
    // "message":"Invalid or missing API key","data":[]}` — shaped exactly like a
    // legitimate no-data day. Without these two fields the reader cannot tell an
    // outage from an empty day and would mark the whole hot window done (design §4.2).
    [JsonPropertyName("error")] public string? Error { get; set; }
    [JsonPropertyName("message")] public string? Message { get; set; }
}

/// <summary>
/// One <c>data[]</c> element. Every measure arrives as a JSON <b>string</b>, so all
/// measure fields are typed <c>string?</c> and parsed tolerantly by
/// <see cref="AgsiParse"/> (blank/unparseable → NULL). <c>info</c> is dropped.
/// </summary>
public sealed class AgsiStorageRecord
{
    // Name/Code are not PERSISTED — arm.GasStorage links back via EntityId (from the work unit),
    // so the code echo is never stored (design §2/§7.2). `url` was dropped from the model entirely.
    // Code IS load-bearing at the reader boundary though: it is the ONLY thing that says which
    // entity a data[] element describes, and a request does not guarantee a matching answer —
    // `?country=eu` and `?country=ne` both return BOTH aggregates. See AgsiStorageSourceReader.
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("code")] public string? Code { get; set; }
    [JsonPropertyName("updatedAt")] public string? UpdatedAt { get; set; }
    [JsonPropertyName("gasDayStart")] public string? GasDayStart { get; set; }
    [JsonPropertyName("gasDayEnd")] public string? GasDayEnd { get; set; }
    [JsonPropertyName("gasInStorage")] public string? GasInStorage { get; set; }
    [JsonPropertyName("consumption")] public string? Consumption { get; set; }
    [JsonPropertyName("consumptionFull")] public string? ConsumptionFull { get; set; }
    [JsonPropertyName("injection")] public string? Injection { get; set; }
    [JsonPropertyName("withdrawal")] public string? Withdrawal { get; set; }
    [JsonPropertyName("netWithdrawal")] public string? NetWithdrawal { get; set; }
    [JsonPropertyName("workingGasVolume")] public string? WorkingGasVolume { get; set; }
    [JsonPropertyName("injectionCapacity")] public string? InjectionCapacity { get; set; }
    [JsonPropertyName("withdrawalCapacity")] public string? WithdrawalCapacity { get; set; }
    [JsonPropertyName("contractedCapacity")] public string? ContractedCapacity { get; set; }
    [JsonPropertyName("availableCapacity")] public string? AvailableCapacity { get; set; }
    [JsonPropertyName("coveredCapacity")] public string? CoveredCapacity { get; set; }
    [JsonPropertyName("status")] public string? Status { get; set; }
    [JsonPropertyName("trend")] public string? Trend { get; set; }
    [JsonPropertyName("full")] public string? Full { get; set; }

    /// <summary>
    /// True when this record carries no data (status <c>N</c> and every measure blank) — the
    /// <c>status:"N"</c> no-data shape (design §4.2). Such a record is skipped (no fact row).
    /// </summary>
    public bool IsNoData()
    {
        var statusN = string.Equals(Status?.Trim(), "N", StringComparison.OrdinalIgnoreCase);
        if (!statusN) return false;
        return AgsiParse.Decimal(GasInStorage) is null
            && AgsiParse.Decimal(Consumption) is null
            && AgsiParse.Decimal(Injection) is null
            && AgsiParse.Decimal(Withdrawal) is null
            && AgsiParse.Decimal(NetWithdrawal) is null
            && AgsiParse.Decimal(WorkingGasVolume) is null
            && AgsiParse.Decimal(Full) is null;
    }
}

/// <summary>Shared, tolerant <see cref="JsonSerializerOptions"/> for the AGSI envelope.</summary>
internal static class AgsiJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString
    };
}
