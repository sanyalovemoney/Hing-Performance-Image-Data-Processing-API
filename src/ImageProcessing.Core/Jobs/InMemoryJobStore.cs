using System.Collections.Concurrent;

namespace ImageProcessing.Core.Jobs;

/// <summary>
/// Single-node store. Per-record monitor + snapshot-on-read keeps API readers
/// and the worker writer consistent without a global lock.
/// Swap for Redis/Postgres behind the same interface when scaling out.
/// </summary>
public sealed class InMemoryJobStore : IJobStore
{
    private readonly ConcurrentDictionary<Guid, JobRecord> _jobs = new();

    public JobRecord? Get(Guid id)
    {
        if (!_jobs.TryGetValue(id, out var job))
        {
            return null;
        }

        lock (job)
        {
            return job.Snapshot();
        }
    }

    public void Add(JobRecord job) => _jobs[job.Id] = job;

    public bool Update(Guid id, Action<JobRecord> mutate)
    {
        if (!_jobs.TryGetValue(id, out var job))
        {
            return false;
        }

        lock (job)
        {
            mutate(job);
        }

        return true;
    }

    public bool TryRemove(Guid id) => _jobs.TryRemove(id, out _);

    public IReadOnlyList<JobRecord> List()
    {
        var all = new List<JobRecord>(_jobs.Count);
        foreach (var job in _jobs.Values)
        {
            lock (job)
            {
                all.Add(job.Snapshot());
            }
        }

        return all;
    }
}
