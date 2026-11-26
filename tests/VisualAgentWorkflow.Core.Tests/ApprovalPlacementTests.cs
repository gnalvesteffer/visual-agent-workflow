using System.Collections.Immutable;
using VisualAgentWorkflow.Core;
using Xunit;

namespace VisualAgentWorkflow.Core.Tests;

/// <summary>
/// Approval placement (Decision 0081, "Approval placement rule (2026-11-25)"): an approval whose execution path lies inside a
/// Sequence output of a Sequence with more than one output is rejected with <c>approval.placement</c>. The walk is the shared
/// region walk behind <c>wait.placement</c>. Root-path approvals keep validating.
/// </summary>
public sealed class ApprovalPlacementTests
{
    private static ConnectionDefinition Exec(string from, string fromPin, string to, string toPin) => new(new(from, fromPin), new(to, toPin), "execution");

    private static ConnectionDefinition Data(string from, string fromPin, string to, string toPin) => new(new(from, fromPin), new(to, toPin), "data");

    private static NodeDefinition Tool(string binding) => new(NodeTypes.McpTool, "1.0.0", new ToolBindingRequest(binding, new string('a', 64))) { McpArgumentsJson = "{}" };

    private static NodeDefinition Approval() => new(NodeTypes.Approval, "1.0.0") { Approval = new("Allow this tool?") };

    /// <summary>
    /// input -> sequence. The <c>first</c> output runs approval -> first-tool, the <c>second</c> output runs second-tool; both reach output.
    /// </summary>
    private static WorkflowDefinition ApprovalInSequenceOutput(int outputCount = 2)
    {
        var outputs = Enumerable.Range(1, outputCount).Select(index => new SequenceOutputDescriptor(index == 1 ? "first" : $"output{index}", $"Output {index}")).ToArray();
        var nodes = ImmutableSortedDictionary.CreateBuilder<string, NodeDefinition>(StringComparer.Ordinal);
        nodes["input"] = new(NodeTypes.Input, "1.0.0");
        nodes["sequence"] = new(NodeTypes.Sequence, "1.0.0") { Sequence = new([.. outputs]) };
        nodes["approval"] = Approval();
        nodes["first-tool"] = Tool("fixture.first");
        nodes["second-tool"] = Tool("fixture.second");
        nodes["output"] = new(NodeTypes.Output, "1.0.0");
        var execution = ImmutableSortedDictionary.CreateBuilder<string, ConnectionDefinition>(StringComparer.Ordinal);
        execution["input-sequence"] = Exec("input", "then", "sequence", "execute");
        execution["sequence-approval"] = Exec("sequence", SequenceOutputLimits.PinId("first"), "approval", "execute");
        execution["approval-tool"] = Exec("approval", "then", "first-tool", "execute");
        execution["first-output"] = Exec("first-tool", "then", "output", "execute");
        execution["sequence-second"] = Exec("sequence", SequenceOutputLimits.PinId("output2"), "second-tool", "execute");
        execution["second-output"] = Exec("second-tool", "then", "output", "execute");
        var data = ImmutableSortedDictionary.CreateBuilder<string, ConnectionDefinition>(StringComparer.Ordinal);
        data["input-approval"] = Data("input", "out", "approval", "in");
        data["approval-tool"] = Data("approval", "out", "first-tool", "in");
        return new(WorkflowSchemaVersions.UnifiedWorkflowGraph, "approval-placement", "input", nodes.ToImmutable(), data.ToImmutable())
        {
            ExecutionConnections = execution.ToImmutable()
        };
    }

    /// <summary>input -> approval -> tool -> output on the root path, with no Sequence and no Fork.</summary>
    private static WorkflowDefinition RootPathApproval()
    {
        var nodes = ImmutableSortedDictionary.CreateBuilder<string, NodeDefinition>(StringComparer.Ordinal);
        nodes["input"] = new(NodeTypes.Input, "1.0.0");
        nodes["approval"] = Approval();
        nodes["tool"] = Tool("fixture.tool");
        nodes["output"] = new(NodeTypes.Output, "1.0.0");
        var execution = ImmutableSortedDictionary.CreateBuilder<string, ConnectionDefinition>(StringComparer.Ordinal);
        execution["input-approval"] = Exec("input", "then", "approval", "execute");
        execution["approval-tool"] = Exec("approval", "then", "tool", "execute");
        execution["tool-output"] = Exec("tool", "then", "output", "execute");
        var data = ImmutableSortedDictionary.CreateBuilder<string, ConnectionDefinition>(StringComparer.Ordinal);
        data["input-approval"] = Data("input", "out", "approval", "in");
        data["approval-tool"] = Data("approval", "out", "tool", "in");
        data["tool-output"] = Data("tool", "out", "output", "in");
        return new(WorkflowSchemaVersions.UnifiedWorkflowGraph, "approval-root", "input", nodes.ToImmutable(), data.ToImmutable())
        {
            ExecutionConnections = execution.ToImmutable()
        };
    }

    /// <summary>
    /// input -> fork -> two branches -> join -> approval -> tool -> output. The approval sits after the Fork/Join region on the
    /// root path, not inside any Sequence output.
    /// </summary>
    private static WorkflowDefinition ApprovalAfterForkJoin()
    {
        var nodes = ImmutableSortedDictionary.CreateBuilder<string, NodeDefinition>(StringComparer.Ordinal);
        nodes["input"] = new(NodeTypes.Input, "1.0.0");
        nodes["fork"] = new(NodeTypes.ParallelFork, "1.0.0") { ParallelFork = new([new("a", "A", "branch-a"), new("b", "B", "branch-b")]) };
        nodes["branch-a"] = new(NodeTypes.ParallelBranch, "1.0.0");
        nodes["branch-b"] = new(NodeTypes.ParallelBranch, "1.0.0");
        nodes["join"] = new(NodeTypes.ParallelJoin, "1.0.0") { ParallelJoin = new("fork", ["a", "b"]) };
        nodes["approval"] = Approval();
        nodes["tool"] = Tool("fixture.tool");
        nodes["output"] = new(NodeTypes.Output, "1.0.0");
        var execution = ImmutableSortedDictionary.CreateBuilder<string, ConnectionDefinition>(StringComparer.Ordinal);
        execution["input-fork"] = Exec("input", "then", "fork", "execute");
        execution["fork-a"] = Exec("fork", ParallelBranchLimits.PinId("a"), "branch-a", "execute");
        execution["fork-b"] = Exec("fork", ParallelBranchLimits.PinId("b"), "branch-b", "execute");
        execution["a-join"] = Exec("branch-a", "then", "join", ParallelBranchLimits.PinId("a"));
        execution["b-join"] = Exec("branch-b", "then", "join", ParallelBranchLimits.PinId("b"));
        execution["join-approval"] = Exec("join", "then", "approval", "execute");
        execution["approval-tool"] = Exec("approval", "then", "tool", "execute");
        execution["tool-output"] = Exec("tool", "then", "output", "execute");
        var data = ImmutableSortedDictionary.CreateBuilder<string, ConnectionDefinition>(StringComparer.Ordinal);
        data["input-approval"] = Data("input", "out", "approval", "in");
        data["approval-tool"] = Data("approval", "out", "tool", "in");
        data["tool-output"] = Data("tool", "out", "output", "in");
        return new(WorkflowSchemaVersions.UnifiedWorkflowGraph, "approval-after-join", "input", nodes.ToImmutable(), data.ToImmutable())
        {
            ExecutionConnections = execution.ToImmutable()
        };
    }

    [Fact]
    public void An_approval_in_one_output_of_a_two_output_sequence_is_rejected_with_approval_placement()
    {
        var definition = ApprovalInSequenceOutput();

        Assert.Equal(["approval"], WorkflowWaitPlacement.NodesInsideConcurrentSequenceOutputs(definition).ToArray());
        Assert.Contains(WorkflowValidator.Validate(definition), issue => issue.Code == "approval.placement" && issue.Path == "nodes.approval");
    }

    [Fact]
    public void The_placement_rule_does_not_fire_for_a_single_output_sequence()
    {
        // The Sequence contract requires at least two outputs (SequenceOutputLimits.MinimumOutputs), so a one-output Sequence
        // is reported by sequence.configuration. This test pins only that the placement rule keys on more than one output.
        var definition = ApprovalInSequenceOutput(outputCount: 1);

        Assert.Empty(WorkflowWaitPlacement.NodesInsideConcurrentSequenceOutputs(definition));
        var issues = WorkflowValidator.Validate(definition);
        Assert.DoesNotContain(issues, issue => issue.Code == "approval.placement");
        Assert.Contains(issues, issue => issue.Code == "sequence.configuration");
    }

    [Fact]
    public void An_approval_on_the_root_path_without_a_sequence_validates()
    {
        var definition = RootPathApproval();

        Assert.Empty(WorkflowWaitPlacement.NodesInsideConcurrentSequenceOutputs(definition));
        Assert.Empty(WorkflowValidator.Validate(definition));
    }

    [Fact]
    public void An_approval_after_a_fork_join_on_the_root_path_validates()
    {
        var definition = ApprovalAfterForkJoin();

        Assert.Empty(WorkflowWaitPlacement.NodesInsideConcurrentSequenceOutputs(definition));
        Assert.DoesNotContain(WorkflowValidator.Validate(definition), issue => issue.Code == "approval.placement");
    }

    [Fact]
    public void Without_an_execution_table_every_approval_beside_a_multi_output_sequence_is_reported()
    {
        // Fail closed, as wait.placement does: the walk cannot prove an approval is outside a Sequence output without edges.
        var definition = ApprovalInSequenceOutput() with { ExecutionConnections = null };

        Assert.Equal(["approval"], WorkflowWaitPlacement.NodesInsideConcurrentSequenceOutputs(definition).ToArray());
    }
}
