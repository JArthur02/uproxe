using System.Buffers.Binary;
using System.Net;
using System.Text;
using UProxy.Core.Dns;
using UProxy.Core.Models;

namespace UProxy.Core.Chaining;

/// <summary>SOCKS4 / SOCKS4a CONNECT handshake on an existing stream.</summary>
public static class Socks4Handshake
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

        byte[] ipBytes;
        string? hostname = null;
        var auth = ProxyAuthMethod.None;

        if (IPAddress.TryParse(destHost, out var ip) &&
            ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork &&
            !FakeIpDns.IsFakeIp(ip))
        {
            ipBytes = ip.GetAddressBytes();
        }
        else if (options.UseSocks4a || options.ResolveHostnamesThroughProxy || !IPAddress.TryParse(destHost, out _))
        {
            ipBytes = [0, 0, 0, 1];
            hostname = destHost;
            if (!options.UseSocks4a && !options.ResolveHostnamesThroughProxy)
            {
                var addrs = await System.Net.Dns.GetHostAddressesAsync(destHost, ct).ConfigureAwait(false);
                var v4 = addrs.FirstOrDefault(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                         ?? throw new ProxyHandshakeException(FailureReason.DnsFailure, "No IPv4 for hostname (SOCKS4).");
                ipBytes = v4.GetAddressBytes();
                hostname = null;
            }
        }
        else
        {
            throw new ProxyHandshakeException(FailureReason.DnsFailure,
                "Could not resolve hostname through SOCKS 4. Enable SOCKS 4A extension or specify an IP address.");
        }

        using var ms = new MemoryStream();
        ms.WriteByte(0x04);
        ms.WriteByte(0x01); // CONNECT
        Span<byte> portBuf = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(portBuf, (ushort)destPort);
        ms.Write(portBuf);
        ms.Write(ipBytes);

        var userId = proxy.Username ?? "";
        var userBytes = Encoding.ASCII.GetBytes(userId);
        ms.Write(userBytes);
        ms.WriteByte(0x00);
        if (!string.IsNullOrEmpty(userId))
            auth = ProxyAuthMethod.Socks4UserId;

        if (hostname is not null)
        {
            ms.Write(Encoding.ASCII.GetBytes(hostname));
            ms.WriteByte(0x00);
        }

        await stream.WriteAsync(ms.ToArray(), ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);

        var resp = new byte[8];
        await StreamProtocolReader.ReadExactAsync(stream, resp, ct).ConfigureAwait(false);
        if (resp[0] != 0x00 || resp[1] != 0x5A)
        {
            var msg = resp[1] switch
            {
                0x5B => "SOCKS4 request rejected or failed",
                0x5C => "SOCKS4 rejected: cannot connect to identd on the client",
                0x5D => "SOCKS4 rejected: identd user-id mismatch",
                _ => $"SOCKS4 rejected (code 0x{resp[1]:X2})"
            };
            var reason = resp[1] switch
            {
                0x5C or 0x5D => FailureReason.AuthenticationRequired,
                0x5B => FailureReason.TargetUnreachableThroughProxy,
                _ => FailureReason.ProxyHandshakeFailure
            };
            throw new ProxyHandshakeException(reason, msg);
        }

        return auth;
    }

    private static string NormalizeHost(string host) =>
        host.Trim().TrimStart('[').TrimEnd(']');
}
