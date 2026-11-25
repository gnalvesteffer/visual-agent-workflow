using System.Collections.Immutable;
using System.Text.Json;
using VisualAgentWorkflow.Core;
using Xunit;

namespace VisualAgentWorkflow.Core.Tests;

/// <summary>
/// Characterizes how Core readers treat an ExecutionEvent whose Kind they do not recognize (decision 0081,
/// "Unknown event kind behavior"). No Core reader rejects an unknown kind: each one ignores it. These tests pin
/// that current behavior so an additive kind such as <c>wait.requested</c> can be introduced deliberately.
/// </summary>
public sealed class UnknownEventKindTests
{
    private const string Future = "future.unknown-kind";
    /// <summary>A still-unrecognized additive "requested" kind. wait.* kinds are recognized since the Wait first slice
    /// (see WaitNodeTests); this stands in for the next additive kind and keeps the ignore-by-default hazard pinned.</summary>
    private const string Wait = "future.requested";

    private static readonly Guid RunId = Guid.NewGuid();
    private static readonly Guid RevisionId = Guid.NewGuid();

    private static ExecutionEvent Evt(long sequence, string kind, string? nodeId = null, Guid? invocation = null, string? data = null) =>
        new(RunId, RevisionId, sequence, DateTimeOffset.UtcNow, kind, nodeId, invocation, Data: data);

    private static RunSnapshot Snapshot(params ExecutionEvent[] events)
    {
        var definition = WorkflowJson.Deserialize("""
            {"schemaVersion":"1.0","workflowId":"fixture","entryNodeId":"input","nodes":{"input":{"type":"core.input","typeVersion":"1.0.0"},"output":{"type":"core.output","typeVersion":"1.0.0"}},"connections":{"edge":{"source":{"nodeId":"input","portId":"out"},"target":{"nodeId":"output","portId":"in"},"kind":"data"}}}
            """);
        return new(new(RunId, RevisionId, null, "fixture", DateTimeOffset.UtcNow, "hash", definition, "input"), [.. events]);
    }

    [Theory]
    [InlineData(Future)]
    [InlineData(Wait)]
    public void Unknown_event_kind_leaves_run_status_running_and_non_terminal(string kind)
    {
        var run = Snapshot(Evt(1, kind, "input", Guid.NewGuid()));
        Assert.Equal("running", run.Status);
        Assert.False(run.IsTerminal);
    }

    [Theory]
    [InlineData(Future)]
    [InlineData(Wait)]
    public void Unknown_event_kind_does_not_change_terminal_pause_suspension_or_approval_status(string kind)
    {
        Assert.Equal("completed", Snapshot(Evt(1, ExecutionEventKinds.Started), Evt(2, kind), Evt(3, ExecutionEventKinds.Completed)).Status);
        Assert.Equal("failed", Snapshot(Evt(1, ExecutionEventKinds.Started), Evt(2, ExecutionEventKinds.Failed), Evt(3, kind)).Status);
        Assert.Equal("paused", Snapshot(Evt(1, ExecutionEventKinds.Started), Evt(2, ExecutionEventKinds.Paused), Evt(3, kind)).Status);
        Assert.Equal("suspended", Snapshot(Evt(1, ExecutionEventKinds.Started), Evt(2, ExecutionEventKinds.Suspended), Evt(3, kind)).Status);
        Assert.Equal("waiting-approval", Snapshot(Evt(1, ExecutionEventKinds.Started), Evt(2, ExecutionEventKinds.ApprovalRequested), Evt(3, kind)).Status);
    }

    [Fact]
    public void Unknown_event_kind_as_the_only_event_is_running_not_queued()
    {
        // Only pause events are treated as "no work yet"; any other kind, including an unknown one, means running.
        Assert.Equal("queued", Snapshot().Status);
        Assert.Equal("running", Snapshot(Evt(1, Future)).Status);
    }

    [Theory]
    [InlineData(Future)]
    [InlineData(Wait)]
    public void Unknown_event_kind_is_ignored_by_work_evidence(string kind)
    {
        var node = Guid.NewGuid();
        var open = WorkflowWorkEvidence.Read(Snapshot(
            Evt(1, ExecutionEventKinds.NodeStarted, "input", node), Evt(2, kind, "input", node)));
        var item = Assert.Single(open.UnresolvedWork);
        Assert.Equal(UnresolvedWorkKind.Node, item.Kind);
        Assert.False(open.HasIncompleteCorrelation);

        var settled = WorkflowWorkEvidence.Read(Snapshot(
            Evt(1, ExecutionEventKinds.NodeStarted, "input", node), Evt(2, kind, "input", node),
            Evt(3, ExecutionEventKinds.NodeCompleted, "input", node)));
        Assert.Equal(0, settled.TotalUnresolvedWork);
    }

    [Fact]
    public void Unsettled_unrecognized_requested_kind_is_not_reported_as_unresolved_work_until_a_reader_names_it()
    {
        // Documents a forward-compatibility gap: an unrecognized "requested" kind can never surface as unresolved work.
        var report = WorkflowWorkEvidence.ReadAll(Snapshot(Evt(1, Wait, "input", Guid.NewGuid(),
            JsonSerializer.Serialize(new { waitId = Guid.NewGuid() }))));
        Assert.Equal(0, report.TotalUnresolvedWork);
        Assert.Empty(report.UnresolvedWork);
        Assert.False(report.HasIncompleteCorrelation);
    }

    [Fact]
    public void Unknown_event_kind_is_ignored_by_pending_approval_projection()
    {
        Assert.False(WorkflowApprovalEvidence.IsTransition(Future));
        Assert.Null(WorkflowApprovalEvidence.Pending(Snapshot(Evt(1, ExecutionEventKinds.Started), Evt(2, Future))));
    }

    [Fact]
    public void Unknown_event_kind_is_ignored_by_sequence_playback()
    {
        var sequenceId = Guid.NewGuid();
        var activation = Guid.NewGuid();
        ExecutionEvent Life(long sequence, string kind, SequenceLifecycleEvidence evidence) => new(RunId, RevisionId, sequence,
            DateTimeOffset.UtcNow, kind, evidence.SequenceNodeId, sequenceId, evidence.ConnectionId, SequenceEvidence.Serialize(evidence));
        var events = new[]
        {
            Life(1, ExecutionEventKinds.SequenceStarted, new(sequenceId, "sequence", null, null, null, null)),
            Evt(2, Wait, "sequence", sequenceId),
            Life(3, ExecutionEventKinds.SequenceOutputDispatched, new(sequenceId, "sequence", "first", 0, activation, "c")),
            Evt(4, Future, "sequence", sequenceId),
            Life(5, ExecutionEventKinds.SequenceOutputCompleted, new(sequenceId, "sequence", "first", 0, activation, "c")),
            Life(6, ExecutionEventKinds.SequenceCompleted, new(sequenceId, "sequence", null, null, null, null))
        };
        Assert.True(SequenceEvidence.TryReconstructPlayback(events, out var playback));
        Assert.Equal("completed", playback!.Status);
        Assert.False(SequenceEvidence.IsLifecycleEvent(Future));
        Assert.False(SequenceEvidence.TryReconstructPlayback([Evt(1, Future)], out _));
    }

    [Fact]
    public void Unknown_event_kind_is_ignored_by_variable_playback()
    {
        var write = Evt(1, ExecutionEventKinds.VariableWritten, "set", Guid.NewGuid(), JsonSerializer.Serialize(new
        { variableId = "count", dataType = "integer", previousValue = 1, value = 2, version = 2L }));
        Assert.True(VariableEvidence.TryReconstructPlayback([write, Evt(2, Future), Evt(3, Wait)], out var playback));
        Assert.Equal("2", playback!.Values["count"].Value);
        Assert.Single(playback.Operations);
        Assert.False(VariableEvidence.IsStateEvent(Future));
    }

    [Fact]
    public void Unknown_event_kind_is_ignored_by_workflow_call_playback()
    {
        var revision = Guid.NewGuid();
        var hash = new string('a', 64);
        ExecutionEvent Call(long sequence, string lifecycle) => new(RunId, RevisionId, sequence, DateTimeOffset.UtcNow,
            ExecutionEventKinds.SubworkflowEvidence, "call", Guid.NewGuid(), null,
            new WorkflowCallLifecycleEvidence(lifecycle, "call", "child", revision, hash, "call/child", null).Serialize())
        { ScopeId = "call/child" };
        ExecutionEvent[] events =
        [
            Call(1, ExecutionEventKinds.SubworkflowStarted), Evt(2, Future), Call(3, ExecutionEventKinds.SubworkflowCompleted),
            Evt(4, Wait), Evt(5, ExecutionEventKinds.Completed)
        ];
        Assert.True(WorkflowCallEvidence.TryReconstructPlayback(events, out var playback));
        Assert.Equal("completed", playback!.Status);
        Assert.False(WorkflowCallEvidence.TryReconstructPlayback([Evt(1, Future)], out _));
    }

    [Fact]
    public void Unknown_event_kind_is_covered_by_loop_checkpoint_evidence_hash_but_not_interpreted()
    {
        var revision = WorkflowRevision.Create(LoopVariableTopologyTests.Candidate(), "checkpoint-proof");
        var runId = Guid.NewGuid();
        List<ExecutionEvent> events =
        [
            new(runId, revision.RevisionId, 1, DateTimeOffset.UtcNow, ExecutionEventKinds.Started),
            new(runId, revision.RevisionId, 2, DateTimeOffset.UtcNow, ExecutionEventKinds.LoopIterationStarted, "loop",
                Data: JsonSerializer.Serialize(new { iteration = 1, maximumIterations = 3 })),
            new(runId, revision.RevisionId, 3, DateTimeOffset.UtcNow, Wait, "loop")
        ];
        var variables = new WorkflowVariableStore(revision.Definition).Snapshot();
        var frame = WorkflowCheckpointFrame.Create(revision, runId, "{}",
            new("loop", "body", 1, 3, "body", "execute", "loop", "body", 2), variables, events);

        frame.Validate(revision, events);
        // The unknown event is part of the hashed prefix, so a frame cannot be validated against a prefix that lacks it.
        Assert.Throws<InvalidDataException>(() => frame.Validate(revision, events.Take(2).ToList()));
    }
}
