using System.Collections.Immutable;

namespace VisualAgentWorkflow.Core;

public sealed record WorkflowCallUpgradeResult(WorkflowDefinition Definition,
    ImmutableArray<WorkflowCallParameterMapping> DroppedParameterMappings);

/// <summary>Explicit edits for immutable, revision-pinned workflow call nodes.</summary>
public static class WorkflowCallEditing
{
    /// <summary>
    /// Adds one pure JSON composition node to an existing schema-1.53 direct JSON call path.
    /// The call remains the execution terminal; this edit only replaces its data edge.
    /// </summary>
    public static WorkflowDefinition AppendSequenceWorkflowCallJsonCompositionDraft(WorkflowDefinition parent,
        string callNodeId, string compositionType, string pointer)
    {
        ArgumentNullException.ThrowIfNull(parent);
        ArgumentException.ThrowIfNullOrWhiteSpace(callNodeId);
        ArgumentNullException.ThrowIfNull(pointer);
        if (parent.SchemaVersion is not (WorkflowSchemaVersions.UnifiedWorkflowGraph or WorkflowSchemaVersions.SequenceWorkflowCallJsonGraph) ||
            compositionType is not (NodeTypes.JsonSelectString or NodeTypes.JsonProject))
            throw new ArgumentException("JSON composition authoring requires a schema 1.53 graph and a JSON selector or projection node.", nameof(parent));
        WorkflowJsonPointer.Parse(pointer);
        WorkflowValidator.ThrowIfInvalid(parent);
        var analysis = SequenceWorkflowCallJsonTopology.Analyze(parent.SchemaVersion == WorkflowSchemaVersions.UnifiedWorkflowGraph ? parent with { SchemaVersion = WorkflowSchemaVersions.SequenceWorkflowCallJsonGraph } : parent);
        var path = analysis.Paths.SingleOrDefault(candidate => candidate.CallNodeId == callNodeId);
        if (!analysis.IsValid || path is null || path.JsonNodeId is not null)
            throw new ArgumentException("Choose a valid direct JSON WorkflowCall path without an existing composition node.", nameof(callNodeId));
        var outputId = parent.Nodes.Single(entry => entry.Value.Type == NodeTypes.Output).Key;
        var dataEdge = parent.Connections.SingleOrDefault(entry =>
            entry.Value.Source == new PortReference(callNodeId, "out") &&
            entry.Value.Target == new PortReference(outputId, "in"));
        if (dataEdge.Value is null || parent.Connections.Values.Any(edge =>
                edge.Target.NodeId == outputId && edge.Target.PortId == "in" && edge != dataEdge.Value))
            throw new ArgumentException("The direct JSON call must be the only data source for Output.", nameof(callNodeId));

        var compositionId = NewId(compositionType == NodeTypes.JsonSelectString ? "json-select" : "json-project");
        var composition = compositionType == NodeTypes.JsonSelectString
            ? new NodeDefinition(NodeTypes.JsonSelectString, "1.0.0") { JsonPropertySelection = new(pointer) }
            : new NodeDefinition(NodeTypes.JsonProject, "1.0.0")
            {
                JsonProjection = new([new JsonProjectionField("value", pointer)])
            };
        var updated = parent with
        {
            Nodes = parent.Nodes.Add(compositionId, composition)
                .SetItem(outputId, parent.Nodes[outputId] with
                {
                    OutputDataType = compositionType == NodeTypes.JsonSelectString ? WorkflowDataTypes.String : WorkflowDataTypes.Json
                }),
            Connections = parent.Connections.Remove(dataEdge.Key)
                .Add(NewId("workflow-call-json-data"), new(new(callNodeId, "out"), new(compositionId, "in"), "data"))
                .Add(NewId("workflow-call-json-data"), new(new(compositionId, "out"), new(outputId, "in"), "data"))
        };
        WorkflowJson.SerializeDraft(updated);
        return updated;
    }

    /// <summary>
    /// Adds one compatible pure JSON node to a schema-1.54 chain. Only a JSON Project may be
    /// extended, because SelectString is a terminal string-producing node.
    /// </summary>
    public static WorkflowDefinition AppendSequenceWorkflowCallJsonChainCompositionDraft(WorkflowDefinition parent,
        string callNodeId, string compositionType, string pointer)
    {
        ArgumentNullException.ThrowIfNull(parent);
        ArgumentException.ThrowIfNullOrWhiteSpace(callNodeId);
        ArgumentNullException.ThrowIfNull(pointer);
        if (parent.SchemaVersion is not (WorkflowSchemaVersions.UnifiedWorkflowGraph or WorkflowSchemaVersions.SequenceWorkflowCallJsonChainGraph) ||
            compositionType is not (NodeTypes.JsonSelectString or NodeTypes.JsonProject))
            throw new ArgumentException("JSON chain authoring requires a schema 1.54 graph and a JSON selector or projection node.", nameof(parent));
        WorkflowJsonPointer.Parse(pointer);
        WorkflowValidator.ThrowIfInvalid(parent);
        var analysis = SequenceWorkflowCallJsonChainTopology.Analyze(parent.SchemaVersion == WorkflowSchemaVersions.UnifiedWorkflowGraph ? parent with { SchemaVersion = WorkflowSchemaVersions.SequenceWorkflowCallJsonChainGraph } : parent);
        var path = analysis.Paths.SingleOrDefault(candidate => candidate.CallNodeId == callNodeId);
        if (!analysis.IsValid || path is null || path.JsonNodeIds.Length != 1 ||
            parent.Nodes[path.JsonNodeIds[0]].Type != NodeTypes.JsonProject)
            throw new ArgumentException("Choose a valid schema 1.54 path with one JSON projection; selectors are terminal.", nameof(callNodeId));

        var previousId = path.JsonNodeIds[0];
        var outputId = parent.Nodes.Single(entry => entry.Value.Type == NodeTypes.Output).Key;
        var dataEdge = parent.Connections.SingleOrDefault(entry =>
            entry.Value.Source == new PortReference(previousId, "out") &&
            entry.Value.Target == new PortReference(outputId, "in"));
        if (dataEdge.Value is null)
            throw new ArgumentException("The existing JSON projection must be the only data source for Output.", nameof(callNodeId));

        var compositionId = NewId(compositionType == NodeTypes.JsonSelectString ? "json-select" : "json-project");
        var composition = compositionType == NodeTypes.JsonSelectString
            ? new NodeDefinition(NodeTypes.JsonSelectString, "1.0.0") { JsonPropertySelection = new(pointer) }
            : new NodeDefinition(NodeTypes.JsonProject, "1.0.0")
            {
                JsonProjection = new([new JsonProjectionField("value", pointer)])
            };
        var updated = parent with
        {
            Nodes = parent.Nodes.Add(compositionId, composition)
                .SetItem(outputId, parent.Nodes[outputId] with
                {
                    OutputDataType = compositionType == NodeTypes.JsonSelectString ? WorkflowDataTypes.String : WorkflowDataTypes.Json
                }),
            Connections = parent.Connections.Remove(dataEdge.Key)
                .Add(NewId("workflow-call-json-chain-data"), new(new(previousId, "out"), new(compositionId, "in"), "data"))
                .Add(NewId("workflow-call-json-chain-data"), new(new(compositionId, "out"), new(outputId, "in"), "data"))
        };
        WorkflowJson.SerializeDraft(updated);
        return updated;
    }

    /// <summary>
    /// Adds the bounded schema-1.55 object merge after a single JSON projection. The child
    /// output is deliberately fanned out to both the projection and merge, while execution
    /// remains the existing WorkflowCall-to-Output barrier.
    /// </summary>
    public static WorkflowDefinition AppendSequenceWorkflowCallJsonMergeDraft(WorkflowDefinition parent,
        string callNodeId)
    {
        ArgumentNullException.ThrowIfNull(parent);
        ArgumentException.ThrowIfNullOrWhiteSpace(callNodeId);
        if (parent.SchemaVersion is not (WorkflowSchemaVersions.UnifiedWorkflowGraph or WorkflowSchemaVersions.SequenceWorkflowCallJsonChainGraph))
            throw new ArgumentException("JSON merge authoring requires a schema 1.54 graph.", nameof(parent));

        WorkflowValidator.ThrowIfInvalid(parent);
        var analysis = SequenceWorkflowCallJsonChainTopology.Analyze(parent.SchemaVersion == WorkflowSchemaVersions.UnifiedWorkflowGraph ? parent with { SchemaVersion = WorkflowSchemaVersions.SequenceWorkflowCallJsonChainGraph } : parent);
        var path = analysis.Paths.SingleOrDefault(candidate => candidate.CallNodeId == callNodeId);
        if (!analysis.IsValid || path is null || path.JsonNodeIds.Length != 1 ||
            parent.Nodes[path.JsonNodeIds[0]].Type != NodeTypes.JsonProject)
            throw new ArgumentException("Choose a valid schema 1.54 path with one JSON projection before adding a merge.", nameof(callNodeId));

        var projectionId = path.JsonNodeIds[0];
        var outputId = parent.Nodes.Single(entry => entry.Value.Type == NodeTypes.Output).Key;
        var dataEdge = parent.Connections.SingleOrDefault(entry =>
            entry.Value.Source == new PortReference(projectionId, "out") &&
            entry.Value.Target == new PortReference(outputId, "in"));
        if (dataEdge.Value is null)
            throw new ArgumentException("The existing JSON projection must be the only data source for Output.", nameof(callNodeId));

        var mergeId = NewId("json-merge");
        var updated = parent with
        {
            SchemaVersion = parent.SchemaVersion == WorkflowSchemaVersions.UnifiedWorkflowGraph ? parent.SchemaVersion : WorkflowSchemaVersions.SequenceWorkflowCallJsonMergeGraph,
            Nodes = parent.Nodes.Add(mergeId, new NodeDefinition(NodeTypes.JsonMerge, "1.0.0")),
            Connections = parent.Connections.Remove(dataEdge.Key)
                .Add(NewId("workflow-call-json-merge-data"), new(new(callNodeId, "out"), new(mergeId, "left"), "data"))
                .Add(NewId("workflow-call-json-merge-data"), new(new(projectionId, "out"), new(mergeId, "right"), "data"))
                .Add(NewId("workflow-call-json-merge-data"), new(new(mergeId, "out"), new(outputId, "in"), "data"))
        };
        if (!SequenceWorkflowCallJsonMergeTopology.Analyze(updated.SchemaVersion == WorkflowSchemaVersions.UnifiedWorkflowGraph ? updated with { SchemaVersion = WorkflowSchemaVersions.SequenceWorkflowCallJsonMergeGraph } : updated).IsValid)
            throw new InvalidOperationException("The JSON merge authoring edit did not produce the bounded schema 1.55 topology.");
        WorkflowJson.SerializeDraft(updated);
        return updated;
    }

    /// <summary>Adds the bounded schema-1.56 JSON-array refinement and length node to a direct JSON call path.</summary>
    public static WorkflowDefinition AppendSequenceWorkflowCallJsonArrayDraft(WorkflowDefinition parent, string callNodeId)
    {
        ArgumentNullException.ThrowIfNull(parent);
        ArgumentException.ThrowIfNullOrWhiteSpace(callNodeId);
        if (parent.SchemaVersion is not (WorkflowSchemaVersions.UnifiedWorkflowGraph or WorkflowSchemaVersions.SequenceWorkflowCallJsonMergeGraph))
            throw new ArgumentException("JSON-array authoring requires a schema 1.55 graph.", nameof(parent));

        WorkflowValidator.ThrowIfInvalid(parent);
        var analysis = SequenceWorkflowCallJsonChainTopology.Analyze(parent with
        { SchemaVersion = WorkflowSchemaVersions.SequenceWorkflowCallJsonChainGraph });
        var path = analysis.Paths.SingleOrDefault(candidate => candidate.CallNodeId == callNodeId);
        if (!analysis.IsValid || path is null || path.JsonNodeIds.Length != 0)
            throw new ArgumentException("Choose a valid direct JSON WorkflowCall path without an existing merge.", nameof(callNodeId));

        var outputId = parent.Nodes.Single(entry => entry.Value.Type == NodeTypes.Output).Key;
        var dataEdge = parent.Connections.SingleOrDefault(entry =>
            entry.Value.Source == new PortReference(callNodeId, "out") &&
            entry.Value.Target == new PortReference(outputId, "in"));
        if (dataEdge.Value is null)
            throw new ArgumentException("The direct JSON call must be the only data source for Output.", nameof(callNodeId));

        var toArrayId = NewId("json-to-array");
        var lengthId = NewId("array-length");
        var updated = parent with
        {
            SchemaVersion = parent.SchemaVersion == WorkflowSchemaVersions.UnifiedWorkflowGraph ? parent.SchemaVersion : WorkflowSchemaVersions.SequenceWorkflowCallJsonArrayGraph,
            Nodes = parent.Nodes
                .Add(toArrayId, new NodeDefinition(NodeTypes.JsonToArray, "1.0.0"))
                .Add(lengthId, new NodeDefinition(NodeTypes.ArrayLength, "1.0.0"))
                .SetItem(outputId, parent.Nodes[outputId] with { OutputDataType = WorkflowDataTypes.Integer }),
            Connections = parent.Connections.Remove(dataEdge.Key)
                .Add(NewId("workflow-call-json-array-data"), new(new(callNodeId, "out"), new(toArrayId, "in"), "data"))
                .Add(NewId("workflow-call-json-array-data"), new(new(toArrayId, "out"), new(lengthId, "in"), "data"))
                .Add(NewId("workflow-call-json-array-data"), new(new(lengthId, "out"), new(outputId, "in"), "data"))
        };
        if (!SequenceWorkflowCallJsonArrayTopology.Analyze(updated.SchemaVersion == WorkflowSchemaVersions.UnifiedWorkflowGraph ? updated with { SchemaVersion = WorkflowSchemaVersions.SequenceWorkflowCallJsonArrayGraph } : updated).IsValid)
            throw new InvalidOperationException("The JSON-array authoring edit did not produce the bounded schema 1.56 topology.");
        WorkflowJson.SerializeDraft(updated);
        return updated;
    }

    /// <summary>Adds the bounded schema-1.57 integer predicate and failure route to a direct array-length call path.</summary>
    public static WorkflowDefinition AppendSequenceWorkflowCallJsonControlDraft(WorkflowDefinition parent, string callNodeId, long expectedLength)
    {
        ArgumentNullException.ThrowIfNull(parent);
        ArgumentException.ThrowIfNullOrWhiteSpace(callNodeId);
        if (parent.SchemaVersion is not (WorkflowSchemaVersions.UnifiedWorkflowGraph or WorkflowSchemaVersions.SequenceWorkflowCallJsonArrayGraph))
            throw new ArgumentException("JSON control authoring requires a schema 1.56 graph.", nameof(parent));

        WorkflowValidator.ThrowIfInvalid(parent);
        var analysis = SequenceWorkflowCallJsonArrayTopology.Analyze(parent.SchemaVersion == WorkflowSchemaVersions.UnifiedWorkflowGraph ? parent with { SchemaVersion = WorkflowSchemaVersions.SequenceWorkflowCallJsonArrayGraph } : parent);
        var path = analysis.Paths.SingleOrDefault(candidate => candidate.CallNodeId == callNodeId);
        if (!analysis.IsValid || path is null)
            throw new ArgumentException("Choose a valid bounded JSON-array WorkflowCall path.", nameof(callNodeId));

        var outputId = parent.Nodes.Single(entry => entry.Value.Type == NodeTypes.Output).Key;
        var executionEdge = parent.ExecutionConnections!.SingleOrDefault(entry =>
            entry.Value.Source == new PortReference(callNodeId, "out") &&
            entry.Value.Target == new PortReference(outputId, "execute"));
        if (executionEdge.Value is null)
            throw new ArgumentException("The JSON-array call must directly execute Output before control routing is added.", nameof(callNodeId));
        var executionConnections = parent.ExecutionConnections!;

        var equalsId = NewId("integer-equals");
        var constantId = NewId("integer");
        var branchId = NewId("branch");
        var terminateId = NewId("terminate");
        var updated = parent with
        {
            SchemaVersion = parent.SchemaVersion == WorkflowSchemaVersions.UnifiedWorkflowGraph ? parent.SchemaVersion : WorkflowSchemaVersions.SequenceWorkflowCallJsonControlGraph,
            Nodes = parent.Nodes
                .Add(equalsId, new NodeDefinition(NodeTypes.IntegerEquals, "1.0.0"))
                .Add(constantId, new NodeDefinition(NodeTypes.IntegerConstant, "1.0.0") { IntegerConstant = new(expectedLength) })
                .Add(branchId, new NodeDefinition(NodeTypes.BooleanBranch, "1.0.0"))
                .Add(terminateId, new NodeDefinition(NodeTypes.Terminate, "1.0.0")
                {
                    Termination = new(WorkflowTerminationOutcomes.Failure)
                }),
            Connections = parent.Connections
                .Add(NewId("workflow-call-json-control-data"), new(new(path.LengthNodeId, "out"), new(equalsId, "left"), "data"))
                .Add(NewId("workflow-call-json-control-data"), new(new(constantId, "out"), new(equalsId, "right"), "data"))
                .Add(NewId("workflow-call-json-control-data"), new(new(equalsId, "out"), new(branchId, "condition"), "data")),
            ExecutionConnections = executionConnections.Remove(executionEdge.Key)
                .Add(NewId("workflow-call-json-control-execution"), new(new(callNodeId, "out"), new(branchId, "execute"), "execution"))
                .Add(NewId("workflow-call-json-control-execution"), new(new(branchId, "true"), new(outputId, "execute"), "execution"))
                .Add(NewId("workflow-call-json-control-execution"), new(new(branchId, "false"), new(terminateId, "execute"), "execution"))
        };
        if (!SequenceWorkflowCallJsonControlTopology.Analyze(updated.SchemaVersion == WorkflowSchemaVersions.UnifiedWorkflowGraph ? updated with { SchemaVersion = WorkflowSchemaVersions.SequenceWorkflowCallJsonControlGraph } : updated).IsValid)
            throw new InvalidOperationException("The JSON-control authoring edit did not produce the bounded schema 1.57 topology.");
        WorkflowJson.SerializeDraft(updated);
        return updated;
    }

    /// <summary>
    /// Adds a new pinned JSON child path to a clean schema-1.52 Sequence draft. The edit
    /// creates a fresh Sequence output and moves the draft to schema 1.53; it deliberately
    /// does not reinterpret an existing string call or an Agent/transform path.
    /// </summary>
    public static WorkflowDefinition AppendSequenceWorkflowCallJsonDraft(WorkflowDefinition parent,
        string sequenceNodeId, PublishedWorkflowRevision target)
    {
        ArgumentNullException.ThrowIfNull(parent);
        ArgumentException.ThrowIfNullOrWhiteSpace(sequenceNodeId);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(target.Revision);
        if (parent.SchemaVersion is not (WorkflowSchemaVersions.UnifiedWorkflowGraph or WorkflowSchemaVersions.SequenceWorkflowCallAgentGraph) ||
            target.Version < 1 || target.Revision.Definition.WorkflowId == parent.WorkflowId)
            throw new ArgumentException("JSON WorkflowCall authoring requires a schema 1.52 draft and a different published child.", nameof(parent));
        WorkflowValidator.ThrowIfInvalid(parent);
        if (!HasJsonBoundary(target.Revision.Definition))
            throw new ArgumentException("The child workflow must have JSON input and output boundaries.", nameof(target));
        if (parent.Nodes.Values.Any(node => node.Type is NodeTypes.WorkflowCall or NodeTypes.Agent or
                NodeTypes.SequenceUppercase or NodeTypes.SequenceReverse))
            throw new ArgumentException("JSON WorkflowCall authoring is limited to a clean direct-MCP Sequence draft; existing calls, Agents, and transforms are not reinterpreted.", nameof(parent));

        var outputNode = parent.Nodes.SingleOrDefault(entry => entry.Value.Type == NodeTypes.Output);
        if (outputNode.Value is null || parent.Connections.Values.Any(edge => edge.Target.NodeId == outputNode.Key && edge.Target.PortId == "in"))
            throw new ArgumentException("The workflow Output must not already have a data value when adding a JSON path.", nameof(parent));

        var withOutput = GraphEditing.AddSequenceOutput(parent, sequenceNodeId);
        var sequence = withOutput.Nodes[sequenceNodeId];
        var outputId = sequence.Sequence!.Outputs[^1].Id;
        var outputExecution = withOutput.ExecutionConnections!.Single(entry =>
            entry.Value.Source.NodeId == sequenceNodeId && entry.Value.Source.PortId == SequenceOutputLimits.PinId(outputId));
        var callId = NewId("workflow-call-json");
        var call = new NodeDefinition(NodeTypes.WorkflowCall, "1.0.0")
        {
            WorkflowCall = new(target.Revision.Definition.WorkflowId, target.Revision.RevisionId, target.Version,
                target.Revision.ContentHash, target.Revision.Definition)
        };
        var updatedExecution = withOutput.ExecutionConnections!
            .SetItem(outputExecution.Key, outputExecution.Value with { Target = new(callId, "execute") })
            .Add(NewId("workflow-call-json"), new(new(callId, "out"), new(outputNode.Key, "execute"), "execution"));
        var updated = withOutput with
        {
            SchemaVersion = parent.SchemaVersion == WorkflowSchemaVersions.UnifiedWorkflowGraph ? parent.SchemaVersion : WorkflowSchemaVersions.SequenceWorkflowCallJsonGraph,
            Nodes = withOutput.Nodes
                .SetItem(outputNode.Key, outputNode.Value with { OutputDataType = WorkflowDataTypes.Json })
                .Add(callId, call),
            Connections = withOutput.Connections.Add(NewId("workflow-call-json-data"),
                new(new(callId, "out"), new(outputNode.Key, "in"), "data")),
            ExecutionConnections = updatedExecution
        };
        WorkflowJson.SerializeDraft(updated);
        return updated;
    }

    /// <summary>
    /// Adds one deterministic transform after a terminal call while preserving schema 1.50 as
    /// an editable draft. Explicit draft promotion is required before the graph can run.
    /// </summary>
    public static WorkflowDefinition AppendSequenceWorkflowCallTransformDraft(WorkflowDefinition parent,
        string callNodeId, string transformType)
    {
        ArgumentNullException.ThrowIfNull(parent);
        ArgumentException.ThrowIfNullOrWhiteSpace(callNodeId);
        if (parent.SchemaVersion is not (WorkflowSchemaVersions.UnifiedWorkflowGraph or WorkflowSchemaVersions.SequenceWorkflowCallGraph) ||
            parent.ExecutionConnections is not { } execution ||
            !parent.Nodes.TryGetValue(callNodeId, out var call) || call.Type != NodeTypes.WorkflowCall)
            throw new ArgumentException("Transform authoring requires a schema 1.50 WorkflowCall draft.", nameof(parent));
        if (transformType is not (NodeTypes.SequenceUppercase or NodeTypes.SequenceReverse))
            throw new ArgumentException("Only the bounded uppercase or reverse transform is supported.", nameof(transformType));
        if (!SequenceWorkflowCallTopology.Analyze(parent).IsValid)
            throw new ArgumentException("The source WorkflowCall topology must be valid before adding a transform.", nameof(parent));

        var output = parent.Nodes.Single(entry => entry.Value.Type == NodeTypes.Output).Key;
        var edge = execution.Single(entry => entry.Value.Source.NodeId == callNodeId &&
            entry.Value.Source.PortId == "out" && entry.Value.Target.NodeId == output);
        var transformId = "workflow-call-transform-" + Guid.NewGuid().ToString("N");
        var updated = parent with
        {
            Nodes = parent.Nodes.Add(transformId, new NodeDefinition(transformType, "1.0.0")),
            ExecutionConnections = execution
                .SetItem(edge.Key, edge.Value with { Target = new(transformId, "execute") })
                .Add("workflow-call-transform-" + Guid.NewGuid().ToString("N"),
                    new(new(transformId, "then"), new(output, "execute"), "execution"))
        };
        WorkflowJson.SerializeDraft(updated);
        return updated;
    }

    /// <summary>
    /// Appends a pinned child to one direct-MCP Sequence output path while preserving the
    /// source schema as a saveable draft. The explicit draft-promotion boundary upgrades it
    /// only after the complete schema-1.50 topology validates.
    /// </summary>
    public static WorkflowDefinition AppendSequenceWorkflowCallDraft(WorkflowDefinition parent,
        string sequenceNodeId, string outputId, PublishedWorkflowRevision target)
    {
        ArgumentNullException.ThrowIfNull(parent);
        ArgumentException.ThrowIfNullOrWhiteSpace(sequenceNodeId);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputId);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(target.Revision);
        if (parent.SchemaVersion is not (WorkflowSchemaVersions.UnifiedWorkflowGraph or WorkflowSchemaVersions.SequenceTransformChainGraph))
            throw new ArgumentException("WorkflowCall authoring requires a schema 1.49 Sequence draft.", nameof(parent));
        if (target.Version < 1 || target.Revision.Definition.WorkflowId == parent.WorkflowId)
            throw new ArgumentException("Choose a different published child workflow revision.", nameof(target));
        WorkflowValidator.ThrowIfInvalid(target.Revision.Definition);
        if (!HasStringBoundary(target.Revision.Definition))
            throw new ArgumentException("The child workflow must have string input and output boundaries.", nameof(target));

        if (!parent.Nodes.TryGetValue(sequenceNodeId, out var sequence) || sequence.Type != NodeTypes.Sequence ||
            sequence.Sequence is null || !sequence.Sequence.Outputs.Any(output => output.Id == outputId))
            throw new ArgumentException("Choose an existing Sequence output.", nameof(outputId));
        WorkflowValidator.ThrowIfInvalid(parent);
        var analysis = SequencePathTopologyAnalyzer.Analyze(parent);
        var path = analysis.Paths.SingleOrDefault(candidate => candidate.OutputId == outputId);
        if (path is null || !path.IsDirectMcpPath)
            throw new ArgumentException("Choose an output with a valid direct-MCP path. Calls must be terminal and cannot follow transforms or Agents.", nameof(outputId));

        var execution = parent.ExecutionConnections!;
        var outputNode = parent.Nodes.SingleOrDefault(entry => entry.Value.Type == NodeTypes.Output);
        if (outputNode.Value is null) throw new ArgumentException("The Sequence graph has no Output node.", nameof(parent));
        var terminalNodeId = path.NodeIds.IsEmpty ? sequenceNodeId : path.NodeIds[^1];
        var terminalPort = path.NodeIds.IsEmpty ? SequenceOutputLimits.PinId(outputId) : "then";
        var existingEdge = execution.SingleOrDefault(edge => edge.Value.Source.NodeId == terminalNodeId &&
            edge.Value.Source.PortId == terminalPort && edge.Value.Target.NodeId == outputNode.Key && edge.Value.Target.PortId == "execute");
        if (existingEdge.Value is null)
            throw new ArgumentException("The selected Sequence output must have exactly one direct path to Output.", nameof(outputId));

        var callId = NewId("workflow-call");
        var call = new NodeDefinition(NodeTypes.WorkflowCall, "1.0.0")
        {
            WorkflowCall = new(target.Revision.Definition.WorkflowId, target.Revision.RevisionId, target.Version,
                target.Revision.ContentHash, target.Revision.Definition)
        };
        var updatedExecution = execution
            .SetItem(existingEdge.Key, existingEdge.Value with { Target = new(callId, "execute") })
            .Add(NewId("workflow-call"), new(new(callId, "out"), new(outputNode.Key, "execute"), "execution"));
        var updated = parent with
        {
            Nodes = parent.Nodes.Add(callId, call),
            ExecutionConnections = updatedExecution
        };
        // This intentionally validates only the editor draft contract. Strict validation is
        // reserved for WorkflowDraftPromotion, which is the only path to schema 1.50.
        WorkflowJson.SerializeDraft(updated);
        return updated;
    }

    /// <summary>Updates a pinned call in a schema-1.49 call-bearing draft without changing its node ID.</summary>
    public static WorkflowCallUpgradeResult UpgradeSequenceWorkflowCallDraft(WorkflowDefinition parent,
        string nodeId, PublishedWorkflowRevision target)
    {
        ArgumentNullException.ThrowIfNull(parent);
        ArgumentException.ThrowIfNullOrWhiteSpace(nodeId);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(target.Revision);
        if (parent.SchemaVersion is not (WorkflowSchemaVersions.UnifiedWorkflowGraph or WorkflowSchemaVersions.SequenceTransformChainGraph))
            throw new ArgumentException("WorkflowCall authoring requires a schema 1.49 Sequence draft.", nameof(parent));
        if (!parent.Nodes.TryGetValue(nodeId, out var node) || node.Type != NodeTypes.WorkflowCall || node.WorkflowCall is not { } call)
            throw new ArgumentException("Choose an existing WorkflowCall node.", nameof(nodeId));
        if (call.WorkflowId != target.Revision.Definition.WorkflowId || target.Version < 1)
            throw new ArgumentException("The replacement must be another revision of the same child workflow.", nameof(target));
        WorkflowValidator.ThrowIfInvalid(target.Revision.Definition);
        if (!HasStringBoundary(target.Revision.Definition))
            throw new ArgumentException("The child workflow must have string input and output boundaries.", nameof(target));

        var parentParameters = parent.Parameters ?? ImmutableSortedDictionary<string, WorkflowParameterDefinition>.Empty;
        var childParameters = target.Revision.Definition.Parameters ?? ImmutableSortedDictionary<string, WorkflowParameterDefinition>.Empty;
        var existingMappings = call.ParameterMappings ?? [];
        var retainedMappings = existingMappings.Where(mapping =>
            parentParameters.TryGetValue(mapping.ParentParameterId, out var parentParameter) &&
            childParameters.TryGetValue(mapping.ChildParameterId, out var childParameter) &&
            WorkflowParameters.AreCompatible(parentParameter, childParameter)).ToImmutableArray();
        var droppedMappings = existingMappings.Except(retainedMappings).ToImmutableArray();
        var updatedCall = call with
        {
            RevisionId = target.Revision.RevisionId,
            Version = target.Version,
            ContentHash = target.Revision.ContentHash,
            Definition = target.Revision.Definition,
            ParameterMappings = retainedMappings.IsEmpty ? null : retainedMappings
        };
        var updated = parent with { Nodes = parent.Nodes.SetItem(nodeId, node with { WorkflowCall = updatedCall }) };
        WorkflowJson.SerializeDraft(updated);
        return new(updated, droppedMappings);
    }

    public static WorkflowCallUpgradeResult Upgrade(WorkflowDefinition parent, string nodeId,
        PublishedWorkflowRevision target)
    {
        ArgumentNullException.ThrowIfNull(parent);
        ArgumentException.ThrowIfNullOrWhiteSpace(nodeId);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(target.Revision);
        WorkflowValidator.ThrowIfInvalid(parent);
        WorkflowValidator.ThrowIfInvalid(target.Revision.Definition);

        if (target.Version < 1) throw new ArgumentException("The target publication version is invalid.", nameof(target));
        if (!parent.Nodes.TryGetValue(nodeId, out var node) || node.Type != NodeTypes.WorkflowCall || node.WorkflowCall is not { } call)
            throw new ArgumentException("Choose an existing pinned workflow call node.", nameof(nodeId));
        if (call.WorkflowId != target.Revision.Definition.WorkflowId)
            throw new ArgumentException("A pinned call can only be upgraded to another revision of the same workflow.", nameof(target));
        if (call.RevisionId == target.Revision.RevisionId)
            throw new ArgumentException("This revision is already pinned by the selected call.", nameof(target));

        var parentParameters = parent.Parameters ?? ImmutableSortedDictionary<string, WorkflowParameterDefinition>.Empty;
        var childParameters = target.Revision.Definition.Parameters ?? ImmutableSortedDictionary<string, WorkflowParameterDefinition>.Empty;
        var existingMappings = call.ParameterMappings ?? [];
        var retainedMappings = existingMappings.Where(mapping =>
            parentParameters.TryGetValue(mapping.ParentParameterId, out var parentParameter) &&
            childParameters.TryGetValue(mapping.ChildParameterId, out var childParameter) &&
            WorkflowParameters.AreCompatible(parentParameter, childParameter)).ToImmutableArray();
        var droppedMappings = existingMappings.Except(retainedMappings).ToImmutableArray();
        var updatedCall = call with
        {
            RevisionId = target.Revision.RevisionId,
            Version = target.Version,
            ContentHash = target.Revision.ContentHash,
            Definition = target.Revision.Definition,
            ParameterMappings = retainedMappings.IsEmpty ? null : retainedMappings
        };
        var updated = GraphEditing.ReplaceNode(parent, nodeId, node with { WorkflowCall = updatedCall });
        return new(updated, droppedMappings);
    }

    private static bool HasStringBoundary(WorkflowDefinition definition)
    {
        var input = definition.Nodes.GetValueOrDefault(definition.EntryNodeId);
        var output = definition.Nodes.Values.FirstOrDefault(node => node.Type == NodeTypes.Output);
        return input is not null && output is not null &&
            WorkflowPorts.Outputs(input).SingleOrDefault()?.DataType == WorkflowDataTypes.String &&
            WorkflowPorts.Inputs(output).SingleOrDefault()?.DataType == WorkflowDataTypes.String;
    }

    private static bool HasJsonBoundary(WorkflowDefinition definition)
    {
        var input = definition.Nodes.GetValueOrDefault(definition.EntryNodeId);
        var output = definition.Nodes.Values.FirstOrDefault(node => node.Type == NodeTypes.Output);
        return input is not null && output is not null &&
            WorkflowPorts.Outputs(input).SingleOrDefault()?.DataType == WorkflowDataTypes.Json &&
            WorkflowPorts.Inputs(output).SingleOrDefault()?.DataType == WorkflowDataTypes.Json;
    }

    private static string NewId(string prefix) => prefix + "-" + Guid.NewGuid().ToString("N");
}
