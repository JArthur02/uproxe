using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using UProxy.Core.Dns;
using UProxy.Core.Models;

namespace UProxy.Core.Checking;

public sealed class SocksConnectOptions
{
    /// <summary>Proxifier "Use SOCKS 4A extension (remote hostname resolving feature)".</summary>
    public bool UseSocks4a { get; init; } = true;

    /// <summary>Send hostnames to the proxy instead of resolving locally (privacy + Fake-IP path).</summary>
    public bool ResolveHostnamesThroughProxy { get; init; } = true;

    public FakeIpDns? FakeIpDns { get; init; }
}

/// <summary>SOCKS4 / SOCKS4a / SOCKS5 client with embedded auth and Fake-IP aware remote DNS.</summary>
public static class SocksClient
{
    public static async Task<(bool Ok, FailureReason Failure, string? Error, int LatencyMs, int ConnectMs, ProxyAuthMethod Auth)> ConnectAndHttpGetAsync(
        ParsedProxy proxy,
        ProxyProtocol socksVersion,
        string destinationHost,
        int destinationPort,
        string httpPath,
        int timeoutMs,
        CancellationToken ct,
        SocksConnectOptions? options = null)
    {
        options ??= new SocksConnectOptions();
        var authUsed = ProxyAuthMethod.None;
        var connectMs = 0;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeoutMs);

        try
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp)
            {
                NoDelay = true
            };

            if (!IPAddress.TryParse(proxy.Host, out var proxyIp))
            {
                var addresses = await System.Net.Dns.GetHostAddressesAsync(proxy.Host, cts.Token).ConfigureAwait(false);
                proxyIp = addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork)
                          ?? addresses.FirstOrDefault()
                          ?? throw new SocketException((int)SocketError.HostNotFound);
            }

            // Test 1 + Test 3: pure TCP connect to the proxy (reachability + connect latency).
            var connectSw = System.Diagnostics.Stopwatch.StartNew();
            await socket.ConnectAsync(new IPEndPoint(proxyIp, proxy.Port), cts.Token).ConfigureAwait(false);
            connectSw.Stop();
            connectMs = (int)connectSw.ElapsedMilliseconds;

            var dest = ResolveDestination(destinationHost, options);

            if (socksVersion == ProxyProtocol.Socks4)
            {
                authUsed = await Socks4HandshakeAsync(socket, proxy, dest, destinationPort, options, cts.Token)
                    .ConfigureAwait(false);
            }
            else
            {
                authUsed = await Socks5HandshakeAsync(socket, proxy, dest, destinationPort, options, cts.Token)
                    .ConfigureAwait(false);
            }

            // Host header must be the logical hostname (not a Fake-IP).
            var hostHeader = options.FakeIpDns?.ResolveDestinationForProxy(destinationHost, resolveHostnamesThroughProxy: true)
                             ?? destinationHost;
            var request = Encoding.ASCII.GetBytes(
                $"GET {httpPath} HTTP/1.1\r\nHost: {hostHeader}\r\nConnection: close\r\nUser-Agent: μProxy-Tool/2.0\r\n\r\n");
            await socket.SendAsync(request, SocketFlags.None, cts.Token).ConfigureAwait(false);

            var buffer = new byte[512];
            var read = await socket.ReceiveAsync(buffer, SocketFlags.None, cts.Token).ConfigureAwait(false);
            sw.Stop();

            if (read < 12)
                return (false, FailureReason.EmptyResponse, "No HTTP response through SOCKS", (int)sw.ElapsedMilliseconds, connectMs, authUsed);

            var text = Encoding.ASCII.GetString(buffer, 0, read);
            if (!text.StartsWith("HTTP/", StringComparison.Ordinal))
                return (false, FailureReason.JudgeMismatch, "Non-HTTP response through SOCKS", (int)sw.ElapsedMilliseconds, connectMs, authUsed);

            return (true, FailureReason.None, null, (int)sw.ElapsedMilliseconds, connectMs, authUsed);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return (false, FailureReason.Cancelled, "Cancelled", (int)sw.ElapsedMilliseconds, connectMs, authUsed);
        }
        catch (OperationCanceledException)
        {
            // If we never finished connecting to the proxy, it's a proxy connect timeout;
            // otherwise the handshake/target hop timed out.
            var reason = connectMs == 0 ? FailureReason.ConnectTimeout : FailureReason.Timeout;
            return (false, reason, "Timed out", (int)sw.ElapsedMilliseconds, connectMs, authUsed);
        }
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionRefused)
        {
            return (false, FailureReason.ConnectRefused, ex.Message, (int)sw.ElapsedMilliseconds, connectMs, authUsed);
        }
        catch (SocketException ex) when (ex.SocketErrorCode is SocketError.TimedOut or SocketError.TryAgain)
        {
            return (false, FailureReason.ConnectTimeout, ex.Message, (int)sw.ElapsedMilliseconds, connectMs, authUsed);
        }
        catch (SocksProtocolException ex)
        {
            return (false, ex.Reason, ex.Message, (int)sw.ElapsedMilliseconds, connectMs, authUsed);
        }
        catch (Exception ex)
        {
            return (false, FailureReason.UnknownError, ex.Message, (int)sw.ElapsedMilliseconds, connectMs, authUsed);
        }
    }

    private static string ResolveDestination(string destinationHost, SocksConnectOptions options)
    {
        if (options.FakeIpDns is not null)
            return options.FakeIpDns.ResolveDestinationForProxy(destinationHost, options.ResolveHostnamesThroughProxy);

        if (FakeIpDns.IsFakeIp(destinationHost))
            throw new SocksProtocolException(FailureReason.DnsFailure,
                "Destination is a Fake-IP with no hostname mapping. Enable Fake-IP DNS.");

        return destinationHost.Trim().TrimStart('[').TrimEnd(']');
    }

    private static async Task<ProxyAuthMethod> Socks4HandshakeAsync(
        Socket socket,
        ParsedProxy proxy,
        string destHost,
        int destPort,
        SocksConnectOptions options,
        CancellationToken ct)
    {
        byte[] ipBytes;
        string? hostname = null;
        var auth = ProxyAuthMethod.None;

        if (IPAddress.TryParse(destHost, out var ip) && ip.AddressFamily == AddressFamily.InterNetwork && !FakeIpDns.IsFakeIp(ip))
        {
            ipBytes = ip.GetAddressBytes();
        }
        else if (options.UseSocks4a || options.ResolveHostnamesThroughProxy || !IPAddress.TryParse(destHost, out _))
        {
            // SOCKS4a: DSTIP = 0.0.0.x (x != 0), then userid\0, then hostname\0
            ipBytes = [0, 0, 0, 1];
            hostname = destHost;
            if (!options.UseSocks4a && !options.ResolveHostnamesThroughProxy)
            {
                // Forced local resolve
                var addrs = await System.Net.Dns.GetHostAddressesAsync(destHost, ct).ConfigureAwait(false);
                var v4 = addrs.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork)
                         ?? throw new SocksProtocolException(FailureReason.DnsFailure, "No IPv4 for hostname (SOCKS4).");
                ipBytes = v4.GetAddressBytes();
                hostname = null;
            }
        }
        else
        {
            throw new SocksProtocolException(FailureReason.DnsFailure,
                "Could not resolve hostname through SOCKS 4. Enable SOCKS 4A extension or specify an IP address.");
        }

        using var ms = new MemoryStream();
        ms.WriteByte(0x04);
        ms.WriteByte(0x01); // CONNECT
        Span<byte> portBuf = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(portBuf, (ushort)destPort);
        ms.Write(portBuf);
        ms.Write(ipBytes);

        // Embedded SOCKS4 userid (Proxifier "Authentication: UserID only")
        var userId = proxy.Username ?? "";
        var userBytes = Encoding.ASCII.GetBytes(userId);
        ms.Write(userBytes);
        ms.WriteByte(0x00);
        if (!string.IsNullOrEmpty(userId))
            auth = ProxyAuthMethod.Socks4UserId;

        if (hostname is not null)
        {
            var hostBytes = Encoding.ASCII.GetBytes(hostname);
            ms.Write(hostBytes);
            ms.WriteByte(0x00);
        }

        await socket.SendAsync(ms.ToArray(), SocketFlags.None, ct).ConfigureAwait(false);

        var resp = new byte[8];
        await ReadExactAsync(socket, resp, ct).ConfigureAwait(false);
        if (resp[0] != 0x00 || resp[1] != 0x5A)
        {
            var msg = resp[1] switch
            {
                0x5B => "SOCKS4 request rejected or failed",
                0x5C => "SOCKS4 rejected: cannot connect to identd on the client",
                0x5D => "SOCKS4 rejected: identd user-id mismatch",
                _ => $"SOCKS4 rejected (code 0x{resp[1]:X2})"
            };
            // TCP to the proxy already succeeded, so a plain rejection means the proxy could not
            // reach the requested target; identd codes are auth-related.
            var reason = resp[1] switch
            {
                0x5C or 0x5D => FailureReason.AuthenticationRequired,
                0x5B => FailureReason.TargetUnreachableThroughProxy,
                _ => FailureReason.ProxyHandshakeFailure
            };
            throw new SocksProtocolException(reason, msg);
        }

        return auth;
    }

    private static async Task<ProxyAuthMethod> Socks5HandshakeAsync(
        Socket socket,
        ParsedProxy proxy,
        string destHost,
        int destPort,
        SocksConnectOptions options,
        CancellationToken ct)
    {
        var authUsed = ProxyAuthMethod.None;
        var hasCreds = !string.IsNullOrEmpty(proxy.Username);

        // Always offer no-auth; also offer user/pass when credentials are embedded.
        byte[] greeting = hasCreds
            ? [0x05, 0x02, 0x00, 0x02]
            : [0x05, 0x01, 0x00];

        await socket.SendAsync(greeting, SocketFlags.None, ct).ConfigureAwait(false);
        var methodResp = new byte[2];
        await ReadExactAsync(socket, methodResp, ct).ConfigureAwait(false);
        if (methodResp[0] != 0x05)
            throw new SocksProtocolException(FailureReason.ProxyHandshakeFailure, "Not a SOCKS5 proxy");

        if (methodResp[1] == 0x02)
        {
            if (!hasCreds)
                throw new SocksProtocolException(FailureReason.AuthenticationRequired,
                    "The proxy server requires authentication. Provide username and password.");

            var userBytes = Encoding.UTF8.GetBytes(proxy.Username!);
            var passBytes = Encoding.UTF8.GetBytes(proxy.Password ?? "");
            if (userBytes.Length > 255 || passBytes.Length > 255)
                throw new SocksProtocolException(FailureReason.AuthenticationRequired, "Credentials too long");

            using var auth = new MemoryStream();
            auth.WriteByte(0x01);
            auth.WriteByte((byte)userBytes.Length);
            auth.Write(userBytes);
            auth.WriteByte((byte)passBytes.Length);
            auth.Write(passBytes);
            await socket.SendAsync(auth.ToArray(), SocketFlags.None, ct).ConfigureAwait(false);

            var authResp = new byte[2];
            await ReadExactAsync(socket, authResp, ct).ConfigureAwait(false);
            if (authResp[1] != 0x00)
                throw new SocksProtocolException(FailureReason.AuthenticationRequired,
                    "Authentication on the proxy server failed. Check username and password.");
            authUsed = ProxyAuthMethod.SocksUserPass;
        }
        else if (methodResp[1] == 0xFF)
        {
            throw new SocksProtocolException(FailureReason.AuthenticationRequired,
                "SOCKS5: no acceptable authentication method.");
        }
        else if (methodResp[1] != 0x00)
        {
            throw new SocksProtocolException(FailureReason.ProxyHandshakeFailure,
                $"SOCKS5 method 0x{methodResp[1]:X2} not supported");
        }

        using var req = new MemoryStream();
        req.WriteByte(0x05);
        req.WriteByte(0x01);
        req.WriteByte(0x00);

        if (IPAddress.TryParse(destHost, out var destIp) && !FakeIpDns.IsFakeIp(destIp))
        {
            if (destIp.AddressFamily == AddressFamily.InterNetwork)
            {
                req.WriteByte(0x01);
                req.Write(destIp.GetAddressBytes());
            }
            else
            {
                req.WriteByte(0x04);
                req.Write(destIp.GetAddressBytes());
            }
        }
        else
        {
            // Domain name — remote DNS (and Fake-IP reverse map already applied)
            if (!options.ResolveHostnamesThroughProxy && IPAddress.TryParse(destHost, out _) == false)
            {
                var addrs = await System.Net.Dns.GetHostAddressesAsync(destHost, ct).ConfigureAwait(false);
                var pick = addrs.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork)
                           ?? addrs.FirstOrDefault()
                           ?? throw new SocksProtocolException(FailureReason.DnsFailure, "DNS failed for destination.");
                if (pick.AddressFamily == AddressFamily.InterNetwork)
                {
                    req.WriteByte(0x01);
                    req.Write(pick.GetAddressBytes());
                }
                else
                {
                    req.WriteByte(0x04);
                    req.Write(pick.GetAddressBytes());
                }
            }
            else
            {
                var hostBytes = Encoding.ASCII.GetBytes(destHost);
                if (hostBytes.Length is 0 or > 255)
                    throw new SocksProtocolException(FailureReason.ProxyHandshakeFailure, "Hostname invalid/too long");
                req.WriteByte(0x03);
                req.WriteByte((byte)hostBytes.Length);
                req.Write(hostBytes);
            }
        }

        Span<byte> portBuf = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(portBuf, (ushort)destPort);
        req.Write(portBuf);
        await socket.SendAsync(req.ToArray(), SocketFlags.None, ct).ConfigureAwait(false);

        var header = new byte[4];
        await ReadExactAsync(socket, header, ct).ConfigureAwait(false);
        if (header[0] != 0x05)
            throw new SocksProtocolException(FailureReason.ProxyHandshakeFailure, "Invalid SOCKS5 reply");
        if (header[1] != 0x00)
            throw new SocksProtocolException(MapSocks5Reply(header[1]), $"SOCKS5 CONNECT failed (0x{header[1]:X2})");

        var addrLen = header[3] switch
        {
            0x01 => 4,
            0x03 => -1,
            0x04 => 16,
            _ => throw new SocksProtocolException(FailureReason.ProxyHandshakeFailure, "Unknown SOCKS5 ATYP")
        };

        if (addrLen == -1)
        {
            var lenBuf = new byte[1];
            await ReadExactAsync(socket, lenBuf, ct).ConfigureAwait(false);
            addrLen = lenBuf[0];
        }

        var rest = new byte[addrLen + 2];
        await ReadExactAsync(socket, rest, ct).ConfigureAwait(false);
        return authUsed;
    }

    private static FailureReason MapSocks5Reply(byte code) => code switch
    {
        // 0x03 network unreachable, 0x04 host unreachable, 0x05 connection refused by target:
        // the proxy was reached but could not complete the onward connection.
        0x03 or 0x04 or 0x05 => FailureReason.TargetUnreachableThroughProxy,
        0x02 => FailureReason.ConnectRefused,
        0x06 => FailureReason.DnsFailure,
        _ => FailureReason.ProxyHandshakeFailure
    };

    private static async Task ReadExactAsync(Socket socket, byte[] buffer, CancellationToken ct)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var n = await socket.ReceiveAsync(buffer.AsMemory(offset), SocketFlags.None, ct).ConfigureAwait(false);
            if (n == 0)
                throw new SocksProtocolException(FailureReason.EmptyResponse, "Connection closed during SOCKS handshake");
            offset += n;
        }
    }

    private sealed class SocksProtocolException(FailureReason reason, string message) : Exception(message)
    {
        public FailureReason Reason { get; } = reason;
    }
}
