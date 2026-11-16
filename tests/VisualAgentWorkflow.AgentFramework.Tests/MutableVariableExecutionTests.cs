using System.Collections.Immutable;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using VisualAgentWorkflow.Hosting;
using VisualAgentWorkflow.Core;
using Xunit;

namespace VisualAgentWorkflow.AgentFramework.Tests;

public sealed class MutableVariableExecutionTests
{
    [Theory]
    [InlineData("1.44")]
    [InlineData("2.0")]
    public async Task Set_then_get_reads_the_committed_value_and_records_state_transitions(string schemaVersion)
    {
        var revision = WorkflowRevision.Create(Definition() with { SchemaVersion = schemaVersion }, "variable-proof");
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
        Assert.True(VariableEvidence.TryReconstructPlayback(events, out var playback));
        Assert.Equal("from input", playback!.Values["message"].Value);
        Assert.Equal(2, playback.Values["message"].Version);
    }

    [Fact]
    public async Task Typed_array_transform_set_and_get_preserve_the_canonical_value_in_events_and_output()
    {
        var revision = WorkflowRevision.Create(ArrayVariableDefinition(), "typed-array-variable-runtime");
        var events = new List<ExecutionEvent>();

        var result = await new WorkflowRunner().ExecuteAsync(Guid.NewGuid(), revision, "unused",
            evt => { events.Add(evt); return ValueTask.CompletedTask; });

        Assert.Equal("[\"seed\",\"next\"]", result);
        var written = JsonDocument.Parse(events.Single(evt => evt.Kind == ExecutionEventKinds.VariableWritten).Data!);
        var read = JsonDocument.Parse(events.Single(evt => evt.Kind == ExecutionEventKinds.VariableRead).Data!);
        Assert.Equal("[\"seed\"]", written.RootElement.GetProperty("previousValue").GetRawText());
        Assert.Equal("[\"seed\",\"next\"]", written.RootElement.GetProperty("value").GetRawText());
        Assert.Equal(written.RootElement.GetProperty("value").GetRawText(), read.RootElement.GetProperty("value").GetRawText());
        Assert.True(VariableEvidence.TryReconstructPlayback(events, out var playback));
        Assert.Equal("[\"seed\",\"next\"]", playback!.Values["items"].Value);
    }

    [Fact]
    public async Task Typed_array_variable_updates_are_ordered_across_bounded_loop_iterations()
    {
        var revision = WorkflowRevision.Create(ArrayLoopVariableDefinition(), "array-loop-variable-runtime");
        var events = new List<ExecutionEvent>();

        var result = await new WorkflowRunner().ExecuteAsync(Guid.NewGuid(), revision, "continue",
            evt => { events.Add(evt); return ValueTask.CompletedTask; });

        Assert.Equal(3, events.Count(evt => evt.Kind == ExecutionEventKinds.VariableWritten));
        Assert.Equal("[\"next\",\"next\",\"next\"]", result);
        Assert.True(VariableEvidence.TryReconstructPlayback(events, out var playback));
        Assert.Equal("[\"next\",\"next\",\"next\"]", playback!.Values["items"].Value);
        Assert.Equal([1, 2, 3], playback.Operations.Where(operation => operation.Kind == "write")
            .Select(operation => operation.LoopIteration).ToArray());

        AssertEveryArrayVariableEvidencePrefixReconstructs(events);
    }

    [Theory]
    [InlineData("file")]
    [InlineData("sqlite")]
    public async Task Typed_array_variable_evidence_survives_store_reopen(string storeKind)
    {
        var directory = Path.Combine(Path.GetTempPath(), "vaw-array-variable-store-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        try
        {
            var revision = WorkflowRevision.Create(ArrayVariableDefinition(), "array-variable-store-reopen");
            IWorkflowRunStore store = storeKind == "sqlite"
                ? new SqliteWorkflowRunStore(Path.Combine(directory, "runs.sqlite3"))
                : new FileRunJournal(Path.Combine(directory, "runs"));
            var created = store.Create(revision, "unused");
            await new WorkflowRunner().ExecuteAsync(created.Header.RunId, revision, "unused",
                evt => { store.Append(evt); return ValueTask.CompletedTask; });

            IWorkflowRunStore reopened = storeKind == "sqlite"
                ? new SqliteWorkflowRunStore(Path.Combine(directory, "runs.sqlite3"))
                : new FileRunJournal(Path.Combine(directory, "runs"));
            var snapshot = reopened.Get(created.Header.RunId)!;

            Assert.Equal("completed", snapshot.Status);
            Assert.True(VariableEvidence.TryReconstructPlayback(snapshot.Events, out var playback));
            Assert.Equal("[\"seed\",\"next\"]", playback!.Values["items"].Value);
            AssertEveryArrayVariableEvidencePrefixReconstructs(snapshot.Events);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    [Theory]
    [InlineData("file")]
    [InlineData("sqlite")]
    public async Task Json_array_variable_get_set_preserves_compact_json_evidence_after_reopen(string storeKind)
    {
        var directory = Path.Combine(Path.GetTempPath(), "vaw-json-array-variable-store-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        try
        {
            var revision = WorkflowRevision.Create(JsonArrayVariableDefinition(), "json-array-variable-store-reopen");
            IWorkflowRunStore store = storeKind == "sqlite"
                ? new SqliteWorkflowRunStore(Path.Combine(directory, "runs.sqlite3"))
                : new FileRunJournal(Path.Combine(directory, "runs"));
            var created = store.Create(revision, "unused");
            var output = await new WorkflowRunner().ExecuteAsync(created.Header.RunId, revision, "unused",
                evt => { store.Append(evt); return ValueTask.CompletedTask; });

            IWorkflowRunStore reopened = storeKind == "sqlite"
                ? new SqliteWorkflowRunStore(Path.Combine(directory, "runs.sqlite3"))
                : new FileRunJournal(Path.Combine(directory, "runs"));
            var snapshot = reopened.Get(created.Header.RunId)!;

            Assert.Equal("[null,{\"name\":\"alpha\",\"n\":1.00},[true],null]", output);
            Assert.Equal("completed", snapshot.Status);
            var written = Assert.Single(snapshot.Events, evt => evt.Kind == ExecutionEventKinds.VariableWritten);
            using var writePayload = JsonDocument.Parse(written.Data!);
            Assert.Equal("[{\"seed\":1.00}]", writePayload.RootElement.GetProperty("previousValue").GetRawText());
            Assert.Equal(output, writePayload.RootElement.GetProperty("value").GetRawText());
            Assert.Contains(snapshot.Events, evt => evt.Kind == ExecutionEventKinds.DataEvaluated && evt.NodeId == "append");
            Assert.True(VariableEvidence.TryReconstructPlayback(snapshot.Events, out var playback));
            Assert.Equal(output, playback!.Values["items"].Value);
            AssertEveryArrayVariableEvidencePrefixReconstructs(snapshot.Events);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void Json_array_variable_budget_failure_does_not_commit_the_value_or_version()
    {
        const string arrayType = WorkflowDataTypes.JsonArray;
        var definition = WorkflowVariableEditing.Add(
            new WorkflowDefinition(WorkflowSchemaVersions.UnifiedWorkflowGraph, "json-variable-budget", "input",
                ImmutableSortedDictionary<string, NodeDefinition>.Empty.Add("input", new(NodeTypes.Input, "1.0.0")),
                ImmutableSortedDictionary<string, ConnectionDefinition>.Empty),
            "items", "Items", "[]", type: arrayType);
        var store = new WorkflowVariableStore(definition);
        var largeValue = "[\"" + new string('x', WorkflowArrayLiteral.MaximumPayloadBytes - 4) + "\"]";

        var first = store.Write("items", arrayType, largeValue);
        var second = store.Write("items", arrayType, largeValue);
        var beforeFailure = second;

        Assert.Throws<WorkflowArrayEvidenceBudgetExceededException>(() => store.Write("items", arrayType, largeValue));
        Assert.True(store.TryRead("items", out var afterFailure));
        Assert.Equal(beforeFailure.DataType, afterFailure.DataType);
        Assert.Equal(3, afterFailure.Version);
        Assert.Equal(WorkflowVariableStore.FormatValue(arrayType, beforeFailure.Value),
            WorkflowVariableStore.FormatValue(arrayType, afterFailure.Value));
        Assert.Equal(WorkflowVariableStore.FormatValue(arrayType, first.Value),
            WorkflowVariableStore.FormatValue(arrayType, beforeFailure.Value));
    }

    [Theory]
    [InlineData("file")]
    [InlineData("sqlite")]
    public async Task Json_array_transform_evidence_budget_failure_preserves_last_committed_value(string storeKind)
    {
        var directory = Path.Combine(Path.GetTempPath(), "vaw-json-array-transform-budget-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        try
        {
            var revision = WorkflowRevision.Create(JsonArrayTransformBudgetDefinition(), "json-array-transform-budget");
            IWorkflowRunStore store = storeKind == "sqlite"
                ? new SqliteWorkflowRunStore(Path.Combine(directory, "runs.sqlite3"))
                : new FileRunJournal(Path.Combine(directory, "runs"));
            var created = store.Create(revision, "unused");
            await Assert.ThrowsAsync<InvalidOperationException>(() => new WorkflowRunner().ExecuteAsync(
                created.Header.RunId, revision, "unused", evt =>
                {
                    store.Append(evt);
                    return ValueTask.CompletedTask;
                }));

            IWorkflowRunStore reopened = storeKind == "sqlite"
                ? new SqliteWorkflowRunStore(Path.Combine(directory, "runs.sqlite3"))
                : new FileRunJournal(Path.Combine(directory, "runs"));
            var snapshot = reopened.Get(created.Header.RunId)!;
            var writes = snapshot.Events.Where(evt => evt.Kind == ExecutionEventKinds.VariableWritten).ToArray();
            Assert.Single(writes);
            using var committed = JsonDocument.Parse(writes[0].Data!);
            Assert.Equal("[null]", committed.RootElement.GetProperty("value").GetRawText());
            Assert.Contains(snapshot.Events, evt => evt.Kind == ExecutionEventKinds.DataEvaluated && evt.NodeId == "append-1");
            var budgetFailure = Assert.Single(snapshot.Events.Where(evt => evt.Kind == ExecutionEventKinds.NodeFailed &&
                evt.NodeId?.StartsWith("append-", StringComparison.Ordinal) == true));
            Assert.Contains("65,536-byte aggregate limit", budgetFailure.Data);
            Assert.True(VariableEvidence.TryReconstructPlayback(snapshot.Events, out var playback));
            Assert.Equal("[null]", playback!.Values["items"].Value);
            AssertEveryArrayVariableEvidencePrefixReconstructs(snapshot.Events);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void Json_array_variable_state_remains_outside_checkpoint_snapshots()
    {
        var definition = JsonArrayVariableDefinition();
        var store = new WorkflowVariableStore(definition);

        Assert.Throws<InvalidOperationException>(() => store.Snapshot());
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
    public async Task Typed_array_cancellation_before_write_leaves_no_variable_state_evidence()
    {
        var revision = WorkflowRevision.Create(ArrayVariableDefinition(), "typed-array-cancel-before-write");
        using var cancellation = new CancellationTokenSource();
        var events = new List<ExecutionEvent>();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new WorkflowRunner().ExecuteAsync(
            Guid.NewGuid(), revision, "unused", evt =>
            {
                events.Add(evt);
                if (evt.Kind == ExecutionEventKinds.NodeStarted && evt.NodeId == "set") cancellation.Cancel();
                return ValueTask.CompletedTask;
            }, cancellation.Token));

        Assert.DoesNotContain(events, evt => evt.Kind is ExecutionEventKinds.VariableRead or ExecutionEventKinds.VariableWritten);
        Assert.False(VariableEvidence.TryReconstructPlayback(events, out _));
    }

    [Fact]
    public async Task Typed_array_cancellation_after_write_preserves_reconstructable_variable_state()
    {
        var revision = WorkflowRevision.Create(ArrayVariableDefinition(), "typed-array-cancel-after-write");
        using var cancellation = new CancellationTokenSource();
        var events = new List<ExecutionEvent>();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new WorkflowRunner().ExecuteAsync(
            Guid.NewGuid(), revision, "unused", evt =>
            {
                events.Add(evt);
                if (evt.Kind == ExecutionEventKinds.VariableWritten) cancellation.Cancel();
                return ValueTask.CompletedTask;
            }, cancellation.Token));

        var written = Assert.Single(events, evt => evt.Kind == ExecutionEventKinds.VariableWritten);
        using var payload = JsonDocument.Parse(written.Data!);
        Assert.Equal("[\"seed\",\"next\"]", payload.RootElement.GetProperty("value").GetRawText());
        AssertEveryArrayVariableEvidencePrefixReconstructs(events);
    }

    [Fact]
    public async Task Typed_array_failure_after_committed_write_preserves_the_recorded_prefix()
    {
        var revision = WorkflowRevision.Create(ArrayVariableDefinition(), "typed-array-failure-prefix");
        var events = new List<ExecutionEvent>();
        var injectFailure = true;

        await Assert.ThrowsAsync<InvalidOperationException>(() => new WorkflowRunner().ExecuteAsync(
            Guid.NewGuid(), revision, "unused", evt =>
            {
                events.Add(evt);
                if (injectFailure && evt.Kind == ExecutionEventKinds.NodeCompleted && evt.NodeId == "set")
                {
                    injectFailure = false;
                    throw new InvalidOperationException("injected recorder failure");
                }
                return ValueTask.CompletedTask;
            }));

        var written = Assert.Single(events, evt => evt.Kind == ExecutionEventKinds.VariableWritten);
        using var payload = JsonDocument.Parse(written.Data!);
        Assert.Equal("[\"seed\",\"next\"]", payload.RootElement.GetProperty("value").GetRawText());
        AssertEveryArrayVariableEvidencePrefixReconstructs(events);
    }

    [Fact]
    public async Task Sequential_child_invocations_get_independent_typed_array_variable_stores()
    {
        var childDefinition = ChildWithArrayVariable(ArrayVariableDefinition() with { WorkflowId = "array-variable-child" });
        var child = WorkflowRevision.Create(childDefinition, "typed-array-child-isolation");
        var firstCall = new NodeDefinition(NodeTypes.WorkflowCall, "1.0.0")
        {
            WorkflowCall = new(child.Definition.WorkflowId, child.RevisionId, 1, child.ContentHash, child.Definition)
        };
        var secondCall = firstCall;
        var nodes = ImmutableSortedDictionary<string, NodeDefinition>.Empty
            .Add("input", new(NodeTypes.Input, "1.0.0"))
            .Add("call-first", firstCall)
            .Add("call-second", secondCall)
            .Add("output", new(NodeTypes.Output, "1.0.0"));
        var data = ImmutableSortedDictionary<string, ConnectionDefinition>.Empty
            .Add("input-first", new(new("input", "out"), new("call-first", "in"), "data"))
            .Add("first-second", new(new("call-first", "out"), new("call-second", "in"), "data"))
            .Add("second-output", new(new("call-second", "out"), new("output", "in"), "data"));
        var execution = ImmutableSortedDictionary<string, ConnectionDefinition>.Empty
            .Add("input-first", new(new("input", "then"), new("call-first", "execute"), "execution"))
            .Add("first-second", new(new("call-first", "out"), new("call-second", "execute"), "execution"))
            .Add("second-output", new(new("call-second", "out"), new("output", "execute"), "execution"));
        var parent = new WorkflowDefinition(WorkflowSchemaVersions.UnifiedWorkflowGraph,
            "array-variable-parent", "input", nodes, data) { ExecutionConnections = execution };
        var revision = WorkflowRevision.Create(parent, "typed-array-child-isolation");
        var events = new List<ExecutionEvent>();

        var result = await new WorkflowRunner().ExecuteAsync(Guid.NewGuid(), revision, "unused",
            evt => { events.Add(evt); return ValueTask.CompletedTask; });

        Assert.Equal("2", result);
        foreach (var callId in new[] { "call-first", "call-second" })
        {
            var childEvents = events.Where(evt => evt.ScopeId == callId).ToArray();
            Assert.True(VariableEvidence.TryReconstructPlayback(childEvents, out var playback));
            Assert.Equal("[\"seed\",\"next\"]", playback!.Values["items"].Value);
            Assert.Equal(2, playback.Values["items"].Version);
            var write = Assert.Single(childEvents, evt => evt.Kind == ExecutionEventKinds.VariableWritten);
            using var payload = JsonDocument.Parse(write.Data!);
            Assert.Equal("[\"seed\"]", payload.RootElement.GetProperty("previousValue").GetRawText());
        }
    }

    [Fact]
    public async Task Concurrent_runs_get_independent_typed_array_variable_stores()
    {
        var revision = WorkflowRevision.Create(ArrayVariableDefinitionUsingInputItem(), "typed-array-run-isolation");
        var firstEvents = new List<ExecutionEvent>();
        var secondEvents = new List<ExecutionEvent>();
        var runner = new WorkflowRunner();

        var results = await Task.WhenAll(
            runner.ExecuteAsync(Guid.NewGuid(), revision, "first", evt =>
            {
                firstEvents.Add(evt);
                return ValueTask.CompletedTask;
            }),
            runner.ExecuteAsync(Guid.NewGuid(), revision, "second", evt =>
            {
                secondEvents.Add(evt);
                return ValueTask.CompletedTask;
            }));

        Assert.Equal("[\"seed\",\"first\"]", results[0]);
        Assert.Equal("[\"seed\",\"second\"]", results[1]);
        Assert.True(VariableEvidence.TryReconstructPlayback(firstEvents, out var firstPlayback));
        Assert.True(VariableEvidence.TryReconstructPlayback(secondEvents, out var secondPlayback));
        Assert.Equal("[\"seed\",\"first\"]", firstPlayback!.Values["items"].Value);
        Assert.Equal("[\"seed\",\"second\"]", secondPlayback!.Values["items"].Value);
        Assert.Equal(2, firstPlayback.Values["items"].Version);
        Assert.Equal(2, secondPlayback.Values["items"].Version);
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

    [Theory]
    [InlineData("1.45")]
    [InlineData("2.0")]
    public async Task Loop_can_write_and_read_a_run_scoped_variable_on_each_bounded_pass(string schemaVersion)
    {
        var revision = WorkflowRevision.Create(LoopDefinition() with { SchemaVersion = schemaVersion }, "loop-variable-runtime");
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
        Assert.NotEmpty(first.Boundaries);
        Assert.Equal(first.Boundaries.Count, first.Captures.Count);
        Assert.Equal(first.Captures.Count, first.Captures.Select(capture => capture.Checkpoint.CheckpointId).Distinct().Count());
        Assert.All(first.Boundaries, boundary =>
        {
            Assert.True(boundary.Generation > 0);
            Assert.True(boundary.CheckpointCallbacks > 0);
            Assert.Equal(0, boundary.ActiveHandlers);
            Assert.NotNull(boundary.SourceNodeId);
            Assert.NotNull(boundary.SourcePortId);
            Assert.NotNull(boundary.NextNodeId);
            Assert.NotNull(boundary.NextInputPortId);
        });
        Assert.Equal(1, events.Count(evt => evt.Kind == ExecutionEventKinds.VariableWritten));

        var resumed = new ProofCheckpointSink(events, restored: saved);
        Assert.Equal("continue", await new WorkflowRunner().ExecuteCheckpointedProofAsync(runId, revision, "continue",
            evt => { events.Add(evt); return ValueTask.CompletedTask; }, resumed));
        Assert.Equal(3, events.Count(evt => evt.Kind == ExecutionEventKinds.VariableWritten));
        Assert.Equal([2L, 3L, 4L], events.Where(evt => evt.Kind == ExecutionEventKinds.VariableWritten)
            .Select(evt => JsonDocument.Parse(evt.Data!).RootElement.GetProperty("version").GetInt64()));
    }

    [Fact]
    public void Checkpoint_boundary_probe_fails_closed_when_a_handler_changes_after_pre_save()
    {
        var tracker = new CheckpointBoundaryTracker();
        tracker.BeginHandler("body", "loop", "body", 1);
        tracker.OnCheckpointing("body");
        tracker.PublishTransition("body", "then", "set", "execute", 1);

        Assert.False(tracker.TryCapture(out var observation));
        Assert.Equal(0, observation.ActiveHandlers);
    }

    [Fact]
    public void Checkpoint_boundary_probe_accepts_multiple_stable_executor_callbacks_once()
    {
        var tracker = new CheckpointBoundaryTracker();
        tracker.BeginHandler("loop", "input", "then", 0);
        tracker.PublishTransition("loop", "body", "body", "execute", 1);
        tracker.OnCheckpointing("loop");
        tracker.OnCheckpointing("body");

        Assert.True(tracker.TryCapture(out var observation));
        Assert.Equal(2, observation.CheckpointCallbacks);
        Assert.Equal("loop", observation.SourceNodeId);
        Assert.Equal("body", observation.NextNodeId);
        Assert.Equal("execute", observation.NextInputPortId);
    }

    [Fact]
    public void Resumed_checkpoint_boundary_requires_framework_restore_callback()
    {
        var tracker = new CheckpointBoundaryTracker(requiresRestoration: true);
        tracker.BeginHandler("body", "loop", "body", 1);
        tracker.PublishTransition("body", "then", "loop", "in", 1);
        tracker.OnCheckpointing("loop");

        Assert.False(tracker.TryCapture(out _));

        tracker.OnCheckpointRestored("loop");
        tracker.BeginHandler("body", "loop", "body", 1);
        tracker.PublishTransition("body", "then", "loop", "in", 1);
        tracker.OnCheckpointing("loop");

        Assert.True(tracker.TryCapture(out _));
    }

    [Fact]
    public async Task Public_checkpoint_path_keeps_schema_145_loop_recovery_fail_closed()
    {
        var revision = WorkflowRevision.Create(LoopDefinition(), "loop-checkpoint-admission");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => new WorkflowRunner().ExecuteCheckpointedAsync(
            Guid.NewGuid(), revision, "continue", _ => ValueTask.CompletedTask,
            new ProofCheckpointSink([])));
        Assert.Contains("schema 1.45 loop graphs requires explicit host opt-in", error.Message);
        Assert.Contains("production checkpoint frame", error.Message);
    }

    [Fact]
    public async Task Public_checkpoint_host_keeps_schema_145_loop_recovery_fail_closed()
    {
        var directory = Path.Combine(Path.GetTempPath(), "vaw-loop-host-admission-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        try
        {
            await using var host = new CheckpointWorkflowHost(new SqliteWorkflowRunStore(Path.Combine(directory, "runs.sqlite3")));
            await Assert.ThrowsAsync<InvalidOperationException>(() => host.StartAsync(
                WorkflowRevision.Create(LoopDefinition(), "loop-checkpoint-host-admission"), "continue", 4));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task Explicitly_enabled_loop_checkpoint_host_persists_a_composite_frame()
    {
        var directory = Path.Combine(Path.GetTempPath(), "vaw-loop-host-opt-in-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        try
        {
            var database = Path.Combine(directory, "runs.sqlite3");
            var store = new SqliteWorkflowRunStore(database);
            var revision = WorkflowRevision.Create(LoopDefinition(), "loop-checkpoint-host-opt-in");
            RunSnapshot suspended;
            WorkflowRunCheckpoint checkpoint;
            await using (var host = new CheckpointWorkflowHost(store, new CheckpointWorkflowHostOptions(true)))
            {
                suspended = await host.StartAsync(revision, "continue", 4);
                checkpoint = store.GetCheckpoint(suspended.Header.RunId)!;
            }

            Assert.Equal("suspended", suspended.Status);
            Assert.NotNull(checkpoint.Frame);
            Assert.True(WorkflowCheckpointRecovery.Assess(suspended, checkpoint).CanResume);

            await using var replacement = new CheckpointWorkflowHost(new SqliteWorkflowRunStore(database),
                new CheckpointWorkflowHostOptions(true));
            var resumedSuspended = await replacement.ResumeAsync(suspended.Header.RunId, checkpoint.Native.CheckpointId,
                suspended.Events.Count(evt => evt.Kind == ExecutionEventKinds.NodeCompleted) + 4);
            var nextCheckpoint = store.GetCheckpoint(suspended.Header.RunId);
            Assert.Equal("suspended", resumedSuspended.Status);
            Assert.NotNull(nextCheckpoint?.Frame);
            Assert.True(WorkflowCheckpointRecovery.Assess(resumedSuspended, nextCheckpoint).CanResume);
            Assert.True(VariableEvidence.TryReconstructPlayback(resumedSuspended.Events, out var playback));
            Assert.NotNull(playback);
            Assert.NotEmpty(playback!.Operations);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task Reopened_opt_in_loop_history_supports_passive_playback_without_execution()
    {
        var directory = Path.Combine(Path.GetTempPath(), "vaw-loop-passive-playback-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        try
        {
            var database = Path.Combine(directory, "runs.sqlite3");
            RunSnapshot suspended;
            await using (var host = new CheckpointWorkflowHost(new SqliteWorkflowRunStore(database),
                new CheckpointWorkflowHostOptions(true)))
            {
                suspended = await host.StartAsync(WorkflowRevision.Create(LoopDefinition(), "loop-passive-playback"), "continue", 4);
            }

            var reopened = new SqliteWorkflowRunStore(database).Get(suspended.Header.RunId)!;
            Assert.Equal("suspended", reopened.Status);
            Assert.True(VariableEvidence.TryReconstructPlayback(reopened.Events, out var playback));
            Assert.NotNull(playback);
            Assert.Equal("continue", playback!.Values["message"].Value);
            Assert.Single(playback.Operations);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task Competing_opt_in_hosts_allow_only_one_process_to_resume_a_checkpoint()
    {
        var directory = Path.Combine(Path.GetTempPath(), "vaw-loop-competing-recovery-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        var database = Path.Combine(directory, "runs.sqlite3");
        var startResult = Path.Combine(directory, "start.json");
        var firstResult = Path.Combine(directory, "first.json");
        var secondResult = Path.Combine(directory, "second.json");
        try
        {
            var start = await RunCheckpointProbeAndKillAsync(database, startResult, "host-start");
            Assert.NotEqual(0, start.ExitCode);
            var started = await ReadProbeResultAsync(startResult);

            var resumes = await Task.WhenAll(
                RunCheckpointProbeAsync(database, firstResult, "host-resume", started.RunId, started.CheckpointId),
                RunCheckpointProbeAsync(database, secondResult, "host-resume", started.RunId, started.CheckpointId));

            Assert.Single(resumes, result => result.ExitCode == 0);
            Assert.Single(resumes, result => result.ExitCode != 0);
            var completed = new SqliteWorkflowRunStore(database).Get(started.RunId)!;
            Assert.Equal("completed", completed.Status);
            Assert.Equal(new long[] { 2, 3, 4 }, completed.Events.Where(evt => evt.Kind == ExecutionEventKinds.VariableWritten)
                .Select(evt => JsonDocument.Parse(evt.Data!).RootElement.GetProperty("version").GetInt64()).ToArray());
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task Loop_checkpoint_capture_requires_a_boundary_sink()
    {
        var revision = WorkflowRevision.Create(LoopDefinition(), "loop-checkpoint-boundary-sink");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => new WorkflowRunner().ExecuteCheckpointedProofAsync(
            Guid.NewGuid(), revision, "continue", _ => ValueTask.CompletedTask,
            new LoopWithoutBoundarySink()));

        Assert.Contains("boundary sink", error.Message);
    }

    [Fact]
    public async Task Cancellation_before_capture_is_terminal_but_cancellation_after_capture_keeps_saved_boundary()
    {
        var beforeEvents = new List<ExecutionEvent>();
        using var before = new CancellationTokenSource();
        before.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new WorkflowRunner().ExecuteCheckpointedProofAsync(
            Guid.NewGuid(), WorkflowRevision.Create(Definition(), "checkpoint-cancel-before"), "before",
            evt => { beforeEvents.Add(evt); return ValueTask.CompletedTask; }, new ProofCheckpointSink(beforeEvents), before.Token));
        Assert.Contains(beforeEvents, evt => evt.Kind == ExecutionEventKinds.Cancelled);

        var afterEvents = new List<ExecutionEvent>();
        using var after = new CancellationTokenSource();
        var sink = new CancelAfterCaptureSink(after);
        var result = await new WorkflowRunner().ExecuteCheckpointedProofAsync(
            Guid.NewGuid(), WorkflowRevision.Create(Definition(), "checkpoint-cancel-after"), "after",
            evt => { afterEvents.Add(evt); return ValueTask.CompletedTask; }, sink, after.Token);
        Assert.Null(result);
        Assert.True(after.IsCancellationRequested);
        Assert.DoesNotContain(afterEvents, evt => evt.Kind is ExecutionEventKinds.Cancelled or ExecutionEventKinds.Failed or ExecutionEventKinds.Completed);
        Assert.NotNull(sink.Last);
    }

    [Fact]
    public async Task Loop_checkpoint_publication_failure_leaves_no_recoverable_boundary_or_partial_capture()
    {
        var directory = Path.Combine(Path.GetTempPath(), "vaw-loop-publication-failure-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        try
        {
            var database = Path.Combine(directory, "runs.sqlite3");
            var store = new SqliteWorkflowRunStore(database);
            var revision = WorkflowRevision.Create(LoopDefinition(), "loop-checkpoint-publication-failure");
            var initial = store.Create(revision, "continue");
            using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = database, Pooling = false }.ToString()))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "CREATE TRIGGER fail_loop_checkpoint BEFORE INSERT ON run_checkpoints BEGIN SELECT RAISE(ABORT,'injected'); END;";
                command.ExecuteNonQuery();
            }

            var sink = new DurableLoopProofSink(store, initial, revision, restored: null, suspendAfter: 4);
            await Assert.ThrowsAsync<InvalidOperationException>(() => new WorkflowRunner().ExecuteCheckpointedProofAsync(
                initial.Header.RunId, revision, "continue", sink.RecordAsync, sink));

            var stored = store.Get(initial.Header.RunId)!;
            Assert.Equal("failed", stored.Status);
            Assert.Null(store.GetCheckpoint(initial.Header.RunId));
            Assert.DoesNotContain(stored.Events, evt => evt.Kind is ExecutionEventKinds.CheckpointCaptured or ExecutionEventKinds.Suspended);
            Assert.False(WorkflowCheckpointRecovery.Assess(stored, null).CanResume);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task Cancellation_after_durable_loop_publication_preserves_the_saved_boundary()
    {
        var directory = Path.Combine(Path.GetTempPath(), "vaw-loop-cancel-publication-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        try
        {
            var database = Path.Combine(directory, "runs.sqlite3");
            var store = new SqliteWorkflowRunStore(database);
            var revision = WorkflowRevision.Create(LoopDefinition(), "loop-checkpoint-cancel-publication");
            var initial = store.Create(revision, "continue");
            using var cancellation = new CancellationTokenSource();
            var sink = new DurableLoopProofSink(store, initial, revision, restored: null, suspendAfter: 4, cancellation);

            var result = await new WorkflowRunner().ExecuteCheckpointedProofAsync(
                initial.Header.RunId, revision, "continue", sink.RecordAsync, sink, cancellation.Token);

            Assert.Null(result);
            Assert.True(cancellation.IsCancellationRequested);
            var stored = store.Get(initial.Header.RunId)!;
            var checkpoint = store.GetCheckpoint(initial.Header.RunId);
            Assert.NotNull(checkpoint);
            Assert.Equal("suspended", stored.Status);
            Assert.True(WorkflowCheckpointRecovery.Assess(stored, checkpoint).CanResume);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task Cancellation_during_loop_resume_does_not_mutate_the_saved_checkpoint()
    {
        var directory = Path.Combine(Path.GetTempPath(), "vaw-loop-cancel-resume-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        try
        {
            var database = Path.Combine(directory, "runs.sqlite3");
            var store = new SqliteWorkflowRunStore(database);
            var revision = WorkflowRevision.Create(LoopDefinition(), "loop-checkpoint-cancel-resume");
            var initial = store.Create(revision, "continue");
            var firstSink = new DurableLoopProofSink(store, initial, revision, restored: null, suspendAfter: 4);
            Assert.Null(await new WorkflowRunner().ExecuteCheckpointedProofAsync(
                initial.Header.RunId, revision, "continue", firstSink.RecordAsync, firstSink));
            var suspended = store.Get(initial.Header.RunId)!;
            var checkpoint = store.GetCheckpoint(initial.Header.RunId)!;

            using var cancellation = new CancellationTokenSource();
            var resumeSink = new CancelOnRestoreSink(checkpoint.Native, cancellation);
            async ValueTask Record(ExecutionEvent evt)
            {
                var current = store.Get(initial.Header.RunId)!;
                store.Append(current.Header.RunId, current.Events.Length, [evt with { Sequence = current.Events.Length + 1 }]);
            }

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new WorkflowRunner().ExecuteCheckpointedProofAsync(
                initial.Header.RunId, revision, "continue", Record, resumeSink, cancellation.Token));

            var after = store.Get(initial.Header.RunId)!;
            var advancedCheckpoint = store.GetCheckpoint(initial.Header.RunId)!;
            Assert.False(WorkflowCheckpointRecovery.Assess(after, advancedCheckpoint).CanResume);
            Assert.Equal(1, after.Events.Count(evt => evt.Kind == ExecutionEventKinds.VariableWritten));
            Assert.False(WorkflowCheckpointRecovery.Assess(after, checkpoint).CanResume);
            Assert.Contains(after.Events, evt => evt.Kind == ExecutionEventKinds.Cancelled);
            Assert.NotEqual(suspended.Events.Length, after.Events.Length);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task Loop_checkpoint_round_trips_through_independent_test_processes_and_durable_frame()
    {
        var directory = Path.Combine(Path.GetTempPath(), "vaw-loop-process-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        var database = Path.Combine(directory, "runs.sqlite3");
        var startResult = Path.Combine(directory, "start.json");
        var secondResumeResult = Path.Combine(directory, "resume-2.json");
        var finalResumeResult = Path.Combine(directory, "resume-final.json");
        try
        {
            var start = await RunCheckpointProbeAndKillAsync(database, startResult, "start");
            Assert.NotEqual(0, start.ExitCode);
            var started = await ReadProbeResultAsync(startResult);
            Assert.NotEqual(Environment.ProcessId, started.ProcessId);

            var store = new SqliteWorkflowRunStore(database);
            var snapshot = store.Get(started.RunId)!;
            var checkpoint = store.GetCheckpoint(started.RunId)!;
            Assert.Equal("suspended", snapshot.Status);
            Assert.NotNull(checkpoint.Frame);
            Assert.True(WorkflowCheckpointRecovery.Assess(snapshot, checkpoint).CanResume);
            var mismatchedNative = checkpoint with
            {
                Native = checkpoint.Native with { Payload = "{\"different\":true}" },
                PayloadHash = WorkflowRunCheckpoint.Hash("{\"different\":true}")
            };
            Assert.False(WorkflowCheckpointRecovery.Assess(snapshot, mismatchedNative).CanResume);
            Assert.Equal(1, snapshot.Events.Count(evt => evt.Kind == ExecutionEventKinds.VariableWritten));

            var secondResume = await RunCheckpointProbeAsync(database, secondResumeResult, "resume", started.RunId,
                started.CheckpointId, suspendAfterCompletedNodes: 8);
            Assert.True(secondResume.ExitCode == 0, secondResume.StandardOutput + Environment.NewLine + secondResume.StandardError);
            var resumed = await ReadProbeResultAsync(secondResumeResult);
            Assert.NotEqual(started.ProcessId, resumed.ProcessId);
            Assert.Equal(started.RunId, resumed.RunId);
            Assert.Null(resumed.Output);
            Assert.NotEqual(started.CheckpointId, resumed.CheckpointId);

            var repeatedStore = new SqliteWorkflowRunStore(database);
            var repeated = repeatedStore.Get(started.RunId)!;
            var repeatedCheckpoint = repeatedStore.GetCheckpoint(started.RunId)!;
            Assert.Equal("suspended", repeated.Status);
            Assert.True(WorkflowCheckpointRecovery.Assess(repeated, repeatedCheckpoint).CanResume);
            Assert.Equal(new long[] { 2, 3 }, repeated.Events.Where(evt => evt.Kind == ExecutionEventKinds.VariableWritten)
                .Select(evt => JsonDocument.Parse(evt.Data!).RootElement.GetProperty("version").GetInt64()).ToArray());

            var finalResume = await RunCheckpointProbeAsync(database, finalResumeResult, "resume", started.RunId,
                resumed.CheckpointId);
            Assert.True(finalResume.ExitCode == 0, finalResume.StandardOutput + Environment.NewLine + finalResume.StandardError);
            var completedResult = await ReadProbeResultAsync(finalResumeResult);
            Assert.NotEqual(resumed.ProcessId, completedResult.ProcessId);
            Assert.Equal(started.RunId, completedResult.RunId);
            Assert.Equal("continue", completedResult.Output);

            var finalStore = new SqliteWorkflowRunStore(database);
            var completed = finalStore.Get(started.RunId)!;
            Assert.Equal("completed", completed.Status);
            Assert.Equal("continue", completed.Events[^1].Data);
            Assert.Equal(new long[] { 2, 3, 4 }, completed.Events.Where(evt => evt.Kind == ExecutionEventKinds.VariableWritten)
                .Select(evt => JsonDocument.Parse(evt.Data!).RootElement.GetProperty("version").GetInt64()).ToArray());
            Assert.Equal(3, completed.Events.Count(evt => evt.Kind == ExecutionEventKinds.LoopIterationCompleted));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task Opted_in_public_loop_host_recovers_after_owner_process_loss()
    {
        var directory = Path.Combine(Path.GetTempPath(), "vaw-loop-host-process-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        var database = Path.Combine(directory, "runs.sqlite3");
        var startResult = Path.Combine(directory, "start.json");
        var secondResumeResult = Path.Combine(directory, "resume-2.json");
        var finalResumeResult = Path.Combine(directory, "resume-final.json");
        try
        {
            var start = await RunCheckpointProbeAndKillAsync(database, startResult, "host-start");
            Assert.NotEqual(0, start.ExitCode);
            var started = await ReadProbeResultAsync(startResult);
            var store = new SqliteWorkflowRunStore(database);
            var snapshot = store.Get(started.RunId)!;
            var checkpoint = store.GetCheckpoint(started.RunId)!;
            Assert.Equal("suspended", snapshot.Status);
            Assert.NotNull(checkpoint.Frame);
            Assert.True(WorkflowCheckpointRecovery.Assess(snapshot, checkpoint).CanResume);

            var secondResume = await RunCheckpointProbeAsync(database, secondResumeResult, "host-resume", started.RunId,
                started.CheckpointId, suspendAfterCompletedNodes: 8);
            Assert.True(secondResume.ExitCode == 0, secondResume.StandardOutput + Environment.NewLine + secondResume.StandardError);
            var resumed = await ReadProbeResultAsync(secondResumeResult);
            Assert.Equal(started.RunId, resumed.RunId);
            var repeated = new SqliteWorkflowRunStore(database).Get(started.RunId)!;
            var repeatedCheckpoint = new SqliteWorkflowRunStore(database).GetCheckpoint(started.RunId)!;
            Assert.Equal("suspended", repeated.Status);
            Assert.True(WorkflowCheckpointRecovery.Assess(repeated, repeatedCheckpoint).CanResume);
            Assert.Equal(new long[] { 2, 3 }, repeated.Events.Where(evt => evt.Kind == ExecutionEventKinds.VariableWritten)
                .Select(evt => JsonDocument.Parse(evt.Data!).RootElement.GetProperty("version").GetInt64()).ToArray());

            var finalResume = await RunCheckpointProbeAsync(database, finalResumeResult, "host-resume", started.RunId,
                resumed.CheckpointId);
            Assert.True(finalResume.ExitCode == 0, finalResume.StandardOutput + Environment.NewLine + finalResume.StandardError);
            var completed = new SqliteWorkflowRunStore(database).Get(started.RunId)!;
            Assert.Equal("completed", completed.Status);
            Assert.Equal(new long[] { 2, 3, 4 }, completed.Events.Where(evt => evt.Kind == ExecutionEventKinds.VariableWritten)
                .Select(evt => JsonDocument.Parse(evt.Data!).RootElement.GetProperty("version").GetInt64()).ToArray());
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task Opted_in_public_loop_host_process_loss_before_publication_leaves_no_resumable_boundary()
    {
        var directory = Path.Combine(Path.GetTempPath(), "vaw-loop-host-prepublication-loss-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        var database = Path.Combine(directory, "runs.sqlite3");
        var resultPath = Path.Combine(directory, "start.json");
        try
        {
            var start = await RunCheckpointProbeAndKillAsync(database, resultPath, "host-start", holdBeforePublication: true);
            Assert.NotEqual(0, start.ExitCode);
            var entered = await ReadProbeResultAsync(resultPath + ".entered");
            var store = new SqliteWorkflowRunStore(database);
            var snapshot = store.Get(entered.RunId)!;

            Assert.Null(store.GetCheckpoint(entered.RunId));
            Assert.DoesNotContain(snapshot.Events, evt => evt.Kind is ExecutionEventKinds.CheckpointCaptured or ExecutionEventKinds.Suspended);
            Assert.False(WorkflowCheckpointRecovery.Assess(snapshot, null).CanResume);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task Fresh_process_checkpoint_probe()
    {
        var operation = Environment.GetEnvironmentVariable("VAW_CHECKPOINT_PROBE");
        if (string.IsNullOrWhiteSpace(operation)) return;

        var database = Environment.GetEnvironmentVariable("VAW_CHECKPOINT_DATABASE")!;
        var resultPath = Environment.GetEnvironmentVariable("VAW_CHECKPOINT_RESULT")!;
        var input = "continue";
        var store = new SqliteWorkflowRunStore(database);
        if (operation.StartsWith("host-", StringComparison.Ordinal))
        {
            IWorkflowCheckpointHostStore hostStore = store;
            if (Environment.GetEnvironmentVariable("VAW_CHECKPOINT_HOLD_BEFORE_PUBLICATION") == "1")
                hostStore = new HoldBeforePublicationStore(store, resultPath + ".entered");
            await using var host = new CheckpointWorkflowHost(hostStore, new CheckpointWorkflowHostOptions(true));
            RunSnapshot hostRun;
            if (operation == "host-start")
            {
                var hostRevision = WorkflowRevision.Create(LoopDefinition(), "loop-host-process-proof");
                hostRun = await host.StartAsync(hostRevision, input, 4);
            }
            else
            {
                var hostRunId = Guid.Parse(Environment.GetEnvironmentVariable("VAW_CHECKPOINT_RUN_ID")!);
                var hostCheckpointId = Environment.GetEnvironmentVariable("VAW_CHECKPOINT_ID")!;
                var hostThreshold = int.TryParse(Environment.GetEnvironmentVariable("VAW_CHECKPOINT_SUSPEND_AFTER"), out var parsed)
                    ? parsed : (int?)null;
                hostRun = await host.ResumeAsync(hostRunId, hostCheckpointId, hostThreshold);
            }
            var hostCheckpoint = store.GetCheckpoint(hostRun.Header.RunId);
            var hostOutput = hostRun.Status == "completed" ? hostRun.Events.LastOrDefault(evt => evt.Kind == ExecutionEventKinds.Completed)?.Data : null;
            await File.WriteAllTextAsync(resultPath, JsonSerializer.Serialize(new ProbeResult(hostRun.Header.RunId,
                hostCheckpoint?.Native.CheckpointId ?? "", Environment.ProcessId, hostOutput)));
            if (Environment.GetEnvironmentVariable("VAW_CHECKPOINT_HOLD") == "1")
                await Task.Delay(Timeout.InfiniteTimeSpan);
            return;
        }
        using var ownership = store.AcquireRuntimeOwnership();
        Guid runId;
        string? expectedCheckpoint = null;
        WorkflowRevision revision;
        RunSnapshot snapshot;
        NativeWorkflowCheckpoint? restored = null;
        if (operation == "start")
        {
            revision = WorkflowRevision.Create(LoopDefinition(), "loop-process-proof");
            snapshot = store.Create(revision, input);
            runId = snapshot.Header.RunId;
        }
        else
        {
            runId = Guid.Parse(Environment.GetEnvironmentVariable("VAW_CHECKPOINT_RUN_ID")!);
            expectedCheckpoint = Environment.GetEnvironmentVariable("VAW_CHECKPOINT_ID");
            snapshot = store.Get(runId)!;
            var checkpoint = store.GetCheckpoint(runId)!;
            Assert.Equal(expectedCheckpoint, checkpoint.Native.CheckpointId);
            Assert.True(WorkflowCheckpointRecovery.Assess(snapshot, checkpoint).CanResume);
            restored = checkpoint.Native;
            revision = WorkflowRevision.Rehydrate(snapshot.Header.RevisionId, snapshot.Header.Definition,
                snapshot.Header.Author, snapshot.Header.ParentRevisionId, snapshot.Header.CreatedAt, snapshot.Header.ContentHash);
        }

        int? suspendAfter = operation == "start" ? 4 : null;
        if (suspendAfter is null && int.TryParse(Environment.GetEnvironmentVariable("VAW_CHECKPOINT_SUSPEND_AFTER"), out var threshold))
            suspendAfter = threshold;
        var sink = new DurableLoopProofSink(store, snapshot, revision, restored, suspendAfter);
        var events = new List<ExecutionEvent>();
        async ValueTask Record(ExecutionEvent evt)
        {
            events.Add(evt);
            await sink.RecordAsync(evt);
        }
        string? output;
        try
        {
            output = await new WorkflowRunner().ExecuteCheckpointedProofAsync(runId, revision, input, Record, sink);
        }
        catch (Exception error)
        {
            await File.WriteAllTextAsync(resultPath, error.ToString());
            Console.Error.WriteLine(error);
            throw;
        }
        var result = new ProbeResult(runId, sink.LastCheckpointId ?? expectedCheckpoint ?? "", Environment.ProcessId, output);
        await File.WriteAllTextAsync(resultPath, JsonSerializer.Serialize(result));
        if (Environment.GetEnvironmentVariable("VAW_CHECKPOINT_HOLD") == "1")
            await Task.Delay(Timeout.InfiniteTimeSpan);
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

    private static WorkflowDefinition ArrayVariableDefinition()
    {
        var nodes = ImmutableSortedDictionary<string, NodeDefinition>.Empty
            .Add("input", new(NodeTypes.Input, "1.0.0"))
            .Add("array", new(NodeTypes.ArrayLiteral, "1.0.0")
            { ArrayLiteral = new(WorkflowDataTypes.String, "[\"seed\"]") })
            .Add("item", new(NodeTypes.StringConstant, "1.0.0") { StringConstant = new("next") })
            .Add("append", new(NodeTypes.ArrayAppend, "1.0.0") { ArrayElementType = WorkflowDataTypes.String })
            .Add("set", new(NodeTypes.VariableSet, "1.0.0") { VariableId = "items", InputDataType = "array<string>" })
            .Add("get", new(NodeTypes.VariableGet, "1.0.0") { VariableId = "items", OutputDataType = "array<string>" })
            .Add("output", new(NodeTypes.Output, "1.0.0") { OutputDataType = "array<string>" });
        var data = ImmutableSortedDictionary<string, ConnectionDefinition>.Empty
            .Add("array-append", new(new("array", "out"), new("append", "array"), "data"))
            .Add("item-append", new(new("item", "out"), new("append", "item"), "data"))
            .Add("append-set", new(new("append", "out"), new("set", "value"), "data"))
            .Add("get-output", new(new("get", "out"), new("output", "in"), "data"));
        var execution = ImmutableSortedDictionary<string, ConnectionDefinition>.Empty
            .Add("input-set", new(new("input", "then"), new("set", "execute"), "execution"))
            .Add("set-get", new(new("set", "then"), new("get", "execute"), "execution"))
            .Add("get-output", new(new("get", "then"), new("output", "execute"), "execution"));
        return new WorkflowDefinition(WorkflowSchemaVersions.UnifiedWorkflowGraph, "array-variable-runtime", "input", nodes, data)
        {
            Variables = ImmutableSortedDictionary<string, WorkflowVariableDefinition>.Empty
                .Add("items", new("Items", "[\"seed\"]") { Type = "array<string>" }),
            ExecutionConnections = execution
        };
    }

    private static WorkflowDefinition JsonArrayVariableDefinition()
    {
        const string arrayType = WorkflowDataTypes.JsonArray;
        var nodes = ImmutableSortedDictionary<string, NodeDefinition>.Empty
            .Add("input", new(NodeTypes.Input, "1.0.0"))
            .Add("array", new(NodeTypes.ArrayLiteral, "1.0.0")
                { ArrayLiteral = new(WorkflowDataTypes.Json, "[null,{\"name\":\"alpha\",\"n\":1.00},[true]]") })
            .Add("index", new(NodeTypes.IntegerConstant, "1.0.0") { IntegerConstant = new(0) })
            .Add("get-item", new(NodeTypes.ArrayGet, "1.0.0") { ArrayElementType = WorkflowDataTypes.Json })
            .Add("append", new(NodeTypes.ArrayAppend, "1.0.0") { ArrayElementType = WorkflowDataTypes.Json })
            .Add("set", new(NodeTypes.VariableSet, "1.0.0") { VariableId = "items", InputDataType = arrayType })
            .Add("get", new(NodeTypes.VariableGet, "1.0.0") { VariableId = "items", OutputDataType = arrayType })
            .Add("output", new(NodeTypes.Output, "1.0.0") { OutputDataType = arrayType });
        var data = ImmutableSortedDictionary<string, ConnectionDefinition>.Empty
            .Add("array-set", new(new("array", "out"), new("set", "value"), "data"))
            .Add("array-get-item", new(new("array", "out"), new("get-item", "array"), "data"))
            .Add("index-get-item", new(new("index", "out"), new("get-item", "index"), "data"))
            .Add("array-append", new(new("array", "out"), new("append", "array"), "data"))
            .Add("item-append", new(new("get-item", "item"), new("append", "item"), "data"))
            .SetItem("array-set", new(new("append", "out"), new("set", "value"), "data"))
            .Add("get-output", new(new("get", "out"), new("output", "in"), "data"));
        var execution = ImmutableSortedDictionary<string, ConnectionDefinition>.Empty
            .Add("input-set", new(new("input", "then"), new("set", "execute"), "execution"))
            .Add("set-get", new(new("set", "then"), new("get", "execute"), "execution"))
            .Add("get-output", new(new("get", "then"), new("output", "execute"), "execution"));
        return new WorkflowDefinition(WorkflowSchemaVersions.UnifiedWorkflowGraph,
            "json-array-variable-runtime", "input", nodes, data)
        {
            Variables = ImmutableSortedDictionary<string, WorkflowVariableDefinition>.Empty
                .Add("items", new("Items", " [ { \"seed\" : 1.00 } ] ") { Type = arrayType }),
            ExecutionConnections = execution
        };
    }

    private static WorkflowDefinition JsonArrayTransformBudgetDefinition()
    {
        const string arrayType = WorkflowDataTypes.JsonArray;
        var nodes = ImmutableSortedDictionary<string, NodeDefinition>.Empty.WithComparers(StringComparer.Ordinal)
            .Add("input", new(NodeTypes.Input, "1.0.0"))
            .Add("initial-set", new(NodeTypes.VariableSet, "1.0.0") { VariableId = "items", InputDataType = arrayType })
            .Add("large", new(NodeTypes.ArrayLiteral, "1.0.0")
            {
                ArrayLiteral = new(WorkflowDataTypes.Json, "[{\"seed\":\"" + new string('x', 12_000) + "\"}]")
            })
            .Add("small", new(NodeTypes.ArrayLiteral, "1.0.0")
                { ArrayLiteral = new(WorkflowDataTypes.Json, "[null]") })
            .Add("index", new(NodeTypes.IntegerConstant, "1.0.0") { IntegerConstant = new(0) })
            .Add("item", new(NodeTypes.ArrayGet, "1.0.0") { ArrayElementType = WorkflowDataTypes.Json })
            .Add("output", new(NodeTypes.Output, "1.0.0") { OutputDataType = arrayType })
            .Add("get", new(NodeTypes.VariableGet, "1.0.0") { VariableId = "items", OutputDataType = arrayType });
        var data = ImmutableSortedDictionary<string, ConnectionDefinition>.Empty.WithComparers(StringComparer.Ordinal)
            .Add("initial-value", new(new("small", "out"), new("initial-set", "value"), "data"))
            .Add("small-item-array", new(new("small", "out"), new("item", "array"), "data"))
            .Add("item-index", new(new("index", "out"), new("item", "index"), "data"))
            .Add("get-output", new(new("get", "out"), new("output", "in"), "data"));
        var execution = ImmutableSortedDictionary<string, ConnectionDefinition>.Empty.WithComparers(StringComparer.Ordinal)
            .Add("initialize", new(new("input", "then"), new("initial-set", "execute"), "execution"))
            .Add("grow", new(new("initial-set", "then"), new("large-set", "execute"), "execution"))
            .Add("read-output", new(new("get", "then"), new("output", "execute"), "execution"));
        var previousNode = "large";
        for (var index = 1; index <= 7; index++)
        {
            var appendId = $"append-{index}";
            nodes = nodes.Add(appendId, new(NodeTypes.ArrayAppend, "1.0.0") { ArrayElementType = WorkflowDataTypes.Json });
            data = data.Add($"{previousNode}-{appendId}", new(new(previousNode, "out"), new(appendId, "array"), "data"));
            data = data.Add($"item-{appendId}", new(new("item", "item"), new(appendId, "item"), "data"));
            previousNode = appendId;
        }
        nodes = nodes.Add("large-set", new(NodeTypes.VariableSet, "1.0.0") { VariableId = "items", InputDataType = arrayType });
        data = data.Add("large-set-value", new(new(previousNode, "out"), new("large-set", "value"), "data"));
        execution = execution.Add("large-set-read", new(new("large-set", "then"), new("get", "execute"), "execution"));
        return new WorkflowDefinition(WorkflowSchemaVersions.UnifiedWorkflowGraph, "json-array-budget", "input", nodes, data)
        {
            Variables = ImmutableSortedDictionary<string, WorkflowVariableDefinition>.Empty.WithComparers(StringComparer.Ordinal)
                .Add("items", new("Items", "[]") { Type = arrayType }),
            ExecutionConnections = execution
        };
    }

    private static WorkflowDefinition ChildWithArrayVariable(WorkflowDefinition definition)
    {
        var nodes = definition.Nodes
            .SetItem("output", definition.Nodes["output"] with { OutputDataType = WorkflowDataTypes.String })
            .Add("length", new(NodeTypes.ArrayLength, "1.0.0") { ArrayElementType = WorkflowDataTypes.String })
            .Add("format-length", new(NodeTypes.IntegerToString, "1.0.0"));
        var data = definition.Connections.Remove("get-output")
            .Add("get-length", new(new("get", "out"), new("length", "in"), "data"))
            .Add("length-format", new(new("length", "out"), new("format-length", "in"), "data"))
            .Add("format-output", new(new("format-length", "out"), new("output", "in"), "data"));
        return definition with { Nodes = nodes, Connections = data };
    }

    private static WorkflowDefinition ArrayVariableDefinitionUsingInputItem()
    {
        var definition = ArrayVariableDefinition();
        return definition with
        {
            Nodes = definition.Nodes.Remove("item"),
            Connections = definition.Connections.SetItem("item-append",
                new(new("input", "out"), new("append", "item"), "data"))
        };
    }

    private static void AssertEveryArrayVariableEvidencePrefixReconstructs(IReadOnlyList<ExecutionEvent> events)
    {
        for (var length = 1; length <= events.Count; length++)
        {
            var prefix = events.Take(length).ToArray();
            var stateEvents = prefix.Where(evt => VariableEvidence.IsStateEvent(evt.Kind)).ToArray();
            if (stateEvents.Length == 0) continue;

            Assert.True(VariableEvidence.TryReconstructPlayback(prefix, out var playback));
            Assert.Equal(stateEvents.Length, playback!.Operations.Count);
            Assert.Equal("items", Assert.Single(playback.Values).Key);
            using var latestState = JsonDocument.Parse(stateEvents[^1].Data!);
            Assert.Equal(latestState.RootElement.GetProperty("value").GetRawText(), playback.Values["items"].Value);
            Assert.Equal(latestState.RootElement.GetProperty("version").GetInt64(), playback.Values["items"].Version);
        }
    }

    private static WorkflowDefinition ArrayLoopVariableDefinition()
    {
        var nodes = ImmutableSortedDictionary<string, NodeDefinition>.Empty
            .Add("input", new(NodeTypes.Input, "1.0.0"))
            .Add("loop", new(NodeTypes.Loop, "1.0.0") { Loop = new(new("contains", "continue"), 3) })
            .Add("body", new(NodeTypes.LoopBody, "1.0.0"))
            .Add("item", new(NodeTypes.StringConstant, "1.0.0") { StringConstant = new("next") })
            .Add("get", new(NodeTypes.VariableGet, "1.0.0") { VariableId = "items", OutputDataType = "array<string>" })
            .Add("append", new(NodeTypes.ArrayAppend, "1.0.0") { ArrayElementType = WorkflowDataTypes.String })
            .Add("set", new(NodeTypes.VariableSet, "1.0.0") { VariableId = "items", InputDataType = "array<string>" })
            .Add("final-get", new(NodeTypes.VariableGet, "1.0.0") { VariableId = "items", OutputDataType = "array<string>" })
            .Add("output", new(NodeTypes.Output, "1.0.0") { OutputDataType = "array<string>" });
        var data = ImmutableSortedDictionary<string, ConnectionDefinition>.Empty
            .Add("input-loop", new(new("input", "out"), new("loop", "in"), "data"))
            .Add("get-array", new(new("get", "out"), new("append", "array"), "data"))
            .Add("item-append", new(new("item", "out"), new("append", "item"), "data"))
            .Add("append-set", new(new("append", "out"), new("set", "value"), "data"))
            .Add("get-output", new(new("final-get", "out"), new("output", "in"), "data"));
        var execution = ImmutableSortedDictionary<string, ConnectionDefinition>.Empty
            .Add("input-loop", new(new("input", "then"), new("loop", "execute"), "execution"))
            .Add("loop-body", new(new("loop", "body"), new("body", "execute"), "execution"))
            .Add("body-get", new(new("body", "then"), new("get", "execute"), "execution"))
            .Add("get-set", new(new("get", "then"), new("set", "execute"), "execution"))
            .Add("set-loop", new(new("set", "then"), new("loop", "execute"), "execution"))
            .Add("loop-output", new(new("loop", "done"), new("final-get", "execute"), "execution"))
            .Add("final-get-output", new(new("final-get", "then"), new("output", "execute"), "execution"));
        return new WorkflowDefinition(WorkflowSchemaVersions.UnifiedWorkflowGraph, "array-loop-variable-runtime", "input", nodes, data)
        {
            Variables = ImmutableSortedDictionary<string, WorkflowVariableDefinition>.Empty
                .Add("items", new("Items", "[]") { Type = "array<string>" }),
            ExecutionConnections = execution
        };
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
        NativeWorkflowCheckpoint? restored = null) : IWorkflowCheckpointSink, IWorkflowCheckpointBoundarySink
    {
        public NativeWorkflowCheckpoint? RestoredCheckpoint { get; } = restored;
        public NativeWorkflowCheckpoint? Last { get; private set; }
        public List<CheckpointBoundaryObservation> Boundaries { get; } = [];
        public List<(NativeWorkflowCheckpoint Checkpoint, CheckpointBoundaryObservation Observation)> Captures { get; } = [];

        public void ObserveBoundary(NativeWorkflowCheckpoint checkpoint, CheckpointBoundaryObservation observation)
        {
            Boundaries.Add(observation);
            Captures.Add((checkpoint, observation));
        }

        public ValueTask<bool> CaptureAsync(NativeWorkflowCheckpoint checkpoint)
        {
            Last = checkpoint;
            var shouldSuspend = suspendAfterCompletedNodes is { } threshold &&
                events.Count(evt => evt.Kind == ExecutionEventKinds.NodeCompleted) >= threshold;
            return ValueTask.FromResult(shouldSuspend);
        }
    }

    private sealed class CancelAfterCaptureSink(CancellationTokenSource cancellation) : IWorkflowCheckpointSink
    {
        public NativeWorkflowCheckpoint? RestoredCheckpoint => null;
        public NativeWorkflowCheckpoint? Last { get; private set; }

        public ValueTask<bool> CaptureAsync(NativeWorkflowCheckpoint checkpoint)
        {
            Last = checkpoint;
            cancellation.Cancel();
            return ValueTask.FromResult(true);
        }
    }

    private sealed class LoopWithoutBoundarySink : IWorkflowCheckpointSink
    {
        public NativeWorkflowCheckpoint? RestoredCheckpoint => null;

        public ValueTask<bool> CaptureAsync(NativeWorkflowCheckpoint checkpoint) => ValueTask.FromResult(false);
    }

    private sealed class CancelOnRestoreSink(NativeWorkflowCheckpoint restored, CancellationTokenSource cancellation) : IWorkflowCheckpointSink
    {
        public NativeWorkflowCheckpoint? RestoredCheckpoint
        {
            get
            {
                cancellation.Cancel();
                return restored;
            }
        }

        public ValueTask<bool> CaptureAsync(NativeWorkflowCheckpoint checkpoint) => ValueTask.FromResult(false);
    }

    private static async Task<ProbeProcessResult> RunCheckpointProbeAsync(string database, string resultPath, string operation,
        Guid? runId = null, string? checkpointId = null, int? suspendAfterCompletedNodes = null)
    {
        using var process = Process.Start(CreateCheckpointProbeStartInfo(database, resultPath, operation,
            runId, checkpointId, suspendAfterCompletedNodes, holdAfterCapture: false))!;
        return await CompleteCheckpointProbeAsync(process, resultPath);
    }

    private static async Task<ProbeProcessResult> RunCheckpointProbeAndKillAsync(string database, string resultPath, string operation,
        Guid? runId = null, string? checkpointId = null, int? suspendAfterCompletedNodes = null,
        bool holdBeforePublication = false)
    {
        using var process = Process.Start(CreateCheckpointProbeStartInfo(database, resultPath, operation,
            runId, checkpointId, suspendAfterCompletedNodes, holdAfterCapture: !holdBeforePublication,
            holdBeforePublication))!;
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        var readinessPath = holdBeforePublication ? resultPath + ".entered" : resultPath;
        for (var attempt = 0; attempt < 100 && !File.Exists(readinessPath); attempt++) await Task.Delay(20);
        Assert.True(File.Exists(readinessPath), "The checkpoint probe did not reach the expected process-loss boundary.");
        process.Kill(entireProcessTree: true);
        await process.WaitForExitAsync();
        return new ProbeProcessResult(process.ExitCode, await stdoutTask, await stderrTask);
    }

    private static ProcessStartInfo CreateCheckpointProbeStartInfo(string database, string resultPath, string operation,
        Guid? runId, string? checkpointId, int? suspendAfterCompletedNodes, bool holdAfterCapture,
        bool holdBeforePublication = false)
    {
        var project = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "../../../../VisualAgentWorkflow.AgentFramework.Tests/VisualAgentWorkflow.AgentFramework.Tests.csproj"));
        var startInfo = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = Directory.GetCurrentDirectory(),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        startInfo.ArgumentList.Add("test");
        startInfo.ArgumentList.Add(project);
        startInfo.ArgumentList.Add("--no-build");
        startInfo.ArgumentList.Add("--no-restore");
        startInfo.ArgumentList.Add("--filter");
        startInfo.ArgumentList.Add("FullyQualifiedName~Fresh_process_checkpoint_probe");
        startInfo.Environment["VAW_CHECKPOINT_PROBE"] = operation;
        startInfo.Environment["VAW_CHECKPOINT_DATABASE"] = database;
        startInfo.Environment["VAW_CHECKPOINT_RESULT"] = resultPath;
        if (holdAfterCapture) startInfo.Environment["VAW_CHECKPOINT_HOLD"] = "1";
        if (holdBeforePublication) startInfo.Environment["VAW_CHECKPOINT_HOLD_BEFORE_PUBLICATION"] = "1";
        if (suspendAfterCompletedNodes is { } threshold)
            startInfo.Environment["VAW_CHECKPOINT_SUSPEND_AFTER"] = threshold.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (runId is { } id) startInfo.Environment["VAW_CHECKPOINT_RUN_ID"] = id.ToString("D");
        if (checkpointId is not null) startInfo.Environment["VAW_CHECKPOINT_ID"] = checkpointId;
        return startInfo;
    }

    private static async Task<ProbeProcessResult> CompleteCheckpointProbeAsync(Process process, string resultPath)
    {
        var stdout = await process.StandardOutput.ReadToEndAsync();
        var stderr = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        if (process.ExitCode != 0 && File.Exists(resultPath))
            stderr += Environment.NewLine + await File.ReadAllTextAsync(resultPath);
        return new ProbeProcessResult(process.ExitCode, stdout, stderr);
    }

    private static async Task<ProbeResult> ReadProbeResultAsync(string path)
    {
        for (var attempt = 0; attempt < 50 && !File.Exists(path); attempt++) await Task.Delay(20);
        return JsonSerializer.Deserialize<ProbeResult>(await File.ReadAllTextAsync(path))!;
    }

    private sealed record ProbeResult(Guid RunId, string CheckpointId, int ProcessId, string? Output);
    private sealed record ProbeProcessResult(int ExitCode, string StandardOutput, string StandardError);

    private sealed class HoldBeforePublicationStore(SqliteWorkflowRunStore inner, string enteredPath) : IWorkflowCheckpointHostStore
    {
        public RunSnapshot Create(WorkflowRevision revision, string input) => inner.Create(revision, input);
        public RunSnapshot? Get(Guid runId) => inner.Get(runId);
        public IReadOnlyList<RunSnapshot> List() => inner.List();
        public RunSnapshot Append(Guid runId, long expectedSequence, IReadOnlyList<ExecutionEvent> events) => inner.Append(runId, expectedSequence, events);
        public IDisposable AcquireRuntimeOwnership() => inner.AcquireRuntimeOwnership();
        public WorkflowRunCheckpoint? GetCheckpoint(Guid runId) => inner.GetCheckpoint(runId);

        public RunSnapshot CommitCheckpoint(Guid runId, long expectedSequence, IReadOnlyList<ExecutionEvent> events,
            WorkflowRunCheckpoint checkpoint)
        {
            File.WriteAllText(enteredPath, JsonSerializer.Serialize(new ProbeResult(runId, checkpoint.Native.CheckpointId,
                Environment.ProcessId, null)));
            Thread.Sleep(Timeout.Infinite);
            throw new InvalidOperationException("Unreachable after process-loss probe.");
        }
    }

    private sealed class DurableLoopProofSink(SqliteWorkflowRunStore store, RunSnapshot initial,
        WorkflowRevision revision, NativeWorkflowCheckpoint? restored, int? suspendAfter,
        CancellationTokenSource? cancelAfterCommit = null) : IWorkflowCheckpointSink, IWorkflowCheckpointBoundarySink
    {
        private RunSnapshot snapshot = initial;
        private (NativeWorkflowCheckpoint Checkpoint, CheckpointBoundaryObservation Observation)? boundary;
        public NativeWorkflowCheckpoint? RestoredCheckpoint => restored;
        public string? LastCheckpointId { get; private set; }

        public void ObserveBoundary(NativeWorkflowCheckpoint checkpoint, CheckpointBoundaryObservation observation) =>
            boundary = (checkpoint, observation);

        public ValueTask RecordAsync(ExecutionEvent evt)
        {
            snapshot = store.Append(snapshot.Header.RunId, snapshot.Events.Length,
                [evt with { Sequence = snapshot.Events.Length + 1 }]);
            return ValueTask.CompletedTask;
        }

        public ValueTask<bool> CaptureAsync(NativeWorkflowCheckpoint native)
        {
            var completed = snapshot.Events.Count(evt => evt.Kind == ExecutionEventKinds.NodeCompleted);
            if (suspendAfter is not { } threshold || completed < threshold ||
                snapshot.Events.Any(evt => evt.Kind == ExecutionEventKinds.LoopStopped))
                return ValueTask.FromResult(false);

            var now = DateTimeOffset.UtcNow;
            var capture = new ExecutionEvent(snapshot.Header.RunId, snapshot.Header.RevisionId,
                snapshot.Events.Length + 1, now, ExecutionEventKinds.CheckpointCaptured,
                Data: JsonSerializer.Serialize(new { native.CheckpointId, payloadHash = WorkflowRunCheckpoint.Hash(native.Payload) }));
            var suspended = capture with { Sequence = capture.Sequence + 1, Kind = ExecutionEventKinds.Suspended,
                Data = "Saved loop proof boundary; explicit test recovery required." };
            var evidence = snapshot.Events.Concat([capture, suspended]).ToArray();
            var loopId = revision.Definition.Nodes.Single(pair => pair.Value.Type == NodeTypes.Loop).Key;
            var bodyId = revision.Definition.Nodes.Single(pair => pair.Value.Type == NodeTypes.LoopBody).Key;
            var observed = boundary ?? throw new InvalidOperationException("The proof sink did not receive a checkpoint boundary observation.");
            Assert.Equal(native.CheckpointId, observed.Checkpoint.CheckpointId);
            var loopEvent = snapshot.Events.Last(evt => evt.NodeId == loopId &&
                evt.Kind is ExecutionEventKinds.LoopIterationStarted or ExecutionEventKinds.LoopIterationCompleted or ExecutionEventKinds.LoopStopped);
            var cursor = new LoopCheckpointCursor(loopId, bodyId, observed.Observation.LoopIteration,
                revision.Definition.Nodes[loopId].Loop!.MaximumIterations,
                observed.Observation.NextNodeId ?? throw new InvalidOperationException("The proof boundary has no next node."),
                observed.Observation.NextInputPortId ?? throw new InvalidOperationException("The proof boundary has no next input port."),
                observed.Observation.SourceNodeId, observed.Observation.SourcePortId, loopEvent.Sequence);
            var frame = WorkflowCheckpointFrame.Create(revision, snapshot.Header.RunId, native.Payload, cursor,
                native.VariableState!, evidence);
            var checkpoint = new WorkflowRunCheckpoint("1.0", WorkflowRunner.CheckpointRuntimeVersion,
                snapshot.Header.RunId, snapshot.Header.RevisionId, snapshot.Header.ContentHash, evidence.Length, native,
                WorkflowRunCheckpoint.Hash(native.Payload)) with { Frame = frame, VariableStateHash = native.VariableState!.ComputeHash() };
            try
            {
                snapshot = store.CommitCheckpoint(snapshot.Header.RunId, snapshot.Events.Length, [capture, suspended], checkpoint);
            }
            catch (Exception error)
            {
                Console.Error.WriteLine("checkpoint commit: " + error);
                throw;
            }
            LastCheckpointId = native.CheckpointId;
            cancelAfterCommit?.Cancel();
            return ValueTask.FromResult(true);
        }
    }
}
