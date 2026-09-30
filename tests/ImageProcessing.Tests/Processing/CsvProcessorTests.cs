using System.Globalization;
using System.Text;
using System.Text.Json;
using ImageProcessing.Core.Jobs;
using ImageProcessing.Core.Storage;
using ImageProcessing.Processing.Csv;

namespace ImageProcessing.Tests.Processing;

public sealed class CsvProcessorTests
{
    /// <summary>id,value,group — 100 rows: value = i*1.5, group = i%3.</summary>
    private static string BuildCsv()
    {
        var builder = new StringBuilder("id,value,group\n");
        for (var i = 0; i < 100; i++)
        {
            builder.Append(i.ToString(CultureInfo.InvariantCulture)).Append(',')
                .Append((i * 1.5).ToString(CultureInfo.InvariantCulture)).Append(',')
                .Append((i % 3).ToString(CultureInfo.InvariantCulture)).Append('\n');
        }

        return builder.ToString();
    }

    private static async Task<(JsonDocument Json, JobRecord Job, TempFileStorage Storage, string Root)> RunAsync(
        string csv, string? paramsJson)
    {
        var root = Path.Combine(Path.GetTempPath(), "csvproc-" + Guid.NewGuid().ToString("N"));
        var storage = new TempFileStorage(root);
        var processor = new CsvProcessor(storage);
        var job = new JobRecord
        {
            Id = Guid.NewGuid(),
            Type = "csv",
            Params = paramsJson,
            UploadPath = storage.CreateUploadPath(Guid.NewGuid(), ".csv"),
        };

        await using (var output = storage.OpenWrite(job.UploadPath))
        {
            var bytes = Encoding.UTF8.GetBytes(csv);
            await output.WriteAsync(bytes);
        }

        await processor.ProcessAsync(job, CancellationToken.None);
        Assert.NotNull(job.ResultPath);
        Assert.Equal("application/json", job.ResultContentType);

        await using var input = storage.OpenRead(job.ResultPath!);
        var json = await JsonDocument.ParseAsync(input);
        return (json, job, storage, root);
    }

    private static async Task CleanupAsync(TempFileStorage storage, string root)
    {
        await Task.CompletedTask;
        try
        {
            Directory.Delete(root, recursive: true);
        }
        catch (IOException)
        {
            // best effort — a lingering temp dir must not fail the suite
        }
    }

    [Theory]
    [InlineData("""{"column":1,"aggregate":"sum"}""", 7425)]       // 1.5 * (0+..+99)
    [InlineData("""{"column":1,"aggregate":"avg"}""", 74.25)]
    [InlineData("""{"column":1,"aggregate":"min"}""", 0)]
    [InlineData("""{"column":1,"aggregate":"max"}""", 148.5)]
    [InlineData("""{"column":1,"aggregate":"count"}""", 100)]
    [InlineData("""{"column":1,"aggregate":"p95"}""", 141)]         // 95th of 0..148.5
    public async Task Aggregates_over_numeric_column(string paramsJson, double expected)
    {
        var (json, _, storage, root) = await RunAsync(BuildCsv(), paramsJson);
        using (json)
        {
            Assert.Equal(100, json.RootElement.GetProperty("rows").GetInt64());
            Assert.Equal(0, json.RootElement.GetProperty("skippedRows").GetInt64());
            Assert.Equal(expected, json.RootElement.GetProperty("value").GetDouble(), 6);
        }

        await CleanupAsync(storage, root);
    }

    [Fact]
    public async Task Defaults_sum_column_zero_when_params_absent()
    {
        var (json, _, storage, root) = await RunAsync(BuildCsv(), null);
        using (json)
        {
            // column 0 = id → sum 0..99 = 4950
            Assert.Equal(4950, json.RootElement.GetProperty("value").GetDouble(), 6);
            Assert.Equal("sum", json.RootElement.GetProperty("aggregate").GetString());
        }

        await CleanupAsync(storage, root);
    }

    [Fact]
    public async Task GroupBy_produces_per_group_sums()
    {
        var (json, _, storage, root) = await RunAsync(
            BuildCsv(), """{"column":1,"aggregate":"sum","groupBy":2}""");
        using (json)
        {
            var groups = json.RootElement.GetProperty("groups");
            Assert.Equal(2524.5, groups.GetProperty("0").GetDouble(), 6);
            Assert.Equal(2425.5, groups.GetProperty("1").GetDouble(), 6);
            Assert.Equal(2475, groups.GetProperty("2").GetDouble(), 6);
        }

        await CleanupAsync(storage, root);
    }

    [Fact]
    public async Task Header_row_counts_as_skipped_when_not_skipping()
    {
        var (json, _, storage, root) = await RunAsync(
            BuildCsv(), """{"column":1,"aggregate":"count","skipHeader":false}""");
        using (json)
        {
            Assert.Equal(100, json.RootElement.GetProperty("rows").GetInt64());
            Assert.Equal(1, json.RootElement.GetProperty("skippedRows").GetInt64());
        }

        await CleanupAsync(storage, root);
    }

    [Fact]
    public async Task Non_numeric_values_are_skipped_not_fatal()
    {
        const string csv = "name,score\nalice,10\nbob,not-a-number\ncarol,2.5\n";
        var (json, _, storage, root) = await RunAsync(csv, """{"column":1,"aggregate":"sum"}""");
        using (json)
        {
            Assert.Equal(2, json.RootElement.GetProperty("rows").GetInt64());
            Assert.Equal(1, json.RootElement.GetProperty("skippedRows").GetInt64());
            Assert.Equal(12.5, json.RootElement.GetProperty("value").GetDouble(), 6);
        }

        await CleanupAsync(storage, root);
    }

    [Fact]
    public async Task P95_buffer_grows_past_initial_pool_rental()
    {
        // 5000 rows > initial 1024-slot pooled buffer → exercises the doubling path.
        var builder = new StringBuilder("v\n");
        for (var i = 1; i <= 5000; i++)
        {
            builder.Append(i.ToString(CultureInfo.InvariantCulture)).Append('\n');
        }

        var (json, _, storage, root) = await RunAsync(builder.ToString(), """{"column":0,"aggregate":"p95"}""");
        using (json)
        {
            // ceil(0.95*5000) = 4750th smallest of 1..5000 → 4750
            Assert.Equal(4750, json.RootElement.GetProperty("value").GetDouble(), 6);
            Assert.Equal(5000, json.RootElement.GetProperty("rows").GetInt64());
        }

        await CleanupAsync(storage, root);
    }

    [Fact]
    public async Task Allocated_bytes_are_measured_into_the_job()
    {
        var (_, job, storage, root) = await RunAsync(BuildCsv(), """{"column":1,"aggregate":"sum"}""");
        Assert.True(job.AllocatedBytes >= 0);
        await CleanupAsync(storage, root);
    }

    [Fact]
    public async Task Missing_upload_fails_the_job()
    {
        var root = Path.Combine(Path.GetTempPath(), "csvproc-" + Guid.NewGuid().ToString("N"));
        var storage = new TempFileStorage(root);
        var processor = new CsvProcessor(storage);
        var job = new JobRecord
        {
            Id = Guid.NewGuid(),
            Type = "csv",
            UploadPath = "uploads/does-not-exist.csv",
        };

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => processor.ProcessAsync(job, CancellationToken.None));
        await CleanupAsync(storage, root);
    }

    [Theory]
    [InlineData("""{"aggregate":"median"}""")]          // unknown aggregate
    [InlineData("""{"column":-1}""")]                  // negative column
    [InlineData("""{"column":"x"}""")]                 // wrong type
    [InlineData("""{"aggregate":"p95","groupBy":0}""")] // unsupported combo
    [InlineData("{not json")]                          // malformed
    [InlineData("[1,2]")]                              // not an object
    [InlineData("""{"skipHeader":"yes"}""")]           // bool expected
    public async Task Invalid_params_throw_format_exception(string paramsJson)
    {
        var root = Path.Combine(Path.GetTempPath(), "csvproc-" + Guid.NewGuid().ToString("N"));
        var storage = new TempFileStorage(root);
        var processor = new CsvProcessor(storage);
        var job = new JobRecord
        {
            Id = Guid.NewGuid(),
            Type = "csv",
            Params = paramsJson,
            UploadPath = storage.CreateUploadPath(Guid.NewGuid(), ".csv"),
        };
        await using (var output = storage.OpenWrite(job.UploadPath))
        {
            await output.WriteAsync("a\n1\n"u8.ToArray());
        }

        await Assert.ThrowsAsync<FormatException>(
            () => processor.ProcessAsync(job, CancellationToken.None));
        await CleanupAsync(storage, root);
    }
}
