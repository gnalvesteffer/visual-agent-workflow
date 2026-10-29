using System.Globalization;
using Microsoft.Data.Sqlite;
using System.Text.Json;
using VisualAgentWorkflow.Core;

namespace VisualAgentWorkflow.Hosting;

/// <summary>Transactional local definition store. The development host supplies a placeholder actor.</summary>
public sealed class SqliteWorkflowCatalog : IWorkflowDesignCatalog, IWorkflowMigrationCatalog,
    IWorkflowDraftPromotionCatalog, IWorkflowCatalogFileOperations
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
        if (schemaVersion is not (0 or 1 or 2 or 3 or 4 or 5)) throw new InvalidDataException("Unsupported workflow database schema.");
        Execute(connection, transaction, """
            CREATE TABLE IF NOT EXISTS workflows (
                workflow_id TEXT PRIMARY KEY, draft_json TEXT NOT NULL, generation INTEGER NOT NULL,
                head_revision TEXT NULL, active_revision TEXT NULL);
            CREATE TABLE IF NOT EXISTS revisions (
                revision_id TEXT PRIMARY KEY, workflow_id TEXT NOT NULL REFERENCES workflows(workflow_id),
                version INTEGER NOT NULL, parent_revision TEXT NULL REFERENCES revisions(revision_id),
                author TEXT NOT NULL, created_at TEXT NOT NULL, definition_json TEXT NOT NULL, content_hash TEXT NOT NULL,
                migration_provenance_json TEXT NULL, migration_provenance_hash TEXT NULL,
                draft_promotion_provenance_json TEXT NULL, draft_promotion_provenance_hash TEXT NULL,
                UNIQUE(workflow_id, version));
            CREATE TABLE IF NOT EXISTS workflow_audit (
                sequence INTEGER PRIMARY KEY AUTOINCREMENT, workflow_id TEXT NOT NULL REFERENCES workflows(workflow_id),
                actor TEXT NOT NULL, timestamp TEXT NOT NULL, kind TEXT NOT NULL,
                generation INTEGER NOT NULL, revision_id TEXT NULL, content_hash TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS workflow_layouts (
                workflow_id TEXT PRIMARY KEY REFERENCES workflows(workflow_id), layout_json TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS revision_layouts (
                revision_id TEXT PRIMARY KEY REFERENCES revisions(revision_id), layout_json TEXT NOT NULL);
            """);
        if (schemaVersion < 3)
        {
            if (!HasColumn(connection, transaction, "workflows", "archived_at"))
                Execute(connection, transaction, "ALTER TABLE workflows ADD COLUMN archived_at TEXT NULL;");
            Execute(connection, transaction, "PRAGMA user_version = 3;");
        }
        if (schemaVersion < 4)
        {
            if (!HasColumn(connection, transaction, "revisions", "migration_provenance_json"))
                Execute(connection, transaction, "ALTER TABLE revisions ADD COLUMN migration_provenance_json TEXT NULL;");
            if (!HasColumn(connection, transaction, "revisions", "migration_provenance_hash"))
                Execute(connection, transaction, "ALTER TABLE revisions ADD COLUMN migration_provenance_hash TEXT NULL;");
            Execute(connection, transaction, "PRAGMA user_version = 4;");
        }
        if (schemaVersion < 5)
        {
            if (!HasColumn(connection, transaction, "revisions", "draft_promotion_provenance_json"))
                Execute(connection, transaction, "ALTER TABLE revisions ADD COLUMN draft_promotion_provenance_json TEXT NULL;");
            if (!HasColumn(connection, transaction, "revisions", "draft_promotion_provenance_hash"))
                Execute(connection, transaction, "ALTER TABLE revisions ADD COLUMN draft_promotion_provenance_hash TEXT NULL;");
            Execute(connection, transaction, "PRAGMA user_version = 5;");
        }
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
        using var command = Command(connection, null, "SELECT draft_json,generation,head_revision,active_revision FROM workflows WHERE archived_at IS NULL ORDER BY workflow_id;");
        using var reader = command.ExecuteReader();
        var drafts = new List<WorkflowDraft>();
        while (reader.Read()) drafts.Add(ReadDraft(reader));
        return Task.FromResult<IReadOnlyList<WorkflowDraft>>(drafts);
    }

    public Task<IReadOnlyList<WorkflowDraft>> ListArchivedDraftsAsync()
    {
        using var connection = Open();
        using var command = Command(connection, null, "SELECT draft_json,generation,head_revision,active_revision FROM workflows WHERE archived_at IS NOT NULL ORDER BY workflow_id;");
        using var reader = command.ExecuteReader();
        var drafts = new List<WorkflowDraft>();
        while (reader.Read()) drafts.Add(ReadDraft(reader));
        return Task.FromResult<IReadOnlyList<WorkflowDraft>>(drafts);
    }

    public Task ArchiveWorkflowAsync(string workflowId, long expectedGeneration)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction(deferred: false);
        var draft = RequireDraft(connection, transaction, workflowId, expectedGeneration);
        if (IsArchived(connection, transaction, workflowId)) throw new InvalidOperationException("Workflow is already deleted.");
        var generation = checked(draft.Generation + 1);
        Execute(connection, transaction, "UPDATE workflows SET archived_at=$time,generation=$generation WHERE workflow_id=$id;",
            ("$time", DateTimeOffset.UtcNow.ToString("O")), ("$generation", generation), ("$id", workflowId));
        Audit(connection, transaction, workflowId, "workflow.archived", generation, null, WorkflowJson.ComputeDraftHash(draft.Definition));
        transaction.Commit();
        return Task.CompletedTask;
    }

    public Task RestoreArchivedWorkflowAsync(string workflowId, long expectedGeneration)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction(deferred: false);
        var draft = RequireDraft(connection, transaction, workflowId, expectedGeneration);
        if (!IsArchived(connection, transaction, workflowId)) throw new InvalidOperationException("Workflow is not in Deleted workflows.");
        var generation = checked(draft.Generation + 1);
        Execute(connection, transaction, "UPDATE workflows SET archived_at=NULL,generation=$generation WHERE workflow_id=$id;",
            ("$generation", generation), ("$id", workflowId));
        Audit(connection, transaction, workflowId, "workflow.restored", generation, null, WorkflowJson.ComputeDraftHash(draft.Definition));
        transaction.Commit();
        return Task.CompletedTask;
    }

    public Task<WorkflowDraft> SaveDraftAsync(WorkflowDefinition definition, long expectedGeneration) =>
        Task.FromResult(SaveDesign(definition, null, expectedGeneration).Draft);

    public Task<WorkflowDesign> SaveDesignAsync(WorkflowDefinition definition, WorkflowLayout layout, long expectedGeneration) =>
        Task.FromResult(SaveDesign(definition, layout, expectedGeneration));

    private WorkflowDesign SaveDesign(WorkflowDefinition definition, WorkflowLayout? layout, long expectedGeneration)
    {
        var json = WorkflowJson.SerializeDraft(definition);
        using var connection = Open();
        using var transaction = connection.BeginTransaction(deferred: false);
        var previous = GetDraft(connection, transaction, definition.WorkflowId);
        CheckGeneration(definition.WorkflowId, expectedGeneration, previous?.Generation ?? 0);
        if (previous is not null && IsArchived(connection, transaction, definition.WorkflowId))
            throw new InvalidOperationException("Workflow is in Deleted workflows. Restore it before editing.");
        // Legacy definition-only saves preserve surviving presentation identities.
        var explicitLayout = layout is not null;
        layout ??= previous is null ? WorkflowLayout.Create(definition) :
            ReadLayout(connection, transaction, "workflow_layouts", "workflow_id", definition.WorkflowId, previous.Definition).Reconcile(definition);
        var layoutJson = WorkflowLayoutJson.Serialize(layout, definition);
        var generation = checked(expectedGeneration + 1);
        Execute(connection, transaction, """
            INSERT INTO workflows(workflow_id,draft_json,generation) VALUES($id,$json,$generation)
            ON CONFLICT(workflow_id) DO UPDATE SET draft_json=$json,generation=$generation;
            INSERT INTO workflow_layouts(workflow_id,layout_json) VALUES($id,$layout)
            ON CONFLICT(workflow_id) DO UPDATE SET layout_json=$layout;
            """, ("$id", definition.WorkflowId), ("$json", json), ("$layout", layoutJson), ("$generation", generation));
        Audit(connection, transaction, definition.WorkflowId, "draft.saved", generation, null, WorkflowJson.ComputeDraftHash(definition));
        if (explicitLayout) Audit(connection, transaction, definition.WorkflowId, "layout.saved", generation, null, WorkflowLayoutJson.ComputeHash(layout, definition));
        var draft = GetDraft(connection, transaction, definition.WorkflowId)!;
        transaction.Commit();
        return new(draft, layout);
    }

    public Task<WorkflowDesign?> GetDesignAsync(string workflowId)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        var draft = GetDraft(connection, transaction, workflowId);
        var result = draft is null ? null : new WorkflowDesign(draft,
            ReadLayout(connection, transaction, "workflow_layouts", "workflow_id", workflowId, draft.Definition));
        transaction.Commit();
        return Task.FromResult(result);
    }

    public Task<WorkflowLayout?> GetRevisionLayoutAsync(Guid revisionId)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        var revision = GetRevision(connection, transaction, revisionId);
        var result = revision is null ? null : ReadLayout(connection, transaction, "revision_layouts", "revision_id", revisionId.ToString(), revision.Revision.Definition);
        transaction.Commit();
        return Task.FromResult(result);
    }

    private static WorkflowLayout ReadLayout(SqliteConnection connection, SqliteTransaction transaction, string table,
        string key, string id, WorkflowDefinition definition)
    {
        // Table/key are private constants, never request data.
        var json = Scalar(connection, transaction, $"SELECT layout_json FROM {table} WHERE {key}=$id;", ("$id", id)) as string;
        return json is null ? WorkflowLayout.Create(definition) : WorkflowLayoutJson.Deserialize(json, definition);
    }

    /// <summary>Publishing creates an immutable version and explicitly activates it in one transaction.</summary>
    public Task<PublishedWorkflowRevision> PublishAsync(string workflowId, long expectedGeneration)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction(deferred: false);
        var draft = RequireDraft(connection, transaction, workflowId, expectedGeneration);
        EnsureNotArchived(connection, transaction, workflowId);
        var revision = WorkflowRevision.Create(draft.Definition, actor, draft.HeadRevisionId);
        return Task.FromResult(PublishRevision(connection, transaction, draft, revision));
    }

    public Task<PublishedWorkflowRevision> PublishMigratedAsync(WorkflowRevision revision, long expectedGeneration)
    {
        ArgumentNullException.ThrowIfNull(revision);
        if (revision.MigrationProvenance is null)
            throw new ArgumentException("A migration provenance record is required.", nameof(revision));
        using var connection = Open();
        using var transaction = connection.BeginTransaction(deferred: false);
        var draft = RequireDraft(connection, transaction, revision.Definition.WorkflowId, expectedGeneration);
        EnsureNotArchived(connection, transaction, revision.Definition.WorkflowId);
        if (draft.HeadRevisionId != revision.ParentRevisionId || revision.ParentRevisionId is null)
            throw new InvalidOperationException("The migrated revision does not descend from the current workflow head.");
        var source = GetRevision(connection, transaction, revision.ParentRevisionId.Value)?.Revision;
        if (source is null || source.ContentHash != revision.MigrationProvenance.SourceContentHash)
            throw new InvalidOperationException("The migrated revision source does not match the current workflow head.");
        WorkflowMigrationRegistry.ValidateProvenance(revision.MigrationProvenance, revision);
        if (WorkflowJson.Serialize(draft.Definition) == WorkflowJson.Serialize(revision.Definition))
            throw new InvalidOperationException("The migrated revision must change the workflow definition.");
        return Task.FromResult(PublishRevision(connection, transaction, draft, revision));
    }

    public Task<PublishedWorkflowRevision> PublishPromotedDraftAsync(WorkflowDraftPromotionResult promotion,
        long expectedGeneration)
    {
        ArgumentNullException.ThrowIfNull(promotion);
        ArgumentNullException.ThrowIfNull(promotion.TargetRevision);
        promotion.Provenance.Validate(promotion.TargetRevision);
        var candidate = promotion.SourceCandidate;
        if (candidate.Definition.WorkflowId != promotion.TargetRevision.Definition.WorkflowId ||
            candidate.DefinitionHash != WorkflowJson.ComputeDraftHash(candidate.Definition) ||
            candidate.LayoutHash != WorkflowLayoutJson.ComputeHash(candidate.Layout, candidate.Definition))
            throw new ArgumentException("The draft promotion candidate is not canonical.", nameof(promotion));
        using var connection = Open();
        using var transaction = connection.BeginTransaction(deferred: false);
        var draft = RequireDraft(connection, transaction, candidate.Definition.WorkflowId, expectedGeneration);
        EnsureNotArchived(connection, transaction, candidate.Definition.WorkflowId);
        var currentLayout = ReadLayout(connection, transaction, "workflow_layouts", "workflow_id",
            candidate.Definition.WorkflowId, draft.Definition);
        if (draft.Generation != candidate.Generation || WorkflowJson.ComputeDraftHash(draft.Definition) != candidate.DefinitionHash ||
            WorkflowLayoutJson.ComputeHash(currentLayout, draft.Definition) != candidate.LayoutHash)
            throw new WorkflowConflictException(candidate.Definition.WorkflowId, candidate.Generation, draft.Generation);
        if (promotion.TargetRevision.ParentRevisionId != draft.HeadRevisionId)
            throw new InvalidOperationException("The promoted revision must descend from the current workflow head.");
        var version = checked(Convert.ToInt32(Scalar(connection, transaction,
            "SELECT COALESCE(MAX(version),0) FROM revisions WHERE workflow_id=$id;", ("$id", candidate.Definition.WorkflowId)), CultureInfo.InvariantCulture) + 1);
        var provenanceJson = WorkflowDraftPromotionProvenance.Serialize(promotion.Provenance);
        var provenanceHash = WorkflowDraftPromotionProvenance.ComputeHash(promotion.Provenance);
        var definitionJson = WorkflowJson.Serialize(promotion.TargetRevision.Definition);
        var layoutJson = WorkflowLayoutJson.Serialize(candidate.Layout, promotion.TargetRevision.Definition);
        Execute(connection, transaction, """
            INSERT INTO revisions(revision_id,workflow_id,version,parent_revision,author,created_at,definition_json,content_hash,migration_provenance_json,migration_provenance_hash,draft_promotion_provenance_json,draft_promotion_provenance_hash)
            VALUES($revision,$id,$version,$parent,$author,$created,$json,$hash,NULL,NULL,$provenance,$provenance_hash);
            UPDATE workflows SET draft_json=$draft_json,head_revision=$revision,active_revision=$revision,generation=generation+1 WHERE workflow_id=$id;
            UPDATE workflow_layouts SET layout_json=$layout WHERE workflow_id=$id;
            INSERT INTO revision_layouts(revision_id,layout_json) VALUES($revision,$layout);
            """, ("$revision", promotion.TargetRevision.RevisionId.ToString()), ("$id", candidate.Definition.WorkflowId),
            ("$version", version), ("$parent", promotion.TargetRevision.ParentRevisionId?.ToString()),
            ("$author", actor), ("$created", promotion.TargetRevision.CreatedAt.ToString("O")),
            ("$json", definitionJson), ("$hash", promotion.TargetRevision.ContentHash),
            ("$draft_json", WorkflowJson.SerializeDraft(promotion.TargetRevision.Definition)),
            ("$provenance", provenanceJson), ("$provenance_hash", provenanceHash), ("$layout", layoutJson));
        Audit(connection, transaction, candidate.Definition.WorkflowId, "draft.promoted-and-published",
            checked(draft.Generation + 1), promotion.TargetRevision.RevisionId, promotion.TargetRevision.ContentHash);
        transaction.Commit();
        return Task.FromResult(new PublishedWorkflowRevision(version, promotion.TargetRevision));
    }

    private PublishedWorkflowRevision PublishRevision(SqliteConnection connection, SqliteTransaction transaction,
        WorkflowDraft draft, WorkflowRevision revision)
    {
        var version = checked(Convert.ToInt32(Scalar(connection, transaction,
            "SELECT COALESCE(MAX(version),0) FROM revisions WHERE workflow_id=$id;", ("$id", revision.Definition.WorkflowId)), CultureInfo.InvariantCulture) + 1);
        var provenanceJson = revision.MigrationProvenance is { } provenance
            ? WorkflowMigrationRegistry.SerializeProvenance(provenance) : null;
        var provenanceHash = revision.MigrationProvenance is { } persistedProvenance
            ? WorkflowMigrationRegistry.ComputeProvenanceHash(persistedProvenance) : null;
        Execute(connection, transaction, """
            INSERT INTO revisions(revision_id,workflow_id,version,parent_revision,author,created_at,definition_json,content_hash,migration_provenance_json,migration_provenance_hash,draft_promotion_provenance_json,draft_promotion_provenance_hash)
            VALUES($revision,$id,$version,$parent,$author,$created,$json,$hash,$provenance,$provenance_hash,NULL,NULL);
            UPDATE workflows SET head_revision=$revision,active_revision=$revision,generation=generation+1 WHERE workflow_id=$id;
            """, ("$revision", revision.RevisionId.ToString()), ("$id", revision.Definition.WorkflowId), ("$version", version),
            ("$parent", revision.ParentRevisionId?.ToString()), ("$author", actor), ("$created", revision.CreatedAt.ToString("O")),
            ("$json", WorkflowJson.Serialize(revision.Definition)), ("$hash", revision.ContentHash),
            ("$provenance", provenanceJson), ("$provenance_hash", provenanceHash));
        var layout = ReadLayout(connection, transaction, "workflow_layouts", "workflow_id", revision.Definition.WorkflowId, draft.Definition);
        Execute(connection, transaction, "INSERT INTO revision_layouts(revision_id,layout_json) VALUES($id,$json);",
            ("$id", revision.RevisionId.ToString()), ("$json", WorkflowLayoutJson.Serialize(layout, draft.Definition)));
        Audit(connection, transaction, revision.Definition.WorkflowId, "revision.published-and-activated", checked(draft.Generation + 1), revision.RevisionId, revision.ContentHash);
        transaction.Commit();
        return new PublishedWorkflowRevision(version, revision);
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
        EnsureNotArchived(connection, transaction, workflowId);
        var revision = GetRevision(connection, transaction, revisionId)?.Revision ?? throw new KeyNotFoundException("Revision not found.");
        if (revision.Definition.WorkflowId != workflowId) throw new ArgumentException("Revision belongs to another workflow.");
        var sql = restore
            ? "UPDATE workflows SET draft_json=$value,generation=generation+1 WHERE workflow_id=$id;"
            : "UPDATE workflows SET active_revision=$value,generation=generation+1 WHERE workflow_id=$id;";
        Execute(connection, transaction, sql, ("$value", restore ? WorkflowJson.Serialize(revision.Definition) : revisionId.ToString()), ("$id", workflowId));
        if (restore)
        {
            var layout = ReadLayout(connection, transaction, "revision_layouts", "revision_id", revisionId.ToString(), revision.Definition);
            Execute(connection, transaction, """
                INSERT INTO workflow_layouts(workflow_id,layout_json) VALUES($id,$json)
                ON CONFLICT(workflow_id) DO UPDATE SET layout_json=$json;
                """, ("$id", workflowId), ("$json", WorkflowLayoutJson.Serialize(layout, revision.Definition)));
        }
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

    private static bool IsArchived(SqliteConnection connection, SqliteTransaction transaction, string id) =>
        Scalar(connection, transaction, "SELECT archived_at FROM workflows WHERE workflow_id=$id;", ("$id", id)) is string;

    private static void EnsureNotArchived(SqliteConnection connection, SqliteTransaction transaction, string id)
    {
        if (IsArchived(connection, transaction, id))
            throw new InvalidOperationException("Workflow is in Deleted workflows. Restore it before changing its published state.");
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

    private static WorkflowDraft ReadDraft(SqliteDataReader reader) => new(WorkflowJson.DeserializeDraft(reader.GetString(0)),
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
        var provenanceJson = reader.IsDBNull(8) ? null : reader.GetString(8);
        var provenanceHash = reader.IsDBNull(9) ? null : reader.GetString(9);
        WorkflowMigrationProvenance? provenance = null;
        if (provenanceJson is not null || provenanceHash is not null)
        {
            try
            {
                if (provenanceJson is null || provenanceHash is null)
                    throw new InvalidDataException("Stored migration provenance is incomplete.");
                provenance = WorkflowMigrationRegistry.DeserializeProvenance(provenanceJson);
                if (!string.Equals(provenanceHash, WorkflowMigrationRegistry.ComputeProvenanceHash(provenance), StringComparison.Ordinal))
                    throw new InvalidDataException("Stored migration provenance hash does not match.");
            }
            catch (Exception exception) when (exception is JsonException or InvalidOperationException or ArgumentException)
            {
                throw new InvalidDataException("Stored migration provenance is invalid.", exception);
            }
        }
        var draftPromotionJson = reader.IsDBNull(10) ? null : reader.GetString(10);
        var draftPromotionHash = reader.IsDBNull(11) ? null : reader.GetString(11);
        WorkflowDraftPromotionProvenance? draftPromotion = null;
        if (draftPromotionJson is not null || draftPromotionHash is not null)
        {
            if (draftPromotionJson is null || draftPromotionHash is null)
                throw new InvalidDataException("Stored draft promotion provenance is incomplete.");
            draftPromotion = WorkflowDraftPromotionProvenance.Deserialize(draftPromotionJson);
            if (draftPromotionHash != WorkflowDraftPromotionProvenance.ComputeHash(draftPromotion))
                throw new InvalidDataException("Stored draft promotion provenance hash does not match.");
        }
        return new(reader.GetInt32(2), WorkflowRevision.Rehydrate(Guid.Parse(reader.GetString(0)), definition,
            reader.GetString(4), NullableGuid(reader, 3), DateTimeOffset.Parse(reader.GetString(5), CultureInfo.InvariantCulture), reader.GetString(7), provenance, draftPromotion));
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
    private static bool HasColumn(SqliteConnection connection, SqliteTransaction transaction, string table, string column)
    {
        using var command = Command(connection, transaction, $"PRAGMA table_info({table});");
        using var reader = command.ExecuteReader();
        while (reader.Read()) if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }
}
