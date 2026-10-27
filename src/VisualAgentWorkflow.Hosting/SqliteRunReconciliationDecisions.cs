using Microsoft.Data.Sqlite;
using VisualAgentWorkflow.Core;

namespace VisualAgentWorkflow.Hosting;

public sealed partial class SqliteWorkflowRunStore
{
    public IReadOnlyList<RunReconciliationDecision> GetReconciliationDecisions(Guid runId)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction(deferred: true);
        if (Read(connection, transaction, runId) is null) throw new KeyNotFoundException("Unknown run.");
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT decision_id, revision_id, content_hash, assessment_sequence, work_kind,
                requested_sequence, node_id, invocation_id, call_id, assertion, actor_id, recorded_at
            FROM reconciliation_decisions WHERE run_id=$run ORDER BY recorded_at, decision_id;
            """;
        command.Parameters.AddWithValue("$run", runId.ToString());
        var decisions = new List<RunReconciliationDecision>();
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                var work = new RunReconciliationWorkIdentity(Enum.Parse<UnresolvedWorkKind>(reader.GetString(4)),
                    reader.GetInt64(5), reader.GetString(6), Guid.Parse(reader.GetString(7)),
                    reader.IsDBNull(8) ? null : Guid.Parse(reader.GetString(8)));
                decisions.Add(new(Guid.Parse(reader.GetString(0)), runId, Guid.Parse(reader.GetString(1)),
                    reader.GetString(2), reader.GetInt64(3), work, Enum.Parse<RunReconciliationAssertion>(reader.GetString(9)),
                    reader.GetString(10), DateTimeOffset.Parse(reader.GetString(11), System.Globalization.CultureInfo.InvariantCulture)));
            }
        }
        transaction.Commit();
        return decisions;
    }

    public RunReconciliationDecision RecordReconciliationDecision(RunReconciliationDecisionCommand command,
        string actorId, DateTimeOffset recordedAt)
    {
        ValidateDecisionCommand(command, actorId);
        if (recordedAt == default) throw new ArgumentOutOfRangeException(nameof(recordedAt));
        using var connection = Open();
        using var transaction = connection.BeginTransaction(deferred: false);
        var snapshot = Read(connection, transaction, command.RunId) ?? throw new KeyNotFoundException("Unknown run.");

        var duplicate = ReadDecision(connection, transaction, command.RunId, command.DecisionId);
        if (duplicate is not null)
        {
            if (SameCommand(duplicate, command) && duplicate.ActorId == actorId) return CommitAndReturn(transaction, duplicate);
            throw new WorkflowRunReconciliationDecisionConflictException("The decision ID already belongs to a different assertion.");
        }

        if (snapshot.Events.Length != command.AssessmentSequence || snapshot.Header.RevisionId != command.RevisionId ||
            !string.Equals(snapshot.Header.ContentHash, command.ContentHash, StringComparison.Ordinal))
            throw new WorkflowRunReconciliationDecisionConflictException("The run evidence changed after the assessment was read.");
        if (!snapshot.IsTerminal)
            throw new WorkflowRunReconciliationDecisionConflictException("Assertions can only be recorded after a run is terminal.");

        var evidence = WorkflowWorkEvidence.ReadAll(snapshot);
        if (evidence.HasIncompleteCorrelation)
            throw new WorkflowRunReconciliationDecisionConflictException("The run contains incomplete or ambiguous work correlation.");
        var matches = evidence.UnresolvedWork.Where(work =>
            work.Kind == command.Work.Kind && work.RequestedSequence == command.Work.RequestedSequence &&
            work.NodeId == command.Work.NodeId && work.InvocationId == command.Work.InvocationId && work.CallId == command.Work.CallId).ToArray();
        if (matches.Length != 1 || command.Work.Kind is not (UnresolvedWorkKind.Model or UnresolvedWorkKind.Tool))
            throw new WorkflowRunReconciliationDecisionConflictException("The target is no longer unresolved or cannot be correlated unambiguously.");
        if (HasDecisionForWork(connection, transaction, command.RunId, command.Work))
            throw new WorkflowRunReconciliationDecisionConflictException("An assertion already exists for this unresolved work item.");

        var result = new RunReconciliationDecision(command.DecisionId, command.RunId, command.RevisionId,
            command.ContentHash, command.AssessmentSequence, command.Work, command.Assertion, actorId, recordedAt.ToUniversalTime());
        var changed = Execute(connection, transaction, """
            INSERT INTO reconciliation_decisions(run_id,decision_id,revision_id,content_hash,assessment_sequence,
                work_kind,requested_sequence,node_id,invocation_id,call_id,assertion,actor_id,recorded_at)
            VALUES($run,$decision,$revision,$hash,$sequence,$kind,$requestSequence,$node,$invocation,$call,$assertion,$actor,$recorded);
            """, ("$run", result.RunId.ToString()), ("$decision", result.DecisionId.ToString()),
            ("$revision", result.RevisionId.ToString()), ("$hash", result.ContentHash),
            ("$sequence", result.AssessmentSequence), ("$kind", result.Work.Kind.ToString()),
            ("$requestSequence", result.Work.RequestedSequence), ("$node", result.Work.NodeId),
            ("$invocation", result.Work.InvocationId.ToString()), ("$call", result.Work.CallId?.ToString() ?? (object)DBNull.Value),
            ("$assertion", result.Assertion.ToString()), ("$actor", result.ActorId), ("$recorded", result.RecordedAt.ToString("O")));
        if (changed != 1) throw new InvalidOperationException("The reconciliation assertion was not stored.");
        transaction.Commit();
        return result;
    }

    private static RunReconciliationDecision? ReadDecision(SqliteConnection connection, SqliteTransaction transaction,
        Guid runId, Guid decisionId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT revision_id, content_hash, assessment_sequence, work_kind, requested_sequence,
                node_id, invocation_id, call_id, assertion, actor_id, recorded_at
            FROM reconciliation_decisions WHERE run_id=$run AND decision_id=$decision;
            """;
        command.Parameters.AddWithValue("$run", runId.ToString());
        command.Parameters.AddWithValue("$decision", decisionId.ToString());
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        var work = new RunReconciliationWorkIdentity(Enum.Parse<UnresolvedWorkKind>(reader.GetString(3)),
            reader.GetInt64(4), reader.GetString(5), Guid.Parse(reader.GetString(6)),
            reader.IsDBNull(7) ? null : Guid.Parse(reader.GetString(7)));
        return new(decisionId, runId, Guid.Parse(reader.GetString(0)), reader.GetString(1), reader.GetInt64(2),
            work, Enum.Parse<RunReconciliationAssertion>(reader.GetString(8)), reader.GetString(9),
            DateTimeOffset.Parse(reader.GetString(10), System.Globalization.CultureInfo.InvariantCulture));
    }

    private static bool HasDecisionForWork(SqliteConnection connection, SqliteTransaction transaction, Guid runId,
        RunReconciliationWorkIdentity work)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT 1 FROM reconciliation_decisions
            WHERE run_id=$run AND work_kind=$kind AND requested_sequence=$requestSequence AND node_id=$node
                AND invocation_id=$invocation AND ifnull(call_id, '')=$call LIMIT 1;
            """;
        command.Parameters.AddWithValue("$run", runId.ToString());
        command.Parameters.AddWithValue("$kind", work.Kind.ToString());
        command.Parameters.AddWithValue("$requestSequence", work.RequestedSequence);
        command.Parameters.AddWithValue("$node", work.NodeId);
        command.Parameters.AddWithValue("$invocation", work.InvocationId.ToString());
        command.Parameters.AddWithValue("$call", work.CallId?.ToString() ?? string.Empty);
        return command.ExecuteScalar() is not null;
    }

    private static void ValidateDecisionCommand(RunReconciliationDecisionCommand command, string actorId)
    {
        if (command.DecisionId == Guid.Empty || command.RunId == Guid.Empty || command.RevisionId == Guid.Empty ||
            string.IsNullOrWhiteSpace(command.ContentHash) || command.ContentHash.Length > 128 || command.AssessmentSequence < 0 ||
            command.Work is null || command.Work.RequestedSequence < 1 || string.IsNullOrWhiteSpace(command.Work.NodeId) ||
            command.Work.InvocationId == Guid.Empty || command.Work.CallId == Guid.Empty ||
            !Enum.IsDefined(command.Assertion) || !Enum.IsDefined(command.Work.Kind) ||
            string.IsNullOrWhiteSpace(actorId) || actorId.Length > 256)
            throw new ArgumentException("The reconciliation assertion is invalid.", nameof(command));
    }

    private static bool SameCommand(RunReconciliationDecision saved, RunReconciliationDecisionCommand command) =>
        saved.RevisionId == command.RevisionId && saved.ContentHash == command.ContentHash &&
        saved.AssessmentSequence == command.AssessmentSequence && saved.Work == command.Work && saved.Assertion == command.Assertion;

    private static RunReconciliationDecision CommitAndReturn(SqliteTransaction transaction, RunReconciliationDecision result)
    {
        transaction.Commit();
        return result;
    }
}
