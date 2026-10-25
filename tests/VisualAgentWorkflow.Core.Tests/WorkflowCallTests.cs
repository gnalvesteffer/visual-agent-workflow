using System.Collections.Immutable;
using VisualAgentWorkflow.Core;
using Xunit;

namespace VisualAgentWorkflow.Core.Tests;

public sealed class WorkflowCallTests
{
    private static WorkflowDefinition Baseline(string workflowId, string schema = "1.0") => new(schema, workflowId, "input",
        new Dictionary<string, NodeDefinition>
        {
            ["input"] = new(NodeTypes.Input, "1.0.0"),
            ["output"] = new(NodeTypes.Output, "1.0.0")
        }.ToImmutableSortedDictionary(StringComparer.Ordinal),
        new Dictionary<string, ConnectionDefinition>
        {
            ["edge"] = new(new("input", "out"), new("output", "in"), "data")
        }.ToImmutableSortedDictionary(StringComparer.Ordinal));

    private static NodeDefinition Call(WorkflowRevision child, int version = 1) => new(NodeTypes.WorkflowCall, "1.0.0")
    {
        WorkflowCall = new(child.Definition.WorkflowId, child.RevisionId, version, child.ContentHash, child.Definition)
    };

    [Fact]
    public void PinnedWorkflowCallValidatesAndPackageListsItsExactRevisionAndRequirements()
    {
        var childDefinition = PipelineEditing.InsertAfter(Baseline("child"), "input", NodeTypes.Uppercase);
        var child = WorkflowRevision.Create(childDefinition, "test");
        var definition = GraphEditing.InsertNodeAfter(Baseline("parent"), "input", "out", Call(child));

        Assert.Equal("1.19", definition.SchemaVersion);
        Assert.Empty(WorkflowValidator.Validate(definition));
        var package = WorkflowPackageJson.Create(definition, WorkflowLayout.Create(definition));
        Assert.Contains(package.Requirements, requirement => requirement.Kind == "workflow-revision" &&
            requirement.Id == "child" && requirement.Version == "1" && requirement.ContractHash == child.ContentHash);
        Assert.Contains(package.Requirements, requirement => requirement.Kind == "node" && requirement.Id == NodeTypes.Uppercase);
        var roundTripRequirements = WorkflowPackageJson.Deserialize(WorkflowPackageJson.Serialize(definition, package.Layout)).Requirements;
        Assert.Equal(package.Requirements.Select(RequirementKey), roundTripRequirements.Select(RequirementKey));
    }

    private static string RequirementKey(PackageRequirement requirement) =>
        $"{requirement.Kind}|{requirement.Id}|{requirement.Version}|{requirement.ContractHash}|{requirement.DescriptionHash}";

    [Fact]
    public void WorkflowCallRejectsTamperedChildAndUnpinnedSchema()
    {
        var child = WorkflowRevision.Create(Baseline("child"), "test");
        var definition = GraphEditing.InsertNodeAfter(Baseline("parent"), "input", "out", Call(child));
        var callNodeId = definition.Nodes.Single(entry => entry.Value.Type == NodeTypes.WorkflowCall).Key;
        definition = definition with { Nodes = definition.Nodes.SetItem(callNodeId,
            definition.Nodes[callNodeId] with { WorkflowCall = definition.Nodes[callNodeId].WorkflowCall! with { ContentHash = new string('0', 64) } }) };

        Assert.Contains(WorkflowValidator.Validate(definition), issue => issue.Code == "workflowCall.configuration");
        Assert.Contains(WorkflowValidator.Validate(definition with { SchemaVersion = "1.18" }), issue => issue.Code == "workflowCall.configuration");
    }

    [Fact]
    public void NestedWorkflowIdsCannotReenterAnAncestor()
    {
        var child = WorkflowRevision.Create(Baseline("child"), "test");
        var root = GraphEditing.InsertNodeAfter(Baseline("root"), "input", "out", Call(child));
        var callNodeId = root.Nodes.Single(entry => entry.Value.Type == NodeTypes.WorkflowCall).Key;
        root = root with { Nodes = root.Nodes.SetItem(callNodeId,
            root.Nodes[callNodeId] with { WorkflowCall = root.Nodes[callNodeId].WorkflowCall! with { WorkflowId = "root" } }) };

        Assert.Contains(WorkflowValidator.Validate(root), issue => issue.Code == "workflowCall.cycle");
    }

    [Fact]
    public void NestedWorkflowApprovalIsRejectedUntilItHasAChildScopedAuthorizationContract()
    {
        var childGraph = GraphEditing.InsertNodeAfter(Baseline("child"), "input", "out",
            new(NodeTypes.McpTool, "1.0.0", new("test.tool", new string('a', 64))));
        var toolNodeId = childGraph.Nodes.Single(entry => entry.Value.Type == NodeTypes.McpTool).Key;
        childGraph = GraphEditing.InsertApprovalBefore(childGraph, toolNodeId);
        var child = WorkflowRevision.Create(childGraph, "test");
        var error = Assert.Throws<ArgumentException>(() =>
            GraphEditing.InsertNodeAfter(Baseline("parent"), "input", "out", Call(child)));

        Assert.Contains("workflowCall.approval", error.Message);
    }
}
