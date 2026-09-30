using ImageProcessing.Core.Configuration;
using ImageProcessing.Core.Jobs;
using ImageProcessing.Core.Storage;
using ImageProcessing.Workers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace ImageProcessing.Tests.Workers;

/// <summary>
/// Drives <see cref="TempCleanupWorker.Sweep"/> directly — no timers involved.
/// </summary>
public sealed class TempCleanupWorkerTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "cleanup-tests-" + Guid.NewGuid().ToString("N"));
    private readonly TempFileStorage _storage;
    private readonly InMemoryJobStore _store = new();

    public TempCleanupWorkerTests() => _storage = new TempFileStorage(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // Best effort — OS temp cleanup handles the rest.
        }
    }

    private TempCleanupWorker MakeWorker(Action<ProcessingOptions>? configure = null)
    {
        var options = new ProcessingOptions(); // Retention = 24h
        configure?.Invoke(options);
        return new TempCleanupWorker(
            _store, _storage, Options.Create(options), NullLogger<TempCleanupWorker>.Instance);
    }

    private void WriteFile(string relativePath)
    {
        using var stream = _storage.OpenWrite(relativePath);
        stream.Write(new byte[] { 1, 2, 3 });
    }

    [Fact]
    public void Aged_finished_job_record_and_files_are_removed()
    {
        var id = Guid.NewGuid();
        var upload = _storage.CreateUploadPath(id, ".dat");
        var result = _storage.CreateResultPath(id, ".dat");
        WriteFile(upload);
        WriteFile(result);
        _store.Add(new JobRecord
        {
            Id = id,
            Type = "echo",
            Status = JobStatus.Succeeded,
            CreatedAt = DateTimeOffset.UtcNow.AddHours(-30),
            FinishedAt = DateTimeOffset.UtcNow.AddHours(-25), // past the 24h retention
            UploadPath = upload,
            ResultPath = result,
        });

        var removed = MakeWorker().Sweep();

        Assert.Equal(1, removed);
        Assert.Null(_store.Get(id));
        Assert.False(_storage.Exists(upload));
        Assert.False(_storage.Exists(result));
    }

    [Fact]
    public void Fresh_job_is_kept()
    {
        var id = Guid.NewGuid();
        var upload = _storage.CreateUploadPath(id, ".dat");
        WriteFile(upload);
        _store.Add(new JobRecord
        {
            Id = id,
            Type = "echo",
            Status = JobStatus.Succeeded,
            CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-40),
            FinishedAt = DateTimeOffset.UtcNow.AddMinutes(-30),
            UploadPath = upload,
        });

        var removed = MakeWorker().Sweep();

        Assert.Equal(0, removed);
        Assert.NotNull(_store.Get(id));
        Assert.True(_storage.Exists(upload));
    }

    [Fact]
    public void Stuck_non_terminal_job_past_retention_is_removed()
    {
        // Simulates a job left Running by a crashed host — no FinishedAt, so
        // CreatedAt decides expiry.
        var id = Guid.NewGuid();
        var upload = _storage.CreateUploadPath(id, ".dat");
        WriteFile(upload);
        _store.Add(new JobRecord
        {
            Id = id,
            Type = "echo",
            Status = JobStatus.Running,
            CreatedAt = DateTimeOffset.UtcNow.AddHours(-48),
            UploadPath = upload,
        });

        var removed = MakeWorker().Sweep();

        Assert.Equal(1, removed);
        Assert.Null(_store.Get(id));
        Assert.False(_storage.Exists(upload));
    }

    [Fact]
    public void Orphaned_files_without_records_are_swept()
    {
        var orphan = _storage.CreateUploadPath(Guid.NewGuid(), ".dat");
        WriteFile(orphan);
        File.SetLastWriteTimeUtc(
            _storage.ResolveFullPath(orphan), DateTime.UtcNow.AddDays(-3));

        var removed = MakeWorker().Sweep();

        Assert.Equal(1, removed);
        Assert.False(_storage.Exists(orphan));
    }

    [Fact]
    public void Short_retention_expires_jobs_sooner()
    {
        var id = Guid.NewGuid();
        _store.Add(new JobRecord
        {
            Id = id,
            Type = "echo",
            Status = JobStatus.Failed,
            CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-10),
            FinishedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
        });

        var kept = MakeWorker(o => o.Retention = TimeSpan.FromMinutes(30)).Sweep();
        Assert.Equal(0, kept);

        var removed = MakeWorker(o => o.Retention = TimeSpan.FromMinutes(1)).Sweep();
        Assert.Equal(1, removed);
        Assert.Null(_store.Get(id));
    }
}
