namespace ImageProcessing.Core.Configuration;

/// <summary>
/// Limits and reliability knobs, bound from the "Processing" configuration
/// section and validated at startup (see <see cref="ProcessingOptionsValidator"/>).
/// </summary>
public sealed class ProcessingOptions
{
    public const string SectionName = "Processing";

    /// <summary>Hard cap for a single upload, in bytes. Larger requests get 413.</summary>
    public long MaxUploadBytes { get; set; } = 50 * 1024 * 1024;

    /// <summary>Bounded queue capacity. When full, new jobs get 503 (backpressure).</summary>
    public int QueueCapacity { get; set; } = 128;

    /// <summary>Maximum number of jobs processed concurrently.</summary>
    public int MaxConcurrency { get; set; } = 2;

    /// <summary>Wall-clock budget for a single job; exceeding it fails the job.</summary>
    public TimeSpan JobTimeout { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>How long finished (or stuck) jobs keep their files and records.</summary>
    public TimeSpan Retention { get; set; } = TimeSpan.FromHours(24);

    /// <summary>How often the cleanup sweep runs.</summary>
    public TimeSpan CleanupInterval { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>Fraction of <see cref="QueueCapacity"/> (0..1] at which /readyz reports 503.</summary>
    public double QueueHighWatermark { get; set; } = 0.9;

    /// <summary>Time the consumer gets to drain in-flight jobs during shutdown.</summary>
    public TimeSpan ShutdownDrainTimeout { get; set; } = TimeSpan.FromSeconds(30);
}
