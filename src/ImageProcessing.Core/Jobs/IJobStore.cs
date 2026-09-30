namespace ImageProcessing.Core.Jobs;

/// <summary>
/// Job state store. Implementations must be safe for concurrent use: the API
/// adds/reads while the worker updates.
/// </summary>
public interface IJobStore
{
    /// <summary>Returns a consistent snapshot (see <see cref="JobRecord.Snapshot"/>), or null when unknown.</summary>
    JobRecord? Get(Guid id);

    void Add(JobRecord job);

    /// <summary>Applies <paramref name="mutate"/> under a per-record lock. False when unknown.</summary>
    bool Update(Guid id, Action<JobRecord> mutate);

    /// <summary>Removes the record (used by TTL cleanup).</summary>
    bool TryRemove(Guid id);
}
