using System.Buffers;

namespace ImageProcessing.Processing.Buffers;

/// <summary>
/// Growable in-memory stream backed by fixed-size <see cref="ArrayPool{T}"/>
/// segments instead of MemoryStream's allocate-and-double growth.
/// Forward-write / forward-read only (CanSeek == false); Dispose returns all
/// rented buffers.
/// </summary>
public sealed class PooledBufferStream : Stream
{
    private readonly List<byte[]> _segments = new();
    private readonly int _chunkSize;
    private int _writeOffset;
    private long _length;
    private long _position;
    private bool _disposed;

    public PooledBufferStream(int chunkSize = PooledBuffer.DefaultBufferSize)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(chunkSize, 16);
        _chunkSize = chunkSize;
    }

    public override bool CanRead => !_disposed;
    public override bool CanSeek => false;
    public override bool CanWrite => !_disposed;

    public override long Length
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _length;
        }
    }

    public override long Position
    {
        get => _position;
        set => throw new NotSupportedException("PooledBufferStream is forward-only.");
    }

    public override void Flush()
    {
    }

    public override void Write(byte[] buffer, int offset, int count) =>
        Write(new ReadOnlySpan<byte>(buffer, offset, count));

    public override void Write(ReadOnlySpan<byte> source)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        while (source.Length > 0)
        {
            if (_segments.Count == 0 || _writeOffset == _chunkSize)
            {
                _segments.Add(ArrayPool<byte>.Shared.Rent(_chunkSize));
                _writeOffset = 0;
            }

            var segment = _segments[^1];
            var take = Math.Min(_chunkSize - _writeOffset, source.Length);
            source[..take].CopyTo(segment.AsSpan(_writeOffset, take));
            _writeOffset += take;
            _length += take;
            source = source[take..];
        }
    }

    public override void WriteByte(byte value)
    {
        Span<byte> one = stackalloc byte[1];
        one[0] = value;
        Write(one);
    }

    public override int Read(byte[] buffer, int offset, int count) =>
        Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> destination)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var total = 0;
        while (destination.Length > 0 && _position < _length)
        {
            var segmentIndex = (int)(_position / _chunkSize);
            var segmentOffset = (int)(_position % _chunkSize);
            var available = (int)Math.Min(_chunkSize - segmentOffset, _length - _position);
            var take = Math.Min(available, destination.Length);

            _segments[segmentIndex].AsSpan(segmentOffset, take).CopyTo(destination[..take]);
            destination = destination[take..];
            _position += take;
            total += take;
        }

        return total;
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (!_disposed)
        {
            if (disposing)
            {
                foreach (var segment in _segments)
                {
                    ArrayPool<byte>.Shared.Return(segment);
                }

                _segments.Clear();
            }

            _disposed = true;
        }

        base.Dispose(disposing);
    }
}
