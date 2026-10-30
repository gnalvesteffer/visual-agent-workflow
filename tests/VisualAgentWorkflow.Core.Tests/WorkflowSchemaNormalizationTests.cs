using System.Collections.Immutable;
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
    public void UnsupportedImplicitControlFlowReportsDiagnosticWithoutMutatingSource()
    {
        var source = GraphEditing.InsertConditionalAfter(Blank(), "input", "out");
        var original = WorkflowJson.Serialize(source);
        var exception = Assert.Throws<ArgumentException>(() => WorkflowSchemaNormalization.NormalizeForEditing(source));
        Assert.Contains("normalization.implicit-unsafe", exception.Message);
        Assert.Equal(original, WorkflowJson.Serialize(source));
    }
}
