using VisualAgentWorkflow.Core;
using Xunit;

namespace VisualAgentWorkflow.Core.Tests;

public sealed class WorkflowCallEvidenceTests
{
    [Fact]
    public void Reconstructs_nested_call_identity_and_terminal_state_without_execution()
    {
        var revision = Guid.NewGuid();
        var hash = new string('a', 64);
        var events = Events(
            new(ExecutionEventKinds.SubworkflowEvidence, "call", "call", "child", revision, hash,
                ExecutionEventKinds.SubworkflowStarted, "call"),
            new(ExecutionEventKinds.SubworkflowEvidence, "call", "call", "child", revision, hash,
                ExecutionEventKinds.SubworkflowOutcomeUnknown, "call"),
            new(ExecutionEventKinds.Failed, null, null, null, Guid.Empty, null));

        Assert.True(WorkflowCallEvidence.TryReconstructPlayback(events, out var playback));
        Assert.Equal("failed", playback!.Status);
        var call = Assert.Single(playback.Calls);
        Assert.Equal("unknown", call.State);
        Assert.Equal(revision, call.ChildRevisionId);
        Assert.Equal(hash, call.ChildContentHash);
    }

    [Fact]
    public void Rejects_parent_success_with_unknown_or_late_child_evidence()
    {
        var revision = Guid.NewGuid();
        var hash = new string('b', 64);
        var unknown = Events(
            new(ExecutionEventKinds.SubworkflowEvidence, "call", "call", "child", revision, hash,
                ExecutionEventKinds.SubworkflowStarted, "call"),
            new(ExecutionEventKinds.SubworkflowEvidence, "call", "call", "child", revision, hash,
                ExecutionEventKinds.SubworkflowOutcomeUnknown, "call"),
            new(ExecutionEventKinds.Completed, null, null, null, Guid.Empty, null));
        var late = Events(
            new(ExecutionEventKinds.SubworkflowEvidence, "call", "call", "child", revision, hash,
                ExecutionEventKinds.SubworkflowStarted, "call"),
            new(ExecutionEventKinds.SubworkflowEvidence, "call", "call", "child", revision, hash,
                ExecutionEventKinds.SubworkflowCancelled, "call"),
            new(ExecutionEventKinds.SubworkflowEvidence, "call", "call", "child", revision, hash,
                ExecutionEventKinds.SubworkflowCompleted, "call"));

        Assert.False(WorkflowCallEvidence.TryReconstructPlayback(unknown, out _));
        Assert.False(WorkflowCallEvidence.TryReconstructPlayback(late, out _));
    }

    [Fact]
    public void Rejects_child_evidence_after_root_terminal()
    {
        var revision = Guid.NewGuid();
        var hash = new string('c', 64);
        // A brand-new child start after the root terminal used to project "completed" plus a running call.
        var lateStart = Events(
            Call("call", "call/child", revision, hash, ExecutionEventKinds.SubworkflowStarted),
            Call("call", "call/child", revision, hash, ExecutionEventKinds.SubworkflowCompleted),
            Root(ExecutionEventKinds.Completed),
            Call("call2", "call2/child2", revision, hash, ExecutionEventKinds.SubworkflowStarted));
        // A duplicated terminal for an already-settled call after the root terminal is rejected as well.
        var lateTerminal = Events(
            Call("call", "call/child", revision, hash, ExecutionEventKinds.SubworkflowStarted),
            Call("call", "call/child", revision, hash, ExecutionEventKinds.SubworkflowCompleted),
            Root(ExecutionEventKinds.Failed),
            Call("call", "call/child", revision, hash, ExecutionEventKinds.SubworkflowCancelled));

        Assert.False(WorkflowCallEvidence.TryReconstructPlayback(lateStart, out _));
        Assert.False(WorkflowCallEvidence.TryReconstructPlayback(lateTerminal, out _));
    }

    [Fact]
    public void Projects_interrupted_run_with_last_known_call_states()
    {
        var revision = Guid.NewGuid();
        var hash = new string('d', 64);
        // An interrupted run is a process-death fact: the in-flight call keeps its last-known state.
        var single = Events(
            Call("call", "call/child", revision, hash, ExecutionEventKinds.SubworkflowStarted),
            Root(ExecutionEventKinds.Interrupted));

        Assert.True(WorkflowCallEvidence.TryReconstructPlayback(single, out var playback));
        Assert.Equal("interrupted", playback!.Status);
        var call = Assert.Single(playback.Calls);
        Assert.Equal("running", call.State);

        // A settled sibling is neither upgraded nor downgraded by the interruption.
        var multi = Events(
            Call("call-a", "call-a/child-a", revision, hash, ExecutionEventKinds.SubworkflowStarted),
            Call("call-a", "call-a/child-a", revision, hash, ExecutionEventKinds.SubworkflowCompleted),
            Call("call-b", "call-b/child-b", revision, hash, ExecutionEventKinds.SubworkflowStarted),
            Root(ExecutionEventKinds.Interrupted));

        Assert.True(WorkflowCallEvidence.TryReconstructPlayback(multi, out var multiPlayback));
        Assert.Equal("interrupted", multiPlayback!.Status);
        Assert.Equal("completed", multiPlayback.Calls.First(item => item.ChildScopeId == "call-a/child-a").State);
        Assert.Equal("running", multiPlayback.Calls.First(item => item.ChildScopeId == "call-b/child-b").State);
    }

    [Fact]
    public void Projects_crash_prefix_without_root_terminal_as_running()
    {
        var revision = Guid.NewGuid();
        var hash = new string('e', 64);
        // A committed prefix that ends mid-execution (the process died between commits) is honest evidence:
        // it projects "running" with each call in its last-known state and never fabricates an outcome.
        var multi = Events(
            Call("call-a", "call-a/child-a", revision, hash, ExecutionEventKinds.SubworkflowStarted),
            Call("call-a", "call-a/child-a", revision, hash, ExecutionEventKinds.SubworkflowCompleted),
            Call("call-b", "call-b/child-b", revision, hash, ExecutionEventKinds.SubworkflowStarted));

        Assert.True(WorkflowCallEvidence.TryReconstructPlayback(multi, out var playback));
        Assert.Equal("running", playback!.Status);
        Assert.Equal("completed", playback.Calls.First(item => item.ChildScopeId == "call-a/child-a").State);
        Assert.Equal("running", playback.Calls.First(item => item.ChildScopeId == "call-b/child-b").State);

        var single = Events(Call("call", "call/child", revision, hash, ExecutionEventKinds.SubworkflowStarted));
        Assert.True(WorkflowCallEvidence.TryReconstructPlayback(single, out var singlePlayback));
        Assert.Equal("running", singlePlayback!.Status);
        Assert.Equal("running", Assert.Single(singlePlayback.Calls).State);
    }

    [Fact]
    public void Rejects_identity_drift_between_evidence_events_of_one_call()
    {
        // Tampered storage: one field of the child identity changes between the start and terminal evidence.
        var hash = new string('f', 64);
        var otherHash = new string('0', 64);
        var revision = Guid.NewGuid();
        var otherRevision = Guid.NewGuid();

        Assert.False(WorkflowCallEvidence.TryReconstructPlayback(Events(
            Call("call", "call/child", revision, hash, ExecutionEventKinds.SubworkflowStarted),
            Call("call", "call/child", revision, otherHash, ExecutionEventKinds.SubworkflowCompleted)), out _));
        Assert.False(WorkflowCallEvidence.TryReconstructPlayback(Events(
            Call("call", "call/child", revision, hash, ExecutionEventKinds.SubworkflowStarted),
            Call("call", "call/child", otherRevision, hash, ExecutionEventKinds.SubworkflowCompleted)), out _));
        var drifted = Events(
            new EvidenceEvent(ExecutionEventKinds.SubworkflowEvidence, "call", "call/child", "child-workflow-a", revision, hash,
                ExecutionEventKinds.SubworkflowStarted, "call"),
            new EvidenceEvent(ExecutionEventKinds.SubworkflowEvidence, "call", "call/child", "child-workflow-b", revision, hash,
                ExecutionEventKinds.SubworkflowCompleted, "call"));
        Assert.False(WorkflowCallEvidence.TryReconstructPlayback(drifted, out _));
    }

    [Fact]
    public void Rejects_envelope_mismatch_between_event_and_payload()
    {
        // The event's ScopeId/NodeId must agree with the payload's ChildScopeId/CallNodeId; cross-wired records are tampering.
        var revision = Guid.NewGuid();
        var hash = new string('9', 64);

        // The payload itself is well-formed (it satisfies TryRead), but the event ScopeId must match the payload ChildScopeId.
        Assert.False(WorkflowCallEvidence.TryReconstructPlayback(Events(
            new EvidenceEvent(ExecutionEventKinds.SubworkflowEvidence, "call", "call/y", "child-workflow", revision, hash,
                ExecutionEventKinds.SubworkflowStarted, "call", PayloadScopeId: "call/x")), out _));
        // And the event NodeId must match the payload CallNodeId.
        Assert.False(WorkflowCallEvidence.TryReconstructPlayback(Events(
            new EvidenceEvent(ExecutionEventKinds.SubworkflowEvidence, "other-node", "call/child", "child-workflow", revision, hash,
                ExecutionEventKinds.SubworkflowStarted, "call")), out _));
    }

    [Fact]
    public void Rejects_duplicate_start_and_terminal_before_start()
    {
        // A duplicate start has no write-time counterpart (the runner's settlement guard covers terminals only),
        // so playback rejection is the only defense for that tamper class.
        var revision = Guid.NewGuid();
        var hash = new string('a', 64);

        Assert.False(WorkflowCallEvidence.TryReconstructPlayback(Events(
            Call("call", "call/child", revision, hash, ExecutionEventKinds.SubworkflowStarted),
            Call("call", "call/child", revision, hash, ExecutionEventKinds.SubworkflowStarted)), out _));
        Assert.False(WorkflowCallEvidence.TryReconstructPlayback(Events(
            Call("call", "call/child", revision, hash, ExecutionEventKinds.SubworkflowCompleted)), out _));
    }

    [Fact]
    public void Ignores_non_subworkflow_scoped_events()
    {
        // Scoped events that are not subworkflow.evidence belong to child-scope semantics owned by other projections;
        // call playback accepts and ignores them rather than rejecting honest journals.
        var revision = Guid.NewGuid();
        var hash = new string('b', 64);

        var events = Events(
            Call("call", "call/child", revision, hash, ExecutionEventKinds.SubworkflowStarted),
            new EvidenceEvent(ExecutionEventKinds.NodeCompleted, "child-node", "call/child-node", null, Guid.Empty, null),
            Call("call", "call/child", revision, hash, ExecutionEventKinds.SubworkflowCompleted),
            Root(ExecutionEventKinds.Completed));

        Assert.True(WorkflowCallEvidence.TryReconstructPlayback(events, out var playback));
        Assert.Equal("completed", playback!.Status);
    }

    private static IReadOnlyList<ExecutionEvent> Events(params EvidenceEvent[] source) => source.Select((item, index) =>
        new ExecutionEvent(Guid.NewGuid(), Guid.NewGuid(), index + 1, DateTimeOffset.UtcNow, item.Kind,
            item.NodeId, Guid.NewGuid(), null,
            item.Kind == ExecutionEventKinds.SubworkflowEvidence
                ? new WorkflowCallLifecycleEvidence(item.Lifecycle!, item.PayloadCallNodeId ?? item.CallNodeId!, item.WorkflowId!, item.RevisionId,
                    item.Hash!, item.PayloadScopeId ?? item.ScopeId!, null).Serialize()
                : item.Data) { ScopeId = item.ScopeId }).ToArray();

    private static EvidenceEvent Call(string nodeId, string scope, Guid revision, string hash, string lifecycle) =>
        new(ExecutionEventKinds.SubworkflowEvidence, nodeId, scope, "child-workflow", revision, hash, lifecycle, nodeId);

    private static EvidenceEvent Root(string kind) => new(kind, null, null, null, Guid.Empty, null);

    private sealed record EvidenceEvent(string Kind, string? NodeId, string? ScopeId, string? WorkflowId,
        Guid RevisionId, string? Hash, string? Lifecycle = null, string? CallNodeId = null, string? Data = null,
        string? PayloadScopeId = null, string? PayloadCallNodeId = null);
}
