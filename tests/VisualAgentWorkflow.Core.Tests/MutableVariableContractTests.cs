using System.Collections.Immutable;
using System.Text.Json;
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
    public void Unified_schema_accepts_typed_array_variable_defaults_and_exact_get_set_pins()
    {
        var definition = ArrayDefinition();
        var restored = WorkflowJson.Deserialize(WorkflowJson.Serialize(definition));

        Assert.Empty(WorkflowValidator.Validate(restored));
        Assert.Equal("array<string>", restored.Variables!["items"].Type);
        Assert.Equal("[\"alpha\",\"beta\"]", WorkflowVariableStore.FormatValue("array<string>",
            WorkflowVariableStore.Parse("array<string>", "[ \"alpha\", \"beta\" ]")));
        Assert.Equal("array<string>", WorkflowPorts.ExplicitDataOutputs(restored.Nodes["get"])[0].DataType);
        Assert.Equal("array<string>", WorkflowPorts.ExplicitDataInputs(restored.Nodes["set"])[0].DataType);

        var wrongPin = restored with
        {
            Nodes = restored.Nodes.SetItem("get", restored.Nodes["get"] with { OutputDataType = "array<boolean>" })
        };
        Assert.Contains(WorkflowValidator.Validate(wrongPin), issue => issue.Code == "variable.type");
    }

    [Fact]
    public void Array_variable_write_is_canonical_bounded_and_atomic_on_invalid_value_or_evidence_exhaustion()
    {
        var store = new WorkflowVariableStore(ArrayDefinition());
        using var initial = JsonDocument.Parse("[ \"next\" ]");
        var written = store.Write("items", "array<string>", initial.RootElement);

        Assert.Equal("[\"next\"]", WorkflowVariableStore.FormatValue(written.DataType, written.Value));
        Assert.Equal(2, written.Version);
        Assert.Throws<ArgumentException>(() => store.Write("items", "array<string>", "[1]"));
        Assert.True(store.TryRead("items", out var unchanged));
        Assert.Equal("[\"next\"]", WorkflowVariableStore.FormatValue(unchanged.DataType, unchanged.Value));
        Assert.Equal(2, unchanged.Version);

        var evidenceBudget = new WorkflowArrayEvidenceBudget();
        var maximumSizedValue = "[\"" + new string('x', 16_380) + "\"]";
        using var maximum = JsonDocument.Parse(maximumSizedValue);
        for (var index = 0; index < 4; index++) evidenceBudget.Consume("array<string>", maximum.RootElement);
        var tooMuchEvidence = new WorkflowVariableStore(ArrayDefinition(), evidenceBudget);
        Assert.Throws<WorkflowArrayEvidenceBudgetExceededException>(() => { tooMuchEvidence.Write("items", "array<string>", maximum.RootElement); });
        Assert.True(tooMuchEvidence.TryRead("items", out var stillDefault));
        Assert.Equal(1, stillDefault.Version);
        Assert.Equal("[\"seed\"]", WorkflowVariableStore.FormatValue(stillDefault.DataType, stillDefault.Value));
    }

    [Fact]
    public void Typed_array_variables_are_rejected_by_legacy_schema_and_checkpoint_snapshots()
    {
        var definition = ArrayDefinition();
        var legacy = definition with { SchemaVersion = WorkflowSchemaVersions.MutableVariablesGraph };

        Assert.Contains(WorkflowValidator.Validate(legacy), issue => issue.Code == "variable.definition");
        Assert.Throws<ArgumentException>(() => WorkflowVariableStore.FromSnapshot(definition,
            new WorkflowVariableSnapshot(WorkflowVariableSnapshot.CurrentFormatVersion,
                [new("items", "array<string>", "[\"seed\"]", 1)], 1)));
        Assert.Throws<InvalidOperationException>(() => new WorkflowVariableStore(definition).Snapshot());
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
    public void Variable_snapshot_round_trips_all_scalar_types_and_next_version()
    {
        var definition = AllScalarDefinition();
        var store = new WorkflowVariableStore(definition);
        store.Write("text", WorkflowDataTypes.String, "updated");
        store.Write("flag", WorkflowDataTypes.Boolean, true);
        store.Write("count", WorkflowDataTypes.Integer, 42L);
        store.Write("ratio", WorkflowDataTypes.Number, 1.25d);

        var restored = WorkflowVariableStore.FromSnapshot(definition, store.Snapshot());

        var originalSnapshot = store.Snapshot();
        var restoredSnapshot = restored.Snapshot();
        Assert.Equal(originalSnapshot.FormatVersion, restoredSnapshot.FormatVersion);
        Assert.Equal(originalSnapshot.NextVersion, restoredSnapshot.NextVersion);
        Assert.Equal(originalSnapshot.Values.ToArray(), restoredSnapshot.Values.ToArray());
        restored.Write("count", WorkflowDataTypes.Integer, 43L);
        Assert.Equal(9L, restored.Snapshot().NextVersion);
    }

    [Fact]
    public void Variable_snapshot_rejects_missing_types_values_and_version_regressions()
    {
        var definition = AllScalarDefinition();
        var snapshot = new WorkflowVariableStore(definition).Snapshot();

        Assert.Throws<ArgumentException>(() => WorkflowVariableStore.FromSnapshot(definition,
            snapshot with { Values = snapshot.Values.RemoveAt(0) }));
        Assert.Throws<ArgumentException>(() => WorkflowVariableStore.FromSnapshot(definition,
            snapshot with { Values = snapshot.Values.SetItem(0, snapshot.Values[0] with { DataType = WorkflowDataTypes.Json }) }));
        Assert.Throws<ArgumentException>(() => WorkflowVariableStore.FromSnapshot(definition,
            snapshot with { Values = snapshot.Values.SetItem(0, snapshot.Values[0] with { Value = "not-a-number" }) }));
        Assert.Throws<ArgumentException>(() => WorkflowVariableStore.FromSnapshot(definition,
            snapshot with { NextVersion = 0 }));
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

    private static WorkflowDefinition AllScalarDefinition() => Definition() with
    {
        Variables = ImmutableSortedDictionary<string, WorkflowVariableDefinition>.Empty
            .Add("count", new("Count", "0") { Type = WorkflowDataTypes.Integer })
            .Add("flag", new("Flag", "false") { Type = WorkflowDataTypes.Boolean })
            .Add("ratio", new("Ratio", "0.5") { Type = WorkflowDataTypes.Number })
            .Add("text", new("Text", "initial") { Type = WorkflowDataTypes.String })
    };

    private static WorkflowDefinition ArrayDefinition()
    {
        var nodes = ImmutableSortedDictionary<string, NodeDefinition>.Empty
            .Add("input", new(NodeTypes.Input, "1.0.0"))
            .Add("array", new(NodeTypes.ArrayLiteral, "1.0.0") { ArrayLiteral = new(WorkflowDataTypes.String, "[\"seed\"]") })
            .Add("set", new(NodeTypes.VariableSet, "1.0.0") { VariableId = "items", InputDataType = "array<string>" })
            .Add("get", new(NodeTypes.VariableGet, "1.0.0") { VariableId = "items", OutputDataType = "array<string>" })
            .Add("output", new(NodeTypes.Output, "1.0.0") { OutputDataType = "array<string>" });
        var data = ImmutableSortedDictionary<string, ConnectionDefinition>.Empty
            .Add("array-set", new(new("array", "out"), new("set", "value"), "data"))
            .Add("get-output", new(new("get", "out"), new("output", "in"), "data"));
        var execution = ImmutableSortedDictionary<string, ConnectionDefinition>.Empty
            .Add("input-set", new(new("input", "then"), new("set", "execute"), "execution"))
            .Add("set-get", new(new("set", "then"), new("get", "execute"), "execution"))
            .Add("get-output", new(new("get", "then"), new("output", "execute"), "execution"));
        return new WorkflowDefinition(WorkflowSchemaVersions.UnifiedWorkflowGraph, "array-variable", "input", nodes, data)
        {
            Variables = ImmutableSortedDictionary<string, WorkflowVariableDefinition>.Empty
                .Add("items", new("Items", "[ \"seed\" ]") { Type = "array<string>" }),
            ExecutionConnections = execution
        };
    }
}
