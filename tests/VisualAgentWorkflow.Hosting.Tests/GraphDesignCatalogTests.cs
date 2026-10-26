using VisualAgentWorkflow.Blazor;
using Microsoft.Data.Sqlite;
using VisualAgentWorkflow.Core;
using Xunit;

namespace VisualAgentWorkflow.Hosting.Tests;

public sealed class GraphDesignCatalogTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "vaw-layout-" + Guid.NewGuid());
    private string Database => Path.Combine(directory, "workflows.db");
    private SqliteWorkflowCatalog Open() => new(Database, "layout-editor");

    [Fact]
    public async Task LayoutAndDefinitionSaveAtomicallyAndStaleEditorsCannotOverwriteEither()
    {
        var definition = WorkflowTemplates.Create("uppercase");
        var layout = WorkflowLayout.Create(definition);
        layout = layout with { Nodes = layout.Nodes.SetItem("step-1", new(new(500, 200), "Title", "Comment")) };
        var store = Open();
        var saved = await store.SaveDesignAsync(definition, layout, 0);
        var audit = await store.ReadAuditAsync(definition.WorkflowId);
        await Assert.ThrowsAsync<WorkflowConflictException>(() => store.SaveDesignAsync(
            WorkflowTemplates.Create("reverse"), WorkflowLayout.Create(definition), 0));
        var current = (await Open().GetDesignAsync(definition.WorkflowId))!;
        Assert.Equal(saved.Draft.Generation, current.Draft.Generation);
        Assert.Equal(WorkflowJson.ComputeHash(definition), WorkflowJson.ComputeHash(current.Draft.Definition));
        Assert.Equal("Title", current.Layout.Nodes["step-1"].Label);
        Assert.Equal(audit.ToArray(), (await store.ReadAuditAsync(definition.WorkflowId)).ToArray());
        Assert.Equal(new[] { "draft.saved", "layout.saved" }, audit.Select(e => e.Kind));
    }

    [Fact]
    public async Task IncompleteDraftCanBeSavedAndReloadedButCannotBePublished()
    {
        var definition = WorkflowTemplates.Create("uppercase");
        var disconnected = GraphEditing.Disconnect(definition, definition.Connections.Keys.First());
        var store = Open();

        var saved = await store.SaveDesignAsync(disconnected, WorkflowLayout.Create(disconnected), 0);
        var reopened = (await Open().GetDesignAsync(definition.WorkflowId))!;

        Assert.Equal(saved.Draft.Generation, reopened.Draft.Generation);
        Assert.Equal(WorkflowJson.ComputeDraftHash(disconnected), WorkflowJson.ComputeDraftHash(reopened.Draft.Definition));
        await Assert.ThrowsAsync<ArgumentException>(() => store.PublishAsync(definition.WorkflowId, saved.Draft.Generation));
    }

    [Fact]
    public async Task LegacyNumericReasoningEffortDefinitionsAndRevisionsRemainReadable()
    {
        var baseline = WorkflowTemplates.Create("uppercase-reverse");
        var node = baseline.Nodes["step-1"] with
        {
            Type = NodeTypes.Agent,
            Agent = new AgentConfiguration("test.provider", "Answer the request.", [])
            { ReasoningEffort = AgentReasoningEffort.Low }
        };
        var definition = baseline with
        {
            SchemaVersion = "1.9",
            Nodes = baseline.Nodes.SetItem("step-1", node)
        };
        var store = Open();
        var saved = await store.SaveDesignAsync(definition, WorkflowLayout.Create(definition), 0);
        var published = await store.PublishAsync(definition.WorkflowId, saved.Draft.Generation);

        var reopened = Open();
        Assert.Empty(WorkflowValidator.Validate((await reopened.GetDraftAsync(definition.WorkflowId))!.Definition));
        Assert.Equal(published.Revision.ContentHash,
            (await reopened.GetRevisionAsync(published.Revision.RevisionId))!.Revision.ContentHash);
    }

    [Fact]
    public async Task PublishedLayoutIsImmutableAndRestoreRestoresItsAnnotationsAndRoutes()
    {
        var definition = WorkflowTemplates.Create("uppercase");
        var layout = WorkflowLayout.Create(definition);
        var link = definition.Connections.Keys.First();
        layout = layout with
        {
            Nodes = layout.Nodes.SetItem("step-1", new(new(460, 200), "Original", "Why this node exists")),
            Wires = layout.Wires.Add(link, new("curve", [new(new(350, 300), 90)])),
            Sections = layout.Sections.Add("text", new("Text operations", ["step-1"]))
        };
        var store = Open();
        var saved = await store.SaveDesignAsync(definition, layout, 0);
        var published = await store.PublishAsync(definition.WorkflowId, saved.Draft.Generation);
        var current = (await store.GetDraftAsync(definition.WorkflowId))!;
        await store.SaveDesignAsync(definition, WorkflowLayout.Create(definition), current.Generation);
        var pinned = (await Open().GetRevisionLayoutAsync(published.Revision.RevisionId))!;
        Assert.Equal(WorkflowLayoutJson.ComputeHash(layout, definition), WorkflowLayoutJson.ComputeHash(pinned, definition));
        current = (await store.GetDraftAsync(definition.WorkflowId))!;
        await store.RestoreDraftAsync(definition.WorkflowId, published.Revision.RevisionId, current.Generation);
        var restored = (await Open().GetDesignAsync(definition.WorkflowId))!;
        Assert.Equal("Original", restored.Layout.Nodes["step-1"].Label);
        Assert.Equal(90, restored.Layout.Wires[link].Points[0].Angle);
        Assert.Equal("Text operations", restored.Layout.Sections["text"].Label);
    }

    [Fact]
    public async Task VersionOneDatabaseMigratesWithoutChangingExistingDefinitionOrGeneration()
    {
        var definition = WorkflowTemplates.Create("uppercase");
        var draft = await Open().SaveDraftAsync(definition, 0);
        using (var connection = new SqliteConnection($"Data Source={Database};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "DROP TABLE workflow_layouts; DROP TABLE revision_layouts; PRAGMA user_version=1;";
            command.ExecuteNonQuery();
        }
        var reopened = (await Open().GetDesignAsync(definition.WorkflowId))!;
        Assert.Equal(draft.Generation, reopened.Draft.Generation);
        Assert.Equal(WorkflowJson.ComputeHash(definition), WorkflowJson.ComputeHash(reopened.Draft.Definition));
        Assert.Equal(definition.Nodes.Keys, reopened.Layout.Nodes.Keys);
    }

    [Fact]
    public async Task InvalidLayoutSaveLeavesBothDocumentsAndAuditUntouched()
    {
        var definition = WorkflowTemplates.Create("uppercase");
        var store = Open();
        var saved = await store.SaveDraftAsync(definition, 0);
        var invalid = WorkflowLayout.Create(definition) with { SchemaVersion = "unsupported" };
        await Assert.ThrowsAsync<ArgumentException>(() => store.SaveDesignAsync(WorkflowTemplates.Create("reverse"), invalid, saved.Generation));
        Assert.Equal(WorkflowJson.ComputeHash(definition), WorkflowJson.ComputeHash((await store.GetDraftAsync(definition.WorkflowId))!.Definition));
        Assert.Single(await store.ReadAuditAsync(definition.WorkflowId));
    }

    public void Dispose() { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
}
