using System.Text;
using VisualAgentWorkflow.Core;
using Xunit;

namespace VisualAgentWorkflow.Core.Tests;

/// <summary>
/// Golden-byte pins for the canonical typed-array encoding. These expectations were captured from the
/// behavior that existed before the shared <see cref="WorkflowCanonicalValueEncoding"/> extraction and
/// must stay byte-identical, because normalized arrays feed stored definitions and content hashes.
/// </summary>
public sealed class WorkflowArrayCanonicalBytesGoldenTests
{
    private static string Normalize(string type, string json)
    {
        Assert.True(WorkflowArrayLiteral.TryNormalizeValue(type, json, out var normalized, out var error), error);
        return normalized;
    }

    [Theory]
    [InlineData("[0]", "[0]")]
    [InlineData("[-0]", "[0]")]
    [InlineData("[0.0]", "[0]")]
    [InlineData("[-0.0]", "[0]")]
    [InlineData("[1.0]", "[1]")]
    [InlineData("[1.50]", "[1.5]")]
    [InlineData("[0.1]", "[0.1]")]
    [InlineData("[1e21]", "[1E+21]")]
    [InlineData("[1.7976931348623157e308]", "[1.7976931348623157E+308]")]
    [InlineData("[5e-324]", "[5E-324]")]
    [InlineData("[-2e3]", "[-2000]")]
    [InlineData("[123456789012345678]", "[1.2345678901234568E+17]")]
    public void Numbers_use_zero_collapse_and_round_trip_spelling(string input, string expected)
    {
        Assert.Equal(expected, Normalize("number", input));
    }

    [Fact]
    public void Negative_zero_and_zero_have_identical_bytes()
    {
        Assert.Equal(
            Encoding.UTF8.GetBytes(Normalize("number", "[0]")),
            Encoding.UTF8.GetBytes(Normalize("number", "[-0.0]")));
    }

    [Theory]
    [InlineData("[1e999]")]
    [InlineData("[-1e999]")]
    public void Non_finite_numbers_are_rejected(string input)
    {
        Assert.False(WorkflowArrayLiteral.TryNormalizeValue("number", input, out _, out _));
    }

    [Theory]
    [InlineData("[\"NaN\"]")]
    [InlineData("[NaN]")]
    [InlineData("[Infinity]")]
    public void Non_finite_literals_are_not_numbers(string input)
    {
        Assert.False(WorkflowArrayLiteral.TryNormalizeValue("number", input, out _, out _));
    }

    [Fact]
    public void Integers_cover_the_signed_64_bit_range()
    {
        Assert.Equal("[-9223372036854775808,-1,0,9223372036854775807]",
            Normalize("integer", "[-9223372036854775808,-1,0,9223372036854775807]"));
        Assert.False(WorkflowArrayLiteral.TryNormalizeValue("integer", "[9223372036854775808]", out _, out _));
        Assert.False(WorkflowArrayLiteral.TryNormalizeValue("integer", "[-9223372036854775809]", out _, out _));
        Assert.False(WorkflowArrayLiteral.TryNormalizeValue("integer", "[1.0]", out _, out _));
    }

    [Fact]
    public void Booleans_are_lowercase_literals()
    {
        Assert.Equal("[true,false,true]", Normalize("boolean", "[ true , false,\n true ]"));
    }

    [Fact]
    public void Insignificant_whitespace_is_removed()
    {
        Assert.Equal("[\"a\",\"b\"]", Normalize("string", " [ \"a\" ,\n \"b\" ] "));
    }

    [Theory]
    [InlineData("[\"a\\\"b\"]", "[\"a\\u0022b\"]")]
    [InlineData("[\"a\\\\b\"]", "[\"a\\\\b\"]")]
    [InlineData("[\"a\\nb\"]", "[\"a\\nb\"]")]
    [InlineData("[\"a\\tb\"]", "[\"a\\tb\"]")]
    [InlineData("[\"\\u0001\"]", "[\"\\u0001\"]")]
    [InlineData("[\"a/b\"]", "[\"a/b\"]")]
    [InlineData("[\"<&>'+\"]", "[\"\\u003C\\u0026\\u003E\\u0027\\u002B\"]")]
    public void String_escapes_use_the_default_json_writer_encoder(string input, string expected)
    {
        Assert.Equal(expected, Normalize("string", input));
    }

    [Fact]
    public void Non_ascii_text_is_escaped_without_unicode_normalization()
    {
        // No NFC/NFD normalization is applied: precomposed and decomposed e-acute stay distinct values.
        var precomposed = Normalize("string", "[\"\\u00e9\"]");
        var decomposed = Normalize("string", "[\"e\\u0301\"]");
        Assert.Equal("[\"\\u00E9\"]", precomposed);
        Assert.Equal("[\"e\\u0301\"]", decomposed);
        Assert.NotEqual(precomposed, decomposed);
        Assert.Equal("[\"\\uD83D\\uDE00\"]", Normalize("string", "[\"\\ud83d\\ude00\"]"));
        Assert.Equal("[\"\\uD83D\\uDE00\"]", Normalize("string", "[\"\ud83d\ude00\"]"));
    }

    [Fact]
    public void Output_is_ascii_so_utf8_bytes_equal_characters()
    {
        var text = Normalize("string", "[\"caf\\u00e9\",\"\\u4e2d\"]");
        Assert.Equal(text.Length, Encoding.UTF8.GetByteCount(text));
    }

    [Fact]
    public void Json_arrays_preserve_element_tokens_verbatim_except_whitespace()
    {
        Assert.Equal("[1.0,{\"b\":2,\"a\":[null,true]},-0]", Normalize("json", "[ 1.0, {\"b\":2, \"a\":[null, true]}, -0 ]"));
    }

    [Fact]
    public void Empty_arrays_normalize_to_brackets()
    {
        foreach (var type in new[] { "string", "boolean", "integer", "number", "json" })
            Assert.Equal("[]", Normalize(type, "[]"));
    }
}
