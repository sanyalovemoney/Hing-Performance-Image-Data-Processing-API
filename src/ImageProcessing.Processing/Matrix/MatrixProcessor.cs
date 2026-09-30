using System.Buffers;
using ImageProcessing.Core.Jobs;
using ImageProcessing.Core.Processing;
using ImageProcessing.Core.Storage;

namespace ImageProcessing.Processing.Matrix;

/// <summary>
/// Matrix job pipeline: streams the binary file into a pooled float buffer,
/// applies a <see cref="MatrixOps"/> transform (SIMD where the layout allows),
/// and writes the result back in the same binary format. Served as
/// application/octet-stream.
/// </summary>
public sealed class MatrixProcessor : IJobProcessor
{
    private readonly IFileStorage _storage;

    public MatrixProcessor(IFileStorage storage) => _storage = storage;

    public string Type => "matrix";

    public async Task ProcessAsync(JobRecord job, CancellationToken ct)
    {
        if (job.UploadPath is null || !_storage.Exists(job.UploadPath))
        {
            throw new InvalidOperationException($"upload '{job.UploadPath}' is missing");
        }

        var parameters = MatrixParamsParser.Parse(job.Params);
        var resultPath = _storage.CreateResultPath(job.Id, ".bin");
        var before = GC.GetTotalAllocatedBytes(true);

        await using var input = _storage.OpenRead(job.UploadPath);
        var (rows, cols) = await MatrixFileFormat.ReadHeaderAsync(input, ct).ConfigureAwait(false);
        var count = rows * cols;

        var data = ArrayPool<float>.Shared.Rent(count);
        float[]? scratch = null;
        try
        {
            await MatrixFileFormat.ReadDataAsync(input, data, count, ct).ConfigureAwait(false);

            await using var output = _storage.OpenWrite(resultPath);
            switch (parameters.Op)
            {
                case MatrixOp.Scale:
                    MatrixOps.ScaleInPlace(data.AsSpan(0, count), parameters.Scale);
                    await MatrixFileFormat.WriteAsync(output, rows, cols, data, ct).ConfigureAwait(false);
                    break;

                case MatrixOp.Normalize:
                    MatrixOps.NormalizeInPlace(data.AsSpan(0, count));
                    await MatrixFileFormat.WriteAsync(output, rows, cols, data, ct).ConfigureAwait(false);
                    break;

                case MatrixOp.Transpose:
                    scratch = ArrayPool<float>.Shared.Rent(count);
                    MatrixOps.Transpose(data.AsSpan(0, count), rows, cols, scratch.AsSpan(0, count));
                    await MatrixFileFormat.WriteAsync(output, cols, rows, scratch, ct).ConfigureAwait(false);
                    break;

                case MatrixOp.ColMean:
                    scratch = ArrayPool<float>.Shared.Rent(cols);
                    MatrixOps.ColumnMeans(data.AsSpan(0, count), rows, cols, scratch.AsSpan(0, cols));
                    await MatrixFileFormat.WriteAsync(output, 1, cols, scratch, ct).ConfigureAwait(false);
                    break;

                default:
                    throw new FormatException($"unsupported op '{parameters.Op}'");
            }
        }
        finally
        {
            ArrayPool<float>.Shared.Return(data);
            if (scratch is not null)
            {
                ArrayPool<float>.Shared.Return(scratch);
            }
        }

        job.ResultPath = resultPath;
        job.ResultContentType = "application/octet-stream";
        job.AllocatedBytes = (long)(GC.GetTotalAllocatedBytes(true) - before);
    }
}
