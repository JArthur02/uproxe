using System.Text;
using UProxy.Core.Chaining;
using UProxy.Core.Models;

namespace UProxy.Core.Tests;

public class StreamProtocolReaderTests
{
    [Fact]
    public async Task ReadExact_AssemblesPartialWrites()
    {
        var chunks = new Queue<byte[]>(
        [
            [1, 2],
            [3],
            [4, 5, 6]
        ]);
        await using var stream = new PartialReadStream(chunks);
        var buf = new byte[6];
        await StreamProtocolReader.ReadExactAsync(stream, buf, CancellationToken.None);
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 6 }, buf);
    }

    [Fact]
    public async Task ReadExact_ThrowsOnPrematureEof()
    {
        await using var stream = new MemoryStream([1, 2]);
        var buf = new byte[4];
        await Assert.ThrowsAsync<ProxyHandshakeException>(() =>
            StreamProtocolReader.ReadExactAsync(stream, buf, CancellationToken.None));
    }

    [Fact]
    public async Task ReadUntil_FindsDelimiterAndRespectsMax()
    {
        var payload = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nFoo: bar\r\n\r\nEXTRA");
        await using var stream = new MemoryStream(payload);
        var headers = await StreamProtocolReader.ReadUntilAsync(
            stream, "\r\n\r\n"u8.ToArray(), 1024, CancellationToken.None);
        Assert.Equal("HTTP/1.1 200 OK\r\nFoo: bar\r\n\r\n", Encoding.ASCII.GetString(headers));
        Assert.Equal((byte)'E', stream.ReadByte());
    }

    [Fact]
    public async Task ReadUntil_ThrowsWhenExceedsMax()
    {
        var payload = Encoding.ASCII.GetBytes(new string('A', 100));
        await using var stream = new MemoryStream(payload);
        await Assert.ThrowsAsync<ProxyHandshakeException>(() =>
            StreamProtocolReader.ReadUntilAsync(stream, "\r\n\r\n"u8.ToArray(), 32, CancellationToken.None));
    }

    private sealed class PartialReadStream(Queue<byte[]> chunks) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (chunks.Count == 0)
                return ValueTask.FromResult(0);
            var chunk = chunks.Dequeue();
            chunk.AsSpan().CopyTo(buffer.Span);
            return ValueTask.FromResult(chunk.Length);
        }
    }
}
