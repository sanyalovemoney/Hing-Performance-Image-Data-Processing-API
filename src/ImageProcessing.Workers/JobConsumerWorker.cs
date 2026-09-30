using ImageProcessing.Core.Configuration;
using ImageProcessing.Core.Jobs;
using ImageProcessing.Core.Processing;
using ImageProcessing.Core.Queue;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ImageProcessing.Workers;

/// <summary>
/// Consumes the job queue for the lifetime of the host:
///  - up to <see cref="ProcessingOptions.MaxConcurrency"/> jobs run at once (SemaphoreSlim gate);
///  - every job gets a <see cref="ProcessingOptions.JobTimeout"/> budget, enforced by its own CTS;
///  - a single IOException gets one retry after a short pause (transient disk hiccups);
///  - on shutdown the queue is completed and in-flight jobs drain within
///    <see cref="ProcessingOptions.ShutdownDrainTimeout"/> instead of being canceled outright.
/// </summary>
public sealed class JobConsumerWorker : BackgroundService
{
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(250);

    private readonly IJobQueue _queue;
    private readonly IJobStore _store;
    private readonly ProcessorRegistry _registry;
    private readonly JobCancellationRegistry _cancellations;
    private readonly ProcessingOptions _options;
    private readonly ILogger<JobConsumerWorker> _log;

    public JobConsumerWorker(
        IJobQueue queue,
        IJobStore store,
        ProcessorRegistry registry,
        JobCancellationRegistry cancellations,
        IOptions<ProcessingOptions> options,
        ILogger<JobConsumerWorker> log)
    {
        _queue = queue;
        _store = store;
        _registry = registry;
        _cancellations = cancellations;
        _options = options.Value;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Completing the writer side makes ReadAllAsync finish after draining,
        // whether shutdown came through StopAsync or the token fired first.
        using var completion = stoppingToken.Register(_queue.Complete);

        // No `using`: an abandoned job (drain timeout) may still touch the gate
        // after ExecuteAsync returns, and Release on a disposed semaphore throws.
        // SemaphoreSlim only needs disposal when its wait handle is used — never here.
        var gate = new SemaphoreSlim(_options.MaxConcurrency);
        var inFlight = new List<Task>();

        _log.LogInformation(
            "job consumer started (capacity {Capacity}, concurrency {Concurrency}, timeout {Timeout})",
            _queue.Capacity, _options.MaxConcurrency, _options.JobTimeout);

        await foreach (var lease in _queue.ReadAllAsync(CancellationToken.None).ConfigureAwait(false))
        {
            await gate.WaitAsync().ConfigureAwait(false);
            lock (inFlight)
            {
                inFlight.RemoveAll(t => t.IsCompleted);
                inFlight.Add(Task.Run(() => RunGatedAsync(gate, lease), CancellationToken.None));
            }
        }

        Task[] pending;
        lock (inFlight)
        {
            pending = [.. inFlight];
        }

        if (pending.Length > 0)
        {
            _log.LogInformation("draining {Count} in-flight job(s)", pending.Length);
            try
            {
                await Task.WhenAll(pending)
                    .WaitAsync(_options.ShutdownDrainTimeout, CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                _log.LogWarning(
                    "shutdown drain timed out after {Timeout}; abandoning {Count} job(s)",
                    _options.ShutdownDrainTimeout, pending.Count(t => !t.IsCompleted));
            }
        }

        _log.LogInformation("job consumer stopped");
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _queue.Complete(); // stop accepting; the reader loop drains what is already queued
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task RunGatedAsync(SemaphoreSlim gate, JobLease lease)
    {
        try
        {
            await ExecuteLeaseAsync(lease).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task ExecuteLeaseAsync(JobLease lease)
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

        // Deliberately NOT linked to the host stopping token: a job that already
        // started should finish (or hit its own timeout) during graceful drain.
        // The linked token combines the job timeout with client cancellation
        // (DELETE /api/v1/jobs/{id}).
        using var timeout = new CancellationTokenSource(_options.JobTimeout);
        var clientCancel = _cancellations.Register(lease.JobId);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            timeout.Token, clientCancel.Token);
        try
        {
            try
            {
                await processor.ProcessAsync(working, linked.Token).ConfigureAwait(false);
            }
            catch (IOException io) when (!linked.IsCancellationRequested)
            {
                // Transient disk hiccup: one retry after a short pause.
                _log.LogWarning(io, "job {JobId} hit an I/O error; retrying once", lease.JobId);
                await Task.Delay(RetryDelay, linked.Token).ConfigureAwait(false);
                await processor.ProcessAsync(working, linked.Token).ConfigureAwait(false);
            }

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
        catch (OperationCanceledException) when (clientCancel.IsCancellationRequested)
        {
            _store.Update(lease.JobId, j =>
            {
                j.Status = JobStatus.Canceled;
                j.Error = "canceled by client";
                j.FinishedAt = DateTimeOffset.UtcNow;
            });
            _log.LogInformation("job {JobId} ({Type}) canceled by client", lease.JobId, lease.Type);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            Fail(lease.JobId, $"job timed out after {_options.JobTimeout}");
            _log.LogWarning("job {JobId} ({Type}) timed out", lease.JobId, lease.Type);
        }
        catch (Exception ex)
        {
            Fail(lease.JobId, ex.Message);
            _log.LogError(ex, "job {JobId} ({Type}) failed", lease.JobId, lease.Type);
        }
        finally
        {
            _cancellations.Unregister(lease.JobId);
            clientCancel.Dispose();
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
