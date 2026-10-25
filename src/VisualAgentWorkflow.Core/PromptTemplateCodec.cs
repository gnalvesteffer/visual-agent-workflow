using System.Collections.Immutable;
using System.Text;

namespace VisualAgentWorkflow.Core;

/// <summary>Converts user-facing prompt text tokens to the structured, non-evaluated prompt contract.</summary>
public static class PromptTemplateCodec
{
    public static string Format(PromptTemplate? template)
    {
        if (template is null) return "{{input}}";
        var text = new StringBuilder();
        foreach (var part in template.Parts)
        {
            switch (part.Kind)
            {
                case "literal":
                    text.Append(part.Value.Replace("\\", "\\\\", StringComparison.Ordinal)
                        .Replace("{{", "\\{{", StringComparison.Ordinal));
                    break;
                case "input":
                    text.Append("{{input}}");
                    break;
                case "parameter":
                    text.Append("{{parameter:").Append(part.Value).Append("}}");
                    break;
                default:
                    throw new InvalidOperationException("The prompt contains an unsupported part.");
            }
        }
        return text.ToString();
    }

    public static PromptTemplate Parse(string text, IReadOnlyDictionary<string, WorkflowParameterDefinition> parameters)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(parameters);
        if (text.Length > 8192) throw new ArgumentException("Prompt text exceeds 8,192 characters.", nameof(text));

        var parts = ImmutableArray.CreateBuilder<PromptPart>();
        var literal = new StringBuilder();
        for (var index = 0; index < text.Length;)
        {
            if (text.AsSpan(index).StartsWith("\\\\", StringComparison.Ordinal))
            {
                literal.Append('\\');
                index += 2;
            }
            else if (text.AsSpan(index).StartsWith("\\{{", StringComparison.Ordinal))
            {
                literal.Append("{{");
                index += 3;
            }
            else if (text.AsSpan(index).StartsWith("{{", StringComparison.Ordinal))
            {
                var end = text.IndexOf("}}", index + 2, StringComparison.Ordinal);
                if (end < 0) throw new FormatException("Complete the reference token or choose a value from the picker.");
                var token = text[(index + 2)..end];
                var reference = token switch
                {
                    "input" => new PromptPart("input", ""),
                    _ when token.StartsWith("parameter:", StringComparison.Ordinal) &&
                        parameters.ContainsKey(token[10..]) => new PromptPart("parameter", token[10..]),
                    _ => throw new FormatException("Use {{input}} or {{parameter:parameter-id}} for prompt references.")
                };
                AddLiteral(parts, literal);
                parts.Add(reference);
                index = end + 2;
            }
            else
            {
                literal.Append(text[index]);
                index++;
            }
        }
        AddLiteral(parts, literal);
        if (parts.Count == 0) parts.Add(new("literal", ""));
        return new(parts.ToImmutable());
    }

    private static void AddLiteral(ImmutableArray<PromptPart>.Builder parts, StringBuilder literal)
    {
        if (literal.Length == 0) return;
        parts.Add(new("literal", literal.ToString()));
        literal.Clear();
    }
}
