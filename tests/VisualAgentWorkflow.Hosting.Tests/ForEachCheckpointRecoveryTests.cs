using System.Collections.Immutable;
using System.Text.Json;
using VisualAgentWorkflow.AgentFramework;
using VisualAgentWorkflow.Core;
using Xunit;

namespace VisualAgentWorkflow.Hosting.Tests;

/// <summary>
/// First For Each checkpoint recovery slice (decision 0070): one top-level For Each over a constant array literal with
/// a pure body, recoverable at an iteration boundary behind a host opt-in. Everything else stays fail-closed.
/// </summary>
public sealed class ForEachCheckpointRecoveryTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "vaw-foreach-checkpoint-" + Guid.NewGuid());
    private SqliteWorkflowRunStore Store() => new(Path.Combine(directory, "runs.sqlite3"));
    private static CheckpointWorkflowHostOptions OptIn => new(AllowForEachCheckpointRecovery: true);

    /// <summary>Input, constant array [a,b,c], For Each; the body is empty; output is reverse(upper(last item)).</summary>
    private static WorkflowDefinition PureDefinition(string itemsJson = "[\"a\",\"b\",\"c\"]")
    {
        var nodes = new Dictionary<string, NodeDefinition>
        {
            ["input"] = new(NodeTypes.Input, "1.0.0"),
            ["items"] = new(NodeTypes.ArrayLiteral, "1.0.0") { ArrayLiteral = new(WorkflowDataTypes.String, itemsJson) },
            ["each"] = new(NodeTypes.ForEach, "1.0.0") { ArrayElementType = WorkflowDataTypes.String },
            ["body"] = new(NodeTypes.LoopBody, "1.0.0"),
            ["upper"] = new(NodeTypes.Uppercase, "1.0.0"),
            ["rev"] = new(NodeTypes.Reverse, "1.0.0"),
            ["output"] = new(NodeTypes.Output, "1.0.0")
        };
        var data = new Dictionary<string, ConnectionDefinition>
        {
            ["items-each"] = new(new("items", "out"), new("each", "array"), "data"),
            ["item-upper"] = new(new("each", "item"), new("upper", "in"), "data"),
            ["upper-rev"] = new(new("upper", "out"), new("rev", "in"), "data"),
            ["rev-output"] = new(new("rev", "out"), new("output", "in"), "data")
        };
        return new(WorkflowSchemaVersions.UnifiedWorkflowGraph, "for-each-checkpoint", "input",
            nodes.ToImmutableSortedDictionary(StringComparer.Ordinal), data.ToImmutableSortedDictionary(StringComparer.Ordinal))
        {
            ExecutionConnections = new Dictionary<string, ConnectionDefinition>
            {
                ["start"] = new(new("input", "then"), new("each", "execute"), "execution"),
                ["body-start"] = new(new("each", "body"), new("body", "execute"), "execution"),
                ["body-return"] = new(new("body", "then"), new("each", "feedback"), "execution"),
                ["complete"] = new(new("each", "completed"), new("output", "execute"), "execution")
            }.ToImmutableSortedDictionary(StringComparer.Ordinal)
        };
    }

    private static (string Kind, string? Node, string? Connection, string? Data)[] Shape(RunSnapshot run) =>
        run.Events.Where(evt => evt.Kind is not (ExecutionEventKinds.CheckpointCaptured or ExecutionEventKinds.Suspended or
                ExecutionEventKinds.Recovered))
            .Select(evt => (evt.Kind, evt.NodeId, evt.ConnectionId, evt.Data)).ToArray();

    private async Task<RunSnapshot> UninterruptedAsync(WorkflowRevision revision)
    {
        await using var host = new CheckpointWorkflowHost(new SqliteWorkflowRunStore(Path.Combine(directory, "plain.sqlite3")), OptIn);
        return await host.StartAsync(revision, "go");
    }

    [Fact]
    public async Task Uninterrupted_for_each_run_completes_through_the_opted_in_host()
    {
        var run = await UninterruptedAsync(WorkflowRevision.Create(PureDefinition(), "test"));
        Assert.Equal("completed", run.Status);
        Assert.Equal("C", run.Events[^1].Data);
        Assert.Equal(3, run.Events.Count(evt => evt.Kind == ExecutionEventKinds.LoopIterationStarted));
        Assert.DoesNotContain(run.Events, evt => evt.Kind is ExecutionEventKinds.CheckpointCaptured or ExecutionEventKinds.Suspended);
    }

    [Theory]
    [InlineData(2, 1)]
    [InlineData(4, 2)]
    [InlineData(6, 3)]
    public async Task Crash_at_iteration_N_then_resume_equals_the_uninterrupted_run(int suspendAfter, int expectedPass)
    {
        var revision = WorkflowRevision.Create(PureDefinition(), "test");
        var expected = await UninterruptedAsync(revision);
        var store = Store();
        RunSnapshot saved;
        await using (var host = new CheckpointWorkflowHost(store, OptIn))
            saved = await host.StartAsync(revision, "go", suspendAfter);
        var checkpoint = store.GetCheckpoint(saved.Header.RunId)!;
        Assert.Equal("suspended", saved.Status);
        Assert.Equal(expectedPass, checkpoint.ForEachFrame!.Cursor.Index);
        Assert.Equal(expectedPass, checkpoint.Native.ForEach!.PassIndex);
        Assert.Equal(expectedPass, saved.Events.Count(evt => evt.Kind == ExecutionEventKinds.LoopIterationStarted));
        Assert.True(WorkflowCheckpointRecovery.Assess(saved, checkpoint).CanResume);

        await using var replacement = new CheckpointWorkflowHost(Store(), OptIn);
        var completed = await replacement.ResumeAsync(saved.Header.RunId, checkpoint.Native.CheckpointId);

        Assert.Equal("completed", completed.Status);
        Assert.Equal(expected.Events[^1].Data, completed.Events[^1].Data);
        Assert.Equal(saved.Events.ToArray(), completed.Events.Take(saved.Events.Length).ToArray());
        Assert.Equal(Shape(expected), Shape(completed));
        Assert.Equal(3, completed.Events.Count(evt => evt.Kind == ExecutionEventKinds.LoopIterationStarted));
        Assert.Equal(Enumerable.Range(1, completed.Events.Length), completed.Events.Select(evt => (int)evt.Sequence));
    }

    [Fact]
    public async Task A_resumed_run_can_suspend_and_resume_again()
    {
        var revision = WorkflowRevision.Create(PureDefinition(), "test");
        var expected = await UninterruptedAsync(revision);
        var store = Store();
        RunSnapshot first;
        await using (var host = new CheckpointWorkflowHost(store, OptIn))
            first = await host.StartAsync(revision, "go", 2);
        var firstId = store.GetCheckpoint(first.Header.RunId)!.Native.CheckpointId;
        RunSnapshot second;
        await using (var host = new CheckpointWorkflowHost(Store(), OptIn))
            second = await host.ResumeAsync(first.Header.RunId, firstId, 4);
        Assert.Equal("suspended", second.Status);
        var secondCheckpoint = Store().GetCheckpoint(second.Header.RunId)!;
        Assert.Equal(2, secondCheckpoint.ForEachFrame!.Cursor.Index);
        await using var last = new CheckpointWorkflowHost(Store(), OptIn);
        var completed = await last.ResumeAsync(second.Header.RunId, secondCheckpoint.Native.CheckpointId);
        Assert.Equal(Shape(expected), Shape(completed));
    }

    private async Task<(RunSnapshot Saved, WorkflowRunCheckpoint Checkpoint)> SavedAtPassTwo()
    {
        var revision = WorkflowRevision.Create(PureDefinition(), "test");
        var store = Store();
        await using var host = new CheckpointWorkflowHost(store, OptIn);
        var saved = await host.StartAsync(revision, "go", 4);
        return (saved, store.GetCheckpoint(saved.Header.RunId)!);
    }

    private static WorkflowRunCheckpoint Rehash(WorkflowRunCheckpoint checkpoint, ForEachCheckpointCursor cursor)
    {
        var frame = checkpoint.ForEachFrame! with { Cursor = cursor };
        return checkpoint with { ForEachFrame = frame with { FrameHash = frame.ComputeFrameHash() } };
    }

    [Fact]
    public async Task Tampered_items_or_hash_are_rejected_even_when_the_frame_is_rehashed()
    {
        var (saved, checkpoint) = await SavedAtPassTwo();
        var cursor = checkpoint.ForEachFrame!.Cursor;
        var forged = "[\"a\",\"b\",\"x\"]";
        Assert.False(WorkflowCheckpointRecovery.Assess(saved, Rehash(checkpoint, cursor with
            { ItemsJson = forged, ItemsHash = ForEachCheckpointCursor.HashItems(forged) })).CanResume);
        Assert.False(WorkflowCheckpointRecovery.Assess(saved, Rehash(checkpoint, cursor with
            { ItemsHash = ForEachCheckpointCursor.HashItems("[]") })).CanResume);
        Assert.False(WorkflowCheckpointRecovery.Assess(saved, checkpoint with
            { Native = checkpoint.Native with { ForEach = checkpoint.Native.ForEach! with { ItemsJson = forged } } }).CanResume);
        Assert.False(WorkflowCheckpointRecovery.Assess(saved, checkpoint with { ForEachFrame = null }).CanResume);
        Assert.False(WorkflowCheckpointRecovery.Assess(saved, checkpoint with
            { ForEachFrame = checkpoint.ForEachFrame with { NativePayload = "{}" } }).CanResume);
    }

    private static WorkflowRunCheckpoint WithPayload(WorkflowRunCheckpoint checkpoint, string payload)
    {
        var frame = checkpoint.ForEachFrame! with { NativePayload = payload, NativePayloadHash = WorkflowRunCheckpoint.Hash(payload) };
        return checkpoint with
        {
            Native = checkpoint.Native with { Payload = payload },
            PayloadHash = WorkflowRunCheckpoint.Hash(payload),
            ForEachFrame = frame with { FrameHash = frame.ComputeFrameHash() }
        };
    }

    [Fact]
    public async Task Pass_frame_identity_outside_the_hashes_must_match_the_native_in_flight_frame()
    {
        var (saved, checkpoint) = await SavedAtPassTwo();
        var frameId = checkpoint.Native.ForEach!.PassFrameId.ToString("D");
        Assert.Contains($"\"id\":\"{frameId}\"", checkpoint.Native.Payload);
        Assert.Contains("\"index\":2", checkpoint.Native.Payload);
        Assert.True(WorkflowCheckpointRecovery.Assess(saved, checkpoint).CanResume);

        // State tampering that no hash covers: the Guid that restore keys item/index under, and the pass index.
        Assert.False(WorkflowCheckpointRecovery.Assess(saved, checkpoint with
            { Native = checkpoint.Native with { ForEach = checkpoint.Native.ForEach with { PassFrameId = Guid.NewGuid() } } }).CanResume);
        Assert.False(WorkflowCheckpointRecovery.Assess(saved, checkpoint with
            { Native = checkpoint.Native with { ForEach = checkpoint.Native.ForEach with { PassFrameId = Guid.Empty } } }).CanResume);

        // Payload tampering with every hash recomputed: the in-flight frame no longer matches the stored state.
        var otherId = Guid.NewGuid().ToString("D");
        Assert.False(WorkflowCheckpointRecovery.Assess(saved,
            WithPayload(checkpoint, checkpoint.Native.Payload.Replace(frameId, otherId))).CanResume);
        Assert.False(WorkflowCheckpointRecovery.Assess(saved,
            WithPayload(checkpoint, checkpoint.Native.Payload.Replace("\"index\":2", "\"index\":1"))).CanResume);
        Assert.False(WorkflowCheckpointRecovery.Assess(saved,
            WithPayload(checkpoint, checkpoint.Native.Payload.Replace("\"item\":\"b\"", "\"item\":\"a\""))).CanResume);
        // A parent/null frame fallback is never accepted: dropping the frame leaves nothing to key the outputs under.
        Assert.False(WorkflowCheckpointRecovery.Assess(saved,
            WithPayload(checkpoint, checkpoint.Native.Payload.Replace("\"activationFrame\"", "\"droppedFrame\""))).CanResume);
    }

    /// <summary>
    /// A pure data chain consumed by a body Condition cannot be admitted by the graph validator: a Condition needs two
    /// body routes, which a For Each body only allows with a Break (outside the first slice). So the first slice body is
    /// the Loop body anchor alone and the pure data chain is evaluated at the Output (covered by the equality tests).
    /// </summary>
    [Fact]
    public void A_pure_chain_consumed_by_a_body_condition_is_not_a_valid_for_each_body()
    {
        var definition = PureDefinition("[\"xa\",\"xb\",\"xc\"]");
        definition = definition with
        {
            Nodes = definition.Nodes.Add("cond", new(NodeTypes.Condition, "1.0.0") { Condition = new("contains", "X") }),
            Connections = definition.Connections.Add("chain-cond", new(new("rev", "out"), new("cond", "in"), "data")),
            ExecutionConnections = definition.ExecutionConnections!.Remove("body-return")
                .Add("body-cond", new(new("body", "then"), new("cond", "execute"), "execution"))
                .Add("cond-return", new(new("cond", "true"), new("each", "feedback"), "execution"))
        };
        Assert.NotEmpty(WorkflowValidator.Validate(definition));
    }

    [Fact]
    public async Task Integer_items_recover_with_typed_item_and_index()
    {
        var definition = PureDefinition("[10,20,30]");
        definition = definition with
        {
            Nodes = definition.Nodes.Remove("upper").Remove("rev")
                .SetItem("each", definition.Nodes["each"] with { ArrayElementType = WorkflowDataTypes.Integer })
                .SetItem("items", new(NodeTypes.ArrayLiteral, "1.0.0") { ArrayLiteral = new(WorkflowDataTypes.Integer, "[10,20,30]") })
                .SetItem("output", new(NodeTypes.Output, "1.0.0") { OutputDataType = WorkflowDataTypes.Integer }),
            Connections = definition.Connections.Remove("item-upper").Remove("upper-rev")
                .SetItem("rev-output", new(new("each", "item"), new("output", "in"), "data"))
        };
        Assert.Empty(WorkflowValidator.Validate(definition));
        var revision = WorkflowRevision.Create(definition, "test");
        var expected = await UninterruptedAsync(revision);
        Assert.Equal("30", expected.Events[^1].Data);
        var store = Store();
        RunSnapshot saved;
        await using (var host = new CheckpointWorkflowHost(store, OptIn))
            saved = await host.StartAsync(revision, "go", 4);
        var checkpoint = store.GetCheckpoint(saved.Header.RunId)!;
        Assert.Equal(2, checkpoint.ForEachFrame!.Cursor.Index);
        await using var replacement = new CheckpointWorkflowHost(Store(), OptIn);
        var completed = await replacement.ResumeAsync(saved.Header.RunId, checkpoint.Native.CheckpointId);
        Assert.Equal(Shape(expected), Shape(completed));
        Assert.Equal("30", completed.Events[^1].Data);
    }

    [Fact]
    public async Task Cursor_that_disagrees_with_the_evidence_or_native_state_is_rejected()
    {
        var (saved, checkpoint) = await SavedAtPassTwo();
        var cursor = checkpoint.ForEachFrame!.Cursor;
        Assert.True(WorkflowCheckpointRecovery.Assess(saved, Rehash(checkpoint, cursor)).CanResume);
        Assert.False(WorkflowCheckpointRecovery.Assess(saved, Rehash(checkpoint, cursor with { Index = 3 })).CanResume);
        Assert.False(WorkflowCheckpointRecovery.Assess(saved, Rehash(checkpoint, cursor with { Index = 0 })).CanResume);
        Assert.False(WorkflowCheckpointRecovery.Assess(saved, Rehash(checkpoint, cursor with
            { LastEvidenceSequence = cursor.LastEvidenceSequence - 1 })).CanResume);
        Assert.False(WorkflowCheckpointRecovery.Assess(saved, Rehash(checkpoint, cursor with
            { LastEvidenceSequence = saved.Events.Length + 1 })).CanResume);
        Assert.False(WorkflowCheckpointRecovery.Assess(saved, Rehash(checkpoint, cursor with { ForEachNodeId = "body" })).CanResume);
        Assert.False(WorkflowCheckpointRecovery.Assess(saved, checkpoint with
            { Native = checkpoint.Native with { ForEach = checkpoint.Native.ForEach! with { PassIndex = 1 } } }).CanResume);
    }

    [Fact]
    public async Task Default_off_host_rejects_for_each_start_and_resume()
    {
        var revision = WorkflowRevision.Create(PureDefinition(), "test");
        var store = Store();
        await using (var defaultHost = new CheckpointWorkflowHost(store))
            await Assert.ThrowsAsync<InvalidOperationException>(() => defaultHost.StartAsync(revision, "go", 2));
        await using (var loopOnly = new CheckpointWorkflowHost(store, new(EnableLoopCheckpointRecovery: true)))
            await Assert.ThrowsAsync<InvalidOperationException>(() => loopOnly.StartAsync(revision, "go", 2));
        Assert.Empty(store.List());

        RunSnapshot saved;
        await using (var host = new CheckpointWorkflowHost(store, OptIn))
            saved = await host.StartAsync(revision, "go", 2);
        var id = store.GetCheckpoint(saved.Header.RunId)!.Native.CheckpointId;
        await using var off = new CheckpointWorkflowHost(Store());
        await Assert.ThrowsAsync<InvalidOperationException>(() => off.ResumeAsync(saved.Header.RunId, id));
        Assert.Equal(saved.Events.ToArray(), Store().Get(saved.Header.RunId)!.Events.ToArray());
    }

    private static WorkflowDefinition WithBodyNode(string id, NodeDefinition node, string inPort, string outPort,
        bool itemInput) => PureDefinition() with
    {
        Nodes = PureDefinition().Nodes.Add(id, node),
        Connections = itemInput ? PureDefinition().Connections.Add("item-x", new(new("each", "item"), new(id, "in"), "data"))
            : PureDefinition().Connections,
        ExecutionConnections = PureDefinition().ExecutionConnections!
            .Remove("body-return")
            .Add("body-x", new(new("body", "then"), new(id, inPort), "execution"))
            .Add("x-feedback", new(new(id, outPort), new("each", "feedback"), "execution"))
    };

    public static TheoryData<string> RejectedShapes => new() { "agent", "wait", "break", "nested", "variables", "dynamic-array" };

    private static WorkflowDefinition Rejected(string shape)
    {
        switch (shape)
        {
            case "agent":
                return WithBodyNode("x", new(NodeTypes.Agent, "1.0.0", Agent: new("fixture", null, "Process input", [])), "execute", "out", true);
            case "wait":
            {
                // A top-level wait after the For Each is valid for the validator but never resumable.
                var definition = PureDefinition();
                return definition with
                {
                    Nodes = definition.Nodes.Add("wait", new(NodeTypes.Wait, "1.0.0") { Wait = new(1) }),
                    ExecutionConnections = definition.ExecutionConnections!.Remove("complete")
                        .Add("complete", new(new("each", "completed"), new("wait", "execute"), "execution"))
                        .Add("after-wait", new(new("wait", "then"), new("output", "execute"), "execution"))
                };
            }
            case "break":
            {
                var definition = PureDefinition();
                return definition with
                {
                    Nodes = definition.Nodes
                        .Add("cond", new(NodeTypes.Condition, "1.0.0") { Condition = new("contains", "b") })
                        .Add("break", new(NodeTypes.Break, "1.0.0")),
                    Connections = definition.Connections.Add("item-cond", new(new("each", "item"), new("cond", "in"), "data")),
                    ExecutionConnections = definition.ExecutionConnections!.Remove("body-return")
                        .Add("body-cond", new(new("body", "then"), new("cond", "execute"), "execution"))
                        .Add("cond-break", new(new("cond", "true"), new("break", "execute"), "execution"))
                        .Add("cond-return", new(new("cond", "false"), new("each", "feedback"), "execution"))
                };
            }
            case "variables":
            {
                var definition = PureDefinition();
                return definition with
                {
                    Variables = ImmutableSortedDictionary<string, WorkflowVariableDefinition>.Empty
                        .Add("message", new("Message", "default") { Type = WorkflowDataTypes.String })
                };
            }
            case "dynamic-array":
            {
                // The array arrives from the run input rather than a constant literal.
                var definition = PureDefinition();
                return definition with
                {
                    Nodes = definition.Nodes.Remove("items").SetItem("input", new(NodeTypes.Input, "1.0.0")
                        { InputDataType = WorkflowDataTypes.ArrayOf(WorkflowDataTypes.String) }),
                    Connections = definition.Connections.SetItem("items-each", new(new("input", "out"), new("each", "array"), "data"))
                };
            }
            default:
                return NestedPure();
        }
    }

    private static WorkflowDefinition NestedPure()
    {
        var nodes = new Dictionary<string, NodeDefinition>
        {
            ["input"] = new(NodeTypes.Input, "1.0.0"),
            ["outer-items"] = new(NodeTypes.ArrayLiteral, "1.0.0") { ArrayLiteral = new(WorkflowDataTypes.String, "[\"x\",\"y\"]") },
            ["inner-items"] = new(NodeTypes.ArrayLiteral, "1.0.0") { ArrayLiteral = new(WorkflowDataTypes.String, "[\"1\",\"2\"]") },
            ["outer"] = new(NodeTypes.ForEach, "1.0.0") { ArrayElementType = WorkflowDataTypes.String },
            ["inner"] = new(NodeTypes.ForEach, "1.0.0") { ArrayElementType = WorkflowDataTypes.String },
            ["outer-body"] = new(NodeTypes.LoopBody, "1.0.0"),
            ["inner-body"] = new(NodeTypes.LoopBody, "1.0.0"),
            ["done-text"] = new(NodeTypes.StringConstant, "1.0.0") { StringConstant = new("done") },
            ["output"] = new(NodeTypes.Output, "1.0.0")
        };
        var data = new Dictionary<string, ConnectionDefinition>
        {
            ["outer-array"] = new(new("outer-items", "out"), new("outer", "array"), "data"),
            ["inner-array"] = new(new("inner-items", "out"), new("inner", "array"), "data"),
            ["result"] = new(new("done-text", "out"), new("output", "in"), "data")
        };
        return new(WorkflowSchemaVersions.UnifiedWorkflowGraph, "for-each-nested", "input",
            nodes.ToImmutableSortedDictionary(), data.ToImmutableSortedDictionary())
        {
            ExecutionConnections = new Dictionary<string, ConnectionDefinition>
            {
                ["start"] = new(new("input", "then"), new("outer", "execute"), "execution"),
                ["outer-start"] = new(new("outer", "body"), new("outer-body", "execute"), "execution"),
                ["enter-inner"] = new(new("outer-body", "then"), new("inner", "execute"), "execution"),
                ["inner-start"] = new(new("inner", "body"), new("inner-body", "execute"), "execution"),
                ["inner-return"] = new(new("inner-body", "then"), new("inner", "feedback"), "execution"),
                ["outer-return"] = new(new("inner", "completed"), new("outer", "feedback"), "execution"),
                ["complete"] = new(new("outer", "completed"), new("output", "execute"), "execution")
            }.ToImmutableSortedDictionary()
        };
    }

    [Theory]
    [MemberData(nameof(RejectedShapes))]
    public async Task Opted_in_host_still_rejects_for_each_shapes_outside_the_first_slice(string shape)
    {
        var definition = Rejected(shape);
        var store = Store();
        await using var host = new CheckpointWorkflowHost(store, new(true, true));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            host.StartAsync(WorkflowRevision.Create(definition, "test"), "go", 2));
        Assert.Empty(store.List());
    }

    public void Dispose()
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, true);
    }
}
