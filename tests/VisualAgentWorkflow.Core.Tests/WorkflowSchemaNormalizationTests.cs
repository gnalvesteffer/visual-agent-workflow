using System.Collections.Immutable;
using System.Text.Json;
using VisualAgentWorkflow.Core;
using Xunit;

namespace VisualAgentWorkflow.Core.Tests;

public class WorkflowSchemaNormalizationTests
{
    private static WorkflowDefinition Blank() => new("1.0", "normalize", "input",
        new Dictionary<string, NodeDefinition>
        {
            ["input"] = new(NodeTypes.Input, "1.0.0"),
            ["output"] = new(NodeTypes.Output, "1.0.0")
        }.ToImmutableSortedDictionary(),
        new Dictionary<string, ConnectionDefinition>
        {
            ["direct"] = new(new("input", "out"), new("output", "in"), "data")
        }.ToImmutableSortedDictionary());

    private static WorkflowDefinition ConditionalTextGraph()
    {
        var graph = GraphEditing.InsertConditionalAfter(Blank(), "input", "out");
        var condition = graph.Nodes.Single(pair => pair.Value.Type == NodeTypes.Condition).Key;
        graph = GraphEditing.InsertNodeAfter(graph, condition, "true", new(NodeTypes.Uppercase, "1.0.0"));
        return GraphEditing.InsertNodeAfter(graph, condition, "false", new(NodeTypes.Reverse, "1.0.0"));
    }

    [Fact]
    public void RemovingUnifiedConfiguredParallelRegionUsesExecutionWiresAndKeepsSchema()
    {
        var source = WorkflowSchemaNormalization.NormalizeForEditing(Blank());
        var branches = ImmutableArray.Create(new ParallelBranchDescriptor("first", "First", "first"),
            new ParallelBranchDescriptor("second", "Second", "second"));
        source = source with
        {
            Nodes = source.Nodes
                .Add("fork", new(NodeTypes.ParallelFork, "1.0.0") { ParallelFork = new(branches) })
                .Add("first", new(NodeTypes.ParallelBranch, "1.0.0") { ParallelBranch = new("fork", "first") })
                .Add("second", new(NodeTypes.ParallelBranch, "1.0.0") { ParallelBranch = new("fork", "second") })
                .Add("join", new(NodeTypes.ParallelJoin, "1.0.0") { ParallelJoin = new("fork", ["first", "second"]) })
                .SetItem("output", source.Nodes["output"] with { OutputDataType = WorkflowDataTypes.Json }),
            Connections = source.Connections.Clear().Add("result", new(new("join", "out"), new("output", "in"), "data")),
            ExecutionConnections = source.ExecutionConnections!.Clear()
                .Add("start", new(new("input", "then"), new("fork", "execute"), "execution"))
                .Add("first", new(new("fork", "branch.first"), new("first", "execute"), "execution"))
                .Add("second", new(new("fork", "branch.second"), new("second", "execute"), "execution"))
                .Add("first-join", new(new("first", "then"), new("join", "branch.first"), "execution"))
                .Add("second-join", new(new("second", "then"), new("join", "branch.second"), "execution"))
                .Add("finish", new(new("join", "then"), new("output", "execute"), "execution"))
        };
        var original = WorkflowJson.Serialize(source);
        Assert.Equal(4, ParallelBlockEditing.Nodes(source, "fork").Length);
        var removed = ParallelBlockEditing.Remove(source, "fork");
        Assert.Equal("2.0", removed.SchemaVersion);
        Assert.Equal(2, removed.Nodes.Count);
        Assert.Empty(WorkflowValidator.Validate(removed));
        Assert.Equal(new PortReference("output", "execute"), Assert.Single(removed.ExecutionConnections!).Value.Target);
        Assert.Equal(original, WorkflowJson.Serialize(source));
    }

    [Fact]
    public void UnifiedLinearEditsAndReusePreserveExecutionAndSchema()
    {
        var source = WorkflowSchemaNormalization.NormalizeForEditing(Blank());
        var edited = WorkflowEditing.InsertAfter(source, "input", NodeTypes.Reverse);
        Assert.Equal("2.0", edited.SchemaVersion);
        Assert.Equal(source.ExecutionConnections, edited.ExecutionConnections);
        var nodeId = edited.Nodes.Single(entry => entry.Value.Type == NodeTypes.Reverse).Key;
        var extracted = WorkflowReuse.Extract(edited, WorkflowLayout.Create(edited), [nodeId], "reusable");
        Assert.Equal("2.0", extracted.Definition.SchemaVersion);
        Assert.Empty(WorkflowValidator.Validate(extracted.Definition));
        var revision = WorkflowRevision.Create(extracted.Definition, "test");
        var inserted = WorkflowReuse.Insert(source, WorkflowLayout.Create(source), "input",
            new PublishedWorkflowRevision(1, revision), extracted.Layout);
        Assert.Equal("2.0", inserted.Definition.SchemaVersion);
        Assert.Empty(WorkflowValidator.Validate(inserted.Definition));
    }

    [Fact]
    public void UnifiedPackagePinsProviderContractAndRoundTrips()
    {
        var source = WorkflowSchemaNormalization.NormalizeForEditing(Blank());
        var agent = new NodeDefinition(NodeTypes.Agent, "1.0.0")
        {
            Agent = new AgentConfiguration("provider", null, "Return text.", [])
            {
                ProviderContractHash = new string('b', 64), MaxTurns = 1, MaxTotalTokens = 4000
            }
        };
        source = source with
        {
            Nodes = source.Nodes.Add("agent", agent),
            Connections = source.Connections.Clear()
                .Add("agent-in", new(new("input", "out"), new("agent", "in"), "data"))
                .Add("agent-out", new(new("agent", "out"), new("output", "in"), "data")),
            ExecutionConnections = source.ExecutionConnections!.Clear()
                .Add("start", new(new("input", "then"), new("agent", "execute"), "execution"))
                .Add("finish", new(new("agent", "out"), new("output", "execute"), "execution"))
        };
        var package = WorkflowPackageJson.Create(source, WorkflowLayout.Create(source));
        Assert.Contains(package.Requirements, requirement => requirement.Kind == "agent-provider-contract" &&
            requirement.Id == "provider" && requirement.ContractHash == new string('b', 64));
        var restored = WorkflowPackageJson.Deserialize(WorkflowPackageJson.Serialize(source, WorkflowLayout.Create(source)));
        Assert.Equal("2.0", restored.Definition.SchemaVersion);
        Assert.Equal(WorkflowJson.ComputeHash(source), WorkflowJson.ComputeHash(restored.Definition));
        Assert.Throws<ArgumentException>(() => WorkflowEditing.OrderedNodes(source));
    }

    [Fact]
    public void ImplicitConversionIsDeterministicPreservesIdsAndLeavesSourceUntouched()
    {
        var source = WorkflowEditing.InsertAfter(Blank(), "input", NodeTypes.Uppercase);
        var original = WorkflowJson.Serialize(source);
        var result = WorkflowSchemaNormalization.NormalizeForEditing(source);
        Assert.Equal("2.0", result.SchemaVersion);
        Assert.Equal(source.Nodes.Keys, result.Nodes.Keys);
        Assert.Equal(source.Connections, result.Connections);
        Assert.Equal(WorkflowJson.Serialize(result), WorkflowJson.Serialize(WorkflowSchemaNormalization.NormalizeForEditing(source)));
        Assert.Equal(original, WorkflowJson.Serialize(source));
        Assert.Empty(WorkflowValidator.Validate(result));
        Assert.Same(result, WorkflowSchemaNormalization.NormalizeForEditing(result));
    }

    [Fact]
    public void NormalizationResultHasDeterministicSourceAndTargetProvenance()
    {
        var legacy = WorkflowEditing.InsertAfter(Blank(), "input", NodeTypes.Uppercase);

        var first = WorkflowSchemaNormalization.NormalizeForEditingWithProvenance(legacy);
        var second = WorkflowSchemaNormalization.NormalizeForEditingWithProvenance(legacy);

        Assert.True(first.Succeeded);
        Assert.NotNull(first.Provenance);
        Assert.Equal(first.Provenance.ConversionId, second.Provenance!.ConversionId);
        Assert.Equal(WorkflowJson.ComputeDraftHash(legacy), first.Provenance.SourceContentHash);
        Assert.Equal(WorkflowJson.ComputeHash(first.Definition!), first.Provenance.TargetContentHash);
        Assert.Equal("1.0", first.Provenance.SourceSchema);
        Assert.Equal("2.0", first.Provenance.TargetSchema);
        Assert.Equal(legacy, first.Provenance.SourceDefinition);
        Assert.Equal(first.Definition, first.Provenance.InitialTargetDefinition);
        Assert.Empty(first.Provenance.Diagnostics);
        first.Provenance.Validate();
    }

    [Fact]
    public void FailedNormalizationReturnsDiagnosticsWithoutPartialTarget()
    {
        var legacy = Blank() with { Nodes = Blank().Nodes.Add("parallel", new(NodeTypes.ParallelFork, "1.0.0")) };

        var result = WorkflowSchemaNormalization.NormalizeForEditingWithProvenance(legacy);

        Assert.False(result.Succeeded);
        Assert.Null(result.Definition);
        Assert.Null(result.Provenance);
        Assert.NotEmpty(result.Diagnostics);
        Assert.StartsWith("normalization.", result.Diagnostics[0].Code);
    }

    [Theory]
    [InlineData("1.17")]
    [InlineData("1.18")]
    [InlineData("1.19")]
    [InlineData("1.20")]
    [InlineData("1.21")]
    [InlineData("1.22")]
    public void Ambiguous_serialized_legacy_loop_returns_precise_diagnostics_without_mutating_source(string schema)
    {
        var validLegacy = GraphEditing.InsertBoundedLoopAfter(Blank() with { SchemaVersion = schema }, "input", "out");
        var loopId = validLegacy.Nodes.Single(pair => pair.Value.Type == NodeTypes.Loop).Key;
        var bodyId = validLegacy.Nodes.Single(pair => pair.Value.Type == NodeTypes.LoopBody).Key;
        var serializedSource = WorkflowJson.Serialize(validLegacy);
        var imported = WorkflowJson.Deserialize(serializedSource);
        var ambiguous = imported with
        {
            Connections = imported.Connections.Add("duplicate-return",
                new(new(bodyId, "out"), new(loopId, "in"), "data"))
        };
        var malformedJson = JsonSerializer.Serialize(ambiguous);
        var malformedImport = JsonSerializer.Deserialize<WorkflowDefinition>(malformedJson)!;
        var issues = WorkflowValidator.Validate(malformedImport);

        var result = WorkflowSchemaNormalization.NormalizeForEditingWithProvenance(malformedImport);

        Assert.NotEmpty(issues);
        Assert.False(result.Succeeded);
        Assert.Null(result.Definition);
        Assert.Null(result.Provenance);
        Assert.All(issues, issue => Assert.Contains(result.Diagnostics, diagnostic =>
            diagnostic.Code == $"normalization.{issue.Code}" && diagnostic.Path == issue.Path &&
            diagnostic.Message == issue.Message));
        Assert.Equal(malformedJson, JsonSerializer.Serialize(malformedImport));
        Assert.Equal(serializedSource, WorkflowJson.Serialize(imported));
    }

    [Fact]
    public void ImplicitLinearAgentGraphAddsExecutionEdgesAroundLazyTextNodes()
    {
        var source = new WorkflowDefinition("1.21", "legacy-agent", "input",
            new Dictionary<string, NodeDefinition>
            {
                ["input"] = new(NodeTypes.Input, "1.0.0"),
                ["upper"] = new(NodeTypes.Uppercase, "1.0.0"),
                ["agent"] = new(NodeTypes.Agent, "1.0.0", Agent: new("fixture", null, "Add a marker.", [])),
                ["reverse"] = new(NodeTypes.Reverse, "1.0.0"),
                ["output"] = new(NodeTypes.Output, "1.0.0")
            }.ToImmutableSortedDictionary(),
            new Dictionary<string, ConnectionDefinition>
            {
                ["input-upper"] = new(new("input", "out"), new("upper", "in"), "data"),
                ["upper-agent"] = new(new("upper", "out"), new("agent", "in"), "data"),
                ["agent-reverse"] = new(new("agent", "out"), new("reverse", "in"), "data"),
                ["reverse-output"] = new(new("reverse", "out"), new("output", "in"), "data")
            }.ToImmutableSortedDictionary());

        var original = WorkflowJson.Serialize(source);
        var normalized = WorkflowSchemaNormalization.NormalizeForEditing(source);

        Assert.Equal("2.0", normalized.SchemaVersion);
        Assert.Equal(source.Connections, normalized.Connections);
        Assert.Equal(original, WorkflowJson.Serialize(source));
        Assert.Equal(new[]
        {
            new ConnectionDefinition(new("input", "then"), new("agent", "execute"), "execution"),
            new ConnectionDefinition(new("agent", "out"), new("output", "execute"), "execution")
        }, normalized.ExecutionConnections!.Values);
        Assert.Empty(WorkflowValidator.Validate(normalized));
    }

    [Fact]
    public void LegacyPureDataLoopNormalizesToUnifiedActivationAndPreservesTheFeedbackWire()
    {
        var source = GraphEditing.InsertBoundedLoopAfter(Blank(), "input", "out");
        var anchorId = source.Nodes.Single(pair => pair.Value.Type == NodeTypes.LoopBody).Key;
        source = GraphEditing.InsertNodeAfter(source, anchorId, "out", new(NodeTypes.Reverse, "1.0.0"));
        var firstReverseId = source.Nodes.Single(pair => pair.Value.Type == NodeTypes.Reverse).Key;
        source = GraphEditing.InsertNodeAfter(source, firstReverseId, "out", new(NodeTypes.Reverse, "1.0.0"));
        var sourceJson = WorkflowJson.Serialize(source);

        var normalized = WorkflowSchemaNormalization.NormalizeForEditing(source);

        Assert.Equal("2.0", normalized.SchemaVersion);
        Assert.Equal(source.Nodes.Keys, normalized.Nodes.Keys);
        Assert.Equal(source.Connections, normalized.Connections);
        Assert.Equal(sourceJson, WorkflowJson.Serialize(source));
        Assert.Equal(4, normalized.ExecutionConnections!.Count);
        Assert.Contains(normalized.ExecutionConnections.Values, edge => edge.Source == new PortReference("input", "then") &&
            edge.Target.NodeId == normalized.Nodes.Single(pair => pair.Value.Type == NodeTypes.Loop).Key);
        Assert.Contains(normalized.ExecutionConnections.Values, edge => edge.Source.NodeId == anchorId &&
            edge.Target == new PortReference(normalized.Nodes.Single(pair => pair.Value.Type == NodeTypes.Loop).Key, "feedback"));
        Assert.Equal(WorkflowJson.Serialize(normalized),
            WorkflowJson.Serialize(WorkflowSchemaNormalization.NormalizeForEditing(source)));
        Assert.Empty(WorkflowValidator.Validate(normalized));
    }

    [Theory]
    [InlineData("1.17")]
    [InlineData("1.18")]
    [InlineData("1.19")]
    [InlineData("1.20")]
    [InlineData("1.21")]
    [InlineData("1.22")]
    public void Serialized_legacy_loop_variants_normalize_then_migrate_feedback_without_identity_loss(string schema)
    {
        var source = GraphEditing.InsertBoundedLoopAfter(Blank() with { SchemaVersion = schema }, "input", "out");
        var anchorId = source.Nodes.Single(pair => pair.Value.Type == NodeTypes.LoopBody).Key;
        source = GraphEditing.InsertNodeAfter(source, anchorId, "out", new(NodeTypes.Reverse, "1.0.0"));
        var originalJson = WorkflowJson.Serialize(source);
        var serializedSource = WorkflowJson.Deserialize(originalJson);

        var normalized = WorkflowSchemaNormalization.NormalizeForEditing(serializedSource);
        Assert.Equal(schema, serializedSource.SchemaVersion);
        Assert.Equal(originalJson, WorkflowJson.Serialize(serializedSource));
        Assert.Equal("2.0", normalized.SchemaVersion);
        Assert.Equal(normalized.Nodes.Keys, serializedSource.Nodes.Keys);
        Assert.Equal(normalized.Connections, serializedSource.Connections);
        Assert.Equal("feedback", normalized.ExecutionConnections!["normalize-loop-return"].Target.PortId);
        Assert.Equal("execute", normalized.ExecutionConnections["normalize-loop-initial"].Target.PortId);
        Assert.Empty(WorkflowValidator.Validate(normalized));
        Assert.Equal(WorkflowJson.Serialize(normalized),
            WorkflowJson.Serialize(WorkflowSchemaNormalization.NormalizeForEditing(serializedSource)));
    }

    [Fact]
    public void LegacyConditionalNormalizationPreservesPureTextPathsBeforeAndAfterTheRegion()
    {
        var source = GraphEditing.InsertConditionalAfter(Blank(), "input", "out");
        var conditionId = source.Nodes.Single(pair => pair.Value.Type == NodeTypes.Condition).Key;
        var mergeId = source.Nodes.Single(pair => pair.Value.Type == NodeTypes.Merge).Key;
        source = GraphEditing.InsertNodeAfter(source, "input", "out", new(NodeTypes.Uppercase, "1.0.0"));
        source = GraphEditing.InsertNodeAfter(source, mergeId, "out", new(NodeTypes.Reverse, "1.0.0"));

        var normalized = WorkflowSchemaNormalization.NormalizeForEditing(source);

        Assert.Equal(source.Nodes.Keys, normalized.Nodes.Keys);
        Assert.Equal(source.Connections, normalized.Connections);
        Assert.Equal(4, normalized.ExecutionConnections!.Count);
        Assert.Contains(normalized.ExecutionConnections.Values, edge => edge.Source == new PortReference("input", "then") &&
            edge.Target == new PortReference(conditionId, "execute"));
        Assert.Empty(WorkflowValidator.Validate(normalized));
    }

    [Fact]
    public void ExplicitConversionPreservesAllWiresAndHistoricalHelperBehavior()
    {
        var source = GraphEditing.EnableExplicitGraph(Blank());
        Assert.Equal("1.23", source.SchemaVersion);
        var result = WorkflowSchemaNormalization.NormalizeForEditing(source);
        Assert.Same(source.Nodes, result.Nodes);
        Assert.Same(source.Connections, result.Connections);
        Assert.Same(source.ExecutionConnections, result.ExecutionConnections);
        Assert.Same(result, GraphEditing.EnableNumberArithmeticGraph(result));
        Assert.Empty(WorkflowValidator.Validate(result));
    }

    [Fact]
    public void UnifiedParameterAndVariableEditsKeepSchemaAndTypedBinding()
    {
        var source = WorkflowSchemaNormalization.NormalizeForEditing(Blank());
        var withParameter = WorkflowParameterEditing.Add(source, "count", "Count", "1");
        var typed = WorkflowParameterEditing.SetType(withParameter, "count", "number", "1.5");
        var variable = WorkflowVariableEditing.Add(typed, "value", "Value", "");
        Assert.Equal("2.0", variable.SchemaVersion);
        Assert.Equal("number", variable.Parameters!["count"].Type);
        Assert.Empty(WorkflowValidator.Validate(variable));
        Assert.Null(source.Parameters);
        Assert.Null(source.Variables);
    }

    [Fact]
    public void EmptyLegacyConditionalArmsNormalizeWithoutMutatingSource()
    {
        var source = GraphEditing.InsertConditionalAfter(Blank(), "input", "out");
        var original = WorkflowJson.Serialize(source);
        var normalized = WorkflowSchemaNormalization.NormalizeForEditing(source);

        Assert.Equal("2.0", normalized.SchemaVersion);
        Assert.Equal(4, normalized.ExecutionConnections!.Count);
        Assert.Empty(WorkflowValidator.Validate(normalized));
        Assert.Equal(original, WorkflowJson.Serialize(source));
    }

    [Fact]
    public void LegacyConditionalTextGraphNormalizesDeterministicallyWithStableIdentities()
    {
        var source = ConditionalTextGraph();
        var original = WorkflowJson.Serialize(source);
        var normalized = WorkflowSchemaNormalization.NormalizeForEditing(source);

        Assert.Equal("2.0", normalized.SchemaVersion);
        Assert.Equal(source.Nodes.Keys, normalized.Nodes.Keys);
        Assert.Equal(source.Connections, normalized.Connections);
        Assert.Equal(original, WorkflowJson.Serialize(source));
        Assert.Equal(WorkflowJson.Serialize(normalized),
            WorkflowJson.Serialize(WorkflowSchemaNormalization.NormalizeForEditing(source)));
        var conversion = WorkflowSchemaNormalization.NormalizeForEditingWithProvenance(source);
        Assert.True(conversion.Succeeded);
        Assert.Equal(WorkflowSchemaNormalization.NormalizerVersion, conversion.Provenance!.NormalizerVersion);
        conversion.Provenance.Validate();
        var expectedExecution = new[]
        {
            new ConnectionDefinition(new("input", "then"), new(source.Nodes.Single(pair => pair.Value.Type == NodeTypes.Condition).Key, "execute"), "execution"),
            new ConnectionDefinition(new(source.Nodes.Single(pair => pair.Value.Type == NodeTypes.Condition).Key, "true"), new(source.Nodes.Single(pair => pair.Value.Type == NodeTypes.Merge).Key, "execute"), "execution"),
            new ConnectionDefinition(new(source.Nodes.Single(pair => pair.Value.Type == NodeTypes.Condition).Key, "false"), new(source.Nodes.Single(pair => pair.Value.Type == NodeTypes.Merge).Key, "execute"), "execution"),
            new ConnectionDefinition(new(source.Nodes.Single(pair => pair.Value.Type == NodeTypes.Merge).Key, "then"), new("output", "execute"), "execution")
        };
        Assert.Equal(expectedExecution.Length, normalized.ExecutionConnections!.Count);
        Assert.All(expectedExecution, edge => Assert.Contains(normalized.ExecutionConnections.Values, actual => actual == edge));
        Assert.Empty(WorkflowValidator.Validate(normalized));
    }

    [Fact]
    public void LegacyConditionalArmsThatConnectDirectlyToOutputNormalizeSuccessfully()
    {
        var source = GraphEditing.InsertConditionalAfter(Blank(), "input", "out");
        var condition = source.Nodes.Single(pair => pair.Value.Type == NodeTypes.Condition).Key;
        var merge = source.Nodes.Single(pair => pair.Value.Type == NodeTypes.Merge).Key;
        var connections = source.Connections.RemoveRange(source.Connections
            .Where(pair => pair.Value.Source.NodeId == condition || pair.Value.Source.NodeId == merge ||
                pair.Value.Target.NodeId == merge).Select(pair => pair.Key))
            .Add("true-output", new(new(condition, "true"), new("output", "in"), "data"))
            .Add("false-output", new(new(condition, "false"), new("output", "in"), "data"));
        source = source with { Nodes = source.Nodes.Remove(merge), Connections = connections };
        var original = WorkflowJson.Serialize(source);

        var result = WorkflowSchemaNormalization.NormalizeForEditingWithProvenance(source);

        Assert.True(result.Succeeded);
        Assert.True(result.Diagnostics.IsDefaultOrEmpty);
        result.Provenance!.Validate();
        Assert.Equal("2.0", result.Definition!.SchemaVersion);
        Assert.Empty(WorkflowValidator.Validate(result.Definition));
        Assert.Equal(original, WorkflowJson.Serialize(source));
    }

    [Fact]
    public void LegacyConditionalArmsThatEndAtOutputNormalizeThroughDeterministicMerge()
    {
        var source = GraphEditing.InsertConditionalAfter(Blank(), "input", "out");
        var conditionId = source.Nodes.Single(pair => pair.Value.Type == NodeTypes.Condition).Key;
        var mergeId = source.Nodes.Single(pair => pair.Value.Type == NodeTypes.Merge).Key;
        source = GraphEditing.RemoveNode(source, mergeId);
        source = source with
        {
            Nodes = source.Nodes.Add("upper", new(NodeTypes.Uppercase, "1.0.0"))
                .Add("reverse", new(NodeTypes.Reverse, "1.0.0")),
            Connections = source.Connections
                .Add("true-transform", new(new(conditionId, "true"), new("upper", "in"), "data"))
                .Add("true-output", new(new("upper", "out"), new("output", "in"), "data"))
                .Add("false-transform", new(new(conditionId, "false"), new("reverse", "in"), "data"))
                .Add("false-output", new(new("reverse", "out"), new("output", "in"), "data"))
        };
        var original = WorkflowJson.Serialize(source);

        var normalized = WorkflowSchemaNormalization.NormalizeForEditing(source);

        Assert.Equal("2.0", normalized.SchemaVersion);
        Assert.Equal(source.Nodes.Count + 1, normalized.Nodes.Count);
        Assert.All(source.Nodes.Keys, id => Assert.Contains(id, normalized.Nodes.Keys));
        Assert.Equal(original, WorkflowJson.Serialize(source));
        Assert.Equal(WorkflowJson.Serialize(normalized),
            WorkflowJson.Serialize(WorkflowSchemaNormalization.NormalizeForEditing(source)));
        var normalizedMergeId = normalized.Nodes.Single(pair => pair.Value.Type == NodeTypes.Merge).Key;
        Assert.Equal(2, normalized.Connections.Values.Count(edge => edge.Target == new PortReference(normalizedMergeId, "in")));
        Assert.Equal(1, normalized.Connections.Values.Count(edge => edge.Target == new PortReference("output", "in")));
        Assert.Contains(normalized.Connections.Values, edge => edge.Source == new PortReference(normalizedMergeId, "out") &&
            edge.Target == new PortReference("output", "in"));
        Assert.Equal(4, normalized.ExecutionConnections!.Count);
        Assert.Contains(normalized.ExecutionConnections.Values, edge => edge.Source.NodeId == conditionId &&
            edge.Source.PortId == "true" && edge.Target == new PortReference(normalizedMergeId, "execute"));
        Assert.Contains(normalized.ExecutionConnections.Values, edge => edge.Source.NodeId == conditionId &&
            edge.Source.PortId == "false" && edge.Target == new PortReference(normalizedMergeId, "execute"));
        Assert.Empty(WorkflowValidator.Validate(normalized));
    }

    [Fact]
    public void LegacyConditionalWithOnlyOneArmEndingAtOutputRemainsDiagnosticOnly()
    {
        var source = GraphEditing.InsertConditionalAfter(Blank(), "input", "out");
        var conditionId = source.Nodes.Single(pair => pair.Value.Type == NodeTypes.Condition).Key;
        var falseEdge = source.Connections.Single(pair => pair.Value.Source == new PortReference(conditionId, "false"));
        source = source with
        {
            Connections = source.Connections.SetItem(falseEdge.Key,
                falseEdge.Value with { Target = new("output", "in") })
        };
        Assert.Empty(WorkflowValidator.Validate(source));
        var original = WorkflowJson.Serialize(source);

        var result = WorkflowSchemaNormalization.NormalizeForEditingWithProvenance(source);

        Assert.False(result.Succeeded);
        Assert.Null(result.Provenance);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "normalization.conditional-topology");
        Assert.Equal(original, WorkflowJson.Serialize(source));
    }
}
