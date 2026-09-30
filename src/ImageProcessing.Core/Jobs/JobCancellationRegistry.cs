using System.Collections.Concurrent;

namespace ImageProcessing.Core.Jobs;

/// <summary>
/// Maps running job ids to cooperative cancellation sources. The DELETE
/// endpoint signals cancellation here; the consumer links the token into the
/// job's execution CTS so processors observe it on their next await.
/// </summary>
public sealed class JobCancellationRegistry
{
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _running = new();

    /// <summary>Called by the consumer when a job starts running.</summary>
    public CancellationTokenSource Register(Guid jobId)
    {
        var cts = new CancellationTokenSource();
        _running[jobId] = cts;
        return cts;
    }

    /// <summary>
    /// Signals cancellation. False when the job is not (or no longer) running —
    /// it may have just finished, which is not an error.
    /// </summary>
    public bool TryCancel(Guid jobId)
    {
        if (!_running.TryGetValue(jobId, out var cts))
        {
            return false;
        }

        try
        {
            cts.Cancel();
            return true;
        }
        catch (ObjectDisposedException)
        {
            // Unregister raced us — the job finished on its own.
            return false;
        }
    }

    /// <summary>Called by the consumer when the job reaches a terminal state.</summary>
    public void Unregister(Guid jobId)
    {
        if (_running.TryRemove(jobId, out var cts))
        {
            cts.Dispose();
        }
    }
}
