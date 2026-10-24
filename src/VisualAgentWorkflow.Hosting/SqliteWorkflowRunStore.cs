using System.Collections.Immutable;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using VisualAgentWorkflow.Core;

namespace VisualAgentWorkflow.Hosting;

/// <summary>Transactional local run evidence. Concurrent appends use expected sequences; runtime ownership is still exclusive.</summary>
public sealed partial class SqliteWorkflowRunStore : IWorkflowCheckpointStore, IParameterizedWorkflowRunStore, ILocalRuntimeOwnershipStore, IWorkflowRunReconciliationStore
{
    private const int ApplicationId = 0x56415752;
    private readonly string connectionString;
    private readonly string ownershipPath;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public SqliteWorkflowRunStore(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        var path = Path.GetFullPath(databasePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        ownershipPath = path + ".owner";
        connectionString = new SqliteConnectionStringBuilder { DataSource = path, Pooling = false, DefaultTimeout = 10 }.ToString();
        using var connection = Open();
        using var transaction = connection.BeginTransaction(deferred: false);
        var applicationId = Scalar(connection, transaction, "PRAGMA application_id;");
        var version = Scalar(connection, transaction, "PRAGMA user_version;");
        var tableCount = Scalar(connection, transaction, "SELECT count(*) FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%';");
        if (applicationId != ApplicationId && (applicationId != 0 || version != 0 || tableCount != 0) ||
            applicationId == ApplicationId && version is not (1 or 2))
            throw new InvalidDataException("Unsupported run-store database identity or schema version.");
        Execute(connection, transaction, $"""
            CREATE TABLE IF NOT EXISTS runs (
                run_id TEXT PRIMARY KEY,
                header_json TEXT NOT NULL,
                sequence INTEGER NOT NULL CHECK(sequence >= 0),
                status TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS run_events (
                run_id TEXT NOT NULL REFERENCES runs(run_id),
                sequence INTEGER NOT NULL CHECK(sequence > 0),
                event_json TEXT NOT NULL,
                PRIMARY KEY(run_id, sequence));
            CREATE TABLE IF NOT EXISTS run_checkpoints (
                run_id TEXT PRIMARY KEY REFERENCES runs(run_id),
                checkpoint_json TEXT NOT NULL);
            PRAGMA application_id={ApplicationId};
            PRAGMA user_version=2;
            """);
        transaction.Commit();
    }

    public RunSnapshot Create(WorkflowRevision revision, string input)
        => Create(revision, input, null);

    public RunSnapshot Create(WorkflowRevision revision, string input, ImmutableSortedDictionary<string, string>? parameters)
    {
        var snapshot = RunStoreRecords.Create(revision, input, parameters);
        using var connection = Open();
        using var transaction = connection.BeginTransaction(deferred: false);
        Execute(connection, transaction, "INSERT INTO runs(run_id,header_json,sequence,status) VALUES($id,$header,0,'queued');",
            ("$id", snapshot.Header.RunId.ToString()), ("$header", JsonSerializer.Serialize(snapshot.Header, Json)));
        transaction.Commit();
        return snapshot;
    }

    public RunSnapshot Append(Guid runId, long expectedSequence, IReadOnlyList<ExecutionEvent> events)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction(deferred: false);
        var snapshot = Read(connection, transaction, runId) ?? throw new KeyNotFoundException("Unknown run.");
        var next = RunStoreRecords.Apply(snapshot, expectedSequence, events);
        WriteTransition(connection, transaction, snapshot, next);
        transaction.Commit();
        return next;
    }

    public IDisposable AcquireRuntimeOwnership() => LocalRuntimeOwnership.Acquire(ownershipPath);

    private static void WriteTransition(SqliteConnection connection, SqliteTransaction transaction, RunSnapshot snapshot, RunSnapshot next)
    {
        var runId = snapshot.Header.RunId;
        var expectedSequence = snapshot.Events.Length;
        foreach (var evt in next.Events.Skip(snapshot.Events.Length))
            Execute(connection, transaction, "INSERT INTO run_events(run_id,sequence,event_json) VALUES($id,$sequence,$event);",
                ("$id", runId.ToString()), ("$sequence", evt.Sequence), ("$event", JsonSerializer.Serialize(evt, Json)));
        var changed = Execute(connection, transaction, "UPDATE runs SET sequence=$next,status=$status WHERE run_id=$id AND sequence=$expected;",
            ("$next", next.Events.Length), ("$status", next.Status), ("$id", runId.ToString()), ("$expected", expectedSequence));
        if (changed != 1) throw new WorkflowRunStoreConflictException();
    }

    public RunSnapshot? Get(Guid runId)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction(deferred: true);
        var snapshot = Read(connection, transaction, runId);
        transaction.Commit();
        return snapshot;
    }

    public IReadOnlyList<RunSnapshot> List()
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction(deferred: true);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT run_id FROM runs;";
        var ids = new List<Guid>();
        using (var reader = command.ExecuteReader())
            while (reader.Read()) ids.Add(Guid.Parse(reader.GetString(0)));
        var snapshots = ids.Select(id => Read(connection, transaction, id)!).OrderByDescending(r => r.Header.CreatedAt).ToArray();
        transaction.Commit();
        return snapshots;
    }

    private static RunSnapshot? Read(SqliteConnection connection, SqliteTransaction transaction, Guid runId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT header_json,sequence,status FROM runs WHERE run_id=$id;";
        command.Parameters.AddWithValue("$id", runId.ToString());
        RunHeader header;
        long sequence;
        string status;
        using (var reader = command.ExecuteReader())
        {
            if (!reader.Read()) return null;
            header = JsonSerializer.Deserialize<RunHeader>(reader.GetString(0), Json) ?? throw new InvalidDataException("Invalid run header.");
            sequence = reader.GetInt64(1);
            status = reader.GetString(2);
        }
        command.CommandText = "SELECT event_json FROM run_events WHERE run_id=$id ORDER BY sequence;";
        var events = ImmutableArray.CreateBuilder<ExecutionEvent>();
        using (var reader = command.ExecuteReader())
            while (reader.Read()) events.Add(JsonSerializer.Deserialize<ExecutionEvent>(reader.GetString(0), Json)
                ?? throw new InvalidDataException("Invalid event record."));
        var snapshot = new RunSnapshot(header, events.ToImmutable());
        RunStoreRecords.Validate(snapshot, runId);
        if (sequence != snapshot.Events.Length || status != snapshot.Status)
            throw new InvalidDataException("Stored run state does not match its event evidence.");
        return snapshot;
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(connectionString);
        try
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA foreign_keys=ON; PRAGMA synchronous=FULL;";
            command.ExecuteNonQuery();
            return connection;
        }
        catch { connection.Dispose(); throw; }
    }

    private static long Scalar(SqliteConnection connection, SqliteTransaction transaction, string sql)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static int Execute(SqliteConnection connection, SqliteTransaction transaction, string sql, params (string Name, object Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var parameter in parameters) command.Parameters.AddWithValue(parameter.Name, parameter.Value);
        return command.ExecuteNonQuery();
    }
}
