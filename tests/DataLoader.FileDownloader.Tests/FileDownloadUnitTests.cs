using Xunit;

namespace DataLoader.FileDownloader.Tests;

/// <summary>
/// Work-unit identity and the manifest row's redaction rule — the two places where a
/// quiet mistake costs either a full re-download of the back catalogue or a leaked
/// credential.
/// </summary>
public sealed class FileDownloadUnitTests
{
    private static FileDownloadUnit Link(int infoId, DateTime date, string file) => new()
    {
        Mode = FileAcquisitionMode.Link,
        SourceName = "ERCOT Documents",
        RemoteLocator = "https://example.invalid/report",
        DestinationPath = @"\\arm.local\data\AssetRisk\Models\ERCOT Reports",
        FileName = file,
        InfoId = infoId,
        FileDate = date
    };

    private static FileDownloadUnit Remote(FileAcquisitionMode mode, string source, string path) => new()
    {
        Mode = mode,
        SourceName = source,
        RemoteLocator = path,
        DestinationPath = @"\\arm.local\data\AssetRisk\Models\Platts\PipelineFlows",
        FileName = Path.GetFileName(path)
    };

    [Fact]
    public void Key_is_stable_across_runs()
    {
        // ⚠ The whole point. A published file is immutable once fetched, so its unit must
        // settle permanently. If anything run-scoped (a date stamp, a run id) leaked into
        // the key, core.LoadLog could never match it and every run would re-download the
        // entire back catalogue.
        var a = Link(1, new DateTime(2026, 11, 3), "ercot_2026-11-03.zip").Key;
        var b = Link(1, new DateTime(2026, 11, 3), "ercot_2026-11-03.zip").Key;

        Assert.Equal(a, b);
    }

    [Fact]
    public void Key_contains_no_run_token_or_todays_date()
    {
        var key = Remote(FileAcquisitionMode.Sftp, "Platts", "/outbound/Pipeline Flows_2026_Nov.csv").Key;

        Assert.DoesNotContain("run=", key, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(DateTime.UtcNow.ToString("yyyyMMdd"), key);
    }

    [Fact]
    public void Link_keys_separate_by_info_and_filename()
    {
        var baseline = Link(1, new DateTime(2026, 11, 3), "a.zip").Key;

        Assert.NotEqual(baseline, Link(2, new DateTime(2026, 11, 3), "a.zip").Key);
        Assert.NotEqual(baseline, Link(1, new DateTime(2026, 11, 3), "b.zip").Key);
    }

    [Fact]
    public void Link_key_ignores_FileDate_because_the_queue_cannot_supply_one()
    {
        // ⚠ All 17 download.v_*_Not_Downloaded views expose only Id, Value and FileName
        // (verified 2026-11-06), so LinkWorkUnitProvider always sets FileDate = null.
        // A key that varied with FileDate would therefore read 'date=' on every real row
        // and silently re-key every unit — re-downloading the entire back catalogue —
        // the day anyone started populating it. The date is still present: each view
        // composes it into FileName from FileDownloader_Info.DocumentName.
        var withDate = Link(1, new DateTime(2026, 11, 3), "a.zip").Key;
        var withOther = Link(1, new DateTime(2020, 1, 1), "a.zip").Key;

        var withNone = new FileDownloadUnit
        {
            Mode = FileAcquisitionMode.Link,
            SourceName = "ERCOT Documents",
            RemoteLocator = "https://example.invalid/report",
            DestinationPath = @"\\arm.local\data\AssetRisk\Models\ERCOT Reports",
            FileName = "a.zip",
            InfoId = 1,
            FileDate = null
        }.Key;

        Assert.Equal(withDate, withOther);
        Assert.Equal(withDate, withNone);
        Assert.DoesNotContain("date=", withDate);
    }

    [Fact]
    public void Modes_never_collide_on_one_key()
    {
        var sftp = Remote(FileAcquisitionMode.Sftp, "Platts", "/outbound/x.csv").Key;
        var ftp = Remote(FileAcquisitionMode.Ftp, "Platts", "/outbound/x.csv").Key;

        Assert.NotEqual(sftp, ftp);
    }

    [Theory]
    [InlineData("https://vendor.example/files/report.csv?api_key=SECRETVALUE", "https://vendor.example/files/report.csv")]
    [InlineData("https://vendor.example/a/b.zip?token=abc&x=1", "https://vendor.example/a/b.zip")]
    [InlineData("https://vendor.example/plain.csv", "https://vendor.example/plain.csv")]
    public void SafeLocator_strips_the_query_string(string input, string expected)
    {
        // 🔒 Link URLs come from an operator-supplied string.Format template in
        // download.FileDownloader_Info.BaseURL. A vendor that authenticates by query
        // string would otherwise land a live key in a table that gets read by reports,
        // copied into extracts and backed up.
        Assert.Equal(expected, FileDownloadRow.SafeLocator(input));
    }

    [Fact]
    public void SafeLocator_strips_userinfo_credentials()
    {
        Assert.Equal(
            "https://vendor.example/f.csv",
            FileDownloadRow.SafeLocator("https://user:pass@vendor.example/f.csv"));
    }

    [Theory]
    [InlineData("/outbound/Pipeline Flows_2026_Nov.csv")]
    [InlineData(@"\\arm.local\data\AssetRisk\x.csv")]
    public void SafeLocator_leaves_non_url_paths_alone(string path)
    {
        // FTP/SFTP credentials travel in the connection, not the path, so these carry
        // nothing to redact — and mangling them would lose the diagnostic.
        Assert.Equal(path, FileDownloadRow.SafeLocator(path));
    }

    [Fact]
    public void SafeLocator_handles_null_and_blank()
    {
        Assert.Equal(string.Empty, FileDownloadRow.SafeLocator(null));
        Assert.Equal(string.Empty, FileDownloadRow.SafeLocator("   "));
    }

    [Fact]
    public void DisplayName_names_the_source_mode_and_file()
    {
        // core.LoadLog's WorkUnitDisplay is what the watchdog's resolution matching uses,
        // so it has to be meaningful on its own.
        var unit = Remote(FileAcquisitionMode.Ftp, "Argus", "/data/DCRDEUS.csv");

        Assert.Contains("Argus", unit.DisplayName);
        Assert.Contains("Ftp", unit.DisplayName);
        Assert.Contains("DCRDEUS.csv", unit.DisplayName);
    }
}
