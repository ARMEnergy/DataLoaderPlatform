using DataLoader.Core.Configuration;

namespace DataLoader.CME;

/// <summary>
/// How a HOT trade date's work-unit resume key varies between runs.
///
/// <para>All tokens are stamped in <b>UTC</b>. <see cref="RunHour"/>'s
/// <c>yyyyMMddHH</c> is strictly monotonic only in UTC — <c>01:00</c> Central
/// occurs twice on a fall-back night, so a local-zone hour token would repeat, the
/// key would go backwards, and an already-recorded success would suppress a
/// legitimate re-pull for an hour.</para>
/// </summary>
public enum CmeHotKeyStrategy
{
    /// <summary>
    /// Suffix the key with the UTC run date <c>yyyyMMdd</c> so a hot date re-pulls
    /// once per UTC day. <b>The default.</b> CME publishes one final post-clearing
    /// bulletin per trade date per feed, so a second run the same day normally
    /// finds nothing new — and when clearing DOES revise a file, the content stamp
    /// already in the key catches it at any age.
    /// </summary>
    RunDate,

    /// <summary>Suffix with UTC <c>yyyyMMddHH</c> so a hot date re-pulls once per clock hour, for intraday schedules.</summary>
    RunHour,

    /// <summary>Suffix with the run id so every run re-pulls every hot date. Diagnostics only.</summary>
    RunId
}

/// <summary>
/// CME settlement-bulletin SFTP loader settings, bound from <c>Loaders:CME</c>.
/// Inherits the common bits (connection string, retry, concurrency, per-unit
/// timeout) and adds the SFTP connection plus the directory-layout fields.
///
/// <para>
/// <see cref="SftpUsername"/> and <see cref="SftpPassword"/> default to the
/// <c>SEE_DB</c> sentinel and resolve from <c>core.Param</c> at run time (that is
/// what <c>AddLoaderSettings</c> wires up). Either can also be overridden per
/// environment, e.g. <c>DATALOADER_Loaders__CME__SftpPassword</c>. Neither is ever
/// written to a log line.
/// </para>
/// </summary>
public sealed class CmeSettings : LoaderSettingsBase
{
    public string SftpHost { get; set; } = "sftp.cmeprod.datahex.rozettatech.com";
    public int SftpPort { get; set; } = 22;
    public string SftpUsername { get; set; } = "SEE_DB";
    public string SftpPassword { get; set; } = "SEE_DB";

    /// <summary>
    /// Directory holding the per-product product/exchange folders.
    ///
    /// <para>
    /// <b>Must stay RELATIVE.</b> This server answers a directory listing for
    /// <c>.</c> and for <c>./BAS_STLAGS</c>, but returns "no such file" for the
    /// ABSOLUTE <c>/BAS_STLAGS</c> — even though its own directory entries report
    /// exactly that absolute path. See <see cref="CmeSftpFileSystem"/>, which
    /// normalises a leading slash away so a configured <c>"/"</c> still works.
    /// </para>
    /// </summary>
    public string RootDirectory { get; set; } = ".";

    /// <summary>
    /// Feeds to run this pass; matched case-insensitively against the discovered
    /// feed ids (see <see cref="CmeFeed.FeedId"/>). Empty = every feed found on the
    /// drop, which is the shipped default.
    /// </summary>
    public string[] EnabledFeeds { get; set; } = Array.Empty<string>();

    /// <summary>
    /// Oldest trade date to enumerate, <c>yyyy-MM-dd</c>, or blank for "no floor".
    ///
    /// <para>
    /// Blank is the shipped default because the drop is a rolling window that held
    /// exactly 10 business days per feed when this loader was built, so walking all
    /// of it is cheap. Set a floor only to deliberately ignore older dates.
    /// </para>
    /// </summary>
    public string MinTradeDate { get; set; } = string.Empty;

    /// <summary>
    /// A trade date this many days old or newer is HOT — re-pulled every run even
    /// when its content stamp has not moved. Older dates are settled and reload
    /// only when CME republishes the file (detected by its stamp).
    /// </summary>
    public int SettledAfterDays { get; set; } = 3;

    /// <summary>How a hot date's key varies between runs.</summary>
    public CmeHotKeyStrategy HotKeyStrategy { get; set; } = CmeHotKeyStrategy.RunDate;

    /// <summary>Re-download and re-merge every enumerated file regardless of its stamp.</summary>
    public bool ForceReprocess { get; set; }

    /// <summary>
    /// Rows per TVP merge call. One bulletin yields up to roughly 150k fact rows,
    /// and shipping those as a single table-valued parameter makes one very large
    /// request; chunking keeps each merge's transaction and memory bounded.
    /// </summary>
    public int MergeBatchSize { get; set; } = 20000;

    /// <summary>
    /// Fail the work unit when a bulletin line cannot be classified, instead of
    /// counting it and carrying on.
    ///
    /// <para>
    /// Default OFF. The live drop really does contain a stray
    /// <c>KYP11 &lt;br /&gt;</c> line — an HTML fragment embedded in a CME product
    /// name — and one such artifact must not cost the whole 5 MB bulletin. The
    /// count is always logged and recorded on the <c>arm.FileLog</c> row, so a
    /// lenient parse is never a silent one.
    /// </para>
    /// </summary>
    public bool StrictLineParsing { get; set; }
}
