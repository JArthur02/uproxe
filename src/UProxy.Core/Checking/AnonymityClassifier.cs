using System.Net;
using System.Text.RegularExpressions;
using UProxy.Core.Models;

namespace UProxy.Core.Checking;

/// <summary>
/// Classifies anonymity from azenv-style judge bodies (REMOTE_ADDR + HTTP_* headers),
/// with corrected rules vs μProxy 1.81.
/// </summary>
public static partial class AnonymityClassifier
{
    private static readonly string[] ForwardingHeaders =
    [
        "HTTP_X_FORWARDED_FOR",
        "HTTP_X_FORWARDED",
        "HTTP_FORWARDED_FOR",
        "HTTP_FORWARDED",
        "HTTP_CLIENT_IP",
        "HTTP_X_CLIENT_IP",
        "HTTP_X_REAL_IP",
        "HTTP_X_CLUSTER_CLIENT_IP",
        "HTTP_CF_CONNECTING_IP",
        "HTTP_TRUE_CLIENT_IP"
    ];

    private static readonly string[] ProxyMarkerHeaders =
    [
        "HTTP_VIA",
        "HTTP_X_PROXY_ID",
        "HTTP_PROXY_CONNECTION",
        "HTTP_X_PROXY_CONNECTION"
    ];

    // Ordinary headers that must not, by themselves, demote Elite → Anonymous.
    private static readonly HashSet<string> BenignHttpHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "HTTP_ACCEPT",
        "HTTP_ACCEPT_ENCODING",
        "HTTP_ACCEPT_LANGUAGE",
        "HTTP_CONNECTION",
        "HTTP_HOST",
        "HTTP_USER_AGENT",
        "HTTP_CACHE_CONTROL",
        "HTTP_UPGRADE_INSECURE_REQUESTS",
        "HTTP_COOKIE",
        "HTTP_REFERER",
        "HTTP_ORIGIN",
        "HTTP_TE",
        "HTTP_PRAGMA"
    };

    [GeneratedRegex(@"^(?<key>[A-Za-z0-9_]+)\s*=\s*(?<value>.*)$", RegexOptions.Multiline | RegexOptions.Compiled)]
    private static partial Regex HeaderLineRegex();

    [GeneratedRegex(@"REMOTE_ADDR\s*=\s*(?<ip>\S+)", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex RemoteAddrRegex();

    public static bool LooksLikeAzenv(string body) =>
        !string.IsNullOrWhiteSpace(body) &&
        (body.Contains("REMOTE_ADDR", StringComparison.OrdinalIgnoreCase) ||
         body.Contains("REMOTE_PORT", StringComparison.OrdinalIgnoreCase));

    public static AnonymityLevel Classify(string judgeBody, IPAddress? realClientIp = null)
    {
        if (!LooksLikeAzenv(judgeBody))
            return AnonymityLevel.Unknown;

        var headers = ParseHeaders(judgeBody);
        var remoteAddr = TryGetRemoteAddr(judgeBody, headers);

        // Transparent: original client IP appears in a forwarding header, or REMOTE_ADDR is the client
        // when also accompanied by forwarding headers that echo it.
        foreach (var name in ForwardingHeaders)
        {
            if (!headers.TryGetValue(name, out var value))
                continue;

            if (realClientIp is not null && ContainsIp(value, realClientIp))
                return AnonymityLevel.Transparent;

            // Even without knowing the client IP, presence of client-IP forwarding headers
            // is treated as Transparent (proxy is disclosing a client identity).
            if (LooksLikeIpList(value))
                return AnonymityLevel.Transparent;
        }

        if (realClientIp is not null && remoteAddr is not null && IpEquals(remoteAddr, realClientIp))
        {
            // Judge saw the user directly — not a useful anonymizing proxy.
            return AnonymityLevel.Transparent;
        }

        // Anonymous: original IP not exposed, but proxy-identifying headers present.
        foreach (var name in ProxyMarkerHeaders)
        {
            if (headers.ContainsKey(name))
                return AnonymityLevel.Anonymous;
        }

        // Elite: no original IP leak and no proxy-identifying headers beyond ordinary request headers.
        return AnonymityLevel.Elite;
    }

    public static IReadOnlyDictionary<string, string> ParseHeaders(string body)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in HeaderLineRegex().Matches(body))
        {
            var key = m.Groups["key"].Value.Trim();
            var value = m.Groups["value"].Value.Trim();
            if (key.Length == 0)
                continue;
            map[key] = value;
        }
        return map;
    }

    public static IPAddress? TryGetRemoteAddr(string body, IReadOnlyDictionary<string, string>? headers = null)
    {
        headers ??= ParseHeaders(body);
        if (headers.TryGetValue("REMOTE_ADDR", out var v) && IPAddress.TryParse(v.Trim(), out var ip))
            return ip;

        var m = RemoteAddrRegex().Match(body);
        if (m.Success && IPAddress.TryParse(m.Groups["ip"].Value.Trim(), out ip))
            return ip;

        return null;
    }

    private static bool ContainsIp(string value, IPAddress target)
    {
        foreach (var part in value.Split([',', ' ', ';', '\t'], StringSplitOptions.RemoveEmptyEntries))
        {
            var token = part.Trim();
            if (IPAddress.TryParse(token, out var ip) && IpEquals(ip, target))
                return true;
        }
        return false;
    }

    private static bool LooksLikeIpList(string value)
    {
        foreach (var part in value.Split([',', ' ', ';', '\t'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (IPAddress.TryParse(part.Trim(), out _))
                return true;
        }
        return false;
    }

    private static bool IpEquals(IPAddress a, IPAddress b)
    {
        if (a.Equals(b))
            return true;
        // Compare IPv4-mapped IPv6 forms
        var a4 = a.IsIPv4MappedToIPv6 ? a.MapToIPv4() : a;
        var b4 = b.IsIPv4MappedToIPv6 ? b.MapToIPv4() : b;
        return a4.Equals(b4);
    }

    // Exposed for tests / diagnostics — not used in Classify path currently.
    public static bool IsBenignHeader(string name) => BenignHttpHeaders.Contains(name);
}
