using System.Net.Security;
using System.Net.Sockets;

namespace UProxy.Core.Gateway;

/// <summary>Bidirectional byte copy between two streams with idle timeout; disposes both when finished.</summary>
public static class DuplexRelay
{
    public const int DefaultBufferSize = 81920;

    /// <summary>
    /// Copies both directions until EOF on both sides, idle timeout, or cancellation.
    /// On EOF from one direction, completes writes on the other stream (TCP half-close)
    /// and leaves the opposite direction running.
    /// Both directions share one idle timer: any successful read resets it.
    /// Always disposes <paramref name="left"/> and <paramref name="right"/> when the relay ends.
    /// </summary>
    public static async Task RunAsync(
        Stream left,
        Stream right,
        TimeSpan idleTimeout,
        CancellationToken cancellationToken,
        int bufferSize = DefaultBufferSize)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        if (idleTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(idleTimeout));
        if (bufferSize < 1)
            throw new ArgumentOutOfRangeException(nameof(bufferSize));

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        linked.CancelAfter(idleTimeout);
        try
        {
            var t1 = CopyOneWayAsync(left, right, idleTimeout, linked, bufferSize);
            var t2 = CopyOneWayAsync(right, left, idleTimeout, linked, bufferSize);
            try
            {
                await Task.WhenAll(t1, t2).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                // Idle timeout, peer reset, or half-close races — normal relay end.
            }
        }
        finally
        {
            await DisposeQuietAsync(left).ConfigureAwait(false);
            await DisposeQuietAsync(right).ConfigureAwait(false);
        }
    }

    private static async Task CopyOneWayAsync(
        Stream source,
        Stream destination,
        TimeSpan idleTimeout,
        CancellationTokenSource sharedCts,
        int bufferSize)
    {
        var buffer = new byte[bufferSize];
        while (!sharedCts.IsCancellationRequested)
        {
            var n = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), sharedCts.Token)
                .ConfigureAwait(false);

            if (n == 0)
            {
                // Half-close: finish writes toward the peer; do not cancel the opposite direction.
                await CompleteWritesAsync(destination).ConfigureAwait(false);
                return;
            }

            // Any successful read resets the shared idle timer for both directions.
            sharedCts.CancelAfter(idleTimeout);

            await destination.WriteAsync(buffer.AsMemory(0, n), sharedCts.Token).ConfigureAwait(false);
            await destination.FlushAsync(sharedCts.Token).ConfigureAwait(false);
        }
    }

    private static async Task CompleteWritesAsync(Stream stream)
    {
        try
        {
            switch (stream)
            {
                case NetworkStream network:
                    network.Socket.Shutdown(SocketShutdown.Send);
                    break;
                case SslStream ssl:
                    await ssl.ShutdownAsync().ConfigureAwait(false);
                    break;
                default:
                    await stream.FlushAsync().ConfigureAwait(false);
                    break;
            }
        }
        catch
        {
            // Best-effort half-close.
        }
    }

    private static async Task DisposeQuietAsync(Stream stream)
    {
        try
        {
            await stream.DisposeAsync().ConfigureAwait(false);
        }
        catch
        {
            // ignore dispose races
        }
    }
}
