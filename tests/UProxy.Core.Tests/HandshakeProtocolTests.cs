using System.Text;
using UProxy.Core.Chaining;
using UProxy.Core.Models;

namespace UProxy.Core.Tests;

public class HandshakeProtocolTests
{
    [Fact]
    public async Task Socks5_NoAuth_ConnectsAndRelaysHttp()
    {
        await using var echo = new FakeProxyServers.EchoHttpServer("socks5-ok");
        await using var socks = new FakeProxyServers.FakeSocks5Proxy();

        using var client = new System.Net.Sockets.TcpClient();
        await client.ConnectAsync(System.Net.IPAddress.Loopback, socks.Port);
        await using var stream = client.GetStream();

        var auth = await Socks5Handshake.ConnectAsync(
            stream,
            new ParsedProxy("127.0.0.1", socks.Port, ProxyProtocol.Socks5),
            "127.0.0.1",
            echo.Port,
            new HandshakeOptions(),
            CancellationToken.None);

        Assert.Equal(ProxyAuthMethod.None, auth);

        var req = Encoding.ASCII.GetBytes($"GET / HTTP/1.1\r\nHost: 127.0.0.1\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(req);
        var text = await ReadUntilContainsAsync(stream, "socks5-ok");
        Assert.Contains("200", text);
        Assert.Contains("socks5-ok", text);
    }

    [Fact]
    public async Task Socks5_UserPass_SuccessAndRejection()
    {
        await using var echo = new FakeProxyServers.EchoHttpServer("auth-ok");
        await using var socks = new FakeProxyServers.FakeSocks5Proxy("alice", "secret");

        using var okClient = new System.Net.Sockets.TcpClient();
        await okClient.ConnectAsync(System.Net.IPAddress.Loopback, socks.Port);
        await using var okStream = okClient.GetStream();
        var auth = await Socks5Handshake.ConnectAsync(
            okStream,
            new ParsedProxy("127.0.0.1", socks.Port, ProxyProtocol.Socks5, "alice", "secret"),
            "127.0.0.1",
            echo.Port,
            ct: CancellationToken.None);
        Assert.Equal(ProxyAuthMethod.SocksUserPass, auth);

        using var badClient = new System.Net.Sockets.TcpClient();
        await badClient.ConnectAsync(System.Net.IPAddress.Loopback, socks.Port);
        await using var badStream = badClient.GetStream();
        var ex = await Assert.ThrowsAsync<ProxyHandshakeException>(() =>
            Socks5Handshake.ConnectAsync(
                badStream,
                new ParsedProxy("127.0.0.1", socks.Port, ProxyProtocol.Socks5, "alice", "wrong"),
                "127.0.0.1",
                echo.Port,
                ct: CancellationToken.None));
        Assert.Equal(FailureReason.AuthenticationRequired, ex.Reason);
    }

    [Fact]
    public async Task Socks5_ConnectFailure_MapsTargetUnreachable()
    {
        await using var socks = new FakeProxyServers.FakeSocks5Proxy(forceConnectReply: 0x05);
        using var client = new System.Net.Sockets.TcpClient();
        await client.ConnectAsync(System.Net.IPAddress.Loopback, socks.Port);
        await using var stream = client.GetStream();

        var ex = await Assert.ThrowsAsync<ProxyHandshakeException>(() =>
            Socks5Handshake.ConnectAsync(
                stream,
                new ParsedProxy("127.0.0.1", socks.Port),
                "127.0.0.1",
                9,
                ct: CancellationToken.None));
        Assert.Equal(FailureReason.TargetUnreachableThroughProxy, ex.Reason);
    }

    [Fact]
    public async Task Socks4a_Domain_Connects()
    {
        await using var echo = new FakeProxyServers.EchoHttpServer("socks4-ok");
        await using var socks = new FakeProxyServers.FakeSocks4Proxy();
        using var client = new System.Net.Sockets.TcpClient();
        await client.ConnectAsync(System.Net.IPAddress.Loopback, socks.Port);
        await using var stream = client.GetStream();

        await Socks4Handshake.ConnectAsync(
            stream,
            new ParsedProxy("127.0.0.1", socks.Port, ProxyProtocol.Socks4),
            "127.0.0.1",
            echo.Port,
            new HandshakeOptions { UseSocks4a = true },
            CancellationToken.None);

        var req = Encoding.ASCII.GetBytes("GET / HTTP/1.1\r\nHost: x\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(req);
        Assert.Contains("socks4-ok", await ReadUntilContainsAsync(stream, "socks4-ok"));
    }

    [Fact]
    public async Task HttpConnect_200_And_407()
    {
        await using var echo = new FakeProxyServers.EchoHttpServer("http-ok");
        await using var okProxy = new FakeProxyServers.FakeHttpConnectProxy();
        using var client = new System.Net.Sockets.TcpClient();
        await client.ConnectAsync(System.Net.IPAddress.Loopback, okProxy.Port);
        await using var stream = client.GetStream();

        var (code, _, _) = await HttpConnectHandshake.ConnectAsync(
            stream,
            new ParsedProxy("127.0.0.1", okProxy.Port, ProxyProtocol.Http),
            "127.0.0.1",
            echo.Port,
            ct: CancellationToken.None);
        Assert.Equal(200, code);

        await using var deny = new FakeProxyServers.FakeHttpConnectProxy(fixedStatus: 407);
        using var c2 = new System.Net.Sockets.TcpClient();
        await c2.ConnectAsync(System.Net.IPAddress.Loopback, deny.Port);
        await using var s2 = c2.GetStream();
        var ex = await Assert.ThrowsAsync<ProxyHandshakeException>(() =>
            HttpConnectHandshake.ConnectAsync(
                s2,
                new ParsedProxy("127.0.0.1", deny.Port),
                "example.com",
                443,
                ct: CancellationToken.None));
        Assert.Equal(FailureReason.AuthenticationRequired, ex.Reason);
    }

    [Fact]
    public async Task Handshake_CancelledDuringRead_Throws()
    {
        await using var socks = new FakeProxyServers.FakeSocks5Proxy();
        // Accept but never answer: connect then cancel before greeting reply is enough if we cancel immediately.
        using var client = new System.Net.Sockets.TcpClient();
        await client.ConnectAsync(System.Net.IPAddress.Loopback, socks.Port);
        await using var stream = client.GetStream();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Socks5Handshake.ConnectAsync(
                stream,
                new ParsedProxy("127.0.0.1", socks.Port),
                "127.0.0.1",
                80,
                ct: cts.Token));
    }

    private static async Task<string> ReadUntilContainsAsync(Stream stream, string needle)
    {
        var buf = new byte[256];
        using var ms = new MemoryStream();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while (true)
        {
            var n = await stream.ReadAsync(buf.AsMemory(), cts.Token);
            if (n == 0)
                break;
            ms.Write(buf, 0, n);
            var text = Encoding.ASCII.GetString(ms.ToArray());
            if (text.Contains(needle, StringComparison.Ordinal))
                return text;
        }

        return Encoding.ASCII.GetString(ms.ToArray());
    }
}
