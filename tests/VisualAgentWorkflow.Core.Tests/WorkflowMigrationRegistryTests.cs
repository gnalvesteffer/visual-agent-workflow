using System.Collections.Immutable;
using VisualAgentWorkflow.Core;
using Xunit;

namespace VisualAgentWorkflow.Core.Tests;

public sealed class WorkflowMigrationRegistryTests
{
    [Fact]
    public void Registry_exposes_the_explicit_sequence_schema_transitions()
    {
        Assert.Equal(
            ["sequence-reverse-paths", "sequence-transform-chains-from-reverse", "sequence-transform-chains-from-uppercase", "sequence-workflow-call-agents", "sequence-workflow-call-json", "sequence-workflow-call-json-chains", "sequence-workflow-call-json-merge", "sequence-workflow-call-paths", "sequence-workflow-call-transforms"],
            WorkflowMigrationRegistry.All.Select(descriptor => descriptor.OperationId));
        var descriptor = Assert.Single(WorkflowMigrationRegistry.All, item => item.OperationId == "sequence-workflow-call-paths");
        Assert.Equal("1.49", descriptor.FromSchema);
        Assert.Equal("1.50", descriptor.ToSchema);
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
        Assert.NotNull(result.TargetRevision.MigrationProvenance);
        Assert.Equal(result.Provenance.SourceRevisionId, result.TargetRevision.MigrationProvenance.SourceRevisionId);
        Assert.Equal(result.Provenance.SourceContentHash, result.TargetRevision.MigrationProvenance.SourceContentHash);
        Assert.Equal(result.Provenance.TargetRevisionId, result.TargetRevision.MigrationProvenance.TargetRevisionId);
        Assert.Equal(result.Provenance.TargetContentHash, result.TargetRevision.MigrationProvenance.TargetContentHash);
        Assert.Equal(result.Provenance.OperationIds.ToArray(), result.TargetRevision.MigrationProvenance.OperationIds.ToArray());
        Assert.Equal(result.Provenance.ImplementationVersions.ToArray(), result.TargetRevision.MigrationProvenance.ImplementationVersions.ToArray());

        var restored = WorkflowRevision.Rehydrate(result.TargetRevision.RevisionId, result.TargetRevision.Definition,
            result.TargetRevision.Author, result.TargetRevision.ParentRevisionId, result.TargetRevision.CreatedAt,
            result.TargetRevision.ContentHash, WorkflowMigrationRegistry.DeserializeProvenance(
                WorkflowMigrationRegistry.SerializeProvenance(result.Provenance)));
        Assert.Equal(result.TargetRevision.RevisionId, restored.RevisionId);
        Assert.NotNull(restored.MigrationProvenance);
        Assert.Equal(result.Provenance.SourceRevisionId, restored.MigrationProvenance.SourceRevisionId);
        Assert.Equal(result.Provenance.TargetRevisionId, restored.MigrationProvenance.TargetRevisionId);
        Assert.Equal(result.Provenance.OperationIds.ToArray(), restored.MigrationProvenance.OperationIds.ToArray());
        Assert.Equal(result.Provenance.ImplementationVersions.ToArray(), restored.MigrationProvenance.ImplementationVersions.ToArray());
    }

    [Fact]
    public void Explicit_workflow_call_transition_promotes_a_valid_1_49_revision()
    {
        var sourceDefinition = GraphEditing.EnableSequenceTransformChainGraph(SequenceTransformPath());
        var source = WorkflowRevision.Create(sourceDefinition, "source") with { Layout = WorkflowLayout.Create(sourceDefinition) };

        var attempt = WorkflowMigrationRegistry.TryMigrate(new(source, ["sequence-workflow-call-paths"], "migrator"));

        Assert.True(attempt.Succeeded, string.Join("; ", attempt.Diagnostics.Select(diagnostic => diagnostic.Message)));
        Assert.Equal(WorkflowSchemaVersions.SequenceWorkflowCallGraph, attempt.Result!.TargetRevision.Definition.SchemaVersion);
        Assert.Empty(WorkflowValidator.Validate(attempt.Result.TargetRevision.Definition));
        Assert.Equal(source.RevisionId, attempt.Result.TargetRevision.ParentRevisionId);
        Assert.Equal("1.49", attempt.Result.Provenance.SourceSchema);
        Assert.Equal("1.50", attempt.Result.Provenance.TargetSchema);
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
    public void Explicit_json_chain_migration_preserves_graph_identity_and_layout()
    {
        var sourceDefinition = JsonCallGraph();
        var source = WorkflowRevision.Create(sourceDefinition, "source") with { Layout = WorkflowLayout.Create(sourceDefinition) };

        var attempt = WorkflowMigrationRegistry.TryMigrate(new(source, ["sequence-workflow-call-json-chains"], "migrator"));

        Assert.True(attempt.Succeeded, string.Join("; ", attempt.Diagnostics.Select(diagnostic => diagnostic.Message)));
        var result = attempt.Result!;
        Assert.Equal(WorkflowSchemaVersions.SequenceWorkflowCallJsonChainGraph, result.TargetRevision.Definition.SchemaVersion);
        Assert.Empty(WorkflowValidator.Validate(result.TargetRevision.Definition));
        Assert.Equal(source.RevisionId, result.TargetRevision.ParentRevisionId);
        Assert.Equal(source.Definition.Nodes.Keys, result.TargetRevision.Definition.Nodes.Keys);
        Assert.Equal(source.Definition.Connections.Keys, result.TargetRevision.Definition.Connections.Keys);
        Assert.Equal(source.Layout, result.TargetRevision.Layout);
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

    [Fact]
    public void Provenance_rejects_tampered_target_identity_or_operation_chain()
    {
        var source = WorkflowRevision.Create(SequenceTransformPath(), "source");
        var attempt = WorkflowMigrationRegistry.TryMigrate(new(source, ["sequence-reverse-paths"], "migrator"));
        var result = attempt.Result!;

        Assert.Throws<InvalidDataException>(() => WorkflowMigrationRegistry.ValidateProvenance(
            result.Provenance with { TargetRevisionId = Guid.NewGuid() }, result.TargetRevision));
        Assert.Throws<InvalidDataException>(() => WorkflowMigrationRegistry.ValidateProvenance(
            result.Provenance with { OperationIds = ["sequence-transform-chains-from-reverse"] }, result.TargetRevision));
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

    private static WorkflowDefinition JsonCallGraph()
    {
        var child = new WorkflowDefinition("1.13", "migration-json-child", "input",
            new Dictionary<string, NodeDefinition>
            {
                ["input"] = new(NodeTypes.Input, "1.0.0") { InputDataType = WorkflowDataTypes.Json },
                ["output"] = new(NodeTypes.Output, "1.0.0") { OutputDataType = WorkflowDataTypes.Json }
            }.ToImmutableSortedDictionary(StringComparer.Ordinal),
            new Dictionary<string, ConnectionDefinition>
            {
                ["value"] = new(new("input", "out"), new("output", "in"), "data")
            }.ToImmutableSortedDictionary(StringComparer.Ordinal));
        var childRevision = WorkflowRevision.Create(child, "source");
        var call = new NodeDefinition(NodeTypes.WorkflowCall, "1.0.0")
        {
            WorkflowCall = new(child.WorkflowId, childRevision.RevisionId, 1, childRevision.ContentHash, child)
        };
        var definition = new WorkflowDefinition(WorkflowSchemaVersions.SequenceWorkflowCallJsonGraph, "migration-json-parent", "input",
            new Dictionary<string, NodeDefinition>
            {
                ["input"] = new(NodeTypes.Input, "1.0.0"),
                ["sequence"] = new(NodeTypes.Sequence, "1.0.0") { Sequence = new([new("call", "Call"), new("sibling", "Sibling")]) },
                ["call"] = call,
                ["sibling"] = new(NodeTypes.McpTool, "1.0.0", new("sibling", new string('a', 64))) { McpArgumentsJson = "{}" },
                ["output"] = new(NodeTypes.Output, "1.0.0") { OutputDataType = WorkflowDataTypes.Json }
            }.ToImmutableSortedDictionary(StringComparer.Ordinal),
            new Dictionary<string, ConnectionDefinition>
            {
                ["call-output-data"] = new(new("call", "out"), new("output", "in"), "data")
            }.ToImmutableSortedDictionary(StringComparer.Ordinal))
        {
            ExecutionConnections = new Dictionary<string, ConnectionDefinition>
            {
                ["input-sequence"] = new(new("input", "then"), new("sequence", "execute"), "execution"),
                ["sequence-call"] = new(new("sequence", "then.call"), new("call", "execute"), "execution"),
                ["call-output"] = new(new("call", "out"), new("output", "execute"), "execution"),
                ["sequence-sibling"] = new(new("sequence", "then.sibling"), new("sibling", "execute"), "execution"),
                ["sibling-output"] = new(new("sibling", "then"), new("output", "execute"), "execution")
            }.ToImmutableSortedDictionary(StringComparer.Ordinal)
        };
        return definition;
    }
}
