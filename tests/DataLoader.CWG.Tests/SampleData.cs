using System.Reflection;

namespace DataLoader.CWG.Tests;

/// <summary>
/// Loads the REAL captured CWG sample CSVs (copied next to the test assembly by
/// the csproj) for the parse-shape tests. These raw files are the ground truth
/// for column counts, hour labels, matrix widths and sentinels.
/// </summary>
internal static class SampleData
{
    private static readonly string SamplesDir =
        Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!, "Samples");

    public static string Read(string fileName)
    {
        var path = Path.Combine(SamplesDir, fileName);
        if (!File.Exists(path))
            throw new FileNotFoundException($"CWG sample fixture not found: {path}", path);
        return File.ReadAllText(path);
    }
}
