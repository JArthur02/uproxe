using System.Diagnostics;
using System.Net;
using UProxy.Core.Config;
using UProxy.Core.GeoIp;
using UProxy.Core.Models;

namespace UProxy.Core.Checking;

public sealed class ProxyChecker
{
    private readonly AppSettings _settings;
    private readonly JudgeClient _judge;
    private readonly IGeoIpResolver _geoIp;
    private IPAddress? _clientIp;
    private bool _clientIpResolved;

    public ProxyChecker(AppSettings settings, IGeoIpResolver? geoIp = null, JudgeClient? judge = null)
    {
        _settings = settings;
        _geoIp = geoIp ?? NullGeoIpResolver.Instance;
        _judge = judge ?? new JudgeClient(settings);
    }

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
        var sw = Stopwatch.StartNew();

        var (body, failure, error) = await _judge.FetchThroughHttpProxyAsync(proxy, ct).ConfigureAwait(false);
        sw.Stop();

        if (body is null || failure != FailureReason.None)
        {
            return new ProxyCheckResult
            {
                Proxy = proxy,
                IsAlive = false,
                Failure = failure,
                ErrorMessage = error,
                LatencyMs = (int)sw.ElapsedMilliseconds
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
            ObservedSourceIp = observed?.ToString(),
            Failure = FailureReason.None
        };
    }

    public async Task<ProxyCheckResult> CheckSocksAsync(ParsedProxy proxy, CancellationToken ct)
    {
        await EnsureClientIpAsync(ct).ConfigureAwait(false);

        // Destination: use the judge host over port 80 so we validate real application data.
        var judgeUri = new Uri(_settings.JudgeUrl.Contains("://") ? _settings.JudgeUrl : "http://" + _settings.JudgeUrl);
        var destHost = judgeUri.Host;
        var destPort = judgeUri.IsDefaultPort ? 80 : judgeUri.Port;
        var path = string.IsNullOrEmpty(judgeUri.PathAndQuery) ? "/" : judgeUri.PathAndQuery;

        var socks4 = await SocksClient.ConnectAndHttpGetAsync(
            proxy, ProxyProtocol.Socks4, destHost, destPort, path, _settings.TimeoutMs, ct).ConfigureAwait(false);
        var socks5 = await SocksClient.ConnectAndHttpGetAsync(
            proxy, ProxyProtocol.Socks5, destHost, destPort, path, _settings.TimeoutMs, ct).ConfigureAwait(false);

        if (!socks4.Ok && !socks5.Ok)
        {
            return new ProxyCheckResult
            {
                Proxy = proxy,
                IsAlive = false,
                Failure = socks5.Failure != FailureReason.None ? socks5.Failure : socks4.Failure,
                ErrorMessage = socks5.Error ?? socks4.Error,
                LatencyMs = Math.Max(socks4.LatencyMs, socks5.LatencyMs)
            };
        }

        ProxyProtocol confirmed;
        int latency;
        if (socks4.Ok && socks5.Ok)
        {
            confirmed = ProxyProtocol.Socks4And5;
            latency = (socks4.LatencyMs + socks5.LatencyMs) / 2;
        }
        else if (socks4.Ok)
        {
            confirmed = ProxyProtocol.Socks4;
            latency = socks4.LatencyMs;
        }
        else
        {
            confirmed = ProxyProtocol.Socks5;
            latency = socks5.LatencyMs;
        }

        return new ProxyCheckResult
        {
            Proxy = proxy,
            IsAlive = true,
            ConfirmedProtocol = confirmed,
            Anonymity = AnonymityLevel.Elite, // SOCKS does not inject HTTP forwarding headers by itself
            Country = _geoIp.LookupCountry(proxy.Host),
            LatencyMs = latency,
            Failure = FailureReason.None
        };
    }
}
