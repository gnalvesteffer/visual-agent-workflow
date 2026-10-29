using System.Collections.Immutable;
using VisualAgentWorkflow.Core;
using Xunit;

namespace VisualAgentWorkflow.Core.Tests;

public sealed class WorkflowMigrationRegistryTests
{
    [Fact]
    public void Registry_is_explicit_and_does_not_admit_schema_1_50()
    {
        Assert.Equal(
            ["sequence-reverse-paths", "sequence-transform-chains-from-reverse", "sequence-transform-chains-from-uppercase"],
            WorkflowMigrationRegistry.All.Select(descriptor => descriptor.OperationId));
        Assert.All(WorkflowMigrationRegistry.All, descriptor =>
        {
            Assert.NotEqual("1.50", descriptor.FromSchema);
            Assert.NotEqual("1.50", descriptor.ToSchema);
        });
    }

    [Fact]
    public void Named_migration_creates_a_new_source_preserving_revision_candidate()
    {
        var sourceDefinition = SequenceTransformPath();
        var source = WorkflowRevision.Create(sourceDefinition, "source") with
        {
            Layout = WorkflowLayout.Create(sourceDefinition)
        };
        var sourceJson = WorkflowJson.Serialize(source.Definition);
        var attempt = WorkflowMigrationRegistry.TryMigrate(new(source,
            ["sequence-reverse-paths"], "migrator"));

        Assert.True(attempt.Succeeded);
        var result = attempt.Result!;
        Assert.Equal(sourceJson, WorkflowJson.Serialize(source.Definition));
        Assert.Equal(source.ContentHash, result.Provenance.SourceContentHash);
        Assert.Equal(source.RevisionId, result.TargetRevision.ParentRevisionId);
        Assert.NotEqual(source.RevisionId, result.TargetRevision.RevisionId);
        Assert.Equal(WorkflowSchemaVersions.SequenceReversePathGraph, result.TargetRevision.Definition.SchemaVersion);
        Assert.Equal(source.Layout, result.TargetRevision.Layout);
        Assert.Equal(result.TargetRevision.ContentHash, result.Provenance.TargetContentHash);
        Assert.Equal(["sequence-reverse-paths"], result.Provenance.OperationIds.ToArray());
    }

    [Fact]
    public void Explicit_contiguous_operations_compose_in_declared_order()
    {
        var source = WorkflowRevision.Create(SequenceTransformPath(), "source");
        var attempt = WorkflowMigrationRegistry.TryMigrate(new(source,
            ["sequence-reverse-paths", "sequence-transform-chains-from-reverse"], "migrator"));

        Assert.True(attempt.Succeeded);
        Assert.Equal(WorkflowSchemaVersions.SequenceTransformChainGraph, attempt.Result!.TargetRevision.Definition.SchemaVersion);
        Assert.Equal(["sequence-reverse-paths", "sequence-transform-chains-from-reverse"],
            attempt.Result.Provenance.OperationIds.ToArray());
    }

    [Fact]
    public void Unknown_duplicate_and_noncontiguous_operations_fail_without_a_target()
    {
        var source = WorkflowRevision.Create(SequenceTransformPath(), "source");

        var unknown = WorkflowMigrationRegistry.TryMigrate(new(source, ["future"], "migrator"));
        Assert.False(unknown.Succeeded);
        Assert.Null(unknown.Result);
        Assert.Contains(unknown.Diagnostics, diagnostic => diagnostic.Code == "operation.unknown");

        var duplicate = WorkflowMigrationRegistry.TryMigrate(new(source,
            ["sequence-reverse-paths", "sequence-reverse-paths"], "migrator"));
        Assert.False(duplicate.Succeeded);
        Assert.Null(duplicate.Result);
        Assert.Contains(duplicate.Diagnostics, diagnostic => diagnostic.Code == "operation.duplicate");

        var noncontiguous = WorkflowMigrationRegistry.TryMigrate(new(source,
            ["sequence-transform-chains-from-reverse"], "migrator"));
        Assert.False(noncontiguous.Succeeded);
        Assert.Null(noncontiguous.Result);
        Assert.Contains(noncontiguous.Diagnostics, diagnostic => diagnostic.Code == "operation.source-schema");
    }

    [Fact]
    public void Migration_requires_a_valid_source_and_author()
    {
        var source = WorkflowRevision.Create(SequenceTransformPath(), "source");
        var missingAuthor = WorkflowMigrationRegistry.TryMigrate(new(source,
            ["sequence-reverse-paths"], ""));
        Assert.False(missingAuthor.Succeeded);
        Assert.Contains(missingAuthor.Diagnostics, diagnostic => diagnostic.Code == "author.required");

        var missingOperations = WorkflowMigrationRegistry.TryMigrate(new(source,
            ImmutableArray<string>.Empty, "migrator"));
        Assert.False(missingOperations.Succeeded);
        Assert.Contains(missingOperations.Diagnostics, diagnostic => diagnostic.Code == "operations.required");
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
            ["input"] = input,
            ["sequence"] = sequence,
            ["first"] = first,
            ["second"] = second,
            ["output"] = output
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
        var definition = new WorkflowDefinition(WorkflowSchemaVersions.SequencePathGraph, "migration", "input", nodes,
            ImmutableSortedDictionary<string, ConnectionDefinition>.Empty.WithComparers(StringComparer.Ordinal))
        {
            ExecutionConnections = execution
        };
        return GraphEditing.EnableSequenceTransformPathGraph(definition);
    }
}
