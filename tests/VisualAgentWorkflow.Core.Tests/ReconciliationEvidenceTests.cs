using System.Collections.Immutable;
using System.Text.Json;
using Xunit;

namespace VisualAgentWorkflow.Core.Tests;

public sealed class ReconciliationEvidenceTests
{
    private static RunSnapshot Snapshot(params (string Kind, string? Data)[] records)
    {
        var definition = WorkflowJson.Deserialize("""
            {"schemaVersion":"1.0","workflowId":"fixture","entryNodeId":"input","nodes":{"input":{"type":"core.input","typeVersion":"1.0.0"},"output":{"type":"core.output","typeVersion":"1.0.0"}},"connections":{"edge":{"source":{"nodeId":"input","portId":"out"},"target":{"nodeId":"output","portId":"in"},"kind":"data"}}}
            """);
        var revision = WorkflowRevision.Create(definition, "fixture");
        var runId = Guid.NewGuid();
        var invocation = Guid.NewGuid();
        return new(new(runId, revision.RevisionId, null, "fixture", revision.CreatedAt, revision.ContentHash, definition, "private input"),
            records.Select((r, i) => new ExecutionEvent(runId, revision.RevisionId, i + 1, DateTimeOffset.UtcNow, r.Kind,
                "input", invocation, Data: r.Data)).ToImmutableArray());
    }
    private static string Tool(Guid id) => JsonSerializer.Serialize(new { toolCallId = id, input = "private input", output = "private output" });
    private static string Model(Guid id) => JsonSerializer.Serialize(new { modelCallId = id, instructions = "private instructions" });

    [Fact]
    public void TerminalStatusAndNodeFailureDoNotSettleExternalCalls()
    {
        var model = Guid.NewGuid();
        var tool = Guid.NewGuid();
        var run = Snapshot((ExecutionEventKinds.NodeStarted, null), (ExecutionEventKinds.ModelRequested, Model(model)),
            (ExecutionEventKinds.ToolRequested, Tool(tool)), (ExecutionEventKinds.ToolAuthorized, Tool(tool)),
            (ExecutionEventKinds.ToolUncertain, Tool(tool)), (ExecutionEventKinds.NodeFailed, null), (ExecutionEventKinds.Interrupted, null));
        var report = WorkflowWorkEvidence.Read(run);
        Assert.Equal(2, report.TotalUnresolvedWork);
        Assert.Contains(report.UnresolvedWork, w => w.Kind == UnresolvedWorkKind.Model && w.CallId == model);
        Assert.Contains(report.UnresolvedWork, w => w.Kind == UnresolvedWorkKind.Tool && w.State == UnresolvedWorkState.OutcomeUnknown && w.CallId == tool);
        Assert.False(report.HasIncompleteCorrelation);
        var json = JsonSerializer.Serialize(report);
        Assert.DoesNotContain("private", json);
    }

    [Fact]
    public void RepeatedCallsResolveOnlyTheirOwnCorrelationIdentity()
    {
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var run = Snapshot((ExecutionEventKinds.ToolRequested, Tool(first)), (ExecutionEventKinds.ToolAuthorized, Tool(first)),
            (ExecutionEventKinds.ToolUncertain, Tool(first)), (ExecutionEventKinds.ToolRequested, Tool(second)),
            (ExecutionEventKinds.ToolCompleted, Tool(second)));
        var report = WorkflowWorkEvidence.Read(run);
        Assert.Equal(first, Assert.Single(report.UnresolvedWork).CallId);
        var settled = run with { Events = run.Events.Add(run.Events[^1] with { Sequence = 6, Data = Tool(first) }) };
        Assert.Empty(WorkflowWorkEvidence.Read(settled).UnresolvedWork);
    }

    [Fact]
    public void PreDispatchFailureAndDenialAreDifferentFromFailureAfterAuthorization()
    {
        foreach (var kind in new[] { ExecutionEventKinds.ToolFailed, ExecutionEventKinds.ToolDenied })
        {
            var id = Guid.NewGuid();
            Assert.Empty(WorkflowWorkEvidence.Read(Snapshot((ExecutionEventKinds.ToolRequested, Tool(id)), (kind, Tool(id)))).UnresolvedWork);
        }
        var dispatched = Guid.NewGuid();
        var failed = Snapshot((ExecutionEventKinds.ToolRequested, Tool(dispatched)), (ExecutionEventKinds.ToolAuthorized, Tool(dispatched)),
            (ExecutionEventKinds.ToolFailed, Tool(dispatched)));
        Assert.Equal(UnresolvedWorkState.OutcomeUnknown, Assert.Single(WorkflowWorkEvidence.Read(failed).UnresolvedWork).State);
    }

    [Fact]
    public void LegacySerialEvidenceWorksButAmbiguousOrMalformedEvidenceFailsClosed()
    {
        var serial = Snapshot((ExecutionEventKinds.ToolRequested, "{}"), (ExecutionEventKinds.ToolCompleted, "{}"),
            (ExecutionEventKinds.ModelRequested, "{}"), (ExecutionEventKinds.ModelResponded, "{}"));
        Assert.Empty(WorkflowWorkEvidence.Read(serial).UnresolvedWork);
        Assert.False(WorkflowWorkEvidence.Read(serial).HasIncompleteCorrelation);
        var ambiguous = Snapshot((ExecutionEventKinds.ToolRequested, "{}"), (ExecutionEventKinds.ToolUncertain, "{}"),
            (ExecutionEventKinds.ToolCompleted, "{}"));
        Assert.Single(WorkflowWorkEvidence.Read(ambiguous).UnresolvedWork);
        Assert.True(WorkflowWorkEvidence.Read(ambiguous).HasIncompleteCorrelation);
        var malformed = Snapshot((ExecutionEventKinds.ModelRequested, "malformed private text"), (ExecutionEventKinds.ModelResponded, "{}"));
        Assert.True(WorkflowWorkEvidence.Read(malformed).HasIncompleteCorrelation);
    }

    [Fact]
    public void PendingApprovalsRemainVisibleAfterInterruptionWithoutGrantingAnything()
    {
        var id = Guid.NewGuid();
        var data = JsonSerializer.Serialize(new { requestId = id });
        var run = Snapshot((ExecutionEventKinds.ApprovalRequested, data), (ExecutionEventKinds.Interrupted, null));
        Assert.Equal(UnresolvedWorkState.AwaitingDecision, Assert.Single(WorkflowWorkEvidence.Read(run).UnresolvedWork).State);
        var cancelled = run with { Events = run.Events.Add(run.Events[0] with { Sequence = 3, Kind = ExecutionEventKinds.ApprovalCancelled }) };
        Assert.Empty(WorkflowWorkEvidence.Read(cancelled).UnresolvedWork);
    }

    [Fact]
    public void ReturnedDetailsAreBoundedWithoutHidingTotalUnresolvedWork()
    {
        var run = Snapshot(Enumerable.Range(0, 300).Select(_ => (ExecutionEventKinds.ModelRequested, (string?)Model(Guid.NewGuid()))).ToArray());
        var report = WorkflowWorkEvidence.Read(run);
        Assert.Equal(300, report.TotalUnresolvedWork);
        Assert.Equal(256, report.UnresolvedWork.Length);
        Assert.False(report.HasIncompleteCorrelation);
    }
}
