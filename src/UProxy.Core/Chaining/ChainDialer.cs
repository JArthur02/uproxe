using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using UProxy.Core.Config;
using UProxy.Core.Models;

namespace UProxy.Core.Chaining;

/// <summary>
/// Builds a TCP tunnel through an ordered list of proxy hops, returning the final tunneled stream.
/// Caller owns the returned stream and must dispose it.
/// </summary>
public sealed class ChainDialer
{
    public const int MaxHops = 5;

    private readonly TimeSpan _defaultOverallTimeout;
    private readonly string _userAgent;

    public ChainDialer(TimeSpan? overallTimeout = null, string? userAgent = null)
    {
        _defaultOverallTimeout = overallTimeout ?? TimeSpan.FromSeconds(30);
        _userAgent = UserAgents.AsciiSafe(userAgent ?? UserAgents.Default);
    }

    public async Task<Stream> ConnectAsync(
        IReadOnlyList<ProxyHop> hops,
        ChainDestination destination,
        CancellationToken cancellationToken,
        TimeSpan? overallTimeout = null)
    {
        if (hops is null || hops.Count == 0)
            throw new ArgumentException("At least one hop is required.", nameof(hops));
        if (hops.Count > MaxHops)
            throw new ArgumentException($"Chain length cannot exceed {MaxHops}.", nameof(hops));
        ValidateHopOrder(hops);
        if (string.IsNullOrWhiteSpace(destination.Host))
            throw new ArgumentException("Destination host is required.", nameof(destination));
        if (destination.Port is < 1 or > 65535)
            throw new ArgumentOutOfRangeException(nameof(destination));

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        linked.CancelAfter(overallTimeout ?? _defaultOverallTimeout);
        var ct = linked.Token;

        Socket? socket = null;
        Stream? stream = null;
        var ownsStream = true;

        try
        {
            var first = hops[0];
            socket = await ConnectTcpAsync(first.Proxy.Host, first.Proxy.Port, ct).ConfigureAwait(false);
            stream = new NetworkStream(socket, ownsSocket: true);
            socket = null;

            for (var i = 0; i < hops.Count; i++)
            {
                var hop = hops[i];

                string nextHost;
                int nextPort;
                if (i + 1 < hops.Count)
                {
                    nextHost = hops[i + 1].Proxy.Host;
                    nextPort = hops[i + 1].Proxy.Port;
                }
                else
                {
                    nextHost = destination.Host;
                    nextPort = destination.Port;
                }

                var hsOptions = new HandshakeOptions
                {
                    UseSocks4a = true,
                    ResolveHostnamesThroughProxy = hop.RemoteDns,
                    UserAgent = _userAgent
                };

                try
                {
                    // TLS to this hop must be inside the per-hop catch so failures at hop 2–5
                    // report FailedHopIndex correctly (not -1 / first hop).
                    if (hop.Transport == ProxyTransport.Tls)
                    {
                        stream = await WrapTlsAsync(stream, hop.Proxy.Host, ct).ConfigureAwait(false);
                    }

                    await HandshakeAsync(stream, hop, nextHost, nextPort, hsOptions, ct)
                        .ConfigureAwait(false);
                }
                catch (ChainDialException)
                {
                    throw;
                }
                catch (ProxyHandshakeException ex)
                {
                    throw new ChainDialException(
                        failedHopIndex: i,
                        fromEndpoint: hop.Endpoint,
                        toEndpoint: FormatEndpoint(nextHost, nextPort),
                        reason: ex.Reason,
                        message: $"Hop {i + 1}/{hops.Count} ({hop.Kind} {hop.Endpoint} → {FormatEndpoint(nextHost, nextPort)}): {ex.Message}",
                        inner: ex);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    throw new ChainDialException(
                        failedHopIndex: i,
                        fromEndpoint: hop.Endpoint,
                        toEndpoint: FormatEndpoint(nextHost, nextPort),
                        reason: FailureReason.Timeout,
                        message: $"Hop {i + 1}/{hops.Count} timed out ({hop.Endpoint} → {FormatEndpoint(nextHost, nextPort)}).");
                }
                catch (AuthenticationException ex) when (hop.Transport == ProxyTransport.Tls)
                {
                    throw new ChainDialException(
                        failedHopIndex: i,
                        fromEndpoint: hop.Endpoint,
                        toEndpoint: FormatEndpoint(nextHost, nextPort),
                        reason: FailureReason.ProxyTransportTlsFailure,
                        message:
                            $"Hop {i + 1}/{hops.Count} TLS to proxy {hop.Endpoint} failed: {ex.Message}",
                        inner: ex);
                }
                catch (IOException ex)
                {
                    var reason = hop.Transport == ProxyTransport.Tls
                        ? FailureReason.ProxyTransportTlsFailure
                        : FailureReason.ProxyHandshakeFailure;

                    throw new ChainDialException(
                        failedHopIndex: i,
                        fromEndpoint: hop.Endpoint,
                        toEndpoint: FormatEndpoint(nextHost, nextPort),
                        reason: reason,
                        message:
                            $"Hop {i + 1}/{hops.Count} ({hop.Endpoint}) transport failed: {ex.Message}",
                        inner: ex);
                }
            }

            ownsStream = false;
            return stream;
        }
        catch (ChainDialException)
        {
            throw;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException ex)
        {
            throw new ChainDialException(
                failedHopIndex: -1,
                fromEndpoint: null,
                toEndpoint: hops[0].Endpoint,
                reason: FailureReason.ConnectTimeout,
                message: $"Timed out connecting to first hop {hops[0].Endpoint}.",
                inner: ex);
        }
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionRefused)
        {
            throw new ChainDialException(
                failedHopIndex: -1,
                fromEndpoint: null,
                toEndpoint: hops[0].Endpoint,
                reason: FailureReason.ConnectRefused,
                message: $"Connection refused to first hop {hops[0].Endpoint}.",
                inner: ex);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new ChainDialException(
                failedHopIndex: -1,
                fromEndpoint: null,
                toEndpoint: hops[0].Endpoint,
                reason: FailureReason.UnknownError,
                message: $"Failed to start chain at {hops[0].Endpoint}: {ex.Message}",
                inner: ex);
        }
        finally
        {
            if (ownsStream)
            {
                if (stream is not null)
                    await stream.DisposeAsync().ConfigureAwait(false);
                else
                    socket?.Dispose();
            }
        }
    }

    public Func<SocketsHttpConnectionContext, CancellationToken, ValueTask<Stream>> CreateConnectCallback(
        IReadOnlyList<ProxyHop> hops) =>
        async (context, ct) =>
        {
            var dest = new ChainDestination(context.DnsEndPoint.Host, context.DnsEndPoint.Port);
            return await ConnectAsync(hops, dest, ct).ConfigureAwait(false);
        };

    private static async Task<Stream> WrapTlsAsync(Stream inner, string targetHost, CancellationToken ct)
    {
        var ssl = new SslStream(inner, leaveInnerStreamOpen: false);
        try
        {
            await ssl.AuthenticateAsClientAsync(
                new SslClientAuthenticationOptions
                {
                    TargetHost = targetHost.Trim().TrimStart('[').TrimEnd(']'),
                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13
                },
                ct).ConfigureAwait(false);
            return ssl;
        }
        catch
        {
            await ssl.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static async Task HandshakeAsync(
        Stream stream,
        ProxyHop hop,
        string nextHost,
        int nextPort,
        HandshakeOptions options,
        CancellationToken ct)
    {
        switch (hop.Kind)
        {
            case ProxyKind.Socks5:
                await Socks5Handshake.ConnectAsync(stream, hop.Proxy, nextHost, nextPort, options, ct)
                    .ConfigureAwait(false);
                break;
            case ProxyKind.Socks4:
                await Socks4Handshake.ConnectAsync(stream, hop.Proxy, nextHost, nextPort, options, ct)
                    .ConfigureAwait(false);
                break;
            case ProxyKind.Http:
                await HttpConnectHandshake.ConnectAsync(stream, hop.Proxy, nextHost, nextPort, options, ct)
                    .ConfigureAwait(false);
                break;
            default:
                throw new ProxyHandshakeException(FailureReason.ProxyHandshakeFailure,
                    $"Unsupported hop kind {hop.Kind}.");
        }
    }

    private static async Task<Socket> ConnectTcpAsync(string host, int port, CancellationToken ct)
    {
        if (!IPAddress.TryParse(host, out var ip))
        {
            var addrs = await System.Net.Dns.GetHostAddressesAsync(host, ct).ConfigureAwait(false);
            ip = addrs.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork)
                 ?? addrs.FirstOrDefault()
                 ?? throw new SocketException((int)SocketError.HostNotFound);
        }

        var socket = new Socket(ip.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(new IPEndPoint(ip, port), ct).ConfigureAwait(false);
            return socket;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    internal static string FormatEndpoint(string host, int port)
    {
        host = host.Trim();
        if (IPAddress.TryParse(host.TrimStart('[').TrimEnd(']'), out var ip) &&
            ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            var bare = host.TrimStart('[').TrimEnd(']');
            return $"[{bare}]:{port}";
        }

        return host.Contains(':') && !host.StartsWith('[')
            ? $"[{host}]:{port}"
            : $"{host}:{port}";
    }

    /// <summary>
    /// Proxifier rule: an HTTP CONNECT hop must be the final proxy in the chain.
    /// See proxe-code <c>extracted/proxy-chain/MAPPING.md</c> (FUN_140068a50).
    /// </summary>
    public static void ValidateHopOrder(IReadOnlyList<ProxyHop> hops)
    {
        ArgumentNullException.ThrowIfNull(hops);
        for (var i = 0; i < hops.Count - 1; i++)
        {
            if (hops[i].Kind == ProxyKind.Http)
            {
                throw new ArgumentException(
                    "HTTP proxy server must be the last one in the chain.",
                    nameof(hops));
            }
        }
    }
}
