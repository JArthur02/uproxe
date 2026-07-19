using UProxy.Core.Models;

namespace UProxy.Core.Chaining;

/// <summary>Thrown when a proxy hop handshake fails with a classified reason.</summary>
public sealed class ProxyHandshakeException : Exception
{
    public ProxyHandshakeException(FailureReason reason, string message, Exception? inner = null)
        : base(message, inner)
    {
        Reason = reason;
    }

    public FailureReason Reason { get; }
}

/// <summary>Thrown by <see cref="ChainDialer"/> when a specific hop/edge fails.</summary>
public sealed class ChainDialException : Exception
{
    public ChainDialException(
        int failedHopIndex,
        string? fromEndpoint,
        string? toEndpoint,
        FailureReason reason,
        string message,
        Exception? inner = null)
        : base(message, inner)
    {
        FailedHopIndex = failedHopIndex;
        FromEndpoint = fromEndpoint;
        ToEndpoint = toEndpoint;
        Reason = reason;
    }

    /// <summary>Zero-based index of the hop whose handshake failed, or -1 for first-connect failures.</summary>
    public int FailedHopIndex { get; }
    public string? FromEndpoint { get; }
    public string? ToEndpoint { get; }
    public FailureReason Reason { get; }
}
