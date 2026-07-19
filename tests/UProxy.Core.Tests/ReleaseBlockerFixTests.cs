using System.Net;
using System.Net.Sockets;
using System.Text;
using UProxy.Core.Chaining;
using UProxy.Core.Gateway;
using UProxy.Core.Models;

namespace UProxy.Core.Tests;

public class ReleaseBlockerFixTests
{
    [Fact]
    public void InferKind_Socks4And5_PrefersSocks5()
    {
        Assert.Equal(ProxyKind.Socks5, ProxyHop.InferKind(ProxyProtocol.Socks4And5));
        Assert.Equal(ProxyKind.Socks4, ProxyHop.InferKind(ProxyProtocol.Socks4));
    }

    [Fact]
    public void HttpConnect_FormatsIPv6WithBrackets()
    {
        Assert.Equal("[2001:db8::1]:443", HttpConnectHandshake.FormatConnectAuthority("2001:db8::1", 443));
        Assert.Equal("[2001:db8::1]:443", HttpConnectHandshake.FormatConnectAuthority("[2001:db8::1]", 443));
        Assert.Equal("example.com:443", HttpConnectHandshake.FormatConnectAuthority("example.com", 443));
    }

    [Fact]
    public void ChainDialer_FormatEndpoint_BracketsIPv6()
    {
        Assert.Equal("[::1]:1080", ChainDialer.FormatEndpoint("::1", 1080));
        Assert.Equal("127.0.0.1:1080", ChainDialer.FormatEndpoint("127.0.0.1", 1080));
    }

    [Fact]
    public void ExitIpChecker_ValidatesIpLiteral()
    {
        Assert.True(ExitIpChecker.IsIpLiteral("1.2.3.4"));
        Assert.True(ExitIpChecker.IsIpLiteral("2001:db8::1"));
        Assert.False(ExitIpChecker.IsIpLiteral("not-an-ip"));
        Assert.False(ExitIpChecker.IsIpLiteral("<html>"));
    }

    [Fact]
    public async Task ExitIpChecker_RejectsHttpUrl()
    {
        await Assert.ThrowsAsync<ArgumentException>(() =>
            ExitIpChecker.CheckAsync(
                (_, _) => ValueTask.FromResult<Stream>(Stream.Null),
                "http://api.ipify.org",
                CancellationToken.None));
    }

    [Fact]
    public async Task ChainManager_Verification_MarksDownAndFailsOver()
    {
        await using var good = new FakeProxyServers.FakeSocks5Proxy();
        await using var bad = new FakeProxyServers.FakeSocks5Proxy(forceConnectReply: 0x01);
        await using var echo = new FakeProxyServers.EchoHttpServer("failover-ok");

        var health = new ChainHealthTracker();
        var manager = new ChainManager(new ChainDialer(TimeSpan.FromSeconds(5)), health)
        {
            VerificationDestination = new ChainDestination("127.0.0.1", echo.Port)
        };

        var badHop = ProxyHop.FromParsed(new ParsedProxy("127.0.0.1", bad.Port, ProxyProtocol.Socks5));
        var goodHop = ProxyHop.FromParsed(new ParsedProxy("127.0.0.1", good.Port, ProxyProtocol.Socks5));
        var pool = new[]
        {
            new PoolCandidate(badHop, LatencyMs: 10, SuccessRate: 0.99),
            new PoolCandidate(goodHop, LatencyMs: 20, SuccessRate: 0.90)
        };

        manager.SwitchProfile(new ProxyChainProfile(
            Guid.NewGuid(), "fast", ChainMode.FastFailover, new[] { badHop }, "pool"), pool);

        // Drive three attributable failures → NeedsVerification → MarkDown.
        for (var i = 0; i < ChainHealthTracker.FailuresBeforeVerify; i++)
        {
            try
            {
                await using var _ = await manager.ConnectAsync(
                    new ChainDestination("127.0.0.1", echo.Port), CancellationToken.None);
            }
            catch (ChainDialException)
            {
                // expected while bad hop is selected
            }
        }

        Assert.True(health.IsInCooldown(badHop) || !health.IsHealthy(badHop));

        // After cooldown/mark-down, next connect should use the good hop.
        await using var stream = await manager.ConnectAsync(
            new ChainDestination("127.0.0.1", echo.Port), CancellationToken.None);
        var req = Encoding.ASCII.GetBytes("GET / HTTP/1.1\r\nHost: x\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(req);
        var buf = new byte[512];
        using var ms = new MemoryStream();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        try
        {
            int n;
            while ((n = await stream.ReadAsync(buf.AsMemory(), cts.Token)) > 0)
                ms.Write(buf, 0, n);
        }
        catch (OperationCanceledException) { /* ok */ }

        Assert.Contains("failover-ok", Encoding.ASCII.GetString(ms.ToArray()));
        Assert.Equal(goodHop.Endpoint, manager.GetActiveHops()[0].Endpoint);
    }

    [Fact]
    public async Task AutoTwoHop_RespectsTimeBudgetAndCancellation()
    {
        var hops = Enumerable.Range(0, 8)
            .Select(i => new PoolCandidate(
                ProxyHop.FromParsed(new ParsedProxy("127.0.0.1", 10000 + i, ProxyProtocol.Socks5)),
                LatencyMs: i))
            .ToList();

        using var cts = new CancellationTokenSource();
        var calls = 0;
        var result = await ChainSelectionPolicy.SelectAutoTwoHopPrivacyAsync(
            hops,
            async (_, _, ct) =>
            {
                Interlocked.Increment(ref calls);
                await Task.Delay(200, ct);
                return new TwoHopEdgeResult(true, 0.9, 100);
            },
            timeBudget: TimeSpan.FromMilliseconds(250),
            maxConcurrency: 2,
            cancellationToken: cts.Token);

        // Should not run all 8*7 directed pairs; budget cuts it short.
        Assert.True(calls < 56);
        // May or may not have found a pair depending on timing; just ensure no hang.
        _ = result;
    }

    [Fact]
    public void WindowsProxyManager_SaveBackupIfNoPending_DoesNotOverwrite()
    {
#pragma warning disable CA1416 // SaveBackupIfNoPending is pure file I/O; exerciseable off Windows
        var dir = Path.Combine(Path.GetTempPath(), "uproxy-backup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "wininet-proxy-backup.json");
        try
        {
            var mgr = new UProxy.Core.Windows.WindowsProxyManager(path);
            var original = new UProxy.Core.Windows.WinInetProxySnapshot
            {
                ProxyEnable = 0,
                ProxyServer = "ORIGINAL",
                ProxyOverride = "",
                AutoConfigURL = "http://pac.example/proxy.pac",
                CapturedAtUtc = DateTimeOffset.Parse("2020-01-01T00:00:00Z")
            };
            Assert.True(mgr.SaveBackupIfNoPending(original));
            Assert.True(mgr.HasPendingRestore);
            var before = File.ReadAllText(path);

            var overwritten = new UProxy.Core.Windows.WinInetProxySnapshot
            {
                ProxyEnable = 1,
                ProxyServer = "SHOULD-NOT-WRITE",
                CapturedAtUtc = DateTimeOffset.UtcNow
            };
            Assert.False(mgr.SaveBackupIfNoPending(overwritten));
            Assert.Equal(before, File.ReadAllText(path));
            Assert.Contains("ORIGINAL", File.ReadAllText(path));
            Assert.DoesNotContain("SHOULD-NOT-WRITE", File.ReadAllText(path));
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { /* ignore */ }
        }
#pragma warning restore CA1416
    }

    [Fact]
    public async Task FastFailover_AllCoolingDown_DoesNotBypassCooldown()
    {
        var health = new ChainHealthTracker();
        var manager = new ChainManager(new ChainDialer(TimeSpan.FromSeconds(2)), health);

        var hopA = ProxyHop.FromParsed(new ParsedProxy("127.0.0.1", 19901, ProxyProtocol.Socks5));
        var hopB = ProxyHop.FromParsed(new ParsedProxy("127.0.0.1", 19902, ProxyProtocol.Socks5));
        health.MarkDown(hopA);
        health.MarkDown(hopB);

        manager.SwitchProfile(new ProxyChainProfile(
            Guid.NewGuid(), "fast", ChainMode.FastFailover, new[] { hopA, hopB }, "pool"),
            new[] { new PoolCandidate(hopA), new PoolCandidate(hopB) });

        Assert.Empty(manager.GetActiveHops());
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            manager.ConnectAsync(new ChainDestination("127.0.0.1", 80), CancellationToken.None));
        Assert.Contains("cooling down", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DuplexRelay_TransportFault_EndsWithoutFullIdleWait()
    {
        await using var pair = await ConnectedPairForRelay.CreateAsync();
        var idle = TimeSpan.FromSeconds(30);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var relay = DuplexRelay.RunAsync(pair.LeftA, pair.RightA, idle, CancellationToken.None);

        // Deliver one byte so both directions are live, then abort one socket hard.
        await pair.LeftB.WriteAsync(new byte[] { 1 });
        await pair.LeftB.FlushAsync();
        var buf = new byte[8];
        Assert.Equal(1, await pair.RightB.ReadAsync(buf));

        pair.LeftB.Socket.LingerState = new LingerOption(true, 0);
        pair.LeftB.Socket.Close(); // RST — transport fault, not clean EOF

        await relay.WaitAsync(TimeSpan.FromSeconds(5));
        sw.Stop();
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10),
            $"Relay should abort on fault quickly, took {sw.Elapsed}");
    }

    [Fact]
    public void ProxyTransportTlsFailure_IsProxyAttributable()
    {
        Assert.True(
            ChainHealthTracker.IsProxyAttributable(
                FailureReason.ProxyTransportTlsFailure));

        Assert.False(
            ChainHealthTracker.IsProxyAttributable(
                FailureReason.TlsFailure));

        var tracker = new ChainHealthTracker();
        var hop = ProxyHop.FromParsed(
            new ParsedProxy("proxy.example", 443, ProxyProtocol.Https),
            ProxyKind.Http);

        for (var i = 0; i < ChainHealthTracker.FailuresBeforeVerify; i++)
        {
            tracker.RecordProxyFailure(
                hop,
                FailureReason.ProxyTransportTlsFailure);
        }

        Assert.True(tracker.NeedsVerification(hop));
    }

    private sealed class ConnectedPairForRelay : IAsyncDisposable
    {
        public required System.Net.Sockets.NetworkStream LeftA { get; init; }
        public required System.Net.Sockets.NetworkStream LeftB { get; init; }
        public required System.Net.Sockets.NetworkStream RightA { get; init; }
        public required System.Net.Sockets.NetworkStream RightB { get; init; }
        private System.Net.Sockets.TcpListener? _leftListener;
        private System.Net.Sockets.TcpListener? _rightListener;
        private System.Net.Sockets.TcpClient? _leftServer;
        private System.Net.Sockets.TcpClient? _leftClient;
        private System.Net.Sockets.TcpClient? _rightServer;
        private System.Net.Sockets.TcpClient? _rightClient;

        public static async Task<ConnectedPairForRelay> CreateAsync()
        {
            var leftListener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
            var rightListener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
            leftListener.Start();
            rightListener.Start();

            var leftClient = new System.Net.Sockets.TcpClient();
            var rightClient = new System.Net.Sockets.TcpClient();
            var leftAccept = leftListener.AcceptTcpClientAsync();
            var rightAccept = rightListener.AcceptTcpClientAsync();
            await leftClient.ConnectAsync((IPEndPoint)leftListener.LocalEndpoint);
            await rightClient.ConnectAsync((IPEndPoint)rightListener.LocalEndpoint);
            var leftServer = await leftAccept;
            var rightServer = await rightAccept;

            return new ConnectedPairForRelay
            {
                _leftListener = leftListener,
                _rightListener = rightListener,
                _leftServer = leftServer,
                _leftClient = leftClient,
                _rightServer = rightServer,
                _rightClient = rightClient,
                LeftA = leftServer.GetStream(),
                LeftB = leftClient.GetStream(),
                RightA = rightServer.GetStream(),
                RightB = rightClient.GetStream(),
            };
        }

        public async ValueTask DisposeAsync()
        {
            try { LeftB.Dispose(); } catch { /* ignore */ }
            try { RightB.Dispose(); } catch { /* ignore */ }
            _leftClient?.Dispose();
            _rightClient?.Dispose();
            _leftServer?.Dispose();
            _rightServer?.Dispose();
            _leftListener?.Stop();
            _rightListener?.Stop();
            await Task.CompletedTask;
        }
    }
}
