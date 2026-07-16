using System.Net;
using System.Text;
using UProxy.Core.Config;
using UProxy.Core.Models;

namespace UProxy.Core.Checking;

public sealed class JudgeClient
{
    private readonly AppSettings _settings;
    private readonly HttpMessageHandler? _handlerOverride;

    public JudgeClient(AppSettings settings, HttpMessageHandler? handlerOverride = null)
    {
        _settings = settings;
        _handlerOverride = handlerOverride;
    }

    public async Task<(string? Body, FailureReason Failure, string? Error)> FetchThroughHttpProxyAsync(
        ParsedProxy proxy,
        CancellationToken ct)
    {
        var judges = EnumerateJudges().ToList();
        FailureReason lastFailure = FailureReason.JudgeUnavailable;
        string? lastError = null;

        foreach (var judgeUrl in judges)
        {
            try
            {
                using var handler = _handlerOverride ?? CreateProxyHandler(proxy);
                using var client = new HttpClient(handler, disposeHandler: _handlerOverride is null)
                {
                    Timeout = TimeSpan.FromMilliseconds(_settings.TimeoutMs)
                };
                client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", _settings.UserAgent);

                using var response = await client.GetAsync(judgeUrl, HttpCompletionOption.ResponseContentRead, ct)
                    .ConfigureAwait(false);
                var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

                if (!response.IsSuccessStatusCode)
                {
                    lastFailure = FailureReason.JudgeMismatch;
                    lastError = $"HTTP {(int)response.StatusCode}";
                    continue;
                }

                if (!AnonymityClassifier.LooksLikeAzenv(body))
                {
                    // Captive portal / block page — do not treat as alive.
                    lastFailure = FailureReason.JudgeMismatch;
                    lastError = "Response was not an azenv-style judge body";
                    continue;
                }

                return (body, FailureReason.None, null);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return (null, FailureReason.Cancelled, "Cancelled");
            }
            catch (TaskCanceledException)
            {
                lastFailure = FailureReason.Timeout;
                lastError = "Timed out";
            }
            catch (HttpRequestException ex)
            {
                lastFailure = MapHttpException(ex);
                lastError = ex.Message;
            }
            catch (Exception ex)
            {
                lastFailure = FailureReason.UnknownError;
                lastError = ex.Message;
            }
        }

        return (null, lastFailure, lastError);
    }

    public async Task<(bool Ok, FailureReason Failure, string? Error)> ProbeHttpsAsync(
        ParsedProxy proxy,
        CancellationToken ct)
    {
        try
        {
            using var handler = CreateProxyHandler(proxy);
            using var client = new HttpClient(handler)
            {
                Timeout = TimeSpan.FromMilliseconds(_settings.TimeoutMs)
            };
            client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", _settings.UserAgent);

            using var response = await client.GetAsync("https://www.google.com/generate_204", ct)
                .ConfigureAwait(false);
            // 204 No Content is Google's connectivity check; also accept 200/301/302.
            var code = (int)response.StatusCode;
            if (code is 204 or 200 or 301 or 302)
                return (true, FailureReason.None, null);

            return (false, FailureReason.TlsFailure, $"Unexpected status {code}");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return (false, FailureReason.Cancelled, "Cancelled");
        }
        catch (TaskCanceledException)
        {
            return (false, FailureReason.Timeout, "Timed out");
        }
        catch (HttpRequestException ex)
        {
            return (false, MapHttpException(ex), ex.Message);
        }
        catch (Exception ex)
        {
            return (false, FailureReason.UnknownError, ex.Message);
        }
    }

    /// <summary>Fetches the judge without a proxy so we know the user's real IP for classification.</summary>
    public async Task<IPAddress?> GetDirectClientIpAsync(CancellationToken ct)
    {
        foreach (var judgeUrl in EnumerateJudges())
        {
            try
            {
                using var handler = new SocketsHttpHandler
                {
                    AutomaticDecompression = DecompressionMethods.All,
                    UseProxy = false
                };
                using var client = new HttpClient(handler)
                {
                    Timeout = TimeSpan.FromMilliseconds(Math.Min(_settings.TimeoutMs, 8_000))
                };
                client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", _settings.UserAgent);
                var body = await client.GetStringAsync(judgeUrl, ct).ConfigureAwait(false);
                return AnonymityClassifier.TryGetRemoteAddr(body);
            }
            catch
            {
                // try next
            }
        }
        return null;
    }

    private IEnumerable<string> EnumerateJudges()
    {
        yield return _settings.JudgeUrl;
        foreach (var url in _settings.FallbackJudgeUrls)
        {
            if (!string.Equals(url, _settings.JudgeUrl, StringComparison.OrdinalIgnoreCase))
                yield return url;
        }
    }

    private static HttpMessageHandler CreateProxyHandler(ParsedProxy proxy)
    {
        var webProxy = new WebProxy(proxy.Host, proxy.Port);
        if (!string.IsNullOrEmpty(proxy.Username))
            webProxy.Credentials = new NetworkCredential(proxy.Username, proxy.Password);

        return new SocketsHttpHandler
        {
            Proxy = webProxy,
            UseProxy = true,
            AutomaticDecompression = DecompressionMethods.All,
            ConnectTimeout = TimeSpan.FromSeconds(15)
        };
    }

    private static FailureReason MapHttpException(HttpRequestException ex)
    {
        var msg = ex.Message;
        if (msg.Contains("refused", StringComparison.OrdinalIgnoreCase))
            return FailureReason.ConnectRefused;
        if (msg.Contains("SSL", StringComparison.OrdinalIgnoreCase) ||
            msg.Contains("TLS", StringComparison.OrdinalIgnoreCase) ||
            msg.Contains("certificate", StringComparison.OrdinalIgnoreCase))
            return FailureReason.TlsFailure;
        if (msg.Contains("auth", StringComparison.OrdinalIgnoreCase) ||
            msg.Contains("407", StringComparison.OrdinalIgnoreCase))
            return FailureReason.AuthenticationRequired;
        return FailureReason.ProxyHandshakeFailure;
    }
}
