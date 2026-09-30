using System.Text.Json;

namespace ImageProcessing.Processing.Images;

public enum ImageFilter
{
    Grayscale = 0,
    Blur = 1,
    Threshold = 2,
    Resize = 3,
}

/// <summary>Immutable image job parameters parsed from the request JSON.</summary>
public readonly record struct ImageParams(
    ImageFilter Filter = ImageFilter.Grayscale,
    int? Width = null,
    int? Height = null,
    int? MaxDimension = null,
    byte ThresholdLevel = 128);

/// <summary>
/// Lenient parser: unknown/invalid input throws <see cref="FormatException"/>
/// so the worker records a meaningful job failure.
/// </summary>
public static class ImageParamsParser
{
    public static ImageParams Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return default;
        }

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            throw new FormatException($"params is not valid JSON: {ex.Message}", ex);
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new FormatException("params must be a JSON object");
            }

            var filter = ImageFilter.Grayscale;
            if (root.TryGetProperty("filter", out var filterElement))
            {
                var filterName = filterElement.ValueKind == JsonValueKind.String
                    ? filterElement.GetString()
                    : null;
                if (filterName is null ||
                    !Enum.TryParse<ImageFilter>(filterName, ignoreCase: true, out filter))
                {
                    throw new FormatException($"unknown filter '{filterName}'");
                }
            }

            var width = GetPositiveInt(root, "width");
            var height = GetPositiveInt(root, "height");
            var maxDimension = GetPositiveInt(root, "maxDimension");

            byte thresholdLevel = 128;
            if (root.TryGetProperty("thresholdLevel", out var level) && level.ValueKind == JsonValueKind.Number)
            {
                if (!level.TryGetByte(out thresholdLevel))
                {
                    throw new FormatException("thresholdLevel must be 0..255");
                }
            }

            return new ImageParams(filter, width, height, maxDimension, thresholdLevel);
        }
    }

    private static int? GetPositiveInt(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var element) || element.ValueKind != JsonValueKind.Number)
        {
            return null;
        }

        if (!element.TryGetInt32(out var value) || value <= 0)
        {
            throw new FormatException($"{name} must be a positive integer");
        }

        return value;
    }
}
