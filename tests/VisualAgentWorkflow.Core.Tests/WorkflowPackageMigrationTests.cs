using System.Collections.Immutable;
using System.Text.Json.Nodes;
using VisualAgentWorkflow.Core;
using Xunit;

namespace VisualAgentWorkflow.Core.Tests;

public sealed class WorkflowPackageMigrationTests
{
    [Fact]
    public void Migrated_package_round_trips_canonical_source_package_provenance()
    {
        var source = WorkflowRevision.Create(SequenceTransformPath(), "source") with
        {
            Layout = WorkflowLayout.Create(SequenceTransformPath())
        };
        var migration = WorkflowMigrationRegistry.TryMigrate(new(source, ["sequence-reverse-paths"], "migrator"));
        Assert.True(migration.Succeeded);
        var result = migration.Result!;

        var json = WorkflowPackageJson.Serialize(result);
        var package = WorkflowPackageJson.Deserialize(json);
        Assert.NotNull(package.MigrationProvenance);
        var provenance = package.MigrationProvenance!;
        var sourcePackage = WorkflowPackageJson.Deserialize(provenance.SourcePackageJson);

        Assert.Null(sourcePackage.MigrationProvenance);
        Assert.Equal(result.Provenance.SourceContentHash, sourcePackage.DefinitionHash);
        Assert.Equal(result.Provenance.TargetContentHash, package.DefinitionHash);
        Assert.Equal(provenance.SourcePackageHash, WorkflowPackageJson.ComputePackageHash(provenance.SourcePackageJson));
        Assert.Equal(json, WorkflowPackageJson.Serialize(result));
    }

    [Fact]
    public void Legacy_package_round_trip_has_no_migration_provenance()
    {
        var definition = SequenceTransformPath();
        var json = WorkflowPackageJson.Serialize(definition, WorkflowLayout.Create(definition));

        Assert.Null(WorkflowPackageJson.Deserialize(json).MigrationProvenance);
    }

    [Fact]
    public void Tampered_source_package_bytes_are_rejected()
    {
        var source = WorkflowRevision.Create(SequenceTransformPath(), "source");
        var migration = WorkflowMigrationRegistry.TryMigrate(new(source, ["sequence-reverse-paths"], "migrator"));
        var root = JsonNode.Parse(WorkflowPackageJson.Serialize(migration.Result!))!.AsObject();
        root["migrationProvenance"]!["sourcePackageJson"] = "{}";

        Assert.Throws<ArgumentException>(() => WorkflowPackageJson.Deserialize(root.ToJsonString()));
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
        var definition = new WorkflowDefinition(WorkflowSchemaVersions.SequencePathGraph, "package-migration", "input", nodes,
            ImmutableSortedDictionary<string, ConnectionDefinition>.Empty.WithComparers(StringComparer.Ordinal))
        {
            ExecutionConnections = execution
        };
        return GraphEditing.EnableSequenceTransformPathGraph(definition);
    }
}
