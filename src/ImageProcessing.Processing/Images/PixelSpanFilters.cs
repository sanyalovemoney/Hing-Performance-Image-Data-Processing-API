using System.Buffers;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace ImageProcessing.Processing.Images;

/// <summary>
/// In-place pixel filters operating over <see cref="Span{T}"/> rows exposed by
/// ImageSharp's pixel accessor: no per-row arrays, no intermediate image copies,
/// scratch buffers come from <see cref="ArrayPool{T}"/>.
/// </summary>
public static class PixelSpanFilters
{
    private const float LumaR = 0.299f;
    private const float LumaG = 0.587f;
    private const float LumaB = 0.114f;

    /// <summary>ITU-R BT.601 luma, alpha preserved.</summary>
    public static void GrayscaleInPlace(Image<Bgra32> image) =>
        image.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < accessor.Height; y++)
            {
                var row = accessor.GetRowSpan(y);
                for (var x = 0; x < row.Length; x++)
                {
                    ref var p = ref row[x];
                    var g = (byte)((LumaR * p.R) + (LumaG * p.G) + (LumaB * p.B));
                    p = new Bgra32(g, g, g, p.A);
                }
            }
        });

    /// <summary>Binarizes luma around <paramref name="level"/>, alpha preserved.</summary>
    public static void ThresholdInPlace(Image<Bgra32> image, byte level) =>
        image.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < accessor.Height; y++)
            {
                var row = accessor.GetRowSpan(y);
                for (var x = 0; x < row.Length; x++)
                {
                    ref var p = ref row[x];
                    var luma = (byte)((LumaR * p.R) + (LumaG * p.G) + (LumaB * p.B));
                    var v = luma >= level ? (byte)255 : (byte)0;
                    p = new Bgra32(v, v, v, p.A);
                }
            }
        });

    /// <summary>
    /// 3x3 box blur with clamped edges. Slides a window over three rented row
    /// buffers regardless of image height — O(width) extra memory, zero GC pressure
    /// after the pool is warm.
    /// </summary>
    public static void BoxBlur3x3InPlace(Image<Bgra32> image) =>
        image.ProcessPixelRows(accessor =>
        {
            var width = accessor.Width;
            var height = accessor.Height;

            var scratch = ArrayPool<Bgra32>.Shared.Rent(width * 3);
            try
            {
                var prev = scratch.AsSpan(0, width);
                var cur = scratch.AsSpan(width, width);
                var next = scratch.AsSpan(width * 2, width);

                accessor.GetRowSpan(0).CopyTo(prev);
                accessor.GetRowSpan(Math.Min(1, height - 1)).CopyTo(cur);

                for (var y = 0; y < height; y++)
                {
                    accessor.GetRowSpan(Math.Min(y + 1, height - 1)).CopyTo(next);
                    var row = accessor.GetRowSpan(y);

                    for (var x = 0; x < width; x++)
                    {
                        var x0 = Math.Max(0, x - 1);
                        var x2 = Math.Min(width - 1, x + 1);

                        int r = 0, g = 0, b = 0, a = 0, n = 0;
                        Accumulate(prev, x0, x2, ref r, ref g, ref b, ref a, ref n);
                        Accumulate(cur, x0, x2, ref r, ref g, ref b, ref a, ref n);
                        Accumulate(next, x0, x2, ref r, ref g, ref b, ref a, ref n);

                        row[x] = new Bgra32((byte)(r / n), (byte)(g / n), (byte)(b / n), (byte)(a / n));
                    }

                    // Rotate the window: oldest row buffer becomes the next scratch slot.
                    var tmp = prev;
                    prev = cur;
                    cur = next;
                    next = tmp;
                }
            }
            finally
            {
                ArrayPool<Bgra32>.Shared.Return(scratch);
            }
        });

    private static void Accumulate(
        ReadOnlySpan<Bgra32> row, int x0, int x2,
        ref int r, ref int g, ref int b, ref int a, ref int n)
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
