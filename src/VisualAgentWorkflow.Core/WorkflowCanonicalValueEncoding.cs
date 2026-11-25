using System.Globalization;
using System.Text.Json;

namespace VisualAgentWorkflow.Core;

/// <summary>
/// The single canonical JSON spelling for typed primitive values. Typed arrays use it today; any future
/// composite value (for example a record) must reuse it rather than re-deriving the rules.
/// </summary>
/// <remarks>
/// Rules, pinned by golden-byte tests:
/// numbers must be finite; zero (including negative zero) is "0"; every other double uses the
/// round-trip "R" format with the invariant culture; integers are signed 64-bit decimal; booleans are
/// <c>true</c>/<c>false</c>; strings are written by <see cref="Utf8JsonWriter"/> with its default encoder
/// (ASCII output, non-ASCII and HTML-sensitive characters as uppercase <c>\uXXXX</c>) and are NOT
/// Unicode-normalized. Output has no insignificant whitespace.
/// </remarks>
public static class WorkflowCanonicalValueEncoding
{
    public static bool TryFormatNumber(double value, out string text)
    {
        if (!double.IsFinite(value))
        {
            text = string.Empty;
            return false;
        }

        text = value == 0d ? "0" : value.ToString("R", CultureInfo.InvariantCulture);
        return true;
    }

    public static string FormatNumber(double value) =>
        TryFormatNumber(value, out var text)
            ? text
            : throw new ArgumentOutOfRangeException(nameof(value), "Canonical numbers must be finite.");

    public static void WriteNumber(Utf8JsonWriter writer, double value) =>
        writer.WriteRawValue(FormatNumber(value));

    public static void WriteInteger(Utf8JsonWriter writer, long value) => writer.WriteNumberValue(value);

    public static void WriteBoolean(Utf8JsonWriter writer, bool value) => writer.WriteBooleanValue(value);

    public static void WriteString(Utf8JsonWriter writer, string? value) => writer.WriteStringValue(value);
}
