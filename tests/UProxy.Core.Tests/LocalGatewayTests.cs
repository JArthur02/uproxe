using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using UProxy.Core.Chaining;
using UProxy.Core.Gateway;
using UProxy.Core.Models;

namespace UProxy.Core.Tests;

public class LocalGatewayTests
{
    [Fact]
    public async Task Socks5_DomainConnect_ThroughOneHop_PreservesDomain()
    {
        await using var echo = new FakeProxyServers.EchoHttpServer("socks-domain");
        var seen = new List<ChainDestination>();
        var direct = new DirectTcpConnector();
        // Record at gateway edge — destination host must remain the domain (no local resolve).
        var domainConnector = new FuncConnector(async (dest, ct) =>
        {
            seen.Add(dest);
            Assert.Equal("app.example.test", dest.Host);
            return await direct.ConnectAsync(new ChainDestination("127.0.0.1", echo.Port), ct);
        });

        await using var gateway = new LocalSocks5Server(domainConnector, IPAddress.Loopback, port: 0,
            idleTimeout: TimeSpan.FromSeconds(10));
        await gateway.StartAsync();

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, gateway.Port);
        await using var stream = client.GetStream();

        await Socks5ConnectDomainAsync(stream, "app.example.test", echo.Port);
        await WriteGetAsync(stream, "app.example.test");
        var body = await ReadTextAsync(stream);
        Assert.Contains("socks-domain", body);
        Assert.Contains(seen, d => d.Host == "app.example.test");
    }

    [Fact]
    public async Task Socks5_DomainConnect_ThroughOneHop_ChainDialer()
    {
        await using var echo = new FakeProxyServers.EchoHttpServer("socks-1hop");
        await using var hop = new FakeProxyServers.FakeSocks5Proxy();
        var dialer = new ChainDialer(TimeSpan.FromSeconds(8));
        var hops = new[]
        {
            ProxyHop.FromParsed(new ParsedProxy("127.0.0.1", hop.Port, ProxyProtocol.Socks5))
        };
        var connector = new ChainDialerConnector(dialer, hops);

        await using var gateway = new LocalSocks5Server(connector, IPAddress.Loopback, port: 0,
            idleTimeout: TimeSpan.FromSeconds(10));
        await gateway.StartAsync();

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, gateway.Port);
        await using var stream = client.GetStream();

        // Domain form of loopback — FakeSocks5 resolves "localhost"
        await Socks5ConnectDomainAsync(stream, "localhost", echo.Port);
        await WriteGetAsync(stream, "localhost");
        Assert.Contains("socks-1hop", await ReadTextAsync(stream));
    }

    [Fact]
    public async Task HttpConnect_ThroughOneHop()
    {
        await using var echo = new FakeProxyServers.EchoHttpServer("http-1hop");
        await using var hop = new FakeProxyServers.FakeSocks5Proxy();
        var dialer = new ChainDialer(TimeSpan.FromSeconds(8));
        var hops = new[]
        {
            ProxyHop.FromParsed(new ParsedProxy("127.0.0.1", hop.Port, ProxyProtocol.Socks5))
        };
        var connector = new ChainDialerConnector(dialer, hops);

        await using var gateway = new LocalHttpProxyServer(connector, IPAddress.Loopback, port: 0,
            idleTimeout: TimeSpan.FromSeconds(10));
        await gateway.StartAsync();

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, gateway.Port);
        await using var stream = client.GetStream();

        await HttpConnectAsync(stream, "127.0.0.1", echo.Port);
        await WriteGetAsync(stream, "127.0.0.1");
        Assert.Contains("http-1hop", await ReadTextAsync(stream));
    }

    [Fact]
    public async Task HttpConnect_ThroughTwoHops()
    {
        await using var echo = new FakeProxyServers.EchoHttpServer("http-2hop");
        await using var hop2 = new FakeProxyServers.FakeSocks5Proxy();
        await using var hop1 = new FakeProxyServers.FakeHttpConnectProxy();
        var dialer = new ChainDialer(TimeSpan.FromSeconds(10));
        var hops = new[]
        {
            ProxyHop.FromParsed(new ParsedProxy("127.0.0.1", hop1.Port, ProxyProtocol.Http), ProxyKind.Http),
            ProxyHop.FromParsed(new ParsedProxy("127.0.0.1", hop2.Port, ProxyProtocol.Socks5), ProxyKind.Socks5)
        };
        var connector = new ChainDialerConnector(dialer, hops);

        await using var gateway = new LocalHttpProxyServer(connector, IPAddress.Loopback, port: 0,
            idleTimeout: TimeSpan.FromSeconds(10));
        await gateway.StartAsync();

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, gateway.Port);
        await using var stream = client.GetStream();

        await HttpConnectAsync(stream, "127.0.0.1", echo.Port);
        await WriteGetAsync(stream, "127.0.0.1");
        Assert.Contains("http-2hop", await ReadTextAsync(stream));
    }

    [Fact]
    public async Task Concurrent_Clients_OnSocks5Gateway()
    {
        await using var echo = new FakeProxyServers.EchoHttpServer("concurrent");
        var connector = new DirectTcpConnector();
        await using var gateway = new LocalSocks5Server(connector, IPAddress.Loopback, port: 0,
            idleTimeout: TimeSpan.FromSeconds(10));
        await gateway.StartAsync();

        async Task OneAsync(int id)
        {
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, gateway.Port);
            await using var stream = client.GetStream();
            await Socks5ConnectIpv4Async(stream, IPAddress.Loopback, echo.Port);
            await WriteGetAsync(stream, "127.0.0.1");
            var body = await ReadTextAsync(stream);
            Assert.Contains("concurrent", body);
        }

        await Task.WhenAll(Enumerable.Range(0, 8).Select(OneAsync));
    }

    [Fact]
    public async Task PortOccupied_StartThrows()
    {
        var blocker = new TcpListener(IPAddress.Loopback, 0);
        blocker.Start();
        var port = ((IPEndPoint)blocker.LocalEndpoint).Port;
        try
        {
            await using var gateway = new LocalSocks5Server(new DirectTcpConnector(), IPAddress.Loopback, port,
                idleTimeout: TimeSpan.FromSeconds(5));
            await Assert.ThrowsAnyAsync<SocketException>(() => gateway.StartAsync());
        }
        finally
        {
            blocker.Stop();
        }
    }

    [Fact]
    public async Task Socks5_BindAndUdpAssociate_Rejected()
    {
        var connector = new DirectTcpConnector();
        await using var gateway = new LocalSocks5Server(connector, IPAddress.Loopback, port: 0,
            idleTimeout: TimeSpan.FromSeconds(5));
        await gateway.StartAsync();

        foreach (byte cmd in new byte[] { 0x02, 0x03 })
        {
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, gateway.Port);
            await using var stream = client.GetStream();

            await stream.WriteAsync(new byte[] { 0x05, 0x01, 0x00 });
            var method = new byte[2];
            await ReadExact(stream, method);
            Assert.Equal(new byte[] { 0x05, 0x00 }, method);

            // VER CMD RSV ATYP IPv4 PORT
            var req = new byte[] { 0x05, cmd, 0x00, 0x01, 127, 0, 0, 1, 0, 80 };
            await stream.WriteAsync(req);
            var reply = new byte[10];
            await ReadExact(stream, reply);
            Assert.Equal(0x05, reply[0]);
            Assert.Equal(0x07, reply[1]); // Command not supported
        }
    }

    [Fact]
    public void LoopbackOnly_ConstructorRejectsNonLoopback()
    {
        Assert.Throws<ArgumentException>(() =>
            new LocalSocks5Server(new DirectTcpConnector(), IPAddress.Any, 0));
        Assert.Throws<ArgumentException>(() =>
            new LocalHttpProxyServer(new DirectTcpConnector(), IPAddress.Parse("192.168.1.1"), 0));
    }

    [Fact]
    public async Task HttpProxy_SelfRoute_Rejected()
    {
        var connector = new DirectTcpConnector();
        await using var gateway = new LocalHttpProxyServer(connector, IPAddress.Loopback, port: 0,
            idleTimeout: TimeSpan.FromSeconds(5));
        await gateway.StartAsync();

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, gateway.Port);
        await using var stream = client.GetStream();

        var req = Encoding.ASCII.GetBytes(
            $"CONNECT 127.0.0.1:{gateway.Port} HTTP/1.1\r\nHost: 127.0.0.1:{gateway.Port}\r\n\r\n");
        await stream.WriteAsync(req);
        var text = await ReadTextAsync(stream);
        Assert.StartsWith("HTTP/1.1 403", text);
    }

    [Fact]
    public async Task HttpProxy_AbsoluteFormGet()
    {
        await using var echo = new FakeProxyServers.EchoHttpServer("absolute-get");
        var connector = new DirectTcpConnector();
        await using var gateway = new LocalHttpProxyServer(connector, IPAddress.Loopback, port: 0,
            idleTimeout: TimeSpan.FromSeconds(10));
        await gateway.StartAsync();

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, gateway.Port);
        await using var stream = client.GetStream();

        var req = Encoding.ASCII.GetBytes(
            $"GET http://127.0.0.1:{echo.Port}/ HTTP/1.1\r\nHost: 127.0.0.1:{echo.Port}\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(req);
        Assert.Contains("absolute-get", await ReadTextAsync(stream));
    }

    [Fact]
    public void HttpProxyRequestParser_RejectsCrlfInjectionAndOversizedAuthority()
    {
        Assert.Throws<HttpProxyParseException>(() =>
            HttpProxyRequestParser.Parse("GET http://evil.com/%0d%0aX: y HTTP/1.1\r\nHost: evil.com\r\n\r\n"));

        Assert.False(HttpProxyRequestParser.TryParseAuthority("host\r\nX: injected:80", out _, out _));
        Assert.False(HttpProxyRequestParser.TryParseAuthority("host:99999", out _, out _));

        var ok = HttpProxyRequestParser.TryParseAbsoluteUri(
            "http://example.com:8080/a?b=1", out var host, out var port, out var origin);
        Assert.True(ok);
        Assert.Equal("example.com", host);
        Assert.Equal(8080, port);
        Assert.Equal("/a?b=1", origin);
    }

    [Fact]
    public async Task HttpProxy_OversizedHeaders_Rejected()
    {
        var connector = new DirectTcpConnector();
        await using var gateway = new LocalHttpProxyServer(connector, IPAddress.Loopback, port: 0,
            idleTimeout: TimeSpan.FromSeconds(5));
        await gateway.StartAsync();

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, gateway.Port);
        await using var stream = client.GetStream();

        var sb = new StringBuilder();
        sb.Append("GET http://127.0.0.1:9/ HTTP/1.1\r\nHost: 127.0.0.1:9\r\n");
        while (sb.Length < HttpProxyRequestParser.MaxHeaderBytes + 100)
            sb.Append("X-Pad: ").Append(new string('a', 200)).Append("\r\n");
        // Never send final CRLFCRLF — keep writing past limit; parser should abort.
        var bytes = Encoding.ASCII.GetBytes(sb.ToString());
        await stream.WriteAsync(bytes);
        var text = await ReadTextAsync(stream);
        Assert.StartsWith("HTTP/1.1 400", text);
    }

    private static async Task Socks5ConnectDomainAsync(NetworkStream stream, string host, int port)
    {
        await stream.WriteAsync(new byte[] { 0x05, 0x01, 0x00 });
        var method = new byte[2];
        await ReadExact(stream, method);
        Assert.Equal(0x05, method[0]);
        Assert.Equal(0x00, method[1]);

        var hostBytes = Encoding.ASCII.GetBytes(host);
        using var ms = new MemoryStream();
        ms.WriteByte(0x05);
        ms.WriteByte(0x01);
        ms.WriteByte(0x00);
        ms.WriteByte(0x03);
        ms.WriteByte((byte)hostBytes.Length);
        ms.Write(hostBytes);
        Span<byte> portBuf = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(portBuf, (ushort)port);
        ms.Write(portBuf);
        await stream.WriteAsync(ms.ToArray());

        var replyHead = new byte[4];
        await ReadExact(stream, replyHead);
        Assert.Equal(0x05, replyHead[0]);
        Assert.Equal(0x00, replyHead[1]);
        await DrainSocks5BoundAsync(stream, replyHead[3]);
    }

    private static async Task Socks5ConnectIpv4Async(NetworkStream stream, IPAddress ip, int port)
    {
        await stream.WriteAsync(new byte[] { 0x05, 0x01, 0x00 });
        var method = new byte[2];
        await ReadExact(stream, method);
        Assert.Equal(new byte[] { 0x05, 0x00 }, method);

        var ipBytes = ip.GetAddressBytes();
        using var ms = new MemoryStream();
        ms.WriteByte(0x05);
        ms.WriteByte(0x01);
        ms.WriteByte(0x00);
        ms.WriteByte(0x01);
        ms.Write(ipBytes);
        Span<byte> portBuf = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(portBuf, (ushort)port);
        ms.Write(portBuf);
        await stream.WriteAsync(ms.ToArray());

        var replyHead = new byte[4];
        await ReadExact(stream, replyHead);
        Assert.Equal(0x00, replyHead[1]);
        await DrainSocks5BoundAsync(stream, replyHead[3]);
    }

    private static async Task DrainSocks5BoundAsync(Stream stream, byte atyp)
    {
        int addrLen = atyp switch
        {
            0x01 => 4,
            0x04 => 16,
            0x03 => -1,
            _ => throw new InvalidOperationException("bad atyp")
        };
        if (addrLen < 0)
        {
            var len = new byte[1];
            await ReadExact(stream, len);
            addrLen = len[0];
        }
        var rest = new byte[addrLen + 2];
        await ReadExact(stream, rest);
    }

    private static async Task HttpConnectAsync(NetworkStream stream, string host, int port)
    {
        var req = Encoding.ASCII.GetBytes(
            $"CONNECT {host}:{port} HTTP/1.1\r\nHost: {host}:{port}\r\n\r\n");
        await stream.WriteAsync(req);
        var text = await ReadHeadersAsTextAsync(stream);
        Assert.Contains("200", text.Split('\n')[0]);
    }

    private static async Task WriteGetAsync(Stream stream, string host)
    {
        var req = Encoding.ASCII.GetBytes(
            $"GET / HTTP/1.1\r\nHost: {host}\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(req);
        await stream.FlushAsync();
    }

    private static async Task ReadExact(Stream stream, byte[] buffer)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(offset));
            if (n == 0)
                throw new EndOfStreamException();
            offset += n;
        }
    }

    private static async Task<string> ReadHeadersAsTextAsync(Stream stream)
    {
        using var ms = new MemoryStream();
        var buf = new byte[1];
        while (ms.Length < 8192)
        {
            var n = await stream.ReadAsync(buf);
            if (n == 0)
                break;
            ms.WriteByte(buf[0]);
            if (ms.Length >= 4)
            {
                var a = ms.ToArray();
                var i = a.Length - 4;
                if (a[i] == '\r' && a[i + 1] == '\n' && a[i + 2] == '\r' && a[i + 3] == '\n')
                    break;
            }
        }
        return Encoding.ASCII.GetString(ms.ToArray());
    }

    private static async Task<string> ReadTextAsync(Stream stream)
    {
        var buf = new byte[1024];
        using var ms = new MemoryStream();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        try
        {
            while (true)
            {
                var n = await stream.ReadAsync(buf.AsMemory(), cts.Token);
                if (n == 0)
                    break;
                ms.Write(buf, 0, n);
            }
        }
        catch (OperationCanceledException)
        {
            // return what we have
        }
        return Encoding.ASCII.GetString(ms.ToArray());
    }

    private sealed class FuncConnector : IChainConnector
    {
        private readonly Func<ChainDestination, CancellationToken, Task<Stream>> _fn;
        public FuncConnector(Func<ChainDestination, CancellationToken, Task<Stream>> fn) => _fn = fn;
        public Task<Stream> ConnectAsync(ChainDestination destination, CancellationToken cancellationToken) =>
            _fn(destination, cancellationToken);
    }
}
