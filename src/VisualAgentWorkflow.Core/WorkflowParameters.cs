using System.Collections.Immutable;
using System.Globalization;

namespace VisualAgentWorkflow.Core;

public static class WorkflowParameters
{
    internal const int MaximumNumberTextLength = 128;

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
            var allowsFiniteNumbers = definition.SchemaVersion is WorkflowSchemaVersions.UnifiedWorkflowGraph or WorkflowSchemaVersions.NumberPropagationGraph or WorkflowSchemaVersions.NumberArithmeticGraph;
            if (!IsValidValue(parameter, value, allowsFiniteNumbers))
                throw new ArgumentException($"Value for workflow parameter '{id}' does not match its {parameter.Type ?? "string"} type.", nameof(overrides));
            resolved.Add(id, NormalizeValue(parameter, value, allowsFiniteNumbers));
        }
        var values = resolved.ToImmutable();
        ValidateAgentLimits(definition, values);
        return values;
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

    public static bool IsValidValue(WorkflowParameterDefinition parameter, string value, bool allowFiniteNumber) =>
        (parameter.Type ?? "string") == "number" && allowFiniteNumber ? TryParseFiniteNumber(value, out _) : IsValidValue(parameter, value);

    public static string NormalizeValue(WorkflowParameterDefinition parameter, string value) => (parameter.Type ?? "string") switch
    {
        "integer" when long.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var integer) => integer.ToString(CultureInfo.InvariantCulture),
        "number" when decimal.TryParse(value, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var number) => number.ToString("G29", CultureInfo.InvariantCulture),
        _ => value
    };

    public static string NormalizeValue(WorkflowParameterDefinition parameter, string value, bool allowFiniteNumber) =>
        (parameter.Type ?? "string") == "number" && allowFiniteNumber && TryParseFiniteNumber(value, out var number)
            ? number.ToString("R", CultureInfo.InvariantCulture)
            : NormalizeValue(parameter, value);

    public static bool TryParseFiniteNumber(string value, out double number)
    {
        number = default;
        if (string.IsNullOrEmpty(value) || value.Length > MaximumNumberTextLength || value.Any(char.IsWhiteSpace)) return false;
        var index = 0;
        if (value[index] is '+' or '-') index++;
        var digitsBefore = 0;
        while (index < value.Length && value[index] is >= '0' and <= '9') { digitsBefore++; index++; }
        var digitsAfter = 0;
        if (index < value.Length && value[index] == '.')
        {
            index++;
            while (index < value.Length && value[index] is >= '0' and <= '9') { digitsAfter++; index++; }
        }
        if (digitsBefore + digitsAfter == 0) return false;
        if (index < value.Length && value[index] is 'e' or 'E')
        {
            index++;
            if (index < value.Length && value[index] is '+' or '-') index++;
            var exponentDigits = 0;
            while (index < value.Length && value[index] is >= '0' and <= '9') { exponentDigits++; index++; }
            if (exponentDigits == 0) return false;
        }
        if (index != value.Length || !double.TryParse(value, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint | NumberStyles.AllowExponent, CultureInfo.InvariantCulture, out number)) return false;
        return double.IsFinite(number);
    }

    private static void ValidateAgentLimits(WorkflowDefinition definition, IReadOnlyDictionary<string, string> values)
    {
        foreach (var (nodeId, node) in definition.Nodes)
        {
            var agent = node?.Agent;
            if (agent is null) continue;
            ValidateBoundLimit(agent.MaxOutputTokensParameterId, AgentOutputTokenLimits.Minimum, AgentOutputTokenLimits.Maximum,
                "Maximum output tokens", nodeId);
            ValidateBoundLimit(agent.MaxTotalTokensParameterId, AgentTotalTokenLimits.Minimum, AgentTotalTokenLimits.Maximum,
                "Maximum total tokens", nodeId);
        }

        void ValidateBoundLimit(string? parameterId, int minimum, int maximum, string setting, string nodeId)
        {
            if (parameterId is null) return;
            if (!values.TryGetValue(parameterId, out var value) ||
                !int.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var parsed) ||
                parsed < minimum || parsed > maximum)
                throw new ArgumentException($"Workflow parameter '{parameterId}' for {setting} on agent '{nodeId}' must be from {minimum} to {maximum}.", nameof(values));
        }
    }
}

public static class WorkflowParameterGraphTypes
{
    public static string? DataType(WorkflowParameterDefinition parameter, bool allowNumber = false) => (parameter.Type ?? "string") switch
    {
        "string" or "choice" => "string",
        "boolean" => "boolean",
        "integer" => "integer",
        "number" when allowNumber => "number",
        _ => null
    };
}
