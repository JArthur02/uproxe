using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using UProxy.Core.Config;
using UProxy.Core.Dns;
using UProxy.Core.GeoIp;
using UProxy.Core.Models;

namespace UProxy.Core.Checking;

public sealed class ProxyChecker
{
    private readonly AppSettings _settings;
    private readonly JudgeClient _judge;
    private readonly IGeoIpResolver _geoIp;
    private readonly FakeIpDns _fakeIpDns = new();
    private IPAddress? _clientIp;
    private bool _clientIpResolved;

    public ProxyChecker(AppSettings settings, IGeoIpResolver? geoIp = null, JudgeClient? judge = null)
    {
        _settings = settings;
        _geoIp = geoIp ?? NullGeoIpResolver.Instance;
        _judge = judge ?? new JudgeClient(settings);
    }

    public FakeIpDns FakeIpDns => _fakeIpDns;

    public async Task EnsureClientIpAsync(CancellationToken ct)
    {
        if (_clientIpResolved)
            return;
        _clientIp = await _judge.GetDirectClientIpAsync(ct).ConfigureAwait(false);
        _clientIpResolved = true;
    }

    public async Task<ProxyCheckResult> CheckHttpAsync(ParsedProxy proxy, CancellationToken ct)
    {
        await EnsureClientIpAsync(ct).ConfigureAwait(false);

        // Test 1 + Test 3 (Proxifier-style): a raw TCP connect to the proxy establishes whether the
        // proxy itself is reachable and yields a clean connect-latency independent of the judge fetch.
        var (proxyReachable, connectMs, connectFailure, connectError) =
            await MeasureProxyConnectAsync(proxy, ct).ConfigureAwait(false);

        if (!proxyReachable)
        {
            return new ProxyCheckResult
            {
                Proxy = proxy,
                IsAlive = false,
                Failure = connectFailure,
                ErrorMessage = connectError,
                LatencyMs = connectMs,
                ConnectMs = connectMs
            };
        }

        var sw = Stopwatch.StartNew();
        var (body, failure, error, auth) = await _judge.FetchThroughHttpProxyAsync(proxy, ct).ConfigureAwait(false);
        sw.Stop();

        if (body is null || failure != FailureReason.None)
        {
            // The proxy answered TCP but the judge request failed. If the failure is a
            // connection-establishment problem, it is the proxy → target hop that broke,
            // not the proxy itself (which we already reached).
            var mapped = failure is FailureReason.ConnectRefused or FailureReason.ConnectTimeout
                ? FailureReason.TargetUnreachableThroughProxy
                : failure;

            return new ProxyCheckResult
            {
                Proxy = proxy,
                IsAlive = false,
                Failure = mapped,
                ErrorMessage = error,
                LatencyMs = (int)sw.ElapsedMilliseconds,
                ConnectMs = connectMs,
                AuthMethod = auth
            };
        }

        var anonymity = AnonymityClassifier.Classify(body, _clientIp);
        var observed = AnonymityClassifier.TryGetRemoteAddr(body);
        var country = _geoIp.LookupCountry(proxy.Host);

        var protocol = ProxyProtocol.Http;
        var (httpsOk, _, _) = await _judge.ProbeHttpsAsync(proxy, ct).ConfigureAwait(false);
        if (httpsOk)
            protocol = ProxyProtocol.Https;

        return new ProxyCheckResult
        {
            Proxy = proxy,
            IsAlive = true,
            ConfirmedProtocol = protocol,
            Anonymity = anonymity,
            Country = country,
            LatencyMs = (int)Math.Min(sw.ElapsedMilliseconds, _settings.TimeoutMs),
            ConnectMs = connectMs,
            ObservedSourceIp = observed?.ToString(),
            Failure = FailureReason.None,
            AuthMethod = auth
        };
    }

    /// <summary>
    /// Test 1 (connection to the proxy) + Test 3 (proxy latency): raw TCP connect to the proxy
    /// endpoint, returning the round-trip connect time and, on failure, why the proxy is unreachable.
    /// </summary>
    private async Task<(bool Ok, int ConnectMs, FailureReason Failure, string? Error)> MeasureProxyConnectAsync(
        ParsedProxy proxy, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(Math.Min(_settings.ConnectTimeoutMs, _settings.TimeoutMs));
        var sw = Stopwatch.StartNew();
        try
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp)
            {
                NoDelay = true
            };

            if (!IPAddress.TryParse(proxy.Host, out var proxyIp))
            {
                var addrs = await System.Net.Dns.GetHostAddressesAsync(proxy.Host, cts.Token).ConfigureAwait(false);
                proxyIp = addrs.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork)
                          ?? addrs.FirstOrDefault()
                          ?? throw new SocketException((int)SocketError.HostNotFound);
            }

            await socket.ConnectAsync(new IPEndPoint(proxyIp, proxy.Port), cts.Token).ConfigureAwait(false);
            sw.Stop();
            var ms = (int)Math.Round(sw.Elapsed.TotalMilliseconds);
            if (ms == 0 && sw.ElapsedTicks > 0)
                ms = 1;
            return (true, ms, FailureReason.None, null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return (false, (int)sw.ElapsedMilliseconds, FailureReason.Cancelled, "Cancelled");
        }
        catch (OperationCanceledException)
        {
            return (false, (int)sw.ElapsedMilliseconds, FailureReason.ConnectTimeout,
                "Attempt to connect to the proxy timed out.");
        }
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionRefused)
        {
            return (false, (int)sw.ElapsedMilliseconds, FailureReason.ConnectRefused, ex.Message);
        }
        catch (SocketException ex) when (ex.SocketErrorCode is SocketError.TimedOut or SocketError.TryAgain)
        {
            return (false, (int)sw.ElapsedMilliseconds, FailureReason.ConnectTimeout, ex.Message);
        }
        catch (SocketException ex) when (ex.SocketErrorCode is SocketError.HostNotFound or SocketError.NoData)
        {
            return (false, (int)sw.ElapsedMilliseconds, FailureReason.DnsFailure, ex.Message);
        }
        catch (Exception ex)
        {
            return (false, (int)sw.ElapsedMilliseconds, FailureReason.ConnectRefused, ex.Message);
        }
    }

    public async Task<ProxyCheckResult> CheckSocksAsync(ParsedProxy proxy, CancellationToken ct)
    {
        await EnsureClientIpAsync(ct).ConfigureAwait(false);

        var judgeUri = new Uri(_settings.JudgeUrl.Contains("://") ? _settings.JudgeUrl : "http://" + _settings.JudgeUrl);
        var destHost = judgeUri.Host;
        var destPort = judgeUri.IsDefaultPort ? 80 : judgeUri.Port;
        var path = string.IsNullOrEmpty(judgeUri.PathAndQuery) ? "/" : judgeUri.PathAndQuery;

        string? fakeIp = null;
        if (_settings.EnableFakeIpDns && _settings.ResolveHostnamesThroughProxy &&
            !IPAddress.TryParse(destHost, out _))
        {
            fakeIp = _fakeIpDns.Allocate(destHost).ToString();
        }

        var socksOpts = new SocksConnectOptions
        {
            UseSocks4a = _settings.UseSocks4a,
            ResolveHostnamesThroughProxy = _settings.ResolveHostnamesThroughProxy,
            FakeIpDns = _settings.EnableFakeIpDns ? _fakeIpDns : null
        };

        var socksDest = fakeIp ?? destHost;

        var socks4 = await SocksClient.ConnectAndHttpGetAsync(
            proxy, ProxyProtocol.Socks4, socksDest, destPort, path, _settings.TimeoutMs, ct, socksOpts).ConfigureAwait(false);
        var socks5 = await SocksClient.ConnectAndHttpGetAsync(
            proxy, ProxyProtocol.Socks5, socksDest, destPort, path, _settings.TimeoutMs, ct, socksOpts).ConfigureAwait(false);

        // Connect latency must come from attempts that actually completed TCP connect.
        // Math.Min(socks4, socks5) was wrong: a failed SOCKS4/5 attempt that never connected
        // (ConnectMs still 0) zeroed out the successful attempt's real RTT in the UI.
        var connectMs = PickSocksConnectMs(socks4, socks5);

        if (!socks4.Ok && !socks5.Ok)
        {
            // Prefer a failure that indicates the proxy was reachable (target/handshake issue)
            // over one that indicates we could not reach the proxy at all.
            var (failure, error) = PickSocksFailure(socks4, socks5);
            return new ProxyCheckResult
            {
                Proxy = proxy,
                IsAlive = false,
                Failure = failure,
                ErrorMessage = error,
                LatencyMs = Math.Max(socks4.LatencyMs, socks5.LatencyMs),
                ConnectMs = connectMs,
                AuthMethod = socks5.Auth != ProxyAuthMethod.None ? socks5.Auth : socks4.Auth,
                UsedRemoteDns = _settings.ResolveHostnamesThroughProxy,
                FakeIp = fakeIp
            };
        }

        ProxyProtocol confirmed;
        int latency;
        ProxyAuthMethod auth;
        if (socks4.Ok && socks5.Ok)
        {
            confirmed = ProxyProtocol.Socks4And5;
            latency = (socks4.LatencyMs + socks5.LatencyMs) / 2;
            auth = socks5.Auth != ProxyAuthMethod.None ? socks5.Auth : socks4.Auth;
        }
        else if (socks4.Ok)
        {
            confirmed = ProxyProtocol.Socks4;
            latency = socks4.LatencyMs;
            auth = socks4.Auth;
        }
        else
        {
            confirmed = ProxyProtocol.Socks5;
            latency = socks5.LatencyMs;
            auth = socks5.Auth;
        }

        return new ProxyCheckResult
        {
            Proxy = proxy,
            IsAlive = true,
            ConfirmedProtocol = confirmed,
            Anonymity = AnonymityLevel.Elite,
            Country = _geoIp.LookupCountry(proxy.Host),
            LatencyMs = latency,
            ConnectMs = connectMs,
            Failure = FailureReason.None,
            AuthMethod = auth,
            UsedRemoteDns = _settings.ResolveHostnamesThroughProxy,
            FakeIp = fakeIp
        };
    }

    /// <summary>
    /// Prefer connect RTTs from successful SOCKS attempts; otherwise any attempt that finished TCP connect.
    /// </summary>
    internal static int PickSocksConnectMs(
        (bool Ok, FailureReason Failure, string? Error, int LatencyMs, int ConnectMs, ProxyAuthMethod Auth) socks4,
        (bool Ok, FailureReason Failure, string? Error, int LatencyMs, int ConnectMs, ProxyAuthMethod Auth) socks5)
    {
        if (socks4.Ok && socks5.Ok)
            return Math.Min(socks4.ConnectMs, socks5.ConnectMs);
        if (socks4.Ok)
            return socks4.ConnectMs;
        if (socks5.Ok)
            return socks5.ConnectMs;
        if (socks4.ConnectMs > 0 && socks5.ConnectMs > 0)
            return Math.Min(socks4.ConnectMs, socks5.ConnectMs);
        return Math.Max(socks4.ConnectMs, socks5.ConnectMs);
    }

    private static (FailureReason Failure, string? Error) PickSocksFailure(
        (bool Ok, FailureReason Failure, string? Error, int LatencyMs, int ConnectMs, ProxyAuthMethod Auth) socks4,
        (bool Ok, FailureReason Failure, string? Error, int LatencyMs, int ConnectMs, ProxyAuthMethod Auth) socks5)
    {
        // "Proxy reachable but target failed" is more informative than a generic connect failure.
        static int Rank(FailureReason r) => r switch
        {
            FailureReason.AuthenticationRequired => 3,
            FailureReason.TargetUnreachableThroughProxy => 2,
            FailureReason.ConnectRefused or FailureReason.ConnectTimeout => 0,
            _ => 1
        };

        return Rank(socks5.Failure) >= Rank(socks4.Failure)
            ? (socks5.Failure, socks5.Error)
            : (socks4.Failure, socks4.Error);
    }
}
