using System.Collections.Immutable;
using VisualAgentWorkflow.Core;
using Xunit;

namespace VisualAgentWorkflow.Core.Tests;

/// <summary>
/// Wait first slice (Decision 0081, 2026-11-24): a duration-only, bounded, non-resumable node. Core owns the node
/// contract, the validator rules (schema, bound, top-level placement), the host-bound admission check, and the
/// stepping/work-evidence consequences. Runtime behavior is covered in the AgentFramework and Hosting suites.
/// </summary>
public sealed class WaitNodeTests
{
    private static ConnectionDefinition Execution(string from, string fromPin, string to, string toPin) =>
        new(new(from, fromPin), new(to, toPin), "execution");

    private static NodeDefinition Wait(long milliseconds = 1_000) =>
        new(NodeTypes.Wait, "1.0.0") { Wait = new(milliseconds) };

    /// <summary>Input -then-> wait -then-> output, with the value carried by a data edge.</summary>
    private static WorkflowDefinition TopLevel(long milliseconds = 1_000, int waits = 1)
    {
        var nodes = ImmutableSortedDictionary.CreateBuilder<string, NodeDefinition>(StringComparer.Ordinal);
        nodes["input"] = new(NodeTypes.Input, "1.0.0");
        nodes["output"] = new(NodeTypes.Output, "1.0.0");
        var execution = ImmutableSortedDictionary.CreateBuilder<string, ConnectionDefinition>(StringComparer.Ordinal);
        var previous = ("input", "then");
        for (var index = 1; index <= waits; index++)
        {
            var id = $"wait{index}";
            nodes[id] = Wait(milliseconds);
            execution[$"to-{id}"] = Execution(previous.Item1, previous.Item2, id, "execute");
            previous = (id, "then");
        }
        execution["finish"] = Execution(previous.Item1, previous.Item2, "output", "execute");
        return new(WorkflowSchemaVersions.UnifiedWorkflowGraph, "wait-workflow", "input", nodes.ToImmutable(),
            ImmutableSortedDictionary<string, ConnectionDefinition>.Empty.Add("value", new(new("input", "out"), new("output", "in"), "data")))
        {
            ExecutionConnections = execution.ToImmutable()
        };
    }

    [Fact]
    public void The_contract_declares_a_host_owned_wait_with_one_execution_input_and_a_then_output()
    {
        Assert.True(WorkflowNodeContractCatalog.TryGet(NodeTypes.Wait, out var contract));
        Assert.Equal("core.wait", NodeTypes.Wait);
        Assert.True(contract.RequiresHostCapability);
        Assert.Equal(WorkflowNodeStateKind.HostOwned, contract.State);
        Assert.Equal(WorkflowNodeFailureKind.WaitsForHost, contract.Failure);
        Assert.Equal(WorkflowNodeEvidenceKind.Wait, contract.Evidence);
        Assert.True(contract.Effects.HasFlag(WorkflowNodeEffectFlags.HostInteraction));
        Assert.True(contract.Authoring.HasFlag(WorkflowNodeAuthoringFlags.Configurable));
        Assert.Contains(contract.Limits, limit => limit.Id == "duration-ms" && limit.Maximum == WorkflowWaitLimits.MaximumDurationMilliseconds);
        Assert.Empty(WorkflowNodeContractConformance.ValidateCatalog());

        Assert.True(WorkflowNodeDescriptorRegistry.TryGet(NodeTypes.Wait, WorkflowNodeDescriptorRegistry.CurrentVersion, out var descriptor));
        Assert.Equal("wait", descriptor.ConfigurationKind);
        var node = Wait();
        Assert.Equal(["execute"], WorkflowPorts.ExplicitExecutionInputs(node).ToArray());
        Assert.Equal(["then"], WorkflowPorts.ExplicitExecutionOutputs(node).ToArray());
        Assert.Empty(WorkflowPorts.ExplicitDataInputs(node));
        Assert.Empty(WorkflowPorts.ExplicitDataOutputs(node));
    }

    [Fact]
    public void Conformance_reports_drift_in_the_wait_lifecycle_contract()
    {
        var drifted = WorkflowNodeContractCatalog.Get(NodeTypes.Wait) with { State = WorkflowNodeStateKind.Stateless };
        Assert.Contains(WorkflowNodeContractConformance.Validate(drifted), issue => issue.Code == "metadata.wait-state");
    }

    [Fact]
    public void A_top_level_wait_in_a_unified_graph_is_valid_and_round_trips_through_json()
    {
        var definition = TopLevel(2_500);

        Assert.Empty(WorkflowValidator.Validate(definition));
        var restored = WorkflowJson.Deserialize(WorkflowJson.Serialize(definition));
        Assert.Equal(2_500, restored.Nodes["wait1"].Wait!.DurationMilliseconds);
        Assert.Equal(WorkflowJson.ComputeHash(definition), WorkflowJson.ComputeHash(restored));
        Assert.Matches("\"durationMilliseconds\":\\s*2500", WorkflowJson.Serialize(definition));
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(-5L)]
    [InlineData(WorkflowWaitLimits.MaximumDurationMilliseconds + 1)]
    public void A_duration_outside_one_millisecond_to_twenty_four_hours_is_rejected_never_clamped(long milliseconds)
    {
        var issues = WorkflowValidator.Validate(TopLevel(milliseconds));

        Assert.Contains(issues, issue => issue.Code == "wait.duration" && issue.Path == "nodes.wait1.wait.durationMilliseconds");
    }

    [Theory]
    [InlineData(1L)]
    [InlineData(WorkflowWaitLimits.MaximumDurationMilliseconds)]
    public void The_duration_bounds_are_inclusive(long milliseconds) =>
        Assert.Empty(WorkflowValidator.Validate(TopLevel(milliseconds)));

    [Fact]
    public void A_wait_without_configuration_is_rejected()
    {
        var definition = TopLevel();
        definition = definition with { Nodes = definition.Nodes.SetItem("wait1", new(NodeTypes.Wait, "1.0.0")) };

        Assert.Contains(WorkflowValidator.Validate(definition), issue => issue.Code == "wait.duration");
    }

    [Fact]
    public void Wait_configuration_on_another_node_type_is_rejected()
    {
        var definition = TopLevel();
        definition = definition with { Nodes = definition.Nodes.SetItem("output", definition.Nodes["output"] with { Wait = new(1) }) };

        Assert.Contains(WorkflowValidator.Validate(definition), issue => issue.Code == "wait.unexpected");
    }

    [Fact]
    public void Historical_schemas_reject_a_wait_node()
    {
        var definition = TopLevel() with { SchemaVersion = WorkflowSchemaVersions.IntegerStringConversionGraph };

        Assert.Contains(WorkflowValidator.Validate(definition), issue => issue.Code == "wait.schema");
    }

    [Fact]
    public void The_host_check_rejects_a_per_wait_duration_above_the_host_maximum_without_clamping()
    {
        var options = new WorkflowWaitOptions(TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(10), 2);
        var definition = TopLevel(61_000);

        var issues = options.Check(definition);

        var issue = Assert.Single(issues, candidate => candidate.Code == "wait.bound");
        Assert.Equal("nodes.wait1.wait.durationMilliseconds", issue.Path);
        Assert.Equal(61_000, definition.Nodes["wait1"].Wait!.DurationMilliseconds);
        Assert.Empty(options.Check(TopLevel(60_000)));
    }

    [Fact]
    public void The_host_check_rejects_a_cumulative_total_above_the_run_budget()
    {
        var options = new WorkflowWaitOptions(TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(2), 2);

        Assert.Empty(options.Check(TopLevel(60_000, waits: 2)));
        Assert.Contains(options.Check(TopLevel(60_000, waits: 3)), issue => issue.Code == "wait.budget");
    }

    [Fact]
    public void Default_host_bounds_are_small_and_below_the_admission_limit()
    {
        var options = WorkflowWaitOptions.Default;

        Assert.Equal(TimeSpan.FromHours(1), options.MaximumWait);
        Assert.Equal(TimeSpan.FromHours(2), options.CumulativeBudget);
        Assert.True(options.MaximumWaitingRuns is >= 1 and <= 8, "The waiting-run cap must stay well below the 32-run admission limit.");
        Assert.Empty(options.Check(TopLevel(3_600_000)));
        Assert.NotEmpty(options.Check(TopLevel(3_600_001)));
    }

    [Theory]
    [InlineData(0, 60, 1, "maximumWait")]
    [InlineData(1441, 1441, 1, "maximumWait")]
    [InlineData(10, 5, 1, "cumulativeBudget")]
    [InlineData(10, 1441, 1, "cumulativeBudget")]
    [InlineData(10, 20, 0, "maximumWaitingRuns")]
    [InlineData(10, 20, 17, "maximumWaitingRuns")]
    public void Host_options_fail_closed_outside_the_ceilings(int maximumMinutes, int budgetMinutes, int runs, string parameter)
    {
        var error = Assert.Throws<ArgumentOutOfRangeException>(() =>
            new WorkflowWaitOptions(TimeSpan.FromMinutes(maximumMinutes), TimeSpan.FromMinutes(budgetMinutes), runs));
        Assert.Equal(parameter, error.ParamName);
    }

    // ---- placement ---------------------------------------------------------------------------------------------

    private static WorkflowDefinition Skeleton(Action<ImmutableSortedDictionary<string, NodeDefinition>.Builder, ImmutableSortedDictionary<string, ConnectionDefinition>.Builder, ImmutableSortedDictionary<string, ConnectionDefinition>.Builder> build)
    {
        var nodes = ImmutableSortedDictionary.CreateBuilder<string, NodeDefinition>(StringComparer.Ordinal);
        var execution = ImmutableSortedDictionary.CreateBuilder<string, ConnectionDefinition>(StringComparer.Ordinal);
        var data = ImmutableSortedDictionary.CreateBuilder<string, ConnectionDefinition>(StringComparer.Ordinal);
        nodes["input"] = new(NodeTypes.Input, "1.0.0");
        nodes["output"] = new(NodeTypes.Output, "1.0.0");
        build(nodes, execution, data);
        return new(WorkflowSchemaVersions.UnifiedWorkflowGraph, "placement", "input", nodes.ToImmutable(), data.ToImmutable())
        {
            ExecutionConnections = execution.ToImmutable()
        };
    }

    private static void Edge(ImmutableSortedDictionary<string, ConnectionDefinition>.Builder edges, string from, string fromPin, string to, string toPin) =>
        edges[$"{from}-{fromPin}-{to}-{toPin}"] = Execution(from, fromPin, to, toPin);

    [Fact]
    public void A_wait_inside_a_sequence_output_is_rejected()
    {
        var definition = Skeleton((nodes, execution, _) =>
        {
            nodes["sequence"] = new(NodeTypes.Sequence, "1.0.0") { Sequence = new([new("first", "First"), new("second", "Second")]) };
            nodes["wait"] = Wait();
            Edge(execution, "input", "then", "sequence", "execute");
            Edge(execution, "sequence", SequenceOutputLimits.PinId("first"), "wait", "execute");
            Edge(execution, "wait", "then", "output", "execute");
            Edge(execution, "sequence", SequenceOutputLimits.PinId("second"), "output", "execute");
        });

        Assert.Equal(["wait"], WorkflowWaitPlacement.NodesInsideRegions(definition).ToArray());
        Assert.Contains(WorkflowValidator.Validate(definition), issue => issue.Code == "wait.placement" && issue.Path == "nodes.wait");
    }

    private static WorkflowDefinition LoopWithWait(string loopType, bool waitInBody, long milliseconds = 1_000, int maximumIterations = 3, bool waitOnFeedbackOnly = false)
    {
        var done = loopType == NodeTypes.Loop ? "done" : "completed";
        return Skeleton((nodes, execution, _) =>
        {
            nodes["loop"] = new(loopType, "1.0.0") { Loop = loopType == NodeTypes.Loop ? new(new("equals", "again"), maximumIterations) : null };
            nodes["body"] = new(NodeTypes.LoopBody, "1.0.0");
            nodes["step"] = new(NodeTypes.SequenceReverse, "1.0.0");
            nodes["wait"] = Wait(milliseconds);
            Edge(execution, "input", "then", "loop", "execute");
            Edge(execution, "loop", "body", "body", "execute");
            if (waitInBody)
            {
                Edge(execution, "body", "then", "wait", "execute");
                Edge(execution, "wait", "then", "step", "execute");
                Edge(execution, "loop", done, "output", "execute");
            }
            else
            {
                Edge(execution, "body", "then", "step", "execute");
                Edge(execution, "loop", done, "wait", "execute");
                Edge(execution, "wait", "then", "output", "execute");
            }
            Edge(execution, "step", "then", "loop", "feedback");
        });
    }

    [Theory]
    [InlineData(NodeTypes.Loop)]
    [InlineData(NodeTypes.ForEach)]
    public void A_wait_inside_a_loop_or_for_each_body_is_accepted_by_placement_and_a_wait_after_the_loop_is_unchanged(string loopType)
    {
        Assert.Empty(WorkflowWaitPlacement.NodesInsideRegions(LoopWithWait(loopType, waitInBody: true)));
        Assert.Empty(WorkflowWaitPlacement.NodesInsideRegions(LoopWithWait(loopType, waitInBody: false)));
        Assert.DoesNotContain(WorkflowValidator.Validate(LoopWithWait(loopType, waitInBody: true)), issue => issue.Code.StartsWith("wait.", StringComparison.Ordinal));
        Assert.Equal<long?>(loopType == NodeTypes.Loop ? 3 : WorkflowJsonArrayLimits.MaximumItems, WorkflowWaitPlacement.ExecutionBounds(LoopWithWait(loopType, true))["wait"]);
        Assert.Equal<long?>(1, WorkflowWaitPlacement.ExecutionBounds(LoopWithWait(loopType, false))["wait"]);
    }

    [Fact]
    public void A_wait_inside_a_loop_body_is_rejected_when_the_loop_sits_in_a_fork_branch_or_a_sequence_output()
    {
        var inFork = Skeleton((nodes, execution, _) =>
        {
            nodes["fork"] = new(NodeTypes.ParallelFork, "1.0.0") { ParallelFork = new([new("a", "A", "branch-a"), new("b", "B", "branch-b")]) };
            nodes["branch-a"] = new(NodeTypes.ParallelBranch, "1.0.0");
            nodes["branch-b"] = new(NodeTypes.ParallelBranch, "1.0.0");
            nodes["join"] = new(NodeTypes.ParallelJoin, "1.0.0") { ParallelJoin = new("fork", ["a", "b"]) };
            nodes["loop"] = new(NodeTypes.Loop, "1.0.0") { Loop = new(new("equals", "again"), 2) };
            nodes["body"] = new(NodeTypes.LoopBody, "1.0.0");
            nodes["wait"] = Wait();
            Edge(execution, "input", "then", "fork", "execute");
            Edge(execution, "fork", ParallelBranchLimits.PinId("a"), "branch-a", "execute");
            Edge(execution, "fork", ParallelBranchLimits.PinId("b"), "branch-b", "execute");
            Edge(execution, "branch-a", "then", "loop", "execute");
            Edge(execution, "loop", "body", "body", "execute");
            Edge(execution, "body", "then", "wait", "execute");
            Edge(execution, "wait", "then", "loop", "feedback");
            Edge(execution, "loop", "done", "join", ParallelBranchLimits.PinId("a"));
            Edge(execution, "branch-b", "then", "join", ParallelBranchLimits.PinId("b"));
            Edge(execution, "join", "then", "output", "execute");
        });
        Assert.Equal(["wait"], WorkflowWaitPlacement.NodesInsideRegions(inFork).ToArray());
        Assert.Contains(WorkflowValidator.Validate(inFork), issue => issue.Code == "wait.placement");

        var inSequence = Skeleton((nodes, execution, _) =>
        {
            nodes["sequence"] = new(NodeTypes.Sequence, "1.0.0") { Sequence = new([new("first", "First"), new("second", "Second")]) };
            nodes["loop"] = new(NodeTypes.Loop, "1.0.0") { Loop = new(new("equals", "again"), 2) };
            nodes["body"] = new(NodeTypes.LoopBody, "1.0.0");
            nodes["wait"] = Wait();
            Edge(execution, "input", "then", "sequence", "execute");
            Edge(execution, "sequence", SequenceOutputLimits.PinId("first"), "loop", "execute");
            Edge(execution, "loop", "body", "body", "execute");
            Edge(execution, "body", "then", "wait", "execute");
            Edge(execution, "wait", "then", "loop", "feedback");
            Edge(execution, "loop", "done", "output", "execute");
            Edge(execution, "sequence", SequenceOutputLimits.PinId("second"), "output", "execute");
        });
        Assert.Equal(["wait"], WorkflowWaitPlacement.NodesInsideRegions(inSequence).ToArray());
        Assert.Contains(WorkflowValidator.Validate(inSequence), issue => issue.Code == "wait.placement");
    }

    [Fact]
    public void A_wait_behind_a_fork_inside_a_loop_body_and_a_wait_after_its_join_stay_rejected()
    {
        var definition = Skeleton((nodes, execution, _) =>
        {
            nodes["loop"] = new(NodeTypes.ForEach, "1.0.0");
            nodes["body"] = new(NodeTypes.LoopBody, "1.0.0");
            nodes["fork"] = new(NodeTypes.ParallelFork, "1.0.0") { ParallelFork = new([new("a", "A", "branch-a"), new("b", "B", "branch-b")]) };
            nodes["branch-a"] = new(NodeTypes.ParallelBranch, "1.0.0");
            nodes["branch-b"] = new(NodeTypes.ParallelBranch, "1.0.0");
            nodes["join"] = new(NodeTypes.ParallelJoin, "1.0.0") { ParallelJoin = new("fork", ["a", "b"]) };
            nodes["inbranch"] = Wait();
            nodes["wait"] = Wait();
            Edge(execution, "input", "then", "loop", "execute");
            Edge(execution, "loop", "body", "body", "execute");
            Edge(execution, "body", "then", "fork", "execute");
            Edge(execution, "fork", ParallelBranchLimits.PinId("a"), "branch-a", "execute");
            Edge(execution, "fork", ParallelBranchLimits.PinId("b"), "branch-b", "execute");
            Edge(execution, "branch-a", "then", "inbranch", "execute");
            Edge(execution, "inbranch", "then", "join", ParallelBranchLimits.PinId("a"));
            Edge(execution, "branch-b", "then", "join", ParallelBranchLimits.PinId("b"));
            Edge(execution, "join", "then", "wait", "execute");
            Edge(execution, "wait", "then", "loop", "feedback");
            Edge(execution, "loop", "completed", "output", "execute");
        });

        Assert.Equal(["inbranch", "wait"], WorkflowWaitPlacement.NodesInsideRegions(definition).ToArray());
    }

    [Fact]
    public void A_wait_on_the_feedback_path_only_counts_as_in_the_loop_and_nothing_past_feedback_is_walked()
    {
        // The Wait feeds the feedback pin directly: it is in the body. A Wait after the loop's done pin is not.
        var definition = LoopWithWait(NodeTypes.ForEach, waitInBody: true);
        Assert.Empty(WorkflowWaitPlacement.NodesInsideRegions(definition));
        Assert.Equal<long?>(WorkflowJsonArrayLimits.MaximumItems, WorkflowWaitPlacement.ExecutionBounds(definition)["wait"]);
    }

    [Theory]
    [InlineData(NodeTypes.Loop, 3, 1_000, 3_000, true)]
    [InlineData(NodeTypes.Loop, 3, 1_000, 2_999, false)]
    [InlineData(NodeTypes.Loop, 16, 60_000, 960_000, true)]
    [InlineData(NodeTypes.Loop, 16, 60_000, 959_999, false)]
    [InlineData(NodeTypes.ForEach, 0, 1_000, 64_000, true)]
    [InlineData(NodeTypes.ForEach, 0, 1_000, 63_999, false)]
    public void Static_budget_charges_the_duration_times_a_proven_bound_and_rejects_over_budget(string loopType, int iterations, long durationMs, long budgetMs, bool accepted)
    {
        var options = new WorkflowWaitOptions(TimeSpan.FromHours(1), TimeSpan.FromMilliseconds(Math.Max(budgetMs, 3_600_000)), 4);
        var tight = new WorkflowWaitOptions(TimeSpan.FromMilliseconds(Math.Min(durationMs, budgetMs)), TimeSpan.FromMilliseconds(budgetMs), 4);
        var definition = LoopWithWait(loopType, waitInBody: true, durationMs, iterations);

        var issues = tight.Check(definition);

        Assert.Equal(accepted, issues.IsEmpty);
        if (!accepted) Assert.All(issues, issue => Assert.Equal("wait.budget", issue.Code));
        Assert.NotNull(options);
    }

    [Fact]
    public void Nested_loops_multiply_and_a_wait_after_the_inner_loop_is_charged_by_the_outer_loop_only()
    {
        var definition = Skeleton((nodes, execution, _) =>
        {
            nodes["outer"] = new(NodeTypes.Loop, "1.0.0") { Loop = new(new("equals", "again"), 4) };
            nodes["obody"] = new(NodeTypes.LoopBody, "1.0.0");
            nodes["inner"] = new(NodeTypes.Loop, "1.0.0") { Loop = new(new("equals", "again"), 5) };
            nodes["ibody"] = new(NodeTypes.LoopBody, "1.0.0");
            nodes["innerwait"] = Wait();
            nodes["afterwait"] = Wait();
            Edge(execution, "input", "then", "outer", "execute");
            Edge(execution, "outer", "body", "obody", "execute");
            Edge(execution, "obody", "then", "inner", "execute");
            Edge(execution, "inner", "body", "ibody", "execute");
            Edge(execution, "ibody", "then", "innerwait", "execute");
            Edge(execution, "innerwait", "then", "inner", "feedback");
            Edge(execution, "inner", "done", "afterwait", "execute");
            Edge(execution, "afterwait", "then", "outer", "feedback");
            Edge(execution, "outer", "done", "output", "execute");
        });

        var bounds = WorkflowWaitPlacement.ExecutionBounds(definition);

        Assert.Equal<long?>(20, bounds["innerwait"]);
        Assert.Equal<long?>(4, bounds["afterwait"]);
    }

    [Fact]
    public void A_bound_that_cannot_be_proven_is_rejected_with_wait_budget()
    {
        var missingLimit = LoopWithWait(NodeTypes.Loop, waitInBody: true);
        missingLimit = missingLimit with { Nodes = missingLimit.Nodes.SetItem("loop", missingLimit.Nodes["loop"] with { Loop = null }) };
        var outOfRange = LoopWithWait(NodeTypes.Loop, waitInBody: true);
        outOfRange = outOfRange with { Nodes = outOfRange.Nodes.SetItem("loop", outOfRange.Nodes["loop"] with { Loop = new(new("equals", "again"), 17) }) };
        var noEdges = LoopWithWait(NodeTypes.Loop, waitInBody: true) with { ExecutionConnections = null };
        var generous = new WorkflowWaitOptions(TimeSpan.FromHours(1), TimeSpan.FromHours(24), 4);

        foreach (var definition in new[] { missingLimit, outOfRange, noEdges })
            Assert.Contains(generous.Check(definition), issue => issue.Code == "wait.budget");
    }

    [Fact]
    public void The_per_wait_maximum_still_applies_inside_a_loop_and_a_top_level_wait_is_charged_once()
    {
        var options = new WorkflowWaitOptions(TimeSpan.FromMinutes(1), TimeSpan.FromHours(24), 4);

        Assert.Contains(options.Check(LoopWithWait(NodeTypes.Loop, true, 60_001)), issue => issue.Code == "wait.bound");
        Assert.Empty(options.Check(TopLevel(60_000)));
        Assert.Equal<long?>(1, WorkflowWaitPlacement.ExecutionBounds(TopLevel(waits: 2))["wait1"]);
    }

    [Fact]
    public void Repeated_wait_evidence_per_iteration_correlates_by_wait_id_and_leaves_only_the_unsettled_iteration()
    {
        var definition = TopLevel();
        var header = new RunHeader(Guid.NewGuid(), Guid.NewGuid(), null, "x", DateTimeOffset.UtcNow, WorkflowJson.ComputeHash(definition), definition, "in");
        var events = new List<ExecutionEvent>();
        void Add(string kind, Guid invocation, Guid? waitId = null, int? iteration = null) => events.Add(new(header.RunId, header.RevisionId, events.Count + 1,
            DateTimeOffset.UtcNow, kind, "wait1", invocation, Data: waitId is null ? null : $"{{\"waitId\":\"{waitId}\",\"loopIteration\":{iteration}}}"));
        Guid second = Guid.NewGuid(), secondWait = Guid.NewGuid();
        var first = Guid.NewGuid();
        var firstWait = Guid.NewGuid();
        Add(ExecutionEventKinds.NodeStarted, first); Add(ExecutionEventKinds.WaitRequested, first, firstWait, 0);
        Add(ExecutionEventKinds.WaitElapsed, first, firstWait, 0); Add(ExecutionEventKinds.NodeCompleted, first);
        Add(ExecutionEventKinds.NodeStarted, second); Add(ExecutionEventKinds.WaitRequested, second, secondWait, 1);

        var evidence = WorkflowWorkEvidence.Read(new RunSnapshot(header, [.. events]));

        Assert.False(evidence.HasIncompleteCorrelation);
        Assert.Equal(2, evidence.TotalUnresolvedWork);
        Assert.All(evidence.UnresolvedWork, work => Assert.Equal(second, work.InvocationId));
        Assert.Contains(evidence.UnresolvedWork, work => work.CallId == secondWait);
    }

    [Fact]
    public void A_wait_in_a_fork_branch_is_rejected_but_a_wait_after_the_join_is_not()
    {
        WorkflowDefinition Build(bool waitInBranch) => Skeleton((nodes, execution, _) =>
        {
            nodes["fork"] = new(NodeTypes.ParallelFork, "1.0.0") { ParallelFork = new([new("a", "A", "branch-a"), new("b", "B", "branch-b")]) };
            nodes["branch-a"] = new(NodeTypes.ParallelBranch, "1.0.0");
            nodes["branch-b"] = new(NodeTypes.ParallelBranch, "1.0.0");
            nodes["join"] = new(NodeTypes.ParallelJoin, "1.0.0") { ParallelJoin = new("fork", ["a", "b"]) };
            nodes["wait"] = Wait();
            Edge(execution, "input", "then", "fork", "execute");
            Edge(execution, "fork", ParallelBranchLimits.PinId("a"), "branch-a", "execute");
            Edge(execution, "fork", ParallelBranchLimits.PinId("b"), "branch-b", "execute");
            Edge(execution, "branch-b", "then", "join", ParallelBranchLimits.PinId("b"));
            if (waitInBranch)
            {
                Edge(execution, "branch-a", "then", "wait", "execute");
                Edge(execution, "wait", "then", "join", ParallelBranchLimits.PinId("a"));
                Edge(execution, "join", "then", "output", "execute");
            }
            else
            {
                Edge(execution, "branch-a", "then", "join", ParallelBranchLimits.PinId("a"));
                Edge(execution, "join", "then", "wait", "execute");
                Edge(execution, "wait", "then", "output", "execute");
            }
        });

        Assert.Equal(["wait"], WorkflowWaitPlacement.NodesInsideRegions(Build(waitInBranch: true)).ToArray());
        Assert.Empty(WorkflowWaitPlacement.NodesInsideRegions(Build(waitInBranch: false)));
        Assert.Contains(WorkflowValidator.Validate(Build(waitInBranch: true)), issue => issue.Code == "wait.placement");
    }

    [Fact]
    public void A_wait_after_a_join_nested_in_a_loop_body_stays_inside_the_loop_region()
    {
        var definition = Skeleton((nodes, execution, _) =>
        {
            nodes["loop"] = new(NodeTypes.ForEach, "1.0.0");
            nodes["body"] = new(NodeTypes.LoopBody, "1.0.0");
            nodes["fork"] = new(NodeTypes.ParallelFork, "1.0.0") { ParallelFork = new([new("a", "A", "branch-a"), new("b", "B", "branch-b")]) };
            nodes["branch-a"] = new(NodeTypes.ParallelBranch, "1.0.0");
            nodes["branch-b"] = new(NodeTypes.ParallelBranch, "1.0.0");
            nodes["join"] = new(NodeTypes.ParallelJoin, "1.0.0") { ParallelJoin = new("fork", ["a", "b"]) };
            nodes["wait"] = Wait();
            Edge(execution, "input", "then", "loop", "execute");
            Edge(execution, "loop", "body", "body", "execute");
            Edge(execution, "body", "then", "fork", "execute");
            Edge(execution, "fork", ParallelBranchLimits.PinId("a"), "branch-a", "execute");
            Edge(execution, "fork", ParallelBranchLimits.PinId("b"), "branch-b", "execute");
            Edge(execution, "branch-a", "then", "join", ParallelBranchLimits.PinId("a"));
            Edge(execution, "branch-b", "then", "join", ParallelBranchLimits.PinId("b"));
            Edge(execution, "join", "then", "wait", "execute");
            Edge(execution, "wait", "then", "loop", "feedback");
            Edge(execution, "loop", "completed", "output", "execute");
        });

        Assert.Equal(["wait"], WorkflowWaitPlacement.NodesInsideRegions(definition).ToArray());
    }

    [Fact]
    public void A_wait_inside_a_sub_workflow_is_rejected_in_the_calling_workflow()
    {
        var child = WorkflowRevision.Create(TopLevel() with { WorkflowId = "child-wait" }, "test");
        Assert.Empty(WorkflowValidator.Validate(child.Definition));
        var nodes = ImmutableSortedDictionary.CreateBuilder<string, NodeDefinition>(StringComparer.Ordinal);
        nodes["input"] = new(NodeTypes.Input, "1.0.0");
        nodes["call"] = new(NodeTypes.WorkflowCall, "1.0.0")
        {
            WorkflowCall = new(child.Definition.WorkflowId, child.RevisionId, 1, child.ContentHash, child.Definition)
        };
        nodes["output"] = new(NodeTypes.Output, "1.0.0");
        var parent = new WorkflowDefinition(WorkflowSchemaVersions.UnifiedWorkflowGraph, "parent", "input", nodes.ToImmutable(),
            ImmutableSortedDictionary<string, ConnectionDefinition>.Empty
                .Add("in", new(new("input", "out"), new("call", "in"), "data"))
                .Add("out", new(new("call", "out"), new("output", "in"), "data")))
        {
            ExecutionConnections = ImmutableSortedDictionary<string, ConnectionDefinition>.Empty
                .Add("a", Execution("input", "then", "call", "execute"))
                .Add("b", Execution("call", "out", "output", "execute"))
        };

        var issues = WorkflowValidator.Validate(parent);

        Assert.Contains(issues, issue => issue.Code == "wait.placement" && issue.Path.Contains("workflowCall.definition.nodes.wait1", StringComparison.Ordinal));
    }

    [Fact]
    public void A_wait_cannot_sit_between_an_approval_and_its_tool_so_it_never_overlaps_an_authorization_window()
    {
        var definition = Skeleton((nodes, execution, data) =>
        {
            nodes["approval"] = new(NodeTypes.Approval, "1.0.0") { Approval = new("Review.") };
            nodes["wait"] = Wait();
            Edge(execution, "input", "then", "approval", "execute");
            Edge(execution, "approval", "then", "wait", "execute");
            Edge(execution, "wait", "then", "output", "execute");
            data["a"] = new(new("input", "out"), new("approval", "in"), "data");
            data["b"] = new(new("input", "out"), new("output", "in"), "data");
        });

        Assert.Contains(WorkflowValidator.Validate(definition), issue => issue.Code == "approval.target");
    }

    [Theory]
    [InlineData(NodeTypes.Sequence)]
    [InlineData(NodeTypes.ParallelFork)]
    [InlineData(NodeTypes.ParallelBranch)]
    [InlineData(NodeTypes.ParallelJoin)]
    public void Placement_fails_closed_when_edges_are_absent_and_a_region_node_coexists_with_a_wait(string regionType)
    {
        var definition = Skeleton((nodes, _, _) =>
        {
            nodes["region"] = new(regionType, "1.0.0");
            nodes["wait"] = Wait();
        }) with { ExecutionConnections = null };

        Assert.Equal(["wait"], WorkflowWaitPlacement.NodesInsideRegions(definition).ToArray());
        Assert.Contains(WorkflowValidator.Validate(definition), issue => issue.Code == "wait.placement" && issue.Path == "nodes.wait");
    }

    [Theory]
    [InlineData(NodeTypes.Loop)]
    [InlineData(NodeTypes.ForEach)]
    [InlineData(NodeTypes.LoopBody)]
    [InlineData(NodeTypes.Break)]
    public void Loop_nodes_no_longer_fail_placement_closed_without_edges_but_the_static_budget_cannot_prove_a_bound(string loopType)
    {
        var definition = Skeleton((nodes, _, _) =>
        {
            nodes["region"] = new(loopType, "1.0.0");
            nodes["wait"] = Wait();
        }) with { ExecutionConnections = null };

        Assert.Empty(WorkflowWaitPlacement.NodesInsideRegions(definition));
        var issues = new WorkflowWaitOptions(TimeSpan.FromHours(1), TimeSpan.FromHours(24), 4).Check(definition);
        if (loopType is NodeTypes.Loop or NodeTypes.ForEach) Assert.Contains(issues, issue => issue.Code == "wait.budget");
    }

    [Fact]
    public void Placement_without_edges_is_clean_when_no_region_node_exists()
    {
        var definition = Skeleton((nodes, _, _) => nodes["wait"] = Wait()) with { ExecutionConnections = null };

        Assert.Empty(WorkflowWaitPlacement.NodesInsideRegions(definition));
    }

    [Fact]
    public void The_walk_sees_the_edges_of_a_real_unified_graph()
    {
        // Guards the fail-closed shortcut: a genuine schema 2.0 graph keeps its edges in ExecutionConnections.
        Assert.NotNull(TopLevel().ExecutionConnections);
        Assert.Equal(["wait"], WorkflowWaitPlacement.NodesInsideRegions(Skeleton((nodes, execution, _) =>
        {
            nodes["sequence"] = new(NodeTypes.Sequence, "1.0.0") { Sequence = new([new("first", "First")]) };
            nodes["wait"] = Wait();
            Edge(execution, "input", "then", "sequence", "execute");
            Edge(execution, "sequence", SequenceOutputLimits.PinId("first"), "wait", "execute");
            Edge(execution, "wait", "then", "output", "execute");
        })).ToArray());
    }

    // ---- mixed-region characterization -------------------------------------------------------------------------

    /// <summary>The For Each fixture from ForEachNodeTests with a top-level Wait before or after the iterator.</summary>
    private static WorkflowDefinition ForEachWithWait(bool waitBefore)
    {
        var nodes = ImmutableSortedDictionary.CreateBuilder<string, NodeDefinition>(StringComparer.Ordinal);
        nodes["input"] = new(NodeTypes.Input, "1.0.0");
        nodes["wait"] = Wait();
        nodes["items"] = new(NodeTypes.ArrayLiteral, "1.0.0") { ArrayLiteral = new(WorkflowDataTypes.String, "[\"a\",\"b\"]") };
        nodes["each"] = new(NodeTypes.ForEach, "1.0.0") { ArrayElementType = WorkflowDataTypes.String };
        nodes["body"] = new(NodeTypes.LoopBody, "1.0.0");
        nodes["step"] = new(NodeTypes.SequenceReverse, "1.0.0");
        nodes["output"] = new(NodeTypes.Output, "1.0.0");
        var data = ImmutableSortedDictionary<string, ConnectionDefinition>.Empty
            .Add("items-each", new(new("items", "out"), new("each", "array"), "data"))
            .Add("item-step", new(new("each", "item"), new("step", "in"), "data"))
            .Add("result", new(new("step", "out"), new("output", "in"), "data"));
        var execution = ImmutableSortedDictionary<string, ConnectionDefinition>.Empty
            .Add("body-start", Execution("each", "body", "body", "execute"))
            .Add("body-step", Execution("body", "then", "step", "execute"))
            .Add("return", Execution("step", "then", "each", "feedback"));
        execution = waitBefore
            ? execution.Add("start", Execution("input", "then", "wait", "execute")).Add("then-each", Execution("wait", "then", "each", "execute"))
                .Add("done", Execution("each", "completed", "output", "execute"))
            : execution.Add("start", Execution("input", "then", "each", "execute")).Add("done", Execution("each", "completed", "wait", "execute"))
                .Add("then-output", Execution("wait", "then", "output", "execute"));
        return new(WorkflowSchemaVersions.UnifiedWorkflowGraph, "wait-with-each", "input", nodes.ToImmutable(), data) { ExecutionConnections = execution };
    }

    [Fact]
    public void A_top_level_wait_after_a_for_each_is_legal_and_validates_in_the_loop_profile()
    {
        var definition = ForEachWithWait(waitBefore: false);

        Assert.Empty(WorkflowWaitPlacement.NodesInsideRegions(definition));
        Assert.Empty(WorkflowValidator.Validate(definition));
    }

    [Fact]
    public void A_wait_before_a_for_each_is_placed_legally_but_the_loop_profile_still_rejects_the_graph_closed()
    {
        // Characterization of an open limit (Decision 0081, sub-decision 10): the loop topology proof requires the
        // iterator to be entered directly from Input, so a Wait ahead of it is refused by that profile, not by Wait.
        var issues = WorkflowValidator.Validate(ForEachWithWait(waitBefore: true));

        Assert.Empty(WorkflowWaitPlacement.NodesInsideRegions(ForEachWithWait(waitBefore: true)));
        Assert.DoesNotContain(issues, issue => issue.Code.StartsWith("wait.", StringComparison.Ordinal));
        Assert.Contains(issues, issue => issue.Code == "loop.feedback.root");
    }

    // ---- consequences in other Core projections ---------------------------------------------------------------

    [Fact]
    public void Node_stepping_is_not_supported_for_a_workflow_with_a_wait()
    {
        Assert.False(WorkflowDebugging.SupportsNodeStepping(TopLevel()));
        Assert.True(WorkflowDebugging.SupportsBreakpoint(TopLevel(), "wait1"));
    }

    [Fact]
    public void An_unsettled_wait_requested_is_unresolved_node_work_correlated_by_its_wait_id_and_settles_cleanly()
    {
        var waitId = Guid.NewGuid();
        var invocation = Guid.NewGuid();
        var definition = TopLevel();
        var header = new RunHeader(Guid.NewGuid(), Guid.NewGuid(), null, "x", DateTimeOffset.UtcNow, WorkflowJson.ComputeHash(definition), definition, "in");
        ExecutionEvent Event(long sequence, string kind, string? data = null) =>
            new(header.RunId, header.RevisionId, sequence, DateTimeOffset.UtcNow, kind, "wait1", invocation, Data: data);
        var requested = Event(2, ExecutionEventKinds.WaitRequested, $"{{\"waitId\":\"{waitId}\"}}");
        var started = Event(1, ExecutionEventKinds.NodeStarted);

        var unsettled = WorkflowWorkEvidence.Read(new RunSnapshot(header, [started, requested]));
        Assert.Equal(2, unsettled.TotalUnresolvedWork);
        Assert.Contains(unsettled.UnresolvedWork, work => work.Kind == UnresolvedWorkKind.Node && work.CallId == waitId &&
            work.NodeId == "wait1" && work.State == UnresolvedWorkState.AwaitingOutcome);
        Assert.False(unsettled.HasIncompleteCorrelation);

        foreach (var settlement in new[] { ExecutionEventKinds.WaitElapsed, ExecutionEventKinds.WaitCancelled, ExecutionEventKinds.WaitInterrupted })
        {
            var settled = WorkflowWorkEvidence.Read(new RunSnapshot(header,
                [started, requested, Event(3, settlement, $"{{\"waitId\":\"{waitId}\"}}"), Event(4, ExecutionEventKinds.NodeCompleted)]));
            Assert.Equal(0, settled.TotalUnresolvedWork);
            Assert.False(settled.HasIncompleteCorrelation);
        }
    }

    [Fact]
    public void A_wait_requested_without_a_wait_id_is_ambiguous_correlation_not_silently_ignored()
    {
        var definition = TopLevel();
        var header = new RunHeader(Guid.NewGuid(), Guid.NewGuid(), null, "x", DateTimeOffset.UtcNow, WorkflowJson.ComputeHash(definition), definition, "in");
        var requested = new ExecutionEvent(header.RunId, header.RevisionId, 1, DateTimeOffset.UtcNow,
            ExecutionEventKinds.WaitRequested, "wait1", Guid.NewGuid(), Data: "{}");

        var evidence = WorkflowWorkEvidence.Read(new RunSnapshot(header, [requested]));

        Assert.True(evidence.HasIncompleteCorrelation);
    }

    [Fact]
    public void The_unresolved_work_kind_enum_is_unchanged_so_older_observers_never_see_an_undefined_value()
    {
        Assert.Equal(new[] { "Node", "Model", "Tool", "Approval" }, Enum.GetNames<UnresolvedWorkKind>());
    }
}
