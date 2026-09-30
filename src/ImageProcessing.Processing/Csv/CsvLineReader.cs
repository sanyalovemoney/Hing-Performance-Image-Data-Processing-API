using System.Buffers;
using System.Runtime.CompilerServices;

namespace ImageProcessing.Processing.Csv;

/// <summary>
/// One logical CSV line. The backing memory is rented and REUSED by the reader:
/// it is only valid until the next iteration step — consume it inside the loop body.
/// </summary>
public readonly struct CsvLine
{
    public ReadOnlyMemory<byte> Memory { get; }

    public CsvLine(ReadOnlyMemory<byte> memory) => Memory = memory;

    public bool IsEmpty => Memory.Length == 0;

    public CsvLine TrimCarriageReturn() =>
        Memory.Length > 0 && Memory.Span[^1] == (byte)'\r' ? new CsvLine(Memory[..^1]) : this;
}

/// <summary>
/// Handler for the zero-allocation line path. The memory aliases a pooled buffer
/// and is only valid for the duration of the call — do not retain it.
/// </summary>
public delegate ValueTask CsvLineHandler(ReadOnlyMemory<byte> line);

/// <summary>
/// Buffered line reader over any Stream: one pooled buffer, lines assembled
/// across read boundaries, CRLF/LF tolerant, never loads the whole file.
/// </summary>
public static class CsvLineReader
{
    public const int DefaultBufferSize = 65536;
    private const int MaxLineBytes = 4 * 1024 * 1024;

    /// <summary>
    /// Raw line enumeration. Yielded <see cref="CsvLine"/> instances alias rented
    /// buffers and are only valid until the next MoveNextAsync — the standard
    /// foreach body satisfies this contract.
    /// </summary>
    public static async IAsyncEnumerable<CsvLine> ReadLinesAsync(
        Stream source,
        [EnumeratorCancellation] CancellationToken ct = default,
        int bufferSize = DefaultBufferSize)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(bufferSize, 64);
        await using (source.ConfigureAwait(false))
        {
            var buffer = ArrayPool<byte>.Shared.Rent(bufferSize);
            var carry = ArrayPool<byte>.Shared.Rent(bufferSize);
            var carryCap = bufferSize;
            try
            {
                var carryLen = 0;
                int read;
                while ((read = await source.ReadAsync(buffer.AsMemory(0, bufferSize), ct).ConfigureAwait(false)) > 0)
                {
                    var pos = 0;
                    while (pos < read)
                    {
                        // C# 12 bans ref-struct locals in async iterators, so the
                        // span work lives in the sync helpers below.
                        var nl = IndexOfNewline(buffer, pos, read - pos);
                        if (nl < 0)
                        {
                            // Partial line — park it in the carry buffer.
                            EnsureCarry(ref carry, ref carryCap, carryLen, carryLen + (read - pos));
                            CopySlice(buffer, pos, read - pos, carry, carryLen);
                            carryLen += read - pos;
                            break;
                        }

                        CsvLine line;
                        if (carryLen > 0)
                        {
                            EnsureCarry(ref carry, ref carryCap, carryLen, carryLen + nl);
                            CopySlice(buffer, pos, nl, carry, carryLen);
                            carryLen += nl;
                            line = new CsvLine(carry.AsMemory(0, carryLen));
                        }
                        else
                        {
                            line = new CsvLine(buffer.AsMemory(pos, nl));
                        }

                        pos += nl + 1;
                        var trimmed = line.TrimCarriageReturn();
                        if (!trimmed.IsEmpty)
                        {
                            yield return trimmed;
                        }

                        carryLen = 0;
                    }
                }

                if (carryLen > 0)
                {
                    var tail = new CsvLine(carry.AsMemory(0, carryLen)).TrimCarriageReturn();
                    if (!tail.IsEmpty)
                    {
                        yield return tail;
                    }
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
                ArrayPool<byte>.Shared.Return(carry);
            }
        }
    }

    /// <summary>Zero-allocation line walk — the handler sees raw UTF-8 spans.</summary>
    public static async Task ForEachLineAsync(
        Stream source,
        CsvLineHandler onLine,
        CancellationToken ct = default,
        int bufferSize = DefaultBufferSize)
    {
        await foreach (var line in ReadLinesAsync(source, ct, bufferSize).ConfigureAwait(false))
        {
            await onLine(line.Memory).ConfigureAwait(false);
        }
    }

    /// <summary>Materializing walk for clients that need strings (NDJSON streaming).</summary>
    public static async IAsyncEnumerable<CsvRow> ReadRowsAsync(
        Stream source,
        [EnumeratorCancellation] CancellationToken ct = default,
        int bufferSize = DefaultBufferSize)
    {
        var index = 0;
        await foreach (var line in ReadLinesAsync(source, ct, bufferSize).ConfigureAwait(false))
        {
            yield return SpanCsvParser.Materialize(line.Memory, index++);
        }
    }

    private static int IndexOfNewline(byte[] buffer, int start, int count) =>
        buffer.AsSpan(start, count).IndexOf((byte)'\n');

    private static void CopySlice(byte[] source, int sourceStart, int count, byte[] destination, int destinationStart) =>
        source.AsSpan(sourceStart, count).CopyTo(destination.AsSpan(destinationStart));

    private static void EnsureCarry(ref byte[] carry, ref int carryCap, int carryLen, int needed)
    {
        if (needed <= carryCap)
        {
            return;
        }

        if (needed > MaxLineBytes)
        {
            throw new FormatException($"CSV line exceeds the {MaxLineBytes}-byte limit");
        }

        var newCap = Math.Min(Math.Max(carryCap * 2, needed), MaxLineBytes);
        var fresh = ArrayPool<byte>.Shared.Rent(newCap);
        carry.AsSpan(0, carryLen).CopyTo(fresh);
        ArrayPool<byte>.Shared.Return(carry);
        carry = fresh;
        carryCap = newCap;
    }
}
