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
            await writer.WriteLineAsync("proxy,protocol,anonymity,country,latency_ms,auth,remote_dns,checked_at_utc,failure").ConfigureAwait(false);
            foreach (var r in filtered)
            {
                ct.ThrowIfCancellationRequested();
                var line = string.Join(',',
                    Csv(r.Proxy.ToExportString(filter.IncludeCredentials)),
                    Csv(r.ConfirmedProtocol.ToString()),
                    Csv(r.Anonymity.ToString()),
                    Csv(r.Country),
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

    private static string Csv(string? value)
    {
        value ??= "";
        if (value.Contains('"') || value.Contains(',') || value.Contains('\n'))
            return "\"" + value.Replace("\"", "\"\"") + "\"";
        return value;
    }
}
