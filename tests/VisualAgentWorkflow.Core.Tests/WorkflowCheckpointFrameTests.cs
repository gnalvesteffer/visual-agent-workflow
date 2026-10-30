using System.Text.Json;
using System.Collections.Immutable;
using VisualAgentWorkflow.Core;
using Xunit;

namespace VisualAgentWorkflow.Core.Tests;

public sealed class WorkflowCheckpointFrameTests
{
    [Theory]
    [InlineData(WorkflowSchemaVersions.LoopVariablesGraph)]
    [InlineData(WorkflowSchemaVersions.UnifiedWorkflowGraph)]
    public void Proof_frame_binds_loop_cursor_variables_and_evidence_prefix(string schemaVersion)
    {
        var revision = WorkflowRevision.Create(LoopVariableTopologyTests.Candidate() with { SchemaVersion = schemaVersion }, "checkpoint-proof");
        var runId = Guid.NewGuid();
        var events = Evidence(runId, revision.RevisionId);
        var variables = new WorkflowVariableStore(revision.Definition).Snapshot();
        var frame = WorkflowCheckpointFrame.Create(revision, runId, "{}",
            new("loop", "body", 1, 3, "body", "execute", "loop", "body", 2), variables, events);

        frame.Validate(revision, events);
        Assert.Equal(WorkflowCheckpointFrame.CurrentFrameVersion, frame.FrameVersion);
        Assert.Equal(schemaVersion, frame.WorkflowSchemaVersion);
        Assert.Equal(2, frame.EvidenceCursor);
        Assert.Equal(64, frame.FrameHash!.Length);
    }

    [Fact]
    public void Proof_frame_rejects_tampered_native_state_evidence_or_cursor()
    {
        var revision = WorkflowRevision.Create(LoopVariableTopologyTests.Candidate(), "checkpoint-proof");
        var runId = Guid.NewGuid();
        var events = Evidence(runId, revision.RevisionId);
        var variables = new WorkflowVariableStore(revision.Definition).Snapshot();
        var frame = WorkflowCheckpointFrame.Create(revision, runId, "{}",
            new("loop", "body", 1, 3, "body", "execute", "loop", "body", 2), variables, events);

        Assert.Throws<InvalidDataException>(() => (frame with { NativePayload = "{\"changed\":true}" }).Validate(revision, events));
        Assert.Throws<InvalidDataException>(() => frame.Validate(revision, [.. events, new(runId, revision.RevisionId, 3, DateTimeOffset.UtcNow, ExecutionEventKinds.NodeStarted, "body")]));
        Assert.Throws<InvalidDataException>(() => (frame with { Loop = frame.Loop with { NextInputPortId = "missing" } }).Validate(revision, events));
    }

    [Fact]
    public void Loop_frame_rejects_an_unknown_compatibility_version()
    {
        var revision = WorkflowRevision.Create(LoopVariableTopologyTests.Candidate(), "checkpoint-proof");
        var runId = Guid.NewGuid();
        var events = Evidence(runId, revision.RevisionId);
        var variables = new WorkflowVariableStore(revision.Definition).Snapshot();
        var frame = WorkflowCheckpointFrame.Create(revision, runId, "{}",
            new("loop", "body", 1, 3, "body", "execute", "loop", "body", 2), variables, events);
        var incompatible = frame with { FrameVersion = "future", FrameHash = null };
        incompatible = incompatible with { FrameHash = incompatible.ComputeFrameHash() };

        Assert.Throws<InvalidDataException>(() => incompatible.Validate(revision, events));
    }

    [Fact]
    public void Proof_frame_rejects_malformed_and_oversized_native_payloads()
    {
        var revision = WorkflowRevision.Create(LoopVariableTopologyTests.Candidate(), "checkpoint-proof");
        var runId = Guid.NewGuid();
        var events = Evidence(runId, revision.RevisionId);
        var variables = new WorkflowVariableStore(revision.Definition).Snapshot();
        var frame = WorkflowCheckpointFrame.Create(revision, runId, "{}",
            new("loop", "body", 1, 3, "body", "execute", "loop", "body", 2), variables, events);

        Assert.Throws<InvalidDataException>(() => (frame with { NativePayload = "[]" }).Validate(revision, events));
        Assert.Throws<InvalidDataException>(() => (frame with { NativePayload = new string('x', 1_048_577) }).Validate(revision, events));
    }

    [Fact]
    public void Proof_frame_rejects_foreign_run_identity()
    {
        var revision = WorkflowRevision.Create(LoopVariableTopologyTests.Candidate(), "checkpoint-proof");
        var runId = Guid.NewGuid();
        var events = Evidence(runId, revision.RevisionId);
        var variables = new WorkflowVariableStore(revision.Definition).Snapshot();
        var frame = WorkflowCheckpointFrame.Create(revision, runId, "{}",
            new("loop", "body", 1, 3, "body", "execute", "loop", "body", 2), variables, events);
        var foreign = frame with { RunId = Guid.NewGuid(), FrameHash = null };
        foreign = foreign with { FrameHash = foreign.ComputeFrameHash() };

        Assert.Throws<InvalidDataException>(() => foreign.Validate(revision, events));
    }

    [Fact]
    public void Proof_frame_rejects_cursor_that_does_not_match_recorded_iteration()
    {
        var revision = WorkflowRevision.Create(LoopVariableTopologyTests.Candidate(), "checkpoint-proof");
        var runId = Guid.NewGuid();
        var events = Evidence(runId, revision.RevisionId);
        var variables = new WorkflowVariableStore(revision.Definition).Snapshot();
        var frame = WorkflowCheckpointFrame.Create(revision, runId, "{}",
            new("loop", "body", 2, 3, "body", "execute", "loop", "body", 2), variables, events);

        Assert.Throws<InvalidDataException>(() => frame.Validate(revision, events));
    }

    [Fact]
    public void Proof_frame_rejects_a_variable_snapshot_that_disagrees_with_evidence()
    {
        var revision = WorkflowRevision.Create(LoopVariableTopologyTests.Candidate(), "checkpoint-proof");
        var runId = Guid.NewGuid();
        var store = new WorkflowVariableStore(revision.Definition);
        var written = store.Write("message", WorkflowDataTypes.String, "changed");
        var events = Evidence(runId, revision.RevisionId).Append(
            new ExecutionEvent(runId, revision.RevisionId, 3, DateTimeOffset.UtcNow, ExecutionEventKinds.VariableWritten, "set",
                Data: JsonSerializer.Serialize(new { variableId = "message", dataType = WorkflowDataTypes.String,
                    previousValue = "default", value = "changed", version = written.Version, loopIteration = 1 }))).ToArray();
        var frame = WorkflowCheckpointFrame.Create(revision, runId, "{}",
            new("loop", "body", 1, 3, "body", "execute", "loop", "body", 2), store.Snapshot(), events);
        frame.Validate(revision, events);

        var tamperedValues = frame.Variables.Values.SetItem(0, frame.Variables.Values[0] with { Value = "tampered" });
        var tamperedSnapshot = frame.Variables with { Values = tamperedValues };
        var tampered = frame with { Variables = tamperedSnapshot, VariableStateHash = tamperedSnapshot.ComputeHash(), FrameHash = null };
        tampered = tampered with { FrameHash = tampered.ComputeFrameHash() };

        Assert.Throws<InvalidDataException>(() => tampered.Validate(revision, events));
    }

    [Fact]
    public void Proof_frame_rejects_a_hash_consistent_but_graph_inconsistent_cursor()
    {
        var revision = WorkflowRevision.Create(LoopVariableTopologyTests.Candidate(), "checkpoint-proof");
        var runId = Guid.NewGuid();
        var events = Evidence(runId, revision.RevisionId);
        var variables = new WorkflowVariableStore(revision.Definition).Snapshot();
        var frame = WorkflowCheckpointFrame.Create(revision, runId, "{}",
            new("loop", "body", 1, 3, "set", "execute", "body", "then", 2), variables, events);
        frame.Validate(revision, events);

        var tampered = frame with { Loop = frame.Loop with { NextNodeId = "get" }, FrameHash = null };
        tampered = tampered with { FrameHash = tampered.ComputeFrameHash() };

        Assert.Throws<InvalidDataException>(() => tampered.Validate(revision, events));
    }

    [Fact]
    public void Proof_frame_rejects_recorded_loop_limit_that_disagrees_with_the_cursor()
    {
        var revision = WorkflowRevision.Create(LoopVariableTopologyTests.Candidate(), "checkpoint-proof");
        var runId = Guid.NewGuid();
        var events = Evidence(runId, revision.RevisionId).Select(evt => evt.Kind == ExecutionEventKinds.LoopIterationStarted
            ? evt with { Data = JsonSerializer.Serialize(new { iteration = 1, maximumIterations = 4 }) }
            : evt).ToArray();
        var variables = new WorkflowVariableStore(revision.Definition).Snapshot();
        var frame = WorkflowCheckpointFrame.Create(revision, runId, "{}",
            new("loop", "body", 1, 3, "body", "execute", "loop", "body", 2), variables, events);

        Assert.Throws<InvalidDataException>(() => frame.Validate(revision, events));
    }

    private static List<ExecutionEvent> Evidence(Guid runId, Guid revisionId) =>
    [
        new(runId, revisionId, 1, DateTimeOffset.UtcNow, ExecutionEventKinds.Started),
        new(runId, revisionId, 2, DateTimeOffset.UtcNow, ExecutionEventKinds.LoopIterationStarted, "loop",
            Data: JsonSerializer.Serialize(new { iteration = 1, maximumIterations = 3 }))
    ];
}
