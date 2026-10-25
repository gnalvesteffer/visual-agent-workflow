using System.Collections.Immutable;
using System.Globalization;

namespace VisualAgentWorkflow.Core;

public static class WorkflowParameters
{
    /// <summary>Checks whether every value allowed by a parent parameter is accepted by a child parameter.</summary>
    public static bool AreCompatible(WorkflowParameterDefinition parent, WorkflowParameterDefinition child)
    {
        ArgumentNullException.ThrowIfNull(parent);
        ArgumentNullException.ThrowIfNull(child);
        if ((parent.Type ?? "string") != (child.Type ?? "string")) return false;
        return parent.Type != "choice" || parent.Choices is { } parentChoices && child.Choices is { } childChoices &&
            parentChoices.All(choice => childChoices.Contains(choice, StringComparer.Ordinal));
    }

    /// <summary>Resolves portable defaults and per-run string overrides. Values are audit evidence, never secrets.</summary>
    public static ImmutableSortedDictionary<string, string>? Resolve(
        WorkflowDefinition definition, IReadOnlyDictionary<string, string>? overrides)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var declared = definition.Parameters ?? ImmutableSortedDictionary<string, WorkflowParameterDefinition>.Empty;
        if (overrides is not null && overrides.Keys.Any(id => !declared.ContainsKey(id)))
            throw new ArgumentException("A run override references an undefined workflow parameter.", nameof(overrides));

        if (declared.Count == 0)
        {
            if (overrides is { Count: > 0 }) throw new ArgumentException("This workflow defines no parameters.", nameof(overrides));
            return null;
        }

        var resolved = ImmutableSortedDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);
        foreach (var (id, parameter) in declared)
        {
            var value = overrides is not null && overrides.TryGetValue(id, out var supplied)
                ? supplied
                : parameter.DefaultValue;
            if (value is null || value.Length > 16_384)
                throw new ArgumentException($"Parameter '{id}' must be a string up to 16,384 characters.", nameof(overrides));
            if (!IsValidValue(parameter, value))
                throw new ArgumentException($"Value for workflow parameter '{id}' does not match its {parameter.Type ?? "string"} type.", nameof(overrides));
            resolved.Add(id, NormalizeValue(parameter, value));
        }
        return resolved.ToImmutable();
    }

    public static bool IsValidValue(WorkflowParameterDefinition parameter, string value) => (parameter.Type ?? "string") switch
    {
        "string" => value.Length <= 16_384,
        "integer" => long.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out _),
        "number" => decimal.TryParse(value, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out _),
        "boolean" => value is "true" or "false",
        "choice" => parameter.Choices is { } choices && choices.Contains(value, StringComparer.Ordinal),
        _ => false
    };

    public static string NormalizeValue(WorkflowParameterDefinition parameter, string value) => (parameter.Type ?? "string") switch
    {
        "integer" when long.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var integer) => integer.ToString(CultureInfo.InvariantCulture),
        "number" when decimal.TryParse(value, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var number) => number.ToString("G29", CultureInfo.InvariantCulture),
        _ => value
    };
}
