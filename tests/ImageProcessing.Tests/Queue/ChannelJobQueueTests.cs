using ImageProcessing.Core.Queue;

namespace ImageProcessing.Tests.Queue;

public class ChannelJobQueueTests
{
    [Fact]
    public void TryEnqueue_returns_false_when_queue_is_full()
    {
        var queue = new ChannelJobQueue(capacity: 2);

        Assert.True(queue.TryEnqueue(new JobLease(Guid.NewGuid(), "image")));
        Assert.True(queue.TryEnqueue(new JobLease(Guid.NewGuid(), "image")));
        Assert.False(queue.TryEnqueue(new JobLease(Guid.NewGuid(), "image")));
    }

    [Fact]
    public async Task Enqueued_leases_are_read_in_order()
    {
        var queue = new ChannelJobQueue(capacity: 8);
        var first = new JobLease(Guid.NewGuid(), "csv");
        var second = new JobLease(Guid.NewGuid(), "image");
        Assert.True(queue.TryEnqueue(first));
        Assert.True(queue.TryEnqueue(second));
        queue.Complete();

        var read = new List<JobLease>();
        await foreach (var lease in queue.ReadAllAsync())
        {
            read.Add(lease);
        }

        Assert.Equal(new[] { first, second }, read);
    }

    [Fact]
    public async Task ReadAllAsync_finishes_after_drain_when_completed()
    {
        var queue = new ChannelJobQueue(capacity: 4);
        Assert.True(queue.TryEnqueue(new JobLease(Guid.NewGuid(), "echo")));
        queue.Complete();

        var count = 0;
        await foreach (var _ in queue.ReadAllAsync())
        {
            count++;
        }

        Assert.Equal(1, count);
    }

    [Fact]
    public async Task ReadAllAsync_throws_when_canceled()
    {
        var queue = new ChannelJobQueue(capacity: 4);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in queue.ReadAllAsync(cts.Token))
            {
            }
        });
    }

    [Fact]
    public void Count_tracks_backlog()
    {
        var queue = new ChannelJobQueue(capacity: 4);

        Assert.Equal(0, queue.Count);
        Assert.True(queue.TryEnqueue(new JobLease(Guid.NewGuid(), "echo")));
        Assert.Equal(1, queue.Count);
    }

    [Fact]
    public void Constructor_rejects_non_positive_capacity() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new ChannelJobQueue(0));
}
