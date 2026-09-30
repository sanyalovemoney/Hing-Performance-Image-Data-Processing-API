namespace ImageProcessing.Core.Processing;

/// <summary>
/// Routes job types to processors. Built once at startup, read-only afterwards —
/// no locking needed on the hot path.
/// </summary>
public sealed class ProcessorRegistry
{
    private readonly Dictionary<string, IJobProcessor> _byType = new(StringComparer.OrdinalIgnoreCase);
    private IJobProcessor? _fallback;

    public ProcessorRegistry Register(IJobProcessor processor)
    {
        _byType[processor.Type] = processor;
        return this;
    }

    /// <summary>Processor used for types without an explicit registration.</summary>
    public ProcessorRegistry RegisterFallback(IJobProcessor processor)
    {
        _fallback = processor;
        return this;
    }

    public IJobProcessor? Resolve(string type) =>
        _byType.TryGetValue(type, out var processor) ? processor : _fallback;
}
