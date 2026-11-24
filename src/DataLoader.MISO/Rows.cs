namespace DataLoader.MISO;

// =============================================================================
// Target rows (design §2). Property order documents — and each sink's BuildTable
// mirrors — the TVP column order of sql/MISO/002. Source columns only:
// CreatedAtUtc/ModifiedAtUtc are never TVP columns. *_local is the fixed EST wall
// clock as MISO sends it; *_utc = local + 5h (design §3).
// =============================================================================

/// <summary>The four interval columns every MISO table starts with.</summary>
public interface IMisoIntervalRow
{
    DateTime IntervalStartLocal { get; }
    DateTime IntervalStartUtc { get; }
    DateTime IntervalEndLocal { get; }
    DateTime IntervalEndUtc { get; }
}

/// <summary><c>miso.miso_zonal_load_hourly</c> — PK <c>(interval_start_utc)</c>. TVP <c>miso.MisoZonalLoadHourlyType</c>, 11 columns.</summary>
public sealed class MisoZonalLoadHourlyRow : IMisoIntervalRow
{
    public required DateTime IntervalStartLocal { get; init; }   // 1  DATETIME2(0) NOT NULL
    public required DateTime IntervalStartUtc { get; init; }     // 2  DATETIME2(0) NOT NULL
    public required DateTime IntervalEndLocal { get; init; }     // 3  DATETIME2(0) NOT NULL
    public required DateTime IntervalEndUtc { get; init; }       // 4  DATETIME2(0) NOT NULL
    public decimal? Lrz1 { get; init; }                          // 5  DECIMAL(18,4) NULL
    public decimal? Lrz2_7 { get; init; }                        // 6
    public decimal? Lrz3_5 { get; init; }                        // 7
    public decimal? Lrz4 { get; init; }                          // 8
    public decimal? Lrz6 { get; init; }                          // 9
    public decimal? Lrz8_9_10 { get; init; }                     // 10
    public decimal? Miso { get; init; }                          // 11 decimal sum of the six; NULL if any is NULL
}

/// <summary><c>miso.miso_load_hourly</c> — PK <c>(interval_start_utc)</c>. TVP <c>miso.MisoLoadHourlyType</c>, 8 columns.</summary>
public sealed class MisoLoadHourlyRow : IMisoIntervalRow
{
    public required DateTime IntervalStartLocal { get; init; }   // 1
    public required DateTime IntervalStartUtc { get; init; }     // 2
    public required DateTime IntervalEndLocal { get; init; }     // 3
    public required DateTime IntervalEndUtc { get; init; }       // 4
    public decimal? North { get; init; }                         // 5
    public decimal? Central { get; init; }                       // 6
    public decimal? South { get; init; }                         // 7
    public decimal? Miso { get; init; }                          // 8 decimal sum of the three; NULL if any is NULL
}

/// <summary><c>miso.miso_fuel_mix_hourly</c> — PK <c>(interval_start_utc, region)</c>. TVP <c>miso.MisoFuelMixHourlyType</c>, 14 columns.</summary>
public sealed class MisoFuelMixHourlyRow : IMisoIntervalRow
{
    public required DateTime IntervalStartLocal { get; init; }   // 1
    public required DateTime IntervalStartUtc { get; init; }     // 2
    public required DateTime IntervalEndLocal { get; init; }     // 3
    public required DateTime IntervalEndUtc { get; init; }       // 4
    public required string Region { get; init; }                 // 5 VARCHAR(20) NOT NULL, UPPER
    public decimal? Coal { get; init; }                          // 6
    public decimal? Gas { get; init; }                           // 7
    public decimal? Nuclear { get; init; }                       // 8
    public decimal? Water { get; init; }                         // 9
    public decimal? Wind { get; init; }                          // 10
    public decimal? Solar { get; init; }                         // 11
    public decimal? Other { get; init; }                         // 12
    public decimal? Storage { get; init; }                       // 13
    public decimal? TotalMw { get; init; }                       // 14
}

/// <summary><c>miso.miso_load_forecast_mtlf</c> — PK <c>(init_date, interval_start_utc, local_resource_zone)</c>. TVP <c>miso.MisoLoadForecastMtlfType</c>, 8 columns.</summary>
public sealed class MisoLoadForecastMtlfRow : IMisoIntervalRow
{
    public required DateOnly InitDate { get; init; }             // 1 DATE NOT NULL
    public required DateTime IntervalStartLocal { get; init; }   // 2
    public required DateTime IntervalStartUtc { get; init; }     // 3
    public required DateTime IntervalEndLocal { get; init; }     // 4
    public required DateTime IntervalEndUtc { get; init; }       // 5
    public required string Region { get; init; }                 // 6 VARCHAR(20) NOT NULL, UPPER
    public required string LocalResourceZone { get; init; }      // 7 VARCHAR(10) NOT NULL, canonical Z1..Z10
    public decimal? LoadForecast { get; init; }                  // 8
}
