using System.Net;
using System.Net.Sockets;
using System.Text;
using UProxy.Core.Chaining;
using UProxy.Core.Dns;
using UProxy.Core.Models;

namespace UProxy.Core.Checking;

public sealed class SocksConnectOptions
{
    /// <summary>Proxifier "Use SOCKS 4A extension (remote hostname resolving feature)".</summary>
    public bool UseSocks4a { get; init; } = true;

    /// <summary>Send hostnames to the proxy instead of resolving locally (privacy + Fake-IP path).</summary>
    public bool ResolveHostnamesThroughProxy { get; init; } = true;

    public FakeIpDns? FakeIpDns { get; init; }
}

/// <summary>SOCKS4 / SOCKS4a / SOCKS5 client with embedded auth and Fake-IP aware remote DNS.</summary>
public static class SocksClient
{
    public static async Task<(bool Ok, FailureReason Failure, string? Error, int LatencyMs, int ConnectMs, ProxyAuthMethod Auth)> ConnectAndHttpGetAsync(
        ParsedProxy proxy,
        ProxyProtocol socksVersion,
        string destinationHost,
        int destinationPort,
        string httpPath,
        int timeoutMs,
        CancellationToken ct,
        SocksConnectOptions? options = null)
    {
        options ??= new SocksConnectOptions();
        var authUsed = ProxyAuthMethod.None;
        var connectMs = 0;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeoutMs);

        try
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp)
            {
                NoDelay = true
            };

            if (!IPAddress.TryParse(proxy.Host, out var proxyIp))
            {
                var addresses = await System.Net.Dns.GetHostAddressesAsync(proxy.Host, cts.Token).ConfigureAwait(false);
                proxyIp = addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork)
                          ?? addresses.FirstOrDefault()
                          ?? throw new SocketException((int)SocketError.HostNotFound);
            }

            // Test 1 + Test 3: pure TCP connect to the proxy (reachability + connect latency).
            var connectSw = System.Diagnostics.Stopwatch.StartNew();
            await socket.ConnectAsync(new IPEndPoint(proxyIp, proxy.Port), cts.Token).ConfigureAwait(false);
            connectSw.Stop();
            var ms = (int)Math.Round(connectSw.Elapsed.TotalMilliseconds);
            connectMs = ms > 0 ? ms : (connectSw.ElapsedTicks > 0 ? 1 : 0);

            await using var stream = new NetworkStream(socket, ownsSocket: false);
            var dest = ResolveDestination(destinationHost, options);
            var hs = new HandshakeOptions
            {
                UseSocks4a = options.UseSocks4a,
                ResolveHostnamesThroughProxy = options.ResolveHostnamesThroughProxy
            };

            if (socksVersion == ProxyProtocol.Socks4)
            {
                authUsed = await Socks4Handshake.ConnectAsync(stream, proxy, dest, destinationPort, hs, cts.Token)
                    .ConfigureAwait(false);
            }
            else
            {
                authUsed = await Socks5Handshake.ConnectAsync(stream, proxy, dest, destinationPort, hs, cts.Token)
                    .ConfigureAwait(false);
            }

            // Host header must be the logical hostname (not a Fake-IP).
            var hostHeader = options.FakeIpDns?.ResolveDestinationForProxy(destinationHost, resolveHostnamesThroughProxy: true)
                             ?? destinationHost;
            var request = Encoding.ASCII.GetBytes(
                $"GET {httpPath} HTTP/1.1\r\nHost: {hostHeader}\r\nConnection: close\r\nUser-Agent: uProxy-Tool/2.0\r\n\r\n");
            await stream.WriteAsync(request, cts.Token).ConfigureAwait(false);
            await stream.FlushAsync(cts.Token).ConfigureAwait(false);

            var buffer = new byte[512];
            var read = await stream.ReadAsync(buffer, cts.Token).ConfigureAwait(false);
            sw.Stop();

            if (read < 12)
                return (false, FailureReason.EmptyResponse, "No HTTP response through SOCKS", (int)sw.ElapsedMilliseconds, connectMs, authUsed);

            var text = Encoding.ASCII.GetString(buffer, 0, read);
            if (!text.StartsWith("HTTP/", StringComparison.Ordinal))
                return (false, FailureReason.JudgeMismatch, "Non-HTTP response through SOCKS", (int)sw.ElapsedMilliseconds, connectMs, authUsed);

            return (true, FailureReason.None, null, (int)sw.ElapsedMilliseconds, connectMs, authUsed);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return (false, FailureReason.Cancelled, "Cancelled", (int)sw.ElapsedMilliseconds, connectMs, authUsed);
        }
        catch (OperationCanceledException)
        {
            var reason = connectMs == 0 ? FailureReason.ConnectTimeout : FailureReason.Timeout;
            return (false, reason, "Timed out", (int)sw.ElapsedMilliseconds, connectMs, authUsed);
        }
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionRefused)
        {
            return (false, FailureReason.ConnectRefused, ex.Message, (int)sw.ElapsedMilliseconds, connectMs, authUsed);
        }
        catch (SocketException ex) when (ex.SocketErrorCode is SocketError.TimedOut or SocketError.TryAgain)
        {
            return (false, FailureReason.ConnectTimeout, ex.Message, (int)sw.ElapsedMilliseconds, connectMs, authUsed);
        }
        catch (ProxyHandshakeException ex)
        {
            return (false, ex.Reason, ex.Message, (int)sw.ElapsedMilliseconds, connectMs, authUsed);
        }
        catch (Exception ex)
        {
            return (false, FailureReason.UnknownError, ex.Message, (int)sw.ElapsedMilliseconds, connectMs, authUsed);
        }
    }

    private static string ResolveDestination(string destinationHost, SocksConnectOptions options)
    {
        if (options.FakeIpDns is not null)
            return options.FakeIpDns.ResolveDestinationForProxy(destinationHost, options.ResolveHostnamesThroughProxy);

        if (FakeIpDns.IsFakeIp(destinationHost))
            throw new ProxyHandshakeException(FailureReason.DnsFailure,
                "Destination is a Fake-IP with no hostname mapping. Enable Fake-IP DNS.");

        return destinationHost.Trim().TrimStart('[').TrimEnd(']');
    }
}
