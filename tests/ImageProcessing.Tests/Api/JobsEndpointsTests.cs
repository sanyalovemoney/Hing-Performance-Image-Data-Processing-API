using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
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
