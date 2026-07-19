namespace UProxy.Core.Config;

/// <summary>
/// Resolves shipped data files (GeoIP MMDB, source lists) when settings hold a stale absolute
/// path from a previous install folder — the usual cause of Country = "Unknown" for every proxy.
/// </summary>
public static class AppDataPaths
{
    public static string ResolveExistingOrDefault(
        string? configured,
        string defaultRelative,
        string appDirectory,
        params string[] extraSearchRoots)
    {
        foreach (var candidate in EnumerateCandidates(configured, defaultRelative, appDirectory, extraSearchRoots))
        {
            if (File.Exists(candidate))
                return Path.GetFullPath(candidate);
        }

        return Path.GetFullPath(Path.Combine(appDirectory, defaultRelative));
    }

    public static IEnumerable<string> EnumerateCandidates(
        string? configured,
        string defaultRelative,
        string appDirectory,
        params string[] extraSearchRoots)
    {
        if (!string.IsNullOrWhiteSpace(configured))
        {
            yield return Path.IsPathRooted(configured)
                ? configured
                : Path.GetFullPath(Path.Combine(appDirectory, configured));
        }

        yield return Path.GetFullPath(Path.Combine(appDirectory, defaultRelative));
        foreach (var root in extraSearchRoots)
        {
            if (string.IsNullOrWhiteSpace(root))
                continue;
            yield return Path.GetFullPath(Path.Combine(root, defaultRelative));
        }
    }

    /// <summary>Store a path relative to <paramref name="appDirectory"/> when it lives under that root.</summary>
    public static string ToPortable(string path, string appDirectory)
    {
        try
        {
            var full = Path.GetFullPath(path);
            var root = Path.GetFullPath(appDirectory)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            if (full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                return full[root.Length..];
        }
        catch
        {
            // keep absolute
        }
        return path;
    }
}
