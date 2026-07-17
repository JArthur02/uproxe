using System.Text;
using System.Text.Json;
using UProxy.Core.Models;

namespace UProxy.Core.Exporting;

public sealed class ExportFilter
{
    public HashSet<AnonymityLevel> AnonymityLevels { get; init; } = [];
    public HashSet<ProxyProtocol> Protocols { get; init; } = [];
    public HashSet<string> Countries { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public bool FilterByCountry { get; init; }
    public bool IncludeCredentials { get; init; }
    public bool AliveOnly { get; init; } = true;
}

public static class ProxyExporter
{
    public static IEnumerable<ProxyCheckResult> Filter(IEnumerable<ProxyCheckResult> results, ExportFilter filter)
    {
        foreach (var r in results)
        {
            if (filter.AliveOnly && !r.IsAlive)
                continue;
            if (filter.AnonymityLevels.Count > 0 && !filter.AnonymityLevels.Contains(r.Anonymity))
                continue;
            if (filter.Protocols.Count > 0 && !filter.Protocols.Contains(r.ConfirmedProtocol))
                continue;
            if (filter.FilterByCountry && filter.Countries.Count > 0 &&
                !filter.Countries.Contains(r.Country))
                continue;
            yield return r;
        }
    }

    public static async Task<int> WritePlainAsync(
        string path,
        IEnumerable<ProxyCheckResult> results,
        ExportFilter filter,
        CancellationToken ct = default)
    {
        var filtered = Filter(results, filter).ToList();
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        var tmp = path + ".tmp";
        await using (var writer = new StreamWriter(tmp, false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
        {
            foreach (var r in filtered)
            {
                ct.ThrowIfCancellationRequested();
                await writer.WriteLineAsync(r.Proxy.ToExportString(filter.IncludeCredentials)).ConfigureAwait(false);
            }
        }

        File.Move(tmp, path, overwrite: true);
        return filtered.Count;
    }

    public static async Task<int> WriteCsvAsync(
        string path,
        IEnumerable<ProxyCheckResult> results,
        ExportFilter filter,
        CancellationToken ct = default)
    {
        var filtered = Filter(results, filter).ToList();
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        var tmp = path + ".tmp";
        await using (var writer = new StreamWriter(tmp, false, new UTF8Encoding(false)))
        {
            await writer.WriteLineAsync("proxy,protocol,anonymity,country,connect_ms,latency_ms,auth,remote_dns,checked_at_utc,failure").ConfigureAwait(false);
            foreach (var r in filtered)
            {
                ct.ThrowIfCancellationRequested();
                var line = string.Join(',',
                    Csv(r.Proxy.ToExportString(filter.IncludeCredentials)),
                    Csv(r.ConfirmedProtocol.ToString()),
                    Csv(r.Anonymity.ToString()),
                    Csv(r.Country),
                    (r.ConnectMs ?? 0).ToString(),
                    r.LatencyMs.ToString(),
                    Csv(r.AuthMethod.ToString()),
                    r.UsedRemoteDns ? "1" : "0",
                    Csv(r.CheckedAt.UtcDateTime.ToString("o")),
                    Csv(r.Failure.ToString()));
                await writer.WriteLineAsync(line).ConfigureAwait(false);
            }
        }

        File.Move(tmp, path, overwrite: true);
        return filtered.Count;
    }

    public static async Task<int> WriteJsonAsync(
        string path,
        IEnumerable<ProxyCheckResult> results,
        ExportFilter filter,
        CancellationToken ct = default)
    {
        var filtered = Filter(results, filter).Select(r => new
        {
            proxy = r.Proxy.ToExportString(filter.IncludeCredentials),
            protocol = r.ConfirmedProtocol.ToString(),
            anonymity = r.Anonymity.ToString(),
            country = r.Country,
            connectMs = r.ConnectMs,
            latencyMs = r.LatencyMs,
            auth = r.AuthMethod.ToString(),
            usedRemoteDns = r.UsedRemoteDns,
            fakeIp = r.FakeIp,
            checkedAtUtc = r.CheckedAt,
            failure = r.Failure.ToString(),
            alive = r.IsAlive
        }).ToList();

        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        var tmp = path + ".tmp";
        await using (var stream = File.Create(tmp))
        {
            await JsonSerializer.SerializeAsync(stream, filtered, new JsonSerializerOptions { WriteIndented = true }, ct)
                .ConfigureAwait(false);
        }

        File.Move(tmp, path, overwrite: true);
        return filtered.Count;
    }

    /// <summary>
    /// Writes a ready-to-use proxychains-ng 4.x configuration. HTTPS-capable HTTP
    /// proxies use proxychains' <c>http</c> type, while dual SOCKS proxies prefer SOCKS5.
    /// </summary>
    public static async Task<int> WriteProxyChainsAsync(
        string path,
        IEnumerable<ProxyCheckResult> results,
        ExportFilter filter,
        CancellationToken ct = default)
    {
        var filtered = Filter(results, filter)
            .Where(r => ToProxyChainsType(r.ConfirmedProtocol) is not null)
            .ToList();
        if (filter.IncludeCredentials)
        {
            foreach (var proxy in filtered.Select(r => r.Proxy).Where(p => !string.IsNullOrEmpty(p.Username)))
            {
                EnsureProxyChainsToken(proxy.Username!, "username");
                EnsureProxyChainsToken(proxy.Password ?? "", "password");
            }
        }
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        var tmp = path + ".tmp";
        await using (var writer = new StreamWriter(tmp, false, new UTF8Encoding(false)))
        {
            await writer.WriteLineAsync("# Generated by uProxy Tool for proxychains-ng 4.x").ConfigureAwait(false);
            await writer.WriteLineAsync("dynamic_chain").ConfigureAwait(false);
            await writer.WriteLineAsync("proxy_dns").ConfigureAwait(false);
            await writer.WriteLineAsync("remote_dns_subnet 224").ConfigureAwait(false);
            await writer.WriteLineAsync("tcp_read_time_out 15000").ConfigureAwait(false);
            await writer.WriteLineAsync("tcp_connect_time_out 8000").ConfigureAwait(false);
            await writer.WriteLineAsync().ConfigureAwait(false);
            await writer.WriteLineAsync("[ProxyList]").ConfigureAwait(false);

            foreach (var result in filtered)
            {
                ct.ThrowIfCancellationRequested();
                var proxy = result.Proxy;
                var fields = new List<string>
                {
                    ToProxyChainsType(result.ConfirmedProtocol)!,
                    proxy.Host,
                    proxy.Port.ToString(System.Globalization.CultureInfo.InvariantCulture)
                };

                if (filter.IncludeCredentials && !string.IsNullOrEmpty(proxy.Username))
                {
                    fields.Add(proxy.Username);
                    fields.Add(proxy.Password ?? "");
                }

                await writer.WriteLineAsync(string.Join('\t', fields)).ConfigureAwait(false);
            }
        }

        File.Move(tmp, path, overwrite: true);
        return filtered.Count;
    }

    private static string? ToProxyChainsType(ProxyProtocol protocol) => protocol switch
    {
        ProxyProtocol.Http or ProxyProtocol.Https => "http",
        ProxyProtocol.Socks4 => "socks4",
        ProxyProtocol.Socks5 or ProxyProtocol.Socks4And5 => "socks5",
        _ => null
    };

    private static void EnsureProxyChainsToken(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Any(char.IsWhiteSpace))
            throw new InvalidOperationException($"Proxychains {name}s cannot be empty or contain whitespace.");
    }

    private static string Csv(string? value)
    {
        value ??= "";
        if (value.Contains('"') || value.Contains(',') || value.Contains('\n'))
            return "\"" + value.Replace("\"", "\"\"") + "\"";
        return value;
    }
}
