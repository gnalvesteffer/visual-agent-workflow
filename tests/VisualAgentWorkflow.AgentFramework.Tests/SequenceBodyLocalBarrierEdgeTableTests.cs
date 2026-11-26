using System.Collections.Immutable;
using VisualAgentWorkflow.Core;
using Xunit;

namespace VisualAgentWorkflow.AgentFramework.Tests;

/// <summary>
/// Decision 0070 slice (b) unblocker 4: the body-local barrier mode of the Sequence dispatch edge table. Each output
/// route's return reaches the Sequence collector, and only the collector continues to the For Each feedback pin.
/// </summary>
public sealed class SequenceBodyLocalBarrierEdgeTableTests
{
    private static ConnectionDefinition Execution(string from, string fromPin, string to, string toPin) =>
        new(new(from, fromPin), new(to, toPin), "execution");

    private static WorkflowDefinition BarrierWorkflow()
    {
        var nodes = ImmutableSortedDictionary.CreateBuilder<string, NodeDefinition>(StringComparer.Ordinal);
        nodes["input"] = new(NodeTypes.Input, "1.0.0");
        nodes["items"] = new(NodeTypes.ArrayLiteral, "1.0.0") { ArrayLiteral = new(WorkflowDataTypes.String, "[\"a\",\"b\"]") };
        nodes["each"] = new(NodeTypes.ForEach, "1.0.0") { ArrayElementType = WorkflowDataTypes.String };
        nodes["body"] = new(NodeTypes.LoopBody, "1.0.0");
        nodes["sequence"] = new(NodeTypes.Sequence, "1.0.0") { Sequence = new([new("first", "First"), new("second", "Second")]) };
        nodes["step-a"] = new(NodeTypes.SequenceReverse, "1.0.0");
        nodes["step-b"] = new(NodeTypes.SequenceUppercase, "1.0.0");
        nodes["output"] = new(NodeTypes.Output, "1.0.0");
        var data = ImmutableSortedDictionary<string, ConnectionDefinition>.Empty
            .Add("items-each", new(new("items", "out"), new("each", "array"), "data"))
            .Add("item-a", new(new("each", "item"), new("step-a", "in"), "data"))
            .Add("item-b", new(new("each", "item"), new("step-b", "in"), "data"));
        var execution = ImmutableSortedDictionary<string, ConnectionDefinition>.Empty
            .Add("start", Execution("input", "then", "each", "execute"))
            .Add("body-start", Execution("each", "body", "body", "execute"))
            .Add("body-sequence", Execution("body", "then", "sequence", "execute"))
            .Add("first-a", Execution("sequence", "then.first", "step-a", "execute"))
            .Add("second-b", Execution("sequence", "then.second", "step-b", "execute"))
            .Add("return-a", Execution("step-a", "then", "each", "feedback"))
            .Add("return-b", Execution("step-b", "then", "each", "feedback"))
            .Add("done", Execution("each", "completed", "output", "execute"));
        return new(WorkflowSchemaVersions.UnifiedWorkflowGraph, "for-each-sequence-barrier", "input", nodes.ToImmutable(), data)
        {
            ExecutionConnections = execution
        };
    }

    private static LoopFeedbackSequenceBarrierProof Barrier(WorkflowDefinition definition) =>
        LoopFeedbackTopologyProofAnalyzer.Analyze(definition, LoopFeedbackPinShape.ExplicitFeedbackInput,
            allowBodyLocalSequenceBarrier: true).Regions.Single().SequenceBarrier!;

    [Fact]
    public void Each_output_return_joins_at_the_collector_and_only_the_collector_reaches_the_feedback_pin()
    {
        var definition = BarrierWorkflow();
        var table = SequenceDispatchEdgeTable.CreateBodyLocal(definition, Barrier(definition));

        Assert.Equal([SequenceDispatchEdgeTable.CollectorId],
            table.TargetsFor("step-a", new WorkflowRunner.GraphMessage("a", "step-a", "then")));
        Assert.Equal([SequenceDispatchEdgeTable.CollectorId],
            table.TargetsFor("step-b", new WorkflowRunner.GraphMessage("b", "step-b", "then")));
        var feedback = Assert.Single(table.Edges, edge => edge.TargetId == "each");
        Assert.Equal(SequenceDispatchEdgeTable.CollectorId, feedback.SourceId);
        Assert.True(feedback.Matches(new WorkflowRunner.GraphMessage("", SequenceDispatchEdgeTable.CollectorId, "completed")));
        Assert.Equal(["each"], table.TargetsFor(SequenceDispatchEdgeTable.CollectorId,
            new WorkflowRunner.GraphMessage("", SequenceDispatchEdgeTable.CollectorId, "completed")));
        // The table covers only the Sequence's own routes: no edge leaves or enters the body outside them.
        Assert.DoesNotContain(table.Edges, edge => edge.SourceId is "input" or "each" or "body" or "sequence");
    }

    [Fact]
    public void The_body_local_mode_requires_a_barrier_from_the_same_definition()
    {
        var definition = BarrierWorkflow();
        var barrier = Barrier(definition) with { ReturnConnectionIds = ["return-a", "missing"] };

        Assert.Throws<ArgumentException>(() => SequenceDispatchEdgeTable.CreateBodyLocal(definition, barrier));
    }
}
