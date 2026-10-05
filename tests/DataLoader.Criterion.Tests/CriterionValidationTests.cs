using System.Text.RegularExpressions;
using Xunit;

namespace DataLoader.Criterion.Tests;

/// <summary>
/// Guards the split between the two validation procs.
///
/// <para>
/// <b>The failure these exist to prevent.</b> <c>arm.usp_ValidateLoad</c> runs after
/// every load, and a client-side command timeout aborts the WHOLE proc — so a single
/// unbounded check does not merely fail itself, it silently costs every other finding
/// in the proc. That happened on 2026-10-02: the <c>OrphanSeriesData</c> check scanned
/// all of <c>arm.Financial_SeriesData</c> (~110M rows), expired at the client's 30s
/// default (<c>SqlException</c> -2), and took the cheap dimension and gap checks down
/// with it. Nobody noticed, because the validator swallows its own failures by design.
/// </para>
/// <para>
/// The rule that came out of it: <b>everything in usp_ValidateLoad must be bounded</b>,
/// and deliberately expensive full-history work lives in
/// <c>arm.usp_ValidateIntegrity</c>, which nothing calls per run.
/// </para>
/// </summary>
public sealed class CriterionValidationTests
{
    private static readonly string SchemaSql = File.ReadAllText(RepoPaths.SchemaScript);
    private static readonly string ProcSql = File.ReadAllText(RepoPaths.ProceduresScript);
    private static readonly string DropSql = File.ReadAllText(RepoPaths.DropScript);

    private static string ValidateLoadBody =>
        TvpParser.StripComments(TvpParser.ProcedureBody(ProcSql, "arm.usp_ValidateLoad"));

    private static string ValidateIntegrityBody =>
        TvpParser.StripComments(TvpParser.ProcedureBody(ProcSql, "arm.usp_ValidateIntegrity"));

    // ------------------------------------------------- usp_ValidateLoad is bounded

    /// <summary>
    /// ⚠ THE REGRESSION GUARD. Every read of the ~110M-row observation table inside the
    /// per-run proc must carry a <c>ModifiedAtUtc</c> bound. Without it the proc times
    /// out and every finding is lost, not just this one.
    /// </summary>
    [Fact]
    public void ValidateLoad_bounds_every_driving_read_of_the_observation_table()
    {
        var unbounded = DrivingReadsOfSeriesData(ValidateLoadBody);

        Assert.True(unbounded.Count == 0,
            "usp_ValidateLoad drives a scan of arm.Financial_SeriesData with no ModifiedAtUtc bound. " +
            "At ~110M rows that times out and aborts the WHOLE proc, losing every other finding with " +
            "it. Full-history work belongs in arm.usp_ValidateIntegrity. Offending statement(s):\n  " +
            string.Join("\n  ", unbounded));
    }

    /// <summary>
    /// Proves the guard above actually guards: the unbounded form of the check — the one
    /// that shipped and timed out — must be reported. Without this, a regex that silently
    /// stopped matching would leave the real test passing on anything.
    /// </summary>
    [Fact]
    public void The_bounding_guard_rejects_the_original_unbounded_statement()
    {
        const string regressed = """
            INSERT @Findings (Severity, Check_, Detail)
            SELECT 'WARN', 'OrphanSeriesData', 'x'
            FROM (SELECT DISTINCT d.FinancialJsonId
                  FROM arm.Financial_SeriesData AS d
                  WHERE NOT EXISTS (SELECT 1 FROM arm.Financial_Series AS s WHERE s.FinancialJsonId = d.FinancialJsonId)) AS x
            HAVING COUNT(*) > 0;
            """;

        Assert.NotEmpty(DrivingReadsOfSeriesData(regressed));
    }

    /// <summary>
    /// The correlated existence probe is a PK seek on <c>(FinancialJsonId, Date)</c> and
    /// is legitimately cheap, so it must NOT be reported — a guard that flagged it would
    /// be noise and would get disabled.
    /// </summary>
    [Fact]
    public void The_bounding_guard_accepts_the_cheap_correlated_probe()
    {
        const string probe = """
            SELECT 'WARN', 'SeriesWithoutData', 'x'
            FROM arm.Financial_Series AS s
            WHERE s.PostDate = @AsOfDate
              AND NOT EXISTS (SELECT 1 FROM arm.Financial_SeriesData AS d WHERE d.FinancialJsonId = s.FinancialJsonId)
            HAVING COUNT(*) > 0;
            """;

        Assert.Empty(DrivingReadsOfSeriesData(probe));
    }

    /// <summary>
    /// Statements that DRIVE a read of <c>arm.Financial_SeriesData</c> without a
    /// <c>ModifiedAtUtc</c> lower bound.
    ///
    /// <para>
    /// Works per STATEMENT (split on <c>;</c>) rather than over the whole proc, so a
    /// bounded statement elsewhere cannot mask an unbounded one. Correlated
    /// <c>NOT EXISTS (SELECT 1 FROM … WHERE …FinancialJsonId = …)</c> probes are stripped
    /// first: those seek the clustered PK and are cheap by construction.
    /// </para>
    /// </summary>
    private static List<string> DrivingReadsOfSeriesData(string sql)
    {
        var offenders = new List<string>();

        foreach (var statement in sql.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            // Drop the cheap correlated probes, then see what still reads the table.
            var driving = Regex.Replace(
                statement,
                @"NOT\s+EXISTS\s*\(\s*SELECT\s+1\s+FROM\s+arm\.Financial_SeriesData\s+AS\s+\w+\s+WHERE\s+\w+\.FinancialJsonId\s*=\s*\w+\.FinancialJsonId\s*\)",
                " ", RegexOptions.IgnoreCase | RegexOptions.Singleline);

            if (!Regex.IsMatch(driving, @"FROM\s+arm\.Financial_SeriesData", RegexOptions.IgnoreCase))
                continue;

            if (Regex.IsMatch(driving, @"\w+\.ModifiedAtUtc\s*>=", RegexOptions.IgnoreCase))
                continue;

            offenders.Add(Regex.Replace(statement.Trim(), @"\s+", " ") is var flat && flat.Length > 160
                ? flat[..160] + "..."
                : flat);
        }

        return offenders;
    }

    [Fact]
    public void ValidateLoad_declares_the_orphan_lookback_with_a_safe_default()
    {
        var header = TvpParser.ProcedureBody(ProcSql, "arm.usp_ValidateLoad");

        Assert.Matches(new Regex(@"@OrphanLookbackHours\s+INT\s*=\s*48", RegexOptions.IgnoreCase), header);

        // A null or nonsensical value must not disable the bound — that would restore
        // the unbounded scan through configuration rather than code.
        Assert.Matches(
            new Regex(@"IF\s+@OrphanLookbackHours\s+IS\s+NULL\s+OR\s+@OrphanLookbackHours\s*<\s*1",
                RegexOptions.IgnoreCase),
            ValidateLoadBody);
    }

    /// <summary>
    /// The window must stay comfortably wider than the gap between runs, or a single
    /// missed run opens a blind spot the per-run check can never report.
    /// </summary>
    [Fact]
    public void Orphan_lookback_default_covers_more_than_one_daily_run()
    {
        var match = Regex.Match(
            TvpParser.ProcedureBody(ProcSql, "arm.usp_ValidateLoad"),
            @"@OrphanLookbackHours\s+INT\s*=\s*(?<hours>\d+)", RegexOptions.IgnoreCase);

        Assert.True(match.Success, "usp_ValidateLoad does not declare @OrphanLookbackHours.");
        Assert.True(int.Parse(match.Groups["hours"].Value) >= 48,
            "The orphan lookback must span at least two daily runs so one missed run cannot " +
            "create an unreportable blind spot.");
    }

    /// <summary>The bounded check is only fast if the covering index exists.</summary>
    [Fact]
    public void Schema_declares_the_index_the_bounded_orphan_check_depends_on()
    {
        Assert.Matches(
            new Regex(
                @"CREATE\s+NONCLUSTERED\s+INDEX\s+IX_ARM_Financial_SeriesData_ModifiedAtUtc\s+" +
                @"ON\s+arm\.Financial_SeriesData\s*\(\s*ModifiedAtUtc\s*\)\s*INCLUDE\s*\(\s*FinancialJsonId\s*\)",
                RegexOptions.IgnoreCase),
            SchemaSql);
    }

    // --------------------------------------------- usp_ValidateIntegrity is separate

    [Fact]
    public void ValidateIntegrity_exists_and_carries_the_full_history_sweep()
    {
        var body = ValidateIntegrityBody;

        Assert.Contains("OrphanSeriesData_AllHistory", body, StringComparison.Ordinal);

        // The whole point of this proc: the orphan scan here is NOT date-bounded.
        Assert.Matches(
            new Regex(@"FROM\s+arm\.Financial_SeriesData\s+AS\s+d\s+WHERE\s+NOT\s+EXISTS", RegexOptions.IgnoreCase),
            Regex.Replace(body, @"\s+", " "));
    }

    /// <summary>
    /// ⚠ Nothing in the loader may call the expensive proc. If it ever appears in C#,
    /// the split has been undone and the per-run timeout is back.
    /// </summary>
    [Fact]
    public void ValidateIntegrity_is_never_called_by_the_loader()
    {
        var loaderSource = Directory
            .EnumerateFiles(RepoPaths.LoaderSourceDirectory, "*.cs", SearchOption.AllDirectories)
            .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                     && !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"));

        // Looks for the QUOTED literal — that is how a proc is invoked. Prose
        // mentioning the name (CriterionLoadValidator's own comment explains where
        // expensive work belongs) is documentation, not a call, and must not trip this.
        foreach (var file in loaderSource)
            Assert.DoesNotMatch(
                new Regex("\"arm\\.usp_ValidateIntegrity\""),
                File.ReadAllText(file));
    }

    [Fact]
    public void Loader_still_calls_the_bounded_proc()
    {
        var validator = File.ReadAllText(
            Path.Combine(RepoPaths.LoaderSourceDirectory, "CriterionLoadValidator.cs"));

        Assert.Contains("arm.usp_ValidateLoad", validator, StringComparison.Ordinal);
    }

    /// <summary>
    /// The 30-second default is what turned a slow check into a silent total loss of
    /// validation. An explicit timeout is required — see CriterionLoadValidator.
    /// </summary>
    [Fact]
    public void Validator_sets_an_explicit_command_timeout()
    {
        var validator = File.ReadAllText(
            Path.Combine(RepoPaths.LoaderSourceDirectory, "CriterionLoadValidator.cs"));

        var match = Regex.Match(validator, @"CommandTimeout\s*=\s*(?<seconds>\d+)");

        Assert.True(match.Success,
            "CriterionLoadValidator does not set CommandTimeout, so it uses SqlCommand's 30s default. " +
            "A client-side expiry aborts the whole proc and loses every finding.");

        Assert.True(int.Parse(match.Groups["seconds"].Value) >= 120,
            "The command timeout is too tight for a proc that reads this loader's largest tables.");
    }

    [Fact]
    public void Drop_script_drops_both_validation_procs()
    {
        Assert.Contains("arm.usp_ValidateLoad", DropSql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("arm.usp_ValidateIntegrity", DropSql, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Both procs return the same shape, so one reader can consume either.</summary>
    [Fact]
    public void Both_validation_procs_return_the_same_result_shape()
    {
        foreach (var body in new[] { ValidateLoadBody, ValidateIntegrityBody })
            Assert.Matches(
                new Regex(@"SELECT\s+Severity\s*,\s*Check_\s+AS\s+\[Check\]\s*,\s*Detail", RegexOptions.IgnoreCase),
                body);
    }
}
