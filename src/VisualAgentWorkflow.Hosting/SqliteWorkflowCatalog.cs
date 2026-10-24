using System.Globalization;
using Microsoft.Data.Sqlite;
using VisualAgentWorkflow.Core;

namespace VisualAgentWorkflow.Hosting;

/// <summary>Transactional local definition store. The development host supplies a placeholder actor.</summary>
public sealed class SqliteWorkflowCatalog : IWorkflowCatalog
{
    private readonly string connectionString;
    private readonly string actor;

    public SqliteWorkflowCatalog(string databasePath, string actor = "local-developer")
    {
        if (string.IsNullOrWhiteSpace(actor)) throw new ArgumentException("An actor is required.", nameof(actor));
        this.actor = actor;
        var path = Path.GetFullPath(databasePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path, ForeignKeys = true, DefaultTimeout = 10, Pooling = false
        }.ToString();
        using var connection = Open();
        using var transaction = connection.BeginTransaction(deferred: false);
        var schemaVersion = Convert.ToInt32(Scalar(connection, transaction, "PRAGMA user_version;"), CultureInfo.InvariantCulture);
        if (schemaVersion is not (0 or 1)) throw new InvalidDataException("Unsupported workflow database schema.");
        Execute(connection, transaction, """
            CREATE TABLE IF NOT EXISTS workflows (
                workflow_id TEXT PRIMARY KEY, draft_json TEXT NOT NULL, generation INTEGER NOT NULL,
                head_revision TEXT NULL, active_revision TEXT NULL);
            CREATE TABLE IF NOT EXISTS revisions (
                revision_id TEXT PRIMARY KEY, workflow_id TEXT NOT NULL REFERENCES workflows(workflow_id),
                version INTEGER NOT NULL, parent_revision TEXT NULL REFERENCES revisions(revision_id),
                author TEXT NOT NULL, created_at TEXT NOT NULL, definition_json TEXT NOT NULL, content_hash TEXT NOT NULL,
                UNIQUE(workflow_id, version));
            CREATE TABLE IF NOT EXISTS workflow_audit (
                sequence INTEGER PRIMARY KEY AUTOINCREMENT, workflow_id TEXT NOT NULL REFERENCES workflows(workflow_id),
                actor TEXT NOT NULL, timestamp TEXT NOT NULL, kind TEXT NOT NULL,
                generation INTEGER NOT NULL, revision_id TEXT NULL, content_hash TEXT NOT NULL);
            PRAGMA user_version = 1;
            """);
        transaction.Commit();
    }

    public Task<WorkflowDraft?> GetDraftAsync(string workflowId)
    {
        using var connection = Open();
        return Task.FromResult(GetDraft(connection, null, workflowId));
    }

    public Task<IReadOnlyList<WorkflowDraft>> ListDraftsAsync()
    {
        using var connection = Open();
        using var command = Command(connection, null, "SELECT draft_json,generation,head_revision,active_revision FROM workflows ORDER BY workflow_id;");
        using var reader = command.ExecuteReader();
        var drafts = new List<WorkflowDraft>();
        while (reader.Read()) drafts.Add(ReadDraft(reader));
        return Task.FromResult<IReadOnlyList<WorkflowDraft>>(drafts);
    }

    public Task<WorkflowDraft> SaveDraftAsync(WorkflowDefinition definition, long expectedGeneration)
    {
        var json = WorkflowJson.Serialize(definition);
        using var connection = Open();
        using var transaction = connection.BeginTransaction(deferred: false);
        var previous = GetDraft(connection, transaction, definition.WorkflowId);
        CheckGeneration(definition.WorkflowId, expectedGeneration, previous?.Generation ?? 0);
        var generation = checked(expectedGeneration + 1);
        Execute(connection, transaction, """
            INSERT INTO workflows(workflow_id,draft_json,generation) VALUES($id,$json,$generation)
            ON CONFLICT(workflow_id) DO UPDATE SET draft_json=$json,generation=$generation;
            """, ("$id", definition.WorkflowId), ("$json", json), ("$generation", generation));
        Audit(connection, transaction, definition.WorkflowId, "draft.saved", generation, null, WorkflowJson.ComputeHash(definition));
        var draft = GetDraft(connection, transaction, definition.WorkflowId)!;
        transaction.Commit();
        return Task.FromResult(draft);
    }

    /// <summary>Publishing creates an immutable version and explicitly activates it in one transaction.</summary>
    public Task<PublishedWorkflowRevision> PublishAsync(string workflowId, long expectedGeneration)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction(deferred: false);
        var draft = RequireDraft(connection, transaction, workflowId, expectedGeneration);
        var revision = WorkflowRevision.Create(draft.Definition, actor, draft.HeadRevisionId);
        var version = checked(Convert.ToInt32(Scalar(connection, transaction,
            "SELECT COALESCE(MAX(version),0) FROM revisions WHERE workflow_id=$id;", ("$id", workflowId)), CultureInfo.InvariantCulture) + 1);
        Execute(connection, transaction, """
            INSERT INTO revisions(revision_id,workflow_id,version,parent_revision,author,created_at,definition_json,content_hash)
            VALUES($revision,$id,$version,$parent,$author,$created,$json,$hash);
            UPDATE workflows SET head_revision=$revision,active_revision=$revision,generation=generation+1 WHERE workflow_id=$id;
            """, ("$revision", revision.RevisionId.ToString()), ("$id", workflowId), ("$version", version),
            ("$parent", revision.ParentRevisionId?.ToString()), ("$author", actor), ("$created", revision.CreatedAt.ToString("O")),
            ("$json", WorkflowJson.Serialize(revision.Definition)), ("$hash", revision.ContentHash));
        Audit(connection, transaction, workflowId, "revision.published-and-activated", checked(draft.Generation + 1), revision.RevisionId, revision.ContentHash);
        transaction.Commit();
        return Task.FromResult(new PublishedWorkflowRevision(version, revision));
    }

    public Task<PublishedWorkflowRevision?> GetRevisionAsync(Guid revisionId)
    {
        using var connection = Open();
        return Task.FromResult(GetRevision(connection, null, revisionId));
    }

    public Task<IReadOnlyList<PublishedWorkflowRevision>> ListRevisionsAsync(string workflowId)
    {
        using var connection = Open();
        using var command = Command(connection, null, "SELECT * FROM revisions WHERE workflow_id=$id ORDER BY version DESC;", ("$id", workflowId));
        using var reader = command.ExecuteReader();
        var revisions = new List<PublishedWorkflowRevision>();
        while (reader.Read()) revisions.Add(ReadRevision(reader));
        return Task.FromResult<IReadOnlyList<PublishedWorkflowRevision>>(revisions);
    }

    public Task<WorkflowDraft> RestoreDraftAsync(string workflowId, Guid revisionId, long expectedGeneration) =>
        ChangeRevision(workflowId, revisionId, expectedGeneration, restore: true);

    public Task<WorkflowDraft> ActivateAsync(string workflowId, Guid revisionId, long expectedGeneration) =>
        ChangeRevision(workflowId, revisionId, expectedGeneration, restore: false);

    private Task<WorkflowDraft> ChangeRevision(string workflowId, Guid revisionId, long expectedGeneration, bool restore)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction(deferred: false);
        var previous = RequireDraft(connection, transaction, workflowId, expectedGeneration);
        var revision = GetRevision(connection, transaction, revisionId)?.Revision ?? throw new KeyNotFoundException("Revision not found.");
        if (revision.Definition.WorkflowId != workflowId) throw new ArgumentException("Revision belongs to another workflow.");
        var sql = restore
            ? "UPDATE workflows SET draft_json=$value,generation=generation+1 WHERE workflow_id=$id;"
            : "UPDATE workflows SET active_revision=$value,generation=generation+1 WHERE workflow_id=$id;";
        Execute(connection, transaction, sql, ("$value", restore ? WorkflowJson.Serialize(revision.Definition) : revisionId.ToString()), ("$id", workflowId));
        Audit(connection, transaction, workflowId, restore ? "draft.restored" : "revision.activated", checked(previous.Generation + 1), revisionId, revision.ContentHash);
        var result = GetDraft(connection, transaction, workflowId)!;
        transaction.Commit();
        return Task.FromResult(result);
    }

    public Task<IReadOnlyList<WorkflowAuditEntry>> ReadAuditAsync(string workflowId)
    {
        using var connection = Open();
        using var command = Command(connection, null, "SELECT * FROM workflow_audit WHERE workflow_id=$id ORDER BY sequence;", ("$id", workflowId));
        using var reader = command.ExecuteReader();
        var entries = new List<WorkflowAuditEntry>();
        while (reader.Read()) entries.Add(new(reader.GetInt64(0), reader.GetString(1), reader.GetString(2),
            DateTimeOffset.Parse(reader.GetString(3), CultureInfo.InvariantCulture), reader.GetString(4), reader.GetInt64(5),
            NullableGuid(reader, 6), reader.GetString(7)));
        return Task.FromResult<IReadOnlyList<WorkflowAuditEntry>>(entries);
    }

    private void Audit(SqliteConnection connection, SqliteTransaction transaction, string id, string kind, long generation, Guid? revision, string hash) =>
        Execute(connection, transaction, """
            INSERT INTO workflow_audit(workflow_id,actor,timestamp,kind,generation,revision_id,content_hash)
            VALUES($id,$actor,$timestamp,$kind,$generation,$revision,$hash);
            """, ("$id", id), ("$actor", actor), ("$timestamp", DateTimeOffset.UtcNow.ToString("O")),
            ("$kind", kind), ("$generation", generation), ("$revision", revision?.ToString()), ("$hash", hash));

    private static WorkflowDraft RequireDraft(SqliteConnection connection, SqliteTransaction transaction, string id, long expected)
    {
        var draft = GetDraft(connection, transaction, id) ?? throw new KeyNotFoundException("Workflow not found.");
        CheckGeneration(id, expected, draft.Generation);
        return draft;
    }

    private static void CheckGeneration(string id, long expected, long actual)
    {
        if (expected < 0) throw new ArgumentOutOfRangeException(nameof(expected));
        if (expected != actual) throw new WorkflowConflictException(id, expected, actual);
    }

    private static WorkflowDraft? GetDraft(SqliteConnection connection, SqliteTransaction? transaction, string id)
    {
        using var command = Command(connection, transaction,
            "SELECT draft_json,generation,head_revision,active_revision FROM workflows WHERE workflow_id=$id;", ("$id", id));
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadDraft(reader) : null;
    }

    private static WorkflowDraft ReadDraft(SqliteDataReader reader) => new(WorkflowJson.Deserialize(reader.GetString(0)),
        reader.GetInt64(1), NullableGuid(reader, 2), NullableGuid(reader, 3));

    private static PublishedWorkflowRevision? GetRevision(SqliteConnection connection, SqliteTransaction? transaction, Guid id)
    {
        using var command = Command(connection, transaction, "SELECT * FROM revisions WHERE revision_id=$id;", ("$id", id.ToString()));
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadRevision(reader) : null;
    }

    private static PublishedWorkflowRevision ReadRevision(SqliteDataReader reader)
    {
        var definition = WorkflowJson.Deserialize(reader.GetString(6));
        if (definition.WorkflowId != reader.GetString(1)) throw new InvalidDataException("Stored revision workflow mismatch.");
        return new(reader.GetInt32(2), WorkflowRevision.Rehydrate(Guid.Parse(reader.GetString(0)), definition,
            reader.GetString(4), NullableGuid(reader, 3), DateTimeOffset.Parse(reader.GetString(5), CultureInfo.InvariantCulture), reader.GetString(7)));
    }

    private static Guid? NullableGuid(SqliteDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : Guid.Parse(reader.GetString(ordinal));
    private SqliteConnection Open() { var connection = new SqliteConnection(connectionString); connection.Open(); return connection; }

    private static SqliteCommand Command(SqliteConnection connection, SqliteTransaction? transaction, string sql, params (string Name, object? Value)[] parameters)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return command;
    }

    private static int Execute(SqliteConnection connection, SqliteTransaction transaction, string sql, params (string Name, object? Value)[] parameters)
    { using var command = Command(connection, transaction, sql, parameters); return command.ExecuteNonQuery(); }
    private static object? Scalar(SqliteConnection connection, SqliteTransaction transaction, string sql, params (string Name, object? Value)[] parameters)
    { using var command = Command(connection, transaction, sql, parameters); return command.ExecuteScalar(); }
}
