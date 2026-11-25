using System.Collections.Immutable;
using VisualAgentWorkflow.Core;
using Xunit;

namespace VisualAgentWorkflow.Core.Tests;

/// <summary>
/// Typed For Each node contract (Decision 0070, 2026-11-24): a dedicated iterator over a bounded <c>array&lt;T&gt;</c>
/// with typed item and zero-based integer index outputs and explicit body/completed paths. It reuses the Loop's
/// pin roles and body anchor; validation here is exercised before any runtime exists.
/// </summary>
public sealed class ForEachNodeTests
{
    private static NodeDefinition ForEach(string? elementType = WorkflowDataTypes.String) =>
        new(NodeTypes.ForEach, "1.0.0") { ArrayElementType = elementType };

    private static ConnectionDefinition Execution(string from, string fromPin, string to, string toPin) =>
        new(new(from, fromPin), new(to, toPin), "execution");

    private static ConnectionDefinition Data(string from, string fromPin, string to, string toPin) =>
        new(new(from, fromPin), new(to, toPin), "data");

    /// <summary>A valid workflow: a string array drives a body that reverses each item and returns to the iterator.</summary>
    private static WorkflowDefinition Workflow(Func<ImmutableSortedDictionary<string, NodeDefinition>.Builder, ImmutableSortedDictionary<string, NodeDefinition>.Builder>? nodes = null,
        Func<ImmutableSortedDictionary<string, ConnectionDefinition>, ImmutableSortedDictionary<string, ConnectionDefinition>>? data = null,
        Func<ImmutableSortedDictionary<string, ConnectionDefinition>, ImmutableSortedDictionary<string, ConnectionDefinition>>? execution = null)
    {
        var nodeBuilder = ImmutableSortedDictionary.CreateBuilder<string, NodeDefinition>(StringComparer.Ordinal);
        nodeBuilder["input"] = new(NodeTypes.Input, "1.0.0");
        nodeBuilder["items"] = new(NodeTypes.ArrayLiteral, "1.0.0") { ArrayLiteral = new(WorkflowDataTypes.String, "[\"a\",\"b\"]") };
        nodeBuilder["each"] = ForEach();
        nodeBuilder["body"] = new(NodeTypes.LoopBody, "1.0.0");
        nodeBuilder["step"] = new(NodeTypes.SequenceReverse, "1.0.0");
        nodeBuilder["output"] = new(NodeTypes.Output, "1.0.0");
        nodes?.Invoke(nodeBuilder);
        var dataEdges = ImmutableSortedDictionary<string, ConnectionDefinition>.Empty
            .Add("items-each", Data("items", "out", "each", "array"))
            .Add("item-step", Data("each", "item", "step", "in"))
            .Add("result", Data("step", "out", "output", "in"));
        var executionEdges = ImmutableSortedDictionary<string, ConnectionDefinition>.Empty
            .Add("start", Execution("input", "then", "each", "execute"))
            .Add("body-start", Execution("each", "body", "body", "execute"))
            .Add("body-step", Execution("body", "then", "step", "execute"))
            .Add("return", Execution("step", "then", "each", "feedback"))
            .Add("done", Execution("each", "completed", "output", "execute"));
        return new(WorkflowSchemaVersions.UnifiedWorkflowGraph, "for-each-workflow", "input", nodeBuilder.ToImmutable(),
            data?.Invoke(dataEdges) ?? dataEdges)
        {
            ExecutionConnections = execution?.Invoke(executionEdges) ?? executionEdges
        };
    }

    [Fact]
    public void The_contract_catalog_declares_a_dynamic_element_typed_flow_node()
    {
        Assert.True(WorkflowNodeContractCatalog.TryGet(NodeTypes.ForEach, out var contract));
        Assert.Equal("1.0.0", contract.TypeVersion);
        Assert.True(contract.HasDynamicPins);
        Assert.Equal(WorkflowDynamicPinIdentityKind.TypedArrayElementType, contract.DynamicPinIdentity);
    }

    [Theory]
    [InlineData(WorkflowDataTypes.String)]
    [InlineData(WorkflowDataTypes.Integer)]
    [InlineData(WorkflowDataTypes.Boolean)]
    [InlineData(WorkflowDataTypes.Json)]
    public void The_descriptor_types_array_item_and_index_pins_from_the_element_type(string elementType)
    {
        Assert.True(WorkflowNodeDescriptorResolver.TryResolve(ForEach(elementType), null, null, out var descriptor));

        PortOf(descriptor, "array", WorkflowPortDirection.Input, WorkflowPortKind.Data, out var array);
        PortOf(descriptor, "item", WorkflowPortDirection.Output, WorkflowPortKind.Data, out var item);
        PortOf(descriptor, "index", WorkflowPortDirection.Output, WorkflowPortKind.Data, out var index);
        Assert.Equal(WorkflowDataTypes.ArrayOf(elementType), array.DataType);
        Assert.Equal(elementType, item.DataType);
        Assert.Equal(WorkflowDataTypes.Integer, index.DataType);
        Assert.True(array.Required);
        Assert.Equal(WorkflowPortCardinality.ExactlyOne, array.Cardinality);
    }

    [Fact]
    public void The_descriptor_reuses_the_loop_execution_roles()
    {
        Assert.True(WorkflowNodeDescriptorResolver.TryResolve(ForEach(), null, null, out var descriptor));

        PortOf(descriptor, "execute", WorkflowPortDirection.Input, WorkflowPortKind.Execution, out var execute);
        PortOf(descriptor, "feedback", WorkflowPortDirection.Input, WorkflowPortKind.Execution, out var feedback);
        PortOf(descriptor, "body", WorkflowPortDirection.Output, WorkflowPortKind.Execution, out var body);
        PortOf(descriptor, "completed", WorkflowPortDirection.Output, WorkflowPortKind.Execution, out var completed);
        Assert.Equal(WorkflowExecutionPinRole.LoopInitialInput, execute.ExecutionRole);
        Assert.Equal(WorkflowExecutionPinRole.LoopFeedbackInput, feedback.ExecutionRole);
        Assert.Equal(WorkflowExecutionPinRole.LoopBodyOutput, body.ExecutionRole);
        Assert.Equal(WorkflowExecutionPinRole.LoopDoneOutput, completed.ExecutionRole);
    }

    [Fact]
    public void An_unsupported_element_type_has_no_descriptor()
    {
        Assert.False(WorkflowNodeDescriptorResolver.TryResolve(ForEach("banana"), null, null, out _));
    }

    [Fact]
    public void A_valid_for_each_workflow_validates_without_issues() =>
        Assert.Empty(WorkflowValidator.Validate(Workflow()));

    [Fact]
    public void A_for_each_whose_body_does_not_return_to_its_feedback_pin_is_rejected()
    {
        var definition = Workflow(execution: edges => edges.Remove("return"));

        Assert.Contains(WorkflowValidator.Validate(definition), issue => issue.Code.StartsWith("loop.", StringComparison.Ordinal));
    }

    [Fact]
    public void A_for_each_array_input_must_be_connected_and_match_its_element_type()
    {
        var disconnected = Workflow(data: edges => edges.Remove("items-each"));
        Assert.Contains(WorkflowValidator.Validate(disconnected), issue => issue.Path.Contains("each", StringComparison.Ordinal));

        var wrongElement = Workflow(nodes: nodes =>
        {
            nodes["items"] = new(NodeTypes.ArrayLiteral, "1.0.0") { ArrayLiteral = new(WorkflowDataTypes.Integer, "[1,2]") };
            return nodes;
        });
        Assert.NotEmpty(WorkflowValidator.Validate(wrongElement));
    }

    [Fact]
    public void A_for_each_with_an_invalid_element_type_is_rejected()
    {
        var definition = Workflow(nodes: nodes =>
        {
            nodes["each"] = ForEach("banana");
            return nodes;
        });

        Assert.NotEmpty(WorkflowValidator.Validate(definition));
    }

    [Fact]
    public void A_for_each_is_not_accepted_in_a_legacy_schema()
    {
        var definition = Workflow() with { SchemaVersion = WorkflowSchemaVersions.SequenceGraph };

        Assert.NotEmpty(WorkflowValidator.Validate(definition));
    }

    [Fact]
    public void A_for_each_workflow_round_trips_through_json_with_a_stable_hash()
    {
        var definition = Workflow();

        var json = WorkflowJson.Serialize(definition);
        var parsed = WorkflowJson.Deserialize(json);

        Assert.Equal(json, WorkflowJson.Serialize(parsed));
        Assert.Equal(WorkflowRevision.Create(definition, "test").ContentHash, WorkflowRevision.Create(parsed, "test").ContentHash);
        Assert.Equal(WorkflowDataTypes.String, parsed.Nodes["each"].ArrayElementType);
    }

    [Fact]
    public void An_unknown_node_type_fails_closed_as_an_older_reader_would_see_a_for_each()
    {
        var definition = Workflow(nodes: nodes =>
        {
            nodes["each"] = new("core.for-each.from-the-future", "1.0.0") { ArrayElementType = WorkflowDataTypes.String };
            return nodes;
        });

        Assert.Contains(WorkflowValidator.Validate(definition), issue => issue.Code == "node.unsupported");
    }

    [Fact]
    public void A_for_each_inside_a_sequence_path_is_rejected_in_the_first_slice()
    {
        var definition = Workflow(nodes: nodes =>
        {
            nodes["sequence"] = new(NodeTypes.Sequence, "1.0.0")
            {
                Sequence = new([new("first", "First"), new("second", "Second")])
            };
            return nodes;
        }, execution: edges => edges.Remove("start")
            .Add("start", Execution("input", "then", "sequence", "execute"))
            .Add("sequence-each", Execution("sequence", "then.first", "each", "execute"))
            .Add("sequence-output", Execution("sequence", "then.second", "output", "execute")));

        Assert.NotEmpty(WorkflowValidator.Validate(definition));
    }

    [Fact]
    public void Node_stepping_is_offered_for_a_sequential_for_each()
    {
        Assert.True(WorkflowDebugging.SupportsNodeStepping(Workflow()));
    }

    [Theory]
    [InlineData(NodeTypes.Wait)]
    [InlineData(NodeTypes.Sequence)]
    [InlineData(NodeTypes.ParallelFork)]
    [InlineData(NodeTypes.Loop)]
    [InlineData(NodeTypes.Merge)]
    [InlineData(NodeTypes.WorkflowCall)]
    public void Node_stepping_stays_unsupported_for_a_for_each_workflow_with_a_still_unsupported_node(string type)
    {
        var definition = Workflow(nodes: builder => { builder["extra"] = new(type, "1.0.0"); return builder; });
        Assert.False(WorkflowDebugging.SupportsNodeStepping(definition));
    }

    [Fact]
    public void Changing_the_element_type_of_a_for_each_is_an_editing_operation_that_revalidates()
    {
        var definition = Workflow();

        // The connected array<string> source no longer matches array<integer>, so the edit is rejected, not silently kept.
        Assert.ThrowsAny<Exception>(() => GraphEditing.ReplaceNode(definition, "each",
            definition.Nodes["each"] with { ArrayElementType = WorkflowDataTypes.Integer }));
        var same = GraphEditing.ReplaceNode(definition, "each", definition.Nodes["each"] with { ArrayElementType = WorkflowDataTypes.String });
        Assert.Equal(WorkflowDataTypes.String, same.Nodes["each"].ArrayElementType);
    }

    /// <summary>each.body → anchor → condition; true → break, false → step → feedback.</summary>
    private static WorkflowDefinition BreakWorkflow(Func<ImmutableSortedDictionary<string, NodeDefinition>.Builder, ImmutableSortedDictionary<string, NodeDefinition>.Builder>? nodes = null,
        Func<ImmutableSortedDictionary<string, ConnectionDefinition>, ImmutableSortedDictionary<string, ConnectionDefinition>>? execution = null) =>
        Workflow(nodes: builder =>
        {
            builder["cond"] = new(NodeTypes.Condition, "1.0.0") { Condition = new("contains", "stop") };
            builder["break"] = new(NodeTypes.Break, "1.0.0");
            nodes?.Invoke(builder);
            return builder;
        }, data: edges => edges.Add("item-cond", Data("each", "item", "cond", "in")),
        execution: edges =>
        {
            var wired = edges.Remove("body-step")
                .Add("body-cond", Execution("body", "then", "cond", "execute"))
                .Add("cond-break", Execution("cond", "true", "break", "execute"))
                .Add("cond-step", Execution("cond", "false", "step", "execute"));
            return execution?.Invoke(wired) ?? wired;
        });

    [Fact]
    public void A_break_on_one_branch_of_a_linear_for_each_body_validates()
    {
        Assert.Empty(WorkflowValidator.Validate(BreakWorkflow()));
    }

    [Fact]
    public void A_break_in_a_workflow_with_no_for_each_is_rejected()
    {
        var nodes = ImmutableSortedDictionary<string, NodeDefinition>.Empty
            .Add("input", new(NodeTypes.Input, "1.0.0"))
            .Add("break", new(NodeTypes.Break, "1.0.0"))
            .Add("output", new(NodeTypes.Output, "1.0.0"));
        var definition = new WorkflowDefinition(WorkflowSchemaVersions.UnifiedWorkflowGraph, "lonely-break", "input", nodes,
            ImmutableSortedDictionary<string, ConnectionDefinition>.Empty.Add("data", Data("input", "out", "output", "in")))
        {
            ExecutionConnections = ImmutableSortedDictionary<string, ConnectionDefinition>.Empty
                .Add("start", Execution("input", "then", "break", "execute"))
        };

        Assert.NotEmpty(WorkflowValidator.Validate(definition));
    }

    [Fact]
    public void A_break_outside_any_for_each_body_is_rejected()
    {
        var definition = BreakWorkflow(execution: edges => edges.Remove("cond-break")
            .Add("stray", Execution("input", "then", "break", "execute")));

        Assert.NotEmpty(WorkflowValidator.Validate(definition));
    }

    private static ImmutableArray<ParallelBranchDescriptor> ForkBranches() =>
        ImmutableArray.Create(new ParallelBranchDescriptor("a", "A", "anchor-a"),
            new ParallelBranchDescriptor("b", "B", "anchor-b"));

    private static ImmutableSortedDictionary<string, NodeDefinition>.Builder AddForkJoinNodes(
        ImmutableSortedDictionary<string, NodeDefinition>.Builder nodes)
    {
        nodes["fork"] = new(NodeTypes.ParallelFork, "1.0.0") { ParallelFork = new(ForkBranches()) };
        nodes["join"] = new(NodeTypes.ParallelJoin, "1.0.0") { ParallelJoin = new("fork", ["a", "b"]) };
        nodes["anchor-a"] = new(NodeTypes.ParallelBranch, "1.0.0") { ParallelBranch = new("fork", "a") };
        nodes["anchor-b"] = new(NodeTypes.ParallelBranch, "1.0.0") { ParallelBranch = new("fork", "b") };
        nodes["agent-a"] = new(NodeTypes.Agent, "1.0.0", Agent: new("fixture", null, "A", []));
        nodes["agent-b"] = new(NodeTypes.Agent, "1.0.0", Agent: new("fixture", null, "B", []));
        return nodes;
    }

    private static ImmutableSortedDictionary<string, ConnectionDefinition> ForkJoinBodyEdges(
        ImmutableSortedDictionary<string, ConnectionDefinition> edges) => edges
            .Add("fork-a", Execution("fork", ParallelBranchLimits.PinId("a"), "anchor-a", "execute"))
            .Add("fork-b", Execution("fork", ParallelBranchLimits.PinId("b"), "anchor-b", "execute"))
            .Add("anchor-a-agent", Execution("anchor-a", "then", "agent-a", "execute"))
            .Add("agent-a-join", Execution("agent-a", "out", "join", ParallelBranchLimits.PinId("a")));

    [Fact]
    public void A_break_after_a_fork_join_in_the_body_validates()
    {
        var definition = BreakWorkflow(nodes: AddForkJoinNodes,
            execution: edges => ForkJoinBodyEdges(edges.Remove("body-cond"))
                .Add("body-fork", Execution("body", "then", "fork", "execute"))
                .Add("anchor-b-agent", Execution("anchor-b", "then", "agent-b", "execute"))
                .Add("agent-b-join", Execution("agent-b", "out", "join", ParallelBranchLimits.PinId("b")))
                .Add("join-cond", Execution("join", "then", "cond", "execute")));

        Assert.Empty(WorkflowValidator.Validate(definition));
    }

    /// <summary>body → fork; branch b decides with a Boolean Branch whose true outcome is a Break.</summary>
    private static WorkflowDefinition BreakInForkBranch(Func<ImmutableSortedDictionary<string, ConnectionDefinition>,
        ImmutableSortedDictionary<string, ConnectionDefinition>>? execution = null) =>
        Workflow(nodes: nodes =>
        {
            AddForkJoinNodes(nodes);
            nodes["route"] = new(NodeTypes.BooleanBranch, "1.0.0");
            nodes["is-b"] = new(NodeTypes.StringEquals, "1.0.0");
            nodes["b-text"] = new(NodeTypes.StringConstant, "1.0.0") { StringConstant = new("b") };
            nodes["break"] = new(NodeTypes.Break, "1.0.0");
            return nodes;
        }, data: edges => edges
            .Add("item-eq", Data("each", "item", "is-b", "left"))
            .Add("text-eq", Data("b-text", "out", "is-b", "right"))
            .Add("eq-route", Data("is-b", "out", "route", "condition")),
        execution: edges =>
        {
            var wired = ForkJoinBodyEdges(edges.Remove("body-step").Remove("return"))
                .Add("body-fork", Execution("body", "then", "fork", "execute"))
                .Add("anchor-b-route", Execution("anchor-b", "then", "route", "execute"))
                .Add("route-break", Execution("route", "true", "break", "execute"))
                .Add("route-agent-b", Execution("route", "false", "agent-b", "execute"))
                .Add("agent-b-join", Execution("agent-b", "out", "join", ParallelBranchLimits.PinId("b")))
                .Add("join-step", Execution("join", "then", "step", "execute"))
                .Add("return", Execution("step", "then", "each", "feedback"));
            return execution?.Invoke(wired) ?? wired;
        });

    [Fact]
    public void A_break_on_a_boolean_branch_outcome_inside_a_fork_branch_validates()
    {
        Assert.Empty(WorkflowValidator.Validate(BreakInForkBranch()));
    }

    [Fact]
    public void A_fork_branch_break_that_is_not_a_boolean_branch_outcome_is_rejected()
    {
        var definition = BreakInForkBranch(edges => edges.Remove("anchor-b-route").Remove("route-break")
            .Remove("route-agent-b").Remove("agent-b-join")
            .Add("anchor-b-agent", Execution("anchor-b", "then", "agent-b", "execute"))
            .Add("agent-b-break", Execution("agent-b", "out", "break", "execute")));

        Assert.Contains(WorkflowValidator.Validate(definition), issue => issue.Code == "parallel.branch.node");
    }

    [Fact]
    public void A_break_after_a_nested_for_each_in_the_body_is_still_rejected_as_nonlinear()
    {
        var definition = BreakWorkflow(nodes: nodes =>
        {
            nodes["inner"] = ForEach();
            nodes["inner-items"] = new(NodeTypes.ArrayLiteral, "1.0.0") { ArrayLiteral = new(WorkflowDataTypes.String, "[\"x\"]") };
            nodes["inner-body"] = new(NodeTypes.LoopBody, "1.0.0");
            nodes["inner-step"] = new(NodeTypes.SequenceReverse, "1.0.0");
            return nodes;
        }, execution: edges => edges.Remove("body-cond")
            .Add("body-inner", Execution("body", "then", "inner", "execute"))
            .Add("inner-body-start", Execution("inner", "body", "inner-body", "execute"))
            .Add("inner-body-step", Execution("inner-body", "then", "inner-step", "execute"))
            .Add("inner-return", Execution("inner-step", "then", "inner", "feedback"))
            .Add("inner-cond", Execution("inner", "completed", "cond", "execute")));
        definition = definition with
        {
            Connections = definition.Connections
                .Add("inner-array", Data("inner-items", "out", "inner", "array"))
                .Add("inner-item-step", Data("inner", "item", "inner-step", "in"))
        };

        Assert.Contains(WorkflowValidator.Validate(definition), issue => issue.Code == "foreach.break.nonlinear");
    }

    [Fact]
    public void Two_breaks_in_one_for_each_body_are_rejected()
    {
        var definition = BreakWorkflow(nodes: nodes =>
        {
            nodes["cond2"] = new(NodeTypes.Condition, "1.0.0") { Condition = new("contains", "also") };
            nodes["break2"] = new(NodeTypes.Break, "1.0.0");
            return nodes;
        }, execution: edges => edges.Remove("cond-step")
            .Add("cond-cond2", Execution("cond", "false", "cond2", "execute"))
            .Add("cond2-break", Execution("cond2", "true", "break2", "execute"))
            .Add("cond2-step", Execution("cond2", "false", "step", "execute")));
        definition = definition with { Connections = definition.Connections.Add("item-cond2", Data("each", "item", "cond2", "in")) };

        Assert.Contains(WorkflowValidator.Validate(definition), issue => issue.Code == "foreach.break.multiple");
    }

    [Fact]
    public void An_outer_and_an_inner_for_each_may_each_own_one_break()
    {
        var definition = BreakWorkflow(nodes: nodes =>
        {
            nodes["inner"] = ForEach();
            nodes["inner-items"] = new(NodeTypes.ArrayLiteral, "1.0.0") { ArrayLiteral = new(WorkflowDataTypes.String, "[\"x\"]") };
            nodes["inner-body"] = new(NodeTypes.LoopBody, "1.0.0");
            nodes["cond2"] = new(NodeTypes.Condition, "1.0.0") { Condition = new("contains", "also") };
            nodes["break2"] = new(NodeTypes.Break, "1.0.0");
            return nodes;
        }, execution: edges => edges.Remove("cond-step").Remove("return")
            .Add("cond-inner", Execution("cond", "false", "inner", "execute"))
            .Add("inner-body-start", Execution("inner", "body", "inner-body", "execute"))
            .Add("inner-body-cond2", Execution("inner-body", "then", "cond2", "execute"))
            .Add("cond2-break", Execution("cond2", "true", "break2", "execute"))
            .Add("cond2-step", Execution("cond2", "false", "step", "execute"))
            .Add("inner-return", Execution("step", "then", "inner", "feedback"))
            .Add("outer-return", Execution("inner", "completed", "each", "feedback")));
        definition = definition with
        {
            Connections = definition.Connections
                .Add("inner-array", Data("inner-items", "out", "inner", "array"))
                .Add("inner-item-cond2", Data("inner", "item", "cond2", "in"))
        };

        Assert.Empty(WorkflowValidator.Validate(definition));
    }

    [Fact]
    public void Node_stepping_is_supported_for_a_for_each_body_with_a_break()
    {
        Assert.True(WorkflowDebugging.SupportsNodeStepping(BreakWorkflow()));
    }

    private static void PortOf(WorkflowNodeDescriptor descriptor, string id, WorkflowPortDirection direction,
        WorkflowPortKind kind, out WorkflowPortDescriptor port) =>
        port = Assert.Single(descriptor.Ports, candidate => candidate.Id == id && candidate.Direction == direction &&
            candidate.Kind == kind);
}
