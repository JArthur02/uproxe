using System.Net;
using System.Net.Sockets;
using System.Text;
using UProxy.Core.Models;

namespace UProxy.Core.Gateway;

/// <summary>
/// Loopback-only HTTP proxy gateway: CONNECT tunnels and absolute-form requests
/// (dial host:port then forward origin-form). No TLS interception.
/// </summary>
public sealed class LocalHttpProxyServer : IAsyncDisposable
{
    public const int DefaultPort = 8877;

    private readonly IChainConnector _connector;
    private readonly IPAddress _bindAddress;
    private readonly int _requestedPort;
    private readonly TimeSpan _idleTimeout;

    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _acceptLoop;
    private int _started;

    public LocalHttpProxyServer(
        IChainConnector connector,
        IPAddress? bindAddress = null,
        int port = DefaultPort,
        TimeSpan? idleTimeout = null)
    {
        _connector = connector ?? throw new ArgumentNullException(nameof(connector));
        _bindAddress = bindAddress ?? IPAddress.Loopback;
        if (!IsLoopback(_bindAddress))
            throw new ArgumentException(
                "Local HTTP proxy gateway may only bind to a loopback address.", nameof(bindAddress));
        if (port is < 0 or > 65535)
            throw new ArgumentOutOfRangeException(nameof(port));
        _requestedPort = port;
        _idleTimeout = idleTimeout ?? TimeSpan.FromMinutes(5);
        if (_idleTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(idleTimeout));
    }

    public IPEndPoint? LocalEndpoint => _listener is null
        ? null
        : (IPEndPoint)_listener.LocalEndpoint;

    public int Port => LocalEndpoint?.Port ?? _requestedPort;

    public bool IsRunning => _started != 0 && _listener is not null;

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.CompareExchange(ref _started, 1, 0) != 0)
            throw new InvalidOperationException("HTTP proxy gateway is already running.");

        cancellationToken.ThrowIfCancellationRequested();
        _cts = new CancellationTokenSource();
        try
        {
            _listener = new TcpListener(_bindAddress, _requestedPort);
            _listener.Start();
        }
        catch
        {
            Interlocked.Exchange(ref _started, 0);
            _cts.Dispose();
            _cts = null;
            _listener = null;
            throw;
        }

        _acceptLoop = Task.Run(() => AcceptLoopAsync(_cts.Token));
        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        if (Interlocked.Exchange(ref _started, 0) == 0)
            return;

        try { _cts?.Cancel(); } catch { /* ignore */ }
        try { _listener?.Stop(); } catch { /* ignore */ }

        if (_acceptLoop is not null)
        {
            try { await _acceptLoop.ConfigureAwait(false); }
            catch { /* ignore */ }
        }

        _acceptLoop = null;
        _listener = null;
        _cts?.Dispose();
        _cts = null;
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient? client = null;
            try
            {
                client = await _listener!.AcceptTcpClientAsync(ct).ConfigureAwait(false);
                var captured = client;
                _ = Task.Run(() => HandleClientAsync(captured, ct), CancellationToken.None);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                client?.Dispose();
                break;
            }
            catch (ObjectDisposedException) when (ct.IsCancellationRequested)
            {
                client?.Dispose();
                break;
            }
            catch (SocketException) when (ct.IsCancellationRequested)
            {
                client?.Dispose();
                break;
            }
            catch
            {
                client?.Dispose();
            }
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken serverCt)
    {
        using var clientLifetime = client;
        client.NoDelay = true;
        Stream? clientStream = null;
        Stream? remoteStream = null;
        try
        {
            clientStream = client.GetStream();
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(serverCt);
            linked.CancelAfter(_idleTimeout);
            var ct = linked.Token;

            byte[] headerBytes;
            try
            {
                headerBytes = await HttpProxyRequestParser.ReadHeadersAsync(clientStream, ct)
                    .ConfigureAwait(false);
            }
            catch (HttpProxyParseException)
            {
                await WriteSimpleResponseAsync(clientStream, 400, "Bad Request", ct).ConfigureAwait(false);
                return;
            }
            catch (OperationCanceledException) when (!serverCt.IsCancellationRequested)
            {
                await WriteSimpleResponseAsync(clientStream, 408, "Request Timeout", CancellationToken.None)
                    .ConfigureAwait(false);
                return;
            }

            HttpProxyRequestParser.ParsedProxyRequest request;
            try
            {
                request = HttpProxyRequestParser.Parse(headerBytes);
            }
            catch (HttpProxyParseException)
            {
                await WriteSimpleResponseAsync(clientStream, 400, "Bad Request", ct).ConfigureAwait(false);
                return;
            }

            var local = LocalEndpoint;
            if (local is not null &&
                HttpProxyRequestParser.IsSameEndpoint(request.Host, request.Port, local))
            {
                await WriteSimpleResponseAsync(clientStream, 403, "Forbidden", ct).ConfigureAwait(false);
                return;
            }

            try
            {
                remoteStream = await _connector
                    .ConnectAsync(new ChainDestination(request.Host, request.Port), ct)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (serverCt.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                await WriteSimpleResponseAsync(clientStream, 502, "Bad Gateway", ct).ConfigureAwait(false);
                return;
            }

            if (request.IsConnect)
            {
                await WriteSimpleResponseAsync(clientStream, 200, "Connection Established", ct)
                    .ConfigureAwait(false);

                var left = clientStream;
                var right = remoteStream;
                clientStream = null;
                remoteStream = null;
                await DuplexRelay.RunAsync(left, right, _idleTimeout, serverCt).ConfigureAwait(false);
                return;
            }

            // Absolute-form (or origin-form): dial already done; write origin-form request then relay.
            var forward = HttpProxyRequestParser.BuildOriginFormRequest(request);
            await remoteStream.WriteAsync(forward, ct).ConfigureAwait(false);
            await remoteStream.FlushAsync(ct).ConfigureAwait(false);

            {
                var left = clientStream;
                var right = remoteStream;
                clientStream = null;
                remoteStream = null;
                await DuplexRelay.RunAsync(left, right, _idleTimeout, serverCt).ConfigureAwait(false);
            }
        }
        catch
        {
            // client gone / cancelled
        }
        finally
        {
            if (clientStream is not null)
            {
                try { await clientStream.DisposeAsync().ConfigureAwait(false); } catch { /* ignore */ }
            }
            if (remoteStream is not null)
            {
                try { await remoteStream.DisposeAsync().ConfigureAwait(false); } catch { /* ignore */ }
            }
        }
    }

    private static async Task WriteSimpleResponseAsync(
        Stream stream, int status, string reason, CancellationToken ct)
    {
        var bytes = Encoding.ASCII.GetBytes($"HTTP/1.1 {status} {reason}\r\nConnection: close\r\n\r\n");
        try
        {
            await stream.WriteAsync(bytes, ct).ConfigureAwait(false);
            await stream.FlushAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            // ignore write failures on dying clients
        }
    }

    private static bool IsLoopback(IPAddress address) =>
        IPAddress.IsLoopback(address) ||
        address.Equals(IPAddress.IPv6Loopback) ||
        address.Equals(IPAddress.Loopback);
}
