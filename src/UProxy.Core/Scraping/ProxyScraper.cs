using System.Net;
using UProxy.Core.Config;
using UProxy.Core.Models;
using UProxy.Core.Parsing;

namespace UProxy.Core.Scraping;

public sealed class SourceLoader
{
    public IReadOnlyList<string> LoadUrls(string path)
    {
        if (!File.Exists(path))
            return [];

        var urls = new List<string>();
        var lines = File.ReadAllLines(path);
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (line.Length == 0)
                continue;
            if (line.StartsWith('#') || line.StartsWith(';') || line.StartsWith("//"))
                continue;

            // Legacy range expansion: url[1-3] → url1, url2, url3 (bounded)
            if (TryExpandRange(line, out var expanded))
            {
                urls.AddRange(expanded);
                continue;
            }

            if (Uri.TryCreate(line, UriKind.Absolute, out var uri) &&
                (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
            {
                urls.Add(uri.ToString());
            }
        }

        return urls;
    }

    private static bool TryExpandRange(string line, out List<string> expanded)
    {
        expanded = [];
        var start = line.IndexOf('[');
        var end = line.IndexOf(']');
        if (start < 0 || end <= start)
            return false;

        var inside = line[(start + 1)..end];
        var dash = inside.IndexOf('-');
        if (dash < 0)
            return false;

        if (!int.TryParse(inside[..dash], out var from) ||
            !int.TryParse(inside[(dash + 1)..], out var to))
            return false;

        if (to < from)
            (from, to) = (to, from);

        if (to - from > 50)
            return false; // hard cap

        var prefix = line[..start];
        var suffix = line[(end + 1)..];
        for (var n = from; n <= to; n++)
        {
            var candidate = prefix + n + suffix;
            if (Uri.TryCreate(candidate, UriKind.Absolute, out var uri) &&
                (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
            {
                expanded.Add(uri.ToString());
            }
        }

        return expanded.Count > 0;
    }
}

public sealed class ProxyScraper
{
    private readonly AppSettings _settings;
    private readonly HttpMessageHandler? _handlerOverride;

    public ProxyScraper(AppSettings settings, HttpMessageHandler? handlerOverride = null)
    {
        _settings = settings;
        _handlerOverride = handlerOverride;
    }

    public async Task<ScrapeSourceResult> ScrapeAsync(
        string sourceUrl,
        ProxyProtocol defaultProtocol,
        ISet<string> knownKeys,
        CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            using var handler = _handlerOverride ?? new SocketsHttpHandler
            {
                AutomaticDecompression = DecompressionMethods.All,
                AllowAutoRedirect = true,
                UseProxy = false
            };
            using var client = new HttpClient(handler, disposeHandler: _handlerOverride is null)
            {
                Timeout = TimeSpan.FromMilliseconds(Math.Max(_settings.TimeoutMs, 12_000))
            };
            client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", _settings.UserAgent);

            using var response = await client.GetAsync(sourceUrl, ct).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            sw.Stop();

            if (!response.IsSuccessStatusCode)
            {
                return new ScrapeSourceResult
                {
                    SourceUrl = sourceUrl,
                    Success = false,
                    StatusCode = (int)response.StatusCode,
                    DurationMs = (int)sw.ElapsedMilliseconds,
                    ErrorMessage = $"HTTP {(int)response.StatusCode}"
                };
            }

            var parsed = ProxyParser.ExtractFromText(body, defaultProtocol);
            var newUnique = 0;
            foreach (var p in parsed)
            {
                if (knownKeys.Add(p.Key))
                    newUnique++;
            }

            return new ScrapeSourceResult
            {
                SourceUrl = sourceUrl,
                Success = true,
                StatusCode = (int)response.StatusCode,
                RawCandidates = parsed.Count,
                ValidProxies = parsed.Count,
                NewUnique = newUnique,
                DurationMs = (int)sw.ElapsedMilliseconds
            };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return new ScrapeSourceResult
            {
                SourceUrl = sourceUrl,
                Success = false,
                DurationMs = (int)sw.ElapsedMilliseconds,
                ErrorMessage = "Cancelled"
            };
        }
        catch (Exception ex)
        {
            sw.Stop();
            return new ScrapeSourceResult
            {
                SourceUrl = sourceUrl,
                Success = false,
                DurationMs = (int)sw.ElapsedMilliseconds,
                ErrorMessage = ex.Message
            };
        }
    }
}
