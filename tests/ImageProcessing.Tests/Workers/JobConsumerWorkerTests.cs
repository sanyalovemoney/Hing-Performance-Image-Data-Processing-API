using ImageProcessing.Core.Configuration;
using ImageProcessing.Core.Jobs;
using ImageProcessing.Core.Processing;
using ImageProcessing.Core.Queue;
using ImageProcessing.Workers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace ImageProcessing.Tests.Workers;

/// <summary>
/// Drives the real worker against the real ChannelJobQueue + InMemoryJobStore
/// with delegate-based processors — concurrency gate, job timeout, IOException
/// retry, generic failure and graceful shutdown drain.
/// </summary>
public sealed class JobConsumerWorkerTests
{
    private sealed class DelegateProcessor : IJobProcessor
    {
        private readonly Func<JobRecord, CancellationToken, Task> _run;

        public DelegateProcessor(string type, Func<JobRecord, CancellationToken, Task> run)
        {
            Type = type;
            _run = run;
        }

        public string Type { get; }

        public Task ProcessAsync(JobRecord job, CancellationToken ct) => _run(job, ct);
    }

    private static (JobConsumerWorker Worker, InMemoryJobStore Store, ChannelJobQueue Queue) Start(
        Func<JobRecord, CancellationToken, Task> run,
        Action<ProcessingOptions>? configure = null)
    {
        var options = new ProcessingOptions
        {
            MaxConcurrency = 2,
            ShutdownDrainTimeout = TimeSpan.FromSeconds(10),
        };
        configure?.Invoke(options);

        var store = new InMemoryJobStore();
        var queue = new ChannelJobQueue(capacity: 16);
        var registry = new ProcessorRegistry().Register(new DelegateProcessor("test", run));
        var worker = new JobConsumerWorker(
            queue, store, registry, Options.Create(options), NullLogger<JobConsumerWorker>.Instance);
        worker.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
        return (worker, store, queue);
    }

    private static Guid[] Enqueue(InMemoryJobStore store, ChannelJobQueue queue, int count)
    {
        var ids = new Guid[count];
        for (var i = 0; i < count; i++)
        {
            ids[i] = Guid.NewGuid();
            store.Add(new JobRecord { Id = ids[i], Type = "test", Status = JobStatus.Queued });
            Assert.True(queue.TryEnqueue(new JobLease(ids[i], "test")));
        }

        return ids;
    }

    private static async Task WaitForTerminalAsync(InMemoryJobStore store, Guid[] ids, int timeoutMs = 15_000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (ids.All(id => store.Get(id) is { IsTerminal: true }))
            {
                return;
            }

            await Task.Delay(25);
        }

        var stuck = ids
            .Where(id => store.Get(id) is not { IsTerminal: true })
            .Select(id => $"{id}:{store.Get(id)?.Status}");
        throw new TimeoutException($"jobs did not reach a terminal state: {string.Join(", ", stuck)}");
    }

    [Fact]
    public async Task At_most_MaxConcurrency_jobs_run_simultaneously()
    {
        var current = 0;
        var peak = 0;
        var (worker, store, queue) = Start(async (_, ct) =>
        {
            var n = Interlocked.Increment(ref current);
            int p;
            do
            {
                p = Volatile.Read(ref peak);
                if (n <= p)
                {
                    break;
                }
            }
            while (Interlocked.CompareExchange(ref peak, n, p) != p);

            await Task.Delay(150, ct);
            Interlocked.Decrement(ref current);
        });

        try
        {
            var ids = Enqueue(store, queue, 6);
            await WaitForTerminalAsync(store, ids);

            Assert.All(ids, id => Assert.Equal(JobStatus.Succeeded, store.Get(id)!.Status));
            Assert.True(peak <= 2, $"peak concurrency {peak} exceeded the configured limit of 2");
            Assert.Equal(2, peak); // six 150ms jobs must have saturated the gate
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Job_exceeding_timeout_fails_with_readable_error()
    {
        var (worker, store, queue) = Start(
            (_, ct) => Task.Delay(TimeSpan.FromSeconds(30), ct),
            o => o.JobTimeout = TimeSpan.FromMilliseconds(300));

        try
        {
            var ids = Enqueue(store, queue, 1);
            await WaitForTerminalAsync(store, ids);

            var job = store.Get(ids[0])!;
            Assert.Equal(JobStatus.Failed, job.Status);
            Assert.Contains("timed out", job.Error, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task IOException_triggers_exactly_one_retry_then_succeeds()
    {
        var attempts = 0;
        var (worker, store, queue) = Start((_, _) =>
        {
            if (Interlocked.Increment(ref attempts) == 1)
            {
                throw new IOException("transient disk hiccup");
            }

            return Task.CompletedTask;
        });

        try
        {
            var ids = Enqueue(store, queue, 1);
            await WaitForTerminalAsync(store, ids);

            Assert.Equal(JobStatus.Succeeded, store.Get(ids[0])!.Status);
            Assert.Equal(2, attempts);
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Persistent_IOException_fails_after_second_attempt()
    {
        var attempts = 0;
        var (worker, store, queue) = Start((_, _) =>
        {
            Interlocked.Increment(ref attempts);
            throw new IOException("disk is gone");
        });

        try
        {
            var ids = Enqueue(store, queue, 1);
            await WaitForTerminalAsync(store, ids);

            var job = store.Get(ids[0])!;
            Assert.Equal(JobStatus.Failed, job.Status);
            Assert.Contains("disk is gone", job.Error, StringComparison.Ordinal);
            Assert.Equal(2, attempts);
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Non_IO_exception_fails_job_without_retry()
    {
        var attempts = 0;
        var (worker, store, queue) = Start((_, _) =>
        {
            Interlocked.Increment(ref attempts);
            throw new InvalidOperationException("boom");
        });

        try
        {
            var ids = Enqueue(store, queue, 1);
            await WaitForTerminalAsync(store, ids);

            var job = store.Get(ids[0])!;
            Assert.Equal(JobStatus.Failed, job.Status);
            Assert.Contains("boom", job.Error, StringComparison.Ordinal);
            Assert.Equal(1, attempts);
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Lease_without_store_record_is_skipped_and_worker_keeps_running()
    {
        var processed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var (worker, store, queue) = Start((_, _) =>
        {
            processed.SetResult();
            return Task.CompletedTask;
        });

        try
        {
            Assert.True(queue.TryEnqueue(new JobLease(Guid.NewGuid(), "test"))); // orphan lease first
            var ids = Enqueue(store, queue, 1);

            await WaitForTerminalAsync(store, ids);
            await processed.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(JobStatus.Succeeded, store.Get(ids[0])!.Status);
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task StopAsync_drains_in_flight_job_instead_of_canceling_it()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var (worker, store, queue) = Start(async (_, _) =>
        {
            started.SetResult();
            // Ignores every token: only a graceful drain can let this finish.
            await Task.Delay(700, CancellationToken.None);
        });

        var ids = Enqueue(store, queue, 1);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));

        await worker.StopAsync(CancellationToken.None);

        Assert.Equal(JobStatus.Succeeded, store.Get(ids[0])!.Status);
    }
}
