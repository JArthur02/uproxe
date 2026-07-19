using UProxy.Core.Chaining;
using UProxy.Core.Models;

namespace UProxy.Core.Gateway;

/// <summary>Opens a tunneled TCP stream to a destination (typically via <see cref="ChainDialer"/>).</summary>
public interface IChainConnector
{
    Task<Stream> ConnectAsync(ChainDestination destination, CancellationToken cancellationToken);
}

/// <summary>Adapts <see cref="ChainDialer"/> + a fixed hop list to <see cref="IChainConnector"/>.</summary>
public sealed class ChainDialerConnector : IChainConnector
{
    private readonly ChainDialer _dialer;
    private readonly IReadOnlyList<ProxyHop> _hops;
    private readonly TimeSpan? _overallTimeout;

    public ChainDialerConnector(
        ChainDialer dialer,
        IReadOnlyList<ProxyHop> hops,
        TimeSpan? overallTimeout = null)
    {
        _dialer = dialer ?? throw new ArgumentNullException(nameof(dialer));
        _hops = hops ?? throw new ArgumentNullException(nameof(hops));
        if (_hops.Count == 0)
            throw new ArgumentException("At least one hop is required.", nameof(hops));
        _overallTimeout = overallTimeout;
    }

    public Task<Stream> ConnectAsync(ChainDestination destination, CancellationToken cancellationToken) =>
        _dialer.ConnectAsync(_hops, destination, cancellationToken, _overallTimeout);
}

/// <summary>Adapts <see cref="ChainManager"/> to <see cref="IChainConnector"/> for local gateways.</summary>
public sealed class ChainManagerConnector : IChainConnector
{
    private readonly ChainManager _manager;

    public ChainManagerConnector(ChainManager manager)
    {
        _manager = manager ?? throw new ArgumentNullException(nameof(manager));
    }

    public Task<Stream> ConnectAsync(ChainDestination destination, CancellationToken cancellationToken) =>
        _manager.ConnectAsync(destination, cancellationToken);
}

/// <summary>Direct TCP connect (no proxy hops) — useful for unit tests.</summary>
public sealed class DirectTcpConnector : IChainConnector
{
    public async Task<Stream> ConnectAsync(ChainDestination destination, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(destination.Host))
            throw new ArgumentException("Destination host is required.", nameof(destination));
        if (destination.Port is < 1 or > 65535)
            throw new ArgumentOutOfRangeException(nameof(destination));

        var host = destination.Host.Trim().TrimStart('[').TrimEnd(']');
        System.Net.IPAddress? ip = null;
        if (!System.Net.IPAddress.TryParse(host, out ip))
        {
            var addrs = await System.Net.Dns.GetHostAddressesAsync(host, cancellationToken).ConfigureAwait(false);
            ip = addrs.FirstOrDefault(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                 ?? addrs.FirstOrDefault()
                 ?? throw new System.Net.Sockets.SocketException((int)System.Net.Sockets.SocketError.HostNotFound);
        }

        var socket = new System.Net.Sockets.Socket(ip.AddressFamily, System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Tcp)
        {
            NoDelay = true
        };
        try
        {
            await socket.ConnectAsync(new System.Net.IPEndPoint(ip, destination.Port), cancellationToken)
                .ConfigureAwait(false);
            return new System.Net.Sockets.NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}
