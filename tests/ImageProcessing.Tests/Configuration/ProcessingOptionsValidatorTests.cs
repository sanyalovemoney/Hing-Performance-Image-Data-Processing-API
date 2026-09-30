using ImageProcessing.Core.Configuration;

namespace ImageProcessing.Tests.Configuration;

public sealed class ProcessingOptionsValidatorTests
{
    private readonly ProcessingOptionsValidator _validator = new();

    [Fact]
    public void Defaults_are_valid()
    {
        var result = _validator.Validate(name: null, new ProcessingOptions());

        Assert.True(result.Succeeded);
    }

    public static TheoryData<Action<ProcessingOptions>, string> InvalidCases => new()
    {
        { o => o.MaxUploadBytes = 1023, "MaxUploadBytes" },
        { o => o.MaxUploadBytes = 0, "MaxUploadBytes" },
        { o => o.QueueCapacity = 0, "QueueCapacity" },
        { o => o.QueueCapacity = -5, "QueueCapacity" },
        { o => o.MaxConcurrency = 0, "MaxConcurrency" },
        { o => o.MaxConcurrency = 257, "MaxConcurrency" },
        { o => o.JobTimeout = TimeSpan.FromMilliseconds(500), "JobTimeout" },
        { o => o.Retention = TimeSpan.FromSeconds(30), "Retention" },
        { o => o.CleanupInterval = TimeSpan.Zero, "CleanupInterval" },
        { o => o.QueueHighWatermark = 0, "QueueHighWatermark" },
        { o => o.QueueHighWatermark = -0.1, "QueueHighWatermark" },
        { o => o.QueueHighWatermark = 1.5, "QueueHighWatermark" },
        { o => o.ShutdownDrainTimeout = TimeSpan.FromSeconds(-1), "ShutdownDrainTimeout" },
    };

    [Theory]
    [MemberData(nameof(InvalidCases))]
    public void Invalid_values_are_rejected(Action<ProcessingOptions> mutate, string expectedInMessage)
    {
        var options = new ProcessingOptions();
        mutate(options);

        var result = _validator.Validate(name: null, options);

        Assert.True(result.Failed);
        Assert.Contains(expectedInMessage, Describe(result), StringComparison.Ordinal);
    }

    private static string Describe(Microsoft.Extensions.Options.ValidateOptionsResult result) =>
        string.Join(" ", result.Failures ?? Enumerable.Empty<string>());

    [Fact]
    public void Boundary_values_are_accepted()
    {
        var options = new ProcessingOptions
        {
            MaxUploadBytes = 1024,
            QueueCapacity = 1,
            MaxConcurrency = 1,
            JobTimeout = TimeSpan.FromSeconds(1),
            Retention = TimeSpan.FromMinutes(1),
            CleanupInterval = TimeSpan.FromSeconds(1),
            QueueHighWatermark = 1.0,
            ShutdownDrainTimeout = TimeSpan.Zero,
        };

        var result = _validator.Validate(name: null, options);

        Assert.True(result.Succeeded, Describe(result));
    }

    [Fact]
    public void All_violations_are_reported_together()
    {
        var options = new ProcessingOptions
        {
            MaxUploadBytes = 1,
            QueueCapacity = 0,
            MaxConcurrency = 999,
        };

        var result = _validator.Validate(name: null, options);

        Assert.True(result.Failed);
        Assert.Equal(3, result.Failures!.Count());
    }
}
