using System.Globalization;
using DataLoader.Core.Abstractions;
using DataLoader.Core.Sources;

namespace DataLoader.Platts;

/// <summary>
/// One parsed row of the SymbolData feed, targeting <c>arm.SymbolData</c>.
/// The trailing carry fields are not written to <c>arm.SymbolData</c>; they feed
/// the <c>arm.FileLog</c> audit row via the file-logging sink decorator.
/// </summary>
public sealed class SymbolDataRow
{
    // --- arm.SymbolData columns ---
    public string MDC { get; set; } = string.Empty;
    public string Symbol { get; set; } = string.Empty;
    public string Bate { get; set; } = string.Empty;
    public DateTime Date { get; set; }
    public string Action { get; set; } = string.Empty;
    public decimal? Value { get; set; }
    public DateTime ActionDate { get; set; }
    public string SourcePath { get; set; } = string.Empty;

    // --- carry fields for arm.FileLog ---
    public string FileName { get; set; } = string.Empty;
    public DateTime LastModifiedUtc { get; set; }
    public long? SizeBytes { get; set; }
}

/// <summary>
/// One parsed row of the Symbol reference feed, targeting <c>arm.Symbol</c>.
/// The trailing carry fields feed the <c>arm.FileLog</c> audit row.
/// </summary>
public sealed class SymbolRow
{
    // --- arm.Symbol columns ---
    public string? MDC { get; set; }
    public string? Trans { get; set; }
    public string Symbol { get; set; } = string.Empty;
    public string? Bates { get; set; }
    public string? Freq { get; set; }
    public string? Curr { get; set; }
    public string? UOM { get; set; }
    public int? Dec { get; set; }
    public decimal? Conv { get; set; }
    public string? Flag { get; set; }
    public string? ToUom { get; set; }
    public DateTime? Earliest { get; set; }
    public DateTime? Latest { get; set; }
    public string? Description { get; set; }

    // --- carry fields for arm.FileLog ---
    public string SourcePath { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public DateTime LastModifiedUtc { get; set; }
    public long? SizeBytes { get; set; }
}

/// <summary>One <c>.ftp</c> market file in a date folder = one SymbolData work unit.</summary>
public sealed class SymbolDataWorkUnit : WorkUnit
{
    public required RemoteFile File { get; init; }

    /// <summary>The date folder's name (last path segment), e.g. <c>20260731</c>.</summary>
    public required string Folder { get; init; }

    /// <summary>
    /// Run token appended to <see cref="Key"/> when
    /// <see cref="PlattsSettings.ReloadAllFiles"/> is on, so the key can never
    /// match an already-completed <c>core.LoadLog</c> row and the file is always
    /// reloaded. Null in normal operation, which keeps the key stable across runs.
    /// </summary>
    public string? RunToken { get; init; }

    // LastModifiedUtc in the key means an unchanged file is skipped and a changed
    // one (new timestamp) gets a new key -> reprocessed. RunToken, when present,
    // defeats that skip entirely (PlattsSettings.ReloadAllFiles).
    public override string Key =>
        $"platts:sd:{Folder}/{File.Name}:{File.Size}:{File.LastModifiedUtc:O}{PlattsKey.RunSuffix(RunToken)}";

    public override string DisplayName => $"{Folder}/{File.Name}";
}

/// <summary>One reference-metadata CSV = one Symbol work unit.</summary>
public sealed class SymbolWorkUnit : WorkUnit
{
    public required RemoteFile File { get; init; }

    /// <summary>See <see cref="SymbolDataWorkUnit.RunToken"/>.</summary>
    public string? RunToken { get; init; }

    public override string Key =>
        $"platts:sym:{File.Name}:{File.Size}:{File.LastModifiedUtc:O}{PlattsKey.RunSuffix(RunToken)}";

    public override string DisplayName => File.Name;
}

/// <summary>Work-unit key helpers shared by both Platts feeds.</summary>
internal static class PlattsKey
{
    /// <summary>
    /// The <c>:run=&lt;token&gt;</c> tail, or empty when no token is set. Kept in one
    /// place so both feeds spell the suffix identically and so an absent token can
    /// never silently alter the stable key.
    /// </summary>
    public static string RunSuffix(string? runToken) =>
        string.IsNullOrEmpty(runToken) ? string.Empty : $":run={runToken}";

    /// <summary>
    /// The run token for this pass: the run id when
    /// <see cref="PlattsSettings.ReloadAllFiles"/> forces a full reload, else null.
    /// Used by the Symbol feed, whose CSVs carry no date and so have no hot zone.
    /// </summary>
    public static string? RunToken(PlattsSettings settings, LoaderRunContext context) =>
        settings.ReloadAllFiles ? context.RunId.ToString("N") : null;
}

/// <summary>
/// The <c>DaysBack</c> / <c>SettledAfterDays</c> two-zone model applied to the
/// SymbolData feed's <c>yyyymmdd</c> date folders (the same rule CWG and
/// StormVista use, with the folder name standing in for the represented date).
///
/// <para>
/// <c>DaysBack</c> bounds <i>discovery</i>: a folder older than the window is not
/// even listed, so its files cost nothing. <c>SettledAfterDays</c> splits what
/// survives into two zones — a SETTLED folder keeps the stable base key (loaded
/// once per file version, then skipped; a genuinely changed file still gets a new
/// key from its size/last-modified and is re-read), while a HOT folder's key
/// carries the run token so its files are re-read and re-merged every single run.
/// </para>
/// </summary>
internal static class PlattsFolderWindow
{
    private const string FolderDateFormat = "yyyyMMdd";

    /// <summary>
    /// The folder name parsed as a date, or null when it is not a real calendar
    /// date. <c>DateFolderPattern</c> only guarantees eight digits, so values like
    /// <c>20261340</c> reach here and must not be assumed parseable.
    /// </summary>
    public static DateOnly? ParseFolderDate(string folderName) =>
        DateOnly.TryParseExact(folderName, FolderDateFormat, CultureInfo.InvariantCulture,
                               DateTimeStyles.None, out var date)
            ? date
            : null;

    /// <summary>
    /// How many days before <paramref name="todayUtc"/> the folder sits. Null when
    /// the folder is not datable. Negative for a future-dated folder, which happens
    /// whenever the vendor's publication day runs ahead of UTC.
    /// </summary>
    public static int? AgeDays(string folderName, DateOnly todayUtc) =>
        ParseFolderDate(folderName) is { } date ? todayUtc.DayNumber - date.DayNumber : null;

    /// <summary>
    /// Is this folder inside the <c>DaysBack</c> discovery window? A non-positive
    /// <c>DaysBack</c> means "no limit" (every folder on the server, the behaviour
    /// before this setting existed), and an undatable folder is always kept rather
    /// than silently dropped — losing data is worse than listing one extra folder.
    /// </summary>
    public static bool InWindow(int? ageDays, int daysBack) =>
        daysBack <= 0 || ageDays is null || ageDays.Value < daysBack;

    /// <summary>
    /// Is this folder HOT — re-read every run regardless of the load log?
    /// <see cref="PlattsSettings.ReloadAllFiles"/> makes every folder hot; otherwise
    /// the CWG/StormVista rule applies: older than <c>SettledAfterDays</c> settles,
    /// anything newer stays hot. A negative <c>SettledAfterDays</c> leaves nothing
    /// hot, and an undatable folder settles (its size/last-modified still catch real
    /// changes).
    /// </summary>
    public static bool IsHot(int? ageDays, PlattsSettings settings) =>
        settings.ReloadAllFiles || (ageDays is { } age && age <= settings.SettledAfterDays);

    /// <summary>The run token to stamp on a folder's units: the run id when hot, else null.</summary>
    public static string? RunTokenFor(int? ageDays, PlattsSettings settings, LoaderRunContext context) =>
        IsHot(ageDays, settings) ? context.RunId.ToString("N") : null;
}
