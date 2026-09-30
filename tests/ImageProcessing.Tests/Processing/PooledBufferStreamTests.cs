using ImageProcessing.Processing.Buffers;

namespace ImageProcessing.Tests.Processing;

public class PooledBufferStreamTests
{
    [Fact]
    public void Write_then_read_roundtrips_across_segments()
    {
        using var stream = new PooledBufferStream(chunkSize: 1024);
        var data = new byte[5000]; // spans 5 segments
        new Random(42).NextBytes(data);

        stream.Write(data);
        Assert.Equal(5000, stream.Length);

        var back = new byte[5000];
        var read = stream.Read(back);
        Assert.Equal(5000, read);
        Assert.Equal(data, back);
        Assert.Equal(0, stream.Read(new byte[16])); // EOF
    }

    [Fact]
    public void WriteByte_appends_single_bytes()
    {
        using var stream = new PooledBufferStream(chunkSize: 16);

        stream.WriteByte(1);
        stream.WriteByte(2);
        stream.WriteByte(3);

        Assert.Equal(3, stream.Length);
        var back = new byte[3];
        Assert.Equal(3, stream.Read(back));
        Assert.Equal([1, 2, 3], back);
    }

    [Fact]
    public async Task CopyToPooledAsync_feeds_stream_from_file()
    {
        var path = Path.Combine(Path.GetTempPath(), $"pooled-{Guid.NewGuid():N}.bin");
        var payload = new byte[200_000];
        new Random(7).NextBytes(payload);
        await File.WriteAllBytesAsync(path, payload);

        try
        {
            using var file = File.OpenRead(path);
            using var stream = new PooledBufferStream(chunkSize: 4096);

            var copied = await file.CopyToPooledAsync(stream);

            Assert.Equal(payload.Length, copied);
            Assert.Equal(payload.Length, stream.Length);
            var back = new byte[payload.Length];
            Assert.Equal(payload.Length, stream.Read(back));
            Assert.Equal(payload, back);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Seek_and_SetLength_are_not_supported()
    {
        using var stream = new PooledBufferStream();

        Assert.False(stream.CanSeek);
        Assert.Throws<NotSupportedException>(() => stream.Seek(0, SeekOrigin.Begin));
        Assert.Throws<NotSupportedException>(() => stream.SetLength(10));
    }

    [Fact]
    public void Small_chunk_sizes_are_rejected() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new PooledBufferStream(chunkSize: 4));
}
