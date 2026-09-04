using System.Text.Json;
using Xunit;

namespace DataLoader.ICE.Tests;

/// <summary>
/// The local file cache and the SSO token extraction.
/// </summary>
public sealed class CacheAndAuthTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ice-cache-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch (IOException) { /* best-effort test cleanup */ }
    }

    private IceFileCache Cache(Action<IceSettings>? configure = null) =>
        TestHelpers.Cache(TestHelpers.Settings(s =>
        {
            s.DownloadDirectory = _root;
            configure?.Invoke(s);
        }));

    private static readonly IceFeedDescriptor Gas = IceDescriptors.Find("IceGas")!;
    private static readonly DateOnly TradeDate = new(2026, 8, 28);

    [Fact]
    public void Files_are_laid_out_by_feed_id()
    {
        var path = Cache().PathFor(Gas, TradeDate);

        Assert.Equal(
            Path.Combine(_root, "IceGas", "icecleared_gas_2026_08_28.dat"),
            path);
    }

    /// <summary>A relative directory resolves against the host base directory, not the CWD.</summary>
    [Fact]
    public void Relative_download_directory_resolves_against_the_base_directory()
    {
        var resolved = IceFileCache.ResolveRoot("files/ICE");

        Assert.True(Path.IsPathRooted(resolved));
        Assert.StartsWith(Path.GetFullPath(AppContext.BaseDirectory), resolved, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Absolute_download_directory_is_used_as_is()
    {
        Assert.Equal(Path.GetFullPath(_root), IceFileCache.ResolveRoot(_root));
    }

    [Fact]
    public async Task Saved_file_is_read_back()
    {
        var cache = Cache();
        var content = TestHelpers.Bytes(Samples.IceGas);

        await cache.SaveAsync(Gas, TradeDate, content, CancellationToken.None);
        var readBack = await cache.TryReadAsync(Gas, TradeDate, CancellationToken.None);

        Assert.NotNull(readBack);
        Assert.Equal(content, readBack);
    }

    [Fact]
    public async Task Missing_file_reads_back_as_null()
    {
        Assert.Null(await Cache().TryReadAsync(Gas, TradeDate, CancellationToken.None));
    }

    /// <summary>
    /// A zero-length file is what an interrupted write leaves behind. Treating it as
    /// a cache hit would classify as Malformed forever, so it must read as a miss.
    /// </summary>
    [Fact]
    public async Task Zero_length_file_is_treated_as_a_miss()
    {
        var cache = Cache();
        var path = cache.PathFor(Gas, TradeDate);

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllBytesAsync(path, Array.Empty<byte>());

        Assert.Null(await cache.TryReadAsync(Gas, TradeDate, CancellationToken.None));
    }

    [Fact]
    public async Task ForceDownload_bypasses_the_cache()
    {
        var cache = Cache(s => s.ForceDownload = true);
        await cache.SaveAsync(Gas, TradeDate, TestHelpers.Bytes(Samples.IceGas), CancellationToken.None);

        Assert.Null(await cache.TryReadAsync(Gas, TradeDate, CancellationToken.None));
    }

    [Fact]
    public async Task No_temp_file_is_left_behind_after_a_save()
    {
        var cache = Cache();
        await cache.SaveAsync(Gas, TradeDate, TestHelpers.Bytes(Samples.IceGas), CancellationToken.None);

        var directory = Path.GetDirectoryName(cache.PathFor(Gas, TradeDate))!;
        Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
    }

    // ---------------------------------------------------------------- retention

    [Fact]
    public async Task Sweep_deletes_files_older_than_the_retention_window()
    {
        var cache = Cache(s => s.FileRetentionDays = 7);

        var fresh = cache.PathFor(Gas, TradeDate);
        var stale = cache.PathFor(Gas, new DateOnly(2026, 7, 1));

        await cache.SaveAsync(Gas, TradeDate, TestHelpers.Bytes("fresh"), CancellationToken.None);
        await cache.SaveAsync(Gas, new DateOnly(2026, 7, 1), TestHelpers.Bytes("stale"), CancellationToken.None);

        // Retention is by LAST-WRITE time, not by the date in the file name.
        File.SetLastWriteTimeUtc(stale, DateTime.UtcNow.AddDays(-30));

        var deleted = cache.SweepExpired();

        Assert.Equal(1, deleted);
        Assert.False(File.Exists(stale));
        Assert.True(File.Exists(fresh));
    }

    [Fact]
    public async Task Sweep_is_disabled_when_retention_is_zero_or_negative()
    {
        var cache = Cache(s => s.FileRetentionDays = 0);
        var path = cache.PathFor(Gas, TradeDate);

        await cache.SaveAsync(Gas, TradeDate, TestHelpers.Bytes("x"), CancellationToken.None);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-1000));

        Assert.Equal(0, cache.SweepExpired());
        Assert.True(File.Exists(path));
    }

    [Fact]
    public void Sweep_of_a_missing_root_is_a_no_op()
    {
        Assert.Equal(0, Cache().SweepExpired());
    }

    [Fact]
    public async Task Sweep_recurses_into_every_feed_folder()
    {
        var cache = Cache(s => s.FileRetentionDays = 1);
        var oil = IceDescriptors.Find("IceOil")!;

        await cache.SaveAsync(Gas, TradeDate, TestHelpers.Bytes("a"), CancellationToken.None);
        await cache.SaveAsync(oil, TradeDate, TestHelpers.Bytes("b"), CancellationToken.None);

        foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
            File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddDays(-10));

        Assert.Equal(2, cache.SweepExpired());
    }

    // ---------------------------------------------------------------- SSO token

    /// <summary>
    /// The token is at <c>result.data.token</c> — a SIBLING of <c>attributes</c>, not
    /// nested inside it. This is the real envelope shape, trimmed.
    /// </summary>
    [Fact]
    public void Token_is_read_from_result_data_token()
    {
        const string json = """
        {"result":{"data":{
          "attributes":{},
          "roles":["ICEDOWNLOADS:CRUDE_INDEX"],
          "token":"exampleuser.ICEDOWNLOADS.2026_09_01.13_45_00_387.4T..EXAMPLETOKEN..0000000000",
          "user":"exampleuser"
        }}}
        """;

        var root = JsonDocument.Parse(json).RootElement;

        Assert.Equal(
            "exampleuser.ICEDOWNLOADS.2026_09_01.13_45_00_387.4T..EXAMPLETOKEN..0000000000",
            IceSsoAuthenticator.ExtractToken(root));
    }

    /// <summary>A reshuffled envelope degrades to "still works" rather than "every download fails".</summary>
    [Fact]
    public void Token_is_found_even_if_the_envelope_moves()
    {
        var root = JsonDocument.Parse("""{"payload":{"nested":{"deeper":{"token":"abc123"}}}}""").RootElement;
        Assert.Equal("abc123", IceSsoAuthenticator.ExtractToken(root));
    }

    [Fact]
    public void Missing_token_returns_null_rather_than_an_empty_string()
    {
        var root = JsonDocument.Parse("""{"result":{"data":{"user":"exampleuser"}}}""").RootElement;
        Assert.Null(IceSsoAuthenticator.ExtractToken(root));
    }

    [Fact]
    public void Blank_token_is_not_accepted()
    {
        var root = JsonDocument.Parse("""{"result":{"data":{"nested":{"token":"   "}}}}""").RootElement;
        Assert.Null(IceSsoAuthenticator.ExtractToken(root));
    }
}
