using VisualAgentWorkflow.Core;
using Xunit;

namespace VisualAgentWorkflow.Core.Tests;

public sealed class WorkflowNodeContractTests
{
    [Fact]
    public void Catalog_covers_every_supported_node_family_once()
    {
        var contracts = WorkflowNodeContractCatalog.All;

        Assert.Equal(contracts.Length, contracts.Select(contract => contract.Type).Distinct(StringComparer.Ordinal).Count());
        Assert.All(contracts, contract =>
        {
            Assert.True(NodeTypes.IsSupported(contract.Type));
            Assert.Equal("1.0.0", contract.TypeVersion);
            Assert.False(string.IsNullOrWhiteSpace(contract.DisplayName));
            Assert.False(string.IsNullOrWhiteSpace(contract.Description));
        });

        var knownTypes = typeof(NodeTypes).GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .Where(field => field.FieldType == typeof(string))
            .Select(field => (string)field.GetValue(null)!)
            .ToHashSet(StringComparer.Ordinal);
        Assert.Equal(knownTypes, contracts.Select(contract => contract.Type).ToHashSet(StringComparer.Ordinal));
    }

    [Fact]
    public void Capability_nodes_are_marked_as_dynamic_and_host_authorized()
    {
        var agent = WorkflowNodeContractCatalog.Get(NodeTypes.Agent);
        var tool = WorkflowNodeContractCatalog.Get(NodeTypes.McpTool);

        Assert.True(agent.HasDynamicPins);
        Assert.True(agent.RequiresHostCapability);
        Assert.True(tool.HasDynamicPins);
        Assert.True(tool.RequiresHostCapability);
        Assert.False(tool.IsPure);
    }

    [Fact]
    public void Unknown_node_types_are_not_supported_or_resolvable()
    {
        Assert.False(NodeTypes.IsSupported("gameplay.actor.spawn"));
        Assert.False(WorkflowNodeContractCatalog.TryGet("gameplay.actor.spawn", out _));
        Assert.Throws<KeyNotFoundException>(() => WorkflowNodeContractCatalog.Get("gameplay.actor.spawn"));
    }
}
