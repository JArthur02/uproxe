using System.Buffers;
using UProxy.Core.Models;

namespace UProxy.Core.Chaining;

/// <summary>Exact-length and bounded reads for binary/text proxy protocols.</summary>
public static class StreamProtocolReader
{
    public static async Task ReadExactAsync(Stream stream, Memory<byte> buffer, CancellationToken ct)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var n = await stream.ReadAsync(buffer[offset..], ct).ConfigureAwait(false);
            if (n == 0)
                throw new ProxyHandshakeException(FailureReason.EmptyResponse,
                    "Connection closed while reading protocol data.");
            offset += n;
        }
    }

    public static async Task ReadExactAsync(Stream stream, byte[] buffer, CancellationToken ct) =>
        await ReadExactAsync(stream, buffer.AsMemory(), ct).ConfigureAwait(false);

    /// <summary>
    /// Reads until <paramref name="delimiter"/> appears, up to <paramref name="maxBytes"/>.
    /// Returns the buffer including the delimiter.
    /// </summary>
    public static async Task<byte[]> ReadUntilAsync(
        Stream stream,
        ReadOnlyMemory<byte> delimiter,
        int maxBytes,
        CancellationToken ct)
    {
        if (delimiter.Length == 0)
            throw new ArgumentException("Delimiter must be non-empty.", nameof(delimiter));
        if (maxBytes < delimiter.Length)
            throw new ArgumentOutOfRangeException(nameof(maxBytes));

        var buffer = ArrayPool<byte>.Shared.Rent(Math.Min(maxBytes, 4096));
        try
        {
            using var ms = new MemoryStream(capacity: Math.Min(maxBytes, 512));
            var match = 0;
            while (ms.Length < maxBytes)
            {
                var n = await stream.ReadAsync(buffer.AsMemory(0, 1), ct).ConfigureAwait(false);
                if (n == 0)
                    throw new ProxyHandshakeException(FailureReason.EmptyResponse,
                        "Connection closed before delimiter.");

                ms.WriteByte(buffer[0]);
                if (buffer[0] == delimiter.Span[match])
                {
                    match++;
                    if (match == delimiter.Length)
                        return ms.ToArray();
                }
                else
                {
                    // Restart match if current byte could begin the delimiter.
                    match = buffer[0] == delimiter.Span[0] ? 1 : 0;
                }
            }

            throw new ProxyHandshakeException(FailureReason.ProxyHandshakeFailure,
                $"Protocol message exceeded {maxBytes} bytes without delimiter.");
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}
