namespace ImageProcessing.Core.Queue;

/// <summary>
/// Ticket handed to workers. Carries only what is needed to fetch state —
/// the job itself lives in <see cref="Jobs.IJobStore"/>.
/// </summary>
public readonly record struct JobLease(Guid JobId, string Type);
