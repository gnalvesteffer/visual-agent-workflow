using System.Collections.Immutable;
using System.Collections.Concurrent;
using System.Text.Json;
using VisualAgentWorkflow.AgentFramework;
using VisualAgentWorkflow.Core;
using Xunit;

namespace VisualAgentWorkflow.AgentFramework.Tests;

public sealed class SingleSequenceActivationProductionProofTests
{
    [Fact]
    public async Task One_framed_item_uses_production_sequence_dispatch_and_root_lifecycle()
    {
        var definition = new WorkflowDefinition(WorkflowSchemaVersions.UnifiedWorkflowGraph,
            "single-sequence-activation-proof", "input",
            ImmutableSortedDictionary<string, NodeDefinition>.Empty
                .Add("input", new(NodeTypes.Input, "1.0.0"))
                .Add("sequence", new(NodeTypes.Sequence, "1.0.0")
                {
                    Sequence = new([new("body", "Body"), new("follow-up", "Follow-up")])
                })
                .Add("agent", new(NodeTypes.Agent, "1.0.0", Agent: new AgentConfiguration(
                    "proof.echo", null, "Return the input unchanged.", []) { MaxTurns = 1 }))
                .Add("follow-up-agent", new(NodeTypes.Agent, "1.0.0", Agent: new AgentConfiguration(
                    "proof.echo", null, "Return the input unchanged.", []) { MaxTurns = 1 }))
                .Add("output", new(NodeTypes.Output, "1.0.0")),
            ImmutableSortedDictionary<string, ConnectionDefinition>.Empty
                .Add("input-agent", Edge("input", "out", "agent", "in", "data"))
                .Add("input-follow-up-agent", Edge("input", "out", "follow-up-agent", "in", "data"))
                .Add("agent-output-data", Edge("agent", "out", "output", "in", "data")))
        {
            Parameters = ImmutableSortedDictionary<string, WorkflowParameterDefinition>.Empty
                .Add("index", new("Index", "0") { Type = WorkflowDataTypes.Integer }),
            ExecutionConnections = ImmutableSortedDictionary<string, ConnectionDefinition>.Empty
                .Add("input-sequence", Edge("input", "then", "sequence", "execute"))
                .Add("sequence-agent", Edge("sequence", "then.body", "agent", "execute"))
                .Add("sequence-follow-up-agent", Edge("sequence", "then.follow-up", "follow-up-agent", "execute"))
                .Add("agent-output", Edge("agent", "out", "output", "execute"))
                .Add("follow-up-output", Edge("follow-up-agent", "out", "output", "execute"))
        };
        var revision = WorkflowRevision.Create(definition, "single-sequence-activation-proof");
        var agent = new CapturingAgentExecutor();
        var events = new List<ExecutionEvent>();
        var runId = Guid.NewGuid();

        var result = await new WorkflowRunner(agents: agent)
            .ExecuteSingleSequenceActivationProductionProofAsync(runId, revision, "one-item", 0, "index",
                evt => { events.Add(evt); return ValueTask.CompletedTask; });

        Assert.False(result.Failed);
        Assert.Equal("one-item", result.Output);
        Assert.Equal(new[] { "one-item", "one-item" },
            agent.Invocations.Select(invocation => invocation.Input));
        Assert.All(agent.Invocations, invocation =>
            Assert.Equal("0", invocation.Parameters!["index"]));

        var bodyStarted = Assert.Single(events, evt => evt.Kind == ExecutionEventKinds.NodeStarted &&
            evt.NodeId == "agent");
        var bodyCompleted = Assert.Single(events, evt => evt.Kind == ExecutionEventKinds.NodeCompleted &&
            evt.NodeId == "agent");
        var sequenceSettled = events.Where(evt => evt.Kind == ExecutionEventKinds.SequenceOutputCompleted).ToArray();
        Assert.Equal(2, sequenceSettled.Length);
        var sequenceCompleted = Assert.Single(events, evt => evt.Kind == ExecutionEventKinds.SequenceCompleted);
        var rootStarted = Assert.Single(events, evt => evt.Kind == ExecutionEventKinds.Started && evt.NodeId is null);
        var rootCompleted = Assert.Single(events, evt => evt.Kind == ExecutionEventKinds.Completed && evt.NodeId is null);
        Assert.Equal("array-loop/0", bodyStarted.ScopeId);
        Assert.Equal("array-loop/0", bodyCompleted.ScopeId);
        Assert.All(sequenceSettled, evt => Assert.Equal("array-loop/0", evt.ScopeId));
        Assert.Equal("array-loop/0", sequenceCompleted.ScopeId);
        Assert.True(rootStarted.Sequence < bodyCompleted.Sequence);
        Assert.True(bodyCompleted.Sequence < sequenceSettled[^1].Sequence);
        Assert.True(sequenceSettled[^1].Sequence < sequenceCompleted.Sequence);
        Assert.True(sequenceCompleted.Sequence < rootCompleted.Sequence);
        Assert.Equal(Enumerable.Range(1, events.Count).Select(value => (long)value),
            events.Select(evt => evt.Sequence));
    }

    [Fact]
    public async Task Multi_item_coordinator_settles_each_production_sequence_before_admitting_the_next()
    {
        var revision = CreateSequenceRevision("multi-item-sequence-proof");
        var agent = new CapturingAgentExecutor();
        var events = new List<ExecutionEvent>();

        var result = await new WorkflowRunner(agents: agent)
            .ExecuteMultiItemSequenceActivationCoordinatorProofAsync(Guid.NewGuid(), revision,
                "[\"alpha\",\"beta\",\"gamma\"]", "index", "item", WorkflowDataTypes.String,
                evt => { events.Add(evt); return ValueTask.CompletedTask; });

        Assert.False(result.Failed);
        Assert.Equal("gamma", result.Output);
        Assert.Equal(new[] { "alpha", "alpha", "beta", "beta", "gamma", "gamma" },
            agent.Invocations.Select(invocation => invocation.Input));
        Assert.Equal(new[] { "0", "0", "1", "1", "2", "2" }, agent.Invocations
            .Select(invocation => invocation.Parameters!["index"]));
        Assert.Equal(new[] { "alpha", "alpha", "beta", "beta", "gamma", "gamma" }, agent.Invocations
            .Select(invocation => invocation.Parameters!["item"]));
        Assert.Equal(new[] { "agent", "follow-up-agent", "agent", "follow-up-agent", "agent", "follow-up-agent" },
            agent.Invocations.Select(invocation => invocation.NodeId));

        var iterationStarts = events.Where(evt => evt.Kind == ExecutionEventKinds.LoopIterationStarted).ToArray();
        var iterationCompletions = events.Where(evt => evt.Kind == ExecutionEventKinds.LoopIterationCompleted).ToArray();
        var sequenceSettlements = events.Where(evt => evt.Kind == ExecutionEventKinds.SequenceCompleted).ToArray();
        Assert.Equal(3, iterationStarts.Length);
        Assert.Equal(3, iterationCompletions.Length);
        Assert.Equal(3, sequenceSettlements.Length);
        for (var index = 0; index < iterationStarts.Length; index++)
        {
            var scope = $"array-loop/{index + 1}";
            Assert.Equal(scope, iterationStarts[index].ScopeId);
            Assert.Equal(scope, iterationCompletions[index].ScopeId);
            Assert.Equal(scope, sequenceSettlements[index].ScopeId);
            Assert.Equal("completed", JsonDocument.Parse(iterationCompletions[index].Data!).RootElement
                .GetProperty("outcome").GetString());
            Assert.True(iterationStarts[index].Sequence < sequenceSettlements[index].Sequence);
            Assert.True(sequenceSettlements[index].Sequence < iterationCompletions[index].Sequence);
            if (index < iterationStarts.Length - 1)
                Assert.True(iterationCompletions[index].Sequence < iterationStarts[index + 1].Sequence);
        }
        Assert.Single(events, evt => evt.Kind == ExecutionEventKinds.Started && evt.NodeId is null);
        Assert.Single(events, evt => evt.Kind == ExecutionEventKinds.Completed && evt.NodeId is null);
        AssertTerminalAndContiguous(events, ExecutionEventKinds.Completed);
    }

    [Fact]
    public async Task Multi_item_sequence_coordinator_uses_the_root_activation_owner_budget()
    {
        var revision = CreateSequenceRevision("bounded-multi-item-sequence-proof");
        var agent = new CapturingAgentExecutor();
        var events = new List<ExecutionEvent>();

        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => new WorkflowRunner(agents: agent)
            .ExecuteMultiItemSequenceActivationCoordinatorProofAsync(Guid.NewGuid(), revision,
                "[\"alpha\",\"beta\",\"gamma\"]", "index", "item", WorkflowDataTypes.String,
                evt => { events.Add(evt); return ValueTask.CompletedTask; }, maximumLoopPasses: 2));

        Assert.Equal(new[] { "alpha", "alpha", "beta", "beta" },
            agent.Invocations.Select(invocation => invocation.Input));
        Assert.Equal(new[] { "array-loop/1", "array-loop/2" }, events
            .Where(evt => evt.Kind == ExecutionEventKinds.LoopIterationStarted)
            .Select(evt => evt.ScopeId));
        Assert.DoesNotContain(events, evt => evt.Kind == ExecutionEventKinds.LoopIterationStarted &&
            evt.ScopeId == "array-loop/3");
        Assert.Single(events, evt => evt.Kind == ExecutionEventKinds.Failed && evt.NodeId is null);
        Assert.DoesNotContain(events, evt => evt.Kind == ExecutionEventKinds.Completed && evt.NodeId is null);
        AssertTerminalAndContiguous(events, ExecutionEventKinds.Failed);
    }

    [Fact]
    public async Task Sequence_coordinator_waits_for_every_concurrent_output_before_admitting_next_item()
    {
        var revision = CreateSequenceRevision("held-multi-item-sequence-proof");
        var agent = new CapturingAgentExecutor(holdNodeId: "agent");
        var events = new ConcurrentQueue<ExecutionEvent>();
        ValueTask Record(ExecutionEvent evt)
        {
            events.Enqueue(evt);
            return ValueTask.CompletedTask;
        }

        var execution = new WorkflowRunner(agents: agent)
            .ExecuteMultiItemSequenceActivationCoordinatorProofAsync(Guid.NewGuid(), revision,
                "[\"alpha\",\"beta\"]", "index", "item", WorkflowDataTypes.String,
                Record, maximumConcurrentSequenceOutputs: 2);
        try
        {
            await Task.WhenAll(agent.HeldAgentStarted.Task, agent.OtherAgentStarted.Task)
                .WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(execution.IsCompleted,
                "The Sequence item must stay active until every admitted Sequence output settles.");
            Assert.DoesNotContain(events, evt => evt.Kind == ExecutionEventKinds.SequenceCompleted &&
                evt.ScopeId == "array-loop/1");
            Assert.DoesNotContain(events, evt => evt.Kind == ExecutionEventKinds.SequenceOutputCompleted &&
                evt.ScopeId == "array-loop/1");
            Assert.DoesNotContain(events, evt => evt.Kind == ExecutionEventKinds.LoopIterationCompleted &&
                evt.ScopeId == "array-loop/1");
            Assert.DoesNotContain(events, evt => evt.Kind == ExecutionEventKinds.LoopIterationStarted &&
                evt.ScopeId == "array-loop/2");
            Assert.DoesNotContain(agent.Invocations, invocation => invocation.Input == "beta");
        }
        catch (TimeoutException exception)
        {
            throw new Xunit.Sdk.XunitException(exception.Message + " Events: " +
                string.Join(" | ", events.Select(evt => $"{evt.Kind}:{evt.NodeId}:{evt.ScopeId}:{evt.ConnectionId}")) +
                " Invocations: " + string.Join(" | ", agent.Invocations.Select(call => $"{call.NodeId}:{call.Input}")));
        }
        finally
        {
            agent.ReleaseHeldAgent.TrySetResult();
        }

        var result = await execution.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.False(result.Failed);
        Assert.Equal("beta", result.Output);
        var recorded = events.ToArray();
        var firstItemOutputs = recorded.Where(evt => evt.Kind == ExecutionEventKinds.SequenceOutputCompleted &&
            evt.ScopeId == "array-loop/1").ToArray();
        Assert.Equal(2, firstItemOutputs.Length);
        var firstSequenceCompleted = Array.FindIndex(recorded, evt =>
            evt.Kind == ExecutionEventKinds.SequenceCompleted && evt.ScopeId == "array-loop/1");
        var firstIterationCompleted = Array.FindIndex(recorded, evt =>
            evt.Kind == ExecutionEventKinds.LoopIterationCompleted && evt.ScopeId == "array-loop/1");
        var secondIterationStarted = Array.FindIndex(recorded, evt =>
            evt.Kind == ExecutionEventKinds.LoopIterationStarted && evt.ScopeId == "array-loop/2");
        Assert.All(firstItemOutputs, evt => Assert.True(evt.Sequence < recorded[firstSequenceCompleted].Sequence));
        Assert.True(firstSequenceCompleted < firstIterationCompleted);
        Assert.True(firstIterationCompleted < secondIterationStarted);
        Assert.Equal(new[] { ("alpha", "0"), ("alpha", "0"), ("beta", "1"), ("beta", "1") },
            agent.Invocations.Select(invocation => (invocation.Input, invocation.Parameters!["index"]))
                .OrderBy(value => value.Input, StringComparer.Ordinal)
                .ThenBy(value => value.Item2, StringComparer.Ordinal));
        AssertTerminalAndContiguous(recorded, ExecutionEventKinds.Completed);
    }

    [Fact]
    public async Task Multi_item_ordinary_coordinator_uses_validated_graph_dispatch_and_root_frames()
    {
        var revision = CreateOrdinaryRevision("multi-item-ordinary-proof");
        var agent = new CapturingAgentExecutor();
        var events = new List<ExecutionEvent>();

        var result = await new WorkflowRunner(agents: agent)
            .ExecuteMultiItemOrdinaryActivationCoordinatorProofAsync(Guid.NewGuid(), revision,
                "[\"alpha\",\"beta\"]", "index", "item", WorkflowDataTypes.String,
                evt => { events.Add(evt); return ValueTask.CompletedTask; });

        Assert.False(result.Failed);
        Assert.Equal("beta", result.Output);
        Assert.Equal(new[] { ("alpha", "0"), ("beta", "1") }, agent.Invocations
            .Select(invocation => (invocation.Input, invocation.Parameters!["index"])));
        Assert.Equal(new[] { "array-loop/1", "array-loop/2" }, events
            .Where(evt => evt.Kind == ExecutionEventKinds.LoopIterationStarted)
            .Select(evt => evt.ScopeId));
        Assert.Equal(new[] { "array-loop/1", "array-loop/2" }, events
            .Where(evt => evt.Kind == ExecutionEventKinds.NodeCompleted && evt.NodeId == "agent")
            .Select(evt => evt.ScopeId));
        Assert.Single(events, evt => evt.Kind == ExecutionEventKinds.Started && evt.NodeId is null);
        AssertTerminalAndContiguous(events, ExecutionEventKinds.Completed);
        Assert.Equal("completed", JsonDocument.Parse(events.Last(evt =>
            evt.Kind == ExecutionEventKinds.LoopIterationCompleted).Data!).RootElement
            .GetProperty("outcome").GetString());
    }

    [Fact]
    public async Task Multi_item_ordinary_coordinator_shares_root_body_pass_budget()
    {
        var revision = CreateOrdinaryRevision("bounded-multi-item-ordinary-proof");
        var agent = new CapturingAgentExecutor();
        var events = new List<ExecutionEvent>();

        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => new WorkflowRunner(agents: agent)
            .ExecuteMultiItemOrdinaryActivationCoordinatorProofAsync(Guid.NewGuid(), revision,
                "[\"alpha\",\"beta\",\"gamma\"]", "index", "item", WorkflowDataTypes.String,
                evt => { events.Add(evt); return ValueTask.CompletedTask; }, maximumLoopPasses: 2));

        Assert.Equal(new[] { "alpha", "beta" }, agent.Invocations.Select(invocation => invocation.Input));
        Assert.Equal(new[] { "array-loop/1", "array-loop/2" }, events
            .Where(evt => evt.Kind == ExecutionEventKinds.LoopIterationStarted)
            .Select(evt => evt.ScopeId));
        Assert.DoesNotContain(events, evt => evt.Kind == ExecutionEventKinds.LoopIterationStarted &&
            evt.ScopeId == "array-loop/3");
        Assert.Single(events, evt => evt.Kind == ExecutionEventKinds.Failed && evt.NodeId is null);
        Assert.DoesNotContain(events, evt => evt.Kind == ExecutionEventKinds.Completed && evt.NodeId is null);
        AssertTerminalAndContiguous(events, ExecutionEventKinds.Failed);
    }

    [Fact]
    public async Task Multi_item_ordinary_coordinator_stops_admission_after_body_failure()
    {
        var revision = CreateOrdinaryRevision("failed-multi-item-ordinary-proof");
        var agent = new CapturingAgentExecutor(failOnInput: "beta");
        var events = new List<ExecutionEvent>();

        var result = await new WorkflowRunner(agents: agent)
            .ExecuteMultiItemOrdinaryActivationCoordinatorProofAsync(Guid.NewGuid(), revision,
                "[\"alpha\",\"beta\",\"gamma\"]", "index", "item", WorkflowDataTypes.String,
                evt => { events.Add(evt); return ValueTask.CompletedTask; });

        Assert.True(result.Failed);
        Assert.Equal(new[] { "alpha", "beta" }, agent.Invocations.Select(invocation => invocation.Input));
        Assert.Equal(new[] { "completed", "failed" }, events
            .Where(evt => evt.Kind == ExecutionEventKinds.LoopIterationCompleted)
            .Select(evt => JsonDocument.Parse(evt.Data!).RootElement.GetProperty("outcome").GetString()));
        AssertTerminalAndContiguous(events, ExecutionEventKinds.Failed);
    }

    [Fact]
    public async Task Multi_item_ordinary_coordinator_stops_admission_on_cancellation()
    {
        using var cancellation = new CancellationTokenSource();
        var revision = CreateOrdinaryRevision("cancelled-multi-item-ordinary-proof");
        var agent = new CapturingAgentExecutor(cancelOnInput: cancellation);
        var events = new List<ExecutionEvent>();

        var result = await new WorkflowRunner(agents: agent)
            .ExecuteMultiItemOrdinaryActivationCoordinatorProofAsync(Guid.NewGuid(), revision,
                "[\"alpha\",\"beta\",\"gamma\"]", "index", "item", WorkflowDataTypes.String,
                evt => { events.Add(evt); return ValueTask.CompletedTask; }, cancellation.Token);

        Assert.True(result.Cancelled);
        Assert.Equal(new[] { "alpha", "beta" }, agent.Invocations.Select(invocation => invocation.Input));
        Assert.Equal(new[] { "completed", "cancelled" }, events
            .Where(evt => evt.Kind == ExecutionEventKinds.LoopIterationCompleted)
            .Select(evt => JsonDocument.Parse(evt.Data!).RootElement.GetProperty("outcome").GetString()));
        AssertTerminalAndContiguous(events, ExecutionEventKinds.Cancelled);
    }

    [Fact]
    public async Task Multi_item_ordinary_coordinator_consumes_break_for_the_active_frame_and_stops()
    {
        var revision = CreateOrdinaryBreakRevision("break-multi-item-ordinary-proof");
        var agent = new CapturingAgentExecutor();
        var events = new List<ExecutionEvent>();

        var result = await new WorkflowRunner(agents: agent)
            .ExecuteMultiItemOrdinaryActivationCoordinatorProofAsync(Guid.NewGuid(), revision,
                "[\"alpha\",\"stop\",\"gamma\"]", "index", "item", WorkflowDataTypes.String,
                evt => { events.Add(evt); return ValueTask.CompletedTask; });

        Assert.False(result.Failed);
        Assert.Equal("alpha", result.Output);
        Assert.Equal(new[] { "alpha" }, agent.Invocations.Select(invocation => invocation.Input));
        Assert.Equal(new[] { "completed", "break" }, events
            .Where(evt => evt.Kind == ExecutionEventKinds.LoopIterationCompleted)
            .Select(evt => JsonDocument.Parse(evt.Data!).RootElement.GetProperty("outcome").GetString()));

        var breakCompleted = Assert.Single(events, evt => evt.Kind == ExecutionEventKinds.NodeCompleted &&
            evt.NodeId == "break" && evt.ScopeId == "array-loop/2");
        using var requestDocument = JsonDocument.Parse(breakCompleted.Data!);
        var sourceFrameId = requestDocument.RootElement.GetProperty("sourceFrameId").GetGuid();
        var breakIteration = Assert.Single(events, evt => evt.Kind == ExecutionEventKinds.LoopIterationCompleted &&
            evt.ScopeId == "array-loop/2");
        using var settlement = JsonDocument.Parse(breakIteration.Data!);
        Assert.Equal("break", settlement.RootElement.GetProperty("outcome").GetString());
        Assert.Equal(sourceFrameId, settlement.RootElement.GetProperty("targetFrameId").GetGuid());
        Assert.DoesNotContain(events, evt => evt.Kind == ExecutionEventKinds.LoopIterationStarted &&
            evt.ScopeId == "array-loop/3");
        AssertTerminalAndContiguous(events, ExecutionEventKinds.Completed);
    }

    [Fact]
    public async Task Nested_ordinary_coordinator_breaks_nearest_frame_and_resumes_outer_items()
    {
        var revision = CreateOrdinaryBreakRevision("nested-break-multi-item-ordinary-proof");
        var agent = new CapturingAgentExecutor();
        var events = new List<ExecutionEvent>();

        var result = await new WorkflowRunner(agents: agent)
            .ExecuteNestedMultiItemOrdinaryBreakCoordinatorProofAsync(Guid.NewGuid(), revision,
                "[\"outer-a\",\"outer-b\"]", "[\"continue\",\"stop\",\"must-not-run\"]",
                "outerItem", "outerIndex", "item", "index", WorkflowDataTypes.String,
                evt => { events.Add(evt); return ValueTask.CompletedTask; });

        Assert.False(result.Failed);
        Assert.Equal("continue", result.Output);
        Assert.Equal(new[] { ("outer-a", "0"), ("outer-b", "1") }, agent.Invocations
            .Select(invocation => (invocation.Parameters!["outerItem"], invocation.Parameters["outerIndex"])));
        Assert.Equal(new[] { "continue", "continue" }, agent.Invocations.Select(invocation => invocation.Input));

        var outerStarts = events.Where(evt => evt.Kind == ExecutionEventKinds.LoopIterationStarted &&
            evt.NodeId == "outer-array").ToArray();
        Assert.Equal(new[] { "outer-array/1", "outer-array/2" }, outerStarts.Select(evt => evt.ScopeId));
        foreach (var outerIndex in new[] { 1, 2 })
        {
            var outerScope = $"outer-array/{outerIndex}";
            var innerScope = $"{outerScope}/inner-array/2";
            Assert.DoesNotContain(events, evt => evt.Kind == ExecutionEventKinds.LoopIterationStarted &&
                evt.ScopeId == $"{outerScope}/inner-array/3");

            var breakCompleted = Assert.Single(events, evt => evt.Kind == ExecutionEventKinds.NodeCompleted &&
                evt.NodeId == "break" && evt.ScopeId == innerScope);
            using var request = JsonDocument.Parse(breakCompleted.Data!);
            var sourceFrameId = request.RootElement.GetProperty("sourceFrameId").GetGuid();
            var innerStarted = Assert.Single(events, evt => evt.Kind == ExecutionEventKinds.LoopIterationStarted &&
                evt.NodeId == "inner-array" && evt.ScopeId == innerScope);
            Assert.Equal(sourceFrameId, innerStarted.InvocationId);

            var outerStarted = Assert.Single(outerStarts, evt => evt.ScopeId == outerScope);
            var innerSettled = Assert.Single(events, evt => evt.Kind == ExecutionEventKinds.LoopIterationCompleted &&
                evt.NodeId == "inner-array" && evt.ScopeId == innerScope);
            using var settlement = JsonDocument.Parse(innerSettled.Data!);
            Assert.Equal("break", settlement.RootElement.GetProperty("outcome").GetString());
            Assert.Equal(sourceFrameId, settlement.RootElement.GetProperty("targetFrameId").GetGuid());
            Assert.NotEqual(outerStarted.InvocationId, settlement.RootElement.GetProperty("targetFrameId").GetGuid());
        }

        Assert.Equal(2, events.Count(evt => evt.Kind == ExecutionEventKinds.LoopIterationCompleted &&
            evt.NodeId == "outer-array"));
        AssertTerminalAndContiguous(events, ExecutionEventKinds.Completed);
    }

    [Fact]
    public async Task Ordinary_coordinator_break_drains_admitted_parallel_branch_before_settling_item()
    {
        var revision = CreateOrdinaryForkBreakRevision("ordinary-fork-break-drain-proof");
        var agent = new ForkBreakDrainAgentExecutor();
        var events = new ConcurrentQueue<ExecutionEvent>();
        var breakCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        ValueTask Record(ExecutionEvent evt)
        {
            events.Enqueue(evt);
            if (evt.Kind == ExecutionEventKinds.NodeCompleted && evt.NodeId == "break")
                breakCompleted.TrySetResult();
            return ValueTask.CompletedTask;
        }

        var execution = new WorkflowRunner(agents: agent)
            .ExecuteMultiItemOrdinaryActivationCoordinatorProofAsync(Guid.NewGuid(), revision,
                "[\"break\",\"must-not-run\"]", "index", "item", WorkflowDataTypes.String, Record);

        try
        {
            await agent.HeldBranchStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            try { await breakCompleted.Task.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (TimeoutException)
            {
                throw new Xunit.Sdk.XunitException("The Break node did not settle. Execution completed: " +
                    execution.IsCompleted + ". Fault: " + execution.Exception?.GetBaseException() + ". Events: " +
                    string.Join(" | ", events.Select(evt => $"{evt.Kind}:{evt.NodeId}:{evt.ScopeId}:{evt.Data}")));
            }

            Assert.False(execution.IsCompleted,
                "The item cannot settle while its already-admitted sibling branch is still executing.");
            Assert.DoesNotContain(events, evt => evt.Kind == ExecutionEventKinds.LoopIterationCompleted);

            agent.ReleaseHeldBranch.TrySetResult();
            var result = await execution.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.False(result.Failed);
            Assert.False(result.Cancelled);
            Assert.Equal(["break"], agent.Invocations.Select(invocation => invocation.Input));
            Assert.Contains(events, evt => evt.Kind == ExecutionEventKinds.NodeCompleted &&
                evt.NodeId == "held-agent" && evt.ScopeId == "array-loop/1");
            Assert.DoesNotContain(events, evt => evt.ScopeId == "array-loop/2");
            Assert.Single(events, evt => evt.Kind == ExecutionEventKinds.LoopIterationCompleted &&
                evt.ScopeId == "array-loop/1" &&
                JsonDocument.Parse(evt.Data!).RootElement.GetProperty("outcome").GetString() == "break");
            AssertTerminalAndContiguous(events.ToArray(), ExecutionEventKinds.Completed);
        }
        finally
        {
            agent.ReleaseHeldBranch.TrySetResult();
            if (!execution.IsCompleted)
            {
                try { await execution.WaitAsync(TimeSpan.FromSeconds(5)); }
                catch (TimeoutException) { }
            }
        }
    }

    [Fact]
    public async Task Ordinary_coordinator_sibling_unknown_tool_outcome_overrides_break()
    {
        var revision = CreateOrdinaryForkUnknownOutcomeRevision("ordinary-fork-break-unknown-proof");
        var tools = new HeldUnknownOutcomeBroker();
        var events = new ConcurrentQueue<ExecutionEvent>();
        var breakCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ValueTask Record(ExecutionEvent evt)
        {
            events.Enqueue(evt);
            if (evt.Kind == ExecutionEventKinds.NodeCompleted && evt.NodeId == "break")
                breakCompleted.TrySetResult();
            return ValueTask.CompletedTask;
        }

        var execution = new WorkflowRunner(tools: tools)
            .ExecuteMultiItemOrdinaryActivationCoordinatorProofAsync(Guid.NewGuid(), revision,
                "[\"item\",\"must-not-run\"]", "index", "item", WorkflowDataTypes.String, Record);
        try
        {
            await tools.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await breakCompleted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(execution.IsCompleted,
                "The Break cannot settle while the sibling tool call still has an unknown outcome.");

            tools.Release.TrySetResult();
            var result = await execution.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.True(result.Failed);
            Assert.True(result.OutcomeUnknown);
            Assert.DoesNotContain(events, evt => evt.Kind == ExecutionEventKinds.Completed && evt.NodeId is null);
            Assert.Single(events, evt => evt.Kind == ExecutionEventKinds.OutcomeUnknown && evt.NodeId is null);
            Assert.True(events.Any(evt => evt.Kind == ExecutionEventKinds.NodeFailed && evt.NodeId == "held-tool"),
                "Expected the held MCP node to fail with the unresolved tool exception. Events: " +
                string.Join(" | ", events.Select(evt => $"{evt.Kind}:{evt.NodeId}:{evt.ScopeId}:{evt.Data}")));
            Assert.DoesNotContain(events, evt => evt.Kind == ExecutionEventKinds.NodeCompleted && evt.NodeId == "join");
            Assert.DoesNotContain(events, evt => evt.ScopeId == "array-loop/2");
            Assert.Single(events, evt => evt.Kind == ExecutionEventKinds.LoopIterationCompleted &&
                evt.ScopeId == "array-loop/1" &&
                JsonDocument.Parse(evt.Data!).RootElement.GetProperty("outcome").GetString() == "outcome-unknown");
            AssertTerminalAndContiguous(events.ToArray(), ExecutionEventKinds.OutcomeUnknown);
        }
        finally
        {
            tools.Release.TrySetResult();
            if (!execution.IsCompleted)
            {
                try { await execution.WaitAsync(TimeSpan.FromSeconds(5)); }
                catch (TimeoutException) { }
            }
        }
    }

    [Fact]
    public async Task Single_item_coordinator_binds_the_item_and_completes_one_sequence_activation()
    {
        var revision = CreateSequenceRevision("single-item-coordinator-proof");
        var agent = new CapturingAgentExecutor();
        var events = new List<ExecutionEvent>();

        var result = await new WorkflowRunner(agents: agent)
            .ExecuteMultiItemSequenceActivationCoordinatorProofAsync(Guid.NewGuid(), revision,
                "[\"only\"]", "index", "item", WorkflowDataTypes.String,
                evt => { events.Add(evt); return ValueTask.CompletedTask; });

        Assert.False(result.Failed);
        Assert.Equal("only", result.Output);
        Assert.Equal(2, agent.Invocations.Length);
        Assert.All(agent.Invocations, invocation =>
        {
            Assert.Equal("only", invocation.Parameters!["item"]);
            Assert.Equal("0", invocation.Parameters["index"]);
        });
        Assert.Single(events, evt => evt.Kind == ExecutionEventKinds.LoopIterationStarted &&
            evt.ScopeId == "array-loop/1");
        Assert.Single(events, evt => evt.Kind == ExecutionEventKinds.SequenceCompleted &&
            evt.ScopeId == "array-loop/1");
        AssertTerminalAndContiguous(events, ExecutionEventKinds.Completed);
    }

    [Fact]
    public async Task Empty_array_has_one_root_lifecycle_and_does_not_dispatch_a_body()
    {
        var revision = CreateSequenceRevision("empty-sequence-proof");
        var agent = new CapturingAgentExecutor();
        var events = new List<ExecutionEvent>();

        var result = await new WorkflowRunner(agents: agent)
            .ExecuteMultiItemSequenceActivationCoordinatorProofAsync(Guid.NewGuid(), revision,
                "[]", "index", "item", WorkflowDataTypes.String,
                evt => { events.Add(evt); return ValueTask.CompletedTask; });

        Assert.False(result.Failed);
        Assert.False(result.HasOutput);
        Assert.Empty(agent.Invocations);
        Assert.DoesNotContain(events, evt => evt.Kind.StartsWith("sequence.", StringComparison.Ordinal));
        Assert.DoesNotContain(events, evt => evt.Kind is ExecutionEventKinds.LoopIterationStarted or
            ExecutionEventKinds.LoopIterationCompleted);
        AssertTerminalAndContiguous(events, ExecutionEventKinds.Completed);
    }

    [Fact]
    public async Task Item_failure_prevents_later_items_and_owns_one_root_failure()
    {
        var revision = CreateSequenceRevision("failed-sequence-proof");
        var agent = new CapturingAgentExecutor(failOnInput: "beta");
        var events = new List<ExecutionEvent>();

        var result = await new WorkflowRunner(agents: agent)
            .ExecuteMultiItemSequenceActivationCoordinatorProofAsync(Guid.NewGuid(), revision,
                "[\"alpha\",\"beta\",\"gamma\"]", "index", "item", WorkflowDataTypes.String,
                evt => { events.Add(evt); return ValueTask.CompletedTask; });

        Assert.True(result.Failed);
        Assert.Equal(new[] { "alpha", "alpha", "beta" }, agent.Invocations.Select(invocation => invocation.Input));
        Assert.DoesNotContain(agent.Invocations, invocation => invocation.Input == "gamma");
        Assert.Single(events, evt => evt.Kind == ExecutionEventKinds.Failed && evt.NodeId is null);
        Assert.DoesNotContain(events, evt => evt.Kind == ExecutionEventKinds.Completed && evt.NodeId is null);
        Assert.Contains(events, evt => evt.Kind == ExecutionEventKinds.LoopIterationCompleted &&
            evt.ScopeId == "array-loop/2" &&
            JsonDocument.Parse(evt.Data!).RootElement.GetProperty("outcome").GetString() == "failed");
        AssertTerminalAndContiguous(events, ExecutionEventKinds.Failed);
    }

    [Fact]
    public async Task Cancellation_during_an_item_prevents_later_items_and_owns_one_root_cancellation()
    {
        var revision = CreateSequenceRevision("cancelled-sequence-proof");
        using var cancellation = new CancellationTokenSource();
        var agent = new CapturingAgentExecutor(cancelOnInput: cancellation);
        var events = new List<ExecutionEvent>();

        var result = await new WorkflowRunner(agents: agent)
            .ExecuteMultiItemSequenceActivationCoordinatorProofAsync(Guid.NewGuid(), revision,
                "[\"alpha\",\"beta\",\"gamma\"]", "index", "item", WorkflowDataTypes.String,
                evt => { events.Add(evt); return ValueTask.CompletedTask; }, cancellation.Token);

        Assert.True(result.Cancelled);
        Assert.Equal(new[] { "alpha", "alpha", "beta" }, agent.Invocations.Select(invocation => invocation.Input));
        Assert.DoesNotContain(agent.Invocations, invocation => invocation.Input == "gamma");
        Assert.Single(events, evt => evt.Kind == ExecutionEventKinds.Cancelled && evt.NodeId is null);
        Assert.DoesNotContain(events, evt => evt.NodeId is null &&
            evt.Kind is ExecutionEventKinds.Completed or ExecutionEventKinds.Failed);
        Assert.Contains(events, evt => evt.Kind == ExecutionEventKinds.LoopIterationCompleted &&
            evt.ScopeId == "array-loop/2" &&
            JsonDocument.Parse(evt.Data!).RootElement.GetProperty("outcome").GetString() == "cancelled");
        AssertTerminalAndContiguous(events, ExecutionEventKinds.Cancelled);
    }

    [Fact]
    public async Task Rejected_sequence_evidence_keeps_only_the_accepted_root_prefix_without_a_terminal()
    {
        var revision = CreateSequenceRevision("rejected-sequence-evidence-proof");
        var events = new List<ExecutionEvent>();
        var rejection = new IOException("Controlled sequence evidence rejection.");
        var rejected = false;

        var failure = await Record.ExceptionAsync(() => new WorkflowRunner(agents: new CapturingAgentExecutor())
            .ExecuteMultiItemSequenceActivationCoordinatorProofAsync(Guid.NewGuid(), revision,
                "[\"alpha\",\"beta\"]", "index", "item", WorkflowDataTypes.String,
                evt =>
                {
                    if (evt.Kind == ExecutionEventKinds.SequenceOutputCompleted && evt.ScopeId == "array-loop/1")
                    {
                        rejected = true;
                        return ValueTask.FromException(rejection);
                    }
                    events.Add(evt);
                    return ValueTask.CompletedTask;
                }));

        Assert.NotNull(failure);
        Assert.True(rejected);
        Assert.DoesNotContain(events, evt => evt.NodeId is null &&
            evt.Kind is ExecutionEventKinds.Completed or ExecutionEventKinds.Failed or
                ExecutionEventKinds.Cancelled or ExecutionEventKinds.Terminated);
        Assert.DoesNotContain(events, evt => evt.Kind == ExecutionEventKinds.LoopIterationStarted &&
            evt.ScopeId == "array-loop/2");
        Assert.Equal(Enumerable.Range(1, events.Count).Select(value => (long)value),
            events.Select(evt => evt.Sequence));
    }

    private static WorkflowRevision CreateSequenceRevision(string workflowId)
    {
        var definition = new WorkflowDefinition(WorkflowSchemaVersions.UnifiedWorkflowGraph, workflowId, "input",
            ImmutableSortedDictionary<string, NodeDefinition>.Empty
                .Add("input", new(NodeTypes.Input, "1.0.0"))
                .Add("sequence", new(NodeTypes.Sequence, "1.0.0")
                {
                    Sequence = new([new("body", "Body"), new("follow-up", "Follow-up")])
                })
                .Add("agent", new(NodeTypes.Agent, "1.0.0", Agent: new AgentConfiguration(
                    "proof.echo", null, "Return the input unchanged.", []) { MaxTurns = 1 }))
                .Add("follow-up-agent", new(NodeTypes.Agent, "1.0.0", Agent: new AgentConfiguration(
                    "proof.echo", null, "Return the input unchanged.", []) { MaxTurns = 1 }))
                .Add("output", new(NodeTypes.Output, "1.0.0")),
            ImmutableSortedDictionary<string, ConnectionDefinition>.Empty
                .Add("input-agent", Edge("input", "out", "agent", "in", "data"))
                .Add("input-follow-up-agent", Edge("input", "out", "follow-up-agent", "in", "data"))
                .Add("agent-output-data", Edge("agent", "out", "output", "in", "data")))
        {
            Parameters = ImmutableSortedDictionary<string, WorkflowParameterDefinition>.Empty
                .Add("index", new("Index", "0") { Type = WorkflowDataTypes.Integer })
                .Add("item", new("Item", "") { Type = WorkflowDataTypes.String }),
            ExecutionConnections = ImmutableSortedDictionary<string, ConnectionDefinition>.Empty
                .Add("input-sequence", Edge("input", "then", "sequence", "execute"))
                .Add("sequence-agent", Edge("sequence", "then.body", "agent", "execute"))
                .Add("sequence-follow-up-agent", Edge("sequence", "then.follow-up", "follow-up-agent", "execute"))
                .Add("agent-output", Edge("agent", "out", "output", "execute"))
                .Add("follow-up-output", Edge("follow-up-agent", "out", "output", "execute"))
        };
        return WorkflowRevision.Create(definition, workflowId);
    }

    private static WorkflowRevision CreateOrdinaryRevision(string workflowId)
    {
        var definition = new WorkflowDefinition(WorkflowSchemaVersions.UnifiedWorkflowGraph, workflowId, "input",
            ImmutableSortedDictionary<string, NodeDefinition>.Empty
                .Add("input", new(NodeTypes.Input, "1.0.0"))
                .Add("item", new(NodeTypes.WorkflowValue, "1.0.0")
                    { ParameterId = "item", OutputDataType = WorkflowDataTypes.String })
                .Add("agent", new(NodeTypes.Agent, "1.0.0", Agent: new AgentConfiguration(
                    "proof.echo", null, "Return the input unchanged.", []) { MaxTurns = 1 }))
                .Add("output", new(NodeTypes.Output, "1.0.0")),
            ImmutableSortedDictionary<string, ConnectionDefinition>.Empty
                .Add("item-agent", Edge("item", "out", "agent", "in", "data"))
                .Add("agent-output-data", Edge("agent", "out", "output", "in", "data")))
        {
            Parameters = ImmutableSortedDictionary<string, WorkflowParameterDefinition>.Empty
                .Add("index", new("Index", "0") { Type = WorkflowDataTypes.Integer })
                .Add("item", new("Item", "") { Type = WorkflowDataTypes.String }),
            ExecutionConnections = ImmutableSortedDictionary<string, ConnectionDefinition>.Empty
                .Add("input-agent", Edge("input", "then", "agent", "execute"))
                .Add("agent-output", Edge("agent", "out", "output", "execute"))
        };
        return WorkflowRevision.Create(definition, workflowId);
    }

    private static WorkflowRevision CreateOrdinaryBreakRevision(string workflowId)
    {
        var definition = new WorkflowDefinition(WorkflowSchemaVersions.UnifiedWorkflowGraph, workflowId, "input",
            ImmutableSortedDictionary<string, NodeDefinition>.Empty
                .Add("input", new(NodeTypes.Input, "1.0.0"))
                .Add("item", new(NodeTypes.WorkflowValue, "1.0.0")
                    { ParameterId = "item", OutputDataType = WorkflowDataTypes.String })
                .Add("stop-value", new(NodeTypes.StringConstant, "1.0.0")
                    { StringConstant = new("stop") })
                .Add("equals", new(NodeTypes.StringEquals, "1.0.0"))
                .Add("branch", new(NodeTypes.BooleanBranch, "1.0.0"))
                .Add("break", new(WorkflowRunner.StructuredProofBreakNodeType, "proof-only"))
                .Add("agent", new(NodeTypes.Agent, "1.0.0", Agent: new AgentConfiguration(
                    "proof.echo", null, "Return the input unchanged.", []) { MaxTurns = 1 }))
                .Add("output", new(NodeTypes.Output, "1.0.0")),
            ImmutableSortedDictionary<string, ConnectionDefinition>.Empty
                .Add("item-equals", Edge("item", "out", "equals", "left", "data"))
                .Add("stop-equals", Edge("stop-value", "out", "equals", "right", "data"))
                .Add("equals-branch", Edge("equals", "out", "branch", "condition", "data"))
                .Add("item-agent", Edge("item", "out", "agent", "in", "data"))
                .Add("agent-output-data", Edge("agent", "out", "output", "in", "data")))
        {
            Parameters = ImmutableSortedDictionary<string, WorkflowParameterDefinition>.Empty
                .Add("index", new("Index", "0") { Type = WorkflowDataTypes.Integer })
                .Add("item", new("Item", "") { Type = WorkflowDataTypes.String })
                .Add("outerIndex", new("Outer index", "0") { Type = WorkflowDataTypes.Integer })
                .Add("outerItem", new("Outer item", "") { Type = WorkflowDataTypes.String }),
            ExecutionConnections = ImmutableSortedDictionary<string, ConnectionDefinition>.Empty
                .Add("input-branch", Edge("input", "then", "branch", "execute"))
                .Add("branch-break", Edge("branch", "true", "break", "execute"))
                .Add("branch-agent", Edge("branch", "false", "agent", "execute"))
                .Add("break-output", Edge("break", "out", "output", "execute"))
                .Add("agent-output", Edge("agent", "out", "output", "execute"))
        };
        return WorkflowRevision.CreateProofOnly(definition, workflowId);
    }

    private static WorkflowRevision CreateOrdinaryForkBreakRevision(string workflowId)
    {
        var branches = ImmutableArray.Create(
            new ParallelBranchDescriptor("break", "Break", "break-anchor"),
            new ParallelBranchDescriptor("held", "Held", "held-anchor"));
        var nodes = ImmutableSortedDictionary<string, NodeDefinition>.Empty
            .Add("input", new(NodeTypes.Input, "1.0.0"))
            .Add("fork", new(NodeTypes.ParallelFork, "1.0.0") { ParallelFork = new(branches) })
            .Add("break-anchor", new(NodeTypes.ParallelBranch, "1.0.0")
                { ParallelBranch = new("fork", "break") })
            .Add("held-anchor", new(NodeTypes.ParallelBranch, "1.0.0")
                { ParallelBranch = new("fork", "held") })
            .Add("break", new(WorkflowRunner.StructuredProofBreakNodeType, "proof-only"))
            .Add("held-agent", new(NodeTypes.Agent, "1.0.0",
                Agent: new AgentConfiguration("proof.fork-drain", null, "Wait for release", []) { MaxTurns = 1 }))
            .Add("join", new(NodeTypes.ParallelJoin, "1.0.0")
                { ParallelJoin = new("fork", ["break", "held"]) })
            .Add("output", new(NodeTypes.Output, "1.0.0") { OutputDataType = WorkflowDataTypes.Json });
        var connections = ImmutableSortedDictionary<string, ConnectionDefinition>.Empty
            .Add("input-held-agent", Edge("input", "out", "held-agent", "in", "data"))
            .Add("join-output-data", Edge("join", "out", "output", "in", "data"));
        var execution = ImmutableSortedDictionary<string, ConnectionDefinition>.Empty
            .Add("input-fork", Edge("input", "then", "fork", "execute"))
            .Add("fork-break", Edge("fork", ParallelBranchLimits.PinId("break"), "break-anchor", "execute"))
            .Add("fork-held", Edge("fork", ParallelBranchLimits.PinId("held"), "held-anchor", "execute"))
            .Add("break-request", Edge("break-anchor", "then", "break", "execute"))
            .Add("break-join", Edge("break", "out", "join", "branch.break"))
            .Add("held-agent", Edge("held-anchor", "then", "held-agent", "execute"))
            .Add("held-join", Edge("held-agent", "out", "join", "branch.held"))
            .Add("join-output", Edge("join", "then", "output", "execute"));
        var definition = new WorkflowDefinition(WorkflowSchemaVersions.UnifiedWorkflowGraph, workflowId, "input",
            nodes, connections)
        {
            Parameters = ImmutableSortedDictionary<string, WorkflowParameterDefinition>.Empty
                .Add("index", new("Index", "0") { Type = WorkflowDataTypes.Integer })
                .Add("item", new("Item", "") { Type = WorkflowDataTypes.String }),
            ExecutionConnections = execution
        };
        return WorkflowRevision.CreateProofOnly(definition, workflowId);
    }

    private static WorkflowRevision CreateOrdinaryForkUnknownOutcomeRevision(string workflowId)
    {
        const string bindingId = "proof.unknown-tool";
        var revision = CreateOrdinaryForkBreakRevision(workflowId);
        var definition = revision.Definition;
        var nodes = definition.Nodes.Remove("held-agent").Add("held-tool",
            new NodeDefinition(NodeTypes.McpTool, "1.0.0",
                new ToolBindingRequest(bindingId, new string('a', 64))) { McpArgumentsJson = "{}" });
        var connections = definition.Connections.Remove("input-held-agent")
            .Add("input-held-tool", Edge("input", "out", "held-tool", "in", "data"));
        var execution = definition.ExecutionConnections!.Remove("held-agent").Remove("held-join")
            .Add("held-tool", Edge("held-anchor", "then", "held-tool", "execute"))
            .Add("tool-join", Edge("held-tool", "then", "join", "branch.held"));
        return WorkflowRevision.CreateProofOnly(definition with
        {
            Nodes = nodes,
            Connections = connections,
            ExecutionConnections = execution
        }, workflowId);
    }

    private static void AssertTerminalAndContiguous(IReadOnlyList<ExecutionEvent> events, string terminalKind)
    {
        Assert.Single(events, evt => evt.NodeId is null && evt.Kind == terminalKind);
        Assert.Equal(Enumerable.Range(1, events.Count).Select(value => (long)value),
            events.Select(evt => evt.Sequence));
        Assert.Equal(terminalKind, events[^1].Kind);
    }

    private static ConnectionDefinition Edge(string sourceNode, string sourcePort, string targetNode,
        string targetPort, string kind = "execution") =>
        new(new(sourceNode, sourcePort), new(targetNode, targetPort), kind);

    private sealed class CapturingAgentExecutor : IAgentNodeExecutor
    {
        private readonly ConcurrentQueue<Invocation> invocations = new();
        public Invocation[] Invocations => [.. invocations];

        public sealed record Invocation(string NodeId, string Input,
            IReadOnlyDictionary<string, string>? Parameters);

        public CapturingAgentExecutor(string? failOnInput = null, CancellationTokenSource? cancelOnInput = null,
            string? holdNodeId = null)
        {
            this.failOnInput = failOnInput;
            this.cancelOnInput = cancelOnInput;
            this.holdNodeId = holdNodeId;
        }

        private readonly string? failOnInput;
        private readonly CancellationTokenSource? cancelOnInput;
        private readonly string? holdNodeId;
        public TaskCompletionSource HeldAgentStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource OtherAgentStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseHeldAgent { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<IReadOnlyList<AgentProviderDescriptor>> ListProvidersAsync() =>
            Task.FromResult<IReadOnlyList<AgentProviderDescriptor>>(
                [new("proof.echo", "Proof Echo", "proof", true)]);

        public async Task<string> ExecuteAsync(NodeInvocationContext invocation, string input,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            invocations.Enqueue(new(invocation.NodeId, input, invocation.Parameters));
            if (invocation.NodeId == holdNodeId)
            {
                HeldAgentStarted.TrySetResult();
                await ReleaseHeldAgent.Task.WaitAsync(cancellationToken);
            }
            else if (holdNodeId is not null)
            {
                OtherAgentStarted.TrySetResult();
            }
            if (input == failOnInput)
                throw new InvalidOperationException("Deterministic proof failure.");
            if (input == "beta" && cancelOnInput is not null)
            {
                cancelOnInput.Cancel();
                cancellationToken.ThrowIfCancellationRequested();
            }
            return input;
        }
    }

    private sealed class ForkBreakDrainAgentExecutor : IAgentNodeExecutor
    {
        private readonly ConcurrentQueue<(string NodeId, string Input)> invocations = new();
        public IReadOnlyList<(string NodeId, string Input)> Invocations => [.. invocations];
        public TaskCompletionSource HeldBranchStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseHeldBranch { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<IReadOnlyList<AgentProviderDescriptor>> ListProvidersAsync() =>
            Task.FromResult<IReadOnlyList<AgentProviderDescriptor>>(
                [new("proof.fork-drain", "Fork Drain Proof", "proof", true)]);

        public async Task<string> ExecuteAsync(NodeInvocationContext invocation, string input,
            CancellationToken cancellationToken = default)
        {
            invocations.Enqueue((invocation.NodeId, input));
            if (invocation.NodeId == "held-agent")
            {
                HeldBranchStarted.TrySetResult();
                await ReleaseHeldBranch.Task.WaitAsync(cancellationToken);
            }
            return input;
        }
    }

    private sealed class HeldUnknownOutcomeBroker : INodeToolBroker
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<IReadOnlyList<ToolBindingDescriptor>> ListBindingsAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ToolBindingDescriptor>>([
                new("proof.unknown-tool", "Unknown outcome test", "proof", "unknown", new string('a', 64))
            ]);

        public INodeToolScope OpenScope(NodeInvocationContext invocation) => new Scope(this);

        private sealed class Scope(HeldUnknownOutcomeBroker owner) : INodeToolScope
        {
            public Task<IReadOnlyList<ToolBindingDescriptor>> ListToolsAsync(
                CancellationToken cancellationToken = default) =>
                Task.FromResult<IReadOnlyList<ToolBindingDescriptor>>([]);

            public Task<string> InvokeAsync(string bindingId, string input,
                CancellationToken cancellationToken = default) => InvokeUnknownAsync(cancellationToken);

            public Task<string> InvokeJsonAsync(string bindingId, JsonElement arguments,
                CancellationToken cancellationToken = default) => InvokeUnknownAsync(cancellationToken);

            private async Task<string> InvokeUnknownAsync(CancellationToken cancellationToken)
            {
                owner.Started.TrySetResult();
                await owner.Release.Task.WaitAsync(cancellationToken);
                throw new ToolInvocationException("The sibling tool outcome is unknown.",
                    ToolInvocationFailureDisposition.OutcomeUnknown);
            }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
