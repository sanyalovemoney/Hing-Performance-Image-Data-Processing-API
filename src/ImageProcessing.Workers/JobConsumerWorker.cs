using ImageProcessing.Core.Jobs;
using ImageProcessing.Core.Processing;
using ImageProcessing.Core.Queue;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ImageProcessing.Workers;

/// <summary>
/// Consumes the job queue for the lifetime of the host.
/// Phase 1 processes leases sequentially; bounded concurrency via SemaphoreSlim
/// and graceful drain land in the Limits phase.
/// </summary>
public sealed class JobConsumerWorker : BackgroundService
{
    private readonly IJobQueue _queue;
    private readonly IJobStore _store;
    private readonly ProcessorRegistry _registry;
    private readonly ILogger<JobConsumerWorker> _log;

    public JobConsumerWorker(
        IJobQueue queue,
        IJobStore store,
        ProcessorRegistry registry,
        ILogger<JobConsumerWorker> log)
    {
        _queue = queue;
        _store = store;
        _registry = registry;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _log.LogInformation("job consumer started (queue capacity {Capacity})", _queue.Capacity);
        try
        {
            await foreach (var lease in _queue.ReadAllAsync(stoppingToken).ConfigureAwait(false))
            {
                await ExecuteLeaseAsync(lease, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Host shutdown: ReadAllAsync canceled — nothing left to do.
        }

        _log.LogInformation("job consumer stopped");
    }

    private async Task ExecuteLeaseAsync(JobLease lease, CancellationToken stoppingToken)
    {
        var snapshot = _store.Get(lease.JobId);
        if (snapshot is null || snapshot.Status != JobStatus.Queued)
        {
            _log.LogWarning("skipping lease {JobId}: not queued (status {Status})",
                lease.JobId, snapshot?.Status.ToString() ?? "unknown");
            return;
        }

        var processor = _registry.Resolve(lease.Type);
        if (processor is null)
        {
            Fail(lease.JobId, $"no processor registered for type '{lease.Type}'");
            return;
        }

        _store.Update(lease.JobId, j =>
        {
            j.Status = JobStatus.Running;
            j.StartedAt = DateTimeOffset.UtcNow;
        });

        // Processors work on a private copy; only terminal state is written back.
        var working = snapshot with { Status = JobStatus.Running };
        try
        {
            await processor.ProcessAsync(working, stoppingToken).ConfigureAwait(false);
            _store.Update(lease.JobId, j =>
            {
                j.Status = JobStatus.Succeeded;
                j.ResultPath = working.ResultPath;
                j.ResultContentType = working.ResultContentType;
                j.AllocatedBytes = working.AllocatedBytes;
                j.FinishedAt = DateTimeOffset.UtcNow;
            });
            _log.LogInformation("job {JobId} ({Type}) succeeded", lease.JobId, lease.Type);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            _store.Update(lease.JobId, j =>
            {
                j.Status = JobStatus.Canceled;
                j.Error = "canceled during shutdown";
                j.FinishedAt = DateTimeOffset.UtcNow;
            });
            _log.LogInformation("job {JobId} canceled", lease.JobId);
        }
        catch (Exception ex)
        {
            Fail(lease.JobId, ex.Message);
            _log.LogError(ex, "job {JobId} ({Type}) failed", lease.JobId, lease.Type);
        }
    }

    private void Fail(Guid jobId, string error) =>
        _store.Update(jobId, j =>
        {
            j.Status = JobStatus.Failed;
            j.Error = error;
            j.FinishedAt = DateTimeOffset.UtcNow;
        });
}
