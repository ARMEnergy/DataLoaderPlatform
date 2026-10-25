namespace DataLoader.Host;

/// <summary>
/// The command line, parsed.
///
/// <para>Pulled out of <c>Program</c> so it can be tested directly. The reason
/// it needs testing: every unrecognised argument used to be fatal, so adding a
/// flag means the flag has to be consumed BEFORE loader-name resolution or it
/// exits 1 with "Unknown loader '--watchdog'".</para>
/// </summary>
public sealed class HostArguments
{
    public bool WatchdogMode { get; private init; }
    public IReadOnlyList<string> Loaders { get; private init; } = Array.Empty<string>();
    public string? Error { get; private init; }
    public bool IsValid => Error is null;

    private static bool IsWatchdogFlag(string arg) =>
        arg.Equals("--watchdog", StringComparison.OrdinalIgnoreCase) ||
        arg.Equals("/watchdog", StringComparison.OrdinalIgnoreCase);

    public static HostArguments Parse(string[] args, IReadOnlyList<string> knownLoaders)
    {
        if (args.Length == 0) return new HostArguments();

        if (args.Any(IsWatchdogFlag))
        {
            if (args.Length > 1)
            {
                return new HostArguments
                {
                    Error = "--watchdog runs the absence watchdog and takes no other arguments; " +
                            $"got: {string.Join(" ", args)}"
                };
            }
            return new HostArguments { WatchdogMode = true };
        }

        var known = knownLoaders.ToDictionary(n => n, StringComparer.OrdinalIgnoreCase);
        var resolved = new List<string>();
        var unknown = new List<string>();

        foreach (var arg in args)
        {
            if (known.TryGetValue(arg, out var canonical)) resolved.Add(canonical);
            else unknown.Add(arg);
        }

        if (unknown.Count > 0)
        {
            return new HostArguments
            {
                Error = $"Unknown loader(s): {string.Join(", ", unknown)}. " +
                        $"Known: {string.Join(", ", knownLoaders)}"
            };
        }

        return new HostArguments { Loaders = resolved };
    }
}
