using System.Globalization;

namespace VisualAgentWorkflow.Core;

public static class StringIntegerConversion
{
    public const int MaximumInputLength = 20;
    public const string ParserVersion = "1";

    public sealed record Failure(string Code, string ParserVersion, string Reason, int InputLength);
    public sealed record Result(bool Success, long? Value, Failure? Failure)
    {
        public static Result Succeeded(long value) => new(true, value, null);
        public static Result Rejected(string reason, int inputLength) => new(false, null,
            new("string-to-integer.invalid", StringIntegerConversion.ParserVersion, reason, inputLength));
    }

    public static Result Parse(string? text)
    {
        var inputLength = text?.Length ?? 0;
        if (text is null or { Length: 0 }) return Result.Rejected("empty", inputLength);
        if (text.Length > MaximumInputLength) return Result.Rejected("too-long", inputLength);

        var index = text[0] is '+' or '-' ? 1 : 0;
        if (index == text.Length) return Result.Rejected("syntax", inputLength);
        for (; index < text.Length; index++)
            if (text[index] is < '0' or > '9') return Result.Rejected("syntax", inputLength);

        return long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value)
            ? Result.Succeeded(value)
            : Result.Rejected("overflow", inputLength);
    }
}
