using System.Collections.Immutable;

namespace VisualAgentWorkflow.Core;

public sealed record UnifiedJsonArrayInputTopologyIssue(string Code, string Path, string Message);

public sealed record UnifiedJsonArrayInputTopologyAnalysis(
    ImmutableArray<UnifiedJsonArrayInputTopologyIssue> Issues)
{
    public bool IsValid => Issues.IsDefaultOrEmpty;
}

/// <summary>
/// Defines the first unified-schema use of the bounded JSON-array refinement:
/// caller JSON input flows through array refinement and length to integer output.
/// </summary>
public static class UnifiedJsonArrayInputTopology
{
    public static UnifiedJsonArrayInputTopologyAnalysis Analyze(WorkflowDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var issues = ImmutableArray.CreateBuilder<UnifiedJsonArrayInputTopologyIssue>();
        void Issue(string code, string path, string message) => issues.Add(new(code, path, message));

        if (definition.SchemaVersion != WorkflowSchemaVersions.UnifiedWorkflowGraph)
        {
            Issue("unified.json-array.schema", "schemaVersion", "The direct JSON-array input chain requires unified schema 2.0.");
            return new(issues.ToImmutable());
        }

        var inputs = definition.Nodes.Where(entry => entry.Value?.Type == NodeTypes.Input).ToArray();
        var outputs = definition.Nodes.Where(entry => entry.Value?.Type == NodeTypes.Output).ToArray();
        var arrays = definition.Nodes.Where(entry => entry.Value?.Type == NodeTypes.JsonToArray).ToArray();
        var lengths = definition.Nodes.Where(entry => entry.Value?.Type == NodeTypes.ArrayLength).ToArray();

        if (inputs.Length != 1 || outputs.Length != 1 || arrays.Length != 1 || lengths.Length != 1 || definition.Nodes.Count != 4)
            Issue("unified.json-array.nodes", "nodes", "This unified JSON-array slice requires exactly one Input, JSON to array, Array length, and Output node.");
        if (inputs.Length != 1 || outputs.Length != 1 || arrays.Length != 1 || lengths.Length != 1 || definition.Nodes.Count != 4)
            return new(issues.ToImmutable());

        var input = inputs[0].Key;
        var output = outputs[0].Key;
        var array = arrays[0].Key;
        var length = lengths[0].Key;
        if (definition.EntryNodeId != input || definition.Nodes[input].InputDataType != WorkflowDataTypes.Json)
            Issue("unified.json-array.input", $"nodes.{input}", "The graph entry must be a JSON Input node.");
        if (definition.Nodes[output].OutputDataType != WorkflowDataTypes.Integer)
            Issue("unified.json-array.output", $"nodes.{output}", "Array length must connect to an integer Output node.");

        RequireSingleDataEdge(definition, input, "out", array, "in", Issue);
        RequireSingleDataEdge(definition, array, "out", length, "in", Issue);
        RequireSingleDataEdge(definition, length, "out", output, "in", Issue);
        if (definition.Connections.Count != 3)
            Issue("unified.json-array.data-shape", "connections", "The bounded JSON-array slice permits only the Input → JSON to array → Array length → Output data chain.");

        var execution = definition.ExecutionConnections?.Values.Where(edge => edge is not null).ToArray() ?? [];
        if (execution.Length != 1 || execution[0].Source != new PortReference(input, "then") ||
            execution[0].Target != new PortReference(output, "execute"))
            Issue("unified.json-array.execution", "executionConnections", "Input must activate Output directly; data evaluation occurs when Output resolves its input.");

        return new(issues.ToImmutable());
    }

    private static void RequireSingleDataEdge(WorkflowDefinition definition, string source, string sourcePort,
        string target, string targetPort, Action<string, string, string> issue)
    {
        var matches = definition.Connections.Values.Where(edge => edge is not null &&
            edge.Source == new PortReference(source, sourcePort) && edge.Target == new PortReference(target, targetPort)).ToArray();
        if (matches.Length != 1)
            issue("unified.json-array.data-edge", "connections",
                $"Exactly one data edge must connect {source}.{sourcePort} to {target}.{targetPort}.");
    }
}
