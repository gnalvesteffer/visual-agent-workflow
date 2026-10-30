using System.Collections.Immutable;

namespace VisualAgentWorkflow.Core;

public sealed record SequenceWorkflowCallJsonArrayPath(
    string OutputId,
    ImmutableArray<string> NodeIds,
    string CallNodeId,
    string ToArrayNodeId,
    string LengthNodeId,
    string ChildWorkflowId,
    Guid ChildRevisionId,
    string ChildContentHash);

public sealed record SequenceWorkflowCallJsonArrayTopologyAnalysis(
    ImmutableArray<SequenceWorkflowCallJsonArrayPath> Paths,
    ImmutableArray<SequenceWorkflowCallTopologyIssue> Issues)
{
    public bool IsValid => Issues.IsDefaultOrEmpty;
}

/// <summary>
/// Schema 1.56 admits one explicit immutable JSON-array refinement followed by a
/// length operation. Existing schema-1.55 graphs remain valid under the promoted
/// schema without inventing array nodes.
/// </summary>
public static class SequenceWorkflowCallJsonArrayTopology
{
    public const int MaximumArrayItems = 64;

    public static SequenceWorkflowCallJsonArrayTopologyAnalysis Analyze(WorkflowDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var paths = ImmutableArray.CreateBuilder<SequenceWorkflowCallJsonArrayPath>();
        var issues = ImmutableArray.CreateBuilder<SequenceWorkflowCallTopologyIssue>();
        void Issue(string code, string path, string message) => issues.Add(new(code, path, message));

        if (definition.SchemaVersion != WorkflowSchemaVersions.SequenceWorkflowCallJsonArrayGraph)
        {
            Issue("sequence.call.json.array.schema", "schemaVersion", "Typed JSON-array topology requires schema 1.56.");
            return new(paths.ToImmutable(), issues.ToImmutable());
        }

        var toArrayNodes = definition.Nodes.Where(entry => entry.Value?.Type == NodeTypes.JsonToArray).ToArray();
        var lengthNodes = definition.Nodes.Where(entry => entry.Value?.Type == NodeTypes.ArrayLength).ToArray();
        if (toArrayNodes.Length == 0 && lengthNodes.Length == 0)
        {
            var legacy = SequenceWorkflowCallJsonMergeTopology.Analyze(definition with
            { SchemaVersion = WorkflowSchemaVersions.SequenceWorkflowCallJsonMergeGraph });
            foreach (var issue in legacy.Issues) issues.Add(issue);
            return new(paths.ToImmutable(), issues.ToImmutable());
        }

        if (toArrayNodes.Length != 1)
            Issue("sequence.call.json.array.to-array", "nodes", "The bounded array topology requires exactly one JSON-to-array node.");
        if (lengthNodes.Length != 1)
            Issue("sequence.call.json.array.length", "nodes", "The bounded array topology requires exactly one array-length node.");
        if (toArrayNodes.Length != 1 || lengthNodes.Length != 1)
            return new(paths.ToImmutable(), issues.ToImmutable());

        var toArrayId = toArrayNodes[0].Key;
        var lengthId = lengthNodes[0].Key;
        if (definition.Nodes.Values.Any(node => node?.Type == NodeTypes.JsonMerge))
            Issue("sequence.call.json.array.composition", "nodes", "The typed JSON-array chain cannot be combined with the bounded JSON object merge.");
        var executionDefinition = WithoutArrayNodes(definition);
        var execution = SequenceWorkflowCallJsonChainTopology.Analyze(executionDefinition with
        { SchemaVersion = WorkflowSchemaVersions.SequenceWorkflowCallJsonChainGraph });
        foreach (var issue in execution.Issues) issues.Add(issue);
        var callPath = execution.Paths.SingleOrDefault();
        if (callPath is null)
        {
            Issue("sequence.call.json.array.call", "nodes", "The bounded array topology requires one valid JSON WorkflowCall path.");
            return new(paths.ToImmutable(), issues.ToImmutable());
        }

        var callId = callPath.CallNodeId;
        var outputId = definition.Nodes.SingleOrDefault(entry => entry.Value?.Type == NodeTypes.Output).Key;
        if (string.IsNullOrWhiteSpace(outputId))
        {
            Issue("sequence.call.json.array.output", "nodes", "The bounded array topology requires one Output node.");
            return new(paths.ToImmutable(), issues.ToImmutable());
        }

        var dataEdges = definition.Connections.Values.Where(edge => edge is not null).ToArray();
        RequireSingleEdge(dataEdges, callId, "out", toArrayId, "in", Issue,
            "WorkflowCall.out must feed JsonToArray.in.");
        RequireSingleEdge(dataEdges, toArrayId, "out", lengthId, "in", Issue,
            "JsonToArray.out must feed ArrayLength.in.");
        RequireSingleEdge(dataEdges, lengthId, "out", outputId, "in", Issue,
            "ArrayLength.out must feed Output.in.");

        var expected = new HashSet<(string Source, string SourcePort, string Target, string TargetPort)>
        {
            (callId, "out", toArrayId, "in"),
            (toArrayId, "out", lengthId, "in"),
            (lengthId, "out", outputId, "in")
        };
        foreach (var edge in dataEdges)
            if (!expected.Contains((edge.Source.NodeId, edge.Source.PortId, edge.Target.NodeId, edge.Target.PortId)))
                Issue("sequence.call.json.array.data", "connections", "Schema 1.56 permits only the WorkflowCall-to-array-to-length data chain.");

        if (definition.Nodes[outputId].OutputDataType != WorkflowDataTypes.Integer)
            Issue("sequence.call.json.array.output-type", $"nodes.{outputId}", "ArrayLength requires an integer workflow Output.");

        var call = definition.Nodes[callId].WorkflowCall!;
        paths.Add(new(callPath.OutputId, callPath.NodeIds.Add(toArrayId).Add(lengthId), callId, toArrayId, lengthId,
            call.WorkflowId, call.RevisionId, call.ContentHash));
        foreach (var node in definition.Nodes.Where(entry => entry.Value?.Type is NodeTypes.JsonToArray or NodeTypes.ArrayLength))
            if (node.Key != toArrayId && node.Key != lengthId)
                Issue("sequence.call.json.array.orphan", $"nodes.{node.Key}", "Every array node must belong to the bounded call data chain.");
        return new(paths.ToImmutable(), issues.ToImmutable());
    }

    private static WorkflowDefinition WithoutArrayNodes(WorkflowDefinition definition)
    {
        var removed = definition.Nodes.Where(entry => entry.Value?.Type is NodeTypes.JsonToArray or NodeTypes.ArrayLength)
            .Select(entry => entry.Key).ToHashSet(StringComparer.Ordinal);
        var outputId = definition.Nodes.Single(entry => entry.Value.Type == NodeTypes.Output).Key;
        var nodes = definition.Nodes.RemoveRange(removed).SetItem(outputId,
            definition.Nodes[outputId] with { OutputDataType = WorkflowDataTypes.Json });
        var connections = definition.Connections.RemoveRange(definition.Connections.Where(entry =>
            removed.Contains(entry.Value.Source.NodeId) || removed.Contains(entry.Value.Target.NodeId)).Select(entry => entry.Key));
        connections = connections.SetItem("typed-array-underlying-json", new(
            new(definition.Nodes.Single(entry => entry.Value.Type == NodeTypes.WorkflowCall).Key, "out"),
            new(outputId, "in"), "data"));
        return definition with
        {
            SchemaVersion = WorkflowSchemaVersions.SequenceWorkflowCallJsonMergeGraph,
            // The array chain changes only the public data result; validate the
            // underlying WorkflowCall barrier against its JSON source contract.
            Nodes = nodes,
            Connections = connections
        };
    }

    private static void RequireSingleEdge(
        IEnumerable<ConnectionDefinition> edges, string source, string sourcePort, string target, string targetPort,
        Action<string, string, string> issue, string message)
    {
        var matches = edges.Count(edge => edge.Source == new PortReference(source, sourcePort) && edge.Target == new PortReference(target, targetPort));
        if (matches != 1) issue("sequence.call.json.array.edge", "connections", message);
    }
}
