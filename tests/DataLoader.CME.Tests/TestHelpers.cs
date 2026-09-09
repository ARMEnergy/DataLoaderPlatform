using Xunit;
using DataLoader.Core.Sources;

namespace DataLoader.CME.Tests;

internal static class TestHelpers
{
    public static readonly DateOnly TradeDate = new(2026, 9, 4);

    /// <summary>A feed whose exchange code decides the tick convention.</summary>
    public static CmeFeed Feed(string exchangeCode, string productCode = "EOD") =>
        new(productCode, exchangeCode, "BAS_" + exchangeCode, productCode + "_" + exchangeCode);

    public static (List<CmeFactRow> Rows, CmeParseStats Stats) Parse(
        string text, string exchangeCode = "STLNYMEX", DateOnly? tradeDate = null, bool strict = false) =>
        new CmeBulletinParser(Feed(exchangeCode), tradeDate ?? TradeDate, strict).Parse(text);

    public static CmeFactRow Option(this IEnumerable<CmeFactRow> rows, string symbol, decimal strike, string putCall) =>
        rows.Single(r => r.Kind == CmeRowKind.Option
                         && r.ProductSymbol == symbol
                         && r.Strike == strike
                         && r.PutCall == putCall);

    public static CmeFactRow Future(this IEnumerable<CmeFactRow> rows, string symbol, int year, int month) =>
        rows.Single(r => r.Kind == CmeRowKind.Future
                         && r.ProductSymbol == symbol
                         && r.ContractYear == year
                         && r.ContractMonth == month);

    public static RemoteFile File(
        string path, string name, long size = 1234, DateTime? modified = null) =>
        new(path, name, size, modified ?? new DateTime(2026, 9, 4, 23, 4, 0, DateTimeKind.Utc));

    /// <summary>Repo root, found by walking up from the test binaries to the folder holding the .sln.</summary>
    public static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);

        while (dir is not null && !System.IO.File.Exists(Path.Combine(dir.FullName, "DataLoaderPlatform.sln")))
            dir = dir.Parent;

        Assert.NotNull(dir);
        return dir!.FullName;
    }

    public static string SqlText(string fileName) =>
        System.IO.File.ReadAllText(Path.Combine(RepoRoot(), "sql", "CME", fileName));
}
