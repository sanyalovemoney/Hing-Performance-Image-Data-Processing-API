using System.Text.Json;

namespace ImageProcessing.Processing.Matrix;

/// <summary>Operation supported by <see cref="MatrixProcessor"/>.</summary>
public enum MatrixOp
{
    Transpose,
    Normalize,
    ColMean,
    Scale,
}

/// <summary>Validated matrix job parameters.</summary>
public readonly record struct MatrixParams(MatrixOp Op = MatrixOp.Transpose, float Scale = 1f);

/// <summary>
/// Parses the job's params JSON: {"op":"scale","scale":2.5}.
/// Throws <see cref="FormatException"/> on anything invalid.
/// </summary>
public static class MatrixParamsParser
{
    public static MatrixParams Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new MatrixParams();
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

            var op = MatrixOp.Transpose;
            var scale = 1f;

            if (root.TryGetProperty("op", out var opEl))
            {
                if (opEl.ValueKind != JsonValueKind.String ||
                    !Enum.TryParse<MatrixOp>(opEl.GetString(), ignoreCase: true, out op))
                {
                    throw new FormatException("op must be one of: transpose, normalize, colmean, scale");
                }
            }

            if (root.TryGetProperty("scale", out var scaleEl))
            {
                if (scaleEl.ValueKind != JsonValueKind.Number ||
                    !scaleEl.TryGetSingle(out scale) ||
                    !float.IsFinite(scale))
                {
                    throw new FormatException("scale must be a finite number");
                }
            }

            return new MatrixParams(op, scale);
        }
    }
}
