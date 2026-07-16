using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using UProxy.Core.Models;

namespace UProxy.Core.Parsing;

public static partial class ProxyParser
{
    [GeneratedRegex(@"\b((?:\d{1,3}\.){3}\d{1,3}):(\d{1,5})\b", RegexOptions.Compiled)]
    private static partial Regex Ipv4PortRegex();

    [GeneratedRegex(@"\[([0-9a-fA-F:]+)\]:(\d{1,5})", RegexOptions.Compiled)]
    private static partial Regex BracketedIpv6Regex();

    [GeneratedRegex(@"^(?<scheme>https?|socks4a?|socks5h?)://(?:(?<user>[^:@/\s]+):(?<pass>[^@/\s]*)@)?(?<host>\[[^\]]+\]|[^:/\[\]\s]+):(?<port>\d{1,5})$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex SchemeRegex();

    [GeneratedRegex(@"^(?:(?<user>[^:@/\s]+):(?<pass>[^@/\s]*)@)?(?<host>\[[^\]]+\]|[^:/\[\]\s]+):(?<port>\d{1,5})$",
        RegexOptions.Compiled)]
    private static partial Regex AuthHostPortRegex();

    public static bool TryParse(string? input, out ParsedProxy? proxy, ProxyProtocol defaultProtocol = ProxyProtocol.Unknown)
    {
        proxy = null;
        if (string.IsNullOrWhiteSpace(input))
            return false;

        var text = input.Trim().Trim(',', ';', '"', '\'');
        if (text.Length < 7)
            return false;

        var schemeMatch = SchemeRegex().Match(text);
        if (schemeMatch.Success)
        {
            var protocol = ParseScheme(schemeMatch.Groups["scheme"].Value);
            return TryBuild(
                schemeMatch.Groups["host"].Value,
                schemeMatch.Groups["port"].Value,
                protocol,
                NullIfEmpty(schemeMatch.Groups["user"].Value),
                NullIfEmpty(schemeMatch.Groups["pass"].Value),
                out proxy);
        }

        var authMatch = AuthHostPortRegex().Match(text);
        if (authMatch.Success)
        {
            return TryBuild(
                authMatch.Groups["host"].Value,
                authMatch.Groups["port"].Value,
                defaultProtocol,
                NullIfEmpty(authMatch.Groups["user"].Value),
                NullIfEmpty(authMatch.Groups["pass"].Value),
                out proxy);
        }

        return false;
    }

    public static IReadOnlyList<ParsedProxy> ExtractFromText(string? text, ProxyProtocol defaultProtocol = ProxyProtocol.Unknown)
    {
        if (string.IsNullOrEmpty(text))
            return [];

        var results = new Dictionary<string, ParsedProxy>(StringComparer.OrdinalIgnoreCase);

        foreach (Match m in BracketedIpv6Regex().Matches(text))
        {
            if (TryBuild(m.Groups[1].Value, m.Groups[2].Value, defaultProtocol, null, null, out var p) && p is not null)
                results.TryAdd(p.Key, p);
        }

        foreach (Match m in Ipv4PortRegex().Matches(text))
        {
            if (TryBuild(m.Groups[1].Value, m.Groups[2].Value, defaultProtocol, null, null, out var p) && p is not null)
                results.TryAdd(p.Key, p);
        }

        // Also try line-oriented scheme / auth forms
        foreach (var line in text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith('#') || trimmed.StartsWith(';') || trimmed.StartsWith("//"))
                continue;
            if (TryParse(trimmed, out var p, defaultProtocol) && p is not null)
                results.TryAdd(p.Key, p);
        }

        return results.Values.ToList();
    }

    private static bool TryBuild(
        string hostRaw,
        string portRaw,
        ProxyProtocol protocol,
        string? user,
        string? pass,
        out ParsedProxy? proxy)
    {
        proxy = null;
        var host = hostRaw.Trim().TrimStart('[').TrimEnd(']');
        if (!IsValidHost(host, out var normalized))
            return false;

        if (!int.TryParse(portRaw, out var port) || port is < 1 or > 65535)
            return false;

        proxy = new ParsedProxy(normalized, port, protocol, user, pass);
        return true;
    }

    private static bool IsValidHost(string host, out string normalized)
    {
        normalized = host;
        if (string.IsNullOrWhiteSpace(host) || host.Length > 253)
            return false;

        if (IPAddress.TryParse(host, out var ip))
        {
            if (ip.AddressFamily is not (AddressFamily.InterNetwork or AddressFamily.InterNetworkV6))
                return false;

            if (!IPAddress.IsLoopback(ip) && IsUnusable(ip))
                return false;

            normalized = ip.AddressFamily == AddressFamily.InterNetwork ? ip.ToString() : host;
            return true;
        }

        if (host.Contains("..", StringComparison.Ordinal) ||
            host.StartsWith('.') ||
            host.EndsWith('.'))
            return false;

        var labels = host.Split('.');
        if (labels.Length < 2)
            return false;

        foreach (var label in labels)
        {
            if (label.Length is < 1 or > 63)
                return false;
            if (label.StartsWith('-') || label.EndsWith('-'))
                return false;
            if (label.Any(ch => !char.IsAsciiLetterOrDigit(ch) && ch != '-'))
                return false;
        }

        // A hostname's top-level label must not be all-numeric; otherwise a malformed
        // dotted-quad that failed IP parsing (e.g. 256.1.1.1 / 999.1.1.1) would be
        // mistaken for a valid host.
        if (labels[^1].All(char.IsAsciiDigit))
            return false;

        normalized = host.ToLowerInvariant();
        return true;
    }

    private static bool IsUnusable(IPAddress ip)
    {
        var bytes = ip.GetAddressBytes();
        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            // 0.0.0.0, multicast 224+, broadcast-ish
            if (bytes[0] == 0)
                return true;
            if (bytes[0] >= 224)
                return true;
            // octet sanity already enforced by IPAddress.TryParse for values >255
        }
        return ip.Equals(IPAddress.None) || ip.Equals(IPAddress.Broadcast);
    }

    private static ProxyProtocol ParseScheme(string scheme) => scheme.ToLowerInvariant() switch
    {
        "http" => ProxyProtocol.Http,
        "https" => ProxyProtocol.Https,
        "socks4" or "socks4a" => ProxyProtocol.Socks4,
        "socks5" or "socks5h" => ProxyProtocol.Socks5,
        _ => ProxyProtocol.Unknown
    };

    private static string? NullIfEmpty(string? s) => string.IsNullOrEmpty(s) ? null : s;
}
