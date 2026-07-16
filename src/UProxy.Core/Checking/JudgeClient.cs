using System.Net;
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

    public async Task<(string? Body, FailureReason Failure, string? Error, ProxyAuthMethod Auth)> FetchThroughHttpProxyAsync(
        ParsedProxy proxy,
        CancellationToken ct)
    {
        var judges = EnumerateJudges().ToList();
        FailureReason lastFailure = FailureReason.JudgeUnavailable;
        string? lastError = null;
        var auth = string.IsNullOrEmpty(proxy.Username) ? ProxyAuthMethod.None : ProxyAuthMethod.Basic;

        foreach (var judgeUrl in judges)
        {
            // Only dispose handlers we create ourselves; a caller-supplied override
            // must survive across judge iterations (and be disposed by the caller).
            var handler = _handlerOverride ?? CreateProxyHandler(proxy);
            try
            {
                using var client = new HttpClient(handler, disposeHandler: false)
                {
                    Timeout = TimeSpan.FromMilliseconds(_settings.TimeoutMs)
                };
                client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", _settings.UserAgent);
                // Embedded Basic auth (Proxifier: Proxy-Authorization: Basic …)
                ProxyAuth.ApplyHttpBasic(client.DefaultRequestHeaders, proxy);

                using var response = await client.GetAsync(judgeUrl, HttpCompletionOption.ResponseContentRead, ct)
                    .ConfigureAwait(false);

                if ((int)response.StatusCode == 407)
                {
                    var (method, detail) = ProxyAuth.ClassifyProxyAuthenticate(response);
                    auth = method == ProxyAuthMethod.None ? ProxyAuthMethod.Basic : method;
                    lastFailure = FailureReason.AuthenticationRequired;
                    lastError = detail ?? "Proxy authentication required.";
                    continue;
                }

                var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

                if (!response.IsSuccessStatusCode)
                {
                    lastFailure = FailureReason.JudgeMismatch;
                    lastError = $"HTTP {(int)response.StatusCode}";
                    continue;
                }

                if (!AnonymityClassifier.LooksLikeAzenv(body))
                {
                    lastFailure = FailureReason.JudgeMismatch;
                    lastError = "Response was not an azenv-style judge body";
                    continue;
                }

                return (body, FailureReason.None, null, auth);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return (null, FailureReason.Cancelled, "Cancelled", auth);
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
                if (lastFailure == FailureReason.AuthenticationRequired &&
                    ex.Message.Contains("NTLM", StringComparison.OrdinalIgnoreCase))
                    auth = ProxyAuthMethod.NtlmRequired;
            }
            catch (Exception ex)
            {
                lastFailure = FailureReason.UnknownError;
                lastError = ex.Message;
            }
            finally
            {
                if (_handlerOverride is null)
                    handler.Dispose();
            }
        }

        return (null, lastFailure, lastError, auth);
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
            ProxyAuth.ApplyHttpBasic(client.DefaultRequestHeaders, proxy);

            using var response = await client.GetAsync("https://www.google.com/generate_204", ct)
                .ConfigureAwait(false);
            if ((int)response.StatusCode == 407)
            {
                var (_, detail) = ProxyAuth.ClassifyProxyAuthenticate(response);
                return (false, FailureReason.AuthenticationRequired, detail);
            }

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
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var url in new[] { _settings.JudgeUrl }.Concat(_settings.FallbackJudgeUrls))
        {
            if (string.IsNullOrWhiteSpace(url)) continue;
            if (seen.Add(url.Trim().TrimEnd('/'))) yield return url;
        }
    }

    private static HttpMessageHandler CreateProxyHandler(ParsedProxy proxy)
    {
        var webProxy = new WebProxy($"http://{proxy.Endpoint}");
        var cred = ProxyAuth.ToNetworkCredential(proxy);
        if (cred is not null)
        {
            // Prefer explicit Basic; do not enable default/Negotiate credentials (privacy).
            webProxy.Credentials = cred;
            webProxy.UseDefaultCredentials = false;
        }

        return new SocketsHttpHandler
        {
            Proxy = webProxy,
            UseProxy = true,
            AutomaticDecompression = DecompressionMethods.All,
            ConnectTimeout = TimeSpan.FromSeconds(15),
            PreAuthenticate = cred is not null
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
