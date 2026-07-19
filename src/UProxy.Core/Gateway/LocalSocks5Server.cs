using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using UProxy.Core.Models;

namespace UProxy.Core.Gateway;

/// <summary>
/// Loopback-only SOCKS5 gateway: no-auth, CONNECT only.
/// Domain names are passed through to <see cref="IChainConnector"/> without local resolution when possible.
/// </summary>
public sealed class LocalSocks5Server : IAsyncDisposable
{
    public const int DefaultPort = 8878;
    private static readonly TimeSpan StopHandlerTimeout = TimeSpan.FromSeconds(5);

    private readonly IChainConnector _connector;
    private readonly IPAddress _bindAddress;
    private readonly int _requestedPort;
    private readonly TimeSpan _idleTimeout;
    private readonly ConcurrentDictionary<Guid, Task> _activeHandlers = new();

    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _acceptLoop;
    private int _started;

    public LocalSocks5Server(
        IChainConnector connector,
        IPAddress? bindAddress = null,
        int port = DefaultPort,
        TimeSpan? idleTimeout = null)
    {
        _connector = connector ?? throw new ArgumentNullException(nameof(connector));
        _bindAddress = bindAddress ?? IPAddress.Loopback;
        if (!IsLoopback(_bindAddress))
            throw new ArgumentException(
                "Local SOCKS5 gateway may only bind to a loopback address.", nameof(bindAddress));
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
        // Do not claim the single-start guard when the caller has already cancelled.
        // Otherwise every later start attempt would incorrectly report that the
        // gateway is already running even though no listener was created.
        cancellationToken.ThrowIfCancellationRequested();

        if (Interlocked.CompareExchange(ref _started, 1, 0) != 0)
            throw new InvalidOperationException("SOCKS5 gateway is already running.");

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

        var handlers = _activeHandlers.Values.ToArray();
        if (handlers.Length > 0)
        {
            try
            {
                await Task.WhenAll(handlers).WaitAsync(StopHandlerTimeout).ConfigureAwait(false);
            }
            catch
            {
                // Timeout or handler fault — proceed with cleanup.
            }
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
                var id = Guid.NewGuid();
                var task = Task.Run(() => HandleClientAsync(captured, ct), CancellationToken.None);
                _activeHandlers[id] = task;
                _ = task.ContinueWith(
                    _ => _activeHandlers.TryRemove(id, out var _),
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
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

            // Greeting: VER NMETHODS METHODS
            var head = new byte[2];
            await ReadExactAsync(clientStream, head, ct).ConfigureAwait(false);
            if (head[0] != 0x05)
                return;
            var nMethods = head[1];
            if (nMethods == 0)
                return;
            var methods = new byte[nMethods];
            await ReadExactAsync(clientStream, methods, ct).ConfigureAwait(false);

            // No-auth only
            if (!methods.Contains((byte)0x00))
            {
                await clientStream.WriteAsync(new byte[] { 0x05, 0xFF }, ct).ConfigureAwait(false);
                return;
            }
            await clientStream.WriteAsync(new byte[] { 0x05, 0x00 }, ct).ConfigureAwait(false);

            var reqHead = new byte[4];
            await ReadExactAsync(clientStream, reqHead, ct).ConfigureAwait(false);
            if (reqHead[0] != 0x05)
                return;

            var cmd = reqHead[1];
            var atyp = reqHead[3];

            string host;
            try
            {
                host = await ReadAddressAsync(clientStream, atyp, ct).ConfigureAwait(false);
            }
            catch (Socks5ProtocolException)
            {
                await WriteReplyAsync(clientStream, 0x08, ct).ConfigureAwait(false); // Address type not supported
                return;
            }

            var portBuf = new byte[2];
            await ReadExactAsync(clientStream, portBuf, ct).ConfigureAwait(false);
            var port = BinaryPrimitives.ReadUInt16BigEndian(portBuf);

            if (cmd == 0x02 || cmd == 0x03)
            {
                // BIND / UDP ASSOCIATE — Command not supported
                await WriteReplyAsync(clientStream, 0x07, ct).ConfigureAwait(false);
                return;
            }

            if (cmd != 0x01)
            {
                await WriteReplyAsync(clientStream, 0x07, ct).ConfigureAwait(false);
                return;
            }

            if (port == 0)
            {
                await WriteReplyAsync(clientStream, 0x01, ct).ConfigureAwait(false);
                return;
            }

            try
            {
                remoteStream = await _connector
                    .ConnectAsync(new ChainDestination(host, port), ct)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (serverCt.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                await WriteReplyAsync(clientStream, 0x05, ct).ConfigureAwait(false); // Connection refused
                return;
            }

            await WriteReplyAsync(clientStream, 0x00, ct).ConfigureAwait(false);

            // Relay owns disposal of both streams.
            var left = clientStream;
            var right = remoteStream;
            clientStream = null;
            remoteStream = null;
            await DuplexRelay.RunAsync(left, right, _idleTimeout, serverCt).ConfigureAwait(false);
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

    private static async Task<string> ReadAddressAsync(Stream stream, byte atyp, CancellationToken ct)
    {
        switch (atyp)
        {
            case 0x01:
            {
                var ip = new byte[4];
                await ReadExactAsync(stream, ip, ct).ConfigureAwait(false);
                return new IPAddress(ip).ToString();
            }
            case 0x03:
            {
                var lenBuf = new byte[1];
                await ReadExactAsync(stream, lenBuf, ct).ConfigureAwait(false);
                var len = lenBuf[0];
                if (len == 0)
                    throw new Socks5ProtocolException("Empty domain.");
                var name = new byte[len];
                await ReadExactAsync(stream, name, ct).ConfigureAwait(false);
                // Preserve domain literally — do not resolve locally.
                return Encoding.ASCII.GetString(name);
            }
            case 0x04:
            {
                var ip = new byte[16];
                await ReadExactAsync(stream, ip, ct).ConfigureAwait(false);
                return new IPAddress(ip).ToString();
            }
            default:
                throw new Socks5ProtocolException($"Unsupported ATYP 0x{atyp:X2}");
        }
    }

    private static async Task WriteReplyAsync(Stream stream, byte reply, CancellationToken ct)
    {
        // VER REP RSV ATYP BND.ADDR BND.PORT — bind IPv4 0.0.0.0:0
        var buf = new byte[] { 0x05, reply, 0x00, 0x01, 0, 0, 0, 0, 0, 0 };
        await stream.WriteAsync(buf, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    private static async Task ReadExactAsync(Stream stream, byte[] buffer, CancellationToken ct)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(offset), ct).ConfigureAwait(false);
            if (n == 0)
                throw new EndOfStreamException();
            offset += n;
        }
    }

    private static bool IsLoopback(IPAddress address) =>
        IPAddress.IsLoopback(address) ||
        address.Equals(IPAddress.IPv6Loopback) ||
        address.Equals(IPAddress.Loopback);

    private sealed class Socks5ProtocolException : Exception
    {
        public Socks5ProtocolException(string message) : base(message) { }
    }
}
