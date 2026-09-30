using System.Text.Json;

namespace ImageProcessing.Processing.Csv;

/// <summary>Aggregate supported by <see cref="CsvProcessor"/>.</summary>
public enum CsvAggregate
{
    Sum,
    Avg,
    Min,
    Max,
    Count,
    P95,
}

/// <summary>Validated CSV job parameters.</summary>
public readonly record struct CsvParams(
    int Column = 0,
    CsvAggregate Aggregate = CsvAggregate.Sum,
    int? GroupBy = null,
    bool SkipHeader = true);

/// <summary>
/// Parses the job's params JSON: {"column":1,"aggregate":"p95","groupBy":0,"skipHeader":true}.
/// Throws <see cref="FormatException"/> on anything invalid so the job lands in Failed
/// with a readable error instead of a 500 in the worker.
/// </summary>
public static class CsvParamsParser
{
    public static CsvParams Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new CsvParams();
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

            var column = 0;
            var aggregate = CsvAggregate.Sum;
            int? groupBy = null;
            var skipHeader = true;

            if (root.TryGetProperty("column", out var columnEl))
            {
                if (columnEl.ValueKind != JsonValueKind.Number || !columnEl.TryGetInt32(out column) || column < 0)
                {
                    throw new FormatException("column must be a non-negative integer");
                }
            }

            if (root.TryGetProperty("aggregate", out var aggregateEl))
            {
                if (aggregateEl.ValueKind != JsonValueKind.String ||
                    !Enum.TryParse<CsvAggregate>(aggregateEl.GetString(), ignoreCase: true, out aggregate))
                {
                    throw new FormatException(
                        "aggregate must be one of: sum, avg, min, max, count, p95");
                }
            }

            if (root.TryGetProperty("groupBy", out var groupEl))
            {
                if (groupEl.ValueKind != JsonValueKind.Number ||
                    !groupEl.TryGetInt32(out var group) || group < 0)
                {
                    throw new FormatException("groupBy must be a non-negative integer");
                }

                groupBy = group;
            }

            if (root.TryGetProperty("skipHeader", out var skipEl))
            {
                if (skipEl.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                {
                    throw new FormatException("skipHeader must be a boolean");
                }

                skipHeader = skipEl.GetBoolean();
            }

            if (groupBy is not null && aggregate == CsvAggregate.P95)
            {
                throw new FormatException("p95 with groupBy is not supported");
            }

            return new CsvParams(column, aggregate, groupBy, skipHeader);
        }
    }
}
