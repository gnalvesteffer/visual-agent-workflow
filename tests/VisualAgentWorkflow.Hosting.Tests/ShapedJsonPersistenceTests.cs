using System.Collections.Immutable;
using VisualAgentWorkflow.Core;
using VisualAgentWorkflow.Hosting;
using Xunit;

namespace VisualAgentWorkflow.Hosting.Tests;

/// <summary>Decision 0082 shaped json: inputShape and core.object.get survive every persistence path with an identical content hash.</summary>
public sealed class ShapedJsonPersistenceTests
{
    private static WorkflowDefinition Shaped()
    {
        var shape = new JsonObjectShape([new("quantity", "Units", "integer", true), new("note", "Note", "string", false)]);
        var nodes = ImmutableSortedDictionary<string, NodeDefinition>.Empty
            .Add("input", new(NodeTypes.Input, "1.0.0") { InputDataType = "json", InputShape = shape })
            .Add("get", new(NodeTypes.ObjectGet, "1.0.0") { ObjectGet = new("quantity", "integer") })
            .Add("text", new(NodeTypes.IntegerToString, "1.0.0"))
            .Add("output", new(NodeTypes.Output, "1.0.0"));
        var data = ImmutableSortedDictionary<string, ConnectionDefinition>.Empty
            .Add("a", new(new("input", "out"), new("get", "in"), "data"))
            .Add("b", new(new("get", "out"), new("text", "in"), "data"))
            .Add("c", new(new("text", "out"), new("output", "in"), "data"));
        return new(WorkflowSchemaVersions.UnifiedWorkflowGraph, "shaped-persist", "input", nodes, data)
        {
            ExecutionConnections = ImmutableSortedDictionary<string, ConnectionDefinition>.Empty
                .Add("x", new(new("input", "then"), new("output", "execute"), "execution"))
        };
    }

    private static void AssertSame(WorkflowDefinition original, WorkflowDefinition actual)
    {
        Assert.Equal(original.Nodes["input"].InputShape!.Fields.AsEnumerable(), actual.Nodes["input"].InputShape!.Fields.AsEnumerable());
        Assert.Equal(original.Nodes["get"].ObjectGet, actual.Nodes["get"].ObjectGet);
        Assert.Equal(WorkflowJson.ComputeHash(original), WorkflowJson.ComputeHash(actual));
    }

    [Fact]
    public void Shaped_definition_round_trips_through_draft_package_file_and_sqlite_with_the_same_hash()
    {
        var original = Shaped();
        WorkflowValidator.ThrowIfInvalid(original);
        AssertSame(original, WorkflowJson.DeserializeDraft(WorkflowJson.SerializeDraft(original)));
        AssertSame(original, WorkflowJson.Deserialize(WorkflowJson.Serialize(original)));
        AssertSame(original, WorkflowPackageJson.Deserialize(WorkflowPackageJson.Serialize(original, WorkflowLayout.Create(original))).Definition);

        var directory = Path.Combine(Path.GetTempPath(), "vaw-shaped-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        try
        {
            var revision = WorkflowRevision.Create(original, "shaped");
            var fileId = new FileRunJournal(Path.Combine(directory, "runs")).Create(revision, "{}").Header.RunId;
            AssertSame(original, new FileRunJournal(Path.Combine(directory, "runs")).Get(fileId)!.Header.Definition);
            var sqlitePath = Path.Combine(directory, "runs.sqlite3");
            var sqliteId = new SqliteWorkflowRunStore(sqlitePath).Create(revision, "{}").Header.RunId;
            AssertSame(original, new SqliteWorkflowRunStore(sqlitePath).Get(sqliteId)!.Header.Definition);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(directory, true);
        }
    }
}
