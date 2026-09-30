using ImageProcessing.Core.Configuration;
using ImageProcessing.Core.Jobs;
using ImageProcessing.Core.Storage;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ImageProcessing.Workers;

/// <summary>
/// Periodic TTL sweep: deletes files and records of jobs older than
/// <see cref="ProcessingOptions.Retention"/> — terminal jobs by FinishedAt,
/// stuck non-terminal jobs (e.g. leftovers from a crash) by CreatedAt — then
/// sweeps orphaned storage files whose last-write time passed the same cutoff.
/// </summary>
public sealed class TempCleanupWorker : BackgroundService
{
    private readonly IJobStore _store;
    private readonly IFileStorage _storage;
    private readonly ProcessingOptions _options;
    private readonly ILogger<TempCleanupWorker> _log;

    public TempCleanupWorker(
        IJobStore store,
        IFileStorage storage,
        IOptions<ProcessingOptions> options,
        ILogger<TempCleanupWorker> logger)
    {
        _store = store;
        _storage = storage;
        _options = options.Value;
        _log = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _log.LogInformation(
            "cleanup worker started (retention {Retention}, every {Interval})",
            _options.Retention, _options.CleanupInterval);

        try
        {
            // Sweep once at boot: picks up leftovers from a previous crashed run.
            Sweep();

            using var timer = new PeriodicTimer(_options.CleanupInterval);
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                Sweep();
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal host shutdown.
        }
        catch (Exception ex)
        {
            // A broken sweep must not take the host down; the next tick retries.
            _log.LogError(ex, "cleanup sweep failed");
        }

        _log.LogInformation("cleanup worker stopped");
    }

    /// <summary>
    /// Runs one sweep and returns the number of items removed. Public so tests
    /// can drive it without the timer.
    /// </summary>
    public int Sweep()
    {
        var cutoff = DateTimeOffset.UtcNow - _options.Retention;
        var removed = 0;

        foreach (var job in _store.List())
        {
            var expired = job.IsTerminal
                ? job.FinishedAt < cutoff
                : job.CreatedAt < cutoff;
            if (!expired)
            {
                continue;
            }

            if (job.UploadPath is not null)
            {
                _storage.Delete(job.UploadPath);
            }

            if (job.ResultPath is not null)
            {
                _storage.Delete(job.ResultPath);
            }

            if (_store.TryRemove(job.Id))
            {
                removed++;
            }
        }

        // Orphans: files whose job record is gone (or was never written).
        foreach (var entry in _storage.List())
        {
            if (entry.ModifiedUtc < cutoff)
            {
                _storage.Delete(entry.RelativePath);
                removed++;
            }
        }

        if (removed > 0)
        {
            _log.LogInformation("cleanup removed {Count} expired item(s)", removed);
        }

        return removed;
    }
}
