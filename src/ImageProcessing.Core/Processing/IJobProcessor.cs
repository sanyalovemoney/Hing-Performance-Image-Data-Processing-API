using ImageProcessing.Core.Jobs;

namespace ImageProcessing.Core.Processing;

/// <summary>
/// Heavy lifting for one job type. Receives a private working copy of the
/// record; sets ResultPath/ResultContentType/AllocatedBytes and throws on
/// failure — the worker translates outcomes into store state.
/// </summary>
public interface IJobProcessor
{
    /// <summary>Job type key this processor serves ("image", "csv", ...).</summary>
    string Type { get; }

    Task ProcessAsync(JobRecord job, CancellationToken ct);
}
