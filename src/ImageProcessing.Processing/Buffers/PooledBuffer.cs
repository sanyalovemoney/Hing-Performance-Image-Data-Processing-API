using System.Buffers;

namespace ImageProcessing.Processing.Buffers;

/// <summary>
/// Stream copy helpers backed by <see cref="ArrayPool{T}"/> — constant memory
/// cost regardless of payload size.
/// </summary>
public static class PooledBuffer
{
    /// <summary>80 KB — Kestrel's default pipe buffer size; good throughput/locality tradeoff.</summary>
    public const int DefaultBufferSize = 81920;

    /// <summary>
    /// Copies <paramref name="from"/> to <paramref name="to"/> through a rented
    /// buffer. Returns the total number of bytes copied.
    /// </summary>
    public static async Task<long> CopyToPooledAsync(
        this Stream from,
        Stream to,
        CancellationToken ct = default,
        int bufferSize = DefaultBufferSize)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(bufferSize);
        long total = 0;
        try
        {
            int read;
            while ((read = await from.ReadAsync(buffer.AsMemory(0, buffer.Length), ct).ConfigureAwait(false)) > 0)
            {
                await to.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                total += read;
            }

            await to.FlushAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        return total;
    }
}
