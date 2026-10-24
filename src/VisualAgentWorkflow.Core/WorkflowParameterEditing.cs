using System.Collections.Immutable;

namespace VisualAgentWorkflow.Core;

/// <summary>Pure edits for portable workflow parameters and their explicit graph references.</summary>
public static class WorkflowParameterEditing
{
    public static WorkflowDefinition Add(WorkflowDefinition definition, string id, string displayName, string defaultValue)
    {
        var parameters = definition.Parameters ?? ImmutableSortedDictionary<string, WorkflowParameterDefinition>.Empty;
        if (parameters.ContainsKey(id)) throw new ArgumentException("A parameter with this ID already exists.", nameof(id));
        return Checked(definition with
        {
            SchemaVersion = "1.6",
            Parameters = parameters.Add(id, new(displayName, defaultValue))
        });
    }

    public static WorkflowDefinition Update(WorkflowDefinition definition, string id, string displayName, string defaultValue)
    {
        var parameters = definition.Parameters ?? ImmutableSortedDictionary<string, WorkflowParameterDefinition>.Empty;
        if (!parameters.ContainsKey(id)) throw new ArgumentException("The parameter does not exist.", nameof(id));
        return Checked(definition with { SchemaVersion = "1.6", Parameters = parameters.SetItem(id, new(displayName, defaultValue)) });
    }

    public static WorkflowDefinition Remove(WorkflowDefinition definition, string id)
    {
        if (definition.Nodes.Values.Any(node => node.Type == NodeTypes.WorkflowValue && node.ParameterId == id))
            throw new ArgumentException("Remove or reassign every workflow value node before deleting this parameter.", nameof(id));
        var parameters = definition.Parameters ?? ImmutableSortedDictionary<string, WorkflowParameterDefinition>.Empty;
        if (!parameters.ContainsKey(id)) throw new ArgumentException("The parameter does not exist.", nameof(id));
        return Checked(definition with { SchemaVersion = "1.6", Parameters = parameters.Remove(id) });
    }

    public static WorkflowDefinition BindNode(WorkflowDefinition definition, string nodeId, string parameterId)
    {
        if (definition.Parameters is not { } parameters || !parameters.ContainsKey(parameterId))
            throw new ArgumentException("The parameter does not exist.", nameof(parameterId));
        var node = definition.Nodes.TryGetValue(nodeId, out var value) ? value : throw new ArgumentException("The node does not exist.", nameof(nodeId));
        if (node.Type != NodeTypes.WorkflowValue) throw new ArgumentException("Only workflow value nodes can reference a parameter.", nameof(nodeId));
        return Checked(definition with { SchemaVersion = "1.6", Nodes = definition.Nodes.SetItem(nodeId, node with { ParameterId = parameterId }) });
    }

    private static WorkflowDefinition Checked(WorkflowDefinition definition)
    {
        WorkflowValidator.ThrowIfInvalid(definition);
        return definition;
    }
}
