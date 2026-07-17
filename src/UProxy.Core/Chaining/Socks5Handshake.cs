using System.Buffers.Binary;
using System.Net;
using System.Text;
using UProxy.Core.Dns;
using UProxy.Core.Models;

namespace UProxy.Core.Chaining;

/// <summary>SOCKS5 negotiation + CONNECT on an existing stream.</summary>
public static class Socks5Handshake
{
    public static async Task<ProxyAuthMethod> ConnectAsync(
        Stream stream,
        ParsedProxy proxy,
        string destHost,
        int destPort,
        HandshakeOptions? options = null,
        CancellationToken ct = default)
    {
        options ??= new HandshakeOptions();
        destHost = NormalizeHost(destHost);

        var authUsed = ProxyAuthMethod.None;
        var hasCreds = !string.IsNullOrEmpty(proxy.Username);

        byte[] greeting = hasCreds
            ? [0x05, 0x02, 0x00, 0x02]
            : [0x05, 0x01, 0x00];

        await stream.WriteAsync(greeting, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);

        var methodResp = new byte[2];
        await StreamProtocolReader.ReadExactAsync(stream, methodResp, ct).ConfigureAwait(false);
        if (methodResp[0] != 0x05)
            throw new ProxyHandshakeException(FailureReason.ProxyHandshakeFailure, "Not a SOCKS5 proxy");

        if (methodResp[1] == 0x02)
        {
            if (!hasCreds)
                throw new ProxyHandshakeException(FailureReason.AuthenticationRequired,
                    "The proxy server requires authentication. Provide username and password.");

            var userBytes = Encoding.UTF8.GetBytes(proxy.Username!);
            var passBytes = Encoding.UTF8.GetBytes(proxy.Password ?? "");
            if (userBytes.Length > 255 || passBytes.Length > 255)
                throw new ProxyHandshakeException(FailureReason.AuthenticationRequired, "Credentials too long");

            using var auth = new MemoryStream();
            auth.WriteByte(0x01);
            auth.WriteByte((byte)userBytes.Length);
            auth.Write(userBytes);
            auth.WriteByte((byte)passBytes.Length);
            auth.Write(passBytes);
            await stream.WriteAsync(auth.ToArray(), ct).ConfigureAwait(false);
            await stream.FlushAsync(ct).ConfigureAwait(false);

            var authResp = new byte[2];
            await StreamProtocolReader.ReadExactAsync(stream, authResp, ct).ConfigureAwait(false);
            if (authResp[1] != 0x00)
                throw new ProxyHandshakeException(FailureReason.AuthenticationRequired,
                    "Authentication on the proxy server failed. Check username and password.");
            authUsed = ProxyAuthMethod.SocksUserPass;
        }
        else if (methodResp[1] == 0xFF)
        {
            throw new ProxyHandshakeException(FailureReason.AuthenticationRequired,
                "SOCKS5: no acceptable authentication method.");
        }
        else if (methodResp[1] != 0x00)
        {
            throw new ProxyHandshakeException(FailureReason.ProxyHandshakeFailure,
                $"SOCKS5 method 0x{methodResp[1]:X2} not supported");
        }

        using var req = new MemoryStream();
        req.WriteByte(0x05);
        req.WriteByte(0x01); // CONNECT
        req.WriteByte(0x00);

        if (IPAddress.TryParse(destHost, out var destIp) && !FakeIpDns.IsFakeIp(destIp))
        {
            WriteIp(req, destIp);
        }
        else if (!options.ResolveHostnamesThroughProxy && !IPAddress.TryParse(destHost, out _))
        {
            var addrs = await System.Net.Dns.GetHostAddressesAsync(destHost, ct).ConfigureAwait(false);
            var pick = addrs.FirstOrDefault(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                       ?? addrs.FirstOrDefault()
                       ?? throw new ProxyHandshakeException(FailureReason.DnsFailure, "DNS failed for destination.");
            WriteIp(req, pick);
        }
        else
        {
            var hostBytes = Encoding.ASCII.GetBytes(destHost);
            if (hostBytes.Length is 0 or > 255)
                throw new ProxyHandshakeException(FailureReason.ProxyHandshakeFailure, "Hostname invalid/too long");
            req.WriteByte(0x03);
            req.WriteByte((byte)hostBytes.Length);
            req.Write(hostBytes);
        }

        Span<byte> portBuf = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(portBuf, (ushort)destPort);
        req.Write(portBuf);
        await stream.WriteAsync(req.ToArray(), ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);

        var header = new byte[4];
        await StreamProtocolReader.ReadExactAsync(stream, header, ct).ConfigureAwait(false);
        if (header[0] != 0x05)
            throw new ProxyHandshakeException(FailureReason.ProxyHandshakeFailure, "Invalid SOCKS5 reply");
        if (header[1] != 0x00)
            throw new ProxyHandshakeException(MapSocks5Reply(header[1]), $"SOCKS5 CONNECT failed (0x{header[1]:X2})");

        var addrLen = header[3] switch
        {
            0x01 => 4,
            0x03 => -1,
            0x04 => 16,
            _ => throw new ProxyHandshakeException(FailureReason.ProxyHandshakeFailure, "Unknown SOCKS5 ATYP")
        };

        if (addrLen == -1)
        {
            var lenBuf = new byte[1];
            await StreamProtocolReader.ReadExactAsync(stream, lenBuf, ct).ConfigureAwait(false);
            addrLen = lenBuf[0];
        }

        var rest = new byte[addrLen + 2];
        await StreamProtocolReader.ReadExactAsync(stream, rest, ct).ConfigureAwait(false);
        return authUsed;
    }

    private static void WriteIp(MemoryStream req, IPAddress destIp)
    {
        if (destIp.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
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

    private static FailureReason MapSocks5Reply(byte code) => code switch
    {
        0x03 or 0x04 or 0x05 => FailureReason.TargetUnreachableThroughProxy,
        0x02 => FailureReason.ConnectRefused,
        0x06 => FailureReason.DnsFailure,
        _ => FailureReason.ProxyHandshakeFailure
    };

    private static string NormalizeHost(string host) =>
        host.Trim().TrimStart('[').TrimEnd(']');
}
