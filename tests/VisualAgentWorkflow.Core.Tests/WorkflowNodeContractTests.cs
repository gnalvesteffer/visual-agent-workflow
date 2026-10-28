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
        Assert.Equal(WorkflowNodeEvidenceKind.CapabilityInvocation, agent.Evidence);
        Assert.Equal(WorkflowNodeStateKind.HostOwned, tool.State);
        Assert.Equal(WorkflowDynamicPinIdentityKind.AgentRouteId, agent.DynamicPinIdentity);
        Assert.Equal(WorkflowDynamicPinIdentityKind.McpArgumentId, tool.DynamicPinIdentity);
    }

    [Fact]
    public void Dynamic_pin_identity_distinguishes_authored_branch_and_function_contracts()
    {
        Assert.Equal(WorkflowDynamicPinIdentityKind.ParallelBranchId,
            WorkflowNodeContractCatalog.Get(NodeTypes.ParallelJoin).DynamicPinIdentity);
        Assert.Equal(WorkflowDynamicPinIdentityKind.ParallelBranchId,
            WorkflowNodeContractCatalog.Get(NodeTypes.ParallelRendezvous).DynamicPinIdentity);
        Assert.Equal(WorkflowDynamicPinIdentityKind.GraphFunctionPortId,
            WorkflowNodeContractCatalog.Get(NodeTypes.FunctionCall).DynamicPinIdentity);
        Assert.Equal(WorkflowDynamicPinIdentityKind.None,
            WorkflowNodeContractCatalog.Get(NodeTypes.BooleanBranch).DynamicPinIdentity);
    }

    [Fact]
    public void Contracts_report_existing_runtime_limits_without_inventing_new_bounds()
    {
        var agent = WorkflowNodeContractCatalog.Get(NodeTypes.Agent);
        var loop = WorkflowNodeContractCatalog.Get(NodeTypes.Loop);
        var call = WorkflowNodeContractCatalog.Get(NodeTypes.WorkflowCall);

        Assert.Contains(agent.Limits, limit => limit.Id == "route-count" && limit.Maximum == AgentExecutionRouteLimits.MaximumRoutes);
        Assert.Contains(agent.Limits, limit => limit.Id == "output-tokens" && limit.Maximum == AgentOutputTokenLimits.Maximum);
        Assert.Contains(loop.Limits, limit => limit.Id == "iteration-count" && limit.Maximum == WorkflowLoopLimits.MaximumIterations);
        Assert.Contains(call.Limits, limit => limit.Id == "expanded-nodes" && limit.Maximum == WorkflowCallLimits.MaximumExpandedNodes);
        Assert.Empty(WorkflowNodeContractCatalog.Get(NodeTypes.BooleanBranch).Limits);
    }

    [Fact]
    public void Descriptor_semantics_distinguish_pure_data_from_control_and_barriers()
    {
        var constant = WorkflowNodeContractCatalog.Get(NodeTypes.StringConstant);
        var branch = WorkflowNodeContractCatalog.Get(NodeTypes.BooleanBranch);
        var join = WorkflowNodeContractCatalog.Get(NodeTypes.ParallelJoin);

        Assert.Equal(WorkflowNodeActivationKind.DataEvaluation, constant.Activation);
        Assert.Equal(WorkflowNodeStateKind.Stateless, constant.State);
        Assert.Equal(WorkflowNodeFailureKind.Routes, branch.Failure);
        Assert.Equal(WorkflowNodeActivationKind.SchedulerBarrier, join.Activation);
        Assert.Equal(WorkflowNodeFailureKind.AbortsRegion, join.Failure);
    }

    [Fact]
    public void Unknown_node_types_are_not_supported_or_resolvable()
    {
        Assert.False(NodeTypes.IsSupported("gameplay.actor.spawn"));
        Assert.False(WorkflowNodeContractCatalog.TryGet("gameplay.actor.spawn", out _));
        Assert.Throws<KeyNotFoundException>(() => WorkflowNodeContractCatalog.Get("gameplay.actor.spawn"));
    }

    [Fact]
    public void Static_pure_descriptors_are_unique_versioned_and_match_port_resolution()
    {
        var descriptors = WorkflowNodeDescriptorRegistry.All;

        Assert.Equal(descriptors.Length, descriptors.Select(descriptor => (descriptor.NodeType, descriptor.DescriptorVersion)).Distinct().Count());
        Assert.All(descriptors, descriptor =>
        {
            Assert.Equal(WorkflowNodeDescriptorRegistry.CurrentVersion, descriptor.DescriptorVersion);
            Assert.Equal(descriptor.NodeType, descriptor.NodeContract.Type);
            Assert.False(string.IsNullOrWhiteSpace(descriptor.ConfigurationKind));
            Assert.NotEmpty(descriptor.Ports);
            Assert.True(WorkflowNodeDescriptorRegistry.TryGet(descriptor.NodeType, descriptor.DescriptorVersion, out _));
        });

        foreach (var descriptor in descriptors)
        {
            var node = new NodeDefinition(descriptor.NodeType, "1.0.0");
            Assert.Equal(descriptor.Ports, WorkflowPorts.Explicit(node));
            Assert.Equal(
                descriptor.Ports.Where(port => port.Direction == WorkflowPortDirection.Input).Select(port => (port.Id, port.DataType!)),
                WorkflowPorts.ExplicitDataInputs(node).Select(port => (port.Id, port.DataType)));
            Assert.Equal(
                descriptor.Ports.Where(port => port.Direction == WorkflowPortDirection.Output).Select(port => (port.Id, port.DataType!)),
                WorkflowPorts.ExplicitDataOutputs(node).Select(port => (port.Id, port.DataType)));
        }

        Assert.False(WorkflowNodeDescriptorRegistry.TryGet(NodeTypes.StringConstant, "9.9.9", out _));
        Assert.Throws<KeyNotFoundException>(() => WorkflowNodeDescriptorRegistry.Get(NodeTypes.StringConstant, "9.9.9"));
    }
}
