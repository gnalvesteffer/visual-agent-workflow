using System.Globalization;

namespace VisualAgentWorkflow.Core;

/// <summary>Canonical, culture-independent formatting for explicit integer-to-string nodes.</summary>
public static class IntegerStringConversion
{
    public const int MaximumOutputLength = 20;
    public const string FormatterVersion = "1";

    public static string Format(long value) => value.ToString(CultureInfo.InvariantCulture);
}
