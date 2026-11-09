using ImageProcessing.Api.Endpoints;
using ImageProcessing.Core.Configuration;
using ImageProcessing.Core.Jobs;
using ImageProcessing.Core.Processing;
using ImageProcessing.Core.Queue;
using ImageProcessing.Core.Storage;
using ImageProcessing.Processing.Csv;
using ImageProcessing.Processing.Images;
using ImageProcessing.Processing.Matrix;
using ImageProcessing.Workers;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

// Limits and reliability knobs — bound once up front so Kestrel and the queue
// can be configured before the container is built; the same section is also
// registered as validated options for everyone else (fail fast on bad config).
var processing = new ProcessingOptions();
builder.Configuration.GetSection(ProcessingOptions.SectionName).Bind(processing);

builder.Services.AddOptions<ProcessingOptions>()
    .Bind(builder.Configuration.GetSection(ProcessingOptions.SectionName))
    .ValidateOnStart();
builder.Services.AddSingleton<IValidateOptions<ProcessingOptions>, ProcessingOptionsValidator>();

// Kestrel rejects oversized bodies at the socket level with the configured margin,
// but the app-level 413 JSON (MultipartStreamReader) fires first for anything
// just over MaxUploadBytes.
builder.WebHost.ConfigureKestrel(kestrel =>
    kestrel.Limits.MaxRequestBodySize = processing.MaxUploadBytes + 64 * 1024);

// Storage root: relative paths resolve against the content root.
var storageRoot = builder.Configuration["Storage:Root"] ?? "storage";
if (!Path.IsPathRooted(storageRoot))
{
    storageRoot = Path.Combine(builder.Environment.ContentRootPath, storageRoot);
}

builder.Services.AddSingleton<IJobStore, InMemoryJobStore>();
builder.Services.AddSingleton<JobCancellationRegistry>();
builder.Services.AddSingleton<IJobQueue>(_ => new ChannelJobQueue(processing.QueueCapacity));
builder.Services.AddSingleton<IFileStorage>(_ => new TempFileStorage(storageRoot));
builder.Services.AddSingleton(sp =>
{
    var storage = sp.GetRequiredService<IFileStorage>();
    var registry = new ProcessorRegistry();
    registry.Register(new ImageProcessor(storage));
    registry.Register(new CsvProcessor(storage));
    registry.Register(new MatrixProcessor(storage));
    registry.RegisterFallback(new EchoProcessor(storage));
    return registry;
});
builder.Services.AddJobWorkers();

var app = builder.Build();

// Static frontend (wwwroot).
app.UseDefaultFiles();
app.UseStaticFiles();

// Liveness probe — always OK once the process is up.
app.MapGet("/healthz", () => Results.Ok(new { status = "ok" }));

// Readiness probe — 503 once the queue reaches the high-watermark so the load
// balancer stops sending work before the bounded queue actually overflows.
app.MapGet("/readyz", (IJobQueue queue, IOptions<ProcessingOptions> options) =>
{
    var highWatermark = (int)Math.Ceiling(queue.Capacity * options.Value.QueueHighWatermark);
    var queued = queue.Count;
    return queued >= highWatermark
        ? Results.Json(
            new { status = "overloaded", queued, capacity = queue.Capacity },
            statusCode: StatusCodes.Status503ServiceUnavailable)
        : Results.Ok(new { status = "ready", queued, capacity = queue.Capacity });
});

app.MapJobsEndpoints();

app.Run();

/// <summary>Marker for WebApplicationFactory in integration tests.</summary>
public partial class Program
{
}
