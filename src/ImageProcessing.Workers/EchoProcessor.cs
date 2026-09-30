using ImageProcessing.Core.Jobs;
using ImageProcessing.Core.Processing;
using ImageProcessing.Core.Storage;
using ImageProcessing.Processing.Buffers;

namespace ImageProcessing.Workers;

/// <summary>
/// Placeholder processor: copies the upload to the result unchanged.
/// Exercises the full pipeline (queue → worker → storage) before real
/// image/csv/matrix processors land.
/// </summary>
public sealed class EchoProcessor : IJobProcessor
{
    private readonly IFileStorage _storage;

    public EchoProcessor(IFileStorage storage) => _storage = storage;

    public string Type => "echo";

    public async Task ProcessAsync(JobRecord job, CancellationToken ct)
    {
        if (job.UploadPath is null || !_storage.Exists(job.UploadPath))
        {
            throw new InvalidOperationException($"upload '{job.UploadPath}' is missing");
        }

        var extension = Path.GetExtension(job.UploadPath);
        var resultPath = _storage.CreateResultPath(job.Id, extension);

        var before = GC.GetTotalAllocatedBytes(true);
        await using (var input = _storage.OpenRead(job.UploadPath))
        await using (var output = _storage.OpenWrite(resultPath))
        {
            await input.CopyToPooledAsync(output, ct).ConfigureAwait(false);
        }

        job.ResultPath = resultPath;
        job.ResultContentType ??= "application/octet-stream";
        job.AllocatedBytes = (long)(GC.GetTotalAllocatedBytes(true) - before);
    }
}
