using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using ImageProcessing.Api.Endpoints;

namespace ImageProcessing.Tests.Api;

public sealed class JobsEndpointsTests : IClassFixture<JobsApiFactory>
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);
    private readonly JobsApiFactory _factory;

    public JobsEndpointsTests(JobsApiFactory factory) => _factory = factory;

    /// <summary>1x1 px black PNG — smallest valid image payload.</summary>
    private static byte[] TinyPng() => Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");

    private static MultipartFormDataContent MakeForm(string? type = "echo", string fileName = "pixel.png")
    {
        var form = new MultipartFormDataContent();
        var content = new ByteArrayContent(TinyPng());
        content.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        form.Add(content, "file", fileName);
        if (type is not null)
        {
            form.Add(new StringContent(type), "type");
        }

        return form;
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
}
