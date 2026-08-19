namespace DataLoader.AGSI;

// =============================================================================
// Target rows — the source readers build these directly (the identity transform
// passes them straight to the sink). Each row carries its FileLogId (stamped by
// the reader after the arm.FileLog upsert) plus its own natural/data columns.
// Property order documents (and each sink's BuildTable mirrors) the TVP column
// contract in sql/AGSI/002 — CRITICAL: FileLogId is the LAST TVP column on the
// entity table and the FIRST on the storage table (the two differ deliberately).
// =============================================================================

/// <summary>A fact/dimension row whose parent <c>arm.FileLog</c> id is stamped by the source reader.</summary>
public interface IAgsiFactRow
{
    int FileLogId { get; set; }
}

/// <summary>
/// arm.GasStorageEntity (endpoint 1 <c>/api/about</c>) — country dimension, natural
/// key <c>Code</c>. Deduped to one tuple per <c>Code</c> before the MERGE (many SSO
/// entities collapse to one country). <c>FileLogId</c> is the LAST TVP column here
/// (design §7.1 / sql/AGSI/002).
/// </summary>
public sealed class GasStorageEntityRow : IAgsiFactRow
{
    public int FileLogId { get; set; }
    public required string Code { get; init; }        // data.country.code (endpoint-2 query key)
    public required string Name { get; init; }        // data.country.name
    public required string ParentCode { get; init; }  // data.code (region group; 'EU' observed)
    public required string ParentName { get; init; }  // data.name (region group; 'Europe' observed)
}

/// <summary>
/// arm.GasStorage (endpoint 2 <c>/api?country=&amp;date=</c>) — daily fact, natural
/// key <c>(EntityId, GasDayStart)</c> (normalized: <c>Name</c>/<c>Code</c>/<c>Url</c>
/// were dropped in favour of <c>EntityId</c> → <c>arm.GasStorageEntity(Id)</c> —
/// design §2/§7.2). Every measure is nullable (status 'E'/'N' rows may blank them).
/// <c>FileLogId</c> is the FIRST TVP column, <c>EntityId</c> the SECOND
/// (design §7.2 / sql/AGSI/002).
/// </summary>
public sealed class GasStorageRow : IAgsiFactRow
{
    public int FileLogId { get; set; }
    public required int EntityId { get; init; }           // FK → arm.GasStorageEntity(Id); part of the key
    public required DateOnly Date { get; init; }          // request gas-day param (audit; ≡ GasDayStart single-date mode)
    public required DateOnly GasDay { get; init; }        // top-level gas_day (latest-available marker; NOT key)
    public DateTime? UpdatedAt { get; init; }             // data.updatedAt (GIE CET/CEST wall-clock)
    public required DateOnly GasDayStart { get; init; }   // data.gasDayStart (= Date single-date mode)
    public required DateOnly GasDayEnd { get; init; }     // data.gasDayEnd (= gasDayStart + 1)
    public decimal? GasInStorage { get; init; }
    public decimal? Consumption { get; init; }
    public decimal? ConsumptionFull { get; init; }
    public decimal? Injection { get; init; }
    public decimal? Withdrawal { get; init; }
    public decimal? NetWithdrawal { get; init; }          // signed
    public decimal? WorkingGasVolume { get; init; }
    public decimal? InjectionCapacity { get; init; }
    public decimal? WithdrawalCapacity { get; init; }
    public decimal? ContractedCapacity { get; init; }
    public decimal? AvailableCapacity { get; init; }
    public decimal? CoveredCapacity { get; init; }
    public required string Status { get; init; }          // 'C'/'E'/'N'
    public decimal? Trend { get; init; }                  // signed
    public decimal? Full { get; init; }                   // fill %; can slightly exceed 100
}
