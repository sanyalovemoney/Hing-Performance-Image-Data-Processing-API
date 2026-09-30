using BenchmarkDotNet.Attributes;
using ImageProcessing.Processing.Images;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace ImageProcessing.Benchmarks;

/// <summary>
/// Naive allocation-heavy pixel loops vs the Span/ArrayPool filters on a
/// 512×512 Bgra32 image. Both styles mutate the same image in place
/// (grayscale is idempotent; blur re-blurs, which does not change cost).
/// </summary>
[MemoryDiagnoser]
public class ImageFilterBench
{
    private const int Width = 512;
    private const int Height = 512;

    private Image<Bgra32> _image = null!;

    [GlobalSetup]
    public void Setup()
    {
        _image = new Image<Bgra32>(Width, Height);
        var random = new Random(42);
        _image.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < Height; y++)
            {
                var row = accessor.GetRowSpan(y);
                for (var x = 0; x < Width; x++)
                {
                    row[x] = new Bgra32(
                        (byte)random.Next(256), (byte)random.Next(256), (byte)random.Next(256), 255);
                }
            }
        });
    }

    [GlobalCleanup]
    public void Cleanup() => _image.Dispose();

    [Benchmark(Baseline = true)]
    public void Grayscale_naive_row_arrays()
    {
        _image.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < accessor.Height; y++)
            {
                var src = accessor.GetRowSpan(y).ToArray(); // allocation per row...
                var dst = new Bgra32[src.Length];           // ...and another per row
                for (var x = 0; x < src.Length; x++)
                {
                    var p = src[x];
                    var g = (byte)((0.299f * p.R) + (0.587f * p.G) + (0.114f * p.B));
                    dst[x] = new Bgra32(g, g, g, p.A);
                }

                dst.CopyTo(accessor.GetRowSpan(y));
            }
        });
    }

    [Benchmark]
    public void Grayscale_span_in_place() => PixelSpanFilters.GrayscaleInPlace(_image);

    [Benchmark]
    public void BoxBlur_naive_row_arrays()
    {
        _image.ProcessPixelRows(accessor =>
        {
            var width = accessor.Width;
            var height = accessor.Height;

            // Naive: snapshot the whole image, then allocate three row copies
            // plus a destination row on every line.
            var full = new Bgra32[width * height];
            for (var y = 0; y < height; y++)
            {
                accessor.GetRowSpan(y).CopyTo(full.AsSpan(y * width, width));
            }

            for (var y = 0; y < height; y++)
            {
                var prev = full.AsSpan(Math.Max(0, y - 1) * width, width).ToArray();
                var cur = full.AsSpan(y * width, width).ToArray();
                var next = full.AsSpan(Math.Min(height - 1, y + 1) * width, width).ToArray();
                var dst = new Bgra32[width];

                for (var x = 0; x < width; x++)
                {
                    var x0 = Math.Max(0, x - 1);
                    var x2 = Math.Min(width - 1, x + 1);
                    int r = 0, g = 0, b = 0, a = 0, n = 0;
                    Accumulate(prev, x0, x2, ref r, ref g, ref b, ref a, ref n);
                    Accumulate(cur, x0, x2, ref r, ref g, ref b, ref a, ref n);
                    Accumulate(next, x0, x2, ref r, ref g, ref b, ref a, ref n);
                    dst[x] = new Bgra32((byte)(r / n), (byte)(g / n), (byte)(b / n), (byte)(a / n));
                }

                dst.CopyTo(accessor.GetRowSpan(y));
            }
        });
    }

    [Benchmark]
    public void BoxBlur_span_pooled_window() => PixelSpanFilters.BoxBlur3x3InPlace(_image);

    private static void Accumulate(
        Bgra32[] row, int x0, int x2, ref int r, ref int g, ref int b, ref int a, ref int n)
    {
        for (var x = x0; x <= x2; x++)
        {
            ref readonly var p = ref row[x];
            r += p.R;
            g += p.G;
            b += p.B;
            a += p.A;
            n++;
        }
    }
}
