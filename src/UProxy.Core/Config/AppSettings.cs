using System.Text.Json;
using System.Text.Json.Serialization;
using UProxy.Core.Models;

namespace UProxy.Core.Config;

public sealed class AppSettings
{
    public const int CurrentVersion = 2;

    public int Version { get; set; } = CurrentVersion;

    /// <summary>Max concurrent proxy checks / source fetches.</summary>
    public int Concurrency { get; set; } = 48;

    /// <summary>Overall per-proxy timeout in milliseconds.</summary>
    public int TimeoutMs { get; set; } = 10_000;

    public int DnsTimeoutMs { get; set; } = 5_000;
    public int ConnectTimeoutMs { get; set; } = 8_000;

    /// <summary>azenv-style proxy judge URL (HTTP or HTTPS).</summary>
    public string JudgeUrl { get; set; } = "http://azenv.net";

    /// <summary>Optional fallback judges tried if the primary returns a non-azenv body.</summary>
    public List<string> FallbackJudgeUrls { get; set; } =
    [
        "https://www.proxyjudge.info/"
    ];

    public bool AutoCheckAfterScrape { get; set; } = true;
    public bool AutoSaveResults { get; set; } = true;

    /// <summary>0 = HTTP(S), 1 = SOCKS.</summary>
    public int ProxyTypeMode { get; set; }

    public string HttpSourcesPath { get; set; } = Path.Combine("Data", "Source", "HttpSource.txt");
    public string SocksSourcesPath { get; set; } = Path.Combine("Data", "Source", "SocksSource.txt");
    public string GeoIpDatabasePath { get; set; } = Path.Combine("Data", "Country.mmdb");

    public string UserAgent { get; set; } = UserAgents.Default;

    /// <summary>Proxifier-compatible: resolve destination hostnames through the proxy (SOCKS4a / SOCKS5 domain).</summary>
    public bool ResolveHostnamesThroughProxy { get; set; } = true;

    /// <summary>Enable SOCKS4a remote hostname resolving (Ext4a).</summary>
    public bool UseSocks4a { get; set; } = true;

    /// <summary>Allocate 127.8.x.x Fake-IP placeholders for hostnames resolved through proxy.</summary>
    public bool EnableFakeIpDns { get; set; } = true;

    /// <summary>Path to (or name of) the TruffleHog executable used by the secret scanner.</summary>
    public string TruffleHogPath { get; set; } = "trufflehog";

    /// <summary>
    /// Let TruffleHog live-verify findings against provider APIs. Off by default: verification
    /// transmits candidate secrets to third parties, which conflicts with the tool's privacy stance.
    /// </summary>
    public bool SecretScanVerify { get; set; }

    /// <summary>Last main-window width in pixels (null = use default).</summary>
    public int? WindowWidth { get; set; }

    /// <summary>Last main-window height in pixels (null = use default).</summary>
    public int? WindowHeight { get; set; }

    /// <summary>Last main-window left edge; null = center.</summary>
    public int? WindowLeft { get; set; }

    /// <summary>Last main-window top edge; null = center.</summary>
    public int? WindowTop { get; set; }

    public bool WindowMaximized { get; set; }

    /// <summary>Local HTTP chain gateway port (loopback).</summary>
    public int ChainHttpPort { get; set; } = 8877;

    /// <summary>Local SOCKS5 chain gateway port (loopback).</summary>
    public int ChainSocksPort { get; set; } = 8878;

    /// <summary>When true (Windows), opt-in WinINET to the local HTTP gateway after listeners start.</summary>
    public bool ChainEnableSystemProxy { get; set; }

    /// <summary>Persisted id of the last active chain profile (if any).</summary>
    public string? ActiveChainProfileId { get; set; }

    /// <summary>URL used to verify exit IP after enabling a chain.</summary>
    public string ExitIpCheckUrl { get; set; } = "https://api.ipify.org";

    public ProxyProtocol PreferredCheckMode =>
        ProxyTypeMode == 1 ? ProxyProtocol.Socks5 : ProxyProtocol.Http;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    public static AppSettings Load(string path)
    {
        if (!File.Exists(path))
            return new AppSettings();

        var json = File.ReadAllText(path);
        var settings = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();
        settings.Clamp();
        return settings;
    }

    public void Save(string path)
    {
        Clamp();
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, JsonOptions));
        File.Move(tmp, path, overwrite: true);
    }

    public void Clamp()
    {
        Concurrency = Math.Clamp(Concurrency, 1, 256);
        TimeoutMs = Math.Clamp(TimeoutMs, 1_000, 120_000);
        DnsTimeoutMs = Math.Clamp(DnsTimeoutMs, 500, TimeoutMs);
        ConnectTimeoutMs = Math.Clamp(ConnectTimeoutMs, 500, TimeoutMs);
        if (string.IsNullOrWhiteSpace(JudgeUrl))
            JudgeUrl = "http://azenv.net";
        // HTTP/1.1 headers must be ASCII; strip any non-ASCII so checks don't throw.
        UserAgent = UserAgents.AsciiSafe(UserAgent);
        // Migrate UA that lost its leading "μ" via AsciiSafe on an older build.
        if (UserAgent.StartsWith("Proxy-Tool/", StringComparison.Ordinal))
            UserAgent = UserAgents.Default;

        if (WindowWidth is < 400)
            WindowWidth = null;
        if (WindowHeight is < 300)
            WindowHeight = null;

        ChainHttpPort = Math.Clamp(ChainHttpPort, 1, 65_535);
        ChainSocksPort = Math.Clamp(ChainSocksPort, 1, 65_535);
        if (string.IsNullOrWhiteSpace(ExitIpCheckUrl))
            ExitIpCheckUrl = "https://api.ipify.org";
    }
}
