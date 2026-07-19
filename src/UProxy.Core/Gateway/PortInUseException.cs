using System.Net;
using System.Net.Sockets;

namespace UProxy.Core.Gateway;

/// <summary>Thrown when a local gateway listener cannot bind because the port is occupied.</summary>
public sealed class PortInUseException : Exception
{
    public int Port { get; }
    public int? SuggestedPort { get; }

    public PortInUseException(int port, int? suggestedPort = null, Exception? innerException = null)
        : base(FormatMessage(port, suggestedPort), innerException)
    {
        Port = port;
        SuggestedPort = suggestedPort;
    }

    private static string FormatMessage(int port, int? suggestedPort) =>
        suggestedPort is int s
            ? $"Port {port} is already in use. Suggested free port: {s}."
            : $"Port {port} is already in use.";
}

/// <summary>Helpers for picking an unused loopback TCP port.</summary>
public static class LoopbackPortFinder
{
    /// <summary>Bind ephemeral port on loopback and return it (listener is released immediately).</summary>
    public static int FindFreePort(IPAddress? address = null)
    {
        address ??= IPAddress.Loopback;
        var listener = new TcpListener(address, 0);
        listener.Start();
        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }

    internal static bool IsAddressInUse(Exception ex)
    {
        for (var cur = ex; cur is not null; cur = cur.InnerException)
        {
            if (cur is SocketException se &&
                (se.SocketErrorCode == SocketError.AddressAlreadyInUse ||
                 se.SocketErrorCode == SocketError.AccessDenied))
            {
                return true;
            }
        }

        return false;
    }
}
