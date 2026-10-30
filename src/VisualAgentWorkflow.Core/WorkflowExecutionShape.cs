namespace VisualAgentWorkflow.Core;

/// <summary>Selects an existing bounded control contract from graph contents.</summary>
/// <remarks>The result is an internal compatibility view; it never changes the stored revision.</remarks>
public static class WorkflowExecutionShape
{
    public static string SequenceContract(WorkflowDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        bool Has(string type) => definition.Nodes.Values.Any(node => node?.Type == type);
        if (Has(NodeTypes.WorkflowCall))
        {
            if (Has(NodeTypes.BooleanBranch)) return WorkflowSchemaVersions.SequenceWorkflowCallJsonControlGraph;
            if (Has(NodeTypes.JsonToArray) || Has(NodeTypes.ArrayLength)) return WorkflowSchemaVersions.SequenceWorkflowCallJsonArrayGraph;
            if (Has(NodeTypes.JsonMerge)) return WorkflowSchemaVersions.SequenceWorkflowCallJsonMergeGraph;
            if (definition.Nodes.Values.Any(node => node?.WorkflowCall?.Definition?.Nodes?.Values.Any(child =>
                    child?.Type == NodeTypes.Output && child.OutputDataType == WorkflowDataTypes.Json) == true))
                return WorkflowSchemaVersions.SequenceWorkflowCallJsonChainGraph;
            if (Has(NodeTypes.Agent)) return WorkflowSchemaVersions.SequenceWorkflowCallAgentGraph;
            if (Has(NodeTypes.SequenceUppercase) || Has(NodeTypes.SequenceReverse)) return WorkflowSchemaVersions.SequenceWorkflowCallTransformGraph;
            return WorkflowSchemaVersions.SequenceWorkflowCallGraph;
        }
        if (Has(NodeTypes.Agent)) return WorkflowSchemaVersions.SequenceAgentPathGraph;
        if (Has(NodeTypes.SequenceUppercase) || Has(NodeTypes.SequenceReverse)) return WorkflowSchemaVersions.SequenceTransformChainGraph;
        return WorkflowSchemaVersions.SequencePathGraph;
    }
}
