using ImageProcessing.Processing.Matrix;

namespace ImageProcessing.Tests.Processing;

public sealed class MatrixOpsTests
{
    private static float[] RandomValues(int count, int seed)
    {
        var random = new Random(seed);
        var values = new float[count];
        for (var i = 0; i < count; i++)
        {
            values[i] = (float)(random.NextDouble() * 200.0 - 100.0);
        }

        return values;
    }

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(1000)]
    public void Scale_matches_naive_multiplication(int length)
    {
        var data = RandomValues(length, seed: 1);
        var expected = data.Select(v => v * 2.5f).ToArray();

        MatrixOps.ScaleInPlace(data, 2.5f);

        Assert.Equal(expected, data); // identical IEEE ops → exact equality
    }

    [Fact]
    public void Normalize_maps_min_to_zero_max_to_one()
    {
        // Length covers both SIMD body and scalar tail for any Vector width.
        var data = new float[] { 5f, -3f, 1f, 9f, 0f, -3.5f, 7f, 2f, 4f, 8f, 6f };
        MatrixOps.NormalizeInPlace(data);

        Assert.All(data, v => Assert.InRange(v, 0f, 1f));
        Assert.Equal(0f, data[5]);  // -3.5 was the minimum
        Assert.Equal(1f, data[3]);  // 9 was the maximum
        // exact per-element expectations: Normalize_follows_minmax_formula
    }

    [Fact]
    public void Normalize_follows_minmax_formula()
    {
        var data = RandomValues(997, seed: 2);
        var min = data.Min();
        var max = data.Max();
        var expected = data.Select(v => (v - min) / (max - min)).ToArray();

        MatrixOps.NormalizeInPlace(data);

        for (var i = 0; i < data.Length; i++)
        {
            // SIMD multiplies by the reciprocal instead of dividing — allow an ulp of slack.
            Assert.True(
                Math.Abs(expected[i] - data[i]) < 1e-4f,
                $"index {i}: expected {expected[i]}, actual {data[i]}");
        }
    }

    [Fact]
    public void Normalize_of_constant_matrix_is_all_zeros()
    {
        var data = new float[50];
        Array.Fill(data, 3.14f);

        MatrixOps.NormalizeInPlace(data);

        Assert.All(data, v => Assert.Equal(0f, v));
    }

    [Fact]
    public void Transpose_matches_naive_reference()
    {
        const int rows = 17, cols = 23;
        var src = RandomValues(rows * cols, seed: 3);
        var dst = new float[rows * cols];

        MatrixOps.Transpose(src, rows, cols, dst);

        for (var i = 0; i < rows; i++)
        {
            for (var j = 0; j < cols; j++)
            {
                Assert.Equal(src[i * cols + j], dst[j * rows + i]);
            }
        }
    }

    [Theory]
    [InlineData(5, 7)]   // all-scalar tail (cols < typical vector width)
    [InlineData(4, 20)]  // vector body + tail
    [InlineData(1, 9)]   // single row
    public void ColumnMeans_matches_naive_reference(int rows, int cols)
    {
        var src = RandomValues(rows * cols, seed: 4);
        var dst = new float[cols];

        MatrixOps.ColumnMeans(src, rows, cols, dst);

        for (var j = 0; j < cols; j++)
        {
            var expected = 0f;
            for (var i = 0; i < rows; i++)
            {
                expected += src[i * cols + j];
            }

            Assert.Equal(expected / rows, dst[j], 4);
        }
    }

    [Fact]
    public void ColumnMeans_rejects_small_buffers()
    {
        var src = new float[12];
        var dst = new float[2]; // need 4
        Assert.Throws<ArgumentException>(() => MatrixOps.ColumnMeans(src, 3, 4, dst));
    }

    [Fact]
    public void Transpose_rejects_small_buffers()
    {
        var src = new float[5];
        var dst = new float[6];
        Assert.Throws<ArgumentException>(() => MatrixOps.Transpose(src, 2, 3, dst));
    }
}
