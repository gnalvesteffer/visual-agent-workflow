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

    private static IReadOnlyList<ExecutionEvent> Events(params EvidenceEvent[] source) => source.Select((item, index) =>
        new ExecutionEvent(Guid.NewGuid(), Guid.NewGuid(), index + 1, DateTimeOffset.UtcNow, item.Kind,
            item.NodeId, Guid.NewGuid(), null,
            item.Kind == ExecutionEventKinds.SubworkflowEvidence
                ? new WorkflowCallLifecycleEvidence(item.Lifecycle!, item.CallNodeId!, item.WorkflowId!, item.RevisionId,
                    item.Hash!, item.ScopeId!, null).Serialize()
                : item.Data) { ScopeId = item.ScopeId }).ToArray();

    private sealed record EvidenceEvent(string Kind, string? NodeId, string? ScopeId, string? WorkflowId,
        Guid RevisionId, string? Hash, string? Lifecycle = null, string? CallNodeId = null, string? Data = null);
}
