using UProxy.Core.Models;

namespace UProxy.Core.Chaining;

/// <summary>Performs one hop's CONNECT-style handshake on an already-connected <see cref="Stream"/>.</summary>
public interface IProxyHandshake
{
    Task<ProxyAuthMethod> ConnectAsync(
        Stream stream,
        ParsedProxy proxyCredentials,
        string destinationHost,
        int destinationPort,
        HandshakeOptions options,
        CancellationToken cancellationToken);
}

public sealed class HandshakeOptions
{
    public bool UseSocks4a { get; init; } = true;
    public bool ResolveHostnamesThroughProxy { get; init; } = true;
    public string? UserAgent { get; init; }
    /// <summary>Max HTTP CONNECT response headers (bytes).</summary>
    public int MaxHttpHeaderBytes { get; init; } = 32 * 1024;
}
