namespace DataLoader.EIA.Tests;

/// <summary>
/// Builders for the catalog objects the loader reads out of <c>arm.*</c>, so tests can
/// describe a series in a line instead of filling in every column.
/// </summary>
internal static class TestHelpers
{
    public static EiaSeries Series(
        string seriesId = "coal/market-sales-price",
        string? startPeriod = "2001",
        string? endPeriod = "2024",
        string? defaultDateFormat = "YYYY",
        string? defaultFrequency = "annual",
        bool? allowIncremental = null) => new()
        {
            SeriesId = seriesId,
            Name = seriesId,
            StartPeriod = startPeriod,
            EndPeriod = endPeriod,
            DefaultDateFormat = defaultDateFormat,
            DefaultFrequency = defaultFrequency,
            AllowIncremental = allowIncremental
        };

    public static EiaFrequency Frequency(
        string frequencyId = "annual",
        string? format = "YYYY",
        string seriesId = "coal/market-sales-price") => new()
        {
            SeriesId = seriesId,
            FrequencyId = frequencyId,
            Query = frequencyId[..1].ToUpperInvariant(),
            Format = format
        };

    public static EiaDataType DataType(
        string dataTypeId,
        string seriesId = "coal/market-sales-price",
        string? units = null) => new()
        {
            SeriesId = seriesId,
            DataTypeId = dataTypeId,
            Units = units
        };

    /// <summary>
    /// A plan for the series the loader request uses as its worked example:
    /// <c>coal/market-sales-price</c>, annual, data columns <c>price</c> and
    /// <c>sales</c>.
    /// </summary>
    public static EiaSeriesPlan CoalPlan()
    {
        var dataTypes = new[]
        {
            DataType("price", units: "dollars per short ton"),
            DataType("sales", units: "short tons")
        };

        return new EiaSeriesPlan
        {
            Series = Series(),
            Frequencies = new[] { Frequency() },
            DataTypes = dataTypes,
            DataFieldNames = EiaWorkUnitProvider.BuildDataFieldNames(dataTypes),
            ApiStartPeriod = "2001",
            ApiEndPeriod = "2024"
        };
    }

    public static EiaSeriesPlan Plan(
        EiaSeries series,
        IReadOnlyList<EiaFrequency> frequencies,
        IReadOnlyList<EiaDataType> dataTypes,
        string? apiEndPeriod = null) => new()
        {
            Series = series,
            Frequencies = frequencies,
            DataTypes = dataTypes,
            DataFieldNames = EiaWorkUnitProvider.BuildDataFieldNames(dataTypes),
            ApiEndPeriod = apiEndPeriod ?? series.EndPeriod
        };

    public static EiaWorkUnit Unit(
        EiaSeriesPlan plan,
        EiaFrequency frequency,
        string windowStart,
        string windowEnd,
        bool settled = true,
        string? hotToken = null)
    {
        EiaPeriod.TryParseMask(frequency.Format, out var mask);
        EiaPeriod.TryParse(windowStart, mask, out var start);
        EiaPeriod.TryParse(windowEnd, mask, out var end);

        return new EiaWorkUnit
        {
            Plan = plan,
            Frequency = frequency,
            WindowStart = start,
            ReadStart = start,
            WindowEnd = end,
            Settled = settled,
            HotToken = hotToken
        };
    }
}
