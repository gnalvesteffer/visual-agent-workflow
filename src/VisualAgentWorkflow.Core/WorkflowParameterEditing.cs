using System.Collections.Immutable;

namespace VisualAgentWorkflow.Core;

/// <summary>Pure edits for portable workflow parameters and their explicit graph references.</summary>
public static class WorkflowParameterEditing
{
    public static WorkflowDefinition Add(WorkflowDefinition definition, string id, string displayName, string defaultValue, string? description = null)
    {
        var parameters = definition.Parameters ?? ImmutableSortedDictionary<string, WorkflowParameterDefinition>.Empty;
        if (parameters.ContainsKey(id)) throw new ArgumentException("A parameter with this ID already exists.", nameof(id));
        return Checked(definition with
        {
            SchemaVersion = Schema(definition),
            Parameters = parameters.Add(id, new WorkflowParameterDefinition(displayName, defaultValue) { Description = description })
        });
    }

    public static WorkflowDefinition Update(WorkflowDefinition definition, string id, string displayName, string defaultValue)
    {
        var parameters = definition.Parameters ?? ImmutableSortedDictionary<string, WorkflowParameterDefinition>.Empty;
        if (!parameters.TryGetValue(id, out var parameter)) throw new ArgumentException("The parameter does not exist.", nameof(id));
        return Checked(definition with { SchemaVersion = Schema(definition), Parameters = parameters.SetItem(id, parameter with { DisplayName = displayName, DefaultValue = defaultValue }) });
    }

    public static WorkflowDefinition UpdateDescription(WorkflowDefinition definition, string id, string? description)
    {
        var parameters = definition.Parameters ?? ImmutableSortedDictionary<string, WorkflowParameterDefinition>.Empty;
        if (!parameters.TryGetValue(id, out var parameter)) throw new ArgumentException("The parameter does not exist.", nameof(id));
        return Checked(definition with { SchemaVersion = Schema(definition), Parameters = parameters.SetItem(id, parameter with { Description = description }) });
    }

    public static WorkflowDefinition Remove(WorkflowDefinition definition, string id)
    {
        if (definition.Nodes.Values.Any(node =>
            node.Type == NodeTypes.WorkflowValue && node.ParameterId == id ||
            node.TextTemplate?.Parts.Any(part => part.Kind == "parameter" && part.Value == id) == true ||
            node.Agent?.ReasoningEffortParameterId == id ||
            node.Agent?.MaxOutputTokensParameterId == id ||
            node.Agent?.MaxTotalTokensParameterId == id ||
            node.Agent?.UserMessage?.Parts.Any(part => part.Kind == "parameter" && part.Value == id) == true))
            throw new ArgumentException("Remove or reassign every node that references this parameter before deleting it.", nameof(id));
        var parameters = definition.Parameters ?? ImmutableSortedDictionary<string, WorkflowParameterDefinition>.Empty;
        if (!parameters.ContainsKey(id)) throw new ArgumentException("The parameter does not exist.", nameof(id));
        return Checked(definition with { SchemaVersion = Schema(definition), Parameters = parameters.Remove(id) });
    }

    public static WorkflowDefinition BindNode(WorkflowDefinition definition, string nodeId, string parameterId)
    {
        if (definition.Parameters is not { } parameters || !parameters.ContainsKey(parameterId))
            throw new ArgumentException("The parameter does not exist.", nameof(parameterId));
        var node = definition.Nodes.TryGetValue(nodeId, out var value) ? value : throw new ArgumentException("The node does not exist.", nameof(nodeId));
        if (node.Type != NodeTypes.WorkflowValue) throw new ArgumentException("Only workflow value nodes can reference a parameter.", nameof(nodeId));
        var outputType = definition.SchemaVersion is WorkflowSchemaVersions.IntegerGraph or WorkflowSchemaVersions.ConfigurableParallelGraph or WorkflowSchemaVersions.BranchLocalParallelGraph
            ? WorkflowParameterGraphTypes.DataType(parameters[parameterId])
            : null;
        return Checked(definition with
        {
            SchemaVersion = Schema(definition),
            Nodes = definition.Nodes.SetItem(nodeId, node with { ParameterId = parameterId, OutputDataType = outputType })
        });
    }

    public static WorkflowDefinition SetType(WorkflowDefinition definition, string id, string type, string defaultValue, ImmutableArray<string>? choices = null)
    {
        var parameters = definition.Parameters ?? ImmutableSortedDictionary<string, WorkflowParameterDefinition>.Empty;
        if (!parameters.TryGetValue(id, out var parameter)) throw new ArgumentException("The parameter does not exist.", nameof(id));
        var updated = parameter with { DefaultValue = defaultValue, Type = type, Choices = type == "choice" ? choices : null };
        return Checked(definition with { SchemaVersion = definition.SchemaVersion is "1.9" or "1.10" or "1.11" or "1.12" or "1.13" or "1.14" or "1.15" or "1.16" or "1.17" or "1.19" or "1.22" or "1.21" or "1.20" or "1.18" or WorkflowSchemaVersions.AgentGraphFunctionTools or WorkflowSchemaVersions.AgentParameterBindings or WorkflowSchemaVersions.AgentExecutionRoutes or WorkflowSchemaVersions.IntegerGraph or WorkflowSchemaVersions.ConfigurableParallelGraph or WorkflowSchemaVersions.BranchLocalParallelGraph ? definition.SchemaVersion : "1.8", Parameters = parameters.SetItem(id, updated) });
    }

    private static string Schema(WorkflowDefinition definition) => definition.SchemaVersion is
        "1.6" or "1.7" or "1.8" or "1.9" or "1.10" or "1.11" or "1.12" or "1.13" or "1.14" or "1.15" or
        "1.16" or "1.17" or "1.18" or "1.19" or "1.20" or "1.21" or "1.22" or "1.23" or "1.24" or "1.25" or
        "1.26" or "1.27" or "1.28" or "1.29" or "1.30" or WorkflowSchemaVersions.IntegerGraph or WorkflowSchemaVersions.ConfigurableParallelGraph or WorkflowSchemaVersions.BranchLocalParallelGraph
            ? definition.SchemaVersion
            : "1.6";

    private static WorkflowDefinition Checked(WorkflowDefinition definition) => WorkflowDraftEditing.ValidateEdit(definition);
}
