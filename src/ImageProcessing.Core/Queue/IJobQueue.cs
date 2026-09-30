namespace ImageProcessing.Core.Queue;

/// <summary>
/// Bounded job queue. TryEnqueue never blocks — a full queue surfaces as
/// false so the API can answer 503 instead of piling work into memory.
/// </summary>
public interface IJobQueue
{
    int Capacity { get; }

    /// <summary>Approximate backlog, or -1 when the implementation cannot count.</summary>
    int Count { get; }

    bool TryEnqueue(JobLease lease);

    /// <summary>Streams leases as they arrive; completes after <see cref="Complete"/> + drain.</summary>
    IAsyncEnumerable<JobLease> ReadAllAsync(CancellationToken ct = default);

    /// <summary>Signals no more items will arrive; current readers drain, then finish.</summary>
    void Complete();
}
