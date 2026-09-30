using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace ImageProcessing.Processing.Matrix;

/// <summary>
/// Binary matrix format: 8-byte header (int32 rows, int32 cols — little-endian)
/// followed by rows*cols float32 values in little-endian, row-major order.
/// Malformed files throw <see cref="FormatException"/> so jobs land in Failed
/// with a readable error. I/O runs through a bounded pooled 80KB chunk buffer —
/// the only large allocation is the caller's element array itself.
/// </summary>
public static class MatrixFileFormat
{
    public const int HeaderBytes = 8;

    /// <summary>Hard cap: 64M floats ≈ 256 MB of data.</summary>
    public const int MaxElements = 64_000_000;

    private const int ChunkBytes = 81920;          // 80KB, divisible by 4
    private const int ChunkFloats = ChunkBytes / 4;

    public static (int Rows, int Cols) ReadHeader(ReadOnlySpan<byte> header)
    {
        if (header.Length < HeaderBytes)
        {
            throw new FormatException($"matrix header requires {HeaderBytes} bytes, got {header.Length}");
        }

        var rows = BinaryPrimitives.ReadInt32LittleEndian(header);
        var cols = BinaryPrimitives.ReadInt32LittleEndian(header.Slice(4));
        ValidateDimensions(rows, cols);
        return (rows, cols);
    }

    public static void ValidateDimensions(int rows, int cols)
    {
        if (rows <= 0 || cols <= 0)
        {
            throw new FormatException($"matrix dimensions must be positive, got {rows}x{cols}");
        }

        if ((long)rows * cols > MaxElements)
        {
            throw new FormatException($"matrix {rows}x{cols} exceeds the {MaxElements}-element cap");
        }
    }

    /// <summary>Reads and validates the header; data reads follow via <see cref="ReadDataAsync"/>.</summary>
    public static async Task<(int Rows, int Cols)> ReadHeaderAsync(Stream source, CancellationToken ct)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(HeaderBytes);
        try
        {
            try
            {
                await source.ReadExactlyAsync(buffer.AsMemory(0, HeaderBytes), ct).ConfigureAwait(false);
            }
            catch (EndOfStreamException ex)
            {
                throw new FormatException("file is shorter than the matrix header", ex);
            }

            return ReadHeader(buffer);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>
    /// Fills data[0..count) from the stream; count must equal rows*cols.
    /// Rejects both truncation and trailing garbage.
    /// </summary>
    public static async Task ReadDataAsync(Stream source, float[] data, int count, CancellationToken ct)
    {
        var raw = ArrayPool<byte>.Shared.Rent(ChunkBytes);
        try
        {
            var decoded = 0;
            while (decoded < count)
            {
                var n = Math.Min(ChunkFloats, count - decoded);
                try
                {
                    await source.ReadExactlyAsync(raw.AsMemory(0, n * 4), ct).ConfigureAwait(false);
                }
                catch (EndOfStreamException ex)
                {
                    throw new FormatException($"file is truncated: expected {count} float32 values", ex);
                }

                DecodeChunk(raw, data, decoded, n);
                decoded += n;
            }

            if (source.CanSeek && source.Length > HeaderBytes + (long)count * 4)
            {
                throw new FormatException(
                    $"file has {source.Length - HeaderBytes - (long)count * 4} trailing bytes after the matrix data");
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(raw);
        }
    }

    /// <summary>Writes header + data; data must contain at least rows*cols values.</summary>
    public static async Task WriteAsync(
        Stream destination, int rows, int cols, float[] data, CancellationToken ct)
    {
        ValidateDimensions(rows, cols);
        var count = rows * cols;
        if (data.Length < count)
        {
            throw new ArgumentException($"data holds {data.Length} values, need {count}", nameof(data));
        }

        WriteHeader(destination, rows, cols);

        var raw = ArrayPool<byte>.Shared.Rent(ChunkBytes);
        try
        {
            var encoded = 0;
            while (encoded < count)
            {
                var n = Math.Min(ChunkFloats, count - encoded);
                EncodeChunk(data, encoded, n, raw);
                await destination.WriteAsync(raw.AsMemory(0, n * 4), ct).ConfigureAwait(false);
                encoded += n;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(raw);
        }
    }

    // ---- sync helpers: the only place span/stackalloc locals may live (C# 12
    // bans ref-struct locals in async method bodies) ----

    private static void WriteHeader(Stream destination, int rows, int cols)
    {
        Span<byte> header = stackalloc byte[HeaderBytes];
        BinaryPrimitives.WriteInt32LittleEndian(header, rows);
        BinaryPrimitives.WriteInt32LittleEndian(header.Slice(4), cols);
        destination.Write(header); // 8 bytes — sync write avoids an allocation
    }

    private static void DecodeChunk(byte[] raw, float[] data, int dataOffset, int floats)
    {
        if (BitConverter.IsLittleEndian)
        {
            raw.AsSpan(0, floats * 4)
                .CopyTo(MemoryMarshal.AsBytes(data.AsSpan(dataOffset, floats)));
        }
        else
        {
            // File is little-endian by contract; fix up element by element on BE hosts.
            for (var i = 0; i < floats; i++)
            {
                data[dataOffset + i] = BinaryPrimitives.ReadSingleLittleEndian(raw.AsSpan(i * 4, 4));
            }
        }
    }

    private static void EncodeChunk(float[] data, int dataOffset, int floats, byte[] raw)
    {
        if (BitConverter.IsLittleEndian)
        {
            MemoryMarshal.AsBytes(data.AsSpan(dataOffset, floats))
                .CopyTo(raw.AsSpan(0, floats * 4));
        }
        else
        {
            for (var i = 0; i < floats; i++)
            {
                BinaryPrimitives.WriteSingleLittleEndian(raw.AsSpan(i * 4, 4), data[dataOffset + i]);
            }
        }
    }
}
