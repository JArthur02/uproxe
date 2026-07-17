using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using UProxy.Core.Models;

namespace UProxy.Core.Gateway;

/// <summary>
/// Loopback-only HTTP proxy gateway: CONNECT tunnels and absolute-form requests
/// (dial host:port then forward origin-form). No TLS interception.
/// Non-CONNECT requests are single-shot (Connection: close) — no keep-alive multiplexing.
/// </summary>
public sealed class LocalHttpProxyServer : IAsyncDisposable
{
    public const int DefaultPort = 8877;
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
        // Do not claim the single-start guard when the caller has already cancelled.
        // Otherwise every later start attempt would incorrectly report that the
        // gateway is already running even though no listener was created.
        cancellationToken.ThrowIfCancellationRequested();

        if (Interlocked.CompareExchange(ref _started, 1, 0) != 0)
            throw new InvalidOperationException("HTTP proxy gateway is already running.");

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

        _activeHandlers.Clear();
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
                    _ => _activeHandlers.TryRemove(id, out Task? _),
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
            using var linked =
                CancellationTokenSource.CreateLinkedTokenSource(serverCt);

            void ResetIdleTimeout() => linked.CancelAfter(_idleTimeout);

            ResetIdleTimeout();
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

            // Single-request forward: origin-form headers + body, then response until EOF, then close.
            // Do not duplex-relay — that would allow keep-alive / pipelined requests to the wrong host.
            RequestBodyLengthKind bodyKind;
            long contentLength;
            try
            {
                bodyKind = HttpProxyRequestParser.GetRequestBodyLengthPolicy(request, out contentLength);
            }
            catch (HttpProxyParseException)
            {
                await WriteSimpleResponseAsync(clientStream, 400, "Bad Request", ct).ConfigureAwait(false);
                return;
            }

            var forward = HttpProxyRequestParser.BuildOriginFormRequest(request);
            await remoteStream.WriteAsync(forward, ct).ConfigureAwait(false);
            await remoteStream.FlushAsync(ct).ConfigureAwait(false);

            // Break Expect: 100-continue deadlock — compliant clients wait for 100 before
            // sending the body; Expect is already stripped from the origin-form request.
            if (HttpProxyRequestParser.HasExpectContinue(request) &&
                bodyKind is RequestBodyLengthKind.ContentLength or RequestBodyLengthKind.Chunked)
            {
                await WriteContinue100Async(clientStream, ct).ConfigureAwait(false);
            }

            switch (bodyKind)
            {
                case RequestBodyLengthKind.ContentLength:
                    await CopyExactAsync(
                            clientStream,
                            remoteStream,
                            contentLength,
                            ct,
                            ResetIdleTimeout)
                        .ConfigureAwait(false);
                    break;

                case RequestBodyLengthKind.Chunked:
                    await CopyChunkedRequestBodyAsync(
                            clientStream,
                            remoteStream,
                            ct,
                            ResetIdleTimeout)
                        .ConfigureAwait(false);
                    break;

                case RequestBodyLengthKind.NoBody:
                    break;
            }

            await remoteStream.FlushAsync(ct).ConfigureAwait(false);
            ResetIdleTimeout();

            await CopyUntilEofAsync(
                    remoteStream,
                    clientStream,
                    ct,
                    ResetIdleTimeout)
                .ConfigureAwait(false);
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

    private static async Task CopyExactAsync(
        Stream source,
        Stream destination,
        long count,
        CancellationToken ct,
        Action onActivity)
    {
        var bufferLength = (int)Math.Min(81920L, Math.Max(1L, count));
        var buffer = new byte[bufferLength];
        var remaining = count;

        while (remaining > 0)
        {
            var toRead = (int)Math.Min(buffer.Length, remaining);
            var n = await source
                .ReadAsync(buffer.AsMemory(0, toRead), ct)
                .ConfigureAwait(false);

            if (n == 0)
            {
                throw new EndOfStreamException(
                    "Unexpected EOF while copying Content-Length body.");
            }

            onActivity();

            await destination
                .WriteAsync(buffer.AsMemory(0, n), ct)
                .ConfigureAwait(false);

            remaining -= n;
        }
    }

    /// <summary>
    /// Copies chunked transfer-coding framing (including the terminating chunk and optional trailers)
    /// from client to origin without decoding.
    /// </summary>
    private static async Task CopyChunkedRequestBodyAsync(
        Stream source,
        Stream destination,
        CancellationToken ct,
        Action onActivity)
    {
        while (true)
        {
            var sizeLine = await ReadLineCrlfAsync(source, ct)
                .ConfigureAwait(false);

            onActivity();

            await WriteAsciiLineAsync(destination, sizeLine, ct)
                .ConfigureAwait(false);

            var sizeToken = sizeLine;
            var semi = sizeToken.IndexOf(';');
            if (semi >= 0)
                sizeToken = sizeToken[..semi];

            sizeToken = sizeToken.Trim();

            if (!long.TryParse(
                    sizeToken,
                    System.Globalization.NumberStyles.HexNumber,
                    null,
                    out var chunkSize) ||
                chunkSize < 0)
            {
                throw new HttpProxyParseException("Invalid chunk size.");
            }

            if (chunkSize > 0)
            {
                await CopyExactAsync(
                        source,
                        destination,
                        chunkSize,
                        ct,
                        onActivity)
                    .ConfigureAwait(false);

                var afterData = await ReadLineCrlfAsync(source, ct)
                    .ConfigureAwait(false);

                onActivity();

                if (afterData.Length != 0)
                    throw new HttpProxyParseException("Invalid chunk framing.");

                await WriteAsciiLineAsync(destination, afterData, ct)
                    .ConfigureAwait(false);
            }
            else
            {
                while (true)
                {
                    var trailer = await ReadLineCrlfAsync(source, ct)
                        .ConfigureAwait(false);

                    onActivity();

                    await WriteAsciiLineAsync(destination, trailer, ct)
                        .ConfigureAwait(false);

                    if (trailer.Length == 0)
                        return;
                }
            }
        }
    }

    private static async Task CopyUntilEofAsync(
        Stream source,
        Stream destination,
        CancellationToken ct,
        Action onActivity)
    {
        var buffer = new byte[81920];

        while (true)
        {
            var n = await source
                .ReadAsync(buffer.AsMemory(), ct)
                .ConfigureAwait(false);

            if (n == 0)
                return;

            onActivity();

            await destination
                .WriteAsync(buffer.AsMemory(0, n), ct)
                .ConfigureAwait(false);

            await destination.FlushAsync(ct).ConfigureAwait(false);
        }
    }

    private static async Task<string> ReadLineCrlfAsync(Stream stream, CancellationToken ct)
    {
        using var ms = new MemoryStream(64);
        var prevWasCr = false;
        var buf = new byte[1];
        while (ms.Length < 8192)
        {
            var n = await stream.ReadAsync(buf.AsMemory(0, 1), ct).ConfigureAwait(false);
            if (n == 0)
                throw new EndOfStreamException("Unexpected EOF reading chunk line.");
            var b = buf[0];
            if (prevWasCr)
            {
                if (b != (byte)'\n')
                    throw new HttpProxyParseException("HTTP requires CRLF line endings.");
                // Exclude the CR already written; LF not included.
                var arr = ms.ToArray();
                return Encoding.ASCII.GetString(arr, 0, arr.Length - 1);
            }
            if (b == (byte)'\n')
                throw new HttpProxyParseException("HTTP requires CRLF line endings.");
            ms.WriteByte(b);
            prevWasCr = b == (byte)'\r';
        }
        throw new HttpProxyParseException("Chunk line too long.");
    }

    private static async Task WriteAsciiLineAsync(Stream stream, string lineWithoutCrlf, CancellationToken ct)
    {
        var bytes = Encoding.ASCII.GetBytes(lineWithoutCrlf + "\r\n");
        await stream.WriteAsync(bytes, ct).ConfigureAwait(false);
    }

    private static async Task WriteContinue100Async(Stream stream, CancellationToken ct)
    {
        // Interim response must not advertise Connection: close.
        var bytes = Encoding.ASCII.GetBytes("HTTP/1.1 100 Continue\r\n\r\n");
        await stream.WriteAsync(bytes, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
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
