namespace ImageProcessing.Core.Jobs;

/// <summary>Lifecycle states of a processing job.</summary>
public enum JobStatus
{
    Queued = 0,
    Running = 1,
    Succeeded = 2,
    Failed = 3,
    Canceled = 4,
}
