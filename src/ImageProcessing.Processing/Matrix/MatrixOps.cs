using System.Numerics;

namespace ImageProcessing.Processing.Matrix;

/// <summary>
/// Matrix transforms over flat row-major float spans. Bulk paths run through
/// <see cref="Vector{T}"/> of float (hardware SIMD); scalar tails handle the
/// remainder. All methods operate on caller-rented buffers.
/// </summary>
public static class MatrixOps
{
    private static readonly int Width = Vector<float>.Count;

    /// <summary>data[i] *= factor — SIMD with scalar tail.</summary>
    public static void ScaleInPlace(Span<float> data, float factor)
    {
        var vFactor = new Vector<float>(factor);
        var i = 0;
        for (; i <= data.Length - Width; i += Width)
        {
            var slice = data.Slice(i, Width);
            (new Vector<float>(slice) * vFactor).CopyTo(slice);
        }

        for (; i < data.Length; i++)
        {
            data[i] *= factor;
        }
    }

    /// <summary>
    /// Min-max normalization into [0,1]. A (near-)constant matrix becomes all
    /// zeros rather than dividing by ~zero.
    /// </summary>
    public static void NormalizeInPlace(Span<float> data)
    {
        if (data.Length == 0)
        {
            return;
        }

        var vMin = new Vector<float>(float.MaxValue);
        var vMax = new Vector<float>(float.MinValue);
        var i = 0;
        for (; i <= data.Length - Width; i += Width)
        {
            var v = new Vector<float>(data.Slice(i, Width));
            vMin = Vector.Min(vMin, v);
            vMax = Vector.Max(vMax, v);
        }

        var min = float.MaxValue;
        var max = float.MinValue;
        for (var lane = 0; lane < Width; lane++)
        {
            min = Math.Min(min, vMin[lane]);
            max = Math.Max(max, vMax[lane]);
        }

        for (; i < data.Length; i++)
        {
            min = Math.Min(min, data[i]);
            max = Math.Max(max, data[i]);
        }

        var range = max - min;
        if (range <= float.Epsilon)
        {
            data.Clear();
            return;
        }

        var vMinOffset = new Vector<float>(min);
        var vInvRange = new Vector<float>(1f / range);
        i = 0;
        for (; i <= data.Length - Width; i += Width)
        {
            var slice = data.Slice(i, Width);
            ((new Vector<float>(slice) - vMinOffset) * vInvRange).CopyTo(slice);
        }

        for (; i < data.Length; i++)
        {
            data[i] = (data[i] - min) / range;
        }
    }

    /// <summary>dst (cols x rows) = src (rows x cols) transposed.</summary>
    public static void Transpose(ReadOnlySpan<float> src, int rows, int cols, Span<float> dst)
    {
        if (src.Length < rows * cols || dst.Length < rows * cols)
        {
            throw new ArgumentException("buffer smaller than rows*cols");
        }

        for (var i = 0; i < rows; i++)
        {
            var srcRow = src.Slice(i * cols, cols);
            for (var j = 0; j < cols; j++)
            {
                dst[j * rows + i] = srcRow[j];
            }
        }
    }

    /// <summary>
    /// dst[0..cols) = per-column arithmetic mean. Columns are accumulated in
    /// Vector&lt;float&gt;-wide blocks (contiguous within a row), with a scalar
    /// tail for the remainder.
    /// </summary>
    public static void ColumnMeans(ReadOnlySpan<float> src, int rows, int cols, Span<float> dst)
    {
        if (src.Length < rows * cols || dst.Length < cols)
        {
            throw new ArgumentException("buffer smaller than required");
        }

        var divisor = new Vector<float>(rows);
        var j = 0;
        for (; j <= cols - Width; j += Width)
        {
            var acc = Vector<float>.Zero;
            for (var i = 0; i < rows; i++)
            {
                acc += new Vector<float>(src.Slice(i * cols + j, Width));
            }

            (acc / divisor).CopyTo(dst.Slice(j, Width));
        }

        for (; j < cols; j++)
        {
            var sum = 0f;
            for (var i = 0; i < rows; i++)
            {
                sum += src[i * cols + j];
            }

            dst[j] = sum / rows;
        }
    }
}
