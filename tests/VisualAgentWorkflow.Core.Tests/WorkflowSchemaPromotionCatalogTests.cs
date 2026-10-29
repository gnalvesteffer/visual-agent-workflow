using System.Collections.Immutable;
using VisualAgentWorkflow.Core;
using Xunit;

namespace VisualAgentWorkflow.Core.Tests;

public sealed class WorkflowSchemaPromotionCatalogTests
{
    [Fact]
    public void Catalog_is_a_stable_non_executable_inventory_of_named_promotions()
    {
        var expected = new[]
        {
            ("explicit-branch", "1.23", "1.24", nameof(GraphEditing.EnableExplicitBranchGraph)),
            ("typed-boolean", "1.24", "1.25", nameof(GraphEditing.EnableTypedBooleanGraph)),
            ("boolean-topology", "1.25", "1.26", nameof(GraphEditing.EnableBooleanTopologyGraph)),
            ("pure-graph-functions", "1.26", "1.27", nameof(GraphEditing.EnablePureGraphFunctions)),
            ("agent-graph-function-tools", "1.27", "1.28", nameof(GraphEditing.EnableAgentGraphFunctionTools)),
            ("agent-integer-values", "1.30", "1.31", nameof(GraphEditing.EnableIntegerGraph)),
            ("string-integer-conversion", "1.31", "1.39", nameof(GraphEditing.EnableStringIntegerConversionGraph)),
            ("direct-mcp-typed-pins", "1.23", "1.37", nameof(GraphEditing.EnableDirectMcpGraph)),
            ("finite-number-pins", "1.37", "1.40", nameof(GraphEditing.EnableNumberGraph)),
            ("number-parameter-propagation", "1.40", "1.41", nameof(GraphEditing.EnableNumberPropagationGraph)),
            ("number-addition", "1.41", "1.42", nameof(GraphEditing.EnableNumberArithmeticGraph)),
            ("sequence-paths", "1.38", "1.43", nameof(GraphEditing.EnableSequencePathGraph)),
            ("sequence-agent-paths", "1.43", "1.46", nameof(GraphEditing.EnableSequenceAgentPathGraph)),
            ("sequence-transform-paths", "1.43", "1.47", nameof(GraphEditing.EnableSequenceTransformPathGraph)),
            ("sequence-reverse-paths", "1.47", "1.48", nameof(GraphEditing.EnableSequenceReversePathGraph)),
            ("sequence-transform-chains-from-uppercase", "1.47", "1.49", nameof(GraphEditing.EnableSequenceTransformChainGraph)),
            ("sequence-transform-chains-from-reverse", "1.48", "1.49", nameof(GraphEditing.EnableSequenceTransformChainGraph)),
            ("sequence-workflow-call-paths", "1.49", "1.50", nameof(GraphEditing.EnableSequenceWorkflowCallGraph)),
            ("sequence-workflow-call-transforms", "1.50", "1.51", nameof(GraphEditing.EnableSequenceWorkflowCallTransformGraph)),
            ("sequence-workflow-call-agents", "1.51", "1.52", nameof(GraphEditing.EnableSequenceWorkflowCallAgentGraph)),
            ("sequence-workflow-call-json", "1.52", "1.53", nameof(GraphEditing.EnableSequenceWorkflowCallJsonGraph)),
            ("sequence-workflow-call-json-chains", "1.53", "1.54", nameof(GraphEditing.EnableSequenceWorkflowCallJsonChainGraph)),
            ("mutable-variables", "1.23", "1.44", nameof(GraphEditing.EnableMutableVariablesGraph))
        };

        Assert.Equal(expected, WorkflowSchemaPromotionCatalog.All
            .Select(promotion => (promotion.OperationId, promotion.FromSchema, promotion.ToSchema, promotion.GraphEditingMethod)));
        Assert.All(WorkflowSchemaPromotionCatalog.All.Where(promotion => promotion.OperationId is not ("mutable-variables" or "sequence-workflow-call-paths")), promotion => Assert.False(promotion.ChangesTopology));
        Assert.Equal(expected.Length, WorkflowSchemaPromotionCatalog.All.Select(promotion => promotion.OperationId)
            .Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void Promotion_inventory_is_not_serialized_into_definitions_or_hashes()
    {
        var definition = ExplicitGraph();
        var json = WorkflowJson.Serialize(definition);

        Assert.DoesNotContain("explicit-branch", json, StringComparison.Ordinal);
        Assert.DoesNotContain("GraphEditingMethod", json, StringComparison.Ordinal);
        Assert.Equal(WorkflowJson.ComputeHash(definition), WorkflowJson.ComputeHash(WorkflowJson.Deserialize(json)));
    }

    [Fact]
    public void Explicit_graph_promotion_leaves_the_source_definition_unchanged_and_returns_a_new_draft()
    {
        var source = LegacyBlankGraph();
        var sourceJson = WorkflowJson.Serialize(source);
        var sourceHash = WorkflowJson.ComputeHash(source);

        var promoted = GraphEditing.EnableExplicitGraph(source);

        Assert.Equal(WorkflowSchemaVersions.ExplicitGraph, promoted.SchemaVersion);
        Assert.NotSame(source, promoted);
        Assert.Equal(sourceJson, WorkflowJson.Serialize(source));
        Assert.Equal(sourceHash, WorkflowJson.ComputeHash(source));
        Assert.NotEqual(sourceHash, WorkflowJson.ComputeHash(promoted));
        Assert.Empty(WorkflowValidator.Validate(promoted));
    }

    [Fact]
    public void Inventory_does_not_claim_bootstrap_or_topology_mutating_edits_as_schema_promotions()
    {
        Assert.DoesNotContain(WorkflowSchemaPromotionCatalog.All, promotion =>
            promotion.GraphEditingMethod is nameof(GraphEditing.EnableExplicitGraph) or nameof(GraphEditing.CreateConfigurableParallelGraph));
        Assert.DoesNotContain(WorkflowSchemaPromotionCatalog.All, promotion =>
            promotion.ChangesTopology && promotion.OperationId is not ("mutable-variables" or "sequence-workflow-call-paths"));
    }

    private static WorkflowDefinition ExplicitGraph() => new(
        WorkflowSchemaVersions.ExplicitGraph, "promotion-test", "input",
        new Dictionary<string, NodeDefinition>
        {
            ["input"] = new(NodeTypes.Input, "1.0.0"),
            ["output"] = new(NodeTypes.Output, "1.0.0")
        }.ToImmutableSortedDictionary(StringComparer.Ordinal),
        new Dictionary<string, ConnectionDefinition>
        {
            ["data"] = new(new("input", "out"), new("output", "in"), "data")
        }.ToImmutableSortedDictionary(StringComparer.Ordinal))
    {
        ExecutionConnections = new Dictionary<string, ConnectionDefinition>
        {
            ["execution"] = new(new("input", "then"), new("output", "execute"), "execution")
        }.ToImmutableSortedDictionary(StringComparer.Ordinal)
    };

    private static WorkflowDefinition LegacyBlankGraph() => new(
        "1.0", "promotion-test", "input",
        new Dictionary<string, NodeDefinition>
        {
            ["input"] = new(NodeTypes.Input, "1.0.0"),
            ["output"] = new(NodeTypes.Output, "1.0.0")
        }.ToImmutableSortedDictionary(StringComparer.Ordinal),
        new Dictionary<string, ConnectionDefinition>
        {
            ["data"] = new(new("input", "out"), new("output", "in"), "data")
        }.ToImmutableSortedDictionary(StringComparer.Ordinal));
}
