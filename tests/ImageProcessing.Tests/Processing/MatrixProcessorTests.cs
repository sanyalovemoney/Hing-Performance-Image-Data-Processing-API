using System.Buffers.Binary;
using ImageProcessing.Core.Jobs;
using ImageProcessing.Core.Storage;
using ImageProcessing.Processing.Matrix;

namespace ImageProcessing.Tests.Processing;

public sealed class MatrixProcessorTests
{
    private static async Task<(JobRecord Job, TempFileStorage Storage, string Root, byte[] Result)> RunAsync(
        byte[] upload, string? paramsJson)
    {
        var root = Path.Combine(Path.GetTempPath(), "matrix-" + Guid.NewGuid().ToString("N"));
        var storage = new TempFileStorage(root);
        var processor = new MatrixProcessor(storage);
        var job = new JobRecord
        {
            Id = Guid.NewGuid(),
            Type = "matrix",
            Params = paramsJson,
            UploadPath = storage.CreateUploadPath(Guid.NewGuid(), ".bin"),
        };

        await using (var output = storage.OpenWrite(job.UploadPath))
        {
            await output.WriteAsync(upload);
        }

        await processor.ProcessAsync(job, CancellationToken.None);
        Assert.NotNull(job.ResultPath);
        Assert.Equal("application/octet-stream", job.ResultContentType);
        Assert.True(job.AllocatedBytes >= 0);

        await using var input = storage.OpenRead(job.ResultPath!);
        using var ms = new MemoryStream();
        await input.CopyToAsync(ms);
        return (job, storage, root, ms.ToArray());
    }

    private static async Task CleanupAsync(string root)
    {
        await Task.CompletedTask;
        try
        {
            Directory.Delete(root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public async Task Scale_multiplies_every_element()
    {
        var upload = MatrixTestHelpers.BuildBinary(2, 3, new float[] { 1, 2, 3, 4, 5, 6 });

        var (_, _, root, result) = await RunAsync(upload, """{"op":"scale","scale":2.5}""");

        var (rows, cols, data) = MatrixTestHelpers.ParseBinary(result);
        Assert.Equal(2, rows);
        Assert.Equal(3, cols);
        Assert.Equal(new float[] { 2.5f, 5, 7.5f, 10, 12.5f, 15 }, data);
        await CleanupAsync(root);
    }

    [Fact]
    public async Task Transpose_swaps_dimensions()
    {
        var upload = MatrixTestHelpers.BuildBinary(2, 3, new float[] { 1, 2, 3, 4, 5, 6 });

        var (_, _, root, result) = await RunAsync(upload, """{"op":"transpose"}""");

        var (rows, cols, data) = MatrixTestHelpers.ParseBinary(result);
        Assert.Equal(3, rows);
        Assert.Equal(2, cols);
        Assert.Equal(new float[] { 1, 4, 2, 5, 3, 6 }, data);
        await CleanupAsync(root);
    }

    [Fact]
    public async Task Normalize_maps_into_unit_range()
    {
        var upload = MatrixTestHelpers.BuildBinary(1, 3, new float[] { -5, 0, 5 });

        var (_, _, root, result) = await RunAsync(upload, """{"op":"normalize"}""");

        var (_, _, data) = MatrixTestHelpers.ParseBinary(result);
        Assert.Equal(0f, data[0]);
        Assert.Equal(0.5f, data[1], 5);
        Assert.Equal(1f, data[2]);
        await CleanupAsync(root);
    }

    [Fact]
    public async Task ColMean_produces_single_row_of_means()
    {
        var upload = MatrixTestHelpers.BuildBinary(3, 2, new float[] { 1, 10, 2, 20, 3, 30 });

        var (_, _, root, result) = await RunAsync(upload, """{"op":"colmean"}""");

        var (rows, cols, data) = MatrixTestHelpers.ParseBinary(result);
        Assert.Equal(1, rows);
        Assert.Equal(2, cols);
        Assert.Equal(2f, data[0]);
        Assert.Equal(20f, data[1]);
        await CleanupAsync(root);
    }

    [Fact]
    public async Task Defaults_to_transpose_without_params()
    {
        var upload = MatrixTestHelpers.BuildBinary(1, 4, new float[] { 9, 8, 7, 6 });

        var (_, _, root, result) = await RunAsync(upload, null);

        var (rows, cols, data) = MatrixTestHelpers.ParseBinary(result);
        Assert.Equal(4, rows);
        Assert.Equal(1, cols);
        Assert.Equal(new float[] { 9, 8, 7, 6 }, data);
        await CleanupAsync(root);
    }

    [Theory]
    [InlineData(new byte[] { 1, 2, 3, 4, 5 })]                      // shorter than header
    [InlineData(new byte[] { 0, 0, 0, 0, 1, 0, 0, 0 })]            // rows = 0
    [InlineData(new byte[] { 2, 0, 0, 0, 255, 255, 255, 255 })]    // cols = -1
    public async Task Malformed_headers_fail_with_format_exception(byte[] upload)
    {
        await Assert.ThrowsAsync<FormatException>(async () =>
        {
            var (_, _, root, _) = await RunAsync(upload, """{"op":"scale"}""");
            await CleanupAsync(root);
        });
    }

    [Fact]
    public async Task Truncated_data_fails_with_format_exception()
    {
        // header says 2x2 (4 floats = 16 bytes) but only 8 data bytes follow
        var upload = new byte[8 + 8];
        BinaryPrimitives.WriteInt32LittleEndian(upload, 2);
        BinaryPrimitives.WriteInt32LittleEndian(upload.AsSpan(4), 2);

        await Assert.ThrowsAsync<FormatException>(async () =>
        {
            var (_, _, root, _) = await RunAsync(upload, null);
            await CleanupAsync(root);
        });
    }

    [Fact]
    public async Task Trailing_bytes_fail_with_format_exception()
    {
        var upload = MatrixTestHelpers.BuildBinary(1, 2, new float[] { 1, 2 })
            .Concat(new byte[] { 0, 0, 0 })
            .ToArray();

        await Assert.ThrowsAsync<FormatException>(async () =>
        {
            var (_, _, root, _) = await RunAsync(upload, null);
            await CleanupAsync(root);
        });
    }

    [Theory]
    [InlineData("""{"op":"invert"}""")]        // unknown op
    [InlineData("""{"scale":"two"}""")]        // scale not a number
    [InlineData("""{"op":42}""")]              // op not a string
    [InlineData("[1]")]                        // not an object
    [InlineData("{oops")]                      // malformed JSON
    public async Task Invalid_params_throw_format_exception(string paramsJson)
    {
        var upload = MatrixTestHelpers.BuildBinary(1, 1, new float[] { 1 });

        await Assert.ThrowsAsync<FormatException>(async () =>
        {
            var (_, _, root, _) = await RunAsync(upload, paramsJson);
            await CleanupAsync(root);
        });
    }

    [Fact]
    public async Task Missing_upload_fails_the_job()
    {
        var root = Path.Combine(Path.GetTempPath(), "matrix-" + Guid.NewGuid().ToString("N"));
        var storage = new TempFileStorage(root);
        var processor = new MatrixProcessor(storage);
        var job = new JobRecord
        {
            Id = Guid.NewGuid(),
            Type = "matrix",
            UploadPath = "uploads/ghost.bin",
        };

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => processor.ProcessAsync(job, CancellationToken.None));
        await CleanupAsync(root);
    }

    [Fact]
    public async Task Format_roundtrips_large_matrix_through_pool()
    {
        // 300x300 = 90k floats — exercises the 81920-byte write chunking loop.
        var random = new Random(7);
        var values = new float[90_000];
        for (var i = 0; i < values.Length; i++)
        {
            values[i] = (float)random.NextDouble();
        }

        var upload = MatrixTestHelpers.BuildBinary(300, 300, values);
        var (_, _, root, result) = await RunAsync(upload, """{"op":"scale","scale":2}""");

        var (rows, cols, data) = MatrixTestHelpers.ParseBinary(result);
        Assert.Equal(300, rows);
        Assert.Equal(300, cols);
        for (var i = 0; i < values.Length; i++)
        {
            Assert.Equal(values[i] * 2f, data[i]);
        }

        await CleanupAsync(root);
    }
}
