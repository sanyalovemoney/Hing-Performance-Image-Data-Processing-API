using System.Text;
using ImageProcessing.Processing.Csv;

namespace ImageProcessing.Tests.Processing;

public sealed class CsvParserTests
{
    private static string[] FieldsOf(string line, byte separator = (byte)',')
    {
        var bytes = Encoding.UTF8.GetBytes(line);
        var fields = new List<string>();
        foreach (var field in SpanCsvParser.Enumerate(bytes, separator))
        {
            fields.Add(field.GetString());
        }

        return fields.ToArray();
    }

    [Fact]
    public void Splits_plain_fields()
    {
        Assert.Equal(new[] { "a", "b", "c" }, FieldsOf("a,b,c"));
    }

    [Fact]
    public void Quoted_field_may_contain_separator()
    {
        Assert.Equal(new[] { "a,b", "c" }, FieldsOf("\"a,b\",c"));
    }

    [Fact]
    public void Escaped_quotes_are_unescaped()
    {
        Assert.Equal(new[] { "a\"b", "c" }, FieldsOf("\"a\"\"b\",c"));
    }

    [Fact]
    public void Empty_fields_are_preserved()
    {
        Assert.Equal(new[] { "a", "", "b" }, FieldsOf("a,,b"));
        Assert.Equal(new[] { "", "a" }, FieldsOf(",a"));
        Assert.Equal(new[] { "a", "" }, FieldsOf("a,"));
        Assert.Equal(new[] { "", "" }, FieldsOf(","));
    }

    [Fact]
    public void Numeric_fields_parse_straight_from_utf8_bytes()
    {
        var bytes = Encoding.UTF8.GetBytes("42,-7,3.14,1e3");
        var index = 0;
        foreach (var field in SpanCsvParser.Enumerate(bytes))
        {
            switch (index)
            {
                case 0:
                    Assert.True(field.TryGetInt32(out var i0));
                    Assert.Equal(42, i0);
                    break;
                case 1:
                    Assert.True(field.TryGetInt32(out var i1));
                    Assert.Equal(-7, i1);
                    break;
                case 2:
                    Assert.True(field.TryGetDouble(out var d2));
                    Assert.Equal(3.14, d2, 10);
                    break;
                case 3:
                    Assert.True(field.TryGetDouble(out var d3));
                    Assert.Equal(1000, d3);
                    break;
            }

            index++;
        }

        Assert.Equal(4, index);
    }

    [Fact]
    public void Int_field_still_parses_as_double()
    {
        var bytes = Encoding.UTF8.GetBytes("42");
        foreach (var field in SpanCsvParser.Enumerate(bytes))
        {
            Assert.True(field.TryGetDouble(out var d));
            Assert.Equal(42, d);
        }
    }

    [Fact]
    public void Quoted_numeric_field_parses()
    {
        var bytes = Encoding.UTF8.GetBytes("\"42\",\"2.5\"");
        var seen = 0;
        foreach (var field in SpanCsvParser.Enumerate(bytes))
        {
            Assert.True(field.WasQuoted);
            Assert.False(field.HasEscapedQuotes);
            if (seen == 0)
            {
                Assert.True(field.TryGetInt32(out var i));
                Assert.Equal(42, i);
            }
            else
            {
                Assert.True(field.TryGetDouble(out var d));
                Assert.Equal(2.5, d);
            }

            seen++;
        }

        Assert.Equal(2, seen);
    }

    [Fact]
    public void Non_numeric_field_fails_tryparse()
    {
        var bytes = Encoding.UTF8.GetBytes("abc");
        foreach (var field in SpanCsvParser.Enumerate(bytes))
        {
            Assert.False(field.TryGetInt32(out _));
            Assert.False(field.TryGetDouble(out _));
        }
    }

    [Fact]
    public void Garbage_after_closing_quote_is_ignored()
    {
        Assert.Equal(new[] { "a", "b" }, FieldsOf("\"a\"junk,b"));
    }

    [Fact]
    public void Unterminated_quote_consumes_rest_of_line()
    {
        var bytes = Encoding.UTF8.GetBytes("\"abc");
        foreach (var field in SpanCsvParser.Enumerate(bytes))
        {
            Assert.True(field.WasQuoted);
            Assert.Equal("abc", field.GetString());
        }
    }

    [Fact]
    public void Unicode_fields_decode_correctly()
    {
        Assert.Equal(new[] { "привіт", "світ" }, FieldsOf("привіт,світ"));
        Assert.Equal(new[] { "є\"дата" }, FieldsOf("\"є\"\"дата\""));
    }

    [Fact]
    public void Custom_separator_is_honored()
    {
        Assert.Equal(new[] { "a", "b" }, FieldsOf("a;b", (byte)';'));
        // A comma is literal inside a semicolon-separated line.
        Assert.Equal(new[] { "a,b", "c" }, FieldsOf("a,b;c", (byte)';'));
    }

    [Theory]
    [InlineData("", 1)]            // degenerate: one empty field (reader skips empty lines earlier)
    [InlineData("a", 1)]
    [InlineData("a,b,c", 3)]
    [InlineData("a,", 2)]
    [InlineData("\"a,b\",c", 2)]
    [InlineData(",,", 3)]
    public void CountFields_matches_enumeration(string line, int expected)
    {
        var bytes = Encoding.UTF8.GetBytes(line);
        Assert.Equal(expected, SpanCsvParser.CountFields(bytes));
        Assert.Equal(expected, FieldsOf(line).Length);
    }

    [Fact]
    public void Materialize_decodes_all_fields_with_index()
    {
        var bytes = Encoding.UTF8.GetBytes("\"a\"\"b\",c,привіт");
        var row = SpanCsvParser.Materialize(bytes, 7);
        Assert.Equal(7, row.Index);
        Assert.Equal(new[] { "a\"b", "c", "привіт" }, row.Fields);
    }
}
