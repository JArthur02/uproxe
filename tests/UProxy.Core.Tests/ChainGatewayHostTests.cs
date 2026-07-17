using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using UProxy.Core.Chaining;
using UProxy.Core.Config;
using UProxy.Core.Gateway;
using UProxy.Core.Models;

namespace UProxy.Core.Tests;

public class ChainGatewayHostTests
{
    [Fact]
    public void FindFreePort_ReturnsUsableLoopbackPort()
    {
        var port = LoopbackPortFinder.FindFreePort();
        Assert.InRange(port, 1, 65_535);

        var listener = new TcpListener(IPAddress.Loopback, port);
        listener.Start();
        try
        {
            Assert.Equal(port, ((IPEndPoint)listener.LocalEndpoint).Port);
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public void PortInUseException_ExposesPortAndSuggested()
    {
        var ex = new PortInUseException(8877, 19001);
        Assert.Equal(8877, ex.Port);
        Assert.Equal(19001, ex.SuggestedPort);
        Assert.Contains("8877", ex.Message);
        Assert.Contains("19001", ex.Message);
    }

    [Fact]
    public void AppSettings_Clamp_ChainPortsAndExitUrl()
    {
        var s = new AppSettings
        {
            ChainHttpPort = 0,
            ChainSocksPort = 99_999,
            ExitIpCheckUrl = "   "
        };
        s.Clamp();
        Assert.Equal(1, s.ChainHttpPort);
        Assert.Equal(65_535, s.ChainSocksPort);
        Assert.Equal("https://api.ipify.org", s.ExitIpCheckUrl);
        Assert.Equal(2, AppSettings.CurrentVersion);
    }

    [Fact]
    public async Task Start_FailsWithPortInUse_WhenHttpPortOccupied()
    {
        var blocker = new TcpListener(IPAddress.Loopback, 0);
        blocker.Start();
        var occupied = ((IPEndPoint)blocker.LocalEndpoint).Port;
        try
        {
            await using var hop = new FakeProxyServers.FakeSocks5Proxy();
            var manager = CreateManager(hop.Port);
            await using var host = new ChainGatewayHost
            {
                HttpPort = occupied,
                SocksPort = LoopbackPortFinder.FindFreePort()
            };

            var ex = await Assert.ThrowsAsync<PortInUseException>(() =>
                host.StartAsync(manager, enableSystemProxy: false));

            Assert.Equal(occupied, ex.Port);
            Assert.NotNull(ex.SuggestedPort);
            Assert.InRange(ex.SuggestedPort!.Value, 1, 65_535);
            Assert.False(host.IsRunning);
        }
        finally
        {
            blocker.Stop();
        }
    }

    [Fact]
    public async Task StartStop_WithFakeSocks5_IsClean()
    {
        await using var echo = new FakeProxyServers.EchoHttpServer("gateway-host");
        await using var hop = new FakeProxyServers.FakeSocks5Proxy();
        var manager = CreateManager(hop.Port);

        var httpPort = LoopbackPortFinder.FindFreePort();
        var socksPort = LoopbackPortFinder.FindFreePort();
        // Ensure distinct ports (FindFreePort releases immediately; rare collision OK to retry).
        while (socksPort == httpPort)
            socksPort = LoopbackPortFinder.FindFreePort();

        await using var host = new ChainGatewayHost
        {
            HttpPort = httpPort,
            SocksPort = socksPort
        };

        await host.StartAsync(manager, enableSystemProxy: false);
        Assert.True(host.IsRunning);
        Assert.False(host.SystemProxyActive);
        Assert.Same(manager, host.Manager);

        // Smoke: HTTP CONNECT through gateway → chain → echo
        using (var client = new TcpClient())
        {
            await client.ConnectAsync(IPAddress.Loopback, host.HttpPort);
            await using var stream = client.GetStream();
            await HttpConnectAsync(stream, "127.0.0.1", echo.Port);
            await WriteGetAsync(stream);
            Assert.Contains("gateway-host", await ReadTextAsync(stream));
        }

        await host.StopAsync();
        Assert.False(host.IsRunning);
        Assert.Null(host.Manager);

        // Ports should be free again
        var listener = new TcpListener(IPAddress.Loopback, httpPort);
        listener.Start();
        listener.Stop();
    }

    [Fact]
    public async Task SwitchProfile_WhileRunning_KeepsListeners()
    {
        await using var echo = new FakeProxyServers.EchoHttpServer("after-switch");
        await using var hopA = new FakeProxyServers.FakeSocks5Proxy();
        await using var hopB = new FakeProxyServers.FakeSocks5Proxy();

        var manager = new ChainManager();
        var profileA = new ProxyChainProfile(
            Guid.NewGuid(),
            "A",
            ChainMode.StrictMultiHop,
            new[]
            {
                ProxyHop.FromParsed(new ParsedProxy("127.0.0.1", hopA.Port, ProxyProtocol.Socks5))
            });
        manager.SwitchProfile(profileA);

        var httpPort = LoopbackPortFinder.FindFreePort();
        var socksPort = LoopbackPortFinder.FindFreePort();
        while (socksPort == httpPort)
            socksPort = LoopbackPortFinder.FindFreePort();

        await using var host = new ChainGatewayHost
        {
            HttpPort = httpPort,
            SocksPort = socksPort
        };
        await host.StartAsync(manager, enableSystemProxy: false);

        var profileB = new ProxyChainProfile(
            Guid.NewGuid(),
            "B",
            ChainMode.StrictMultiHop,
            new[]
            {
                ProxyHop.FromParsed(new ParsedProxy("127.0.0.1", hopB.Port, ProxyProtocol.Socks5))
            });
        host.SwitchProfile(profileB);

        Assert.True(host.IsRunning);
        Assert.Equal("B", manager.ActiveProfile?.Name);
        Assert.Equal(httpPort, host.HttpPort);
        Assert.Equal(socksPort, host.SocksPort);

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, host.SocksPort);
        await using var stream = client.GetStream();
        await Socks5ConnectIpv4Async(stream, IPAddress.Loopback, echo.Port);
        await WriteGetAsync(stream);
        Assert.Contains("after-switch", await ReadTextAsync(stream));
    }

    [Fact]
    public async Task CancelledStart_DoesNotPreventLaterStart()
    {
        await using var hop = new FakeProxyServers.FakeSocks5Proxy();
        var manager = CreateManager(hop.Port);
        await using var host = new ChainGatewayHost
        {
            HttpPort = LoopbackPortFinder.FindFreePort(),
            SocksPort = LoopbackPortFinder.FindFreePort()
        };
        while (host.SocksPort == host.HttpPort)
            host.SocksPort = LoopbackPortFinder.FindFreePort();

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            host.StartAsync(manager, enableSystemProxy: false, cancelled.Token));

        Assert.False(host.IsRunning);
        Assert.Null(host.Manager);

        await host.StartAsync(manager, enableSystemProxy: false);
        Assert.True(host.IsRunning);
        Assert.Same(manager, host.Manager);
        await host.StopAsync();
    }

    [Fact]
    public async Task DoubleStop_AndDispose_AreSafe()
    {
        await using var hop = new FakeProxyServers.FakeSocks5Proxy();
        var manager = CreateManager(hop.Port);
        var host = new ChainGatewayHost
        {
            HttpPort = LoopbackPortFinder.FindFreePort(),
            SocksPort = LoopbackPortFinder.FindFreePort()
        };
        while (host.SocksPort == host.HttpPort)
            host.SocksPort = LoopbackPortFinder.FindFreePort();

        await host.StartAsync(manager, enableSystemProxy: false);
        await host.StopAsync();
        await host.StopAsync();
        await host.DisposeAsync();
        Assert.False(host.IsRunning);
    }

    private static ChainManager CreateManager(int socksHopPort)
    {
        var manager = new ChainManager();
        var profile = new ProxyChainProfile(
            Guid.NewGuid(),
            "direct-1hop",
            ChainMode.StrictMultiHop,
            new[]
            {
                ProxyHop.FromParsed(new ParsedProxy("127.0.0.1", socksHopPort, ProxyProtocol.Socks5))
            });
        manager.SwitchProfile(profile);
        return manager;
    }

    private static async Task HttpConnectAsync(NetworkStream stream, string host, int port)
    {
        var req = Encoding.ASCII.GetBytes(
            $"CONNECT {host}:{port} HTTP/1.1\r\nHost: {host}:{port}\r\n\r\n");
        await stream.WriteAsync(req);
        var text = await ReadHeadersAsTextAsync(stream);
        Assert.Contains("200", text.Split('\n')[0]);
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

    private static async Task WriteGetAsync(Stream stream)
    {
        var req = Encoding.ASCII.GetBytes(
            "GET / HTTP/1.1\r\nHost: 127.0.0.1\r\nConnection: close\r\n\r\n");
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
}
