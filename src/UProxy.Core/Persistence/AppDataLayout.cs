namespace UProxy.Core.Persistence;

/// <summary>
/// Resolves on-disk layout under <c>%LocalAppData%/uProxyTool</c> (or an injectable root for tests).
/// </summary>
public sealed class AppDataLayout
{
    public const string DefaultFolderName = "uProxyTool";

    public AppDataLayout(string? root = null)
    {
        Root = string.IsNullOrWhiteSpace(root)
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                DefaultFolderName)
            : Path.GetFullPath(root);
    }

    public string Root { get; }

    public string ChainsDirectory => Path.Combine(Root, "chains");
    public string PoolsDirectory => Path.Combine(Root, "pools");
    public string CredentialsDirectory => Path.Combine(Root, "credentials");
    public string ProtectedCredentialsPath => Path.Combine(CredentialsDirectory, "protected.dat");
    public string HealthDirectory => Path.Combine(Root, "health");
    public string ProxyHealthPath => Path.Combine(HealthDirectory, "proxy-health.json");

    public void EnsureDirectories()
    {
        Directory.CreateDirectory(ChainsDirectory);
        Directory.CreateDirectory(PoolsDirectory);
        Directory.CreateDirectory(CredentialsDirectory);
        Directory.CreateDirectory(HealthDirectory);
    }

    /// <summary>Sanitizes a display name into a stable file stem (no extension).</summary>
    public static string SafeFileStem(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Name is required.", nameof(name));

        var invalid = Path.GetInvalidFileNameChars();
        Span<char> buffer = stackalloc char[name.Length];
        var n = 0;
        foreach (var ch in name.Trim())
        {
            if (ch is '.' or ' ' && n == 0)
                continue;
            buffer[n++] = Array.IndexOf(invalid, ch) >= 0 ? '_' : ch;
        }

        if (n == 0)
            throw new ArgumentException("Name yields an empty file stem.", nameof(name));

        var stem = new string(buffer[..n]).TrimEnd('.', ' ');
        if (string.IsNullOrEmpty(stem))
            throw new ArgumentException("Name yields an empty file stem.", nameof(name));
        return stem;
    }

    internal static void AtomicWriteAllBytes(string path, byte[] bytes)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        var tmp = path + ".tmp";
        File.WriteAllBytes(tmp, bytes);
        File.Move(tmp, path, overwrite: true);
    }

    internal static void AtomicWriteAllText(string path, string contents)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        var tmp = path + ".tmp";
        File.WriteAllText(tmp, contents);
        File.Move(tmp, path, overwrite: true);
    }
}
