using F = DataLoader.PJM.PjmFeeds;

namespace DataLoader.PJM;

/// <summary>
/// Per-feed field mapping (design §4.3, §14.2). Required/extra-field checks happen before this is
/// called; timestamps are normalised and checked through <see cref="PjmStamps"/>, so the row is
/// built once, already as it will be keyed and written.
/// </summary>
public interface IPjmRowMapper<TRow> where TRow : IPjmRow
{
    TRow Map(PjmItem item, PjmMapContext ctx);
}

/// <summary><c>gen_by_fuel</c> → <see cref="PjmFuelMixRow"/>.</summary>
public sealed class PjmFuelMixRowMapper : IPjmRowMapper<PjmFuelMixRow>
{
    public PjmFuelMixRow Map(PjmItem item, PjmMapContext ctx)
    {
        var s = PjmStamps.ReadAll(item, ctx);
        var (utc, ept) = s[F.DatetimeBeginningUtc];
        return new()
        {
            DatetimeBeginningUtc = utc,
            DatetimeBeginningEpt = ept,
            FuelType = PjmValueParser.Member(item, F.FuelType),
            Mw = PjmValueParser.Decimal(item, F.Mw, 18, 4),
            FuelPercentageOfTotal = PjmValueParser.Decimal(item, F.FuelPercentageOfTotal, 9, 6), // stored as received, never rescaled
            IsRenewable = PjmValueParser.Boolean(item, F.IsRenewable),
        };
    }
}

/// <summary><c>inst_load</c> → <see cref="PjmLoadRow"/>.</summary>
public sealed class PjmLoadRowMapper : IPjmRowMapper<PjmLoadRow>
{
    public PjmLoadRow Map(PjmItem item, PjmMapContext ctx)
    {
        var s = PjmStamps.ReadAll(item, ctx);
        var (utc, ept) = s[F.DatetimeBeginningUtc];
        return new()
        {
            DatetimeBeginningUtc = utc,
            DatetimeBeginningEpt = ept,
            Area = PjmValueParser.Member(item, F.Area),
            InstantaneousLoad = PjmValueParser.Decimal(item, F.InstantaneousLoad, 18, 4),
        };
    }
}

/// <summary><c>hrl_load_metered</c> → <see cref="PjmLoadMeteredRow"/>.</summary>
public sealed class PjmLoadMeteredRowMapper : IPjmRowMapper<PjmLoadMeteredRow>
{
    public PjmLoadMeteredRow Map(PjmItem item, PjmMapContext ctx)
    {
        var s = PjmStamps.ReadAll(item, ctx);
        var (utc, ept) = s[F.DatetimeBeginningUtc];
        return new()
        {
            DatetimeBeginningUtc = utc,
            DatetimeBeginningEpt = ept,
            NercRegion = PjmValueParser.NullableString(item, F.NercRegion),
            MktRegion = PjmValueParser.NullableString(item, F.MktRegion),
            Zone = PjmValueParser.NullableString(item, F.Zone),
            LoadArea = PjmValueParser.Member(item, F.LoadArea),
            Mw = PjmValueParser.Decimal(item, F.Mw, 18, 4),
            IsVerified = PjmValueParser.Boolean(item, F.IsVerified),
        };
    }
}

/// <summary><c>hrl_load_prelim</c> → <see cref="PjmLoadPrelimRow"/>.</summary>
public sealed class PjmLoadPrelimRowMapper : IPjmRowMapper<PjmLoadPrelimRow>
{
    public PjmLoadPrelimRow Map(PjmItem item, PjmMapContext ctx)
    {
        var s = PjmStamps.ReadAll(item, ctx);
        var (bu, be) = s[F.DatetimeBeginningUtc];
        var (eu, ee) = s[F.DatetimeEndingUtc];
        return new()
        {
            DatetimeBeginningUtc = bu,
            DatetimeBeginningEpt = be,
            DatetimeEndingUtc = eu,
            DatetimeEndingEpt = ee,
            LoadArea = PjmValueParser.Member(item, F.LoadArea),
            PrelimLoadAvgHourly = PjmValueParser.Decimal(item, F.PrelimLoadAvgHourly, 18, 4),
        };
    }
}

/// <summary><c>hrl_load_estimated</c> → <see cref="PjmLoadEstimatedRow"/>.</summary>
public sealed class PjmLoadEstimatedRowMapper : IPjmRowMapper<PjmLoadEstimatedRow>
{
    public PjmLoadEstimatedRow Map(PjmItem item, PjmMapContext ctx)
    {
        var s = PjmStamps.ReadAll(item, ctx);
        var (bu, be) = s[F.DatetimeBeginningUtc];
        var (eu, ee) = s[F.DatetimeEndingUtc];
        return new()
        {
            DatetimeBeginningUtc = bu,
            DatetimeBeginningEpt = be,
            DatetimeEndingUtc = eu,
            DatetimeEndingEpt = ee,
            LoadArea = PjmValueParser.Member(item, F.LoadArea),
            EstimatedLoadHourly = PjmValueParser.Decimal(item, F.EstimatedLoadHourly, 18, 4),
        };
    }
}

/// <summary><c>load_frcstd_hist</c> → <see cref="PjmLoadForecastHistRow"/>.</summary>
public sealed class PjmLoadForecastHistRowMapper : IPjmRowMapper<PjmLoadForecastHistRow>
{
    public PjmLoadForecastHistRow Map(PjmItem item, PjmMapContext ctx)
    {
        var s = PjmStamps.ReadAll(item, ctx);
        var (evu, eve) = s[F.EvaluatedAtUtc];
        var (fhu, fhe) = s[F.ForecastHourBeginningUtc];
        return new()
        {
            EvaluatedAtUtc = evu,
            EvaluatedAtEpt = eve,
            ForecastHourBeginningUtc = fhu,
            ForecastHourBeginningEpt = fhe,
            ForecastArea = PjmValueParser.Member(item, F.ForecastArea),
            ForecastLoadMw = PjmValueParser.Decimal(item, F.ForecastLoadMw, 18, 4),
        };
    }
}

/// <summary><c>very_short_load_frcst</c> → <see cref="PjmLoadForecastVeryShortRow"/>.</summary>
public sealed class PjmLoadForecastVeryShortRowMapper : IPjmRowMapper<PjmLoadForecastVeryShortRow>
{
    public PjmLoadForecastVeryShortRow Map(PjmItem item, PjmMapContext ctx)
    {
        var s = PjmStamps.ReadAll(item, ctx);
        var (evu, eve) = s[F.EvaluatedAtUtc];
        var (fbu, fbe) = s[F.ForecastDatetimeBeginningUtc];
        var (feu, fee) = s[F.ForecastDatetimeEndingUtc];
        return new()
        {
            EvaluatedAtUtc = evu,
            EvaluatedAtEpt = eve,
            ForecastDatetimeBeginningUtc = fbu,
            ForecastDatetimeBeginningEpt = fbe,
            ForecastDatetimeEndingUtc = feu,
            ForecastDatetimeEndingEpt = fee,
            ForecastArea = PjmValueParser.Member(item, F.ForecastArea),
            ForecastLoadMw = PjmValueParser.Decimal(item, F.ForecastLoadMw, 18, 4),
        };
    }
}

/// <summary><c>load_frcstd_7_day</c> → <see cref="PjmLoadForecast7DayRow"/>.</summary>
public sealed class PjmLoadForecast7DayRowMapper : IPjmRowMapper<PjmLoadForecast7DayRow>
{
    public PjmLoadForecast7DayRow Map(PjmItem item, PjmMapContext ctx)
    {
        var s = PjmStamps.ReadAll(item, ctx);
        var (evu, eve) = s[F.EvaluatedAtDatetimeUtc];
        var (fbu, fbe) = s[F.ForecastDatetimeBeginningUtc];
        var (feu, fee) = s[F.ForecastDatetimeEndingUtc];
        return new()
        {
            EvaluatedAtDatetimeUtc = evu,
            EvaluatedAtDatetimeEpt = eve,
            ForecastDatetimeBeginningUtc = fbu,
            ForecastDatetimeBeginningEpt = fbe,
            ForecastDatetimeEndingUtc = feu,
            ForecastDatetimeEndingEpt = fee,
            ForecastArea = PjmValueParser.Member(item, F.ForecastArea),
            ForecastLoadMw = PjmValueParser.Decimal(item, F.ForecastLoadMw, 18, 4),
        };
    }
}
