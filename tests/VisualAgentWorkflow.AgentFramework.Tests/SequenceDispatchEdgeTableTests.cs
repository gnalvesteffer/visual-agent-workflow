using System.Collections.Immutable;
using VisualAgentWorkflow.Core;
using Xunit;
using GraphMessage = VisualAgentWorkflow.AgentFramework.WorkflowRunner.GraphMessage;
using SequenceProductionOutput = VisualAgentWorkflow.AgentFramework.WorkflowRunner.SequenceProductionOutput;
using SequenceProductionPlan = VisualAgentWorkflow.AgentFramework.WorkflowRunner.SequenceProductionPlan;

namespace VisualAgentWorkflow.AgentFramework.Tests;

/// <summary>
/// The Sequence builder registers the table's edges and the admission resolver reads the same table
/// (Decision 0070 review, 2026-11-23), so these tests pin the table's receiver sets and its
/// one-edge-per-executor-pair rule.
/// </summary>
public sealed class SequenceDispatchEdgeTableTests
{
    private const string Collector = SequenceDispatchEdgeTable.CollectorId;

    [Fact]
    public void Call_termination_reaches_every_terminal_output_and_the_collector()
    {
        var table = SequenceDispatchEdgeTable.Create(CallDefinition(), CallPlan(), null);

        var targets = table.TargetsFor("call", Terminated("call"));

        Assert.Equal(["output", Collector], targets.OrderBy(target => target, StringComparer.Ordinal)
            .OrderBy(target => target == Collector).ToArray());
    }

    [Fact]
    public void An_output_branchs_internal_termination_handoff_matches_no_edge()
    {
        var table = SequenceDispatchEdgeTable.Create(CallDefinition(), CallPlan(), null);

        // The Output executor returns the call's termination message unchanged (its SourceNode stays "call").
        Assert.Empty(table.TargetsFor("output", Terminated("call")));
    }

    [Fact]
    public void A_normal_output_result_reaches_only_the_collector()
    {
        var table = SequenceDispatchEdgeTable.Create(CallDefinition(), CallPlan(), null);

        Assert.Equal([Collector], table.TargetsFor("output", new("done", "output", "out")));
    }

    [Fact]
    public void An_authored_branch_edge_delivers_to_its_target_only_for_the_matching_port()
    {
        var table = SequenceDispatchEdgeTable.Create(ToolThenOutputDefinition(), ToolPlan(), null);

        Assert.Equal(["output"], table.TargetsFor("tool", new("x", "tool", "then")));
        Assert.Empty(table.TargetsFor("tool", new("x", "tool", "other")));
        Assert.Empty(table.TargetsFor("other-executor", new("x", "tool", "then")));
    }

    [Fact]
    public void Legacy_and_direct_output_collection_merge_into_one_edge_so_delivery_is_not_doubled()
    {
        var definition = ToolThenOutputDefinition() with { ExecutionConnections = null };
        var plan = ToolPlan() with { DirectOutputCollection = true, BypassRevisionExecutionEdges = true };
        var table = SequenceDispatchEdgeTable.Create(definition, plan, null);

        var message = new GraphMessage("x", "tool", "out", SequenceLineage: Lineage("first", 0));

        Assert.Single(table.Edges, edge => edge.SourceId == "tool" && edge.TargetId == Collector);
        Assert.Equal([Collector], table.TargetsFor("tool", message));
    }

    [Fact]
    public void A_bypassing_plan_ignores_authored_execution_edges()
    {
        var plan = ToolPlan() with { BypassRevisionExecutionEdges = true, DirectOutputCollection = true };
        var table = SequenceDispatchEdgeTable.Create(ToolThenOutputDefinition(), plan, null);

        var message = new GraphMessage("x", "tool", "then", SequenceLineage: Lineage("first", 0));

        Assert.Equal([Collector], table.TargetsFor("tool", message));
        Assert.DoesNotContain(table.Edges, edge => edge.TargetId == "output");
    }

    [Fact]
    public void No_two_edges_share_a_source_and_target_pair_in_any_shape()
    {
        foreach (var (definition, plan) in new[]
                 {
                     (CallDefinition(), CallPlan()),
                     (ToolThenOutputDefinition(), ToolPlan()),
                     (ToolThenOutputDefinition() with { ExecutionConnections = null },
                         ToolPlan() with { DirectOutputCollection = true, BypassRevisionExecutionEdges = true }),
                     (ToolThenOutputDefinition() with { ExecutionConnections = null }, ToolPlan())
                 })
        {
            var table = SequenceDispatchEdgeTable.Create(definition, plan, null);
            Assert.Equal(table.Edges.Count, table.Edges.Select(edge => (edge.SourceId, edge.TargetId)).Distinct().Count());
        }
    }

    [Fact]
    public void Break_request_from_an_output_branch_reaches_the_collector_only_for_a_declared_break_node()
    {
        var breakRequest = new WorkflowRunner.StructuredActivationBreakRequest("brk", Guid.NewGuid(), "loop-scope");
        var message = new GraphMessage("x", "brk", "break", ActivationBreakRequest: breakRequest);
        var declared = SequenceDispatchEdgeTable.Create(ToolThenOutputDefinition(), ToolPlan(),
            new HashSet<string>(StringComparer.Ordinal) { "brk" });
        var undeclared = SequenceDispatchEdgeTable.Create(ToolThenOutputDefinition(), ToolPlan(),
            new HashSet<string>(StringComparer.Ordinal));

        Assert.Equal([Collector], declared.TargetsFor("output", message));
        Assert.Empty(undeclared.TargetsFor("output", message));
    }

    private static GraphMessage Terminated(string source) => new("stop", source, "terminated", TerminationSignal: true);

    private static WorkflowRunner.SequenceMessageLineage Lineage(string outputId, int ordinal) =>
        new(Guid.NewGuid(), "sequence", outputId, ordinal, Guid.NewGuid(), "sequence-" + outputId);

    private static SequenceProductionPlan CallPlan() => new("sequence",
        [new SequenceProductionOutput("call", 0, "call", "sequence", "then.call", "sequence-call")],
        null, SequenceTerminalState.Completed);

    private static SequenceProductionPlan ToolPlan() => new("sequence",
        [new SequenceProductionOutput("first", 0, "tool", "sequence", "then.first", "sequence-first")],
        null, SequenceTerminalState.Completed);

    private static WorkflowDefinition CallDefinition() => Definition(
        new Dictionary<string, NodeDefinition>
        {
            ["input"] = new(NodeTypes.Input, "1.0.0"),
            ["sequence"] = new(NodeTypes.Sequence, "1.0.0") { Sequence = new([new("call", "Call")]) },
            ["call"] = new(NodeTypes.WorkflowCall, "1.0.0"),
            ["output"] = new(NodeTypes.Output, "1.0.0")
        },
        new Dictionary<string, ConnectionDefinition>
        {
            ["input-sequence"] = new(new("input", "then"), new("sequence", "execute"), "execution"),
            ["sequence-call"] = new(new("sequence", "then.call"), new("call", "execute"), "execution"),
            ["call-output"] = new(new("call", "out"), new("output", "execute"), "execution")
        });

    private static WorkflowDefinition ToolThenOutputDefinition() => Definition(
        new Dictionary<string, NodeDefinition>
        {
            ["input"] = new(NodeTypes.Input, "1.0.0"),
            ["sequence"] = new(NodeTypes.Sequence, "1.0.0") { Sequence = new([new("first", "First")]) },
            ["tool"] = new(NodeTypes.McpTool, "1.0.0", new ToolBindingRequest("demo.tool", new string('a', 64)))
            {
                McpArgumentsJson = "{}"
            },
            ["output"] = new(NodeTypes.Output, "1.0.0")
        },
        new Dictionary<string, ConnectionDefinition>
        {
            ["input-sequence"] = new(new("input", "then"), new("sequence", "execute"), "execution"),
            ["sequence-tool"] = new(new("sequence", "then.first"), new("tool", "execute"), "execution"),
            ["tool-output"] = new(new("tool", "then"), new("output", "execute"), "execution")
        });

    private static WorkflowDefinition Definition(Dictionary<string, NodeDefinition> nodes,
        Dictionary<string, ConnectionDefinition> execution) => new(WorkflowSchemaVersions.UnifiedWorkflowGraph,
            "edge-table", "input", nodes.ToImmutableSortedDictionary(),
            ImmutableSortedDictionary<string, ConnectionDefinition>.Empty)
    {
        ExecutionConnections = execution.ToImmutableSortedDictionary()
    };
}
