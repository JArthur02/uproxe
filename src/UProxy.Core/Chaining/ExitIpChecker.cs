using System.Net;
using System.Net.Http;
using UProxy.Core.Config;

namespace UProxy.Core.Chaining;

/// <summary>
/// Optional exit-IP check through the active chain (explicit user action only).
/// Uses a configurable HTTPS endpoint — not the azenv judge.
/// </summary>
public static class ExitIpChecker
{
    public static async Task<string> CheckAsync(
        Func<SocketsHttpConnectionContext, CancellationToken, ValueTask<Stream>> connectCallback,
        string? exitIpUrl,
        CancellationToken cancellationToken)
    {
        var url = string.IsNullOrWhiteSpace(exitIpUrl) ? "https://api.ipify.org" : exitIpUrl.Trim();
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            throw new ArgumentException("Exit IP URL must be an absolute https:// URL.", nameof(exitIpUrl));

        var handler = new SocketsHttpHandler
        {
            ConnectCallback = connectCallback,
            AllowAutoRedirect = false
        };
        using var client = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(20)
        };
        client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", UserAgents.AsciiSafe(UserAgents.Default));

        using var response = await client.GetAsync(uri, HttpCompletionOption.ResponseContentRead, cancellationToken)
            .ConfigureAwait(false);
        if ((int)response.StatusCode is >= 300 and < 400)
            throw new InvalidOperationException("Exit IP service redirected; configure a direct HTTPS endpoint.");
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Exit IP service returned HTTP {(int)response.StatusCode}.");

        var text = (await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false)).Trim();
        var token = text.Split('\n', '\r', ' ', '\t')[0].Trim();
        if (!IsIpLiteral(token))
            throw new InvalidOperationException("Exit IP service did not return an IP address.");
        return token;
    }

    /// <summary>
    /// Fetches the public IP without any proxy (direct path) for comparison with a chain exit.
    /// </summary>
    public static async Task<string> CheckDirectAsync(
        string? exitIpUrl,
        CancellationToken cancellationToken)
    {
        var url = string.IsNullOrWhiteSpace(exitIpUrl) ? "https://api.ipify.org" : exitIpUrl.Trim();
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            throw new ArgumentException("Exit IP URL must be an absolute https:// URL.", nameof(exitIpUrl));

        using var handler = new SocketsHttpHandler { AllowAutoRedirect = false };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };
        client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", UserAgents.AsciiSafe(UserAgents.Default));

        using var response = await client.GetAsync(uri, HttpCompletionOption.ResponseContentRead, cancellationToken)
            .ConfigureAwait(false);
        if ((int)response.StatusCode is >= 300 and < 400)
            throw new InvalidOperationException("Exit IP service redirected; configure a direct HTTPS endpoint.");
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Exit IP service returned HTTP {(int)response.StatusCode}.");

        var text = (await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false)).Trim();
        var token = text.Split('\n', '\r', ' ', '\t')[0].Trim();
        if (!IsIpLiteral(token))
            throw new InvalidOperationException("Exit IP service did not return an IP address.");
        return token;
    }

    public static bool IsIpLiteral(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 64)
            return false;
        return IPAddress.TryParse(value.Trim().TrimStart('[').TrimEnd(']'), out _);
    }
}
