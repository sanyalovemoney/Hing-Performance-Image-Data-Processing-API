namespace ImageProcessing.Api.Upload;

/// <summary>Thrown mid-stream when an upload exceeds the configured byte cap.</summary>
public sealed class PayloadTooLargeException(long maxBytes)
    : Exception($"payload exceeds the {maxBytes}-byte limit");
