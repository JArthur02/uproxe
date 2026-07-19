using UProxy.Core.Models;

namespace UProxy.Core.Checking;

/// <summary>Human-readable failure text inspired by Proxifier ProxyChecker reports.</summary>
public static class FailureMessages
{
    public static string Describe(FailureReason reason, string? detail = null)
    {
        var baseMsg = reason switch
        {
            FailureReason.None => "OK",
            FailureReason.InvalidProxy => "Invalid proxy address or port.",
            FailureReason.DnsFailure =>
                "Could not resolve a hostname through the proxy. Enable SOCKS4a / remote DNS, or use an IP target.",
            FailureReason.ConnectRefused =>
                "Connection refused. Check that the proxy address and port are correct, or the proxy may be down.",
            FailureReason.ConnectTimeout =>
                "Attempt to connect timed out without establishing a connection.",
            FailureReason.ProxyHandshakeFailure =>
                "Could not complete the proxy handshake. The proxy may use an unsupported protocol.",
            FailureReason.AuthenticationRequired =>
                "The proxy server requires authentication. Check username/password (or NTLM if advertised).",
            FailureReason.TlsFailure =>
                "TLS/SSL through the proxy failed. The proxy may not support HTTPS CONNECT.",
            FailureReason.JudgeMismatch =>
                "Reply from the target did not look like a valid judge/web response (possible captive portal).",
            FailureReason.JudgeUnavailable =>
                "Proxy judge was unreachable. This is not necessarily a dead proxy.",
            FailureReason.EmptyResponse =>
                "Connection closed unexpectedly or returned an empty response.",
            FailureReason.Cancelled => "Cancelled.",
            FailureReason.Timeout => "Timed out waiting for the proxy or target.",
            FailureReason.TargetUnreachableThroughProxy =>
                "Connected to the proxy, but it could not reach the target host. The proxy may still work for other destinations.",
            FailureReason.HttpsConnectForbidden =>
                "The proxy refused the HTTPS CONNECT tunnel. It may forbid CONNECT to this port (e.g. Squid SSL_ports or Microsoft ISA tunnel-port policy).",
            FailureReason.UnknownError => "Unexpected error.",
            _ => reason.ToString()
        };

        if (string.IsNullOrWhiteSpace(detail))
            return baseMsg;
        return baseMsg + " " + detail.Trim();
    }
}
