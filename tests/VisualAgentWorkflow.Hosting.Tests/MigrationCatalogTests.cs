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

    [Fact]
    public async Task Promoted_draft_updates_the_saved_design_and_persists_distinct_provenance()
    {
        var catalog = new SqliteWorkflowCatalog(Database, "promotion-test");
        var child = WorkflowRevision.Create(WorkflowTemplates.Create("uppercase") with { WorkflowId = "promotion-child" }, "test");
        var source = CallBearingDraft(child);
        var saved = await catalog.SaveDesignAsync(source, WorkflowLayout.Create(source), 0);
        var candidate = WorkflowDraftPromotionCandidate.Create(saved.Draft.Definition, saved.Layout, saved.Draft.Generation);
        var promotion = WorkflowDraftPromotion.TryPromote(candidate, "studio");

        var published = await ((IWorkflowDraftPromotionCatalog)catalog).PublishPromotedDraftAsync(promotion, saved.Draft.Generation);
        var reopened = new SqliteWorkflowCatalog(Database, "promotion-test");
        var stored = (await reopened.GetRevisionAsync(published.Revision.RevisionId))!.Revision;
        var draft = (await reopened.GetDesignAsync(source.WorkflowId))!;

        Assert.Equal(WorkflowSchemaVersions.SequenceWorkflowCallGraph, draft.Draft.Definition.SchemaVersion);
        Assert.Equal(WorkflowSchemaVersions.SequenceWorkflowCallGraph, stored.Definition.SchemaVersion);
        Assert.NotNull(stored.DraftPromotionProvenance);
        Assert.Equal(promotion.Provenance.SourceDefinitionHash, stored.DraftPromotionProvenance.SourceDefinitionHash);
        Assert.Null(stored.MigrationProvenance);
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

    private static WorkflowDefinition CallBearingDraft(WorkflowRevision child)
    {
        var nodes = ImmutableSortedDictionary<string, NodeDefinition>.Empty
            .Add("input", new(NodeTypes.Input, "1.0.0"))
            .Add("sequence", new(NodeTypes.Sequence, "1.0.0") { Sequence = new([new("call", "Call"), new("sibling", "Sibling")]) })
            .Add("call", new(NodeTypes.WorkflowCall, "1.0.0")
            {
                WorkflowCall = new(child.Definition.WorkflowId, child.RevisionId, 1, child.ContentHash, child.Definition)
            })
            .Add("sibling", new(NodeTypes.McpTool, "1.0.0", new("sibling", new string('c', 64))) { McpArgumentsJson = "{}" })
            .Add("output", new(NodeTypes.Output, "1.0.0"));
        return new(WorkflowSchemaVersions.SequenceTransformChainGraph, "promotion-catalog", "input", nodes,
            ImmutableSortedDictionary<string, ConnectionDefinition>.Empty)
        {
            ExecutionConnections = ImmutableSortedDictionary<string, ConnectionDefinition>.Empty
                .Add("input-sequence", new(new("input", "then"), new("sequence", "execute"), "execution"))
                .Add("sequence-call", new(new("sequence", "then.call"), new("call", "execute"), "execution"))
                .Add("call-output", new(new("call", "out"), new("output", "execute"), "execution"))
                .Add("sequence-sibling", new(new("sequence", "then.sibling"), new("sibling", "execute"), "execution"))
                .Add("sibling-output", new(new("sibling", "then"), new("output", "execute"), "execution"))
        };
    }

    public void Dispose()
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }
}
