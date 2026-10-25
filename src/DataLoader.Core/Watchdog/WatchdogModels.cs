namespace DataLoader.Core.Watchdog;

/// <summary>
/// A loader's most recent run start, as recorded in
/// <c>core.LoaderRun_Summary</c>.
///
/// <para>⚠ <c>LastStartCentral</c> is US CENTRAL, not UTC — production's
/// <c>usp_CompleteLoaderRun</c> converts before storing. The name says so
/// because mixing it with a UTC clock reports the entire platform overdue.</para>
/// </summary>
public sealed record LoaderLastRun(string LoaderName, DateTime LastStartCentral);

/// <summary>
/// A <c>core.LoaderRun</c> row that never closed. Its timestamp IS UTC — a
/// different table with a different convention from <see cref="LoaderLastRun"/>.
/// </summary>
public sealed record StuckRun(Guid RunId, DateTime StartedAtUtc, string? HostName);

/// <summary>A run that recorded <c>Success = 0</c>. Timestamp is Central.</summary>
public sealed record RecentFailure(string LoaderName, DateTime StartTimeCentral, int UnitsFailed, int UnitsTotal);

/// <summary>A loader that has not run recently enough.</summary>
public sealed record OverdueLoader(
    string LoaderName,
    DateTime? LastStartCentral,
    double HoursSinceLastRun,
    int MaxGapHours);
