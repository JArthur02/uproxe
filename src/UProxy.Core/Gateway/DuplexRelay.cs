namespace UProxy.Core.Gateway;

/// <summary>Bidirectional byte copy between two streams with idle timeout; disposes both when finished.</summary>
public static class DuplexRelay
{
    public const int DefaultBufferSize = 81920;

    /// <summary>
    /// Copies both directions until EOF, idle timeout, or cancellation.
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
        try
        {
            var t1 = CopyOneWayAsync(left, right, idleTimeout, linked, bufferSize);
            var t2 = CopyOneWayAsync(right, left, idleTimeout, linked, bufferSize);
            var first = await Task.WhenAny(t1, t2).ConfigureAwait(false);
            linked.Cancel();
            try
            {
                await first.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                // Idle timeout, EOF, or peer reset — normal relay end.
            }

            try
            {
                await Task.WhenAll(t1, t2).ConfigureAwait(false);
            }
            catch
            {
                // Other direction cancelled or failed after first finished.
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
            using var readCts = CancellationTokenSource.CreateLinkedTokenSource(sharedCts.Token);
            readCts.CancelAfter(idleTimeout);

            int n;
            try
            {
                n = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), readCts.Token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!sharedCts.Token.IsCancellationRequested)
            {
                // Idle timeout on this direction — stop the whole relay.
                sharedCts.Cancel();
                throw;
            }

            if (n == 0)
            {
                sharedCts.Cancel();
                return;
            }

            await destination.WriteAsync(buffer.AsMemory(0, n), sharedCts.Token).ConfigureAwait(false);
            await destination.FlushAsync(sharedCts.Token).ConfigureAwait(false);
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
