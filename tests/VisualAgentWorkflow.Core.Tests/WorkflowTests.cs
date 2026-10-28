using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using VisualAgentWorkflow.Core;
using Xunit;

namespace VisualAgentWorkflow.Core.Tests;

public class WorkflowTests
{
    private static WorkflowDefinition Workflow(string transform = NodeTypes.Uppercase) => new(
        "1.0", "example", "input",
        new Dictionary<string, NodeDefinition>
        {
            ["input"] = new(NodeTypes.Input, "1.0.0"),
            ["transform"] = new(transform, "1.0.0"),
            ["output"] = new(NodeTypes.Output, "1.0.0")
        }.ToImmutableSortedDictionary(StringComparer.Ordinal),
        new Dictionary<string, ConnectionDefinition>
        {
            ["first"] = new(new("input", "out"), new("transform", "in"), "data"),
            ["second"] = new(new("transform", "out"), new("output", "in"), "data")
        }.ToImmutableSortedDictionary(StringComparer.Ordinal));

    private static WorkflowDefinition ExplicitDataAndExecutionGraph() => new WorkflowDefinition(
        "1.23", "explicit-graph", "input",
        new Dictionary<string, NodeDefinition>
        {
            ["input"] = new(NodeTypes.Input, "1.0.0"),
            ["constant"] = new(NodeTypes.StringConstant, "1.0.0") { StringConstant = new("!") },
            ["concat"] = new(NodeTypes.TextConcat, "1.0.0"),
            ["output"] = new(NodeTypes.Output, "1.0.0")
        }.ToImmutableSortedDictionary(StringComparer.Ordinal),
        new Dictionary<string, ConnectionDefinition>
        {
            ["input-value"] = new(new("input", "out"), new("concat", "left"), "data"),
            ["suffix"] = new(new("constant", "out"), new("concat", "right"), "data"),
            ["result"] = new(new("concat", "out"), new("output", "in"), "data")
        }.ToImmutableSortedDictionary(StringComparer.Ordinal))
    {
        ExecutionConnections = new Dictionary<string, ConnectionDefinition>
        {
            ["activate-output"] = new(new("input", "then"), new("output", "execute"), "execution")
        }.ToImmutableSortedDictionary(StringComparer.Ordinal)
    };

    private static WorkflowDefinition ExplicitBooleanBranchGraph() => new WorkflowDefinition(
        "1.24", "explicit-branch", "input",
        new Dictionary<string, NodeDefinition>
        {
            ["input"] = new(NodeTypes.Input, "1.0.0"),
            ["expected"] = new(NodeTypes.StringConstant, "1.0.0") { StringConstant = new("yes") },
            ["equals"] = new(NodeTypes.StringEquals, "1.0.0"),
            ["branch"] = new(NodeTypes.BooleanBranch, "1.0.0"),
            ["output"] = new(NodeTypes.Output, "1.0.0"),
            ["terminate"] = new(NodeTypes.Terminate, "1.0.0") { Termination = new(WorkflowTerminationOutcomes.Failure) }
        }.ToImmutableSortedDictionary(StringComparer.Ordinal),
        new Dictionary<string, ConnectionDefinition>
        {
            ["input-left"] = new(new("input", "out"), new("equals", "left"), "data"),
            ["expected-right"] = new(new("expected", "out"), new("equals", "right"), "data"),
            ["comparison"] = new(new("equals", "out"), new("branch", "condition"), "data"),
            ["output-value"] = new(new("input", "out"), new("output", "in"), "data")
        }.ToImmutableSortedDictionary(StringComparer.Ordinal))
    {
        ExecutionConnections = new Dictionary<string, ConnectionDefinition>
        {
            ["activate-branch"] = new(new("input", "then"), new("branch", "execute"), "execution"),
            ["true-route"] = new(new("branch", "true"), new("output", "execute"), "execution"),
            ["false-route"] = new(new("branch", "false"), new("terminate", "execute"), "execution")
        }.ToImmutableSortedDictionary(StringComparer.Ordinal)
    };

    private static WorkflowDefinition ExplicitTypedBooleanBranchGraph() => new WorkflowDefinition(
        "1.25", "explicit-typed-boolean", "input",
        new Dictionary<string, NodeDefinition>
        {
            ["input"] = new(NodeTypes.Input, "1.0.0"),
            ["expected"] = new(NodeTypes.StringConstant, "1.0.0") { StringConstant = new("yes") },
            ["equals"] = new(NodeTypes.StringEquals, "1.0.0"),
            ["invert"] = new(NodeTypes.BooleanNot, "1.0.0"),
            ["branch"] = new(NodeTypes.BooleanBranch, "1.0.0"),
            ["output"] = new(NodeTypes.Output, "1.0.0"),
            ["terminate"] = new(NodeTypes.Terminate, "1.0.0") { Termination = new(WorkflowTerminationOutcomes.Failure) }
        }.ToImmutableSortedDictionary(StringComparer.Ordinal),
        new Dictionary<string, ConnectionDefinition>
        {
            ["input-left"] = new(new("input", "out"), new("equals", "left"), "data"),
            ["expected-right"] = new(new("expected", "out"), new("equals", "right"), "data"),
            ["equals-invert"] = new(new("equals", "out"), new("invert", "value"), "data"),
            ["invert-branch"] = new(new("invert", "out"), new("branch", "condition"), "data"),
            ["output-value"] = new(new("input", "out"), new("output", "in"), "data")
        }.ToImmutableSortedDictionary(StringComparer.Ordinal))
    {
        ExecutionConnections = new Dictionary<string, ConnectionDefinition>
        {
            ["activate-branch"] = new(new("input", "then"), new("branch", "execute"), "execution"),
            ["true-route"] = new(new("branch", "true"), new("output", "execute"), "execution"),
            ["false-route"] = new(new("branch", "false"), new("terminate", "execute"), "execution")
        }.ToImmutableSortedDictionary(StringComparer.Ordinal)
    };

    private static WorkflowDefinition ExplicitBooleanTopologyGraph(bool rootChoice, bool nestedChoice) => new WorkflowDefinition(
        "1.26", "boolean-topology", "input",
        new Dictionary<string, NodeDefinition>
        {
            ["input"] = new(NodeTypes.Input, "1.0.0"),
            ["root-value"] = new(NodeTypes.BooleanConstant, "1.0.0") { BooleanConstant = new(rootChoice) },
            ["nested-value"] = new(NodeTypes.BooleanConstant, "1.0.0") { BooleanConstant = new(nestedChoice) },
            ["root"] = new(NodeTypes.BooleanBranch, "1.0.0"),
            ["nested"] = new(NodeTypes.BooleanBranch, "1.0.0"),
            ["output"] = new(NodeTypes.Output, "1.0.0"),
            ["failure"] = new(NodeTypes.Terminate, "1.0.0") { Termination = new(WorkflowTerminationOutcomes.Failure) },
            ["success"] = new(NodeTypes.Terminate, "1.0.0") { Termination = new(WorkflowTerminationOutcomes.Success) }
        }.ToImmutableSortedDictionary(StringComparer.Ordinal),
        new Dictionary<string, ConnectionDefinition>
        {
            ["root-condition"] = new(new("root-value", "out"), new("root", "condition"), "data"),
            ["nested-condition"] = new(new("nested-value", "out"), new("nested", "condition"), "data"),
            ["output-value"] = new(new("input", "out"), new("output", "in"), "data")
        }.ToImmutableSortedDictionary(StringComparer.Ordinal))
    {
        ExecutionConnections = new Dictionary<string, ConnectionDefinition>
        {
            ["activate-root"] = new(new("input", "then"), new("root", "execute"), "execution"),
            ["root-true"] = new(new("root", "true"), new("nested", "execute"), "execution"),
            ["root-false"] = new(new("root", "false"), new("failure", "execute"), "execution"),
            ["nested-true"] = new(new("nested", "true"), new("output", "execute"), "execution"),
            ["nested-false"] = new(new("nested", "false"), new("success", "execute"), "execution")
        }.ToImmutableSortedDictionary(StringComparer.Ordinal)
    };

    [Fact]
    public void ValidWorkflowRoundTripsWithStableHash()
    {
        var definition = Workflow();
        var json = WorkflowJson.Serialize(definition);
        var loaded = WorkflowJson.Deserialize(json);
        Assert.Empty(WorkflowValidator.Validate(loaded));
        Assert.Equal(json, WorkflowJson.Serialize(loaded));
        Assert.Equal(WorkflowJson.ComputeHash(definition), WorkflowJson.ComputeHash(loaded));
    }

    [Fact]
    public void Explicit_execution_and_data_channels_round_trip_without_conflating_their_edges()
    {
        var definition = ExplicitDataAndExecutionGraph();

        var json = WorkflowJson.Serialize(definition);
        var restored = WorkflowJson.Deserialize(json);

        Assert.Empty(WorkflowValidator.Validate(restored));
        Assert.Equal("data", Assert.Single(restored.Connections.Values, edge => edge.Target.NodeId == "output").Kind);
        Assert.Equal("execution", Assert.Single(restored.ExecutionConnections!).Value.Kind);
        Assert.Equal(json, WorkflowJson.Serialize(restored));
        Assert.Equal(WorkflowJson.ComputeHash(definition), WorkflowJson.ComputeHash(restored));
        Assert.Collection(WorkflowPorts.Explicit(restored.Nodes["output"]),
            port =>
            {
                Assert.Equal(new WorkflowPortDescriptor("in", WorkflowPortDirection.Input, WorkflowPortKind.Data, "string")
                {
                    Required = true,
                    Cardinality = WorkflowPortCardinality.ExactlyOne
                }, port);
            },
            port => Assert.Equal(new WorkflowPortDescriptor("execute", WorkflowPortDirection.Input, WorkflowPortKind.Execution)
            {
                Required = true,
                Cardinality = WorkflowPortCardinality.ExactlyOne
            }, port));
    }

    [Fact]
    public void Explicit_boolean_branch_uses_typed_data_and_preserves_draft_rewiring()
    {
        var definition = ExplicitBooleanBranchGraph();
        var json = WorkflowJson.Serialize(definition);
        var restored = WorkflowJson.Deserialize(json);
        Assert.Empty(WorkflowValidator.Validate(restored));
        Assert.Equal(json, WorkflowJson.Serialize(restored));
        Assert.Collection(WorkflowPorts.Explicit(restored.Nodes["branch"]),
            port => Assert.Equal(new WorkflowPortDescriptor("condition", WorkflowPortDirection.Input, WorkflowPortKind.Data, "boolean")
            {
                Required = true,
                Cardinality = WorkflowPortCardinality.ExactlyOne
            }, port),
            port => Assert.Equal(new WorkflowPortDescriptor("execute", WorkflowPortDirection.Input, WorkflowPortKind.Execution)
            {
                Required = true,
                Cardinality = WorkflowPortCardinality.ExactlyOne
            }, port),
            port => Assert.Equal(new WorkflowPortDescriptor("true", WorkflowPortDirection.Output, WorkflowPortKind.Execution)
            {
                Required = true,
                Cardinality = WorkflowPortCardinality.ExactlyOne
            }, port),
            port => Assert.Equal(new WorkflowPortDescriptor("false", WorkflowPortDirection.Output, WorkflowPortKind.Execution)
            {
                Required = true,
                Cardinality = WorkflowPortCardinality.ExactlyOne
            }, port));

        var mismatchedType = definition with
        {
            Connections = definition.Connections.SetItem("comparison",
                new(new("input", "out"), new("branch", "condition"), "data"))
        };
        Assert.Contains(WorkflowValidator.Validate(mismatchedType), issue => issue.Code == "connection.port");

        var incomplete = GraphEditing.AddUnconnectedNode(definition, new(NodeTypes.BooleanBranch, "1.0.0"));
        var draft = WorkflowJson.DeserializeDraft(WorkflowJson.SerializeDraft(incomplete));
        Assert.Equal("1.24", draft.SchemaVersion);
        Assert.NotEmpty(WorkflowValidator.Validate(draft));
        Assert.Throws<ArgumentException>(() => WorkflowJson.Serialize(draft));

        var outcome = WorkflowTerminationEditing.SetOutcome(definition, "terminate", WorkflowTerminationOutcomes.Failure);
        Assert.Equal("1.24", outcome.SchemaVersion);
    }

    [Fact]
    public void Boolean_not_graph_is_valid_only_with_a_boolean_data_input()
    {
        var definition = ExplicitTypedBooleanBranchGraph();

        Assert.Empty(WorkflowValidator.Validate(definition));
        Assert.Equal(WorkflowJson.Serialize(definition), WorkflowJson.Serialize(WorkflowJson.Deserialize(WorkflowJson.Serialize(definition))));
        Assert.Equal("boolean", Assert.Single(WorkflowPorts.ExplicitDataOutputs(definition.Nodes["invert"])).DataType);
        Assert.Equal("boolean", Assert.Single(WorkflowPorts.ExplicitDataInputs(definition.Nodes["invert"])).DataType);

        var invalidInput = definition with
        {
            Connections = definition.Connections.SetItem("equals-invert",
                new(new("input", "out"), new("invert", "value"), "data"))
        };
        Assert.Contains(WorkflowValidator.Validate(invalidInput), issue => issue.Code == "connection.port");
    }

    [Fact]
    public void Boolean_constant_requires_an_explicit_value_and_schema_125()
    {
        var definition = ExplicitTypedBooleanBranchGraph() with
        {
            Nodes = ExplicitTypedBooleanBranchGraph().Nodes
                .Remove("expected").Remove("equals").Remove("invert")
                .Add("constant", new(NodeTypes.BooleanConstant, "1.0.0") { BooleanConstant = new(false) }),
            Connections = ExplicitTypedBooleanBranchGraph().Connections
                .Remove("input-left").Remove("expected-right").Remove("equals-invert").Remove("invert-branch")
                .Add("constant-branch", new(new("constant", "out"), new("branch", "condition"), "data"))
        };

        Assert.Empty(WorkflowValidator.Validate(definition));
        var restored = WorkflowJson.Deserialize(WorkflowJson.Serialize(definition));
        Assert.False(restored.Nodes["constant"].BooleanConstant!.Value);

        var edited = GraphEditing.ReplaceNode(definition, "constant",
            definition.Nodes["constant"] with { BooleanConstant = new(true) });
        Assert.True(edited.Nodes["constant"].BooleanConstant!.Value);
        Assert.Empty(WorkflowValidator.Validate(edited));

        var missingValue = definition with
        {
            Nodes = definition.Nodes.SetItem("constant", definition.Nodes["constant"] with { BooleanConstant = null })
        };
        Assert.Contains(WorkflowValidator.Validate(missingValue), issue => issue.Code == "constant.configuration");

        var oldSchema = definition with { SchemaVersion = "1.24" };
        Assert.Contains(WorkflowValidator.Validate(oldSchema), issue => issue.Code == "node.schema");
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void Schema_126_accepts_bounded_nested_boolean_routes(bool rootChoice, bool nestedChoice)
    {
        var definition = ExplicitBooleanTopologyGraph(rootChoice, nestedChoice);

        Assert.Empty(WorkflowValidator.Validate(definition));
        Assert.Equal(WorkflowJson.Serialize(definition), WorkflowJson.Serialize(WorkflowJson.Deserialize(WorkflowJson.Serialize(definition))));
        var upgraded = GraphEditing.EnableBooleanTopologyGraph(ExplicitTypedBooleanBranchGraph());
        Assert.Equal("1.26", upgraded.SchemaVersion);
        Assert.Equal(ExplicitTypedBooleanBranchGraph().Connections, upgraded.Connections);
        Assert.Equal(ExplicitTypedBooleanBranchGraph().ExecutionConnections, upgraded.ExecutionConnections);
    }

    [Fact]
    public void Schema_126_allows_both_routes_to_return_without_a_termination_node()
    {
        var definition = ExplicitBooleanTopologyGraph(rootChoice: false, nestedChoice: false) with
        {
            Nodes = ExplicitBooleanTopologyGraph(false, false).Nodes.Remove("nested").Remove("nested-value").Remove("failure").Remove("success"),
            Connections = ExplicitBooleanTopologyGraph(false, false).Connections.Remove("nested-condition"),
            ExecutionConnections = new Dictionary<string, ConnectionDefinition>
            {
                ["activate-root"] = new(new("input", "then"), new("root", "execute"), "execution"),
                ["root-true"] = new(new("root", "true"), new("output", "execute"), "execution"),
                ["root-false"] = new(new("root", "false"), new("output", "execute"), "execution")
            }.ToImmutableSortedDictionary(StringComparer.Ordinal)
        };

        Assert.Empty(WorkflowValidator.Validate(definition));
    }

    [Fact]
    public void Schema_126_rejects_branch_and_terminal_counts_over_their_limits()
    {
        var definition = ExplicitBooleanTopologyGraph(true, true);
        var tooManyBranches = definition with
        {
            Nodes = Enumerable.Range(0, 7).Aggregate(definition.Nodes, (nodes, index) =>
                nodes.Add($"extra-branch-{index}", new(NodeTypes.BooleanBranch, "1.0.0")))
        };
        var tooManyTerminations = definition with
        {
            Nodes = Enumerable.Range(0, 15).Aggregate(definition.Nodes, (nodes, index) =>
                nodes.Add($"extra-termination-{index}", new(NodeTypes.Terminate, "1.0.0")))
        };

        Assert.Contains(WorkflowValidator.Validate(tooManyBranches), issue => issue.Code == "graph.limit" && issue.Path == "nodes");
        Assert.Contains(WorkflowValidator.Validate(tooManyTerminations), issue => issue.Code == "graph.limit" && issue.Path == "nodes");
    }

    [Fact]
    public void Schema_126_requires_a_structurally_reachable_output_route()
    {
        var definition = ExplicitBooleanTopologyGraph(false, false) with
        {
            ExecutionConnections = ExplicitBooleanTopologyGraph(false, false).ExecutionConnections!
                .SetItem("nested-true", new(new("nested", "true"), new("success", "execute"), "execution"))
        };

        Assert.Contains(WorkflowValidator.Validate(definition), issue => issue.Code == "graph.unreachable" && issue.Path == "executionConnections");
    }

    [Fact]
    public void Schema_126_graph_editing_preserves_alternative_incoming_execution_edges()
    {
        var definition = ExplicitBooleanTopologyGraph(false, false);

        var edited = GraphEditing.ConnectPins(definition, "root", "false", "nested", "execute");

        Assert.Contains(edited.ExecutionConnections!, edge => edge.Value.Source == new PortReference("root", "true") &&
            edge.Value.Target == new PortReference("nested", "execute"));
        Assert.Contains(edited.ExecutionConnections!, edge => edge.Value.Source == new PortReference("root", "false") &&
            edge.Value.Target == new PortReference("nested", "execute"));
        Assert.Equal(2, edited.ExecutionConnections!.Count(edge => edge.Value.Target == new PortReference("nested", "execute")));
    }

    [Fact]
    public void Explicit_data_edges_do_not_satisfy_execution_activation_requirements()
    {
        var definition = ExplicitDataAndExecutionGraph() with { ExecutionConnections = ImmutableSortedDictionary<string, ConnectionDefinition>.Empty };

        var issues = WorkflowValidator.Validate(definition);

        Assert.Contains(issues, issue => issue.Code == "execution.degree");
        Assert.Contains(issues, issue => issue.Code == "execution.path");
    }

    [Fact]
    public void Explicit_data_cycles_are_rejected_even_when_the_execution_path_is_valid()
    {
        var definition = ExplicitDataAndExecutionGraph() with
        {
            Connections = ExplicitDataAndExecutionGraph().Connections
                .SetItem("cycle-back", new(new("concat", "out"), new("concat", "left"), "data"))
        };

        Assert.Contains(WorkflowValidator.Validate(definition), issue => issue.Code == "data.cycle");
    }

    [Fact]
    public void Explicit_graph_edits_preserve_channels_and_allow_incomplete_drafts()
    {
        var definition = ExplicitDataAndExecutionGraph();
        var withConstant = GraphEditing.AddUnconnectedNode(definition,
            new(NodeTypes.StringConstant, "1.0.0") { StringConstant = new(" deterministic") });
        var constantId = withConstant.Nodes.Keys.Single(id => id.StartsWith("constant-", StringComparison.Ordinal));

        Assert.Equal("1.23", withConstant.SchemaVersion);
        Assert.NotEmpty(WorkflowValidator.Validate(withConstant));

        var connected = GraphEditing.ConnectPins(withConstant, constantId, "out", "concat", "left");
        Assert.Contains(connected.Connections.Values, edge => edge.Source == new PortReference(constantId, "out") &&
            edge.Target == new PortReference("concat", "left"));
        Assert.Single(connected.ExecutionConnections!);

        var disconnected = GraphEditing.Disconnect(connected,
            connected.Connections.Single(edge => edge.Value.Source.NodeId == constantId).Key);
        Assert.DoesNotContain(disconnected.Connections.Values, edge => edge.Source.NodeId == constantId);
        Assert.Single(disconnected.ExecutionConnections!);
        Assert.NotEmpty(WorkflowValidator.Validate(disconnected));
    }

    [Fact]
    public void Explicit_connection_edits_keep_execution_and_data_edges_separate()
    {
        var definition = ExplicitDataAndExecutionGraph();
        var disconnected = GraphEditing.Disconnect(definition, "activate-output");
        Assert.Empty(disconnected.ExecutionConnections!);
        Assert.Equal(definition.Connections, disconnected.Connections);

        var reconnected = GraphEditing.ConnectPins(disconnected, "input", "then", "output", "execute");
        Assert.Single(reconnected.ExecutionConnections!);
        Assert.Equal(definition.Connections, reconnected.Connections);
        Assert.Empty(WorkflowValidator.Validate(reconnected));
    }

    [Fact]
    public void Blank_workflow_can_be_upgraded_to_the_explicit_graph_preview()
    {
        var blank = new WorkflowDefinition("1.0", "blank", "input",
            new Dictionary<string, NodeDefinition>
            {
                ["input"] = new(NodeTypes.Input, "1.0.0"),
                ["output"] = new(NodeTypes.Output, "1.0.0")
            }.ToImmutableSortedDictionary(StringComparer.Ordinal),
            new Dictionary<string, ConnectionDefinition>
            {
                ["initial"] = new(new("input", "out"), new("output", "in"), "data")
            }.ToImmutableSortedDictionary(StringComparer.Ordinal));

        var explicitGraph = GraphEditing.EnableExplicitGraph(blank);

        Assert.Equal("1.23", explicitGraph.SchemaVersion);
        Assert.Single(explicitGraph.Connections);
        Assert.Single(explicitGraph.ExecutionConnections!);
        Assert.Empty(WorkflowValidator.Validate(explicitGraph));
        Assert.Throws<ArgumentException>(() => GraphEditing.EnableExplicitGraph(Workflow()));
    }

    [Fact]
    public void CanonicalSerializationIgnoresCallerDictionaryOrdering()
    {
        var definition = Workflow();
        var reversed = definition with
        {
            Nodes = definition.Nodes.WithComparers(Comparer<string>.Create((a, b) => StringComparer.Ordinal.Compare(b, a)))
        };
        Assert.Equal(WorkflowJson.ComputeHash(definition), WorkflowJson.ComputeHash(reversed));
    }

    [Fact]
    public void ChangingAnOperationChangesHash() =>
        Assert.NotEqual(WorkflowJson.ComputeHash(Workflow()), WorkflowJson.ComputeHash(Workflow(NodeTypes.Reverse)));

    [Fact]
    public void Workflow_names_are_portable_and_preserve_the_stable_id()
    {
        var original = Workflow();
        var named = WorkflowMetadataEditing.Rename(original, "  Customer support  ");
        var restored = WorkflowJson.Deserialize(WorkflowJson.Serialize(named));

        Assert.Equal(original.WorkflowId, named.WorkflowId);
        Assert.Equal("Customer support", restored.DisplayName);
        Assert.Equal(WorkflowJson.ComputeHash(named), WorkflowJson.ComputeHash(restored));
        Assert.NotEqual(WorkflowJson.ComputeHash(original), WorkflowJson.ComputeHash(named));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("workflow\nname")]
    public void Workflow_name_rejects_empty_or_control_characters(string displayName) =>
        Assert.Throws<ArgumentException>(() => WorkflowMetadataEditing.Rename(Workflow(), displayName));

    [Fact]
    public void Workflow_name_rejects_values_over_the_supported_length() =>
        Assert.Throws<ArgumentException>(() => WorkflowMetadataEditing.Rename(Workflow(), new string('x', 129)));

    [Fact]
    public void RehydratesHistoricalWindowsLineEndingHashesWithoutChangingTheirIdentity()
    {
        var revision = WorkflowRevision.Create(Workflow(), "alice");
        var legacyHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
            WorkflowJson.Serialize(revision.Definition).Replace("\n", "\r\n", StringComparison.Ordinal))));

        Assert.NotEqual(revision.ContentHash, legacyHash);
        Assert.True(WorkflowJson.MatchesHash(revision.Definition, legacyHash));
        var restored = WorkflowRevision.Rehydrate(revision.RevisionId, revision.Definition, revision.Author,
            revision.ParentRevisionId, revision.CreatedAt, legacyHash);
        Assert.Equal(legacyHash, restored.ContentHash);
        Assert.False(WorkflowJson.MatchesHash(revision.Definition, new string('0', 64)));
    }

    [Fact]
    public void RestoreCreatesNewRevisionWithoutChangingHistory()
    {
        var first = WorkflowRevision.Create(Workflow(), "alice");
        var second = WorkflowRevision.Create(Workflow(NodeTypes.Reverse), "bob", first.RevisionId);
        var restored = WorkflowRevision.Restore(first, second, "alice");
        Assert.NotEqual(first.RevisionId, restored.RevisionId);
        Assert.Equal(second.RevisionId, restored.ParentRevisionId);
        Assert.Equal(first.ContentHash, restored.ContentHash);
        Assert.Equal(NodeTypes.Reverse, second.Definition.Nodes["transform"].Type);
    }

    [Fact]
    public void RestoreRejectsAnotherWorkflow()
    {
        var first = WorkflowRevision.Create(Workflow(), "alice");
        var other = WorkflowRevision.Create(Workflow() with { WorkflowId = "other" }, "alice");
        Assert.Throws<ArgumentException>(() => WorkflowRevision.Restore(first, other, "alice"));
    }

    [Fact]
    public void MissingEndpointIsReported()
    {
        var definition = Workflow();
        definition = definition with { Connections = definition.Connections.SetItem("first", new(new("absent", "out"), new("transform", "in"), "data")) };
        Assert.Contains(WorkflowValidator.Validate(definition), i => i.Code == "connection.reference");
    }

    [Fact]
    public void DisconnectedCycleIsRejected()
    {
        var definition = Workflow();
        definition = definition with
        {
            Nodes = definition.Nodes.Add("orphan", new(NodeTypes.Reverse, "1.0.0")),
            Connections = definition.Connections.Add("cycle", new(new("orphan", "out"), new("orphan", "in"), "data"))
        };
        Assert.Contains(WorkflowValidator.Validate(definition), i => i.Code == "graph.unreachable");
    }

    [Theory]
    [InlineData("custom.assembly")]
    [InlineData("core.future-agent")]
    public void UnknownOrUnimplementedNodesAreRejected(string type) =>
        Assert.Contains(WorkflowValidator.Validate(Workflow(type)), i => i.Code == "node.unsupported");

    [Fact]
    public void UnsupportedPortsAreRejected()
    {
        var definition = Workflow();
        definition = definition with { Connections = definition.Connections.SetItem("first", new(new("input", "wrong"), new("transform", "in"), "data")) };
        Assert.Contains(WorkflowValidator.Validate(definition), i => i.Code == "connection.port");
    }

    [Fact]
    public void DuplicateJsonPropertiesAreRejected()
    {
        var json = WorkflowJson.Serialize(Workflow()).Replace("\"schemaVersion\": \"1.0\",", "\"schemaVersion\": \"2.0\", \"schemaVersion\": \"1.0\",");
        Assert.Throws<JsonException>(() => WorkflowJson.Deserialize(json));
    }

    [Fact]
    public void UnknownJsonFieldsAreRejected()
    {
        var json = WorkflowJson.Serialize(Workflow()).Replace("\"schemaVersion\":", "\"assembly\": \"unsafe.dll\", \"schemaVersion\":");
        Assert.Throws<JsonException>(() => WorkflowJson.Deserialize(json));
    }

    [Fact]
    public void UnsupportedSchemaAndMissingGraphAreReported()
    {
        var issues = WorkflowValidator.Validate(Workflow() with { SchemaVersion = "2.0", Nodes = null! });
        Assert.Contains(issues, i => i.Code == "schema.unsupported");
        Assert.Contains(issues, i => i.Code == "graph.missing");
    }
}
