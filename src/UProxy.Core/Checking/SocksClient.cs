using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using UProxy.Core.Models;

namespace UProxy.Core.Checking;

/// <summary>Minimal SOCKS4 / SOCKS5 client for connectivity checks (no external dependency).</summary>
public static class SocksClient
{
    public static async Task<(bool Ok, FailureReason Failure, string? Error, int LatencyMs)> ConnectAndHttpGetAsync(
        ParsedProxy proxy,
        ProxyProtocol socksVersion,
        string destinationHost,
        int destinationPort,
        string httpPath,
        int timeoutMs,
        CancellationToken ct)
    {
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
                var addresses = await Dns.GetHostAddressesAsync(proxy.Host, cts.Token).ConfigureAwait(false);
                proxyIp = addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork)
                          ?? addresses.FirstOrDefault()
                          ?? throw new SocketException((int)SocketError.HostNotFound);
            }

            await socket.ConnectAsync(new IPEndPoint(proxyIp, proxy.Port), cts.Token).ConfigureAwait(false);

            if (socksVersion == ProxyProtocol.Socks4)
                await Socks4HandshakeAsync(socket, destinationHost, destinationPort, cts.Token).ConfigureAwait(false);
            else
                await Socks5HandshakeAsync(socket, proxy, destinationHost, destinationPort, cts.Token).ConfigureAwait(false);

            // Send a tiny HTTP request and require a valid HTTP response status line.
            var request = Encoding.ASCII.GetBytes(
                $"GET {httpPath} HTTP/1.1\r\nHost: {destinationHost}\r\nConnection: close\r\nUser-Agent: μProxy-Tool/2.0\r\n\r\n");
            await socket.SendAsync(request, SocketFlags.None, cts.Token).ConfigureAwait(false);

            var buffer = new byte[512];
            var read = await socket.ReceiveAsync(buffer, SocketFlags.None, cts.Token).ConfigureAwait(false);
            sw.Stop();

            if (read < 12)
                return (false, FailureReason.EmptyResponse, "No HTTP response through SOCKS", (int)sw.ElapsedMilliseconds);

            var text = Encoding.ASCII.GetString(buffer, 0, read);
            if (!text.StartsWith("HTTP/", StringComparison.Ordinal))
                return (false, FailureReason.JudgeMismatch, "Non-HTTP response through SOCKS", (int)sw.ElapsedMilliseconds);

            return (true, FailureReason.None, null, (int)sw.ElapsedMilliseconds);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return (false, FailureReason.Cancelled, "Cancelled", (int)sw.ElapsedMilliseconds);
        }
        catch (OperationCanceledException)
        {
            return (false, FailureReason.Timeout, "Timed out", (int)sw.ElapsedMilliseconds);
        }
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionRefused)
        {
            return (false, FailureReason.ConnectRefused, ex.Message, (int)sw.ElapsedMilliseconds);
        }
        catch (SocketException ex) when (ex.SocketErrorCode is SocketError.TimedOut or SocketError.TryAgain)
        {
            return (false, FailureReason.ConnectTimeout, ex.Message, (int)sw.ElapsedMilliseconds);
        }
        catch (SocksProtocolException ex)
        {
            return (false, ex.Reason, ex.Message, (int)sw.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            return (false, FailureReason.UnknownError, ex.Message, (int)sw.ElapsedMilliseconds);
        }
    }

    private static async Task Socks4HandshakeAsync(Socket socket, string destHost, int destPort, CancellationToken ct)
    {
        // SOCKS4a: IP 0.0.0.x + hostname when dest is not an IPv4 literal
        byte[] ipBytes;
        string? hostname = null;
        if (IPAddress.TryParse(destHost, out var ip) && ip.AddressFamily == AddressFamily.InterNetwork)
        {
            ipBytes = ip.GetAddressBytes();
        }
        else
        {
            ipBytes = [0, 0, 0, 1];
            hostname = destHost;
        }

        using var ms = new MemoryStream();
        ms.WriteByte(0x04); // VER
        ms.WriteByte(0x01); // CONNECT
        Span<byte> portBuf = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(portBuf, (ushort)destPort);
        ms.Write(portBuf);
        ms.Write(ipBytes);
        ms.WriteByte(0x00); // userid empty
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
            throw new SocksProtocolException(FailureReason.ProxyHandshakeFailure, $"SOCKS4 rejected (code 0x{resp[1]:X2})");
    }

    private static async Task Socks5HandshakeAsync(
        Socket socket,
        ParsedProxy proxy,
        string destHost,
        int destPort,
        CancellationToken ct)
    {
        var needAuth = !string.IsNullOrEmpty(proxy.Username);
        byte[] greeting = needAuth
            ? [0x05, 0x02, 0x00, 0x02] // no-auth + user/pass
            : [0x05, 0x01, 0x00];

        await socket.SendAsync(greeting, SocketFlags.None, ct).ConfigureAwait(false);
        var methodResp = new byte[2];
        await ReadExactAsync(socket, methodResp, ct).ConfigureAwait(false);
        if (methodResp[0] != 0x05)
            throw new SocksProtocolException(FailureReason.ProxyHandshakeFailure, "Not a SOCKS5 proxy");

        if (methodResp[1] == 0x02)
        {
            if (string.IsNullOrEmpty(proxy.Username))
                throw new SocksProtocolException(FailureReason.AuthenticationRequired, "SOCKS5 requires authentication");

            var userBytes = Encoding.UTF8.GetBytes(proxy.Username);
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
                throw new SocksProtocolException(FailureReason.AuthenticationRequired, "SOCKS5 authentication failed");
        }
        else if (methodResp[1] == 0xFF)
        {
            throw new SocksProtocolException(FailureReason.AuthenticationRequired, "SOCKS5: no acceptable auth method");
        }
        else if (methodResp[1] != 0x00)
        {
            throw new SocksProtocolException(FailureReason.ProxyHandshakeFailure, $"SOCKS5 method 0x{methodResp[1]:X2} not supported");
        }

        // CONNECT request — prefer domain name for remote DNS (SOCKS5)
        using var req = new MemoryStream();
        req.WriteByte(0x05);
        req.WriteByte(0x01); // CONNECT
        req.WriteByte(0x00); // RSV

        if (IPAddress.TryParse(destHost, out var destIp))
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
            var hostBytes = Encoding.ASCII.GetBytes(destHost);
            if (hostBytes.Length > 255)
                throw new SocksProtocolException(FailureReason.ProxyHandshakeFailure, "Hostname too long");
            req.WriteByte(0x03);
            req.WriteByte((byte)hostBytes.Length);
            req.Write(hostBytes);
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
    }

    private static FailureReason MapSocks5Reply(byte code) => code switch
    {
        0x02 => FailureReason.ConnectRefused, // not allowed
        0x05 => FailureReason.ConnectRefused,
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
