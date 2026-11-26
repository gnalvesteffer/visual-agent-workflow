using System.Collections.Immutable;
using VisualAgentWorkflow.Core;
using Xunit;

namespace VisualAgentWorkflow.Core.Tests;

/// <summary>
/// Decision 0070 slice (b) unblocker 4: a body-local Sequence barrier in a typed For Each body. The barrier rule is
/// opt-in on the topology proof, the compiled plan and the validator arm; public validation still rejects the shape.
/// </summary>
public sealed class BodyLocalSequenceBarrierTests
{
    private static ConnectionDefinition Execution(string from, string fromPin, string to, string toPin) =>
        new(new(from, fromPin), new(to, toPin), "execution");

    private static ConnectionDefinition Data(string from, string fromPin, string to, string toPin) =>
        new(new(from, fromPin), new(to, toPin), "data");

    /// <summary>For Each body: Body → Sequence; each output runs one step and returns to the feedback pin.</summary>
    internal static WorkflowDefinition BarrierWorkflow(
        Func<ImmutableSortedDictionary<string, NodeDefinition>.Builder, ImmutableSortedDictionary<string, NodeDefinition>.Builder>? nodes = null,
        Func<ImmutableSortedDictionary<string, ConnectionDefinition>, ImmutableSortedDictionary<string, ConnectionDefinition>>? execution = null,
        string iteratorType = NodeTypes.ForEach)
    {
        var nodeBuilder = ImmutableSortedDictionary.CreateBuilder<string, NodeDefinition>(StringComparer.Ordinal);
        nodeBuilder["input"] = new(NodeTypes.Input, "1.0.0");
        nodeBuilder["items"] = new(NodeTypes.ArrayLiteral, "1.0.0") { ArrayLiteral = new(WorkflowDataTypes.String, "[\"a\",\"b\"]") };
        nodeBuilder["each"] = new(iteratorType, "1.0.0") { ArrayElementType = iteratorType == NodeTypes.ForEach ? WorkflowDataTypes.String : null };
        nodeBuilder["body"] = new(NodeTypes.LoopBody, "1.0.0");
        nodeBuilder["sequence"] = new(NodeTypes.Sequence, "1.0.0")
        {
            Sequence = new([new("first", "First"), new("second", "Second")])
        };
        nodeBuilder["step-a"] = new(NodeTypes.SequenceReverse, "1.0.0");
        nodeBuilder["step-b"] = new(NodeTypes.SequenceUppercase, "1.0.0");
        nodeBuilder["output"] = new(NodeTypes.Output, "1.0.0");
        nodes?.Invoke(nodeBuilder);
        var dataEdges = ImmutableSortedDictionary<string, ConnectionDefinition>.Empty
            .Add("items-each", Data("items", "out", "each", "array"))
            .Add("item-a", Data("each", "item", "step-a", "in"))
            .Add("item-b", Data("each", "item", "step-b", "in"))
            .Add("result", Data("step-a", "out", "output", "in"));
        var executionEdges = ImmutableSortedDictionary<string, ConnectionDefinition>.Empty
            .Add("start", Execution("input", "then", "each", "execute"))
            .Add("body-start", Execution("each", "body", "body", "execute"))
            .Add("body-sequence", Execution("body", "then", "sequence", "execute"))
            .Add("first-a", Execution("sequence", "then.first", "step-a", "execute"))
            .Add("second-b", Execution("sequence", "then.second", "step-b", "execute"))
            .Add("return-a", Execution("step-a", "then", "each", "feedback"))
            .Add("return-b", Execution("step-b", "then", "each", "feedback"))
            .Add("done", Execution("each", "completed", "output", "execute"));
        return new(WorkflowSchemaVersions.UnifiedWorkflowGraph, "for-each-sequence-barrier", "input", nodeBuilder.ToImmutable(),
            dataEdges)
        {
            ExecutionConnections = execution?.Invoke(executionEdges) ?? executionEdges
        };
    }

    [Fact]
    public void Without_the_barrier_rule_two_sequence_returns_still_fail_the_cardinality_proof()
    {
        var proof = LoopFeedbackTopologyProofAnalyzer.Analyze(BarrierWorkflow(), LoopFeedbackPinShape.ExplicitFeedbackInput);

        Assert.False(proof.IsValid);
        Assert.Contains(proof.Issues, issue => issue.Code == "loop.feedback.cardinality");
    }

    [Fact]
    public void The_barrier_rule_joins_every_sequence_output_before_the_feedback_pin()
    {
        var proof = LoopFeedbackTopologyProofAnalyzer.Analyze(BarrierWorkflow(), LoopFeedbackPinShape.ExplicitFeedbackInput,
            allowBodyLocalSequenceBarrier: true);

        Assert.True(proof.IsValid, string.Join("; ", proof.Issues.Select(issue => issue.Code)));
        var region = Assert.Single(proof.Regions);
        var barrier = Assert.IsType<LoopFeedbackSequenceBarrierProof>(region.SequenceBarrier);
        Assert.Equal("each", barrier.LoopNodeId);
        Assert.Equal("sequence", barrier.SequenceNodeId);
        Assert.Equal(["then.first", "then.second"], barrier.SequenceOutputPinIds.ToArray());
        Assert.Equal(["return-a", "return-b"], barrier.ReturnConnectionIds.ToArray());
        Assert.Equal(["step-a", "step-b"], barrier.RouteNodeIds.ToArray());
        Assert.Equal("return-a", region.FeedbackConnectionId);
        Assert.Contains("sequence", region.BodyNodeIds);
    }

    [Fact]
    public void The_barrier_rule_rejects_a_sequence_output_that_does_not_return()
    {
        var definition = BarrierWorkflow(nodes: nodes =>
        {
            nodes["sequence"] = new(NodeTypes.Sequence, "1.0.0")
            {
                Sequence = new([new("first", "First"), new("second", "Second"), new("third", "Third")])
            };
            nodes["step-c"] = new(NodeTypes.SequenceReverse, "1.0.0");
            return nodes;
        }, execution: edges => edges.Add("third-c", Execution("sequence", "then.third", "step-c", "execute")));

        var proof = LoopFeedbackTopologyProofAnalyzer.Analyze(definition, LoopFeedbackPinShape.ExplicitFeedbackInput,
            allowBodyLocalSequenceBarrier: true);

        Assert.False(proof.IsValid);
    }

    [Fact]
    public void The_barrier_rule_rejects_returns_that_mix_a_sequence_route_with_another_route()
    {
        // A second return that does not come from the Sequence (here a second Sequence's output) is not joined.
        var definition = BarrierWorkflow(nodes: nodes =>
        {
            nodes["inner"] = new(NodeTypes.Sequence, "1.0.0")
            {
                Sequence = new([new("x", "X"), new("y", "Y")])
            };
            nodes["step-c"] = new(NodeTypes.SequenceReverse, "1.0.0");
            return nodes;
        }, execution: edges => edges.SetItem("second-b", Execution("sequence", "then.second", "inner", "execute"))
            .Add("inner-b", Execution("inner", "then.x", "step-b", "execute"))
            .Add("inner-c", Execution("inner", "then.y", "step-c", "execute"))
            .Add("return-c", Execution("step-c", "then", "each", "feedback")));

        var proof = LoopFeedbackTopologyProofAnalyzer.Analyze(definition, LoopFeedbackPinShape.ExplicitFeedbackInput,
            allowBodyLocalSequenceBarrier: true);

        Assert.False(proof.IsValid);
        Assert.Contains(proof.Issues, issue => issue.Code == "loop.feedback.sequence-barrier.owner");
    }

    [Fact]
    public void The_barrier_rule_is_for_a_for_each_owner_only()
    {
        var definition = BarrierWorkflow(iteratorType: NodeTypes.Loop) with
        {
            Connections = ImmutableSortedDictionary<string, ConnectionDefinition>.Empty
        };

        var proof = LoopFeedbackTopologyProofAnalyzer.Analyze(definition, LoopFeedbackPinShape.ExplicitFeedbackInput,
            allowBodyLocalSequenceBarrier: true);

        Assert.False(proof.IsValid);
        Assert.Contains(proof.Issues, issue => issue.Code == "loop.feedback.sequence-barrier.owner");
    }

    [Fact]
    public void The_compiled_plan_for_the_barrier_route_records_the_barrier_and_its_activation_bound()
    {
        var definition = BarrierWorkflow();

        Assert.False(StructuredActivationGraphPlan.TryCreate(definition, out _, out _));
        Assert.True(StructuredActivationGraphPlan.TryCreateForBodyLocalSequenceBarrier(definition, out var plan, out var issues),
            string.Join("; ", issues.Select(issue => issue.Code)));
        var barrier = Assert.Single(plan!.SequenceBarriersByLoopNodeId).Value;
        Assert.Equal("sequence", barrier.SequenceNodeId);
        Assert.Equal("each", plan.OwningLoopByBodyNodeId["step-b"]);
        // Interim conservative bound: one pass plus one charge per Sequence output activation.
        Assert.Equal(3, plan.ChargedPassesPerIteration("each"));
        Assert.Equal(WorkflowLoopLimits.MaximumTotalBodyPassesPerRun / 3,
            plan.MaximumIterationsWithinPassBudget("each", WorkflowLoopLimits.MaximumTotalBodyPassesPerRun));
    }

    [Fact]
    public void Public_validation_still_rejects_the_shape_while_the_internal_arm_accepts_it()
    {
        var definition = BarrierWorkflow();

        Assert.Contains(WorkflowValidator.Validate(definition), issue =>
            issue.Code == "foreach.sequence.unsupported" && issue.Path == "nodes.each");
        Assert.Empty(WorkflowValidator.ValidateBodyLocalSequenceBarrierCandidate(definition));
    }

    [Theory]
    [InlineData(NodeTypes.Wait)]
    [InlineData(NodeTypes.Terminate)]
    public void The_internal_arm_rejects_a_route_node_outside_its_allowlist(string nodeType)
    {
        var definition = BarrierWorkflow(nodes: nodes =>
        {
            nodes["step-b"] = new(nodeType, "1.0.0");
            return nodes;
        });

        // A Wait passes the topology proof and is refused by the route allowlist; a Terminate has no continuation and
        // is already refused by the proof. Either way the arm rejects the candidate.
        var issues = WorkflowValidator.ValidateBodyLocalSequenceBarrierCandidate(definition);
        Assert.NotEmpty(issues);
        if (nodeType == NodeTypes.Wait)
            Assert.Contains(issues, issue => issue.Code == "foreach.sequence-barrier.route-node" && issue.Path == "nodes.step-b");
    }
}
