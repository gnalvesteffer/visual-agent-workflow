using System.Collections.Immutable;

namespace VisualAgentWorkflow.Core;

/// <summary>Small immutable edits for the schema 1.44 variable declaration map.</summary>
public static class WorkflowVariableEditing
{
    public static WorkflowDefinition Add(WorkflowDefinition definition, string id, string displayName,
        string defaultValue, string? description = null, string type = WorkflowDataTypes.String)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (definition.SchemaVersion is not (WorkflowSchemaVersions.UnifiedWorkflowGraph or WorkflowSchemaVersions.MutableVariablesGraph))
            throw new ArgumentException("Variables require schema 1.44.", nameof(definition));
        if (!IsValidId(id) || string.IsNullOrWhiteSpace(displayName) || displayName.Length > 128)
            throw new ArgumentException("Variable IDs use lowercase letters, digits, dashes, and underscores; names are required and limited to 128 characters.", nameof(id));
        var variables = definition.Variables ?? ImmutableSortedDictionary<string, WorkflowVariableDefinition>.Empty;
        if (variables.Count >= 64 || variables.ContainsKey(id)) throw new ArgumentException("The variable ID is already in use or the 64-variable limit was reached.", nameof(id));
        return definition with { Variables = variables.Add(id, new(displayName.Trim(), defaultValue) { Type = type, Description = EmptyToNull(description) }) };
    }

    public static WorkflowDefinition Update(WorkflowDefinition definition, string id, string displayName, string defaultValue)
    {
        var variables = Require(definition, id);
        return definition with { Variables = variables.SetItem(id, variables[id] with { DisplayName = displayName, DefaultValue = defaultValue }) };
    }

    public static WorkflowDefinition UpdateDescription(WorkflowDefinition definition, string id, string? description)
    {
        var variables = Require(definition, id);
        return definition with { Variables = variables.SetItem(id, variables[id] with { Description = EmptyToNull(description) }) };
    }

    public static WorkflowDefinition SetType(WorkflowDefinition definition, string id, string type, string defaultValue)
    {
        if (type is not (WorkflowDataTypes.String or WorkflowDataTypes.Boolean or WorkflowDataTypes.Integer or WorkflowDataTypes.Number))
            throw new ArgumentException("Choose a supported scalar variable type.", nameof(type));
        var variables = Require(definition, id);
        return definition with { Variables = variables.SetItem(id, variables[id] with { Type = type, DefaultValue = defaultValue }) };
    }

    public static WorkflowDefinition Remove(WorkflowDefinition definition, string id)
    {
        var variables = Require(definition, id);
        if (definition.Nodes.Values.Any(node => node.VariableId == id))
            throw new ArgumentException("Remove or reconfigure the variable nodes before removing this declaration.", nameof(id));
        return definition with { Variables = variables.Remove(id) };
    }

    private static ImmutableSortedDictionary<string, WorkflowVariableDefinition> Require(WorkflowDefinition definition, string id)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (definition.Variables is not { } variables || !variables.ContainsKey(id))
            throw new ArgumentException("The variable is not declared by this workflow.", nameof(id));
        return variables;
    }

    private static bool IsValidId(string? id) => id is { Length: > 0 and <= 64 } &&
        id[0] is >= 'a' and <= 'z' && id.All(character => character is >= 'a' and <= 'z' or >= '0' and <= '9' or '_' or '-');

    private static string? EmptyToNull(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
