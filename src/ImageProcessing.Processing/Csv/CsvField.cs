using System.Buffers;
using System.Globalization;
using System.Text;

namespace ImageProcessing.Processing.Csv;

/// <summary>
/// One CSV field as a span slice plus quoting metadata — zero allocations.
/// Numeric accessors parse UTF-8 bytes directly (no intermediate string);
/// <see cref="GetString"/> is the only materializing operation.
/// </summary>
public readonly ref struct CsvField
{
    /// <summary>Field content; outer quotes already stripped, inner "" kept as-is.</summary>
    public ReadOnlySpan<byte> Bytes { get; }

    public bool WasQuoted { get; }

    /// <summary>True when the quoted content contains escaped "" pairs.</summary>
    public bool HasEscapedQuotes { get; }

    internal CsvField(ReadOnlySpan<byte> bytes, bool wasQuoted, bool hasEscapedQuotes)
    {
        Bytes = bytes;
        WasQuoted = wasQuoted;
        HasEscapedQuotes = hasEscapedQuotes;
    }

    // Invariant culture is mandatory: the span overloads default to
    // CurrentCulture, and CSV payloads use '.' decimals regardless of locale.
    public bool TryGetInt32(out int value)
    {
        value = 0;
        return !HasEscapedQuotes &&
               int.TryParse(Bytes, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }

    public bool TryGetDouble(out double value)
    {
        value = 0;
        return !HasEscapedQuotes &&
               double.TryParse(Bytes, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    /// <summary>Decodes to string, unescaping "" pairs through a pooled buffer.</summary>
    public string GetString() =>
        HasEscapedQuotes ? UnescapeAndDecode() : Encoding.UTF8.GetString(Bytes);

    private string UnescapeAndDecode()
    {
        var buffer = ArrayPool<byte>.Shared.Rent(Bytes.Length);
        try
        {
            var w = 0;
            for (var i = 0; i < Bytes.Length; i++)
            {
                if (Bytes[i] == (byte)'"' && i + 1 < Bytes.Length && Bytes[i + 1] == (byte)'"')
                {
                    buffer[w++] = (byte)'"';
                    i++;
                }
                else
                {
                    buffer[w++] = Bytes[i];
                }
            }

            return Encoding.UTF8.GetString(buffer, 0, w);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}

/// <summary>
/// Single-pass enumerator over the fields of one CSV line.
/// Handles quoted fields, escaped "" pairs, empty fields and trailing separators.
/// </summary>
public ref struct CsvFieldEnumerator
{
    private readonly ReadOnlySpan<byte> _line;
    private readonly byte _separator;
    private CsvField _current;
    private int _pos;
    private bool _done;

    internal CsvFieldEnumerator(ReadOnlySpan<byte> line, byte separator)
    {
        _line = line;
        _separator = separator;
        _pos = 0;
        _done = false;
        _current = default;
    }

    public readonly CsvFieldEnumerator GetEnumerator() => this;

    public readonly CsvField Current => _current;

    public bool MoveNext()
    {
        if (_done)
        {
            return false;
        }

        var span = _line;
        var start = _pos;
        if (start > span.Length)
        {
            _done = true;
            return false;
        }

        ReadOnlySpan<byte> content = default;
        var end = start;
        var wasQuoted = false;
        var escaped = false;

        if (start < span.Length && span[start] == (byte)'"')
        {
            wasQuoted = true;
            var i = start + 1;
            var contentStart = i;
            var closed = false;
            while (i < span.Length)
            {
                if (span[i] == (byte)'"')
                {
                    if (i + 1 < span.Length && span[i + 1] == (byte)'"')
                    {
                        escaped = true;
                        i += 2;
                        continue;
                    }

                    content = span.Slice(contentStart, i - contentStart);
                    end = i + 1;
                    closed = true;
                    break;
                }

                i++;
            }

            if (closed)
            {
                // Leniently skip garbage between the closing quote and the separator.
                while (end < span.Length && span[end] != _separator)
                {
                    end++;
                }
            }
            else
            {
                // Unterminated quote: consume the rest of the line.
                content = span.Slice(contentStart);
                end = span.Length;
            }
        }
        else
        {
            var sep = span.Slice(start).IndexOf(_separator);
            end = sep < 0 ? span.Length : start + sep;
            content = span.Slice(start, end - start);
        }

        if (end < span.Length)
        {
            _pos = end + 1;
        }
        else
        {
            _done = true;
        }

        _current = new CsvField(content, wasQuoted, escaped);
        return true;
    }
}

/// <summary>
/// Allocation-free CSV line parsing over <see cref="ReadOnlySpan{T}"/> of UTF-8 bytes.
/// </summary>
public static class SpanCsvParser
{
    public static CsvFieldEnumerator Enumerate(ReadOnlySpan<byte> line, byte separator = (byte)',') =>
        new(line, separator);

    public static int CountFields(ReadOnlySpan<byte> line, byte separator = (byte)',')
    {
        var count = 0;
        foreach (var _ in Enumerate(line, separator))
        {
            count++;
        }

        return count;
    }

    /// <summary>
    /// Materializes a line into strings — the only allocating path, used where
    /// rows leave the process (NDJSON streaming).
    /// </summary>
    public static CsvRow Materialize(ReadOnlyMemory<byte> line, int index, byte separator = (byte)',')
    {
        var span = line.Span;
        var fields = new string[CountFields(span, separator)];
        var i = 0;
        foreach (var field in Enumerate(span, separator))
        {
            fields[i++] = field.GetString();
        }

        return new CsvRow(index, fields);
    }
}
