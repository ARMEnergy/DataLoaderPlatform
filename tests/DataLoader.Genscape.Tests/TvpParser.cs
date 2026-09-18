using System.Text.RegularExpressions;

namespace DataLoader.Genscape.Tests;

/// <summary>
/// Minimal T-SQL slicing, enough to pull a <c>CREATE TYPE</c> / <c>CREATE TABLE</c>
/// body or one procedure out of a script. Deliberately not a real parser — it only
/// has to handle the shapes this repo's own scripts use.
/// </summary>
internal static class TvpParser
{
    internal readonly record struct Column(string Name, string SqlType, bool NotNull);

    public static IReadOnlyList<Column> Parse(string sql, string typeName)
    {
        var header = new Regex(
            $@"CREATE\s+TYPE\s+{Regex.Escape(typeName)}\s+AS\s+TABLE\s*\(",
            RegexOptions.IgnoreCase);

        var match = header.Match(sql);
        if (!match.Success) return Array.Empty<Column>();

        var open = match.Index + match.Length - 1;   // index of the '('
        return SplitTopLevel(ReadBalanced(sql, open)).Select(ParseColumn).ToList();
    }

    public static IReadOnlyList<string> DeclaredTypeNames(string sql) =>
        Regex.Matches(sql, @"CREATE\s+TYPE\s+([A-Za-z0-9_\.\[\]]+)\s+AS\s+TABLE", RegexOptions.IgnoreCase)
             .Select(m => m.Groups[1].Value.Replace("[", string.Empty).Replace("]", string.Empty))
             .ToList();

    /// <summary>Body of a <c>CREATE TABLE</c>, for order checks against the descriptor.</summary>
    public static string CreateTableBody(string sql, string tableName)
    {
        var header = new Regex($@"CREATE\s+TABLE\s+{Regex.Escape(tableName)}\s*\(", RegexOptions.IgnoreCase);
        var match = header.Match(sql);

        if (!match.Success)
            throw new InvalidOperationException($"No CREATE TABLE found for {tableName}.");

        return ReadBalanced(sql, match.Index + match.Length - 1);
    }

    /// <summary>Text of one procedure, from its CREATE to the next one (or end of file).</summary>
    public static string ProcedureBody(string sql, string procName)
    {
        var header = new Regex(
            $@"CREATE\s+OR\s+ALTER\s+PROCEDURE\s+{Regex.Escape(procName)}\b",
            RegexOptions.IgnoreCase);

        var match = header.Match(sql);
        if (!match.Success)
            throw new InvalidOperationException($"No procedure found named {procName}.");

        var next = Regex.Match(
            sql[(match.Index + match.Length)..],
            @"CREATE\s+OR\s+ALTER\s+PROCEDURE\s+", RegexOptions.IgnoreCase);

        var length = next.Success ? next.Index : sql.Length - match.Index - match.Length;

        return sql.Substring(match.Index, match.Length + length);
    }

    /// <summary>
    /// Removes <c>--</c> line comments and <c>/* */</c> blocks. The procs in 003 discuss
    /// their own column names at length, so a Contains-style assertion has to look at
    /// code rather than prose.
    /// </summary>
    public static string StripComments(string sql)
    {
        var withoutBlocks = Regex.Replace(sql, @"/\*.*?\*/", " ", RegexOptions.Singleline);
        return Regex.Replace(withoutBlocks, @"--[^\r\n]*", " ");
    }

    /// <summary>Content between <paramref name="openIndex"/>'s paren and its match.</summary>
    private static string ReadBalanced(string sql, int openIndex)
    {
        var depth = 0;
        for (var i = openIndex; i < sql.Length; i++)
        {
            if (sql[i] == '(') depth++;
            else if (sql[i] == ')')
            {
                depth--;
                if (depth == 0) return sql[(openIndex + 1)..i];
            }
        }

        throw new InvalidOperationException("Unbalanced parentheses in body.");
    }

    /// <summary>Split on commas at depth 0, so DECIMAL(13,10) stays intact.</summary>
    private static IEnumerable<string> SplitTopLevel(string body)
    {
        var depth = 0;
        var start = 0;

        for (var i = 0; i < body.Length; i++)
        {
            if (body[i] == '(') depth++;
            else if (body[i] == ')') depth--;
            else if (body[i] == ',' && depth == 0)
            {
                yield return body[start..i];
                start = i + 1;
            }
        }

        if (start < body.Length) yield return body[start..];
    }

    private static Column ParseColumn(string declaration)
    {
        var text = StripComments(declaration).Trim();

        var notNull = Regex.IsMatch(text, @"\bNOT\s+NULL\b", RegexOptions.IgnoreCase);

        // Strip the nullability tail so what remains is "<name> <type>".
        text = Regex.Replace(text, @"\b(NOT\s+)?NULL\b", string.Empty, RegexOptions.IgnoreCase).Trim();

        var match = Regex.Match(text, @"^\[?(?<name>[A-Za-z0-9_]+)\]?\s+(?<type>.+)$", RegexOptions.Singleline);
        if (!match.Success)
            throw new InvalidOperationException($"Could not parse TVP column declaration: '{declaration}'.");

        return new Column(
            match.Groups["name"].Value.Trim(),
            Regex.Replace(match.Groups["type"].Value, @"\s+", " ").Trim(),
            notNull);
    }
}

/// <summary>Locates <c>sql/Genscape</c> by walking up from the test binaries.</summary>
internal static class RepoPaths
{
    public static string SqlDirectory { get; } = FindSqlDirectory();

    public static string SchemaScript => Path.Combine(SqlDirectory, "001_CreateGenscapeSchema.sql");
    public static string TvpScript => Path.Combine(SqlDirectory, "002_CreateGenscapeTvpTypes.sql");
    public static string ProceduresScript => Path.Combine(SqlDirectory, "003_CreateGenscapeProcedures.sql");
    public static string DropScript => Path.Combine(SqlDirectory, "999_DropGenscapeObjects.sql");

    private static string FindSqlDirectory()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);

        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "sql", "Genscape");
            if (Directory.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException(
            $"Could not locate sql/Genscape by walking up from '{AppContext.BaseDirectory}'.");
    }
}
