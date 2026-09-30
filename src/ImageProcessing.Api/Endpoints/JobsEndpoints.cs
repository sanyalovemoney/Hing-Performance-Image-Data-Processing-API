using System.Text.Json;
using ImageProcessing.Api.Upload;
using ImageProcessing.Core.Jobs;
using ImageProcessing.Core.Queue;
using ImageProcessing.Core.Storage;
using ImageProcessing.Processing.Csv;

namespace ImageProcessing.Api.Endpoints;

public static class JobsEndpoints
{
    // Provisional cap until ProcessingOptions land (Limits phase).
    internal const long UploadCapBytes = 50L * 1024 * 1024;

    private static readonly HashSet<string> AllowedTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "image", "csv", "matrix", "echo",
    };

    private static readonly string[] AllowedExtensions =
    [
        ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp",
        ".csv", ".tsv", ".txt", ".bin", ".dat",
    ];

    public static IEndpointRouteBuilder MapJobsEndpoints(this IEndpointRouteBuilder app)
    {
        var jobs = app.MapGroup("/api/v1/jobs").WithTags("jobs");

        jobs.MapPost("", UploadAsync)
            .DisableAntiforgery()
            .WithName("CreateJob")
            .Produces<JobStatusDto>(StatusCodes.Status202Accepted)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status413PayloadTooLarge)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        jobs.MapGet("/{id:guid}/status", (Guid id, IJobStore store) =>
                store.Get(id) is { } job ? Results.Ok(ToDto(job)) : Results.NotFound())
            .WithName("GetJobStatus")
            .Produces<JobStatusDto>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        jobs.MapGet("/{id:guid}/result", GetResultAsync)
            .WithName("GetJobResult")
            .Produces(StatusCodes.Status200OK, contentType: "application/octet-stream")
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        jobs.MapGet("/{id:guid}/rows", StreamRowsAsync)
            .WithName("StreamJobRows")
            .Produces(StatusCodes.Status200OK, contentType: "application/x-ndjson")
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        return app;
    }

    /// <summary>
    /// Streams the job's upload as NDJSON — one {"index":n,"fields":[...]} object
    /// per CSV line — parsing through pooled buffers and flushing to the response
    /// PipeWriter incrementally. Honors client disconnects via RequestAborted.
    /// </summary>
    private static async Task StreamRowsAsync(
        Guid id,
        IJobStore store,
        IFileStorage storage,
        HttpContext http,
        CancellationToken ct)
    {
        var job = store.Get(id);
        if (job is null)
        {
            http.Response.StatusCode = StatusCodes.Status404NotFound;
            await http.Response.WriteAsJsonAsync(new { error = "job not found" }, ct);
            return;
        }

        if (job.Type != "csv" || job.UploadPath is null || !storage.Exists(job.UploadPath))
        {
            http.Response.StatusCode = StatusCodes.Status409Conflict;
            await http.Response.WriteAsJsonAsync(
                new { error = "row streaming requires a csv job with an available upload" }, ct);
            return;
        }

        http.Response.ContentType = "application/x-ndjson";
        var writer = http.Response.BodyWriter;

        await using var upload = storage.OpenRead(job.UploadPath);
        await using var json = new Utf8JsonWriter(writer);
        var buffered = 0L;

        await foreach (var row in CsvLineReader.ReadRowsAsync(upload, ct: ct))
        {
            json.WriteStartObject();
            json.WriteNumber("index", row.Index);
            json.WriteStartArray("fields");
            foreach (var field in row.Fields)
            {
                json.WriteStringValue(field);
            }

            json.WriteEndArray();
            json.WriteEndObject();

            // NDJSON: commit the object into the pipe, append the record
            // separator straight into the pipe buffer, then reset the writer
            // so every line is a fresh root document.
            buffered += json.BytesPending + 1;
            await json.FlushAsync(ct);
            var newline = writer.GetMemory(1);
            newline.Span[0] = (byte)'\n';
            writer.Advance(1);
            json.Reset();

            // FlushAsync awaits when the pipe hits its pause threshold —
            // that is the backpressure point for slow clients.
            if (buffered > 16 * 1024)
            {
                await writer.FlushAsync(ct);
                buffered = 0;
            }
        }

        await writer.FlushAsync(ct);
    }

    /// <summary>
    /// Streams the result file straight from disk — the payload never lands in
    /// a byte[] on the server. 409 while the job is not Succeeded yet.
    /// </summary>
    private static IResult GetResultAsync(Guid id, IJobStore store, IFileStorage storage)
    {
        var job = store.Get(id);
        if (job is null)
        {
            return Results.NotFound();
        }

        if (job.Status != JobStatus.Succeeded || job.ResultPath is null)
        {
            return Results.Conflict(new { error = "result is not available yet", status = job.Status.ToString() });
        }

        if (!storage.Exists(job.ResultPath))
        {
            return Results.Conflict(new { error = "result file is missing (expired?)", status = job.Status.ToString() });
        }

        var stream = storage.OpenRead(job.ResultPath);
        var downloadName = $"{id:N}{Path.GetExtension(job.ResultPath)}";
        return Results.File(stream, job.ResultContentType ?? "application/octet-stream", downloadName);
    }

    private static async Task<IResult> UploadAsync(
        HttpRequest request,
        IJobStore store,
        IJobQueue queue,
        IFileStorage storage,
        CancellationToken ct)
    {
        var id = Guid.NewGuid();
        var partPath = storage.CreateUploadPath(id, ".part");

        MultipartStreamReader.UploadResult? uploaded;
        await using (var upload = storage.OpenWrite(partPath))
        {
            try
            {
                uploaded = await MultipartStreamReader.ReadAsync(request, upload, UploadCapBytes, ct);
            }
            catch (PayloadTooLargeException ex)
            {
                storage.Delete(partPath);
                return Results.Problem(
                    statusCode: StatusCodes.Status413PayloadTooLarge,
                    title: "Payload too large",
                    detail: ex.Message);
            }
        }

        if (uploaded is null)
        {
            storage.Delete(partPath);
            return Results.BadRequest(new { error = "expected multipart/form-data containing a file part" });
        }

        if (!uploaded.Fields.TryGetValue("type", out var rawType) || !AllowedTypes.Contains(rawType.Trim()))
        {
            storage.Delete(partPath);
            return Results.BadRequest(new
            {
                error = $"field 'type' is required and must be one of: {string.Join(", ", AllowedTypes)}",
            });
        }

        if (uploaded.Fields.TryGetValue("params", out var rawParams) && !string.IsNullOrWhiteSpace(rawParams))
        {
            try
            {
                using var _ = JsonDocument.Parse(rawParams);
            }
            catch (JsonException)
            {
                storage.Delete(partPath);
                return Results.BadRequest(new { error = "field 'params' must be valid JSON" });
            }
        }

        var type = rawType.Trim().ToLowerInvariant();
        var extension = AllowedExtensions.FirstOrDefault(e =>
            uploaded.FileName is not null &&
            uploaded.FileName.EndsWith(e, StringComparison.OrdinalIgnoreCase)) ?? ".dat";

        var uploadPath = storage.CreateUploadPath(id, extension);
        storage.Move(partPath, uploadPath);

        var record = new JobRecord
        {
            Id = id,
            Type = type,
            Params = uploaded.Fields.GetValueOrDefault("params"),
            UploadPath = uploadPath,
            UploadSize = uploaded.BytesWritten,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        store.Add(record);

        if (!queue.TryEnqueue(new JobLease(id, type)))
        {
            // Backpressure: queue saturated — refuse work instead of buffering it.
            store.TryRemove(id);
            storage.Delete(uploadPath);
            return QueueFull();
        }

        return Results.Accepted($"/api/v1/jobs/{id}/status", ToDto(record));
    }

    private static IResult QueueFull() => new QueueFullResult();

    private sealed class QueueFullResult : IResult
    {
        public Task ExecuteAsync(HttpContext httpContext)
        {
            httpContext.Response.Headers.RetryAfter = "5";
            httpContext.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            return httpContext.Response.WriteAsJsonAsync(
                new { error = "processing queue is full; retry later" },
                httpContext.RequestAborted);
        }
    }

    internal static JobStatusDto ToDto(JobRecord j) => new(
        j.Id,
        j.Type,
        j.Status.ToString(),
        Progress(j.Status),
        j.UploadSize,
        j.AllocatedBytes,
        j.CreatedAt,
        j.StartedAt,
        j.FinishedAt,
        j.Error);

    private static int Progress(JobStatus status) => status switch
    {
        JobStatus.Succeeded => 100,
        JobStatus.Running => 50,
        _ => 0,
    };
}

public sealed record JobStatusDto(
    Guid Id,
    string Type,
    string Status,
    int Progress,
    long UploadSize,
    long AllocatedBytes,
    DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? FinishedAt,
    string? Error);
