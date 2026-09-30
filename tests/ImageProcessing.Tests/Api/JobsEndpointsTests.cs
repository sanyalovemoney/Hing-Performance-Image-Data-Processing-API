using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using ImageProcessing.Api.Endpoints;
using ImageProcessing.Core.Jobs;
using ImageProcessing.Core.Processing;
using ImageProcessing.Core.Queue;
using ImageProcessing.Core.Storage;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace ImageProcessing.Tests.Api;

public sealed class JobsEndpointsTests : IClassFixture<JobsApiFactory>
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);
    private readonly JobsApiFactory _factory;

    public JobsEndpointsTests(JobsApiFactory factory) => _factory = factory;

    /// <summary>1x1 px black PNG — smallest valid image payload.</summary>
    private static byte[] TinyPng() => Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");

    private static MultipartFormDataContent MakeForm(
        string? type = "echo",
        string fileName = "pixel.png",
        byte[]? payload = null,
        string mediaType = "image/png")
    {
        var form = new MultipartFormDataContent();
        var content = new ByteArrayContent(payload ?? TinyPng());
        content.Headers.ContentType = new MediaTypeHeaderValue(mediaType);
        form.Add(content, "file", fileName);
        if (type is not null)
        {
            form.Add(new StringContent(type), "type");
        }

        return form;
    }

    /// <summary>Header + 20 rows: id=i, value=i*0.5 → sum(value) = 105.</summary>
    private static byte[] BuildCsvPayload()
    {
        var builder = new StringBuilder("id,value\n");
        for (var i = 1; i <= 20; i++)
        {
            builder.Append(i.ToString(CultureInfo.InvariantCulture)).Append(',')
                .Append((i * 0.5).ToString(CultureInfo.InvariantCulture)).Append('\n');
        }

        return Encoding.UTF8.GetBytes(builder.ToString());
    }

    [Fact]
    public async Task Upload_returns_202_with_location_and_job_succeeds()
    {
        using var client = _factory.CreateClient();
        using var form = MakeForm();

        var response = await client.PostAsync("/api/v1/jobs", form);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var location = response.Headers.Location;
        Assert.NotNull(location);
        Assert.EndsWith("/status", location!.ToString(), StringComparison.Ordinal);

        var dto = await PollUntilTerminalAsync(client, location.ToString());
        Assert.Equal("Succeeded", dto.Status);
        Assert.Equal(100, dto.Progress);
    }

    [Fact]
    public async Task Unknown_job_returns_404()
    {
        using var client = _factory.CreateClient();

        var response = await client.GetAsync($"/api/v1/jobs/{Guid.NewGuid()}/status");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Missing_type_field_returns_400()
    {
        using var client = _factory.CreateClient();
        using var form = MakeForm(type: null);

        var response = await client.PostAsync("/api/v1/jobs", form);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Unknown_type_returns_400()
    {
        using var client = _factory.CreateClient();
        using var form = MakeForm(type: "quantum");

        var response = await client.PostAsync("/api/v1/jobs", form);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Invalid_params_json_returns_400()
    {
        using var client = _factory.CreateClient();
        using var form = MakeForm();
        form.Add(new StringContent("{not json"), "params");

        var response = await client.PostAsync("/api/v1/jobs", form);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Request_without_multipart_returns_400()
    {
        using var client = _factory.CreateClient();

        var response = await client.PostAsync("/api/v1/jobs", new StringContent("hello"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Image_job_produces_png_result()
    {
        using var client = _factory.CreateClient();
        using var form = MakeForm(type: "image");
        form.Add(new StringContent("""{"filter":"grayscale"}"""), "params");

        var response = await client.PostAsync("/api/v1/jobs", form);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        var created = await response.Content.ReadFromJsonAsync<JobStatusDto>(JsonOpts);
        Assert.NotNull(created);

        var dto = await PollUntilTerminalAsync(client, $"/api/v1/jobs/{created!.Id}/status");
        Assert.Equal("Succeeded", dto.Status);

        var result = await client.GetAsync($"/api/v1/jobs/{created.Id}/result");
        Assert.Equal(HttpStatusCode.OK, result.StatusCode);
        Assert.Equal("image/png", result.Content.Headers.ContentType?.MediaType);

        var bytes = await result.Content.ReadAsByteArrayAsync();
        using var decoded = SixLabors.ImageSharp.Image.Load<SixLabors.ImageSharp.PixelFormats.Bgra32>(
            new MemoryStream(bytes));
        Assert.Equal(1, decoded.Width);
        Assert.Equal(1, decoded.Height);
    }

    [Fact]
    public async Task Result_for_unknown_job_returns_404()
    {
        using var client = _factory.CreateClient();

        var response = await client.GetAsync($"/api/v1/jobs/{Guid.NewGuid()}/result");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Csv_job_produces_json_aggregate()
    {
        using var client = _factory.CreateClient();
        using var form = MakeForm(type: "csv", fileName: "data.csv", payload: BuildCsvPayload(), mediaType: "text/csv");
        form.Add(new StringContent("""{"column":1,"aggregate":"sum"}"""), "params");

        var response = await client.PostAsync("/api/v1/jobs", form);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        var created = await response.Content.ReadFromJsonAsync<JobStatusDto>(JsonOpts);
        Assert.NotNull(created);

        var dto = await PollUntilTerminalAsync(client, $"/api/v1/jobs/{created!.Id}/status");
        Assert.Equal("Succeeded", dto.Status);

        var result = await client.GetAsync($"/api/v1/jobs/{created.Id}/result");
        Assert.Equal(HttpStatusCode.OK, result.StatusCode);
        Assert.Equal("application/json", result.Content.Headers.ContentType?.MediaType);

        using var json = JsonDocument.Parse(await result.Content.ReadAsStringAsync());
        Assert.Equal(20, json.RootElement.GetProperty("rows").GetInt64());
        Assert.Equal(105, json.RootElement.GetProperty("value").GetDouble(), 6);
    }

    [Fact]
    public async Task Csv_job_with_bad_aggregate_fails_with_readable_error()
    {
        using var client = _factory.CreateClient();
        using var form = MakeForm(type: "csv", fileName: "data.csv", payload: BuildCsvPayload(), mediaType: "text/csv");
        form.Add(new StringContent("""{"column":1,"aggregate":"median"}"""), "params");

        var response = await client.PostAsync("/api/v1/jobs", form);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var created = await response.Content.ReadFromJsonAsync<JobStatusDto>(JsonOpts);

        var dto = await PollUntilTerminalAsync(client, $"/api/v1/jobs/{created!.Id}/status");
        Assert.Equal("Failed", dto.Status);
        Assert.Contains("aggregate", dto.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Rows_endpoint_streams_ndjson()
    {
        using var client = _factory.CreateClient();
        using var form = MakeForm(type: "csv", fileName: "data.csv", payload: BuildCsvPayload(), mediaType: "text/csv");

        var response = await client.PostAsync("/api/v1/jobs", form);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var created = await response.Content.ReadFromJsonAsync<JobStatusDto>(JsonOpts);

        var rows = await client.GetAsync($"/api/v1/jobs/{created!.Id}/rows");
        Assert.Equal(HttpStatusCode.OK, rows.StatusCode);
        Assert.Equal("application/x-ndjson", rows.Content.Headers.ContentType?.MediaType);

        var body = await rows.Content.ReadAsStringAsync();
        var lines = body.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Assert.Equal(21, lines.Length); // header + 20 data rows

        using var first = JsonDocument.Parse(lines[0]);
        Assert.Equal(0, first.RootElement.GetProperty("index").GetInt32());
        Assert.Equal("id", first.RootElement.GetProperty("fields")[0].GetString());

        using var last = JsonDocument.Parse(lines[^1]);
        Assert.Equal(20, last.RootElement.GetProperty("index").GetInt32());
        Assert.Equal("10", last.RootElement.GetProperty("fields")[1].GetString());
    }

    [Fact]
    public async Task Matrix_job_produces_binary_result()
    {
        using var client = _factory.CreateClient();
        var payload = Processing.MatrixTestHelpers.BuildBinary(2, 3, new float[] { 1, 2, 3, 4, 5, 6 });
        using var form = MakeForm(
            type: "matrix", fileName: "m.bin", payload: payload, mediaType: "application/octet-stream");
        form.Add(new StringContent("""{"op":"scale","scale":3}"""), "params");

        var response = await client.PostAsync("/api/v1/jobs", form);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var created = await response.Content.ReadFromJsonAsync<JobStatusDto>(JsonOpts);

        var dto = await PollUntilTerminalAsync(client, $"/api/v1/jobs/{created!.Id}/status");
        Assert.Equal("Succeeded", dto.Status);

        var result = await client.GetAsync($"/api/v1/jobs/{created.Id}/result");
        Assert.Equal(HttpStatusCode.OK, result.StatusCode);
        Assert.Equal("application/octet-stream", result.Content.Headers.ContentType?.MediaType);

        var (rows, cols, data) = Processing.MatrixTestHelpers.ParseBinary(
            await result.Content.ReadAsByteArrayAsync());
        Assert.Equal(2, rows);
        Assert.Equal(3, cols);
        Assert.Equal(new float[] { 3, 6, 9, 12, 15, 18 }, data);
    }

    [Fact]
    public async Task Malformed_matrix_upload_fails_with_readable_error()
    {
        using var client = _factory.CreateClient();
        using var form = MakeForm(
            type: "matrix",
            fileName: "m.bin",
            payload: new byte[] { 9, 0, 0, 0, 9, 0, 0, 0 }, // claims 9x9, has no data
            mediaType: "application/octet-stream");

        var response = await client.PostAsync("/api/v1/jobs", form);
        var created = await response.Content.ReadFromJsonAsync<JobStatusDto>(JsonOpts);

        var dto = await PollUntilTerminalAsync(client, $"/api/v1/jobs/{created!.Id}/status");
        Assert.Equal("Failed", dto.Status);
        Assert.Contains("truncated", dto.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Rows_for_unknown_job_returns_404()
    {
        using var client = _factory.CreateClient();

        var response = await client.GetAsync($"/api/v1/jobs/{Guid.NewGuid()}/rows");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Rows_for_non_csv_job_returns_409()
    {
        using var client = _factory.CreateClient();
        using var form = MakeForm(type: "image");

        var response = await client.PostAsync("/api/v1/jobs", form);
        var created = await response.Content.ReadFromJsonAsync<JobStatusDto>(JsonOpts);

        var rows = await client.GetAsync($"/api/v1/jobs/{created!.Id}/rows");

        Assert.Equal(HttpStatusCode.Conflict, rows.StatusCode);
    }

    [Fact]
    public async Task Readyz_returns_200_when_queue_is_idle()
    {
        using var client = _factory.CreateClient();

        var response = await client.GetAsync("/readyz");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("ready", json.RootElement.GetProperty("status").GetString());
        Assert.True(json.RootElement.GetProperty("capacity").GetInt32() > 0);
    }

    [Fact]
    public async Task Upload_over_max_bytes_returns_413()
    {
        await using var factory = new JobsApiFactory();
        using var smallCap = factory.WithWebHostBuilder(b =>
            b.UseSetting("Processing:MaxUploadBytes", "1024"));
        using var client = smallCap.CreateClient();
        using var form = MakeForm(payload: new byte[5000]);

        var response = await client.PostAsync("/api/v1/jobs", form);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
    }

    /// <summary>Parks on a TCS so the test controls exactly when the job finishes.</summary>
    private sealed class GateProcessor : IJobProcessor
    {
        private readonly TaskCompletionSource _gate;

        public GateProcessor(string type, TaskCompletionSource gate)
        {
            Type = type;
            _gate = gate;
        }

        public string Type { get; }

        public Task ProcessAsync(JobRecord job, CancellationToken ct) => _gate.Task.WaitAsync(ct);
    }

    [Fact]
    public async Task Full_queue_returns_503_and_readyz_reports_overload()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var factory = new JobsApiFactory();
        using var gated = factory.WithWebHostBuilder(b =>
        {
            // Capacity 4 + single slot: the parked job freezes the consumer, so
            // fake leases stay queued and the queue is provably full.
            b.UseSetting("Processing:QueueCapacity", "4");
            b.UseSetting("Processing:MaxConcurrency", "1");
            b.ConfigureTestServices(services =>
            {
                services.AddSingleton(_ =>
                    new ProcessorRegistry().Register(new GateProcessor("echo", gate)));
            });
        });
        using var client = gated.CreateClient();

        using var form = MakeForm();
        var accepted = await client.PostAsync("/api/v1/jobs", form);
        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        var created = await accepted.Content.ReadFromJsonAsync<JobStatusDto>(JsonOpts);
        Assert.NotNull(created);
        await PollUntilRunningAsync(client, created!.Id);

        var queue = gated.Services.GetRequiredService<IJobQueue>();

        // First lease gets picked up by the consumer and parks on the gate
        // (worker slot taken, so the next one cannot be dequeued).
        Assert.True(queue.TryEnqueue(new JobLease(Guid.NewGuid(), "echo")));
        var takenDeadline = DateTime.UtcNow.AddSeconds(10);
        while (queue.Count > 0 && DateTime.UtcNow < takenDeadline)
        {
            await Task.Delay(25);
        }

        Assert.Equal(0, queue.Count);

        // Fill the bounded queue to capacity.
        for (var i = 0; i < 4; i++)
        {
            Assert.True(queue.TryEnqueue(new JobLease(Guid.NewGuid(), "echo")), $"enqueue {i} rejected");
        }

        Assert.Equal(4, queue.Count);

        // Upload beyond capacity → 503 with Retry-After.
        using var overflowForm = MakeForm();
        var rejected = await client.PostAsync("/api/v1/jobs", overflowForm);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, rejected.StatusCode);
        Assert.Equal(TimeSpan.FromSeconds(5), rejected.Headers.RetryAfter?.Delta);

        // /readyz at the high-watermark (ceil(4*0.9)=4) → 503 overloaded.
        var notReady = await client.GetAsync("/readyz");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, notReady.StatusCode);
        using (var body = JsonDocument.Parse(await notReady.Content.ReadAsStringAsync()))
        {
            Assert.Equal("overloaded", body.RootElement.GetProperty("status").GetString());
            Assert.Equal(4, body.RootElement.GetProperty("queued").GetInt32());
        }

        // Release the gate: the parked job finishes, the fake leases drain
        // (skipped — no store records) and readiness recovers.
        gate.SetResult();
        var done = await PollUntilTerminalAsync(client, $"/api/v1/jobs/{created.Id}/status");
        Assert.Equal("Succeeded", done.Status);
        await PollUntilReadyAsync(client);
    }

    private sealed record JobListDto(JobStatusDto[] Items, int Total, int Limit, int Offset);

    [Fact]
    public async Task List_endpoint_paginates_jobs()
    {
        using var client = _factory.CreateClient();
        for (var i = 0; i < 3; i++)
        {
            using var form = MakeForm();
            var response = await client.PostAsync("/api/v1/jobs", form);
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        }

        var page = await client.GetFromJsonAsync<JobListDto>("/api/v1/jobs?limit=2&offset=0", JsonOpts);
        Assert.NotNull(page);
        Assert.True(page!.Total >= 3, $"expected at least the 3 jobs this test created, got {page.Total}");
        Assert.Equal(2, page.Items.Length);
        Assert.Equal(2, page.Limit);
        Assert.Equal(0, page.Offset);

        var second = await client.GetFromJsonAsync<JobListDto>("/api/v1/jobs?limit=2&offset=2", JsonOpts);
        Assert.NotNull(second);
        Assert.DoesNotContain(second!.Items, j => page.Items.Any(p => p.Id == j.Id));
    }

    [Fact]
    public async Task Delete_unknown_job_returns_404()
    {
        using var client = _factory.CreateClient();

        var response = await client.DeleteAsync($"/api/v1/jobs/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Delete_finished_job_removes_record_and_files()
    {
        using var client = _factory.CreateClient();
        using var form = MakeForm();
        var accepted = await client.PostAsync("/api/v1/jobs", form);
        var created = await accepted.Content.ReadFromJsonAsync<JobStatusDto>(JsonOpts);
        var done = await PollUntilTerminalAsync(client, $"/api/v1/jobs/{created!.Id}/status");
        Assert.Equal("Succeeded", done.Status);

        var deleted = await client.DeleteAsync($"/api/v1/jobs/{created.Id}");
        Assert.Equal(HttpStatusCode.Accepted, deleted.StatusCode);

        var after = await client.GetAsync($"/api/v1/jobs/{created.Id}/status");
        Assert.Equal(HttpStatusCode.NotFound, after.StatusCode);

        var storage = _factory.Services.GetRequiredService<IFileStorage>();
        Assert.DoesNotContain(storage.List(), e =>
            e.RelativePath.Contains(created.Id.ToString("N"), StringComparison.Ordinal));
    }

    [Fact]
    public async Task Delete_running_job_cancels_it_cooperatively()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var factory = new JobsApiFactory();
        using var gated = factory.WithWebHostBuilder(b =>
        {
            b.UseSetting("Processing:MaxConcurrency", "1");
            b.ConfigureTestServices(services =>
                services.AddSingleton(_ =>
                    new ProcessorRegistry().Register(new GateProcessor("echo", gate))));
        });
        using var client = gated.CreateClient();

        using var form = MakeForm();
        var accepted = await client.PostAsync("/api/v1/jobs", form);
        var created = await accepted.Content.ReadFromJsonAsync<JobStatusDto>(JsonOpts);
        await PollUntilRunningAsync(client, created!.Id);

        var deleted = await client.DeleteAsync($"/api/v1/jobs/{created.Id}");
        Assert.Equal(HttpStatusCode.Accepted, deleted.StatusCode);

        var dto = await PollUntilTerminalAsync(client, $"/api/v1/jobs/{created.Id}/status");
        Assert.Equal("Canceled", dto.Status);
        Assert.Contains("canceled by client", dto.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Delete_queued_job_cancels_it_and_removes_upload()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var factory = new JobsApiFactory();
        using var gated = factory.WithWebHostBuilder(b =>
        {
            b.UseSetting("Processing:QueueCapacity", "8");
            b.UseSetting("Processing:MaxConcurrency", "1");
            b.ConfigureTestServices(services =>
                services.AddSingleton(_ =>
                    new ProcessorRegistry().Register(new GateProcessor("echo", gate))));
        });
        using var client = gated.CreateClient();

        // Park the single worker slot so the second job provably stays Queued.
        using var firstForm = MakeForm();
        var first = await client.PostAsync("/api/v1/jobs", firstForm);
        var firstDto = await first.Content.ReadFromJsonAsync<JobStatusDto>(JsonOpts);
        await PollUntilRunningAsync(client, firstDto!.Id);

        using var secondForm = MakeForm();
        var second = await client.PostAsync("/api/v1/jobs", secondForm);
        var secondDto = await second.Content.ReadFromJsonAsync<JobStatusDto>(JsonOpts);
        var before = await client.GetFromJsonAsync<JobStatusDto>(
            $"/api/v1/jobs/{secondDto!.Id}/status", JsonOpts);
        Assert.Equal("Queued", before!.Status);

        var deleted = await client.DeleteAsync($"/api/v1/jobs/{secondDto.Id}");
        Assert.Equal(HttpStatusCode.Accepted, deleted.StatusCode);

        var after = await client.GetFromJsonAsync<JobStatusDto>(
            $"/api/v1/jobs/{secondDto.Id}/status", JsonOpts);
        Assert.Equal("Canceled", after!.Status);

        var storage = gated.Services.GetRequiredService<IFileStorage>();
        Assert.DoesNotContain(storage.List(), e =>
            e.RelativePath.Contains(secondDto.Id.ToString("N"), StringComparison.Ordinal));

        // Unblock the worker: the canceled job's lease must be skipped, not run.
        gate.SetResult();
        var done = await PollUntilTerminalAsync(client, $"/api/v1/jobs/{firstDto.Id}/status");
        Assert.Equal("Succeeded", done.Status);
        var stillCanceled = await client.GetFromJsonAsync<JobStatusDto>(
            $"/api/v1/jobs/{secondDto.Id}/status", JsonOpts);
        Assert.Equal("Canceled", stillCanceled!.Status);
    }

    [Fact]
    public async Task Burst_of_six_jobs_all_succeed_under_concurrency_limit()
    {
        using var client = _factory.CreateClient();

        var posts = Enumerable.Range(0, 6).Select(async _ =>
        {
            using var form = MakeForm(
                type: "csv", fileName: "data.csv", payload: BuildCsvPayload(), mediaType: "text/csv");
            form.Add(new StringContent("""{"column":1,"aggregate":"sum"}"""), "params");
            var response = await client.PostAsync("/api/v1/jobs", form);
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
            return (await response.Content.ReadFromJsonAsync<JobStatusDto>(JsonOpts))!.Id;
        });
        var ids = await Task.WhenAll(posts);

        Assert.Equal(6, ids.Distinct().Count());
        foreach (var id in ids)
        {
            var dto = await PollUntilTerminalAsync(client, $"/api/v1/jobs/{id}/status");
            Assert.Equal("Succeeded", dto.Status);
        }
    }

    private static async Task<JobStatusDto> PollUntilTerminalAsync(HttpClient client, string statusUrl)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            var dto = await client.GetFromJsonAsync<JobStatusDto>(statusUrl, JsonOpts);
            Assert.NotNull(dto);
            if (dto!.Status is "Succeeded" or "Failed" or "Canceled")
            {
                return dto;
            }

            await Task.Delay(100);
        }

        throw new TimeoutException($"job at {statusUrl} did not reach a terminal state");
    }

    private static async Task PollUntilRunningAsync(HttpClient client, Guid id)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            var dto = await client.GetFromJsonAsync<JobStatusDto>(
                $"/api/v1/jobs/{id}/status", JsonOpts);
            if (dto?.Status == "Running")
            {
                return;
            }

            await Task.Delay(50);
        }

        throw new TimeoutException($"job {id} did not start running");
    }

    private static async Task PollUntilReadyAsync(HttpClient client)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            var response = await client.GetAsync("/readyz");
            if (response.StatusCode == HttpStatusCode.OK)
            {
                return;
            }

            await Task.Delay(50);
        }

        throw new TimeoutException("/readyz did not recover");
    }
}
