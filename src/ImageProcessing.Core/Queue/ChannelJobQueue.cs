using System.Threading.Channels;

namespace ImageProcessing.Core.Queue;

/// <summary>
/// <see cref="Channel{T}"/>-backed bounded queue — the in-memory default.
/// A RabbitMQ/Redis implementation of the same interface lands behind a flag later.
/// </summary>
public sealed class ChannelJobQueue : IJobQueue
{
    private readonly Channel<JobLease> _channel;

    public ChannelJobQueue(int capacity = 128)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);

        Capacity = capacity;
        _channel = Channel.CreateBounded<JobLease>(new BoundedChannelOptions(capacity)
        {
            // TryWrite still returns false when full; readers await asynchronously.
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = false,
            SingleWriter = false,
        });
    }

    public int Capacity { get; }

    public int Count => _channel.Reader.CanCount ? _channel.Reader.Count : -1;

    public bool TryEnqueue(JobLease lease) => _channel.Writer.TryWrite(lease);

    public IAsyncEnumerable<JobLease> ReadAllAsync(CancellationToken ct = default) =>
        _channel.Reader.ReadAllAsync(ct);

    public void Complete() => _channel.Writer.TryComplete();
}
