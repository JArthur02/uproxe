using System.Net;
using System.Text;
using UProxy.Core.Chaining;
using UProxy.Core.Models;

namespace UProxy.Core.Gateway;

/// <summary>How the request body length is determined for forwarding.</summary>
public enum RequestBodyLengthKind
{
    /// <summary>No request body (typical GET/HEAD/CONNECT, or no length headers).</summary>
    NoBody,
    /// <summary>Body length given by Content-Length.</summary>
    ContentLength,
    /// <summary>Body uses Transfer-Encoding: chunked framing.</summary>
    Chunked,
}

/// <summary>Safe parsing helpers for local HTTP proxy request lines and authorities.</summary>
public static class HttpProxyRequestParser
{
    public const int MaxHeaderBytes = 32 * 1024;

    private static readonly byte[] HeaderDelimiter = "\r\n\r\n"u8.ToArray();

    public sealed record ParsedProxyRequest(
        string Method,
        string RequestTarget,
        string HttpVersion,
        string Host,
        int Port,
        /// <summary>Origin-form path+query to forward after dialing (e.g. <c>/path?x=1</c>).</summary>
        string OriginFormTarget,
        bool IsConnect,
        bool WasAbsoluteForm,
        IReadOnlyList<(string Name, string Value)> Headers);

    /// <summary>Reads request headers up to <see cref="MaxHeaderBytes"/> (inclusive of trailing CRLFCRLF).</summary>
    public static async Task<byte[]> ReadHeadersAsync(Stream stream, CancellationToken ct, int maxBytes = MaxHeaderBytes)
    {
        try
        {
            return await StreamProtocolReader.ReadUntilAsync(stream, HeaderDelimiter, maxBytes, ct)
                .ConfigureAwait(false);
        }
        catch (ProxyHandshakeException ex) when (
            ex.Message.Contains("exceeded", StringComparison.OrdinalIgnoreCase))
        {
            throw new HttpProxyParseException("HTTP headers exceed size limit.", ex);
        }
        catch (ProxyHandshakeException ex)
        {
            throw new HttpProxyParseException("Failed to read HTTP headers.", ex);
        }
    }

    public static ParsedProxyRequest Parse(ReadOnlySpan<byte> headerBytes)
    {
        EnsureStrictCrlf(headerBytes);
        var text = Encoding.ASCII.GetString(headerBytes);
        return Parse(text);
    }

    public static ParsedProxyRequest Parse(string headerText)
    {
        if (string.IsNullOrWhiteSpace(headerText))
            throw new HttpProxyParseException("Empty HTTP request.");

        EnsureStrictCrlf(headerText);

        var lines = headerText.Split("\r\n", StringSplitOptions.None);
        if (lines.Length == 0 || string.IsNullOrWhiteSpace(lines[0]))
            throw new HttpProxyParseException("Missing request line.");

        var requestLine = lines[0].TrimEnd();
        if (ContainsCrlfInjection(requestLine) || requestLine.Contains('\0'))
            throw new HttpProxyParseException("Invalid characters in request line.");

        var parts = requestLine.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 3)
            throw new HttpProxyParseException("Malformed HTTP request line.");

        var method = parts[0];
        var target = parts[1];
        var version = parts[2];
        if (!version.StartsWith("HTTP/", StringComparison.OrdinalIgnoreCase))
            throw new HttpProxyParseException("Unsupported HTTP version.");
        if (ContainsCrlfInjection(method) || ContainsCrlfInjection(target))
            throw new HttpProxyParseException("CRLF injection in request line.");

        var headers = new List<(string Name, string Value)>();
        for (var i = 1; i < lines.Length; i++)
        {
            var line = lines[i];
            if (line.Length == 0)
                break; // end of headers
            if (line[0] is ' ' or '\t')
                throw new HttpProxyParseException("Obsolete line folding is not allowed.");
            var colon = line.IndexOf(':');
            if (colon <= 0)
                throw new HttpProxyParseException("Malformed header field.");
            var name = line[..colon].Trim();
            var value = line[(colon + 1)..].Trim();
            if (name.Length == 0 || ContainsCrlfInjection(name) || ContainsCrlfInjection(value))
                throw new HttpProxyParseException("Invalid header field.");
            headers.Add((name, value));
        }

        var isConnect = method.Equals("CONNECT", StringComparison.OrdinalIgnoreCase);
        string host;
        int port;
        string originForm;
        var wasAbsolute = false;

        if (isConnect)
        {
            if (!TryParseAuthority(target, out host, out port))
                throw new HttpProxyParseException("Invalid CONNECT authority.");
            originForm = "/";
        }
        else if (target.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            throw new HttpProxyParseException(
                "Absolute-form https:// URIs are not supported; use CONNECT for HTTPS.");
        }
        else if (target.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
        {
            wasAbsolute = true;
            if (!TryParseAbsoluteUri(target, out host, out port, out originForm))
                throw new HttpProxyParseException("Invalid absolute-form request URI.");
        }
        else if (target.StartsWith('/'))
        {
            // Origin-form — Host header required.
            originForm = target;
            var hostHeader = headers.FirstOrDefault(h =>
                h.Name.Equals("Host", StringComparison.OrdinalIgnoreCase)).Value;
            if (string.IsNullOrWhiteSpace(hostHeader))
                throw new HttpProxyParseException("Missing Host header for origin-form request.");
            if (!TryParseAuthority(hostHeader, defaultPort: 80, out host, out port))
                throw new HttpProxyParseException("Invalid Host header.");
        }
        else
        {
            throw new HttpProxyParseException("Unsupported request target form.");
        }

        return new ParsedProxyRequest(
            method, target, version, host, port, originForm, isConnect, wasAbsolute, headers);
    }

    /// <summary>
    /// Determines how to copy the request body when forwarding a non-CONNECT request.
    /// Prefer chunked when Transfer-Encoding indicates chunked; otherwise Content-Length.
    /// </summary>
    public static RequestBodyLengthKind GetRequestBodyLengthPolicy(
        ParsedProxyRequest request,
        out long contentLength)
    {
        contentLength = 0;
        if (request.IsConnect)
            return RequestBodyLengthKind.NoBody;

        string? transferEncoding = null;
        string? contentLengthHeader = null;
        foreach (var (name, value) in request.Headers)
        {
            if (name.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase))
                transferEncoding = value;
            else if (name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                contentLengthHeader = value;
        }

        if (transferEncoding is not null)
        {
            // RFC 7230: if Transfer-Encoding is present, it takes precedence over Content-Length.
            foreach (var coding in transferEncoding.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            {
                if (coding.Equals("chunked", StringComparison.OrdinalIgnoreCase))
                    return RequestBodyLengthKind.Chunked;
            }
            throw new HttpProxyParseException($"Unsupported Transfer-Encoding: {transferEncoding}");
        }

        if (contentLengthHeader is not null)
        {
            if (!long.TryParse(contentLengthHeader, out contentLength) || contentLength < 0)
                throw new HttpProxyParseException("Invalid Content-Length.");
            return contentLength == 0 ? RequestBodyLengthKind.NoBody : RequestBodyLengthKind.ContentLength;
        }

        return RequestBodyLengthKind.NoBody;
    }

    /// <summary>
    /// Rebuilds an origin-form request (request line + headers + trailing CRLFCRLF) for forwarding
    /// after dialing. Strips hop-by-hop / proxy headers. Keeps Transfer-Encoding and Content-Length
    /// so chunked bodies remain intact. Always injects Connection: close (single-request only).
    /// </summary>
    public static byte[] BuildOriginFormRequest(ParsedProxyRequest request)
    {
        var sb = new StringBuilder(256);
        sb.Append(request.Method).Append(' ')
          .Append(request.OriginFormTarget).Append(' ')
          .Append(request.HttpVersion).Append("\r\n");

        var sawHost = false;
        foreach (var (name, value) in request.Headers)
        {
            if (IsHopByHopHeader(name))
                continue;
            if (name.Equals("Host", StringComparison.OrdinalIgnoreCase))
            {
                sawHost = true;
                sb.Append("Host: ").Append(FormatAuthority(request.Host, request.Port, request.WasAbsoluteForm
                    ? InferDefaultPort(request.RequestTarget)
                    : 80)).Append("\r\n");
                continue;
            }
            sb.Append(name).Append(": ").Append(value).Append("\r\n");
        }

        if (!sawHost)
        {
            var defaultPort = request.WasAbsoluteForm ? InferDefaultPort(request.RequestTarget) : 80;
            sb.Append("Host: ").Append(FormatAuthority(request.Host, request.Port, defaultPort)).Append("\r\n");
        }

        sb.Append("Connection: close\r\n");
        sb.Append("\r\n");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    public static bool TryParseAuthority(string authority, out string host, out int port) =>
        TryParseAuthority(authority, defaultPort: -1, out host, out port);

    public static bool TryParseAuthority(string authority, int defaultPort, out string host, out int port)
    {
        host = "";
        port = 0;
        if (string.IsNullOrWhiteSpace(authority))
            return false;
        authority = authority.Trim();
        if (ContainsCrlfInjection(authority) || authority.Contains('\0') || authority.Contains('/'))
            return false;

        // [IPv6]:port or [IPv6]
        if (authority.StartsWith('['))
        {
            var close = authority.IndexOf(']');
            if (close <= 1)
                return false;
            host = authority[1..close];
            if (!IPAddress.TryParse(host, out var ip) ||
                ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetworkV6)
                return false;
            var rest = authority[(close + 1)..];
            if (rest.Length == 0)
            {
                if (defaultPort is < 1 or > 65535)
                    return false;
                port = defaultPort;
                return true;
            }
            if (rest[0] != ':' || !int.TryParse(rest.AsSpan(1), out port) || port is < 1 or > 65535)
                return false;
            return true;
        }

        // host:port — last colon (IPv4 or domain). Reject ambiguous bare IPv6 without brackets.
        var colon = authority.LastIndexOf(':');
        if (colon > 0 && authority.IndexOf(':') == colon)
        {
            host = authority[..colon];
            if (!int.TryParse(authority.AsSpan(colon + 1), out port) || port is < 1 or > 65535)
                return false;
            if (string.IsNullOrWhiteSpace(host) || host.Contains(' '))
                return false;
            return IsSafeHostToken(host);
        }

        if (authority.Contains(':'))
            return false; // unbracketed IPv6

        host = authority;
        if (!IsSafeHostToken(host))
            return false;
        if (defaultPort is < 1 or > 65535)
            return false;
        port = defaultPort;
        return true;
    }

    public static bool TryParseAbsoluteUri(
        string uri,
        out string host,
        out int port,
        out string originForm)
    {
        host = "";
        port = 0;
        originForm = "/";
        if (string.IsNullOrWhiteSpace(uri) || ContainsCrlfInjection(uri))
            return false;

        if (!Uri.TryCreate(uri, UriKind.Absolute, out var parsed))
            return false;
        if (parsed.Scheme is not ("http" or "https"))
            return false;
        if (!string.IsNullOrEmpty(parsed.UserInfo))
            return false; // reject userinfo (credential injection / weird authorities)

        host = parsed.IdnHost;
        if (string.IsNullOrWhiteSpace(host) || ContainsCrlfInjection(host))
            return false;

        port = parsed.IsDefaultPort
            ? (parsed.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase) ? 443 : 80)
            : parsed.Port;
        if (port is < 1 or > 65535)
            return false;

        var path = string.IsNullOrEmpty(parsed.AbsolutePath) ? "/" : parsed.AbsolutePath;
        originForm = string.IsNullOrEmpty(parsed.Query) ? path : path + parsed.Query;
        if (ContainsCrlfInjection(originForm))
            return false;
        return true;
    }

    public static bool ContainsCrlfInjection(string value) =>
        value.Contains('\r') || value.Contains('\n');

    public static bool IsSameEndpoint(string host, int port, IPEndPoint local)
    {
        if (port != local.Port)
            return false;
        if (!IPAddress.TryParse(host.Trim().TrimStart('[').TrimEnd(']'), out var ip))
            return false;
        return IPAddress.IsLoopback(ip) && IPAddress.IsLoopback(local.Address);
    }

    /// <summary>
    /// Rejects lone CR or lone LF (not part of CRLF). HTTP/1.x requires CRLF line endings.
    /// </summary>
    internal static void EnsureStrictCrlf(ReadOnlySpan<byte> bytes)
    {
        for (var i = 0; i < bytes.Length; i++)
        {
            var b = bytes[i];
            if (b == (byte)'\n')
            {
                if (i == 0 || bytes[i - 1] != (byte)'\r')
                    throw new HttpProxyParseException("HTTP requires CRLF line endings.");
            }
            else if (b == (byte)'\r')
            {
                if (i + 1 >= bytes.Length || bytes[i + 1] != (byte)'\n')
                    throw new HttpProxyParseException("HTTP requires CRLF line endings.");
            }
        }
    }

    private static void EnsureStrictCrlf(string text)
    {
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '\n')
            {
                if (i == 0 || text[i - 1] != '\r')
                    throw new HttpProxyParseException("HTTP requires CRLF line endings.");
            }
            else if (c == '\r')
            {
                if (i + 1 >= text.Length || text[i + 1] != '\n')
                    throw new HttpProxyParseException("HTTP requires CRLF line endings.");
            }
        }
    }

    private static bool IsSafeHostToken(string host)
    {
        if (string.IsNullOrWhiteSpace(host))
            return false;
        foreach (var c in host)
        {
            if (c is '\r' or '\n' or '\0' or ' ' or '/' or '\\' or '@')
                return false;
        }
        return true;
    }

    /// <summary>
    /// Hop-by-hop headers stripped when rebuilding origin-form for forwarding.
    /// Transfer-Encoding is intentionally kept so chunked bodies are not corrupted.
    /// </summary>
    private static bool IsHopByHopHeader(string name) =>
        name.Equals("Proxy-Connection", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("Proxy-Authorization", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("Connection", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("Keep-Alive", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("TE", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("Trailer", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("Upgrade", StringComparison.OrdinalIgnoreCase);

    private static int InferDefaultPort(string absoluteUri) =>
        absoluteUri.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ? 443 : 80;

    private static string FormatAuthority(string host, int port, int defaultPort)
    {
        var h = host.Contains(':') && !host.StartsWith('[') ? $"[{host}]" : host;
        return port == defaultPort ? h : $"{h}:{port}";
    }
}

public sealed class HttpProxyParseException : Exception
{
    public HttpProxyParseException(string message) : base(message) { }
    public HttpProxyParseException(string message, Exception inner) : base(message, inner) { }
}
