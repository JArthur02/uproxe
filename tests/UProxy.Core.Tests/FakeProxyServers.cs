using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace UProxy.Core.Tests;

/// <summary>Deterministic loopback fake proxies for chain/handshake tests.</summary>
internal static class FakeProxyServers
{
    internal sealed class EchoHttpServer : IAsyncDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _loop;

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        public EchoHttpServer(string body = "ok")
        {
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            _loop = Task.Run(async () =>
            {
                while (!_cts.IsCancellationRequested)
                {
                    TcpClient? client = null;
                    try
                    {
                        client = await _listener.AcceptTcpClientAsync(_cts.Token).ConfigureAwait(false);
                        _ = Task.Run(() => ServeAsync(client, body));
                    }
                    catch when (_cts.IsCancellationRequested)
                    {
                        client?.Dispose();
                        break;
                    }
                }
            });
        }

        private static async Task ServeAsync(TcpClient client, string body)
        {
            using var clientLifetime = client;
            await using var stream = client.GetStream();
            var buf = new byte[4096];
            _ = await stream.ReadAsync(buf).ConfigureAwait(false);
            var payload = Encoding.ASCII.GetBytes(body);
            var resp = Encoding.ASCII.GetBytes(
                $"HTTP/1.1 200 OK\r\nContent-Length: {payload.Length}\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(resp).ConfigureAwait(false);
            await stream.WriteAsync(payload).ConfigureAwait(false);
            await stream.FlushAsync().ConfigureAwait(false);
        }

        public async ValueTask DisposeAsync()
        {
            _cts.Cancel();
            try { _listener.Stop(); } catch { /* ignore */ }
            try { await _loop.ConfigureAwait(false); } catch { /* ignore */ }
            _cts.Dispose();
        }
    }

    /// <summary>
    /// Origin that reads one HTTP request (Content-Length or chunked), echoes the body,
    /// then closes. Optionally keeps the connection open after the response for keep-alive tests.
    /// </summary>
    internal sealed class BodyEchoHttpServer : IAsyncDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _loop;
        private readonly bool _keepAlive;
        private readonly List<string> _receivedBodies = new();
        private readonly List<string> _receivedRequestLines = new();
        private readonly List<string> _receivedHeaderBlocks = new();
        private int _requestCount;

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
        public int RequestCount => Volatile.Read(ref _requestCount);
        public IReadOnlyList<string> ReceivedBodies
        {
            get { lock (_receivedBodies) return _receivedBodies.ToArray(); }
        }
        public IReadOnlyList<string> ReceivedRequestLines
        {
            get { lock (_receivedRequestLines) return _receivedRequestLines.ToArray(); }
        }
        public IReadOnlyList<string> ReceivedHeaderBlocks
        {
            get { lock (_receivedHeaderBlocks) return _receivedHeaderBlocks.ToArray(); }
        }

        public BodyEchoHttpServer(bool keepAlive = false)
        {
            _keepAlive = keepAlive;
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            _loop = Task.Run(AcceptLoopAsync);
        }

        private async Task AcceptLoopAsync()
        {
            while (!_cts.IsCancellationRequested)
            {
                TcpClient? client = null;
                try
                {
                    client = await _listener.AcceptTcpClientAsync(_cts.Token).ConfigureAwait(false);
                    _ = Task.Run(() => ServeAsync(client));
                }
                catch when (_cts.IsCancellationRequested)
                {
                    client?.Dispose();
                    break;
                }
            }
        }

        private async Task ServeAsync(TcpClient client)
        {
            using var clientLifetime = client;
            await using var stream = client.GetStream();
            try
            {
                do
                {
                    var headerText = await ReadHeadersAsync(stream).ConfigureAwait(false);
                    var firstLine = headerText.Split("\r\n", 2)[0];
                    lock (_receivedRequestLines) _receivedRequestLines.Add(firstLine);
                    lock (_receivedHeaderBlocks) _receivedHeaderBlocks.Add(headerText);
                    Interlocked.Increment(ref _requestCount);

                    var body = await ReadRequestBodyAsync(stream, headerText).ConfigureAwait(false);
                    lock (_receivedBodies) _receivedBodies.Add(body);

                    // Honor Connection: close from the proxy (always injected on forward).
                    var requestWantsClose = headerText.Split("\r\n")
                        .Any(l => l.StartsWith("Connection:", StringComparison.OrdinalIgnoreCase) &&
                                  l.Contains("close", StringComparison.OrdinalIgnoreCase));
                    var closeAfter = !_keepAlive || requestWantsClose;

                    var payload = Encoding.ASCII.GetBytes(body);
                    var conn = closeAfter ? "close" : "keep-alive";
                    var resp = Encoding.ASCII.GetBytes(
                        $"HTTP/1.1 200 OK\r\nContent-Length: {payload.Length}\r\nConnection: {conn}\r\n\r\n");
                    await stream.WriteAsync(resp).ConfigureAwait(false);
                    await stream.WriteAsync(payload).ConfigureAwait(false);
                    await stream.FlushAsync().ConfigureAwait(false);

                    if (closeAfter)
                        return;
                } while (!_cts.IsCancellationRequested);
            }
            catch
            {
                // client gone
            }
        }

        private static async Task<string> ReadRequestBodyAsync(Stream stream, string headerText)
        {
            string? transferEncoding = null;
            string? contentLengthHeader = null;
            foreach (var line in headerText.Split("\r\n").Skip(1))
            {
                if (line.Length == 0)
                    break;
                var colon = line.IndexOf(':');
                if (colon <= 0)
                    continue;
                var name = line[..colon].Trim();
                var value = line[(colon + 1)..].Trim();
                if (name.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase))
                    transferEncoding = value;
                else if (name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                    contentLengthHeader = value;
            }

            if (transferEncoding is not null &&
                transferEncoding.Contains("chunked", StringComparison.OrdinalIgnoreCase))
            {
                using var ms = new MemoryStream();
                while (true)
                {
                    var sizeLine = await ReadLineCrlfAsync(stream).ConfigureAwait(false);
                    var semi = sizeLine.IndexOf(';');
                    var sizeToken = (semi >= 0 ? sizeLine[..semi] : sizeLine).Trim();
                    var chunkSize = long.Parse(sizeToken, System.Globalization.NumberStyles.HexNumber);
                    if (chunkSize == 0)
                    {
                        // Drain trailers
                        while (true)
                        {
                            var trailer = await ReadLineCrlfAsync(stream).ConfigureAwait(false);
                            if (trailer.Length == 0)
                                break;
                        }
                        break;
                    }
                    var chunk = new byte[chunkSize];
                    await ReadExact(stream, chunk).ConfigureAwait(false);
                    ms.Write(chunk);
                    var crlf = new byte[2];
                    await ReadExact(stream, crlf).ConfigureAwait(false);
                }
                return Encoding.ASCII.GetString(ms.ToArray());
            }

            if (contentLengthHeader is not null)
            {
                var len = int.Parse(contentLengthHeader);
                if (len == 0)
                    return "";
                var buf = new byte[len];
                await ReadExact(stream, buf).ConfigureAwait(false);
                return Encoding.ASCII.GetString(buf);
            }

            return "";
        }

        private static async Task<string> ReadLineCrlfAsync(Stream stream)
        {
            using var ms = new MemoryStream();
            var prevCr = false;
            var b = new byte[1];
            while (true)
            {
                await ReadExact(stream, b).ConfigureAwait(false);
                if (prevCr && b[0] == (byte)'\n')
                {
                    var arr = ms.ToArray();
                    return Encoding.ASCII.GetString(arr, 0, arr.Length - 1);
                }
                ms.WriteByte(b[0]);
                prevCr = b[0] == (byte)'\r';
            }
        }

        public async ValueTask DisposeAsync()
        {
            _cts.Cancel();
            try { _listener.Stop(); } catch { /* ignore */ }
            try { await _loop.ConfigureAwait(false); } catch { /* ignore */ }
            _cts.Dispose();
        }
    }

    /// <summary>Minimal SOCKS5 CONNECT relay (optional user/pass, optional forced reply code).</summary>
    internal sealed class FakeSocks5Proxy : IAsyncDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _loop;
        private readonly string? _requiredUser;
        private readonly string? _requiredPass;
        private readonly byte? _forceConnectReply;

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        public FakeSocks5Proxy(string? user = null, string? pass = null, byte? forceConnectReply = null)
        {
            _requiredUser = user;
            _requiredPass = pass;
            _forceConnectReply = forceConnectReply;
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            _loop = Task.Run(AcceptLoopAsync);
        }

        private async Task AcceptLoopAsync()
        {
            while (!_cts.IsCancellationRequested)
            {
                TcpClient? client = null;
                try
                {
                    client = await _listener.AcceptTcpClientAsync(_cts.Token).ConfigureAwait(false);
                    _ = Task.Run(() => HandleClientAsync(client));
                }
                catch when (_cts.IsCancellationRequested)
                {
                    client?.Dispose();
                    break;
                }
            }
        }

        private async Task HandleClientAsync(TcpClient client)
        {
            using var clientLifetime = client;
            await using var stream = client.GetStream();
            try
            {
                var head = new byte[2];
                await ReadExact(stream, head).ConfigureAwait(false);
                if (head[0] != 0x05)
                    return;
                var methods = new byte[head[1]];
                await ReadExact(stream, methods).ConfigureAwait(false);

                if (_requiredUser is not null)
                {
                    if (!methods.Contains((byte)0x02))
                    {
                        await stream.WriteAsync(new byte[] { 0x05, 0xFF }).ConfigureAwait(false);
                        return;
                    }
                    await stream.WriteAsync(new byte[] { 0x05, 0x02 }).ConfigureAwait(false);

                    var ver = new byte[1];
                    await ReadExact(stream, ver).ConfigureAwait(false);
                    var ulen = new byte[1];
                    await ReadExact(stream, ulen).ConfigureAwait(false);
                    var user = new byte[ulen[0]];
                    await ReadExact(stream, user).ConfigureAwait(false);
                    var plen = new byte[1];
                    await ReadExact(stream, plen).ConfigureAwait(false);
                    var pass = new byte[plen[0]];
                    await ReadExact(stream, pass).ConfigureAwait(false);
                    var ok = Encoding.UTF8.GetString(user) == _requiredUser &&
                             Encoding.UTF8.GetString(pass) == (_requiredPass ?? "");
                    await stream.WriteAsync(new byte[] { 0x01, ok ? (byte)0x00 : (byte)0x01 }).ConfigureAwait(false);
                    if (!ok)
                        return;
                }
                else
                {
                    await stream.WriteAsync(new byte[] { 0x05, 0x00 }).ConfigureAwait(false);
                }

                var reqHead = new byte[4];
                await ReadExact(stream, reqHead).ConfigureAwait(false);
                if (reqHead[0] != 0x05 || reqHead[1] != 0x01)
                    return;

                string host;
                if (reqHead[3] == 0x01)
                {
                    var ip = new byte[4];
                    await ReadExact(stream, ip).ConfigureAwait(false);
                    host = new IPAddress(ip).ToString();
                }
                else if (reqHead[3] == 0x03)
                {
                    var len = new byte[1];
                    await ReadExact(stream, len).ConfigureAwait(false);
                    var name = new byte[len[0]];
                    await ReadExact(stream, name).ConfigureAwait(false);
                    host = Encoding.ASCII.GetString(name);
                }
                else if (reqHead[3] == 0x04)
                {
                    var ip = new byte[16];
                    await ReadExact(stream, ip).ConfigureAwait(false);
                    host = new IPAddress(ip).ToString();
                }
                else
                    return;

                var portBuf = new byte[2];
                await ReadExact(stream, portBuf).ConfigureAwait(false);
                var port = BinaryPrimitives.ReadUInt16BigEndian(portBuf);

                if (_forceConnectReply is byte code)
                {
                    await stream.WriteAsync(new byte[] { 0x05, code, 0x00, 0x01, 0, 0, 0, 0, 0, 0 })
                        .ConfigureAwait(false);
                    return;
                }

                using var remote = new TcpClient();
                await remote.ConnectAsync(host, port).ConfigureAwait(false);
                await stream.WriteAsync(new byte[] { 0x05, 0x00, 0x00, 0x01, 127, 0, 0, 1, 0, 0 })
                    .ConfigureAwait(false);

                await using var remoteStream = remote.GetStream();
                await RelayAsync(stream, remoteStream).ConfigureAwait(false);
            }
            catch
            {
                // client gone
            }
        }

        public async ValueTask DisposeAsync()
        {
            _cts.Cancel();
            try { _listener.Stop(); } catch { /* ignore */ }
            try { await _loop.ConfigureAwait(false); } catch { /* ignore */ }
            _cts.Dispose();
        }
    }

    /// <summary>Minimal HTTP CONNECT proxy that relays after 200 (or returns a fixed status).</summary>
    internal sealed class FakeHttpConnectProxy : IAsyncDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _loop;
        private readonly int? _fixedStatus;

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        public FakeHttpConnectProxy(int? fixedStatus = null)
        {
            _fixedStatus = fixedStatus;
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            _loop = Task.Run(AcceptLoopAsync);
        }

        private async Task AcceptLoopAsync()
        {
            while (!_cts.IsCancellationRequested)
            {
                TcpClient? client = null;
                try
                {
                    client = await _listener.AcceptTcpClientAsync(_cts.Token).ConfigureAwait(false);
                    _ = Task.Run(() => HandleClientAsync(client));
                }
                catch when (_cts.IsCancellationRequested)
                {
                    client?.Dispose();
                    break;
                }
            }
        }

        private async Task HandleClientAsync(TcpClient client)
        {
            using var clientLifetime = client;
            await using var stream = client.GetStream();
            try
            {
                var header = await ReadHeadersAsync(stream).ConfigureAwait(false);
                var first = header.Split("\r\n", 2)[0];
                if (_fixedStatus is int code)
                {
                    var msg = code switch
                    {
                        200 => "Connection established",
                        407 => "Proxy Authentication Required",
                        403 => "Forbidden",
                        _ => "Error"
                    };
                    var bytes = Encoding.ASCII.GetBytes($"HTTP/1.1 {code} {msg}\r\n\r\n");
                    await stream.WriteAsync(bytes).ConfigureAwait(false);
                    return;
                }

                var parts = first.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 2 || !parts[0].Equals("CONNECT", StringComparison.OrdinalIgnoreCase))
                    return;
                var authority = parts[1];
                var colon = authority.LastIndexOf(':');
                var host = authority[..colon];
                var port = int.Parse(authority[(colon + 1)..]);

                using var remote = new TcpClient();
                await remote.ConnectAsync(host, port).ConfigureAwait(false);
                var ok = Encoding.ASCII.GetBytes("HTTP/1.1 200 Connection established\r\n\r\n");
                await stream.WriteAsync(ok).ConfigureAwait(false);
                await using var remoteStream = remote.GetStream();
                await RelayAsync(stream, remoteStream).ConfigureAwait(false);
            }
            catch
            {
                // ignore
            }
        }

        public async ValueTask DisposeAsync()
        {
            _cts.Cancel();
            try { _listener.Stop(); } catch { /* ignore */ }
            try { await _loop.ConfigureAwait(false); } catch { /* ignore */ }
            _cts.Dispose();
        }
    }

    /// <summary>Minimal SOCKS4a CONNECT relay.</summary>
    internal sealed class FakeSocks4Proxy : IAsyncDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _loop;
        private readonly byte? _forceReply;

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        public FakeSocks4Proxy(byte? forceReply = null)
        {
            _forceReply = forceReply;
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            _loop = Task.Run(AcceptLoopAsync);
        }

        private async Task AcceptLoopAsync()
        {
            while (!_cts.IsCancellationRequested)
            {
                TcpClient? client = null;
                try
                {
                    client = await _listener.AcceptTcpClientAsync(_cts.Token).ConfigureAwait(false);
                    _ = Task.Run(() => HandleClientAsync(client));
                }
                catch when (_cts.IsCancellationRequested)
                {
                    client?.Dispose();
                    break;
                }
            }
        }

        private async Task HandleClientAsync(TcpClient client)
        {
            using var clientLifetime = client;
            await using var stream = client.GetStream();
            try
            {
                var head = new byte[8];
                await ReadExact(stream, head).ConfigureAwait(false);
                if (head[0] != 0x04 || head[1] != 0x01)
                    return;

                var port = BinaryPrimitives.ReadUInt16BigEndian(head.AsSpan(2, 2));
                var ip = new IPAddress(head.AsSpan(4, 4));
                _ = await ReadCString(stream).ConfigureAwait(false);

                string host = head[4] == 0 && head[5] == 0 && head[6] == 0 && head[7] != 0
                    ? await ReadCString(stream).ConfigureAwait(false)
                    : ip.ToString();

                if (_forceReply is byte code)
                {
                    await stream.WriteAsync(new byte[] { 0x00, code, 0, 0, 0, 0, 0, 0 }).ConfigureAwait(false);
                    return;
                }

                using var remote = new TcpClient();
                await remote.ConnectAsync(host, port).ConfigureAwait(false);
                await stream.WriteAsync(new byte[] { 0x00, 0x5A, 0, 0, 0, 0, 0, 0 }).ConfigureAwait(false);
                await using var remoteStream = remote.GetStream();
                await RelayAsync(stream, remoteStream).ConfigureAwait(false);
            }
            catch
            {
                // ignore
            }
        }

        public async ValueTask DisposeAsync()
        {
            _cts.Cancel();
            try { _listener.Stop(); } catch { /* ignore */ }
            try { await _loop.ConfigureAwait(false); } catch { /* ignore */ }
            _cts.Dispose();
        }
    }

    private static async Task ReadExact(Stream stream, byte[] buffer)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(offset)).ConfigureAwait(false);
            if (n == 0)
                throw new EndOfStreamException();
            offset += n;
        }
    }

    private static async Task<string> ReadCString(Stream stream)
    {
        using var ms = new MemoryStream();
        while (true)
        {
            var b = new byte[1];
            await ReadExact(stream, b).ConfigureAwait(false);
            if (b[0] == 0)
                break;
            ms.WriteByte(b[0]);
        }
        return Encoding.ASCII.GetString(ms.ToArray());
    }

    private static async Task<string> ReadHeadersAsync(Stream stream)
    {
        using var ms = new MemoryStream();
        while (ms.Length < 32 * 1024)
        {
            var b = new byte[1];
            await ReadExact(stream, b).ConfigureAwait(false);
            ms.WriteByte(b[0]);
            if (ms.Length >= 4)
            {
                var arr = ms.ToArray();
                var i = arr.Length - 4;
                if (arr[i] == '\r' && arr[i + 1] == '\n' && arr[i + 2] == '\r' && arr[i + 3] == '\n')
                    break;
            }
        }
        return Encoding.ASCII.GetString(ms.ToArray());
    }

    private static async Task RelayAsync(NetworkStream a, NetworkStream b)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var t1 = a.CopyToAsync(b, cts.Token);
        var t2 = b.CopyToAsync(a, cts.Token);
        try { await Task.WhenAny(t1, t2).ConfigureAwait(false); }
        catch { /* ignore */ }
    }
}
