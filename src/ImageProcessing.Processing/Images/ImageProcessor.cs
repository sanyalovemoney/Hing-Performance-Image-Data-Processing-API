using ImageProcessing.Core.Jobs;
using ImageProcessing.Core.Processing;
using ImageProcessing.Core.Storage;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace ImageProcessing.Processing.Images;

/// <summary>
/// Image job pipeline: decode once into Image&lt;Bgra32&gt;, mutate pixel rows
/// in place through <see cref="PixelSpanFilters"/>, encode straight to the
/// result file. The only large allocations are ImageSharp's own pixel buffer
/// (one per job) and its encoder internals — nothing per row or per filter pass.
/// </summary>
public sealed class ImageProcessor : IJobProcessor
{
    private readonly IFileStorage _storage;

    public ImageProcessor(IFileStorage storage) => _storage = storage;

    public string Type => "image";

    public async Task ProcessAsync(JobRecord job, CancellationToken ct)
    {
        if (job.UploadPath is null || !_storage.Exists(job.UploadPath))
        {
            throw new InvalidOperationException($"upload '{job.UploadPath}' is missing");
        }

        var parameters = ImageParamsParser.Parse(job.Params);
        var resultPath = _storage.CreateResultPath(job.Id, ".png");
        var before = GC.GetTotalAllocatedBytes(true);

        await using var input = _storage.OpenRead(job.UploadPath);
        using var image = await Image.LoadAsync<Bgra32>(input, ct).ConfigureAwait(false);
        ApplyFilter(image, parameters);

        await using var output = _storage.OpenWrite(resultPath);
        await image.SaveAsPngAsync(output, ct).ConfigureAwait(false);

        job.ResultPath = resultPath;
        job.ResultContentType = "image/png";
        job.AllocatedBytes = (long)(GC.GetTotalAllocatedBytes(true) - before);
    }

    private static void ApplyFilter(Image<Bgra32> image, ImageParams parameters)
    {
        switch (parameters.Filter)
        {
            case ImageFilter.Grayscale:
                PixelSpanFilters.GrayscaleInPlace(image);
                break;
            case ImageFilter.Blur:
                PixelSpanFilters.BoxBlur3x3InPlace(image);
                break;
            case ImageFilter.Threshold:
                PixelSpanFilters.ThresholdInPlace(image, parameters.ThresholdLevel);
                break;
            case ImageFilter.Resize:
                var (width, height) = ComputeTargetSize(image, parameters);
                image.Mutate(ctx => ctx.Resize(width, height));
                break;
            default:
                throw new FormatException($"unsupported filter '{parameters.Filter}'");
        }
    }

    private static (int Width, int Height) ComputeTargetSize(Image<Bgra32> image, ImageParams parameters)
    {
        if (parameters.MaxDimension is { } max)
        {
            var longest = Math.Max(image.Width, image.Height);
            if (longest <= max)
            {
                return (image.Width, image.Height);
            }

            var scale = (double)max / longest;
            return (
                (int)Math.Round(image.Width * scale),
                (int)Math.Round(image.Height * scale));
        }

        var width = parameters.Width ?? image.Width;
        var height = parameters.Height ?? image.Height;
        if (width <= 0 || height <= 0)
        {
            throw new FormatException("resize requires positive width/height or maxDimension");
        }

        return (width, height);
    }
}
