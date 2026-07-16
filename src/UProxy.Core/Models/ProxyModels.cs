namespace UProxy.Core.Models;

public enum ProxyProtocol
{
    Unknown = 0,
    Http = 1,
    Https = 2,
    Socks4 = 3,
    Socks5 = 4,
    Socks4And5 = 5
}

public enum AnonymityLevel
{
    Unknown = 0,
    Transparent = 1,
    Anonymous = 2,
    Elite = 3
}

public enum FailureReason
{
    None = 0,
    InvalidProxy,
    DnsFailure,
    ConnectRefused,
    ConnectTimeout,
    ProxyHandshakeFailure,
    AuthenticationRequired,
    TlsFailure,
    JudgeMismatch,
    JudgeUnavailable,
    EmptyResponse,
    Cancelled,
    Timeout,
    UnknownError,
    /// <summary>TCP reached the proxy, but the proxy could not open a connection to the target host.</summary>
    TargetUnreachableThroughProxy,
    /// <summary>The proxy refused the HTTPS CONNECT tunnel (e.g. Squid SSL_ports / ISA tunnel-port policy).</summary>
    HttpsConnectForbidden
}

public enum ProxyAuthMethod
{
    None = 0,
    Basic = 1,
    SocksUserPass = 2,
    Socks4UserId = 3,
    /// <summary>Proxy advertised NTLM/Negotiate; not performed by default (privacy).</summary>
    NtlmRequired = 4
}

public enum SessionKind
{
    Idle,
    Scraping,
    Checking
}

public enum SessionStatus
{
    Idle,
    Running,
    Stopping,
    Stopped,
    Completed
}

/// <summary>Parsed proxy endpoint. Credentials are kept separate and omitted from default exports.</summary>
public sealed record ParsedProxy(
    string Host,
    int Port,
    ProxyProtocol Protocol = ProxyProtocol.Unknown,
    string? Username = null,
    string? Password = null)
{
    public string Endpoint => Host.Contains(':') && !Host.StartsWith('[')
        ? $"[{Host}]:{Port}"
        : $"{Host}:{Port}";

    public string Key => $"{NormalizeHost(Host)}|{Port}|{Protocol}|{Username}";

    public override string ToString() =>
        string.IsNullOrEmpty(Username)
            ? Endpoint
            : $"{Username}:***@{Endpoint}";

    public string ToExportString(bool includeCredentials = false)
    {
        if (!includeCredentials || string.IsNullOrEmpty(Username))
            return Endpoint;
        return $"{Username}:{Password}@{Endpoint}";
    }

    private static string NormalizeHost(string host) =>
        host.Trim().TrimStart('[').TrimEnd(']').ToLowerInvariant();
}

public sealed class ProxyCheckResult
{
    public required ParsedProxy Proxy { get; init; }
    public bool IsAlive { get; init; }
    public ProxyProtocol ConfirmedProtocol { get; init; } = ProxyProtocol.Unknown;
    public AnonymityLevel Anonymity { get; init; } = AnonymityLevel.Unknown;
    public string Country { get; init; } = "Unknown";
    public int LatencyMs { get; init; }
    public int? ConnectMs { get; init; }
    public int? FirstByteMs { get; init; }
    public FailureReason Failure { get; init; } = FailureReason.None;
    public string? ErrorMessage { get; init; }
    public DateTimeOffset CheckedAt { get; init; } = DateTimeOffset.UtcNow;
    public string? ObservedSourceIp { get; init; }
    public ProxyAuthMethod AuthMethod { get; init; } = ProxyAuthMethod.None;
    public bool UsedRemoteDns { get; init; }
    public string? FakeIp { get; init; }
}

public sealed record ScrapeSourceResult
{
    public required string SourceUrl { get; init; }
    public bool Success { get; init; }
    public int RawCandidates { get; init; }
    public int ValidProxies { get; init; }
    public int NewUnique { get; init; }
    public int DurationMs { get; init; }
    public string? ErrorMessage { get; init; }
    public int? StatusCode { get; init; }
}

public sealed class ProgressSnapshot
{
    public SessionKind Kind { get; init; }
    public SessionStatus Status { get; init; }
    public int Completed { get; init; }
    public int Total { get; init; }
    public int Alive { get; init; }
    public int Elite { get; init; }
    public int Anonymous { get; init; }
    public int Transparent { get; init; }
    public int Socks4 { get; init; }
    public int Socks5 { get; init; }
    public int Socks4And5 { get; init; }
    public int UniqueProxies { get; init; }
    public string Message { get; init; } = "";
}
