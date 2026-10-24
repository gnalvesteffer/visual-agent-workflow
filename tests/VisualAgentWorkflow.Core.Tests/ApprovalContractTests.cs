using System.Collections.Immutable;
using VisualAgentWorkflow.Core;
using Xunit;

namespace VisualAgentWorkflow.Core.Tests;

public sealed class ApprovalContractTests
{
    private static WorkflowDefinition ToolGraph() => new("1.1", "approval-contract", "input",
        new Dictionary<string, NodeDefinition>
        {
            ["input"] = new(NodeTypes.Input, "1.0.0"), ["tool"] = new(NodeTypes.McpTool, "1.0.0", new("fixture.tool", new string('0', 64))),
            ["output"] = new(NodeTypes.Output, "1.0.0")
        }.ToImmutableSortedDictionary(), new Dictionary<string, ConnectionDefinition>
        {
            ["a"] = new(new("input", "out"), new("tool", "in"), "data"), ["b"] = new(new("tool", "out"), new("output", "in"), "data")
        }.ToImmutableSortedDictionary());

    [Fact]
    public void ApprovalRoundTripsAndRemovalPreservesToolAndLegacyJson()
    {
        var baseline = ToolGraph();
        var original = WorkflowJson.Serialize(baseline);
        Assert.DoesNotContain("\"approval\":", original);
        var graph = GraphEditing.InsertApprovalBefore(baseline, "tool");
        Assert.Equal("1.5", graph.SchemaVersion);
        var node = graph.Nodes.Single(n => n.Value.Type == NodeTypes.Approval).Key;
        var package = WorkflowPackageJson.Deserialize(WorkflowPackageJson.Serialize(graph, WorkflowLayout.Create(graph)));
        Assert.Equal(WorkflowJson.ComputeHash(graph), package.DefinitionHash);
        Assert.Equal(baseline.Nodes["tool"], graph.Nodes["tool"]);
        Assert.Throws<ArgumentException>(() => PipelineEditing.OrderedNodes(graph));
        Assert.Contains(WorkflowValidator.Validate(graph with { SchemaVersion = "1.4" }), i => i.Code == "node.schema");
        var removed = GraphEditing.RemoveApproval(graph, node);
        Assert.Equal(original, WorkflowJson.Serialize(removed with { SchemaVersion = baseline.SchemaVersion }));
    }

    [Fact]
    public void ApprovalCannotGuardUnknownOperationsOrAcceptMissingPrompts()
    {
        var graph = GraphEditing.InsertApprovalBefore(ToolGraph(), "tool");
        var id = graph.Nodes.Single(n => n.Value.Type == NodeTypes.Approval).Key;
        Assert.Throws<ArgumentException>(() => GraphEditing.InsertApprovalBefore(graph, "tool"));
        Assert.Throws<ArgumentException>(() => GraphEditing.InsertApprovalBefore(graph, "output"));
        Assert.Contains(WorkflowValidator.Validate(graph with { Nodes = graph.Nodes.SetItem("tool", new(NodeTypes.Reverse, "1.0.0")) }), i => i.Code == "approval.target");
        Assert.Contains(WorkflowValidator.Validate(graph with { Nodes = graph.Nodes.SetItem(id, graph.Nodes[id] with { Approval = new(" ") }) }), i => i.Code == "approval.configuration");
    }

    [Fact]
    public void PausesAndApprovalsRemainIndependentInRecordedStatus()
    {
        var graph = GraphEditing.InsertApprovalBefore(ToolGraph(), "tool");
        var revision = WorkflowRevision.Create(graph, "fixture");
        var id = Guid.NewGuid();
        var snapshot = new RunSnapshot(new(id, revision.RevisionId, null, revision.Author, revision.CreatedAt, revision.ContentHash, graph, "text"), []);
        ExecutionEvent Event(string kind, int sequence) => new(id, revision.RevisionId, sequence, DateTimeOffset.UtcNow, kind);
        snapshot = snapshot with { Events = [Event(ExecutionEventKinds.Started, 1), Event(ExecutionEventKinds.ApprovalRequested, 2)] };
        Assert.Equal("waiting-approval", snapshot.Status);
        snapshot = snapshot with { Events = snapshot.Events.Add(Event(ExecutionEventKinds.Paused, 3)).Add(Event(ExecutionEventKinds.ApprovalApproved, 4)) };
        Assert.Equal("paused", snapshot.Status);
        snapshot = snapshot with { Events = snapshot.Events.Add(Event(ExecutionEventKinds.Resumed, 5)) };
        Assert.Equal("running", snapshot.Status);
        snapshot = snapshot with { Events = snapshot.Events.Add(Event(ExecutionEventKinds.Interrupted, 6)) };
        Assert.True(snapshot.IsTerminal);
        Assert.Null(WorkflowApprovalEvidence.Pending(snapshot));
    }
}
