using Microsoft.Extensions.Options;

namespace ImageProcessing.Core.Configuration;

/// <summary>
/// Fail-fast validation so nonsensical limits never reach runtime —
/// registered with ValidateOnStart to crash the host at boot instead.
/// </summary>
public sealed class ProcessingOptionsValidator : IValidateOptions<ProcessingOptions>
{
    public ValidateOptionsResult Validate(string? name, ProcessingOptions options)
    {
        var errors = new List<string>();

        if (options.MaxUploadBytes < 1024)
        {
            errors.Add($"MaxUploadBytes must be at least 1024 bytes (got {options.MaxUploadBytes}).");
        }

        if (options.QueueCapacity < 1)
        {
            errors.Add($"QueueCapacity must be at least 1 (got {options.QueueCapacity}).");
        }

        if (options.MaxConcurrency is < 1 or > 256)
        {
            errors.Add($"MaxConcurrency must be within [1, 256] (got {options.MaxConcurrency}).");
        }

        if (options.JobTimeout < TimeSpan.FromSeconds(1))
        {
            errors.Add($"JobTimeout must be at least 1 second (got {options.JobTimeout}).");
        }

        if (options.Retention < TimeSpan.FromMinutes(1))
        {
            errors.Add($"Retention must be at least 1 minute (got {options.Retention}).");
        }

        if (options.CleanupInterval < TimeSpan.FromSeconds(1))
        {
            errors.Add($"CleanupInterval must be at least 1 second (got {options.CleanupInterval}).");
        }

        if (options.QueueHighWatermark is <= 0 or > 1)
        {
            errors.Add($"QueueHighWatermark must be within (0, 1] (got {options.QueueHighWatermark}).");
        }

        if (options.ShutdownDrainTimeout < TimeSpan.Zero)
        {
            errors.Add($"ShutdownDrainTimeout must not be negative (got {options.ShutdownDrainTimeout}).");
        }

        return errors.Count > 0 ? ValidateOptionsResult.Fail(errors) : ValidateOptionsResult.Success;
    }
}
