using System.Collections.Immutable;
using System.Text.Json;
using VisualAgentWorkflow.Core;
using Xunit;

namespace VisualAgentWorkflow.AgentFramework.Tests;

public sealed class MutableVariableExecutionTests
{
    [Fact]
    public async Task Set_then_get_reads_the_committed_value_and_records_state_transitions()
    {
        var revision = WorkflowRevision.Create(Definition(), "variable-proof");
        var events = new List<ExecutionEvent>();

        var result = await new WorkflowRunner().ExecuteAsync(Guid.NewGuid(), revision, "from input",
            evt => { events.Add(evt); return ValueTask.CompletedTask; });

        Assert.Equal("from input", result);
        var written = events.Single(evt => evt.Kind == ExecutionEventKinds.VariableWritten);
        var read = events.Single(evt => evt.Kind == ExecutionEventKinds.VariableRead);
        Assert.Equal("set", written.NodeId);
        Assert.Equal("get", read.NodeId);
        Assert.Equal("message", JsonDocument.Parse(written.Data!).RootElement.GetProperty("variableId").GetString());
        Assert.Equal("from input", JsonDocument.Parse(read.Data!).RootElement.GetProperty("value").GetString());
        Assert.Equal(0, JsonDocument.Parse(written.Data!).RootElement.GetProperty("loopIteration").GetInt32());
        Assert.Equal(0, JsonDocument.Parse(read.Data!).RootElement.GetProperty("loopIteration").GetInt32());
        Assert.Equal(JsonDocument.Parse(written.Data!).RootElement.GetProperty("version").GetInt64(),
            JsonDocument.Parse(read.Data!).RootElement.GetProperty("version").GetInt64());
    }

    [Fact]
    public async Task Separate_runs_have_isolated_variable_stores()
    {
        var revision = WorkflowRevision.Create(Definition(), "variable-isolation");
        var first = new List<ExecutionEvent>();
        var second = new List<ExecutionEvent>();

        var results = await Task.WhenAll(
            new WorkflowRunner().ExecuteAsync(Guid.NewGuid(), revision, "first", evt => { first.Add(evt); return ValueTask.CompletedTask; }),
            new WorkflowRunner().ExecuteAsync(Guid.NewGuid(), revision, "second", evt => { second.Add(evt); return ValueTask.CompletedTask; }));

        Assert.Equal(new[] { "first", "second" }, results.OrderBy(value => value).ToArray());
        Assert.Equal("first", JsonDocument.Parse(first.Single(evt => evt.Kind == ExecutionEventKinds.VariableRead).Data!).RootElement.GetProperty("value").GetString());
        Assert.Equal("second", JsonDocument.Parse(second.Single(evt => evt.Kind == ExecutionEventKinds.VariableRead).Data!).RootElement.GetProperty("value").GetString());
    }

    [Fact]
    public async Task Cancellation_after_a_committed_write_preserves_the_recorded_variable_prefix()
    {
        var revision = WorkflowRevision.Create(Definition(), "variable-cancellation-prefix");
        using var cancellation = new CancellationTokenSource();
        var events = new List<ExecutionEvent>();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new WorkflowRunner().ExecuteAsync(
            Guid.NewGuid(), revision, "committed", evt =>
            {
                events.Add(evt);
                if (evt.Kind == ExecutionEventKinds.VariableWritten) cancellation.Cancel();
                return ValueTask.CompletedTask;
            }, cancellation.Token));

        var written = events.Single(evt => evt.Kind == ExecutionEventKinds.VariableWritten);
        using var payload = JsonDocument.Parse(written.Data!);
        Assert.Equal("committed", payload.RootElement.GetProperty("value").GetString());
        Assert.True(VariableEvidence.TryReconstructPlayback(events, out var playback));
        Assert.Equal("committed", playback!.Values["message"].Value);
        Assert.Equal(2, playback.Values["message"].Version);
    }

    [Fact]
    public async Task Failure_after_a_committed_write_preserves_the_recorded_variable_prefix()
    {
        var revision = WorkflowRevision.Create(Definition(), "variable-failure-prefix");
        var events = new List<ExecutionEvent>();
        var injectFailure = true;

        await Assert.ThrowsAsync<InvalidOperationException>(() => new WorkflowRunner().ExecuteAsync(
            Guid.NewGuid(), revision, "committed", evt =>
            {
                events.Add(evt);
                if (injectFailure && evt.Kind == ExecutionEventKinds.NodeCompleted && evt.NodeId == "set")
                {
                    injectFailure = false;
                    throw new InvalidOperationException("injected recorder failure");
                }
                return ValueTask.CompletedTask;
            }));

        Assert.Contains(events, evt => evt.Kind == ExecutionEventKinds.VariableWritten && evt.NodeId == "set");
        Assert.True(VariableEvidence.TryReconstructPlayback(events, out var playback));
        Assert.Equal("committed", playback!.Values["message"].Value);
        Assert.Equal(2, playback.Values["message"].Version);
    }

    [Fact]
    public async Task Loop_can_write_and_read_a_run_scoped_variable_on_each_bounded_pass()
    {
        var revision = WorkflowRevision.Create(LoopDefinition(), "loop-variable-runtime");
        var events = new List<ExecutionEvent>();

        var result = await new WorkflowRunner().ExecuteAsync(Guid.NewGuid(), revision, "continue",
            evt => { events.Add(evt); return ValueTask.CompletedTask; });

        Assert.Equal("continue", result);
        Assert.Equal(3, events.Count(evt => evt.Kind == ExecutionEventKinds.LoopIterationStarted));
        Assert.Equal(3, events.Count(evt => evt.Kind == ExecutionEventKinds.LoopIterationCompleted));
        Assert.Equal(3, events.Count(evt => evt.Kind == ExecutionEventKinds.VariableWritten));
        Assert.Equal(3, events.Count(evt => evt.Kind == ExecutionEventKinds.VariableRead));
        Assert.Equal(3, events.Count(evt => evt.NodeId == "body" && evt.Kind == ExecutionEventKinds.NodeCompleted));
        Assert.Equal("maximum-iterations", JsonDocument.Parse(events.Single(evt => evt.Kind == ExecutionEventKinds.LoopStopped).Data!)
            .RootElement.GetProperty("reason").GetString());

        var writes = events.Where(evt => evt.Kind == ExecutionEventKinds.VariableWritten)
            .Select(evt => JsonDocument.Parse(evt.Data!).RootElement)
            .ToArray();
        Assert.Equal(new[] { 1, 2, 3 }, writes.Select(payload => payload.GetProperty("loopIteration").GetInt32()).ToArray());
    }

    [Fact]
    public async Task Native_checkpoint_proof_round_trips_a_loop_variable_boundary_without_admitting_recovery()
    {
        var revision = WorkflowRevision.Create(LoopDefinition(), "loop-checkpoint-proof");
        var runId = Guid.NewGuid();
        var events = new List<ExecutionEvent>();
        var first = new ProofCheckpointSink(events, suspendAfterCompletedNodes: 4);

        Assert.Null(await new WorkflowRunner().ExecuteCheckpointedProofAsync(runId, revision, "continue",
            evt => { events.Add(evt); return ValueTask.CompletedTask; }, first));

        var saved = Assert.IsType<NativeWorkflowCheckpoint>(first.Last);
        Assert.NotNull(saved.VariableState);
        Assert.Contains("loop", saved.Payload, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, events.Count(evt => evt.Kind == ExecutionEventKinds.VariableWritten));

        var resumed = new ProofCheckpointSink(events, restored: saved);
        Assert.Equal("continue", await new WorkflowRunner().ExecuteCheckpointedProofAsync(runId, revision, "continue",
            evt => { events.Add(evt); return ValueTask.CompletedTask; }, resumed));
        Assert.Equal(3, events.Count(evt => evt.Kind == ExecutionEventKinds.VariableWritten));
        Assert.Equal([2L, 3L, 4L], events.Where(evt => evt.Kind == ExecutionEventKinds.VariableWritten)
            .Select(evt => JsonDocument.Parse(evt.Data!).RootElement.GetProperty("version").GetInt64()));
    }

    [Fact]
    public async Task Public_checkpoint_path_keeps_schema_145_loop_recovery_fail_closed()
    {
        var revision = WorkflowRevision.Create(LoopDefinition(), "loop-checkpoint-admission");

        await Assert.ThrowsAsync<InvalidOperationException>(() => new WorkflowRunner().ExecuteCheckpointedAsync(
            Guid.NewGuid(), revision, "continue", _ => ValueTask.CompletedTask,
            new ProofCheckpointSink([])));
    }

    private static WorkflowDefinition Definition()
    {
        var nodes = new Dictionary<string, NodeDefinition>
        {
            ["input"] = new(NodeTypes.Input, "1.0.0"),
            ["set"] = new(NodeTypes.VariableSet, "1.0.0") { VariableId = "message", InputDataType = WorkflowDataTypes.String },
            ["get"] = new(NodeTypes.VariableGet, "1.0.0") { VariableId = "message", OutputDataType = WorkflowDataTypes.String },
            ["output"] = new(NodeTypes.Output, "1.0.0")
        };
        var data = new Dictionary<string, ConnectionDefinition>
        {
            ["input-value"] = new(new("input", "out"), new("set", "value"), "data"),
            ["get-value"] = new(new("get", "out"), new("output", "in"), "data")
        };
        var execution = new Dictionary<string, ConnectionDefinition>
        {
            ["input-set"] = new(new("input", "then"), new("set", "execute"), "execution"),
            ["set-get"] = new(new("set", "then"), new("get", "execute"), "execution"),
            ["get-output"] = new(new("get", "then"), new("output", "execute"), "execution")
        };
        var definition = new WorkflowDefinition(WorkflowSchemaVersions.MutableVariablesGraph, "mutable-variable-proof", "input",
            nodes.ToImmutableSortedDictionary(), data.ToImmutableSortedDictionary())
        {
            Variables = ImmutableSortedDictionary<string, WorkflowVariableDefinition>.Empty
                .Add("message", new("Message", "default")),
            ExecutionConnections = execution.ToImmutableSortedDictionary()
        };
        return definition;
    }

    private static WorkflowDefinition LoopDefinition()
    {
        var nodes = new Dictionary<string, NodeDefinition>
        {
            ["input"] = new(NodeTypes.Input, "1.0.0"),
            ["loop"] = new(NodeTypes.Loop, "1.0.0") { Loop = new(new("contains", "continue"), 3) },
            ["body"] = new(NodeTypes.LoopBody, "1.0.0"),
            ["set"] = new(NodeTypes.VariableSet, "1.0.0") { VariableId = "message", InputDataType = WorkflowDataTypes.String },
            ["get"] = new(NodeTypes.VariableGet, "1.0.0") { VariableId = "message", OutputDataType = WorkflowDataTypes.String },
            ["output"] = new(NodeTypes.Output, "1.0.0")
        };
        var data = new Dictionary<string, ConnectionDefinition>
        {
            ["input-value"] = new(new("input", "out"), new("set", "value"), "data"),
            ["get-value"] = new(new("get", "out"), new("output", "in"), "data")
        };
        var execution = new Dictionary<string, ConnectionDefinition>
        {
            ["input-loop"] = new(new("input", "then"), new("loop", "execute"), "execution"),
            ["loop-body"] = new(new("loop", "body"), new("body", "execute"), "execution"),
            ["body-set"] = new(new("body", "then"), new("set", "execute"), "execution"),
            ["set-get"] = new(new("set", "then"), new("get", "execute"), "execution"),
            ["get-loop"] = new(new("get", "then"), new("loop", "execute"), "execution"),
            ["loop-output"] = new(new("loop", "done"), new("output", "execute"), "execution")
        };
        return new WorkflowDefinition(WorkflowSchemaVersions.LoopVariablesGraph, "loop-variable-runtime", "input",
            nodes.ToImmutableSortedDictionary(), data.ToImmutableSortedDictionary())
        {
            Variables = ImmutableSortedDictionary<string, WorkflowVariableDefinition>.Empty
                .Add("message", new("Message", "default")),
            ExecutionConnections = execution.ToImmutableSortedDictionary()
        };
    }

    private sealed class ProofCheckpointSink(List<ExecutionEvent> events, int? suspendAfterCompletedNodes = null,
        NativeWorkflowCheckpoint? restored = null) : IWorkflowCheckpointSink
    {
        public NativeWorkflowCheckpoint? RestoredCheckpoint { get; } = restored;
        public NativeWorkflowCheckpoint? Last { get; private set; }

        public ValueTask<bool> CaptureAsync(NativeWorkflowCheckpoint checkpoint)
        {
            Last = checkpoint;
            var shouldSuspend = suspendAfterCompletedNodes is { } threshold &&
                events.Count(evt => evt.Kind == ExecutionEventKinds.NodeCompleted) >= threshold;
            return ValueTask.FromResult(shouldSuspend);
        }
    }
}
