using System.Collections.Immutable;

namespace VisualAgentWorkflow.Core;

public static class WorkflowParameters
{
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
            resolved.Add(id, value);
        }
        return resolved.ToImmutable();
    }
}
