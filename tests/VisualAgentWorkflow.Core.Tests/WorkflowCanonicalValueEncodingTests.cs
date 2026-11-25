using System.Text;
using System.Text.Json;
using VisualAgentWorkflow.Core;
using Xunit;

namespace VisualAgentWorkflow.Core.Tests;

public sealed class WorkflowCanonicalValueEncodingTests
{
    private static string Write(Action<Utf8JsonWriter> action)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream)) action(writer);
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    [Theory]
    [InlineData(0d, "0")]
    [InlineData(1d, "1")]
    [InlineData(-1.5d, "-1.5")]
    [InlineData(0.1d, "0.1")]
    [InlineData(1e21d, "1E+21")]
    [InlineData(double.MaxValue, "1.7976931348623157E+308")]
    [InlineData(double.Epsilon, "5E-324")]
    [InlineData(-double.MaxValue, "-1.7976931348623157E+308")]
    public void Number_spelling_is_pinned(double value, string expected)
    {
        Assert.Equal(expected, WorkflowCanonicalValueEncoding.FormatNumber(value));
        Assert.Equal(expected, Write(w => WorkflowCanonicalValueEncoding.WriteNumber(w, value)));
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void Non_finite_numbers_are_rejected(double value)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => WorkflowCanonicalValueEncoding.FormatNumber(value));
        Assert.False(WorkflowCanonicalValueEncoding.TryFormatNumber(value, out _));
    }

    [Fact]
    public void Negative_zero_bytes_equal_zero_bytes()
    {
        Assert.Equal(
            Encoding.UTF8.GetBytes(WorkflowCanonicalValueEncoding.FormatNumber(0d)),
            Encoding.UTF8.GetBytes(WorkflowCanonicalValueEncoding.FormatNumber(-0d)));
    }

    [Fact]
    public void Integer_boundaries_are_pinned()
    {
        Assert.Equal("-9223372036854775808", Write(w => WorkflowCanonicalValueEncoding.WriteInteger(w, long.MinValue)));
        Assert.Equal("9223372036854775807", Write(w => WorkflowCanonicalValueEncoding.WriteInteger(w, long.MaxValue)));
        Assert.Equal("0", Write(w => WorkflowCanonicalValueEncoding.WriteInteger(w, 0)));
    }

    [Fact]
    public void Booleans_and_strings_are_pinned()
    {
        Assert.Equal("true", Write(w => WorkflowCanonicalValueEncoding.WriteBoolean(w, true)));
        Assert.Equal("false", Write(w => WorkflowCanonicalValueEncoding.WriteBoolean(w, false)));
        Assert.Equal("\"a\\u0022b\\\\\\n\"", Write(w => WorkflowCanonicalValueEncoding.WriteString(w, "a\"b\\\n")));
        Assert.Equal("\"\\u00E9\"", Write(w => WorkflowCanonicalValueEncoding.WriteString(w, "\u00e9")));
        Assert.Equal("\"e\\u0301\"", Write(w => WorkflowCanonicalValueEncoding.WriteString(w, "e\u0301")));
    }
}
