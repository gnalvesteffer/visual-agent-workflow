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

    [Fact]
    public void WorkflowCallMappingsRequireDeclaredCompatibleParameters()
    {
        var childDefinition = WorkflowParameterEditing.Add(Baseline("child"), "tone", "Tone", "calm");
        var parentDefinition = WorkflowParameterEditing.Add(Baseline("parent"), "style", "Style", "calm");
        var child = WorkflowRevision.Create(childDefinition, "test");
        var call = Call(child) with { WorkflowCall = Call(child).WorkflowCall! with
        { ParameterMappings = [new("tone", "style")] } };
        var definition = GraphEditing.InsertNodeAfter(parentDefinition, "input", "out", call);

        Assert.Empty(WorkflowValidator.Validate(definition));

        var callId = definition.Nodes.Single(entry => entry.Value.Type == NodeTypes.WorkflowCall).Key;
        var invalidCall = definition.Nodes[callId] with { WorkflowCall = definition.Nodes[callId].WorkflowCall! with
        { ParameterMappings = [new("missing", "style")] } };
        var invalid = definition with { Nodes = definition.Nodes.SetItem(callId, invalidCall) };
        Assert.Contains(WorkflowValidator.Validate(invalid), issue => issue.Code == "workflowCall.parameters.reference");
    }

    [Fact]
    public void WorkflowCallMappingsRejectIncompatibleTypesAndDuplicateChildTargets()
    {
        var childDefinition = WorkflowParameterEditing.SetType(
            WorkflowParameterEditing.Add(Baseline("child"), "tone", "Tone", "1"), "tone", "integer", "1");
        var parentDefinition = WorkflowParameterEditing.Add(Baseline("parent"), "style", "Style", "calm");
        var child = WorkflowRevision.Create(childDefinition, "test");
        var definition = GraphEditing.InsertNodeAfter(parentDefinition, "input", "out", Call(child));
        var callId = definition.Nodes.Single(entry => entry.Value.Type == NodeTypes.WorkflowCall).Key;
        var call = definition.Nodes[callId] with { WorkflowCall = definition.Nodes[callId].WorkflowCall! with
        { ParameterMappings = [new("tone", "style"), new("tone", "style")] } };
        definition = definition with { Nodes = definition.Nodes.SetItem(callId, call) };

        Assert.Contains(WorkflowValidator.Validate(definition), issue => issue.Code == "workflowCall.parameters.duplicate");
        Assert.Contains(WorkflowValidator.Validate(definition), issue => issue.Code == "workflowCall.parameters.type");
    }

    [Fact]
    public void WorkflowCallMappingsRoundTripAndArePartOfThePinnedParentContent()
    {
        var childDefinition = WorkflowParameterEditing.Add(Baseline("child"), "tone", "Tone", "calm");
        var parentDefinition = WorkflowParameterEditing.Add(Baseline("parent"), "style", "Style", "calm");
        var child = WorkflowRevision.Create(childDefinition, "test");
        var unmapped = GraphEditing.InsertNodeAfter(parentDefinition, "input", "out", Call(child));
        var nodeId = unmapped.Nodes.Single(entry => entry.Value.Type == NodeTypes.WorkflowCall).Key;
        var call = unmapped.Nodes[nodeId] with { WorkflowCall = unmapped.Nodes[nodeId].WorkflowCall! with
        { ParameterMappings = [new("tone", "style")] } };
        var mapped = unmapped with { Nodes = unmapped.Nodes.SetItem(nodeId, call) };
        var roundTrip = WorkflowJson.Deserialize(WorkflowJson.Serialize(mapped));

        Assert.Equal("style", roundTrip.Nodes[nodeId].WorkflowCall!.ParameterMappings!.Value.Single().ParentParameterId);
        Assert.NotEqual(WorkflowJson.ComputeHash(unmapped), WorkflowJson.ComputeHash(mapped));
        Assert.Empty(WorkflowValidator.Validate(roundTrip));
    }

    [Fact]
    public void WorkflowCallPinsJsonInputAndOutputPinTypesFromItsChild()
    {
        var child = WorkflowRevision.Create(JsonBoundary(Baseline("child")), "test");
        var call = Call(child);
        var parent = JsonBoundary(Baseline("parent"));
        var definition = GraphEditing.InsertNodeAfter(parent, "input", "out", call);

        Assert.Equal("json", WorkflowPorts.Inputs(call).Single().DataType);
        Assert.Equal("json", WorkflowPorts.Outputs(call).Single().DataType);
        Assert.Equal("json", WorkflowPorts.Inputs(definition.Nodes["output"]).Single().DataType);
        Assert.Empty(WorkflowValidator.Validate(definition));
        var roundTrip = WorkflowJson.Deserialize(WorkflowJson.Serialize(definition));
        var roundTripCall = roundTrip.Nodes.Single(entry => entry.Value.Type == NodeTypes.WorkflowCall).Value;
        Assert.Equal("json", WorkflowPorts.Inputs(roundTripCall).Single().DataType);
        Assert.Throws<ArgumentException>(() => GraphEditing.InsertNodeAfter(Baseline("string-parent"), "input", "out", call));
    }

    [Fact]
    public void ExplicitCallUpgradePinsNewRevisionAndRetainsOnlyCompatibleMappings()
    {
        var childDefinition = WorkflowParameterEditing.Add(
            WorkflowParameterEditing.Add(Baseline("child"), "tone", "Tone", "calm"), "count", "Count", "one");
        var originalChild = WorkflowRevision.Create(childDefinition, "test");
        var parentDefinition = WorkflowParameterEditing.Add(
            WorkflowParameterEditing.Add(Baseline("parent"), "style", "Style", "calm"), "count", "Count", "one");
        var call = Call(originalChild) with { WorkflowCall = Call(originalChild).WorkflowCall! with
        { ParameterMappings = [new("tone", "style"), new("count", "count")] } };
        var parent = GraphEditing.InsertNodeAfter(parentDefinition, "input", "out", call);
        var callNodeId = parent.Nodes.Single(entry => entry.Value.Type == NodeTypes.WorkflowCall).Key;
        var newerChildDefinition = WorkflowParameterEditing.SetType(childDefinition, "count", "integer", "1");
        var newerChild = WorkflowRevision.Create(newerChildDefinition, "test", originalChild.RevisionId);

        var result = WorkflowCallEditing.Upgrade(parent, callNodeId, new(2, newerChild));
        var upgradedCall = result.Definition.Nodes[callNodeId].WorkflowCall!;

        Assert.Equal(newerChild.RevisionId, upgradedCall.RevisionId);
        Assert.Equal(2, upgradedCall.Version);
        Assert.Equal(newerChild.ContentHash, upgradedCall.ContentHash);
        Assert.Equal("tone", upgradedCall.ParameterMappings!.Value.Single().ChildParameterId);
        Assert.Equal("count", Assert.Single(result.DroppedParameterMappings).ChildParameterId);
        Assert.Empty(WorkflowValidator.Validate(result.Definition));
    }

    [Fact]
    public void CallUpgradeRejectsDifferentWorkflowAndIncompatibleBoundaryWithoutChangingParent()
    {
        var originalChild = WorkflowRevision.Create(Baseline("child"), "test");
        var parent = GraphEditing.InsertNodeAfter(Baseline("parent"), "input", "out", Call(originalChild));
        var callNodeId = parent.Nodes.Single(entry => entry.Value.Type == NodeTypes.WorkflowCall).Key;
        var differentWorkflow = WorkflowRevision.Create(Baseline("another-child"), "test");
        var jsonInputChild = WorkflowRevision.Create(JsonBoundary(Baseline("child")), "test");

        Assert.Throws<ArgumentException>(() => WorkflowCallEditing.Upgrade(parent, callNodeId, new(2, differentWorkflow)));
        Assert.Throws<ArgumentException>(() => WorkflowCallEditing.Upgrade(parent, callNodeId, new(2, jsonInputChild)));
        Assert.Equal(originalChild.RevisionId, parent.Nodes[callNodeId].WorkflowCall!.RevisionId);
    }

    [Fact]
    public void NestedApprovalsBindToRootAndExactCallPathWhileValidatingChildOperations()
    {
        var childDefinition = GraphEditing.InsertApprovalBefore(
            Baseline("approved-child", "1.1") with
            {
                Nodes = Baseline("approved-child", "1.1").Nodes
                    .SetItem("tool", new(NodeTypes.McpTool, "1.0.0", new("fixture.tool", new string('0', 64)))),
                Connections = ImmutableSortedDictionary<string, ConnectionDefinition>.Empty
                    .Add("input-tool", new(new("input", "out"), new("tool", "in"), "data"))
                    .Add("tool-output", new(new("tool", "out"), new("output", "in"), "data"))
            }, "tool");
        var child = WorkflowRevision.Create(childDefinition, "test");
        var parent = GraphEditing.InsertNodeAfter(Baseline("approval-parent"), "input", "out", Call(child));
        var callNodeId = parent.Nodes.Single(entry => entry.Value.Type == NodeTypes.WorkflowCall).Key;
        var rootRevision = WorkflowRevision.Create(parent, "test");
        var childApprovalId = child.Definition.Nodes.Single(entry => entry.Value.Type == NodeTypes.Approval).Key;
        var childTool = child.Definition.Nodes["tool"].Tool!;
        var runId = Guid.NewGuid();
        var action = new WorkflowApprovalAction(runId, rootRevision.RevisionId, rootRevision.ContentHash,
            childApprovalId, Guid.NewGuid(), "tool", Guid.NewGuid(), childTool, "payload", WorkflowApprovalAction.HashInput("payload"))
        {
            ExecutingRevisionId = child.RevisionId,
            ExecutingWorkflowHash = child.ContentHash,
            WorkflowPath = [new(callNodeId, child.RevisionId, child.ContentHash)]
        };

        Assert.Empty(WorkflowValidator.Validate(parent));
        Assert.Equal(callNodeId, action.ScopeId);
        Assert.True(WorkflowApprovalEvidence.MatchesDefinition(parent, runId, rootRevision.RevisionId,
            rootRevision.ContentHash, action, child.Definition.Nodes[childApprovalId].Approval!.Prompt));
        Assert.False(WorkflowApprovalEvidence.MatchesDefinition(parent, runId, rootRevision.RevisionId,
            rootRevision.ContentHash, action with { WorkflowPath = [new(callNodeId, Guid.NewGuid(), child.ContentHash)] },
            child.Definition.Nodes[childApprovalId].Approval!.Prompt));
    }

    private static WorkflowDefinition JsonBoundary(WorkflowDefinition definition) => definition with
    {
        SchemaVersion = "1.19",
        Nodes = definition.Nodes
            .SetItem("input", definition.Nodes["input"] with { InputDataType = "json" })
            .SetItem("output", definition.Nodes["output"] with { OutputDataType = "json" })
    };

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
    public void NestedWorkflowApprovalIsAcceptedInsideItsExactPinnedChildDefinition()
    {
        var childGraph = GraphEditing.InsertNodeAfter(Baseline("child"), "input", "out",
            new(NodeTypes.McpTool, "1.0.0", new("test.tool", new string('a', 64))));
        var toolNodeId = childGraph.Nodes.Single(entry => entry.Value.Type == NodeTypes.McpTool).Key;
        childGraph = GraphEditing.InsertApprovalBefore(childGraph, toolNodeId);
        var child = WorkflowRevision.Create(childGraph, "test");
        var parent = GraphEditing.InsertNodeAfter(Baseline("parent"), "input", "out", Call(child));

        Assert.Empty(WorkflowValidator.Validate(parent));
    }

    [Fact]
    public void ParallelBranchesCannotWaitForNestedApprovalsAtTheSameTime()
    {
        var childGraph = GraphEditing.InsertNodeAfter(Baseline("approved-child"), "input", "out",
            new(NodeTypes.McpTool, "1.0.0", new("test.tool", new string('a', 64))));
        var toolNodeId = childGraph.Nodes.Single(entry => entry.Value.Type == NodeTypes.McpTool).Key;
        childGraph = GraphEditing.InsertApprovalBefore(childGraph, toolNodeId);
        var child = WorkflowRevision.Create(childGraph, "test");
        var graph = GraphEditing.InsertParallelBlockAfter(Baseline("parallel-parent"), "input", "out");
        var forkId = graph.Nodes.Single(entry => entry.Value.Type == NodeTypes.ParallelFork).Key;
        var firstAnchor = graph.Connections.Values.Single(edge => edge.Source == new PortReference(forkId, "branch_1")).Target.NodeId;
        var secondAnchor = graph.Connections.Values.Single(edge => edge.Source == new PortReference(forkId, "branch_2")).Target.NodeId;

        var oneApprovalBranch = GraphEditing.InsertNodeAfter(graph, firstAnchor, "out", Call(child));
        var branchEdge = oneApprovalBranch.Connections.Single(entry => entry.Value.Source.NodeId == secondAnchor);
        var secondCallId = "second-call";
        var twoApprovalBranches = oneApprovalBranch with
        {
            Nodes = oneApprovalBranch.Nodes.Add(secondCallId, Call(child)),
            Connections = oneApprovalBranch.Connections.Remove(branchEdge.Key)
                .Add("second-call-in", new(new(secondAnchor, "out"), new(secondCallId, "in"), "data"))
                .Add("second-call-out", new(new(secondCallId, "out"), branchEdge.Value.Target, "data"))
        };

        Assert.Empty(WorkflowValidator.Validate(oneApprovalBranch));
        Assert.Contains(WorkflowValidator.Validate(twoApprovalBranches), issue => issue.Code == "parallel.approval.concurrent");
    }
}
