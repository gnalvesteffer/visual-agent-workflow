using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using VisualAgentWorkflow.Blazor;
using VisualAgentWorkflow.Core;
using Xunit;

namespace VisualAgentWorkflow.Hosting.Tests;

public sealed class RunStoreTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "vaw-store-" + Guid.NewGuid());
    private string Database => Path.Combine(directory, "runs.sqlite3");
    private IWorkflowRunStore Store(bool sqlite) => sqlite ? new SqliteWorkflowRunStore(Database) : new FileRunJournal(directory);
    private static WorkflowRevision Revision() => WorkflowRevision.Create(WorkflowTemplates.Create("reverse"), "test");
    private static ExecutionEvent Event(RunSnapshot run, long sequence, string kind = ExecutionEventKinds.Started) =>
        new(run.Header.RunId, run.Header.RevisionId, sequence, DateTimeOffset.UtcNow, kind);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CompleteBatchesAndPinnedEvidenceSurviveReopening(bool sqlite)
    {
        var store = Store(sqlite);
        var revision = Revision();
        var run = store.Create(revision, "hello");
        var completed = store.Append(run.Header.RunId, 0, [Event(run, 1), Event(run, 2, ExecutionEventKinds.Completed) with { Data = "olleh" }]);
        var reopened = Store(sqlite).Get(run.Header.RunId)!;
        Assert.Equal(completed.Events.ToArray(), reopened.Events.ToArray());
        Assert.Equal("completed", reopened.Status);
        Assert.Equal(revision.ContentHash, reopened.Header.ContentHash);
        Assert.Equal(revision.RevisionId, reopened.Header.RevisionId);
        Assert.Single(store.List());
        Assert.Throws<InvalidOperationException>(() => store.Append(run.Header.RunId, 2, [Event(run, 3)]));
        Assert.Equal(2, store.Get(run.Header.RunId)!.Events.Length);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SuccessfulTerminationIsTerminalAndHasNoWorkflowResult(bool sqlite)
    {
        var store = Store(sqlite);
        var run = store.Create(Revision(), "input");

        var terminated = store.Append(run.Header.RunId, 0, [
            Event(run, 1),
            Event(run, 2, ExecutionEventKinds.Terminated)
        ]);

        Assert.Equal("terminated", terminated.Status);
        Assert.True(terminated.IsTerminal);
        Assert.Equal(ExecutionEventKinds.Terminated, terminated.Events[^1].Kind);
        Assert.Null(terminated.Events[^1].Data);
        Assert.Equal("terminated", Store(sqlite).Get(run.Header.RunId)!.Status);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InvalidPartialBatchesAndStaleWritersPublishNothing(bool sqlite)
    {
        var store = Store(sqlite);
        var run = store.Create(Revision(), "hello");
        Assert.Throws<InvalidOperationException>(() => store.Append(run.Header.RunId, 0, [Event(run, 1), Event(run, 3)]));
        Assert.Throws<InvalidOperationException>(() => store.Append(run.Header.RunId, 0, [Event(run, 1), Event(run, 2) with { RevisionId = Guid.NewGuid() }]));
        Assert.Throws<InvalidOperationException>(() => store.Append(run.Header.RunId, 0, [Event(run, 1, ExecutionEventKinds.Completed), Event(run, 2)]));
        Assert.Empty(store.Get(run.Header.RunId)!.Events);
        store.Append(run.Header.RunId, 0, [Event(run, 1)]);
        Assert.Throws<WorkflowRunStoreConflictException>(() => store.Append(run.Header.RunId, 0, [Event(run, 1)]));
        Assert.Single(store.Get(run.Header.RunId)!.Events);
        Assert.Throws<KeyNotFoundException>(() => store.Append(Guid.NewGuid(), 0, [Event(run, 1)]));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CompetingExpectedSequencesHaveOneWinner(bool sqlite)
    {
        var store = Store(sqlite);
        var other = sqlite ? Store(true) : store; // File store has one owner instance; SQLite also serializes independent connections.
        var run = store.Create(Revision(), "hello");
        using var ready = new CountdownEvent(2);
        using var release = new ManualResetEventSlim();
        Task<bool> Attempt(IWorkflowRunStore target) => Task.Run(() =>
        {
            ready.Signal();
            Assert.True(release.Wait(TimeSpan.FromSeconds(10)));
            try { target.Append(run.Header.RunId, 0, [Event(run, 1)]); return true; }
            catch (WorkflowRunStoreConflictException) { return false; }
        });
        var first = Attempt(store);
        var second = Attempt(other);
        Assert.True(ready.Wait(TimeSpan.FromSeconds(10)));
        release.Set();
        var results = await Task.WhenAll(first, second);
        Assert.Single(results, won => won);
        Assert.Single(store.Get(run.Header.RunId)!.Events);
    }

    [Fact]
    public void LegacyFilesRemainReadableAndMigrateOnlyAfterSuccessfulAppend()
    {
        Directory.CreateDirectory(directory);
        var revision = Revision();
        var header = new RunHeader(Guid.NewGuid(), revision.RevisionId, null, revision.Author, revision.CreatedAt,
            revision.ContentHash, revision.Definition, "legacy");
        var run = new RunSnapshot(header, []);
        File.WriteAllText(Path.Combine(directory, header.RunId + ".run.json"), JsonSerializer.Serialize(header, JsonSerializerOptions.Web));
        File.WriteAllText(Path.Combine(directory, header.RunId + ".events.jsonl"), JsonSerializer.Serialize(Event(run, 1), JsonSerializerOptions.Web) + "\n");
        var store = new FileRunJournal(directory);
        Assert.Single(store.Get(header.RunId)!.Events);
        Assert.Throws<WorkflowRunStoreConflictException>(() => store.Append(header.RunId, 0, [Event(run, 1)]));
        Assert.False(File.Exists(Path.Combine(directory, header.RunId + ".snapshot.json")));
        store.Append(header.RunId, 1, [Event(run, 2, ExecutionEventKinds.Completed)]);
        Assert.Single(store.List());
        Assert.Equal("completed", new FileRunJournal(directory).Get(header.RunId)!.Status);
        Assert.Single(File.ReadAllLines(Path.Combine(directory, header.RunId + ".events.jsonl")));
        // A process lost before rename leaves only a temporary file, never a visible partial commit.
        File.WriteAllText(Path.Combine(directory, Guid.NewGuid() + ".tmp"), "partial");
        Assert.Single(store.List());
    }

    [Fact]
    public void FileJournalReadsWindowsSnapshotsWrittenBeforePortableHashNormalization()
    {
        var store = new FileRunJournal(directory);
        var revision = Revision();
        var run = store.Create(revision, "legacy Windows evidence");
        var legacyHash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(WorkflowJson.Serialize(revision.Definition)
                .Replace("\n", "\r\n", StringComparison.Ordinal))));
        var oldSnapshot = run with { Header = run.Header with { ContentHash = legacyHash } };
        var path = Path.Combine(directory, run.Header.RunId + ".snapshot.json");
        File.WriteAllText(path, JsonSerializer.Serialize(oldSnapshot, JsonSerializerOptions.Web));

        var reopened = new FileRunJournal(directory);

        Assert.Equal(legacyHash, reopened.Get(run.Header.RunId)!.Header.ContentHash);
        Assert.Single(reopened.List());
        var restoredRevision = WorkflowRevision.Rehydrate(run.Header.RevisionId, run.Header.Definition,
            run.Header.Author, run.Header.ParentRevisionId, run.Header.CreatedAt, legacyHash);
        Assert.Equal(legacyHash, restoredRevision.ContentHash);
    }

    [Fact]
    public async Task RuntimeStartupPreservesRunsWithDefinitionsRejectedByNewerValidationRules()
    {
        var store = new FileRunJournal(directory);
        var revision = Revision();
        var run = store.Create(revision, "historical run");
        store.Append(run.Header.RunId, 0, [Event(run, 1)]);

        var definition = revision.Definition;
        var agent = definition.Nodes["step-1"] with
        {
            Type = NodeTypes.Agent,
            Agent = new AgentConfiguration("test.provider", "1.0", "Answer the request.", [])
            { ReasoningEffort = AgentReasoningEffort.Off }
        };
        var validCurrent = definition with
        {
            SchemaVersion = "1.10",
            Nodes = definition.Nodes.SetItem("step-1", agent)
        };
        var canonical = WorkflowJson.Serialize(validCurrent);
        var historicalJson = canonical.Replace("\"schemaVersion\": \"1.10\"", "\"schemaVersion\": \"1.9\"", StringComparison.Ordinal);
        var historicalDefinition = validCurrent with { SchemaVersion = "1.9" };
        var historicalHash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(historicalJson)));
        var persisted = store.Get(run.Header.RunId)! with
        {
            Header = store.Get(run.Header.RunId)!.Header with
            {
                Definition = historicalDefinition,
                ContentHash = historicalHash
            }
        };
        File.WriteAllText(Path.Combine(directory, run.Header.RunId + ".snapshot.json"),
            JsonSerializer.Serialize(persisted, JsonSerializerOptions.Web));

        var reopened = new FileRunJournal(directory);
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddSingleton<IWorkflowRunStore>(reopened);
        builder.Services.AddLocalWorkflowRuntime(Path.Combine(directory, "unused-fallback"));
        using var host = builder.Build();
        await host.StartAsync();
        await host.StopAsync();

        Assert.Equal("interrupted", reopened.Get(run.Header.RunId)!.Status);
    }

    [Fact]
    public void SqliteFailureAfterEventInsertRollsBackEvidenceAndState()
    {
        var store = new SqliteWorkflowRunStore(Database);
        var run = store.Create(Revision(), "hello");
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Database, Pooling = false }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "CREATE TRIGGER fail_state BEFORE UPDATE ON runs BEGIN SELECT RAISE(ABORT,'injected failure'); END;";
        command.ExecuteNonQuery();
        Assert.Throws<SqliteException>(() => store.Append(run.Header.RunId, 0, [Event(run, 1), Event(run, 2, ExecutionEventKinds.Completed)]));
        var reopened = new SqliteWorkflowRunStore(Database).Get(run.Header.RunId)!;
        Assert.Empty(reopened.Events);
        Assert.Equal("queued", reopened.Status);
        command.CommandText = "DROP TRIGGER fail_state;";
        command.ExecuteNonQuery();
        Assert.Equal("completed", store.Append(run.Header.RunId, 0, [Event(run, 1, ExecutionEventKinds.Completed)]).Status);
    }

    [Fact]
    public void SqliteRejectsAnotherApplicationAndCorruptState()
    {
        var store = new SqliteWorkflowRunStore(Database);
        var run = store.Create(Revision(), "hello");
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Database, Pooling = false }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE runs SET status='completed';";
        command.ExecuteNonQuery();
        Assert.Throws<InvalidDataException>(() => store.Get(run.Header.RunId));
        command.CommandText = "PRAGMA user_version=99;";
        command.ExecuteNonQuery();
        Assert.Throws<InvalidDataException>(() => new SqliteWorkflowRunStore(Database));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RuntimeObserverAndRestartUseTheSameHostSelectedStore(bool sqlite)
    {
        var store = Store(sqlite);
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddSingleton<IWorkflowRunStore>(store);
        builder.Services.AddLocalWorkflowRuntime(Path.Combine(directory, "unused-fallback"));
        using var host = builder.Build();
        await host.StartAsync();
        var runtime = host.Services.GetRequiredService<LocalWorkflowRuntime>();
        var observer = JournalRunObserver.FromStore(store);
        var run = await runtime.StartRevisionPausedAsync(Revision(), "hello");
        Assert.Equal("paused", (await observer.GetRunAsync(run.Header.RunId))!.Status);
        await runtime.ResumeRunAsync(run.Header.RunId);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!(await observer.GetRunAsync(run.Header.RunId))!.IsTerminal) await Task.Delay(10, deadline.Token);
        Assert.Equal("olleh", (await observer.GetRunAsync(run.Header.RunId))!.Events[^1].Data);
        await host.StopAsync(deadline.Token);
        Assert.False(Directory.Exists(Path.Combine(directory, "unused-fallback")));
        Assert.Single(await observer.ListRunsAsync());
        var abandoned = store.Create(Revision(), "not replayed");
        Store(sqlite).MarkUnfinishedAsInterrupted();
        Assert.Equal("interrupted", store.Get(abandoned.Header.RunId)!.Status);
        Assert.Equal("completed", store.Get(run.Header.RunId)!.Status);
        Assert.Single(store.Get(abandoned.Header.RunId)!.Events);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReadOnlyObserversSeeCompleteCommittedPrefixesDuringWrites(bool sqlite)
    {
        var writer = Store(sqlite);
        var reader = Store(sqlite);
        var run = writer.Create(Revision(), "hello");
        var firstCommit = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var observed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var writing = Task.Run(async () =>
        {
            writer.Append(run.Header.RunId, 0, [Event(run, 1)]);
            firstCommit.SetResult();
            await observed.Task.WaitAsync(deadline.Token);
            for (var sequence = 2; sequence <= 20; sequence++)
                writer.Append(run.Header.RunId, sequence - 1, [Event(run, sequence)]);
            writer.Append(run.Header.RunId, 20, [Event(run, 21, ExecutionEventKinds.Completed)]);
        });
        await firstCommit.Task.WaitAsync(deadline.Token);
        Assert.Single(reader.Get(run.Header.RunId)!.Events);
        observed.SetResult();
        do
        {
            deadline.Token.ThrowIfCancellationRequested();
            var snapshot = reader.Get(run.Header.RunId)!;
            Assert.Equal(Enumerable.Range(1, snapshot.Events.Length).Select(i => (long)i), snapshot.Events.Select(e => e.Sequence));
            Assert.Equal(snapshot.Events.Length == 21 ? "completed" : "running", snapshot.Status);
        } while (!writing.IsCompleted);
        await writing;
        Assert.Equal(21, reader.Get(run.Header.RunId)!.Events.Length);
    }

    [Fact]
    public void ExistingUnrelatedDatabaseIsNeverAdopted()
    {
        Directory.CreateDirectory(directory);
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Database, Pooling = false }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "CREATE TABLE unrelated(value TEXT); INSERT INTO unrelated VALUES('preserve');";
        command.ExecuteNonQuery();
        Assert.Throws<InvalidDataException>(() => new SqliteWorkflowRunStore(Database));
        command.CommandText = "SELECT value FROM unrelated;";
        Assert.Equal("preserve", command.ExecuteScalar());
    }

    public void Dispose() { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
}
