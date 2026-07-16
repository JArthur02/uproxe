using System.Net;
using System.Net.Http.Headers;
using System.Text;
using UProxy.Core.Models;

namespace UProxy.Core.Checking;

/// <summary>
/// Embedded proxy authentication helpers aligned with Proxifier ProxyChecker:
/// HTTP Basic via Proxy-Authorization, SOCKS5 user/pass, SOCKS4 userid,
/// and detection of NTLM-required challenges without sending NTLM by default.
/// </summary>
public static class ProxyAuth
{
    public static string FormatBasicHeader(string username, string? password)
    {
        var token = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{username}:{password ?? ""}"));
        return "Basic " + token;
    }

    public static void ApplyHttpBasic(HttpRequestHeaders headers, ParsedProxy proxy)
    {
        if (string.IsNullOrEmpty(proxy.Username))
            return;
        headers.Remove("Proxy-Authorization");
        headers.TryAddWithoutValidation("Proxy-Authorization", FormatBasicHeader(proxy.Username, proxy.Password));
    }

    public static NetworkCredential? ToNetworkCredential(ParsedProxy proxy) =>
        string.IsNullOrEmpty(proxy.Username)
            ? null
            : new NetworkCredential(proxy.Username, proxy.Password ?? "");

    /// <summary>Inspect a 407 response for auth scheme. Prefer Basic; flag NTLM as required-but-not-sent.</summary>
    public static (ProxyAuthMethod Method, string? Detail) ClassifyProxyAuthenticate(HttpResponseMessage response)
    {
        if (response.StatusCode != HttpStatusCode.ProxyAuthenticationRequired &&
            (int)response.StatusCode != 407)
        {
            return (ProxyAuthMethod.None, null);
        }

        var values = response.Headers.ProxyAuthenticate;
        var joined = string.Join(", ", values);
        if (joined.Length == 0 && response.Headers.TryGetValues("Proxy-Authenticate", out var raw))
            joined = string.Join(", ", raw);

        if (joined.Contains("NTLM", StringComparison.OrdinalIgnoreCase) ||
            joined.Contains("Negotiate", StringComparison.OrdinalIgnoreCase))
        {
            return (ProxyAuthMethod.NtlmRequired,
                "Proxy requires NTLM/Negotiate authentication (not sent by default for privacy).");
        }

        if (joined.Contains("Basic", StringComparison.OrdinalIgnoreCase))
            return (ProxyAuthMethod.Basic, "Proxy requires Basic authentication.");

        return (ProxyAuthMethod.None, "Proxy authentication required (scheme unknown).");
    }

    public static string Describe(ProxyAuthMethod method) => method switch
    {
        ProxyAuthMethod.None => "Authentication: NO",
        ProxyAuthMethod.Basic => "Authentication: Basic",
        ProxyAuthMethod.SocksUserPass => "Authentication: YES",
        ProxyAuthMethod.Socks4UserId => "Authentication: UserID only",
        ProxyAuthMethod.NtlmRequired => "Authentication: NTLM",
        _ => method.ToString()
    };
}
