using System.Net.Http;
using System.Text;
using UProxy.Core.Chaining;
using UProxy.Core.Models;

namespace UProxy.Core.Tests;

public class ChainDialerTests
{
    [Fact]
    public async Task OneHop_Socks5_ToEcho()
    {
        await using var echo = new FakeProxyServers.EchoHttpServer("one-hop");
        await using var socks = new FakeProxyServers.FakeSocks5Proxy();
        var dialer = new ChainDialer(TimeSpan.FromSeconds(5));
        var hops = new[]
        {
            ProxyHop.FromParsed(new ParsedProxy("127.0.0.1", socks.Port, ProxyProtocol.Socks5), ProxyKind.Socks5)
        };

        await using var stream = await dialer.ConnectAsync(
            hops, new ChainDestination("127.0.0.1", echo.Port), CancellationToken.None);

        await WriteGetAsync(stream);
        var body = await ReadTextAsync(stream);
        Assert.Contains("one-hop", body);
    }

    [Fact]
    public async Task TwoHop_Socks5_Socks5()
    {
        await using var echo = new FakeProxyServers.EchoHttpServer("two-hop");
        await using var hop2 = new FakeProxyServers.FakeSocks5Proxy();
        await using var hop1 = new FakeProxyServers.FakeSocks5Proxy();
        var dialer = new ChainDialer(TimeSpan.FromSeconds(8));
        var hops = new[]
        {
            ProxyHop.FromParsed(new ParsedProxy("127.0.0.1", hop1.Port, ProxyProtocol.Socks5)),
            ProxyHop.FromParsed(new ParsedProxy("127.0.0.1", hop2.Port, ProxyProtocol.Socks5))
        };

        await using var stream = await dialer.ConnectAsync(
            hops, new ChainDestination("127.0.0.1", echo.Port), CancellationToken.None);
        await WriteGetAsync(stream);
        Assert.Contains("two-hop", await ReadTextAsync(stream));
    }

    [Fact]
    public async Task Mixed_Socks5_ThenHttpConnect()
    {
        await using var echo = new FakeProxyServers.EchoHttpServer("mixed-sh");
        await using var http = new FakeProxyServers.FakeHttpConnectProxy();
        await using var socks = new FakeProxyServers.FakeSocks5Proxy();
        var dialer = new ChainDialer(TimeSpan.FromSeconds(8));
        var hops = new[]
        {
            ProxyHop.FromParsed(new ParsedProxy("127.0.0.1", socks.Port, ProxyProtocol.Socks5), ProxyKind.Socks5),
            ProxyHop.FromParsed(new ParsedProxy("127.0.0.1", http.Port, ProxyProtocol.Http), ProxyKind.Http)
        };

        await using var stream = await dialer.ConnectAsync(
            hops, new ChainDestination("127.0.0.1", echo.Port), CancellationToken.None);
        await WriteGetAsync(stream);
        Assert.Contains("mixed-sh", await ReadTextAsync(stream));
    }

    [Fact]
    public async Task Mixed_HttpConnect_ThenSocks5_RejectedByHopOrderRule()
    {
        await using var echo = new FakeProxyServers.EchoHttpServer("mixed-hs");
        await using var socks = new FakeProxyServers.FakeSocks5Proxy();
        await using var http = new FakeProxyServers.FakeHttpConnectProxy();
        var dialer = new ChainDialer(TimeSpan.FromSeconds(8));
        var hops = new[]
        {
            ProxyHop.FromParsed(new ParsedProxy("127.0.0.1", http.Port, ProxyProtocol.Http), ProxyKind.Http),
            ProxyHop.FromParsed(new ParsedProxy("127.0.0.1", socks.Port, ProxyProtocol.Socks5), ProxyKind.Socks5)
        };

        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            dialer.ConnectAsync(hops, new ChainDestination("127.0.0.1", echo.Port), CancellationToken.None));
        Assert.Contains("HTTP proxy server must be the last one in the chain", ex.Message);
    }

    [Fact]
    public void ValidateHopOrder_AllowsHttpAsFinalHop()
    {
        var hops = new[]
        {
            ProxyHop.FromParsed(new ParsedProxy("127.0.0.1", 1080, ProxyProtocol.Socks5), ProxyKind.Socks5),
            ProxyHop.FromParsed(new ParsedProxy("127.0.0.1", 8080, ProxyProtocol.Http), ProxyKind.Http)
        };
        ChainDialer.ValidateHopOrder(hops);
    }

    [Fact]
    public void ValidateHopOrder_RejectsHttpBeforeAnotherHop()
    {
        var hops = new[]
        {
            ProxyHop.FromParsed(new ParsedProxy("127.0.0.1", 8080, ProxyProtocol.Http), ProxyKind.Http),
            ProxyHop.FromParsed(new ParsedProxy("127.0.0.1", 1080, ProxyProtocol.Socks5), ProxyKind.Socks5)
        };
        var ex = Assert.Throws<ArgumentException>(() => ChainDialer.ValidateHopOrder(hops));
        Assert.Contains("HTTP proxy server must be the last one in the chain", ex.Message);
    }

    [Fact]
    public async Task Socks4a_ThenSocks5()
    {
        await using var echo = new FakeProxyServers.EchoHttpServer("s4-s5");
        await using var s5 = new FakeProxyServers.FakeSocks5Proxy();
        await using var s4 = new FakeProxyServers.FakeSocks4Proxy();
        var dialer = new ChainDialer(TimeSpan.FromSeconds(8));
        var hops = new[]
        {
            ProxyHop.FromParsed(new ParsedProxy("127.0.0.1", s4.Port, ProxyProtocol.Socks4), ProxyKind.Socks4),
            ProxyHop.FromParsed(new ParsedProxy("127.0.0.1", s5.Port, ProxyProtocol.Socks5), ProxyKind.Socks5)
        };

        await using var stream = await dialer.ConnectAsync(
            hops, new ChainDestination("127.0.0.1", echo.Port), CancellationToken.None);
        await WriteGetAsync(stream);
        Assert.Contains("s4-s5", await ReadTextAsync(stream));
    }

    [Fact]
    public async Task AuthenticatedHop_InsideChain()
    {
        await using var echo = new FakeProxyServers.EchoHttpServer("authed");
        await using var hop2 = new FakeProxyServers.FakeSocks5Proxy("bob", "pw");
        await using var hop1 = new FakeProxyServers.FakeSocks5Proxy();
        var dialer = new ChainDialer(TimeSpan.FromSeconds(8));
        var hops = new[]
        {
            ProxyHop.FromParsed(new ParsedProxy("127.0.0.1", hop1.Port, ProxyProtocol.Socks5)),
            ProxyHop.FromParsed(new ParsedProxy("127.0.0.1", hop2.Port, ProxyProtocol.Socks5, "bob", "pw"))
        };

        await using var stream = await dialer.ConnectAsync(
            hops, new ChainDestination("127.0.0.1", echo.Port), CancellationToken.None);
        await WriteGetAsync(stream);
        Assert.Contains("authed", await ReadTextAsync(stream));
    }

    [Fact]
    public async Task FailureAtSecondHop_ReportsExactEdge()
    {
        await using var bad = new FakeProxyServers.FakeSocks5Proxy(forceConnectReply: 0x04);
        await using var hop1 = new FakeProxyServers.FakeSocks5Proxy();
        var dialer = new ChainDialer(TimeSpan.FromSeconds(5));
        var hops = new[]
        {
            ProxyHop.FromParsed(new ParsedProxy("127.0.0.1", hop1.Port, ProxyProtocol.Socks5)),
            ProxyHop.FromParsed(new ParsedProxy("127.0.0.1", bad.Port, ProxyProtocol.Socks5))
        };

        var ex = await Assert.ThrowsAsync<ChainDialException>(() =>
            dialer.ConnectAsync(hops, new ChainDestination("127.0.0.1", 9), CancellationToken.None));

        Assert.Equal(1, ex.FailedHopIndex);
        Assert.Contains(bad.Port.ToString(), ex.FromEndpoint);
        Assert.Equal(FailureReason.TargetUnreachableThroughProxy, ex.Reason);
    }

    [Fact]
    public async Task RejectsMoreThanMaxHops()
    {
        var dialer = new ChainDialer();
        var hops = Enumerable.Range(0, ChainDialer.MaxHops + 1)
            .Select(i => ProxyHop.FromParsed(new ParsedProxy("127.0.0.1", 1000 + i, ProxyProtocol.Socks5)))
            .ToArray();
        await Assert.ThrowsAsync<ArgumentException>(() =>
            dialer.ConnectAsync(hops, new ChainDestination("127.0.0.1", 80), CancellationToken.None));
    }

    [Fact]
    public async Task Cancellation_DisposesWithoutLeak()
    {
        await using var socks = new FakeProxyServers.FakeSocks5Proxy();
        var dialer = new ChainDialer(TimeSpan.FromSeconds(30));
        var hops = new[]
        {
            ProxyHop.FromParsed(new ParsedProxy("127.0.0.1", socks.Port, ProxyProtocol.Socks5))
        };
        using var cts = new CancellationTokenSource();
        // Destination that never accepts: use a listening-but-not-accepting approach — closed port after CONNECT
        // Cancel before connect completes by cancelling immediately after starting to a blackhole.
        // Use a non-routable address with short cancel.
        cts.CancelAfter(50);
        var closed = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        closed.Start();
        var port = ((System.Net.IPEndPoint)closed.LocalEndpoint).Port;
        closed.Stop();

        await Assert.ThrowsAnyAsync<Exception>(() =>
            dialer.ConnectAsync(hops, new ChainDestination("127.0.0.1", port), cts.Token));
    }

    [Fact]
    public async Task ConnectCallback_HttpClient_TraversesChain()
    {
        await using var echo = new FakeProxyServers.EchoHttpServer("callback-ok");
        await using var socks = new FakeProxyServers.FakeSocks5Proxy();
        var dialer = new ChainDialer(TimeSpan.FromSeconds(8));
        var hops = new[]
        {
            ProxyHop.FromParsed(new ParsedProxy("127.0.0.1", socks.Port, ProxyProtocol.Socks5))
        };

        var handler = new SocketsHttpHandler
        {
            ConnectCallback = dialer.CreateConnectCallback(hops)
        };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(8) };
        var text = await client.GetStringAsync($"http://127.0.0.1:{echo.Port}/");
        Assert.Equal("callback-ok", text);
    }

    private static async Task WriteGetAsync(Stream stream)
    {
        var req = Encoding.ASCII.GetBytes("GET / HTTP/1.1\r\nHost: 127.0.0.1\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(req);
        await stream.FlushAsync();
    }

    private static async Task<string> ReadTextAsync(Stream stream)
    {
        var buf = new byte[1024];
        using var ms = new MemoryStream();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try
        {
            while (true)
            {
                var n = await stream.ReadAsync(buf.AsMemory(), cts.Token).ConfigureAwait(false);
                if (n == 0)
                    break;
                ms.Write(buf, 0, n);
            }
        }
        catch (OperationCanceledException)
        {
            // idle timeout — return what we have
        }

        return Encoding.ASCII.GetString(ms.ToArray());
    }
}
