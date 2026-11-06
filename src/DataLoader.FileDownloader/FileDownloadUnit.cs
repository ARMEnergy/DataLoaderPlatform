using DataLoader.Core.Abstractions;

namespace DataLoader.FileDownloader;

/// <summary>
/// How a file is acquired. These are the four strategies of the standalone
/// <c>etl-tools/FileDownloader</c> console app, which selected between them with a
/// single command-line argument (<c>1</c>..<c>4</c>).
/// </summary>
public enum FileAcquisitionMode
{
    /// <summary>Arg <c>1</c>. URL built from a SQL template plus parameters, fetched with HttpClient.</summary>
    Link = 1,

    /// <summary>
    /// Arg <c>2</c>. A site with no stable URL, driven through a browser.
    ///
    /// <para>The original used Selenium with headless commented out, which is why its
    /// scheduled task runs from a user-profile directory — it needed an interactive
    /// desktop session. The port targets headless Playwright so the mode can run under
    /// the same service account as every other loader.</para>
    /// </summary>
    Browser = 2,

    /// <summary>Arg <c>3</c>. SSH.NET, password or private-key auth.</summary>
    Sftp = 3,

    /// <summary>Arg <c>4</c>. FluentFTP.</summary>
    Ftp = 4
}

/// <summary>What happened to one file. Persisted as <c>download.FileDownloader_File.Status</c>.</summary>
public enum FileDownloadStatus
{
    /// <summary>Fetched and written to the destination this run.</summary>
    Downloaded = 0,

    /// <summary>Already present at the destination; nothing was fetched. See
    /// <see cref="FileDownloaderSettings.SkipIfFileExists"/>.</summary>
    AlreadyPresent = 1,

    /// <summary>
    /// The source answered "no such file" — a legitimate empty read, not a failure.
    /// Mirrors the original app's <c>IsNotFound</c> flag: a report that simply has not
    /// been published for that date yet must not fail the run or alert anyone.
    /// </summary>
    NotFound = 2
}

/// <summary>
/// One file to acquire = one work unit = one transfer.
///
/// <para>The granularity matters: the standalone app looped over files inside a single
/// process-wide try/catch, so one bad file could end the batch and nothing recorded which
/// files had succeeded. One unit per file gives each file its own
/// <c>core.LoadLog</c> row, its own retry, and its own skip on re-run.</para>
/// </summary>
public sealed class FileDownloadUnit : WorkUnit
{
    public required FileAcquisitionMode Mode { get; init; }

    /// <summary>Logical source name — <c>Platts</c>, <c>Argus</c>, <c>ERCOT Documents</c>, …</summary>
    public required string SourceName { get; init; }

    /// <summary>Remote locator: a URL for Link/Browser, a remote path for Ftp/Sftp.</summary>
    public required string RemoteLocator { get; init; }

    /// <summary>Destination directory the file lands in.</summary>
    public required string DestinationPath { get; init; }

    /// <summary>Destination file name.</summary>
    public required string FileName { get; init; }

    /// <summary><c>download.FileDownloader_Info.Id</c> for Link units; null for the other modes.</summary>
    public int? InfoId { get; init; }

    /// <summary>Row id in the source's own list table, used to stamp <c>DateDownloaded</c> back.</summary>
    public int? ListRowId { get; init; }

    /// <summary>Publication date where the source has one. Part of the Link merge key.</summary>
    public DateTime? FileDate { get; init; }

    /// <summary>
    /// Stable identity for <c>core.LoadLog</c>.
    ///
    /// <para>⚠ Deliberately carries NO run token. A published file is immutable once
    /// fetched, so its unit must settle permanently — this is the opposite of the hot-key
    /// loaders (EnergyAspects, IHSPointLogic) whose vendors restate data and which stamp
    /// the run into the key precisely so a key can never recur. Adding a run token here
    /// would re-download the whole back catalogue on every run.</para>
    ///
    /// <para>Link units key on <c>(InfoId, FileName)</c>. ⚠ NOT on <see cref="FileDate"/>,
    /// even though the legacy <c>UK_FileDownloader_Document_FileDate</c> constraint uses
    /// that grain: the queue views expose only <c>Id</c>, <c>Value</c> and
    /// <c>FileName</c> (verified against all 17 on 2026-11-06), so a Link unit can never
    /// carry a date. Keying on a field the provider cannot populate would read as
    /// <c>date=</c> on every row today and silently re-key — re-downloading everything —
    /// the day anyone started populating it. The date is not lost: each view composes it
    /// into <c>FileName</c> from <c>FileDownloader_Info.DocumentName</c>, so the name is
    /// already date-bearing and unique per source.</para>
    ///
    /// <para>Non-Link units key on the remote locator, which for a listing-driven feed
    /// is the remote path.</para>
    /// </summary>
    public override string Key => Mode == FileAcquisitionMode.Link
        ? $"link:info={InfoId};file={FileName}"
        : $"{Mode.ToString().ToLowerInvariant()}:{SourceName}:{RemoteLocator}";

    public override string DisplayName => $"{SourceName} [{Mode}] {FileName}";
}
