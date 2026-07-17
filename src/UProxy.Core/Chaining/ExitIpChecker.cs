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
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
            throw new ArgumentException("Exit IP URL must be an absolute http(s) URL.", nameof(exitIpUrl));

        var handler = new SocketsHttpHandler
        {
            ConnectCallback = connectCallback,
            AllowAutoRedirect = true
        };
        using var client = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(20)
        };
        client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", UserAgents.AsciiSafe(UserAgents.Default));
        var text = (await client.GetStringAsync(uri, cancellationToken).ConfigureAwait(false)).Trim();
        if (string.IsNullOrWhiteSpace(text) || text.Length > 128)
            throw new InvalidOperationException("Exit IP service returned an unexpected body.");
        return text.Split('\n', '\r', ' ')[0].Trim();
    }
}
