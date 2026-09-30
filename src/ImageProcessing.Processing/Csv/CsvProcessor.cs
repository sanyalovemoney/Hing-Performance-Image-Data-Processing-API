using System.Buffers;
using System.Text.Json;
using ImageProcessing.Core.Jobs;
using ImageProcessing.Core.Processing;
using ImageProcessing.Core.Storage;

namespace ImageProcessing.Processing.Csv;

/// <summary>
/// CSV job pipeline: streams the upload line by line through
/// <see cref="CsvLineReader"/> and aggregates one numeric column with
/// <see cref="SpanCsvParser"/> — UTF-8 bytes are parsed to doubles directly,
/// so a full pass allocates nothing per row (p95 values go into a pooled,
/// doubling buffer). The result is a small JSON summary.
/// </summary>
public sealed class CsvProcessor : IJobProcessor
{
    private readonly IFileStorage _storage;

    public CsvProcessor(IFileStorage storage) => _storage = storage;

    public string Type => "csv";

    public async Task ProcessAsync(JobRecord job, CancellationToken ct)
    {
        if (job.UploadPath is null || !_storage.Exists(job.UploadPath))
        {
            throw new InvalidOperationException($"upload '{job.UploadPath}' is missing");
        }

        var parameters = CsvParamsParser.Parse(job.Params);
        var resultPath = _storage.CreateResultPath(job.Id, ".json");
        var before = GC.GetTotalAllocatedBytes(true);

        using var aggregator = new ColumnAggregator(parameters);
        await using (var input = _storage.OpenRead(job.UploadPath))
        {
            await CsvLineReader.ForEachLineAsync(input, aggregator.AddLine, ct).ConfigureAwait(false);
        }

        await using (var output = _storage.OpenWrite(resultPath))
        await using (var json = new Utf8JsonWriter(output))
        {
            aggregator.WriteResult(json);
        }

        job.ResultPath = resultPath;
        job.ResultContentType = "application/json";
        job.AllocatedBytes = (long)(GC.GetTotalAllocatedBytes(true) - before);
    }

    /// <summary>Running aggregate state for one column, optionally per group key.</summary>
    internal sealed class ColumnAggregator : IDisposable
    {
        private readonly CsvParams _p;
        private readonly Dictionary<string, GroupAccumulator>? _groups;
        private double[]? _values;
        private int _valueCap;
        private int _valueCount;
        private bool _first = true;
        private long _rows;
        private long _skipped;
        private double _sum;
        private double _min = double.PositiveInfinity;
        private double _max = double.NegativeInfinity;

        public ColumnAggregator(CsvParams parameters)
        {
            _p = parameters;
            if (parameters.GroupBy is not null)
            {
                _groups = new Dictionary<string, GroupAccumulator>(StringComparer.Ordinal);
            }
        }

        /// <summary>Line handler matching <see cref="CsvLineHandler"/> — fully synchronous.</summary>
        public ValueTask AddLine(ReadOnlyMemory<byte> memory)
        {
            if (_first)
            {
                _first = false;
                if (_p.SkipHeader)
                {
                    return default;
                }
            }

            var target = default(CsvField);
            var group = default(CsvField);
            var foundTarget = false;
            var foundGroup = false;
            var index = 0;
            foreach (var field in SpanCsvParser.Enumerate(memory.Span))
            {
                if (index == _p.Column)
                {
                    target = field;
                    foundTarget = true;
                }

                if (_p.GroupBy is { } g && index == g)
                {
                    group = field;
                    foundGroup = true;
                }

                index++;
                if (foundTarget && (foundGroup || _p.GroupBy is null))
                {
                    break;
                }
            }

            if (!foundTarget || !target.TryGetDouble(out var value))
            {
                _skipped++;
                return default;
            }

            _rows++;
            _sum += value;
            _min = Math.Min(_min, value);
            _max = Math.Max(_max, value);

            if (_p.Aggregate == CsvAggregate.P95)
            {
                AddValue(value);
            }

            if (_p.GroupBy is not null && foundGroup && _groups is not null)
            {
                var key = group.GetString();
                if (!_groups.TryGetValue(key, out var acc))
                {
                    acc = new GroupAccumulator();
                    _groups[key] = acc;
                }

                acc.Add(value);
            }

            return default;
        }

        public void WriteResult(Utf8JsonWriter json)
        {
            json.WriteStartObject();
            json.WriteNumber("column", _p.Column);
            json.WriteString("aggregate", _p.Aggregate.ToString().ToLowerInvariant());
            json.WriteNumber("rows", _rows);
            json.WriteNumber("skippedRows", _skipped);
            json.WriteNumber("value", Math.Round(ComputeValue(), 6));
            if (_groups is { Count: > 0 })
            {
                json.WriteStartObject("groups");
                foreach (var (key, acc) in _groups.OrderBy(kvp => kvp.Key, StringComparer.Ordinal))
                {
                    json.WriteNumber(key, Math.Round(acc.Finalize(_p.Aggregate), 6));
                }

                json.WriteEndObject();
            }

            json.WriteEndObject();
        }

        public void Dispose()
        {
            if (_values is not null)
            {
                ArrayPool<double>.Shared.Return(_values);
                _values = null;
            }
        }

        private double ComputeValue() => _p.Aggregate switch
        {
            CsvAggregate.Sum => _sum,
            CsvAggregate.Avg => _rows > 0 ? _sum / _rows : 0,
            CsvAggregate.Min => _rows > 0 ? _min : 0,
            CsvAggregate.Max => _rows > 0 ? _max : 0,
            CsvAggregate.Count => _rows,
            CsvAggregate.P95 => Percentile(0.95),
            _ => throw new NotSupportedException($"aggregate '{_p.Aggregate}' is not supported"),
        };

        private double Percentile(double q)
        {
            if (_values is null || _valueCount == 0)
            {
                return 0;
            }

            Array.Sort(_values, 0, _valueCount);
            var rank = (int)Math.Ceiling(q * _valueCount) - 1;
            return _values[Math.Clamp(rank, 0, _valueCount - 1)];
        }

        private void AddValue(double value)
        {
            if (_values is null)
            {
                _values = ArrayPool<double>.Shared.Rent(1024);
                _valueCap = 1024;
            }
            else if (_valueCount == _valueCap)
            {
                var newCap = checked(_valueCap * 2);
                var fresh = ArrayPool<double>.Shared.Rent(newCap);
                _values.AsSpan(0, _valueCount).CopyTo(fresh);
                ArrayPool<double>.Shared.Return(_values);
                _values = fresh;
                _valueCap = newCap;
            }

            _values[_valueCount++] = value;
        }
    }

    private sealed class GroupAccumulator
    {
        public double Sum { get; private set; }
        public long Count { get; private set; }
        public double Min { get; private set; } = double.PositiveInfinity;
        public double Max { get; private set; } = double.NegativeInfinity;

        public void Add(double value)
        {
            Sum += value;
            Count++;
            Min = Math.Min(Min, value);
            Max = Math.Max(Max, value);
        }

        public double Finalize(CsvAggregate aggregate) => aggregate switch
        {
            CsvAggregate.Sum => Sum,
            CsvAggregate.Avg => Count > 0 ? Sum / Count : 0,
            CsvAggregate.Min => Count > 0 ? Min : 0,
            CsvAggregate.Max => Count > 0 ? Max : 0,
            CsvAggregate.Count => Count,
            _ => throw new NotSupportedException($"aggregate '{aggregate}' is not supported for groups"),
        };
    }
}
