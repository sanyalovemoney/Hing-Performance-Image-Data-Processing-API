namespace ImageProcessing.Core.Jobs;

/// <summary>
/// Mutable job state. Writers mutate a record only while holding the record's
/// monitor inside <see cref="IJobStore.Update"/>; readers get an immutable
/// snapshot via <see cref="IJobStore.Get"/> / <see cref="Clone"/>.
/// </summary>
public sealed record JobRecord
{
    public required Guid Id { get; init; }
    public required string Type { get; init; }

    /// <summary>Raw processor parameters (JSON), as supplied by the client.</summary>
    public string? Params { get; set; }

    public JobStatus Status { get; set; } = JobStatus.Queued;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
    public string? Error { get; set; }

    /// <summary>Storage-relative path of the uploaded file ("uploads/...").</summary>
    public string? UploadPath { get; set; }

    /// <summary>Storage-relative path of the processing result ("results/...").</summary>
    public string? ResultPath { get; set; }

    /// <summary>MIME type the result should be served with.</summary>
    public string? ResultContentType { get; set; }

    /// <summary>Upload size in bytes, counted while streaming.</summary>
    public long UploadSize { get; set; }

    /// <summary>Bytes allocated by the GC while the job was processed (measured).</summary>
    public long AllocatedBytes { get; set; }

    public bool IsTerminal => Status is JobStatus.Succeeded or JobStatus.Failed or JobStatus.Canceled;

    /// <summary>Shallow copy used to hand out consistent snapshots.</summary>
    public JobRecord Snapshot() => this with { };
}
