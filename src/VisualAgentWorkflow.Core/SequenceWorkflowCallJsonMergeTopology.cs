using System.Collections.Immutable;

namespace VisualAgentWorkflow.Core;

/// <summary>One schema-1.55 WorkflowCall JSON merge path and its pure data nodes.</summary>
public sealed record SequenceWorkflowCallJsonMergePath(
    string OutputId,
    ImmutableArray<string> NodeIds,
    string CallNodeId,
    string ProjectionNodeId,
    string MergeNodeId,
    string ChildWorkflowId,
    Guid ChildRevisionId,
    string ChildContentHash);

public sealed record SequenceWorkflowCallJsonMergeTopologyAnalysis(
    ImmutableArray<SequenceWorkflowCallJsonMergePath> Paths,
    ImmutableArray<SequenceWorkflowCallTopologyIssue> Issues)
{
    public bool IsValid => Issues.IsDefaultOrEmpty;
}

/// <summary>
/// Schema 1.55 adds one deliberately narrow multi-consumer data contract. A JSON
/// WorkflowCall output may feed one projection and one merge input; the projection
/// feeds the other merge input and the merge feeds Output. Execution remains a
/// separate WorkflowCall-to-Output barrier.
/// </summary>
public static class SequenceWorkflowCallJsonMergeTopology
{
    public const int MaximumMergeNodes = 1;

    public static bool IsJsonMergeGraph(WorkflowDefinition definition) =>
        definition.SchemaVersion == WorkflowSchemaVersions.SequenceWorkflowCallJsonMergeGraph &&
        definition.Nodes.Values.Any(node => node?.Type == NodeTypes.JsonMerge);

    public static SequenceWorkflowCallJsonMergeTopologyAnalysis Analyze(WorkflowDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var paths = ImmutableArray.CreateBuilder<SequenceWorkflowCallJsonMergePath>();
        var issues = ImmutableArray.CreateBuilder<SequenceWorkflowCallTopologyIssue>();
        void Issue(string code, string path, string message) => issues.Add(new(code, path, message));

        if (definition.SchemaVersion != WorkflowSchemaVersions.SequenceWorkflowCallJsonMergeGraph)
        {
            Issue("sequence.call.json.merge.schema", "schemaVersion", "JSON merge topology requires schema 1.55.");
            return new(paths.ToImmutable(), issues.ToImmutable());
        }

        var mergeNodes = definition.Nodes.Where(entry => entry.Value?.Type == NodeTypes.JsonMerge).ToArray();
        if (mergeNodes.Length == 0)
        {
            var legacy = SequenceWorkflowCallJsonChainTopology.Analyze(definition with
            {
                SchemaVersion = WorkflowSchemaVersions.SequenceWorkflowCallJsonChainGraph
            });
            foreach (var issue in legacy.Issues) issues.Add(issue);
            return new(paths.ToImmutable(), issues.ToImmutable());
        }

        if (mergeNodes.Length > MaximumMergeNodes)
            Issue("sequence.call.json.merge.limit", "nodes", "Schema 1.55 permits only one JSON merge node.");

        var projectionNodes = definition.Nodes.Where(entry => entry.Value?.Type == NodeTypes.JsonProject).ToArray();
        var selectorNodes = definition.Nodes.Where(entry => entry.Value?.Type == NodeTypes.JsonSelectString).ToArray();
        if (projectionNodes.Length != 1)
            Issue("sequence.call.json.merge.projection", "nodes", "The bounded JSON merge topology requires exactly one JSON projection node.");
        if (selectorNodes.Length != 0)
            Issue("sequence.call.json.merge.node", $"nodes.{selectorNodes[0].Key}", "JSON selectors are not part of the schema 1.55 merge topology.");
        if (mergeNodes.Length != 1 || projectionNodes.Length != 1 || selectorNodes.Length != 0)
            return new(paths.ToImmutable(), issues.ToImmutable());

        var executionDefinition = WithoutPureJsonNodes(definition);
        var execution = SequenceWorkflowCallJsonChainTopology.Analyze(executionDefinition);
        foreach (var issue in execution.Issues) issues.Add(issue);

        var callPaths = execution.Paths.Where(path => path.CallNodeId is not null).ToArray();
        if (callPaths.Length != 1)
        {
            Issue("sequence.call.json.merge.call", "nodes", "The bounded JSON merge topology requires exactly one WorkflowCall path.");
            return new(paths.ToImmutable(), issues.ToImmutable());
        }

        var mergeId = mergeNodes[0].Key;
        var projectionId = projectionNodes[0].Key;
        var callId = callPaths[0].CallNodeId;
        var outputId = definition.Nodes.SingleOrDefault(entry => entry.Value?.Type == NodeTypes.Output).Key;
        if (string.IsNullOrWhiteSpace(outputId))
        {
            Issue("sequence.call.json.merge.graph", "nodes", "The bounded JSON merge topology requires one Output node.");
            return new(paths.ToImmutable(), issues.ToImmutable());
        }

        var dataEdges = definition.Connections.Where(entry => entry.Value is not null)
            .Select(entry => (entry.Key, Edge: entry.Value))
            .ToArray();
        RequireSingleEdge(dataEdges, callId, "out", mergeId, "left", "call-merge", Issue,
            "WorkflowCall.out must feed JsonMerge.left.");
        RequireSingleEdge(dataEdges, callId, "out", projectionId, "in", "call-project", Issue,
            "WorkflowCall.out must feed JsonProject.in.");
        RequireSingleEdge(dataEdges, projectionId, "out", mergeId, "right", "project-merge", Issue,
            "JsonProject.out must feed JsonMerge.right.");
        RequireSingleEdge(dataEdges, mergeId, "out", outputId, "in", "merge-output", Issue,
            "JsonMerge.out must feed Output.in.");

        var compositionNodeIds = new HashSet<string>(StringComparer.Ordinal) { callId, projectionId, mergeId, outputId };
        foreach (var (_, edge) in dataEdges.Where(pair =>
                     pair.Edge.Source.NodeId is var source && compositionNodeIds.Contains(source) ||
                     pair.Edge.Target.NodeId is var target && compositionNodeIds.Contains(target)))
        {
            if (!IsExpectedEdge(edge, callId, projectionId, mergeId, outputId))
                Issue("sequence.call.json.merge.data", "connections", "Schema 1.55 permits only the bounded WorkflowCall-to-project/merge data topology.");
        }

        if (definition.Nodes[outputId].OutputDataType != WorkflowDataTypes.Json)
            Issue("sequence.call.json.merge.output", $"nodes.{outputId}", "JsonMerge requires a JSON workflow Output.");

        var path = callPaths[0];
        var call = definition.Nodes[callId].WorkflowCall!;
        paths.Add(new(path.OutputId, path.NodeIds, callId, projectionId, mergeId,
            call.WorkflowId, call.RevisionId, call.ContentHash));
        foreach (var json in definition.Nodes.Where(entry => entry.Value?.Type is NodeTypes.JsonProject or NodeTypes.JsonMerge))
            if (json.Key != projectionId && json.Key != mergeId)
                Issue("sequence.call.json.merge.orphan", $"nodes.{json.Key}", "Every JSON merge node must belong to the bounded call data topology.");
        return new(paths.ToImmutable(), issues.ToImmutable());
    }

    private static WorkflowDefinition WithoutPureJsonNodes(WorkflowDefinition definition)
    {
        var removed = definition.Nodes.Where(entry => entry.Value?.Type is NodeTypes.JsonSelectString or NodeTypes.JsonProject or NodeTypes.JsonMerge)
            .Select(entry => entry.Key).ToHashSet(StringComparer.Ordinal);
        var nodes = definition.Nodes.RemoveRange(removed);
        var connections = definition.Connections.RemoveRange(definition.Connections.Where(entry =>
            removed.Contains(entry.Value.Source.NodeId) || removed.Contains(entry.Value.Target.NodeId)).Select(entry => entry.Key));
        return definition with
        {
            SchemaVersion = WorkflowSchemaVersions.SequenceWorkflowCallJsonChainGraph,
            Nodes = nodes,
            Connections = connections
        };
    }

    private static void RequireSingleEdge(
        IReadOnlyCollection<(string Key, ConnectionDefinition Edge)> edges,
        string sourceNodeId, string sourcePortId, string targetNodeId, string targetPortId,
        string label, Action<string, string, string> issue, string message)
    {
        var matches = edges.Where(pair => pair.Edge.Source == new PortReference(sourceNodeId, sourcePortId) &&
                                          pair.Edge.Target == new PortReference(targetNodeId, targetPortId)).ToArray();
        if (matches.Length != 1)
            issue("sequence.call.json.merge.cardinality", $"connections.{label}", message);
    }

    private static bool IsExpectedEdge(ConnectionDefinition edge, string callId, string projectionId, string mergeId, string outputId) =>
        edge is { Kind: "data" } &&
        (edge.Source == new PortReference(callId, "out") &&
             (edge.Target == new PortReference(mergeId, "left") || edge.Target == new PortReference(projectionId, "in")) ||
         edge.Source == new PortReference(projectionId, "out") && edge.Target == new PortReference(mergeId, "right") ||
         edge.Source == new PortReference(mergeId, "out") && edge.Target == new PortReference(outputId, "in"));
}
