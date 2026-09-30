using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace ImageProcessing.Tests.Api;

/// <summary>
/// In-process host with the storage root redirected to a per-run temp dir,
/// so tests never touch the repository's ./storage.
/// </summary>
public sealed class JobsApiFactory : WebApplicationFactory<Program>
{
    public string StorageRoot { get; } =
        Path.Combine(Path.GetTempPath(), "imgproc-tests-" + Guid.NewGuid().ToString("N"));

    protected override void ConfigureWebHost(IWebHostBuilder builder) =>
        builder.UseSetting("Storage:Root", StorageRoot);

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            try
            {
                Directory.Delete(StorageRoot, recursive: true);
            }
            catch (IOException)
            {
                // Best effort — leftover temp dirs are cleaned by the OS.
            }
            catch (UnauthorizedAccessException)
            {
                // Best effort.
            }
        }
    }
}
