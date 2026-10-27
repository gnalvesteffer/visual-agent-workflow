using System.Text.Json;
using Microsoft.Data.Sqlite;
using VisualAgentWorkflow.Blazor;
using VisualAgentWorkflow.Core;
using Xunit;

namespace VisualAgentWorkflow.Hosting.Tests;

public sealed class ReconciliationDecisionTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "vaw-reconciliation-decisions-" + Guid.NewGuid());
    private string Database => Path.Combine(directory, "runs.sqlite3");

    [Fact]
    public void TerminalAssertionIsSeparateFromEvidenceAndExactRetriesAreIdempotent()
    {
        var store = new SqliteWorkflowRunStore(Database);
        var run = TerminalRunWithModelRequest(store);
        var before = store.Get(run.Header.RunId)!;
        var reportBefore = WorkflowRunReconciliation.Assess(before, null);
        var work = Assert.Single(reportBefore.UnresolvedWork);
        var command = Command(before, work, RunReconciliationAssertion.BelievedNotApplied);
        var service = new WorkflowRunReconciliationDecisionService(store, new Actor("operator:alice"));

        var recorded = service.Record(command);
        var duplicate = service.Record(command);
        var after = store.Get(run.Header.RunId)!;
        var reportAfter = WorkflowRunReconciliation.Assess(after, null);

        Assert.Equal(recorded, duplicate);
        Assert.Equal("operator:alice", recorded.ActorId);
        Assert.Equal("failed", after.Status);
        Assert.Equal(before.Events.ToArray(), after.Events.ToArray());
        Assert.Equal(before.Header.ContentHash, after.Header.ContentHash);
        Assert.Equal(RunRecoveryDisposition.UnresolvedWork, reportAfter.Disposition);
        Assert.False(reportAfter.CanResume);
        Assert.Equal(work, Assert.Single(reportAfter.UnresolvedWork));
        Assert.Single(service.Read(run.Header.RunId));
        Assert.DoesNotContain("private model input", JsonSerializer.Serialize(service.Read(run.Header.RunId)));
    }

    [Fact]
    public void CurrentHeadAndTerminalStatusAreRequiredBeforeRecording()
    {
        var store = new SqliteWorkflowRunStore(Database);
        var run = store.Create(WorkflowRevision.Create(WorkflowTemplates.Create("reverse"), "test"), "private input");
        var callId = Guid.NewGuid();
        var invocationId = Guid.NewGuid();
        store.Append(run.Header.RunId, 0, [ModelRequest(run, 1, invocationId, callId)]);
        var active = store.Get(run.Header.RunId)!;
        var staleAssessment = WorkflowRunReconciliation.Assess(active, null);
        var staleWork = Assert.Single(staleAssessment.UnresolvedWork);
        Assert.Throws<WorkflowRunReconciliationDecisionConflictException>(() =>
            new WorkflowRunReconciliationDecisionService(store, new Actor("operator:alice"))
                .Record(Command(active, staleWork, RunReconciliationAssertion.RemainsUnknown)));
        store.Append(run.Header.RunId, 1, [Event(run, 2, ExecutionEventKinds.Failed)]);

        var stale = Command(store.Get(run.Header.RunId)!, staleWork, RunReconciliationAssertion.RemainsUnknown) with
        { AssessmentSequence = staleAssessment.LastSequence };
        var currentReport = WorkflowRunReconciliation.Assess(store.Get(run.Header.RunId)!, null);
        var currentWork = Assert.Single(currentReport.UnresolvedWork);
        var current = Command(store.Get(run.Header.RunId)!, currentWork, RunReconciliationAssertion.RemainsUnknown);

        Assert.Throws<WorkflowRunReconciliationDecisionConflictException>(() =>
            new WorkflowRunReconciliationDecisionService(store, new Actor("operator:alice")).Record(stale));
        Assert.Throws<WorkflowRunReconciliationDecisionConflictException>(() =>
            new WorkflowRunReconciliationDecisionService(store, new Actor("operator:alice")).Record(current with
            { ContentHash = "wrong-content-hash" }));
        Assert.Throws<WorkflowRunReconciliationDecisionConflictException>(() =>
            store.RecordReconciliationDecision(stale with { DecisionId = Guid.NewGuid() }, "operator:alice", DateTimeOffset.UtcNow));
        Assert.Empty(store.GetReconciliationDecisions(run.Header.RunId));
    }

    [Fact]
    public void AmbiguousTargetAndContradictoryAssertionAreRejected()
    {
        var store = new SqliteWorkflowRunStore(Database);
        var run = store.Create(WorkflowRevision.Create(WorkflowTemplates.Create("reverse"), "test"), "private input");
        var invocationId = Guid.NewGuid();
        store.Append(run.Header.RunId, 0,
            [ModelRequest(run, 1, invocationId, Guid.NewGuid()), ModelRequest(run, 2, invocationId, Guid.NewGuid()),
             Event(run, 3, ExecutionEventKinds.Failed)]);
        var snapshot = store.Get(run.Header.RunId)!;
        var item = WorkflowRunReconciliation.Assess(snapshot, null).UnresolvedWork[0];
        var decisionService = new WorkflowRunReconciliationDecisionService(store, new Actor("operator:alice"));
        var first = Command(snapshot, item, RunReconciliationAssertion.BelievedCompleted);
        decisionService.Record(first);

        Assert.Throws<WorkflowRunReconciliationDecisionConflictException>(() => decisionService.Record(first with
        { DecisionId = Guid.NewGuid(), Assertion = RunReconciliationAssertion.BelievedNotApplied }));
        Assert.Throws<WorkflowRunReconciliationDecisionConflictException>(() => store.RecordReconciliationDecision(
            Command(snapshot, item with { RequestedSequence = 2 }, RunReconciliationAssertion.RemainsUnknown),
            "operator:alice", DateTimeOffset.UtcNow));
        Assert.Single(store.GetReconciliationDecisions(run.Header.RunId));
    }

    [Fact]
    public async Task ConcurrentAssertionsForOneTargetCommitAtMostOneRecord()
    {
        var store = new SqliteWorkflowRunStore(Database);
        var run = TerminalRunWithModelRequest(store);
        var work = Assert.Single(WorkflowRunReconciliation.Assess(run, null).UnresolvedWork);
        var first = Command(run, work, RunReconciliationAssertion.BelievedCompleted);
        var second = first with { DecisionId = Guid.NewGuid(), Assertion = RunReconciliationAssertion.BelievedNotApplied };
        using var gate = new Barrier(2);

        Task<bool> TryRecord(RunReconciliationDecisionCommand command) => Task.Run(() =>
        {
            Assert.True(gate.SignalAndWait(TimeSpan.FromSeconds(5)));
            try
            {
                store.RecordReconciliationDecision(command, "operator:alice", DateTimeOffset.UtcNow);
                return true;
            }
            catch (WorkflowRunReconciliationDecisionConflictException) { return false; }
        });

        var outcomes = await Task.WhenAll(TryRecord(first), TryRecord(second));

        Assert.Single(outcomes, recorded => recorded);
        Assert.Single(store.GetReconciliationDecisions(run.Header.RunId));
    }

    [Fact]
    public void FullEvidenceLookupCanTargetWorkBeyondTheDisplayLimit()
    {
        var store = new SqliteWorkflowRunStore(Database);
        var run = store.Create(WorkflowRevision.Create(WorkflowTemplates.Create("reverse"), "test"), "private input");
        const int count = WorkflowWorkEvidence.MaximumReturnedWork + 3;
        var requests = Enumerable.Range(1, count).Select(sequence => ModelRequest(run, sequence, Guid.NewGuid(), Guid.NewGuid())).ToArray();
        for (var offset = 0; offset < requests.Length; offset += 256)
            store.Append(run.Header.RunId, offset, requests.Skip(offset).Take(256).ToArray());
        store.Append(run.Header.RunId, count, [Event(run, count + 1, ExecutionEventKinds.Failed)]);

        var snapshot = store.Get(run.Header.RunId)!;
        var visible = WorkflowRunReconciliation.Assess(snapshot, null);
        var all = WorkflowWorkEvidence.ReadAll(snapshot);
        Assert.Equal(count, visible.TotalUnresolvedWork);
        Assert.Equal(WorkflowWorkEvidence.MaximumReturnedWork, visible.UnresolvedWork.Length);
        Assert.Equal(count, all.UnresolvedWork.Length);

        var last = all.UnresolvedWork[^1];
        var recorded = store.RecordReconciliationDecision(Command(snapshot, last, RunReconciliationAssertion.RemainsUnknown),
            "operator:alice", DateTimeOffset.UtcNow);
        Assert.Equal(requests[^1].Sequence, recorded.Work.RequestedSequence);
    }

    [Fact]
    public void HostActorIsRequiredAndCannotBeProvidedByTheCommand()
    {
        var store = new SqliteWorkflowRunStore(Database);
        var run = TerminalRunWithModelRequest(store);
        var work = Assert.Single(WorkflowRunReconciliation.Assess(run, null).UnresolvedWork);
        var service = new WorkflowRunReconciliationDecisionService(store, new Actor(" "));

        Assert.Throws<UnauthorizedAccessException>(() => service.Record(Command(run, work, RunReconciliationAssertion.RemainsUnknown)));
        Assert.Empty(store.GetReconciliationDecisions(run.Header.RunId));
    }

    [Fact]
    public void IncompleteCorrelationCannotBeAssertedAsUnresolvedWork()
    {
        var store = new SqliteWorkflowRunStore(Database);
        var run = store.Create(WorkflowRevision.Create(WorkflowTemplates.Create("reverse"), "test"), "private input");
        var invocationId = Guid.NewGuid();
        store.Append(run.Header.RunId, 0,
            [new ExecutionEvent(run.Header.RunId, run.Header.RevisionId, 1, DateTimeOffset.UtcNow,
                ExecutionEventKinds.ModelRequested, "step-1", invocationId,
                Data: "{\"modelCallId\":\"not-a-guid\",\"messages\":[]}"),
             Event(run, 2, ExecutionEventKinds.Failed)]);
        var snapshot = store.Get(run.Header.RunId)!;
        var evidence = WorkflowWorkEvidence.ReadAll(snapshot);
        var work = Assert.Single(evidence.UnresolvedWork);
        Assert.True(evidence.HasIncompleteCorrelation);
        Assert.Null(work.CallId);

        Assert.Throws<WorkflowRunReconciliationDecisionConflictException>(() =>
            store.RecordReconciliationDecision(Command(snapshot, work, RunReconciliationAssertion.RemainsUnknown),
                "operator:alice", DateTimeOffset.UtcNow));
        Assert.Empty(store.GetReconciliationDecisions(run.Header.RunId));
    }

    [Fact]
    public void VersionTwoSqliteRunDatabaseMigratesToSeparateAssertionTable()
    {
        Directory.CreateDirectory(directory);
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Database, Pooling = false }.ToString()))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                PRAGMA application_id=1447122770;
                CREATE TABLE runs (run_id TEXT PRIMARY KEY, header_json TEXT NOT NULL, sequence INTEGER NOT NULL CHECK(sequence >= 0), status TEXT NOT NULL);
                CREATE TABLE run_events (run_id TEXT NOT NULL REFERENCES runs(run_id), sequence INTEGER NOT NULL CHECK(sequence > 0), event_json TEXT NOT NULL, PRIMARY KEY(run_id, sequence));
                CREATE TABLE run_checkpoints (run_id TEXT PRIMARY KEY REFERENCES runs(run_id), checkpoint_json TEXT NOT NULL);
                PRAGMA user_version=2;
                """;
            command.ExecuteNonQuery();
        }

        _ = new SqliteWorkflowRunStore(Database);

        using var migrated = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Database, Pooling = false }.ToString());
        migrated.Open();
        using var verify = migrated.CreateCommand();
        verify.CommandText = "PRAGMA user_version; SELECT count(*) FROM sqlite_master WHERE type='table' AND name='reconciliation_decisions';";
        using var reader = verify.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal(3, reader.GetInt32(0));
        Assert.True(reader.NextResult());
        Assert.True(reader.Read());
        Assert.Equal(1, reader.GetInt32(0));
    }

    public void Dispose() { if (Directory.Exists(directory)) Directory.Delete(directory, true); }

    private static RunSnapshot TerminalRunWithModelRequest(SqliteWorkflowRunStore store)
    {
        var run = store.Create(WorkflowRevision.Create(WorkflowTemplates.Create("reverse"), "test"), "private model input");
        store.Append(run.Header.RunId, 0,
            [ModelRequest(run, 1, Guid.NewGuid(), Guid.NewGuid()), Event(run, 2, ExecutionEventKinds.Failed)]);
        return store.Get(run.Header.RunId)!;
    }

    private static RunReconciliationDecisionCommand Command(RunSnapshot run, UnresolvedRunWork work,
        RunReconciliationAssertion assertion) => new(Guid.NewGuid(), run.Header.RunId, run.Header.RevisionId,
        run.Header.ContentHash, run.Events.Length, new(work.Kind, work.RequestedSequence, work.NodeId!, work.InvocationId!.Value, work.CallId), assertion);

    private static ExecutionEvent ModelRequest(RunSnapshot run, long sequence, Guid invocation, Guid call) =>
        new(run.Header.RunId, run.Header.RevisionId, sequence, DateTimeOffset.UtcNow, ExecutionEventKinds.ModelRequested,
            "step-1", invocation, Data: JsonSerializer.Serialize(new { modelCallId = call, messages = new[] { "private model input" } }));

    private static ExecutionEvent Event(RunSnapshot run, long sequence, string kind) =>
        new(run.Header.RunId, run.Header.RevisionId, sequence, DateTimeOffset.UtcNow, kind);

    private sealed class Actor(string actorId) : IWorkflowRunReconciliationDecisionActor
    {
        public string GetCurrentActorId() => actorId;
    }
}
