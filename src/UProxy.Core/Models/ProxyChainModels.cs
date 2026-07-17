using UProxy.Core.Models;

namespace UProxy.Core.Models;

/// <summary>Wire protocol of a hop (separate from checker capability flags like Https CONNECT success).</summary>
public enum ProxyKind
{
    Http = 1,
    Socks4 = 2,
    Socks5 = 3
}

/// <summary>Transport used to reach the hop itself (TCP plain vs TLS to the proxy).</summary>
public enum ProxyTransport
{
    Tcp = 0,
    Tls = 1
}

[Flags]
public enum ProxyCapabilities
{
    None = 0,
    HttpForwarding = 1,
    Connect = 2,
    RemoteDns = 4,
    UdpAssociate = 8,
    IPv6Destination = 16
}

public enum ChainMode
{
    FastFailover = 0,
    StrictMultiHop = 1
}

/// <summary>Final TCP destination after all hop handshakes.</summary>
public sealed record ChainDestination(string Host, int Port)
{
    public override string ToString() =>
        Host.Contains(':') && !Host.StartsWith('[')
            ? $"[{Host}]:{Port}"
            : $"{Host}:{Port}";
}

/// <summary>One ordered hop in a chain.</summary>
public sealed record ProxyHop(
    Guid Id,
    ParsedProxy Proxy,
    ProxyKind Kind,
    ProxyTransport Transport = ProxyTransport.Tcp,
    ProxyCapabilities Capabilities = ProxyCapabilities.None,
    bool RemoteDns = true)
{
    public static ProxyHop FromParsed(
        ParsedProxy proxy,
        ProxyKind? kindOverride = null,
        bool remoteDns = true)
    {
        var kind = kindOverride ?? InferKind(proxy.Protocol);
        return new ProxyHop(
            Guid.NewGuid(),
            proxy,
            kind,
            ProxyTransport.Tcp,
            DefaultCapabilities(kind),
            remoteDns);
    }

    public static ProxyKind InferKind(ProxyProtocol protocol) => protocol switch
    {
        ProxyProtocol.Socks4 or ProxyProtocol.Socks4And5 => ProxyKind.Socks4,
        ProxyProtocol.Socks5 => ProxyKind.Socks5,
        ProxyProtocol.Http or ProxyProtocol.Https => ProxyKind.Http,
        _ => ProxyKind.Socks5
    };

    public static ProxyCapabilities DefaultCapabilities(ProxyKind kind) => kind switch
    {
        ProxyKind.Socks5 => ProxyCapabilities.Connect | ProxyCapabilities.RemoteDns | ProxyCapabilities.IPv6Destination,
        ProxyKind.Socks4 => ProxyCapabilities.Connect | ProxyCapabilities.RemoteDns,
        ProxyKind.Http => ProxyCapabilities.Connect | ProxyCapabilities.HttpForwarding,
        _ => ProxyCapabilities.None
    };

    public string Endpoint => Proxy.Endpoint;
}

/// <summary>Saved chain/pool profile shell (persistence filled in later phases).</summary>
public sealed record ProxyChainProfile(
    Guid Id,
    string Name,
    ChainMode Mode,
    IReadOnlyList<ProxyHop> Hops,
    string? CandidatePoolId = null);

/// <summary>High-level runtime status for an active chain/pool.</summary>
public enum ChainRuntimeState
{
    Stopped = 0,
    Healthy = 1,
    Degraded = 2,
    Switching = 3
}

/// <summary>Live health counters for a proxy (stable key: host|port|kind).</summary>
public sealed record ProxyHealthRecord(
    string Key,
    int SuccessCount,
    int FailureCount,
    double SuccessRate,
    bool IsHealthy,
    bool IsInCooldown,
    bool NeedsVerification,
    int CooldownLevel,
    DateTimeOffset? LastSuccessUtc,
    DateTimeOffset? LastFailureUtc,
    DateTimeOffset? CooldownUntilUtc);

/// <summary>JSON-serializable snapshot of per-proxy health for persistence.</summary>
public sealed record ProxyHealthState(
    string Key,
    int SuccessCount = 0,
    int FailureCount = 0,
    int CooldownLevel = 0,
    bool NeedsVerification = false,
    DateTimeOffset? LastSuccessUtc = null,
    DateTimeOffset? LastFailureUtc = null,
    DateTimeOffset? CooldownUntilUtc = null,
    List<DateTimeOffset>? RecentFailureUtc = null);

/// <summary>Pool entry used by selection policies (FastFailover / AutoTwoHop).</summary>
public sealed record PoolCandidate(
    ProxyHop Hop,
    string? Country = null,
    int? LatencyMs = null,
    double? SuccessRate = null,
    DateTimeOffset? LastChecked = null);

/// <summary>Result of testing whether two hops can form a working edge.</summary>
public sealed record TwoHopEdgeResult(
    bool Compatible,
    double Reliability = 1.0,
    int E2eLatencyMs = int.MaxValue);
