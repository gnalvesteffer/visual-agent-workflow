using System.Collections.Immutable;
using VisualAgentWorkflow.Core;
using Xunit;

namespace VisualAgentWorkflow.Core.Tests;

public sealed class MutableVariableContractTests
{
    [Fact]
    public void Schema_144_round_trips_a_linear_variable_graph_and_exposes_typed_pins()
    {
        var definition = Definition();
        var restored = WorkflowJson.Deserialize(WorkflowJson.Serialize(definition));

        Assert.Empty(WorkflowValidator.Validate(restored));
        Assert.Equal(WorkflowSchemaVersions.MutableVariablesGraph, restored.SchemaVersion);
        Assert.Equal(WorkflowDataTypes.Integer, WorkflowPorts.ExplicitDataInputs(restored.Nodes["set"])[0].DataType);
        Assert.Equal(WorkflowDataTypes.Integer, WorkflowPorts.ExplicitDataOutputs(restored.Nodes["get"])[0].DataType);
    }

    [Fact]
    public void Variable_nodes_fail_closed_for_missing_declarations_and_mismatched_types()
    {
        var missing = Definition() with
        {
            Nodes = Definition().Nodes.SetItem("set", Definition().Nodes["set"] with { VariableId = "missing" })
        };
        var mismatch = Definition() with
        {
            Nodes = Definition().Nodes.SetItem("get", Definition().Nodes["get"] with { OutputDataType = WorkflowDataTypes.String })
        };

        Assert.Contains(WorkflowValidator.Validate(missing), issue => issue.Code == "variable.reference");
        Assert.Contains(WorkflowValidator.Validate(mismatch), issue => issue.Code == "variable.type");
    }

    [Fact]
    public void Variable_package_round_trip_preserves_state_contract_layout_and_requirements()
    {
        var definition = Definition();
        var layout = WorkflowLayout.Create(definition) with
        {
            Nodes = WorkflowLayout.Create(definition).Nodes.SetItem("set",
                new(new(384, 192), "Write count", "Persists the current research count."))
        };
        var json = WorkflowPackageJson.Serialize(definition, layout);
        var imported = WorkflowPackageJson.Deserialize(json);

        Assert.Equal(json, WorkflowPackageJson.Serialize(imported.Definition, imported.Layout));
        Assert.Equal(definition.Variables, imported.Definition.Variables);
        Assert.Equal(definition.Nodes["set"], imported.Definition.Nodes["set"]);
        Assert.Equal("Write count", imported.Layout.Nodes["set"].Label);
        Assert.Equal(WorkflowJson.ComputeHash(definition), imported.DefinitionHash);
        Assert.Contains(imported.Requirements, requirement => requirement is { Kind: "node", Id: NodeTypes.VariableSet });
    }

    [Fact]
    public void Incomplete_variable_draft_round_trips_but_strict_package_export_rejects_it()
    {
        var definition = Definition() with
        {
            Nodes = Definition().Nodes.Add("orphan-get", new(NodeTypes.VariableGet, "1.0.0")
            {
                VariableId = "count",
                OutputDataType = WorkflowDataTypes.Integer
            })
        };

        var saved = WorkflowJson.DeserializeDraft(WorkflowJson.SerializeDraft(definition));

        Assert.Equal(WorkflowJson.ComputeDraftHash(definition), WorkflowJson.ComputeDraftHash(saved));
        Assert.Contains(WorkflowValidator.Validate(saved), issue => issue.Code == "graph.unreachable");
        Assert.Throws<ArgumentException>(() => WorkflowPackageJson.Create(saved, WorkflowLayout.Create(saved)));
    }

    [Theory]
    [InlineData(NodeTypes.Loop)]
    [InlineData(NodeTypes.ParallelFork)]
    [InlineData(NodeTypes.WorkflowCall)]
    [InlineData(NodeTypes.FunctionCall)]
    public void Variable_graph_fails_closed_when_a_stateful_or_nested_region_is_added(string unsupportedType)
    {
        var definition = Definition() with
        {
            Nodes = Definition().Nodes.Add("unsupported", new(unsupportedType, "1.0.0"))
        };

        var issues = WorkflowValidator.Validate(definition);

        Assert.Contains(issues, issue => issue.Code == "execution.node.unsupported" && issue.Path == "nodes.unsupported");
    }

    [Fact]
    public void Cancelled_variable_write_does_not_commit_or_advance_version()
    {
        var store = new WorkflowVariableStore(Definition());
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() => store.Write("count", WorkflowDataTypes.Integer, 8L, cancellation.Token));
        Assert.True(store.TryRead("count", out var current));
        Assert.Equal(0L, current.Value);
        Assert.Equal(1L, current.Version);
    }

    [Fact]
    public void Blank_explicit_graph_can_be_promoted_to_the_variable_proof()
    {
        var nodes = ImmutableSortedDictionary<string, NodeDefinition>.Empty
            .Add("input", new(NodeTypes.Input, "1.0.0"))
            .Add("output", new(NodeTypes.Output, "1.0.0"));
        var data = ImmutableSortedDictionary<string, ConnectionDefinition>.Empty
            .Add("value", new(new("input", "out"), new("output", "in"), "data"));
        var blank = new WorkflowDefinition(WorkflowSchemaVersions.ExplicitGraph, "promotion", "input", nodes, data)
        {
            ExecutionConnections = ImmutableSortedDictionary<string, ConnectionDefinition>.Empty
                .Add("activate", new(new("input", "then"), new("output", "execute"), "execution"))
        };

        var promoted = GraphEditing.EnableMutableVariablesGraph(blank);

        Assert.Equal(WorkflowSchemaVersions.MutableVariablesGraph, promoted.SchemaVersion);
        Assert.Empty(WorkflowValidator.Validate(promoted));
        Assert.Contains(promoted.Nodes.Values, node => node.Type == NodeTypes.VariableSet);
        Assert.Contains(promoted.Nodes.Values, node => node.Type == NodeTypes.VariableGet);
    }

    private static WorkflowDefinition Definition()
    {
        var nodes = new Dictionary<string, NodeDefinition>
        {
            ["input"] = new(NodeTypes.Input, "1.0.0"),
            ["constant"] = new(NodeTypes.IntegerConstant, "1.0.0") { IntegerConstant = new(7) },
            ["set"] = new(NodeTypes.VariableSet, "1.0.0") { VariableId = "count", InputDataType = WorkflowDataTypes.Integer },
            ["get"] = new(NodeTypes.VariableGet, "1.0.0") { VariableId = "count", OutputDataType = WorkflowDataTypes.Integer },
            ["output"] = new(NodeTypes.Output, "1.0.0") { OutputDataType = WorkflowDataTypes.Integer }
        };
        var data = new Dictionary<string, ConnectionDefinition>
        {
            ["constant-value"] = new(new("constant", "out"), new("set", "value"), "data"),
            ["get-value"] = new(new("get", "out"), new("output", "in"), "data")
        };
        var execution = new Dictionary<string, ConnectionDefinition>
        {
            ["input-set"] = new(new("input", "then"), new("set", "execute"), "execution"),
            ["set-get"] = new(new("set", "then"), new("get", "execute"), "execution"),
            ["get-output"] = new(new("get", "then"), new("output", "execute"), "execution")
        };
        return new WorkflowDefinition(WorkflowSchemaVersions.MutableVariablesGraph, "variable-contract", "input",
            nodes.ToImmutableSortedDictionary(), data.ToImmutableSortedDictionary())
        {
            Variables = ImmutableSortedDictionary<string, WorkflowVariableDefinition>.Empty
                .Add("count", new("Count", "0") { Type = WorkflowDataTypes.Integer }),
            ExecutionConnections = execution.ToImmutableSortedDictionary()
        };
    }
}
