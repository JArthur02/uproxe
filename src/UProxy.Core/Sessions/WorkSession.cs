using System.Threading.Channels;
using UProxy.Core.Checking;
using UProxy.Core.Config;
using UProxy.Core.GeoIp;
using UProxy.Core.Models;
using UProxy.Core.Parsing;

namespace UProxy.Core.Sessions;

public sealed class WorkSession : IAsyncDisposable
{
    private readonly AppSettings _settings;
    private readonly IGeoIpResolver _geoIp;
    private CancellationTokenSource? _cts;
    private Task? _runner;
    private readonly object _proxyGate = new();
    private readonly object _resultsGate = new();

    public SessionKind Kind { get; private set; } = SessionKind.Idle;
    public SessionStatus Status { get; private set; } = SessionStatus.Idle;

    public List<ParsedProxy> Proxies { get; } = [];
    public List<ProxyCheckResult> Results { get; } = [];

    public event Action<ProgressSnapshot>? ProgressChanged;
    public event Action<ProxyCheckResult>? ResultAdded;
    public event Action<ScrapeSourceResult>? SourceCompleted;

    private int _completed;
    private int _total;
    private int _elite;
    private int _anon;
    private int _transparent;
    private int _socks4;
    private int _socks5;
    private int _socks45;

    public WorkSession(AppSettings settings, IGeoIpResolver? geoIp = null)
    {
        _settings = settings;
        _geoIp = geoIp ?? NullGeoIpResolver.Instance;
    }

    public void ClearProxies()
    {
        lock (_proxyGate)
            Proxies.Clear();
    }

    public void ClearResults()
    {
        lock (_resultsGate)
            Results.Clear();
    }

    public int LoadProxiesFromText(string text, ProxyProtocol defaultProtocol = ProxyProtocol.Unknown)
    {
        var parsed = ProxyParser.ExtractFromText(text, defaultProtocol);
        var added = 0;
        lock (_proxyGate)
        {
            var keys = new HashSet<string>(Proxies.Select(p => p.Key), StringComparer.OrdinalIgnoreCase);
            foreach (var p in parsed)
            {
                if (keys.Add(p.Key))
                {
                    Proxies.Add(p);
                    added++;
                }
            }
        }
        return added;
    }

    public Task StartScrapeAsync(IReadOnlyList<string> sourceUrls, bool socksMode) =>
        RunAsync(SessionKind.Scraping, async ct =>
        {
            _completed = 0;
            _total = sourceUrls.Count;
            var protocol = socksMode ? ProxyProtocol.Socks5 : ProxyProtocol.Http;
            HashSet<string> known;
            lock (_proxyGate)
                known = new HashSet<string>(Proxies.Select(p => p.Key), StringComparer.OrdinalIgnoreCase);

            var channel = Channel.CreateBounded<string>(new BoundedChannelOptions(Math.Max(8, _settings.Concurrency))
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleWriter = true
            });

            using var handler = new System.Net.Http.SocketsHttpHandler
            {
                AutomaticDecompression = System.Net.DecompressionMethods.All,
                AllowAutoRedirect = true,
                UseProxy = false,
                PooledConnectionLifetime = TimeSpan.FromMinutes(5)
            };
            using var client = new HttpClient(handler)
            {
                Timeout = TimeSpan.FromMilliseconds(Math.Max(_settings.TimeoutMs, 12_000))
            };
            client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", UProxy.Core.Config.UserAgents.AsciiSafe(_settings.UserAgent));

            var producer = Task.Run(async () =>
            {
                foreach (var url in sourceUrls)
                    await channel.Writer.WriteAsync(url, ct).ConfigureAwait(false);
                channel.Writer.Complete();
            }, ct);

            var workerCount = Math.Clamp(_settings.Concurrency / 2, 1, 32);
            var workers = Enumerable.Range(0, workerCount).Select(_ => Task.Run(async () =>
            {
                await foreach (var url in channel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
                {
                    var (result, proxies) = await FetchAndParseAsync(client, url, protocol, ct).ConfigureAwait(false);
                    var newUnique = 0;
                    lock (_proxyGate)
                    {
                        foreach (var p in proxies)
                        {
                            if (known.Add(p.Key))
                            {
                                Proxies.Add(p);
                                newUnique++;
                            }
                        }
                    }

                    SourceCompleted?.Invoke(result with { NewUnique = newUnique });
                    Interlocked.Increment(ref _completed);
                    PublishProgress(
                        $"Status: Scraper {Status} | Source [{_completed}/{_total}] | Proxies: {ProxyCount}");
                }
            }, ct)).ToArray();

            await producer.ConfigureAwait(false);
            await Task.WhenAll(workers).ConfigureAwait(false);
        });

    public Task StartCheckAsync(bool socksMode) =>
        RunAsync(SessionKind.Checking, async ct =>
        {
            ClearResults();
            _completed = 0;
            _elite = _anon = _transparent = _socks4 = _socks5 = _socks45 = 0;
            ParsedProxy[] snapshot;
            lock (_proxyGate)
                snapshot = Proxies.ToArray();
            _total = snapshot.Length;

            var checker = new ProxyChecker(_settings, _geoIp);
            await checker.EnsureClientIpAsync(ct).ConfigureAwait(false);

            await Parallel.ForEachAsync(
                snapshot,
                new ParallelOptions
                {
                    MaxDegreeOfParallelism = Math.Max(1, _settings.Concurrency),
                    CancellationToken = ct
                },
                async (proxy, token) =>
                {
                    var result = socksMode
                        ? await checker.CheckSocksAsync(proxy, token).ConfigureAwait(false)
                        : await checker.CheckHttpAsync(proxy, token).ConfigureAwait(false);

                    if (result.IsAlive)
                    {
                        switch (result.Anonymity)
                        {
                            case AnonymityLevel.Elite: Interlocked.Increment(ref _elite); break;
                            case AnonymityLevel.Anonymous: Interlocked.Increment(ref _anon); break;
                            case AnonymityLevel.Transparent: Interlocked.Increment(ref _transparent); break;
                        }

                        switch (result.ConfirmedProtocol)
                        {
                            case ProxyProtocol.Socks4: Interlocked.Increment(ref _socks4); break;
                            case ProxyProtocol.Socks5: Interlocked.Increment(ref _socks5); break;
                            case ProxyProtocol.Socks4And5: Interlocked.Increment(ref _socks45); break;
                        }

                        lock (_resultsGate)
                            Results.Add(result);

                        ResultAdded?.Invoke(result);
                    }

                    Interlocked.Increment(ref _completed);
                    PublishCheckProgress();
                }).ConfigureAwait(false);
        });

    public async Task StopAsync()
    {
        if (_cts is null)
            return;

        Status = SessionStatus.Stopping;
        try { await _cts.CancelAsync().ConfigureAwait(false); }
        catch { /* ignore */ }

        if (_runner is not null)
        {
            try { await _runner.ConfigureAwait(false); }
            catch { /* ignore */ }
        }

        _cts.Dispose();
        _cts = null;
        _runner = null;
        if (Status is SessionStatus.Stopping or SessionStatus.Running)
            Status = SessionStatus.Stopped;

        PublishProgress(Kind == SessionKind.Checking
            ? BuildCheckMessage()
            : $"Status: Scraper {Status} | Source [{_completed}/{_total}] | Proxies: {ProxyCount}");
    }

    private async Task RunAsync(SessionKind kind, Func<CancellationToken, Task> work)
    {
        await StopAsync().ConfigureAwait(false);
        Kind = kind;
        Status = SessionStatus.Running;
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        _runner = Task.Run(async () =>
        {
            try
            {
                await work(ct).ConfigureAwait(false);
                if (!ct.IsCancellationRequested)
                    Status = SessionStatus.Completed;
                else if (Status == SessionStatus.Running)
                    Status = SessionStatus.Stopped;
            }
            catch (OperationCanceledException)
            {
                Status = SessionStatus.Stopped;
            }
            catch
            {
                Status = SessionStatus.Stopped;
                throw;
            }
            finally
            {
                if (kind == SessionKind.Checking)
                    PublishCheckProgress();
                else
                    PublishProgress(
                        $"Status: Scraper {Status} | Source [{_completed}/{_total}] | Proxies: {ProxyCount}");
            }
        }, CancellationToken.None);

        await _runner.ConfigureAwait(false);
    }

    private int ProxyCount
    {
        get { lock (_proxyGate) return Proxies.Count; }
    }

    private void PublishCheckProgress() => PublishProgress(BuildCheckMessage());

    private string BuildCheckMessage()
    {
        if (_settings.ProxyTypeMode == 1)
        {
            var socksAlive = _socks4 + _socks5 + _socks45;
            return
                $"Status: Checker {Status} [{_completed}/{_total}] | Alive: {socksAlive} | Socks4/5: {_socks45} | Socks4: {_socks4} | Socks5: {_socks5}";
        }

        var httpAlive = _elite + _anon + _transparent;
        return
            $"Status: Checker {Status} [{_completed}/{_total}] | Alive: {httpAlive} | Elite: {_elite} | Anon: {_anon} | Transparent: {_transparent}";
    }

    private void PublishProgress(string message)
    {
        int alive;
        lock (_resultsGate) alive = Results.Count;

        ProgressChanged?.Invoke(new ProgressSnapshot
        {
            Kind = Kind,
            Status = Status,
            Completed = _completed,
            Total = _total,
            Alive = alive,
            Elite = _elite,
            Anonymous = _anon,
            Transparent = _transparent,
            Socks4 = _socks4,
            Socks5 = _socks5,
            Socks4And5 = _socks45,
            UniqueProxies = ProxyCount,
            Message = message
        });
    }

    private async Task<(ScrapeSourceResult Result, List<ParsedProxy> Proxies)> FetchAndParseAsync(
        HttpClient client,
        string url,
        ProxyProtocol protocol,
        CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            using var response = await client.GetAsync(url, ct).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            sw.Stop();
            var parsed = ProxyParser.ExtractFromText(body, protocol).ToList();
            return (new ScrapeSourceResult
            {
                SourceUrl = url,
                Success = response.IsSuccessStatusCode,
                StatusCode = (int)response.StatusCode,
                RawCandidates = parsed.Count,
                ValidProxies = parsed.Count,
                DurationMs = (int)sw.ElapsedMilliseconds,
                ErrorMessage = response.IsSuccessStatusCode ? null : $"HTTP {(int)response.StatusCode}"
            }, parsed);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return (new ScrapeSourceResult
            {
                SourceUrl = url,
                Success = false,
                DurationMs = (int)sw.ElapsedMilliseconds,
                ErrorMessage = "Cancelled"
            }, []);
        }
        catch (Exception ex)
        {
            sw.Stop();
            return (new ScrapeSourceResult
            {
                SourceUrl = url,
                Success = false,
                DurationMs = (int)sw.ElapsedMilliseconds,
                ErrorMessage = ex.Message
            }, []);
        }
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);
}
