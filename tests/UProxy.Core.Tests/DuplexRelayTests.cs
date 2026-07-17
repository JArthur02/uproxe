using System.Net;
using System.Net.Sockets;
using System.Text;
using UProxy.Core.Gateway;

namespace UProxy.Core.Tests;

public class DuplexRelayTests
{
    [Fact]
    public async Task HalfClose_LeftEof_RightCanStillRespond()
    {
        await using var pair = await ConnectedPair.CreateAsync();

        var relay = DuplexRelay.RunAsync(
            pair.LeftA,
            pair.RightA,
            TimeSpan.FromSeconds(5),
            CancellationToken.None);

        // Left client sends request then half-closes (FIN).
        var request = Encoding.ASCII.GetBytes("ping");
        await pair.LeftB.WriteAsync(request);
        await pair.LeftB.FlushAsync();
        pair.LeftB.Socket.Shutdown(SocketShutdown.Send);

        // Right side of relay should deliver request, then see EOF from left→right.
        var buf = new byte[64];
        var n = await ReadAtLeastAsync(pair.RightB, buf, request.Length, TimeSpan.FromSeconds(2));
        Assert.Equal("ping", Encoding.ASCII.GetString(buf, 0, n));

        // After left EOF, right can still send a response through the relay to left.
        var response = Encoding.ASCII.GetBytes("pong");
        await pair.RightB.WriteAsync(response);
        await pair.RightB.FlushAsync();

        n = await ReadAtLeastAsync(pair.LeftB, buf, response.Length, TimeSpan.FromSeconds(2));
        Assert.Equal("pong", Encoding.ASCII.GetString(buf, 0, n));

        // Close remaining sides so both copy tasks finish.
        pair.RightB.Socket.Shutdown(SocketShutdown.Send);
        pair.LeftB.Dispose();
        pair.RightB.Dispose();

        await relay.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Idle_ContinuousOneWayTraffic_DoesNotTimeout()
    {
        await using var pair = await ConnectedPair.CreateAsync();
        var idle = TimeSpan.FromMilliseconds(200);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var relay = DuplexRelay.RunAsync(pair.LeftA, pair.RightA, idle, cts.Token);

        var payload = Encoding.ASCII.GetBytes("x");
        var received = 0;
        var reader = Task.Run(async () =>
        {
            var buf = new byte[16];
            while (received < 8)
            {
                var n = await pair.RightB.ReadAsync(buf, cts.Token);
                if (n == 0)
                    break;
                received += n;
            }
        }, cts.Token);

        for (var i = 0; i < 8; i++)
        {
            await pair.LeftB.WriteAsync(payload, cts.Token);
            await pair.LeftB.FlushAsync(cts.Token);
            await Task.Delay(50, cts.Token);
        }

        await reader.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(8, received);
        Assert.False(relay.IsCompleted);

        pair.LeftB.Socket.Shutdown(SocketShutdown.Send);
        pair.RightB.Socket.Shutdown(SocketShutdown.Send);
        pair.LeftB.Dispose();
        pair.RightB.Dispose();

        await relay.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task FullDuplex_ThenClose_Works()
    {
        await using var pair = await ConnectedPair.CreateAsync();

        var relay = DuplexRelay.RunAsync(
            pair.LeftA,
            pair.RightA,
            TimeSpan.FromSeconds(5),
            CancellationToken.None);

        var leftMsg = Encoding.ASCII.GetBytes("hello-from-left");
        var rightMsg = Encoding.ASCII.GetBytes("hello-from-right");

        await pair.LeftB.WriteAsync(leftMsg);
        await pair.RightB.WriteAsync(rightMsg);
        await pair.LeftB.FlushAsync();
        await pair.RightB.FlushAsync();

        var buf = new byte[64];
        var nRight = await ReadAtLeastAsync(pair.RightB, buf, leftMsg.Length, TimeSpan.FromSeconds(2));
        Assert.Equal("hello-from-left", Encoding.ASCII.GetString(buf, 0, nRight));

        nRight = await ReadAtLeastAsync(pair.LeftB, buf, rightMsg.Length, TimeSpan.FromSeconds(2));
        Assert.Equal("hello-from-right", Encoding.ASCII.GetString(buf, 0, nRight));

        pair.LeftB.Socket.Shutdown(SocketShutdown.Send);
        pair.RightB.Socket.Shutdown(SocketShutdown.Send);
        pair.LeftB.Dispose();
        pair.RightB.Dispose();

        await relay.WaitAsync(TimeSpan.FromSeconds(5));
    }

    private static async Task<int> ReadAtLeastAsync(
        NetworkStream stream,
        byte[] buffer,
        int minBytes,
        TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        var total = 0;
        while (total < minBytes)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(total, buffer.Length - total), cts.Token);
            if (n == 0)
                break;
            total += n;
        }
        return total;
    }

    /// <summary>
    /// Two connected TCP pairs: LeftB↔LeftA (relay left) and RightB↔RightA (relay right).
    /// Relay copies between LeftA and RightA; tests drive LeftB / RightB.
    /// </summary>
    private sealed class ConnectedPair : IAsyncDisposable
    {
        public required NetworkStream LeftA { get; init; }
        public required NetworkStream LeftB { get; init; }
        public required NetworkStream RightA { get; init; }
        public required NetworkStream RightB { get; init; }
        private TcpListener? _leftListener;
        private TcpListener? _rightListener;
        private TcpClient? _leftServer;
        private TcpClient? _leftClient;
        private TcpClient? _rightServer;
        private TcpClient? _rightClient;

        public static async Task<ConnectedPair> CreateAsync()
        {
            var leftListener = new TcpListener(IPAddress.Loopback, 0);
            var rightListener = new TcpListener(IPAddress.Loopback, 0);
            leftListener.Start();
            rightListener.Start();

            var leftClient = new TcpClient();
            var rightClient = new TcpClient();
            var leftAccept = leftListener.AcceptTcpClientAsync();
            var rightAccept = rightListener.AcceptTcpClientAsync();
            await leftClient.ConnectAsync((IPEndPoint)leftListener.LocalEndpoint);
            await rightClient.ConnectAsync((IPEndPoint)rightListener.LocalEndpoint);
            var leftServer = await leftAccept;
            var rightServer = await rightAccept;

            return new ConnectedPair
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
            // Relay disposes LeftA/RightA; dispose clients and listeners here.
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
