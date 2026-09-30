using ImageProcessing.Api.Endpoints;
using ImageProcessing.Core.Jobs;
using ImageProcessing.Core.Processing;
using ImageProcessing.Core.Queue;
using ImageProcessing.Core.Storage;
using ImageProcessing.Processing.Csv;
using ImageProcessing.Processing.Images;
using ImageProcessing.Processing.Matrix;
using ImageProcessing.Workers;

var builder = WebApplication.CreateBuilder(args);

// Storage root: relative paths resolve against the content root.
var storageRoot = builder.Configuration["Storage:Root"] ?? "storage";
if (!Path.IsPathRooted(storageRoot))
{
    storageRoot = Path.Combine(builder.Environment.ContentRootPath, storageRoot);
}

builder.Services.AddSingleton<IJobStore, InMemoryJobStore>();
builder.Services.AddSingleton<IJobQueue>(_ => new ChannelJobQueue(capacity: 128));
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

// Liveness probe — always OK once the process is up.
app.MapGet("/healthz", () => Results.Ok(new { status = "ok" }));

app.MapJobsEndpoints();

app.Run();

/// <summary>Marker for WebApplicationFactory in integration tests.</summary>
public partial class Program
{
}
