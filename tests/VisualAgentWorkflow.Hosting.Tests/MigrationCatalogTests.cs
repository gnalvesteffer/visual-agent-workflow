using System.Collections.Immutable;
using Microsoft.Data.Sqlite;
using VisualAgentWorkflow.Blazor;
using VisualAgentWorkflow.Core;
using Xunit;

namespace VisualAgentWorkflow.Hosting.Tests;

public sealed class MigrationCatalogTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "vaw-migration-catalog-" + Guid.NewGuid());
    private string Database => Path.Combine(directory, "workflows.db");

    [Fact]
    public async Task Explicitly_published_migration_provenance_survives_reopen()
    {
        var catalog = new SqliteWorkflowCatalog(Database, "migration-test");
        var sourceDefinition = SequenceTransformPath();
        var draft = await catalog.SaveDesignAsync(sourceDefinition, WorkflowLayout.Create(sourceDefinition), 0);
        var published = await catalog.PublishAsync(sourceDefinition.WorkflowId, draft.Draft.Generation);
        var attempt = WorkflowMigrationRegistry.TryMigrate(new(published.Revision, ["sequence-reverse-paths"], "migration-test"));
        Assert.True(attempt.Succeeded);

        var migrated = await catalog.PublishMigratedAsync(attempt.Result!.TargetRevision,
            (await catalog.GetDraftAsync(sourceDefinition.WorkflowId))!.Generation);
        var reopened = new SqliteWorkflowCatalog(Database, "migration-test");
        var stored = (await reopened.GetRevisionAsync(migrated.Revision.RevisionId))!.Revision;

        Assert.NotNull(stored.MigrationProvenance);
        Assert.Equal(attempt.Result.Provenance.SourceRevisionId, stored.MigrationProvenance.SourceRevisionId);
        Assert.Equal(attempt.Result.Provenance.SourceSchema, stored.MigrationProvenance.SourceSchema);
        Assert.Equal(attempt.Result.Provenance.SourceContentHash, stored.MigrationProvenance.SourceContentHash);
        Assert.Equal(attempt.Result.Provenance.TargetRevisionId, stored.MigrationProvenance.TargetRevisionId);
        Assert.Equal(attempt.Result.Provenance.TargetSchema, stored.MigrationProvenance.TargetSchema);
        Assert.Equal(attempt.Result.Provenance.TargetContentHash, stored.MigrationProvenance.TargetContentHash);
        Assert.Equal(attempt.Result.Provenance.OperationIds.ToArray(), stored.MigrationProvenance.OperationIds.ToArray());
        Assert.Equal(attempt.Result.Provenance.ImplementationVersions.ToArray(), stored.MigrationProvenance.ImplementationVersions.ToArray());
        Assert.Equal(attempt.Result.TargetRevision.ContentHash, stored.ContentHash);
        Assert.Equal(attempt.Result.TargetRevision.ParentRevisionId, stored.ParentRevisionId);
    }

    [Fact]
    public async Task Tampered_migration_provenance_is_rejected_on_read()
    {
        var catalog = new SqliteWorkflowCatalog(Database, "migration-test");
        var sourceDefinition = SequenceTransformPath();
        var draft = await catalog.SaveDesignAsync(sourceDefinition, WorkflowLayout.Create(sourceDefinition), 0);
        var published = await catalog.PublishAsync(sourceDefinition.WorkflowId, draft.Draft.Generation);
        var attempt = WorkflowMigrationRegistry.TryMigrate(new(published.Revision, ["sequence-reverse-paths"], "migration-test"));
        var migrated = await catalog.PublishMigratedAsync(attempt.Result!.TargetRevision,
            (await catalog.GetDraftAsync(sourceDefinition.WorkflowId))!.Generation);

        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Database }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE revisions SET migration_provenance_json = '{\"sourceRevisionId\":\"00000000-0000-0000-0000-000000000001\"}' WHERE revision_id=$id;";
        command.Parameters.AddWithValue("$id", migrated.Revision.RevisionId.ToString());
        command.ExecuteNonQuery();

        var reopened = new SqliteWorkflowCatalog(Database, "migration-test");
        await Assert.ThrowsAsync<InvalidDataException>(() => reopened.GetRevisionAsync(migrated.Revision.RevisionId));
    }

    [Fact]
    public async Task Existing_database_rows_upgrade_with_absent_legacy_provenance()
    {
        var catalog = new SqliteWorkflowCatalog(Database, "migration-test");
        var definition = WorkflowTemplates.Create("uppercase");
        var draft = await catalog.SaveDraftAsync(definition, 0);
        var published = await catalog.PublishAsync(definition.WorkflowId, draft.Generation);

        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Database }.ToString()))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "ALTER TABLE revisions DROP COLUMN migration_provenance_hash; ALTER TABLE revisions DROP COLUMN migration_provenance_json; PRAGMA user_version=3;";
            command.ExecuteNonQuery();
        }

        var reopened = new SqliteWorkflowCatalog(Database, "migration-test");
        Assert.Null((await reopened.GetRevisionAsync(published.Revision.RevisionId))!.Revision.MigrationProvenance);
    }

    private static WorkflowDefinition SequenceTransformPath()
    {
        var input = new NodeDefinition(NodeTypes.Input, "1.0.0");
        var sequence = new NodeDefinition(NodeTypes.Sequence, "1.0.0")
        {
            Sequence = new([new("first", "First"), new("second", "Second"), new("third", "Third")])
        };
        var output = new NodeDefinition(NodeTypes.Output, "1.0.0");
        var first = new NodeDefinition(NodeTypes.McpTool, "1.0.0",
            new ToolBindingRequest("first", new string('a', 64))) { McpArgumentsJson = "{}" };
        var second = new NodeDefinition(NodeTypes.McpTool, "1.0.0",
            new ToolBindingRequest("second", new string('b', 64))) { McpArgumentsJson = "{}" };
        var nodes = new Dictionary<string, NodeDefinition>
        {
            ["input"] = input, ["sequence"] = sequence, ["first"] = first, ["second"] = second, ["output"] = output
        }.ToImmutableSortedDictionary(StringComparer.Ordinal);
        var execution = new Dictionary<string, ConnectionDefinition>
        {
            ["input-sequence"] = new(new("input", "then"), new("sequence", "execute"), "execution"),
            ["sequence-first"] = new(new("sequence", "then.first"), new("first", "execute"), "execution"),
            ["first-output"] = new(new("first", "then"), new("output", "execute"), "execution"),
            ["sequence-second"] = new(new("sequence", "then.second"), new("second", "execute"), "execution"),
            ["second-output"] = new(new("second", "then"), new("output", "execute"), "execution"),
            ["sequence-third"] = new(new("sequence", "then.third"), new("output", "execute"), "execution")
        }.ToImmutableSortedDictionary(StringComparer.Ordinal);
        var definition = new WorkflowDefinition(WorkflowSchemaVersions.SequencePathGraph, "migration-catalog", "input", nodes,
            ImmutableSortedDictionary<string, ConnectionDefinition>.Empty.WithComparers(StringComparer.Ordinal))
        {
            ExecutionConnections = execution
        };
        return GraphEditing.EnableSequenceTransformPathGraph(definition);
    }

    public void Dispose()
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }
}
