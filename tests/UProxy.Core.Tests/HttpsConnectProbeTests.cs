using System.Net;
using System.Net.Sockets;
using System.Text;
using UProxy.Core.Checking;
using UProxy.Core.Config;
using UProxy.Core.Models;

namespace UProxy.Core.Tests;

public class HttpsConnectProbeTests
{
    /// <summary>Minimal fake HTTP proxy: accepts one CONNECT and replies with a fixed status line.</summary>
    private sealed class FakeConnectProxy : IAsyncDisposable
    {
        private readonly TcpListener _listener;
        private readonly Task _serve;

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        public FakeConnectProxy(string statusLine)
        {
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            _serve = Task.Run(async () =>
            {
                using var client = await _listener.AcceptTcpClientAsync().ConfigureAwait(false);
                using var stream = client.GetStream();
                var buf = new byte[1024];
                await stream.ReadAsync(buf).ConfigureAwait(false); // consume CONNECT request
                var resp = Encoding.ASCII.GetBytes(statusLine + "\r\n\r\n");
                await stream.WriteAsync(resp).ConfigureAwait(false);
                await stream.FlushAsync().ConfigureAwait(false);
            });
        }

        public async ValueTask DisposeAsync()
        {
            try { await _serve.ConfigureAwait(false); } catch { /* ignore */ }
            _listener.Stop();
        }
    }

    private static AppSettings FastSettings() => new() { TimeoutMs = 2_000, ConnectTimeoutMs = 2_000 };

    [Theory]
    [InlineData("HTTP/1.1 200 Connection established", true, FailureReason.None)]
    [InlineData("HTTP/1.1 403 Forbidden", false, FailureReason.HttpsConnectForbidden)]
    [InlineData("HTTP/1.1 405 Method Not Allowed", false, FailureReason.HttpsConnectForbidden)]
    [InlineData("HTTP/1.1 407 Proxy Authentication Required", false, FailureReason.AuthenticationRequired)]
    [InlineData("HTTP/1.1 502 Bad Gateway", false, FailureReason.TargetUnreachableThroughProxy)]
    public async Task ProbeHttps_ClassifiesConnectStatusLine(string statusLine, bool expectOk, FailureReason expected)
    {
        await using var proxy = new FakeConnectProxy(statusLine);
        var client = new JudgeClient(FastSettings());

        var (ok, failure, _) = await client.ProbeHttpsAsync(
            new ParsedProxy("127.0.0.1", proxy.Port, ProxyProtocol.Http), CancellationToken.None);

        Assert.Equal(expectOk, ok);
        Assert.Equal(expected, failure);
    }

    [Fact]
    public async Task ProbeHttps_ConnectRefused_WhenProxyPortClosed()
    {
        // Reserve then release a port so nothing is listening on it → connection refused.
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var closedPort = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();

        var client = new JudgeClient(FastSettings());
        var (ok, failure, _) = await client.ProbeHttpsAsync(
            new ParsedProxy("127.0.0.1", closedPort, ProxyProtocol.Http), CancellationToken.None);

        Assert.False(ok);
        Assert.Equal(FailureReason.ConnectRefused, failure);
    }
}
