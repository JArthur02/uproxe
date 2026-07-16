using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

namespace UProxy.Core.Dns;

/// <summary>
/// Proxifier-style Fake-IP DNS helper (checker-side).
/// When hostnames are resolved through a proxy, the real A/AAAA record is unavailable
/// locally, so we assign a placeholder in 127.8.0.0/16 that is only meaningful on this machine.
/// Reverse-mapping lets SOCKS/HTTP CONNECT send the original hostname (SOCKS4a / SOCKS5 ATYP=domain).
/// </summary>
public sealed class FakeIpDns
{
    // Proxifier uses 127.8.*.* placeholders (see Proxifier Name Resolution docs).
    private static readonly byte[] FakePrefix = [127, 8];

    private readonly ConcurrentDictionary<string, IPAddress> _hostToIp = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<IPAddress, string> _ipToHost = new();
    private int _next;

    public IPAddress Allocate(string hostname)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hostname);
        hostname = hostname.Trim().TrimEnd('.');

        return _hostToIp.GetOrAdd(hostname, _ =>
        {
            while (true)
            {
                var n = Interlocked.Increment(ref _next);
                // 127.8.0.1 .. 127.8.255.254 — skip .0 and .255 in each /24-ish slot
                var b2 = (n / 254) % 256;
                var b3 = (n % 254) + 1;
                var ip = new IPAddress(new byte[] { FakePrefix[0], FakePrefix[1], (byte)b2, (byte)b3 });
                if (_ipToHost.TryAdd(ip, hostname))
                    return ip;
            }
        });
    }

    public bool TryGetHostname(IPAddress ip, out string? hostname)
    {
        if (_ipToHost.TryGetValue(Normalize(ip), out var host))
        {
            hostname = host;
            return true;
        }

        hostname = null;
        return false;
    }

    public bool TryGetHostname(string hostOrIp, out string? hostname)
    {
        hostname = null;
        if (IPAddress.TryParse(hostOrIp.Trim().TrimStart('[').TrimEnd(']'), out var ip))
            return TryGetHostname(ip, out hostname);
        return false;
    }

    public static bool IsFakeIp(IPAddress ip)
    {
        ip = Normalize(ip);
        if (ip.AddressFamily != AddressFamily.InterNetwork)
            return false;
        var b = ip.GetAddressBytes();
        return b[0] == FakePrefix[0] && b[1] == FakePrefix[1];
    }

    public static bool IsFakeIp(string hostOrIp) =>
        IPAddress.TryParse(hostOrIp.Trim().TrimStart('[').TrimEnd(']'), out var ip) && IsFakeIp(ip);

    /// <summary>
    /// Returns the hostname that should be sent to the proxy for CONNECT.
    /// Fake IPs are reversed; real IPs stay as-is; hostnames pass through.
    /// </summary>
    public string ResolveDestinationForProxy(string hostOrIp, bool resolveHostnamesThroughProxy)
    {
        var trimmed = hostOrIp.Trim().TrimStart('[').TrimEnd(']');
        if (IPAddress.TryParse(trimmed, out var ip))
        {
            if (TryGetHostname(ip, out var mapped) && mapped is not null)
                return mapped;
            return trimmed;
        }

        if (resolveHostnamesThroughProxy)
            return trimmed; // send domain to proxy (SOCKS4a / SOCKS5 domain / HTTP CONNECT host)

        // Local resolve path: caller may still want a fake IP for bookkeeping
        return trimmed;
    }

    public void Clear()
    {
        _hostToIp.Clear();
        _ipToHost.Clear();
        Interlocked.Exchange(ref _next, 0);
    }

    private static IPAddress Normalize(IPAddress ip) =>
        ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip;
}
