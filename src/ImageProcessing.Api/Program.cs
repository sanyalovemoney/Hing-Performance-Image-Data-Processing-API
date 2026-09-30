var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

// Liveness probe — always OK once the process is up.
app.MapGet("/healthz", () => Results.Ok(new { status = "ok" }));

app.Run();
