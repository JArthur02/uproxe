using UProxy.Core.Models;

namespace UProxy.Core.Persistence;

internal static class PersistenceProxyMapping
{
    public static ProxyProtocol ProtocolFromKind(ProxyKind kind) => kind switch
    {
        ProxyKind.Http => ProxyProtocol.Http,
        ProxyKind.Socks4 => ProxyProtocol.Socks4,
        ProxyKind.Socks5 => ProxyProtocol.Socks5,
        _ => ProxyProtocol.Unknown
    };
}
