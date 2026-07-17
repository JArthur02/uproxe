using System.Text;
using UProxy.Core.Checking;
using UProxy.Core.Config;
using UProxy.Core.Models;

namespace UProxy.Core.Chaining;

/// <summary>HTTP CONNECT handshake on an existing stream (no TLS interception).</summary>
public static class HttpConnectHandshake
{
    private static readonly byte[] HeaderDelimiter = "\r\n\r\n"u8.ToArray();

    public static async Task<(int StatusCode, string StatusLine, ProxyAuthMethod Auth)> ConnectAsync(
        Stream stream,
        ParsedProxy proxy,
        string destinationHost,
        int destinationPort,
        HandshakeOptions? options = null,
        CancellationToken ct = default)
    {
        options ??= new HandshakeOptions();
        destinationHost = destinationHost.Trim().TrimStart('[').TrimEnd(']');
        var auth = string.IsNullOrEmpty(proxy.Username) ? ProxyAuthMethod.None : ProxyAuthMethod.Basic;
        var ua = UserAgents.AsciiSafe(options.UserAgent ?? UserAgents.Default);

        var sb = new StringBuilder();
        sb.Append("CONNECT ").Append(destinationHost).Append(':').Append(destinationPort).Append(" HTTP/1.1\r\n");
        sb.Append("Host: ").Append(destinationHost).Append(':').Append(destinationPort).Append("\r\n");
        if (!string.IsNullOrEmpty(proxy.Username))
            sb.Append("Proxy-Authorization: ")
              .Append(ProxyAuth.FormatBasicHeader(proxy.Username, proxy.Password)).Append("\r\n");
        sb.Append("User-Agent: ").Append(ua).Append("\r\n");
        sb.Append("Proxy-Connection: Keep-Alive\r\n\r\n");

        var request = Encoding.ASCII.GetBytes(sb.ToString());
        await stream.WriteAsync(request, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);

        var headerBytes = await StreamProtocolReader.ReadUntilAsync(
            stream, HeaderDelimiter, options.MaxHttpHeaderBytes, ct).ConfigureAwait(false);
        var text = Encoding.ASCII.GetString(headerBytes);
        var statusLine = text.Split("\r\n", 2, StringSplitOptions.None)[0];
        var code = ParseStatusCode(statusLine);

        if (code == 200)
            return (code, statusLine, auth);

        var reason = code switch
        {
            407 => FailureReason.AuthenticationRequired,
            403 or 405 or 400 => FailureReason.HttpsConnectForbidden,
            502 or 503 or 504 => FailureReason.TargetUnreachableThroughProxy,
            0 => FailureReason.ProxyHandshakeFailure,
            _ => FailureReason.TlsFailure
        };
        throw new ProxyHandshakeException(reason, $"CONNECT failed: {statusLine}");
    }

    /// <summary>Maps CONNECT status to the same classification used by <see cref="JudgeClient.ProbeHttpsAsync"/>.</summary>
    public static (bool Ok, FailureReason Failure, string? Error) ClassifyStatus(int code, string statusLine) =>
        code switch
        {
            200 => (true, FailureReason.None, null),
            407 => (false, FailureReason.AuthenticationRequired, "Proxy requires authentication for CONNECT."),
            403 or 405 or 400 => (false, FailureReason.HttpsConnectForbidden, $"CONNECT rejected: {statusLine}"),
            502 or 503 or 504 => (false, FailureReason.TargetUnreachableThroughProxy, $"CONNECT failed: {statusLine}"),
            _ => (false, FailureReason.TlsFailure, $"Unexpected CONNECT status: {statusLine}")
        };

    public static int ParseStatusCode(string statusLine)
    {
        var parts = statusLine.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2 && int.TryParse(parts[1], out var code) ? code : 0;
    }
}
