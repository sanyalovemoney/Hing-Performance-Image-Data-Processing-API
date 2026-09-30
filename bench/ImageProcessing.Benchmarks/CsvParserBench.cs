using System.Globalization;
using System.Text;
using BenchmarkDotNet.Attributes;
using ImageProcessing.Processing.Csv;

namespace ImageProcessing.Benchmarks;

/// <summary>
/// Scanning a 50k-row CSV column: the classic GetString + string.Split path
/// (one giant string, one array per line, one array per line's fields) vs the
/// ref-struct span parser walking the raw UTF-8 bytes with zero allocations.
/// </summary>
[MemoryDiagnoser]
public class CsvParserBench
{
    private const int Rows = 50_000;

    private byte[] _utf8 = null!;

    [GlobalSetup]
    public void Setup()
    {
        var builder = new StringBuilder(Rows * 32);
        builder.Append("id,name,value\n");
        for (var i = 0; i < Rows; i++)
        {
            builder.Append(i.ToString(CultureInfo.InvariantCulture))
                .Append(",row-").Append(i.ToString(CultureInfo.InvariantCulture))
                .Append(',')
                .Append((i * 1.5).ToString("R", CultureInfo.InvariantCulture))
                .Append('\n');
        }

        _utf8 = Encoding.UTF8.GetBytes(builder.ToString());
    }

    [Benchmark(Baseline = true)]
    public double Scan_string_split()
    {
        var text = Encoding.UTF8.GetString(_utf8);
        var sum = 0.0;
        foreach (var line in text.Split('\n'))
        {
            if (line.Length == 0)
            {
                continue;
            }

            var fields = line.Split(',');
            if (fields.Length < 3)
            {
                continue;
            }

            if (double.TryParse(fields[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
            {
                sum += v;
            }
        }

        return sum;
    }

    [Benchmark]
    public double Scan_span_ref_struct()
    {
        var sum = 0.0;
        var rest = (ReadOnlySpan<byte>)_utf8;
        while (!rest.IsEmpty)
        {
            var newline = rest.IndexOf((byte)'\n');
            var line = newline < 0 ? rest : rest[..newline];
            rest = newline < 0 ? default : rest[(newline + 1)..];
            if (line.IsEmpty)
            {
                continue;
            }

            var index = 0;
            foreach (var field in SpanCsvParser.Enumerate(line))
            {
                if (index == 2 && field.TryGetDouble(out var v))
                {
                    sum += v;
                }

                index++;
            }
        }

        return sum;
    }
}
