using System.Text.Json;
using VisualAgentWorkflow.Core;

namespace VisualAgentWorkflow.Hosting;

/// <summary>Optional consistent assessment capability for a local store.</summary>
public interface IWorkflowRunReconciliationStore
{
    RunReconciliation? GetReconciliation(Guid runId);
}

public static class WorkflowRunReconciliation
{
    public static RunReconciliation Assess(RunSnapshot snapshot, WorkflowRunCheckpoint? checkpoint, bool checkpointInvalid = false)
    {
        var work = WorkflowWorkEvidence.Read(snapshot);
        var valid = false;
        if (checkpoint is not null)
        {
            try { checkpoint.Validate(snapshot); valid = true; }
            catch (Exception error) when (error is InvalidDataException or InvalidOperationException or ArgumentException or JsonException)
            { checkpointInvalid = true; }
        }
        var summary = valid ? new RunCheckpointSummary(checkpoint!.Native.CheckpointId, checkpoint.Sequence, checkpoint.RuntimeVersion) : null;
        if (work.TotalUnresolvedWork > 0 || work.HasIncompleteCorrelation)
            return Report(RunRecoveryDisposition.UnresolvedWork, false,
                "Recorded work is unsettled or ambiguously correlated. A terminal status does not settle external outcomes; live work may still be executing.");
        if (snapshot.IsTerminal)
            return Report(RunRecoveryDisposition.Terminal, false, "The run is terminal. This does not mean external effects were rolled back.");
        if (checkpointInvalid)
            return Report(RunRecoveryDisposition.CheckpointIncompatible, false, "The checkpoint is invalid or incompatible; its native state was not executed.");
        if (checkpoint is null)
            return Report(RunRecoveryDisposition.CheckpointUnavailable, false, "No usable checkpoint is provided by this source. Recorded events alone cannot restore execution.");
        if (checkpoint.Sequence != snapshot.Events.Length)
            return Report(RunRecoveryDisposition.EvidenceAdvanced, false, "Evidence advanced after the checkpoint; do not replay the older frame.");
        var assessment = WorkflowCheckpointRecovery.Assess(snapshot, checkpoint);
        return Report(assessment.CanResume ? RunRecoveryDisposition.SavedSuspension : RunRecoveryDisposition.NoSavedSuspension,
            assessment.CanResume, assessment.CanResume
                ? "Saved deterministic suspension is eligible for explicit host recovery, subject to fresh validation and exclusive ownership."
                : assessment.Reason);

        RunReconciliation Report(RunRecoveryDisposition disposition, bool canResume, string reason) =>
            new(snapshot.Header.RunId, snapshot.Header.RevisionId, snapshot.Header.ContentHash, snapshot.Events.Length, snapshot.Status,
                disposition, canResume, reason, summary, work.UnresolvedWork, work.TotalUnresolvedWork, work.HasIncompleteCorrelation);
    }
}
