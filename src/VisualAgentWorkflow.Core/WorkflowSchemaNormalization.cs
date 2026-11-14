using System.Collections.Immutable;

namespace VisualAgentWorkflow.Core;

/// <summary>Explicit, source-preserving conversion to the current editor contract.</summary>
public static class WorkflowSchemaNormalization
{
    public const string NormalizerVersion = "unified-graph-2.0.2";

    /// <summary>Performs normalization and returns stable provenance or structured diagnostics.</summary>
    public static WorkflowNormalizationResult NormalizeForEditingWithProvenance(
        WorkflowDefinition definition, Guid? sourceRevisionId = null)
    {
        ArgumentNullException.ThrowIfNull(definition);
        try
        {
            var target = NormalizeForEditing(definition);
            if (ReferenceEquals(target, definition))
                return new(target, null);

            var sourceHash = WorkflowJson.ComputeDraftHash(definition);
            var targetHash = WorkflowJson.ComputeHash(target);
            var identity = WorkflowNormalizationProvenance.ComputeIdentity(sourceHash, definition.SchemaVersion,
                targetHash, target.SchemaVersion, NormalizerVersion);
            return new(target, new WorkflowNormalizationProvenance(identity, sourceRevisionId, sourceHash,
                definition.SchemaVersion, targetHash, target.SchemaVersion, NormalizerVersion, [], definition, target));
        }
        catch (ArgumentException exception)
        {
            var message = exception.Message;
            var separator = message.IndexOf(':');
            var code = separator > 0 && message[..separator].StartsWith("normalization.", StringComparison.Ordinal)
                ? message[..separator] : "normalization.failed";
            return new(null, null, [new WorkflowNormalizationDiagnostic(code, "definition", message)]);
        }
    }

    /// <summary>
    /// Preserves existing graph identities and embedded revision snapshots. Implicit linear
    /// string graphs receive deterministic execution wires; ambiguous control flows are rejected.
    /// No publication, dependency upgrade, or persistence is performed.
    /// </summary>
    public static WorkflowDefinition NormalizeForEditing(WorkflowDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (definition.SchemaVersion == WorkflowSchemaVersions.UnifiedWorkflowGraph) return definition;
        WorkflowDefinition candidate;
        if (WorkflowSchemaVersions.UsesExplicitGraph(definition))
        {
            candidate = definition with { SchemaVersion = WorkflowSchemaVersions.UnifiedWorkflowGraph };
        }
        else
        {
            WorkflowValidator.ThrowIfInvalid(definition);
            if (definition.Nodes.Values.Any(node => node.Type == NodeTypes.Condition))
            {
                candidate = NormalizeLegacyConditionalTextGraph(definition);
            }
            else if (definition.Nodes.Values.Any(node => node.Type == NodeTypes.Loop))
            {
                candidate = NormalizeLegacyDataLoop(definition);
            }
            else if (definition.Nodes.Values.Any(node => node.Type is not
                    (NodeTypes.Input or NodeTypes.Output or NodeTypes.Uppercase or NodeTypes.Reverse or NodeTypes.Agent) ||
                node.Type == NodeTypes.Agent && node.Agent?.Routes is not null))
                throw new ArgumentException("normalization.implicit-unsafe: Only implicit linear Input, Output, Uppercase, Reverse and unrouted Agent graphs have a verified translation; legacy control-flow graphs require a separately verified conversion.", nameof(definition));
            else
            {
                var order = WorkflowEditing.OrderedNodes(definition);
                var execution = ImmutableSortedDictionary.CreateBuilder<string, ConnectionDefinition>(StringComparer.Ordinal);
                var executableNodes = order.Where(id => definition.Nodes[id].Type is NodeTypes.Input or NodeTypes.Agent or NodeTypes.Output).ToArray();
                for (var index = 0; index < executableNodes.Length - 1; index++)
                {
                    var sourceId = executableNodes[index];
                    var targetId = executableNodes[index + 1];
                    var sourcePort = definition.Nodes[sourceId].Type == NodeTypes.Input ? "then" : "out";
                    execution.Add($"normalize-execution-{index:D3}", new(
                        new(sourceId, sourcePort), new(targetId, "execute"), "execution"));
                }
                candidate = definition with
                {
                    SchemaVersion = WorkflowSchemaVersions.UnifiedWorkflowGraph,
                    ExecutionConnections = execution.ToImmutable()
                };
            }
        }
        var issues = WorkflowValidator.Validate(candidate);
        if (!issues.IsEmpty)
            throw new ArgumentException("normalization.invalid: " + string.Join("; ", issues.Select(issue => $"{issue.Code} ({issue.Path}): {issue.Message}")), nameof(definition));
        return candidate;
    }

    private static WorkflowDefinition NormalizeLegacyConditionalTextGraph(WorkflowDefinition definition)
    {
        if (definition.SchemaVersion is not ("1.4" or "1.5" or "1.6" or "1.7" or "1.8" or "1.9" or "1.10" or
            "1.11" or "1.12" or "1.13" or "1.14" or "1.15" or "1.16" or "1.17" or "1.18" or "1.19" or "1.20" or "1.21" or "1.22") ||
            definition.ExecutionConnections is { Count: > 0 } ||
            definition.Nodes.Values.Any(node => node.Type is not
                (NodeTypes.Input or NodeTypes.Output or NodeTypes.Condition or NodeTypes.Merge or NodeTypes.Uppercase or NodeTypes.Reverse)))
            throw new ArgumentException("normalization.conditional-unsafe: Only a legacy conditional graph with pure uppercase/reverse text paths has a verified translation.", nameof(definition));

        if (definition.Nodes.Values.Count(node => node.Type == NodeTypes.Condition) != 1 ||
            definition.Nodes.Values.Count(node => node.Type == NodeTypes.Merge) != 1)
            throw new ArgumentException("normalization.conditional-topology: Conversion requires exactly one condition and one matching merge.", nameof(definition));

        var conditionId = definition.Nodes.Single(pair => pair.Value.Type == NodeTypes.Condition).Key;
        var mergeId = definition.Nodes.Single(pair => pair.Value.Type == NodeTypes.Merge).Key;
        var inputId = definition.Nodes.Single(pair => pair.Value.Type == NodeTypes.Input).Key;
        var outputId = definition.Nodes.Single(pair => pair.Value.Type == NodeTypes.Output).Key;
        var outgoing = definition.Connections.Values.GroupBy(edge => edge.Source.NodeId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        var incoming = definition.Connections.Values.GroupBy(edge => edge.Target.NodeId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        var conditionEdges = outgoing.GetValueOrDefault(conditionId, []);
        var trueEdge = conditionEdges.SingleOrDefault(edge => edge.Source.PortId == "true");
        var falseEdge = conditionEdges.SingleOrDefault(edge => edge.Source.PortId == "false");
        var mergeEdge = outgoing.GetValueOrDefault(mergeId, []).SingleOrDefault(edge => edge.Source.PortId == "out");
        if (trueEdge is null || falseEdge is null || mergeEdge is null ||
            !TryWalkConditionalPath(trueEdge.Target.NodeId, mergeId, outgoing, definition.Nodes, out var truePath) ||
            !TryWalkConditionalPath(falseEdge.Target.NodeId, mergeId, outgoing, definition.Nodes, out var falsePath) ||
            truePath.Overlaps(falsePath))
            throw new ArgumentException("normalization.conditional-topology: Both condition outputs must follow disjoint linear pure-text paths into the same merge.", nameof(definition));

        if (!TryWalkConditionalPrefix(incoming.GetValueOrDefault(conditionId, []), inputId, incoming, definition.Nodes, out var prefix) ||
            !TryWalkConditionalSuffix(mergeEdge.Target.NodeId, outputId, outgoing, definition.Nodes, out var suffix))
            throw new ArgumentException("normalization.conditional-topology: The graph must have one linear pure-text path from Input through the condition and merge to Output.", nameof(definition));

        var covered = prefix.Concat(truePath).Concat(falsePath).Concat(suffix)
            .Append(inputId).Append(conditionId).Append(mergeId).Append(outputId).ToHashSet(StringComparer.Ordinal);
        if (covered.Count != definition.Nodes.Count || incoming.GetValueOrDefault(mergeId, []).Length != 2 ||
            incoming.GetValueOrDefault(outputId, []).Length != 1 || outgoing.GetValueOrDefault(inputId, []).Length != 1)
            throw new ArgumentException("normalization.conditional-topology: The graph contains bypasses, shared nodes, or additional paths outside the one conditional region.", nameof(definition));

        var execution = ImmutableSortedDictionary<string, ConnectionDefinition>.Empty.WithComparers(StringComparer.Ordinal)
            .Add("normalize-conditional-start", new(new(inputId, "then"), new(conditionId, "execute"), "execution"))
            .Add("normalize-conditional-true", new(new(conditionId, "true"), new(mergeId, "execute"), "execution"))
            .Add("normalize-conditional-false", new(new(conditionId, "false"), new(mergeId, "execute"), "execution"))
            .Add("normalize-conditional-finish", new(new(mergeId, "then"), new(outputId, "execute"), "execution"));
        return definition with
        {
            SchemaVersion = WorkflowSchemaVersions.UnifiedWorkflowGraph,
            ExecutionConnections = execution
        };
    }

    private static bool TryWalkConditionalPath(string startId, string endId,
        IReadOnlyDictionary<string, ConnectionDefinition[]> outgoing, ImmutableSortedDictionary<string, NodeDefinition> nodes,
        out HashSet<string> path)
    {
        path = new(StringComparer.Ordinal);
        var currentId = startId;
        while (currentId != endId)
        {
            if (!nodes.TryGetValue(currentId, out var node) || node.Type is not (NodeTypes.Uppercase or NodeTypes.Reverse) ||
                !path.Add(currentId) || outgoing.GetValueOrDefault(currentId, []).SingleOrDefault(edge => edge.Source.PortId == "out") is not { } edge)
                return false;
            currentId = edge.Target.NodeId;
        }
        return true;
    }

    private static bool TryWalkConditionalPrefix(ConnectionDefinition[] conditionInputs, string inputId,
        IReadOnlyDictionary<string, ConnectionDefinition[]> incoming, ImmutableSortedDictionary<string, NodeDefinition> nodes,
        out HashSet<string> path)
    {
        path = new(StringComparer.Ordinal);
        if (conditionInputs.Length != 1) return false;
        var currentId = conditionInputs[0].Source.NodeId;
        while (currentId != inputId)
        {
            if (!nodes.TryGetValue(currentId, out var node) || node.Type is not (NodeTypes.Uppercase or NodeTypes.Reverse) ||
                !path.Add(currentId) || incoming.GetValueOrDefault(currentId, []).SingleOrDefault(edge => edge.Target.PortId == "in") is not { } edge)
                return false;
            currentId = edge.Source.NodeId;
        }
        return true;
    }

    private static bool TryWalkConditionalSuffix(string startId, string outputId,
        IReadOnlyDictionary<string, ConnectionDefinition[]> outgoing, ImmutableSortedDictionary<string, NodeDefinition> nodes,
        out HashSet<string> path)
    {
        path = new(StringComparer.Ordinal);
        var currentId = startId;
        while (currentId != outputId)
        {
            if (!nodes.TryGetValue(currentId, out var node) || node.Type is not (NodeTypes.Uppercase or NodeTypes.Reverse) ||
                !path.Add(currentId) || outgoing.GetValueOrDefault(currentId, []).SingleOrDefault(edge => edge.Source.PortId == "out") is not { } edge)
                return false;
            currentId = edge.Target.NodeId;
        }
        return true;
    }

    private static WorkflowDefinition NormalizeLegacyDataLoop(WorkflowDefinition definition)
    {
        if (definition.SchemaVersion is not ("1.17" or "1.18" or "1.19" or "1.20" or "1.21" or "1.22") ||
            definition.ExecutionConnections is { Count: > 0 } ||
            definition.Nodes.Values.Any(node => node.Type is not
                (NodeTypes.Input or NodeTypes.Output or NodeTypes.Loop or NodeTypes.LoopBody or NodeTypes.Uppercase or NodeTypes.Reverse)))
            throw new ArgumentException("normalization.loop-unsafe: Only a validated bounded loop with pure string transforms has a verified legacy translation.", nameof(definition));

        var topology = LoopDataTopologyAnalyzer.Analyze(definition);
        if (topology is null)
            throw new ArgumentException("normalization.loop-topology: The bounded loop data feedback path is ambiguous or unsupported.", nameof(definition));

        var inputId = definition.EntryNodeId;
        var outputId = definition.Nodes.Single(node => node.Value.Type == NodeTypes.Output).Key;
        var execution = ImmutableSortedDictionary<string, ConnectionDefinition>.Empty.WithComparers(StringComparer.Ordinal)
            .Add("normalize-loop-initial", new(new(inputId, "then"), new(topology.LoopNodeId, "execute"), "execution"))
            .Add("normalize-loop-body", new(new(topology.LoopNodeId, "body"), new(topology.BodyAnchorNodeId, "execute"), "execution"))
            .Add("normalize-loop-return", new(new(topology.BodyAnchorNodeId, "then"), new(topology.LoopNodeId, "execute"), "execution"))
            .Add("normalize-loop-done", new(new(topology.LoopNodeId, "done"), new(outputId, "execute"), "execution"));

        return definition with
        {
            SchemaVersion = WorkflowSchemaVersions.UnifiedWorkflowGraph,
            ExecutionConnections = execution
        };
    }
}

public sealed record WorkflowNormalizationDiagnostic(string Code, string Path, string Message);

/// <summary>Immutable evidence for the exact source-to-initial-target authoring conversion.</summary>
public sealed record WorkflowNormalizationProvenance(string ConversionId, Guid? SourceRevisionId,
    string SourceContentHash, string SourceSchema, string TargetContentHash, string TargetSchema,
    string NormalizerVersion, ImmutableArray<WorkflowNormalizationDiagnostic> Diagnostics,
    WorkflowDefinition SourceDefinition, WorkflowDefinition InitialTargetDefinition)
{
    public static string ComputeIdentity(string sourceHash, string sourceSchema, string targetHash,
        string targetSchema, string normalizerVersion) => WorkflowJson.ComputeTextHash(
            $"{sourceHash}\n{sourceSchema}\n{targetHash}\n{targetSchema}\n{normalizerVersion}");

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(ConversionId) || string.IsNullOrWhiteSpace(SourceContentHash) ||
            string.IsNullOrWhiteSpace(TargetContentHash) || string.IsNullOrWhiteSpace(SourceSchema) ||
            string.IsNullOrWhiteSpace(TargetSchema) || string.IsNullOrWhiteSpace(NormalizerVersion) ||
            Diagnostics.IsDefault || SourceDefinition is null || InitialTargetDefinition is null ||
            SourceDefinition.SchemaVersion != SourceSchema || InitialTargetDefinition.SchemaVersion != TargetSchema ||
            WorkflowJson.ComputeDraftHash(SourceDefinition) != SourceContentHash ||
            WorkflowJson.ComputeHash(InitialTargetDefinition) != TargetContentHash ||
            TargetSchema != WorkflowSchemaVersions.UnifiedWorkflowGraph ||
            ConversionId != ComputeIdentity(SourceContentHash, SourceSchema, TargetContentHash, TargetSchema, NormalizerVersion))
            throw new InvalidDataException("Workflow normalization provenance is incomplete or inconsistent.");
    }
}

public sealed record WorkflowNormalizationResult(WorkflowDefinition? Definition,
    WorkflowNormalizationProvenance? Provenance,
    ImmutableArray<WorkflowNormalizationDiagnostic> Diagnostics = default)
{
    public bool Succeeded => Definition is not null;
}
