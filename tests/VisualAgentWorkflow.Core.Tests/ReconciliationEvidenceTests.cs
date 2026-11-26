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
    public void RecoverableToolFeedbackSettlesOnlyThatFailedAttempt()
    {
        var id = Guid.NewGuid();
        var failed = JsonSerializer.Serialize(new { toolCallId = id, disposition = ToolInvocationFailureDisposition.RecoverableFeedback.ToString() });
        var run = Snapshot((ExecutionEventKinds.ToolRequested, Tool(id)), (ExecutionEventKinds.ToolAuthorized, Tool(id)),
            (ExecutionEventKinds.ToolFailed, failed));
        Assert.Empty(WorkflowWorkEvidence.Read(run).UnresolvedWork);
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

    private static string WaitPayload(Guid id) => JsonSerializer.Serialize(new { waitId = id, nodeId = "input", durationMilliseconds = 1000 });

    [Fact]
    public void SeveralUnsettledWaitsOfOneNodeAndInvocationStayDistinctByWaitId()
    {
        var ids = new[] { Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid() };
        var run = Snapshot(ids.Select(id => (ExecutionEventKinds.WaitRequested, (string?)WaitPayload(id))).ToArray());

        var report = WorkflowWorkEvidence.Read(run);

        Assert.Equal(3, report.TotalUnresolvedWork);
        Assert.Equal(ids.Order(), report.UnresolvedWork.Select(work => work.CallId!.Value).Order());
        Assert.False(report.HasIncompleteCorrelation);
    }

    [Fact]
    public void AMixOfSettledAndUnsettledWaitsReportsOnlyTheUnsettledOnes()
    {
        var elapsed = Guid.NewGuid();
        var cancelled = Guid.NewGuid();
        var open1 = Guid.NewGuid();
        var open2 = Guid.NewGuid();
        var run = Snapshot(
            (ExecutionEventKinds.WaitRequested, WaitPayload(elapsed)), (ExecutionEventKinds.WaitRequested, WaitPayload(open1)),
            (ExecutionEventKinds.WaitRequested, WaitPayload(cancelled)), (ExecutionEventKinds.WaitElapsed, WaitPayload(elapsed)),
            (ExecutionEventKinds.WaitRequested, WaitPayload(open2)), (ExecutionEventKinds.WaitCancelled, WaitPayload(cancelled)));

        var report = WorkflowWorkEvidence.Read(run);

        Assert.Equal(new[] { open1, open2 }.Order(), report.UnresolvedWork.Select(work => work.CallId!.Value).Order());
        Assert.False(report.HasIncompleteCorrelation);
        // A settlement of one wait never resolves another wait of the same node and invocation.
        var settledOne = run with { Events = run.Events.Add(run.Events[0] with { Sequence = 7, Kind = ExecutionEventKinds.WaitInterrupted, Data = WaitPayload(open1) }) };
        Assert.Equal(open2, Assert.Single(WorkflowWorkEvidence.Read(settledOne).UnresolvedWork).CallId);
    }
    // Decision 0070, slice (b) unblocker 1: a WorkflowCall child's node events carry the child scope and child node IDs.
    private static WorkflowDefinition ChildDefinition(string workflowId, params (string Id, NodeDefinition Node)[] extra) => new(
        WorkflowSchemaVersions.UnifiedWorkflowGraph, workflowId, "input",
        new Dictionary<string, NodeDefinition> { ["input"] = new(NodeTypes.Input, "1.0.0"), ["output"] = new(NodeTypes.Output, "1.0.0") }
            .Concat(extra.Select(node => KeyValuePair.Create(node.Id, node.Node))).ToImmutableSortedDictionary(),
        ImmutableSortedDictionary<string, ConnectionDefinition>.Empty);

    private static NodeDefinition CallNode(WorkflowDefinition child, Guid revision, string hash) =>
        new(NodeTypes.WorkflowCall, "1.0.0") { WorkflowCall = new(child.WorkflowId, revision, 1, hash, child) };

    private sealed class CallJournal(WorkflowDefinition root)
    {
        private readonly Guid runId = Guid.NewGuid();
        private readonly Guid revisionId = Guid.NewGuid();
        private readonly List<ExecutionEvent> events = [];

        public CallJournal Add(string kind, string? scope, string? node, Guid? invocation, string? data = null)
        {
            events.Add(new ExecutionEvent(runId, revisionId, events.Count + 1, DateTimeOffset.UnixEpoch, kind, node, invocation,
                Data: data) { ScopeId = scope });
            return this;
        }

        public CallJournal Node(string? scope, string node, Guid invocation, string settlement = ExecutionEventKinds.NodeCompleted) =>
            Add(ExecutionEventKinds.NodeStarted, scope, node, invocation).Add(settlement, scope, node, invocation);

        public CallJournal Lifecycle(string lifecycle, string scope, string callNode, WorkflowDefinition child, Guid revision, string hash)
        {
            var invocation = Guid.NewGuid();
            return Add(lifecycle, scope, null, invocation).Add(ExecutionEventKinds.SubworkflowEvidence, scope, callNode, invocation,
                new WorkflowCallLifecycleEvidence(lifecycle, callNode, child.WorkflowId, revision, hash, scope, null).Serialize());
        }

        public RunSnapshot Snapshot() => new(new(runId, revisionId, null, "fixture", DateTimeOffset.UnixEpoch, new string('f', 64), root,
            "private input"), [.. events]);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("each/1")]
    public void ACompletedRunWithAWorkflowCallChildHasNoUnresolvedWork(string? activationScope)
    {
        var childRevision = Guid.NewGuid();
        var childHash = new string('c', 64);
        var child = ChildDefinition("child", ("worker", new(NodeTypes.Agent, "1.0.0")));
        var root = ChildDefinition("parent", ("call", CallNode(child, childRevision, childHash)));
        var callScope = activationScope is null ? "call" : $"{activationScope}/call";
        var callInvocation = Guid.NewGuid();
        var worker = Guid.NewGuid();
        var tool = Guid.NewGuid();
        var run = new CallJournal(root)
            .Add(ExecutionEventKinds.Started, null, null, null)
            .Node(null, "input", Guid.NewGuid())
            .Add(ExecutionEventKinds.NodeStarted, activationScope, "call", callInvocation)
            .Lifecycle(ExecutionEventKinds.SubworkflowStarted, callScope, "call", child, childRevision, childHash)
            .Node(callScope, "input", Guid.NewGuid())
            .Add(ExecutionEventKinds.NodeStarted, callScope, "worker", worker)
            .Add(ExecutionEventKinds.ToolRequested, callScope, "worker", worker, Tool(tool))
            .Add(ExecutionEventKinds.ToolAuthorized, callScope, "worker", worker, Tool(tool))
            .Add(ExecutionEventKinds.ToolCompleted, callScope, "worker", worker, Tool(tool))
            .Add(ExecutionEventKinds.NodeCompleted, callScope, "worker", worker)
            .Node(callScope, "output", Guid.NewGuid())
            .Lifecycle(ExecutionEventKinds.SubworkflowCompleted, callScope, "call", child, childRevision, childHash)
            .Add(ExecutionEventKinds.NodeCompleted, activationScope, "call", callInvocation)
            .Node(null, "output", Guid.NewGuid())
            .Add(ExecutionEventKinds.Completed, null, null, null)
            .Snapshot();

        var report = WorkflowWorkEvidence.Read(run);

        Assert.Equal("completed", run.Status);
        Assert.False(report.HasIncompleteCorrelation);
        Assert.Equal(0, report.TotalUnresolvedWork);
        Assert.Empty(report.UnresolvedWork);
    }

    [Fact]
    public void AGrandchildWorkflowCallIsCorrelatedAgainstTheChildThatEmbedsIt()
    {
        var innerRevision = Guid.NewGuid();
        var innerHash = new string('d', 64);
        var inner = ChildDefinition("inner", ("leaf", new(NodeTypes.Agent, "1.0.0")));
        var childRevision = Guid.NewGuid();
        var childHash = new string('c', 64);
        var child = ChildDefinition("child", ("nested", CallNode(inner, innerRevision, innerHash)));
        var root = ChildDefinition("parent", ("call", CallNode(child, childRevision, childHash)));
        var nested = Guid.NewGuid();
        var run = new CallJournal(root)
            .Lifecycle(ExecutionEventKinds.SubworkflowStarted, "call", "call", child, childRevision, childHash)
            .Add(ExecutionEventKinds.NodeStarted, "call", "nested", nested)
            .Lifecycle(ExecutionEventKinds.SubworkflowStarted, "call/nested", "nested", inner, innerRevision, innerHash)
            .Node("call/nested", "leaf", Guid.NewGuid())
            .Lifecycle(ExecutionEventKinds.SubworkflowCompleted, "call/nested", "nested", inner, innerRevision, innerHash)
            .Add(ExecutionEventKinds.NodeCompleted, "call", "nested", nested)
            .Lifecycle(ExecutionEventKinds.SubworkflowCompleted, "call", "call", child, childRevision, childHash)
            .Add(ExecutionEventKinds.Completed, null, null, null)
            .Snapshot();

        var report = WorkflowWorkEvidence.Read(run);

        Assert.False(report.HasIncompleteCorrelation);
        Assert.Equal(0, report.TotalUnresolvedWork);
    }

    [Fact]
    public void AChildToolThatNeverSettledRemainsUnresolvedWithoutAParentNodeIdentity()
    {
        var childRevision = Guid.NewGuid();
        var childHash = new string('c', 64);
        // The child's tool node shares an ID with a parent node: it must not be reported as the parent's node.
        var child = ChildDefinition("child", ("worker", new(NodeTypes.Agent, "1.0.0")));
        var root = ChildDefinition("parent", ("call", CallNode(child, childRevision, childHash)), ("worker", new(NodeTypes.Agent, "1.0.0")));
        var worker = Guid.NewGuid();
        var tool = Guid.NewGuid();
        var run = new CallJournal(root)
            .Lifecycle(ExecutionEventKinds.SubworkflowStarted, "call", "call", child, childRevision, childHash)
            .Add(ExecutionEventKinds.NodeStarted, "call", "worker", worker)
            .Add(ExecutionEventKinds.ToolRequested, "call", "worker", worker, Tool(tool))
            .Add(ExecutionEventKinds.ToolAuthorized, "call", "worker", worker, Tool(tool))
            .Add(ExecutionEventKinds.NodeCompleted, "call", "worker", worker)
            .Lifecycle(ExecutionEventKinds.SubworkflowCompleted, "call", "call", child, childRevision, childHash)
            .Add(ExecutionEventKinds.Completed, null, null, null)
            .Snapshot();

        var report = WorkflowWorkEvidence.Read(run);

        Assert.False(report.HasIncompleteCorrelation);
        var unresolved = Assert.Single(report.UnresolvedWork);
        Assert.Equal((UnresolvedWorkKind.Tool, UnresolvedWorkState.AwaitingOutcome, (string?)null, (Guid?)worker, (Guid?)tool, 4L),
            (unresolved.Kind, unresolved.State, unresolved.NodeId, unresolved.InvocationId, unresolved.CallId, unresolved.RequestedSequence));
        // A settlement recorded in another scope never settles the child's call.
        var parentSettlement = run with { Events = run.Events.Add(run.Events[3] with { Sequence = run.Events.Length + 1,
            Kind = ExecutionEventKinds.ToolCompleted, ScopeId = null }) };
        Assert.Contains(WorkflowWorkEvidence.Read(parentSettlement).UnresolvedWork, work => work.CallId == tool);
    }

    [Fact]
    public void ChildScopedNodeEventsWithoutMatchingCallEvidenceFailClosed()
    {
        var childRevision = Guid.NewGuid();
        var childHash = new string('c', 64);
        var child = ChildDefinition("child", ("worker", new(NodeTypes.Agent, "1.0.0")));
        var root = ChildDefinition("parent", ("call", CallNode(child, childRevision, childHash)));

        // No lifecycle evidence: the scope is not a known child, so the parent definition applies.
        var undeclared = new CallJournal(root).Node("call", "worker", Guid.NewGuid()).Snapshot();
        // Evidence naming another revision than the pinned call does not identify the child definition.
        var mismatched = new CallJournal(root)
            .Lifecycle(ExecutionEventKinds.SubworkflowStarted, "call", "call", child, Guid.NewGuid(), childHash)
            .Node("call", "worker", Guid.NewGuid()).Snapshot();
        // A node the child definition does not contain stays incomplete under a known child scope.
        var unknownNode = new CallJournal(root)
            .Lifecycle(ExecutionEventKinds.SubworkflowStarted, "call", "call", child, childRevision, childHash)
            .Node("call", "ghost", Guid.NewGuid()).Snapshot();

        Assert.True(WorkflowWorkEvidence.Read(undeclared).HasIncompleteCorrelation);
        Assert.True(WorkflowWorkEvidence.Read(mismatched).HasIncompleteCorrelation);
        Assert.True(WorkflowWorkEvidence.Read(unknownNode).HasIncompleteCorrelation);
    }

    [Fact]
    public void ProductionSequenceRuntimeExecutorsAreRecognizedOnlyInADefinitionWithASequence()
    {
        var sequence = ChildDefinition("sequence-graph", ("sequence", new(NodeTypes.Sequence, "1.0.0")));
        var completed = new CallJournal(sequence)
            .Node(null, "sequence", Guid.NewGuid())
            .Node("sequence/first", "sequence-collector", Guid.NewGuid())
            .Node(null, "sequence-terminal", Guid.NewGuid())
            .Add(ExecutionEventKinds.Completed, null, null, null)
            .Snapshot();
        var collector = Guid.NewGuid();
        var unsettled = new CallJournal(sequence).Add(ExecutionEventKinds.NodeStarted, "sequence/first", "sequence-collector", collector)
            .Add(ExecutionEventKinds.Failed, null, null, null).Snapshot();
        var noSequence = new CallJournal(ChildDefinition("plain")).Node(null, "sequence-collector", Guid.NewGuid()).Snapshot();

        Assert.Equal((0, false), (WorkflowWorkEvidence.Read(completed).TotalUnresolvedWork, WorkflowWorkEvidence.Read(completed).HasIncompleteCorrelation));
        var open = Assert.Single(WorkflowWorkEvidence.Read(unsettled).UnresolvedWork);
        Assert.Equal((UnresolvedWorkKind.Node, (string?)null, (Guid?)collector), (open.Kind, open.NodeId, open.InvocationId));
        Assert.False(WorkflowWorkEvidence.Read(unsettled).HasIncompleteCorrelation);
        Assert.True(WorkflowWorkEvidence.Read(noSequence).HasIncompleteCorrelation);
    }
}
