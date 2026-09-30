using System.Globalization;
using System.Text;
using ImageProcessing.Processing.Csv;

namespace ImageProcessing.Tests.Processing;

public sealed class CsvLineReaderTests
{
    private static MemoryStream Stream(string content) => new(Encoding.UTF8.GetBytes(content));

    private static async Task<List<string>> ReadAllAsync(Stream source, int bufferSize = CsvLineReader.DefaultBufferSize)
    {
        var lines = new List<string>();
        await CsvLineReader.ForEachLineAsync(
            source,
            line =>
            {
                lines.Add(Encoding.UTF8.GetString(line.Span));
                return default;
            },
            CancellationToken.None,
            bufferSize);
        return lines;
    }

    [Fact]
    public async Task Reads_lines_with_mixed_endings_and_skips_blank_ones()
    {
        var lines = await ReadAllAsync(Stream("a\r\n\r\nb\n\n\r\nc"));
        Assert.Equal(new[] { "a", "b", "c" }, lines);
    }

    [Fact]
    public async Task Trailing_line_without_newline_is_emitted()
    {
        Assert.Equal(new[] { "a", "b" }, await ReadAllAsync(Stream("a\nb")));
        Assert.Equal(new[] { "a", "b" }, await ReadAllAsync(Stream("a\r\nb")));
    }

    [Fact]
    public async Task Lines_longer_than_buffer_survive_read_boundaries()
    {
        // bufferSize 64 forces nearly every line (≈40 chars) to straddle reads.
        var expected = Enumerable.Range(0, 100)
            .Select(i => $"line-{i:D4},payload-payload-payload-{i * 7919 % 1000:D3}")
            .ToArray();
        var content = string.Join('\n', expected) + "\n";

        var lines = await ReadAllAsync(Stream(content), bufferSize: 64);

        Assert.Equal(expected, lines);
    }

    [Fact]
    public async Task ReadRowsAsync_materializes_rows_with_sequential_indexes()
    {
        var rows = new List<CsvRow>();
        await foreach (var row in CsvLineReader.ReadRowsAsync(Stream("id,name\n1,\"ann, a\"\n2,bob\n")))
        {
            rows.Add(row);
        }

        Assert.Equal(3, rows.Count);
        Assert.Equal(0, rows[0].Index);
        Assert.Equal(new[] { "id", "name" }, rows[0].Fields);
        Assert.Equal(new[] { "1", "ann, a" }, rows[1].Fields);
        Assert.Equal(2, rows[2].Index);
        Assert.Equal(new[] { "2", "bob" }, rows[2].Fields);
    }

    [Fact]
    public async Task Reads_100k_rows_and_sums_a_column()
    {
        var builder = new StringBuilder(100_000 * 16);
        var expected = 0.0;
        for (var i = 0; i < 100_000; i++)
        {
            var value = i * 0.5;
            expected += value;
            builder.Append(i.ToString(CultureInfo.InvariantCulture)).Append(',')
                .Append(value.ToString(CultureInfo.InvariantCulture)).Append('\n');
        }

        var count = 0;
        var sum = 0.0;
        await using var source = new MemoryStream(Encoding.UTF8.GetBytes(builder.ToString()));
        await CsvLineReader.ForEachLineAsync(source, line =>
        {
            count++;
            var fieldIndex = 0;
            foreach (var field in SpanCsvParser.Enumerate(line.Span))
            {
                if (fieldIndex == 1 && field.TryGetDouble(out var v))
                {
                    sum += v;
                }

                fieldIndex++;
            }

            return default;
        });

        Assert.Equal(100_000, count);
        Assert.Equal(expected, sum, 3);
    }

    [Fact]
    public async Task Numeric_scan_of_20k_rows_allocates_far_less_than_the_file()
    {
        var builder = new StringBuilder();
        for (var i = 0; i < 20_000; i++)
        {
            builder.Append(i).Append(",1.5,some-text-column\n");
        }

        var bytes = Encoding.UTF8.GetBytes(builder.ToString());

        // Warm the ArrayPool buckets this size class needs.
        await CsvLineReader.ForEachLineAsync(new MemoryStream(bytes), _ => default);

        // GC.GetTotalAllocatedBytes(true) counts every thread's allocations, so
        // a live test host's background work can pollute a single attempt.
        // A genuine regression fails every attempt — keep the quietest of three.
        long allocated = long.MaxValue;
        for (var attempt = 0; attempt < 3 && allocated >= bytes.Length / 4; attempt++)
        {
            var before = GC.GetTotalAllocatedBytes(true);
            await CsvLineReader.ForEachLineAsync(new MemoryStream(bytes), _ => default);
            allocated = Math.Min(allocated, GC.GetTotalAllocatedBytes(true) - before);
        }

        Assert.True(
            allocated < bytes.Length / 4,
            $"raw line scan allocated {allocated} B for a {bytes.Length} B file — expected < {bytes.Length / 4} B");
    }

    [Fact]
    public async Task Line_longer_than_4mb_limit_throws_format_exception()
    {
        var huge = new byte[5 * 1024 * 1024];
        Array.Fill(huge, (byte)'x');

        await Assert.ThrowsAsync<FormatException>(
            () => CsvLineReader.ForEachLineAsync(new MemoryStream(huge), _ => default, bufferSize: 65536));
    }

    [Fact]
    public async Task Source_stream_is_disposed_after_enumeration()
    {
        var source = new TrackingStream(Encoding.UTF8.GetBytes("a\nb\n"));
        await ReadAllAsync(source);
        Assert.True(source.WasDisposed);
    }

    [Fact]
    public async Task Tiny_buffer_size_is_rejected()
    {
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => CsvLineReader.ForEachLineAsync(Stream("a\n"), _ => default, bufferSize: 8));
    }

    private sealed class TrackingStream : MemoryStream
    {
        public TrackingStream(byte[] buffer)
            : base(buffer)
        {
        }

        public bool WasDisposed { get; private set; }

        protected override void Dispose(bool disposing)
        {
            WasDisposed = true;
            base.Dispose(disposing);
        }
    }
}
