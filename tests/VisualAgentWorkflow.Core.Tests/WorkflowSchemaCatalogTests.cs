using System.Collections.Immutable;
using VisualAgentWorkflow.Core;
using Xunit;

namespace VisualAgentWorkflow.Core.Tests;

public sealed class WorkflowSchemaCatalogTests
{
    [Fact]
    public void Catalog_contains_each_supported_serialized_version_exactly_once()
    {
        var expected = Enumerable.Range(0, 44).Select(number => $"1.{number}").ToArray();

        Assert.Equal(expected, WorkflowSchemaCatalog.All.Select(schema => schema.Version));
        Assert.Equal(expected.Length, WorkflowSchemaCatalog.All.Select(schema => schema.Version).Distinct(StringComparer.Ordinal).Count());
        Assert.All(WorkflowSchemaCatalog.All, schema => Assert.True(schema.Supported));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("1.44")]
    [InlineData("1.03")]
    [InlineData(" 1.23")]
    [InlineData("1.23 ")]
    [InlineData("1.23.0")]
    public void Catalog_uses_exact_ordinal_schema_identity(string? version)
    {
        Assert.False(WorkflowSchemaCatalog.IsSupported(version));
        Assert.False(WorkflowSchemaCatalog.TryGet(version, out _));
        Assert.False(WorkflowSchemaVersions.UsesExplicitGraph(version));
        Assert.False(WorkflowSchemaVersions.UsesDirectMcpPins(version));
    }

    [Fact]
    public void Catalog_preserves_the_non_monotonic_feature_sets()
    {
        var expectedExplicit = new HashSet<string>(StringComparer.Ordinal)
        {
            "1.23", "1.24", "1.25", "1.26", "1.27", "1.28", "1.30", "1.31", "1.32", "1.33",
            "1.34", "1.35", "1.36", "1.37", "1.38", "1.39", "1.40", "1.41", "1.42", "1.43"
        };
        var expectedDirectMcpPins = new HashSet<string>(StringComparer.Ordinal)
        {
            "1.37", "1.40", "1.41", "1.42", "1.43"
        };

        Assert.Equal(expectedExplicit, WorkflowSchemaCatalog.All.Where(schema => schema.UsesExplicitGraph)
            .Select(schema => schema.Version).ToHashSet(StringComparer.Ordinal));
        Assert.Equal(expectedDirectMcpPins, WorkflowSchemaCatalog.All.Where(schema => schema.UsesDirectMcpPins)
            .Select(schema => schema.Version).ToHashSet(StringComparer.Ordinal));
    }

    [Fact]
    public void Definition_explicit_graph_helper_preserves_schema_129_connection_condition()
    {
        var nodes = ImmutableSortedDictionary<string, NodeDefinition>.Empty.WithComparers(StringComparer.Ordinal)
            .Add("input", new(NodeTypes.Input, "1.0.0"));
        var connections = ImmutableSortedDictionary<string, ConnectionDefinition>.Empty.WithComparers(StringComparer.Ordinal);
        var legacy = new WorkflowDefinition(WorkflowSchemaVersions.AgentParameterBindings, "legacy", "input", nodes, connections);
        var explicitDefinition = legacy with { ExecutionConnections = connections };

        Assert.False(WorkflowSchemaVersions.UsesExplicitGraph(legacy));
        Assert.True(WorkflowSchemaVersions.UsesExplicitGraph(explicitDefinition));
        Assert.True(WorkflowSchemaVersions.UsesExplicitGraph(new WorkflowDefinition(
            WorkflowSchemaVersions.AgentExecutionRoutes, "routes", "input", nodes, connections)));
    }

    [Fact]
    public void Catalog_metadata_does_not_enter_workflow_json_or_hashes()
    {
        var definition = new WorkflowDefinition(
            WorkflowSchemaVersions.ExplicitGraph, "catalog-wire", "input",
            ImmutableSortedDictionary<string, NodeDefinition>.Empty.WithComparers(StringComparer.Ordinal)
                .Add("input", new(NodeTypes.Input, "1.0.0"))
                .Add("output", new(NodeTypes.Output, "1.0.0")),
            ImmutableSortedDictionary<string, ConnectionDefinition>.Empty.WithComparers(StringComparer.Ordinal)
                .Add("value", new(new("input", "out"), new("output", "in"), "data")))
        {
            ExecutionConnections = ImmutableSortedDictionary<string, ConnectionDefinition>.Empty.WithComparers(StringComparer.Ordinal)
                .Add("activate", new(new("input", "then"), new("output", "execute"), "execution"))
        };
        var json = WorkflowJson.Serialize(definition);
        var hash = WorkflowJson.ComputeHash(definition);

        _ = WorkflowSchemaCatalog.All;

        Assert.Equal(json, WorkflowJson.Serialize(definition));
        Assert.Equal(hash, WorkflowJson.ComputeHash(definition));
    }
}
