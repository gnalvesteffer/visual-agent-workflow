using System.Collections.Immutable;
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

    [Fact]
    public void Static_descriptor_conformance_checks_port_and_contract_invariants()
    {
        var valid = WorkflowNodeDescriptorRegistry.Get(NodeTypes.StringConstant);
        Assert.Empty(WorkflowNodeDescriptorConformance.Validate(valid));

        var invalid = valid with
        {
            DescriptorVersion = "9.9.9",
            ConfigurationKind = "",
            Ports =
            [
                new("out", WorkflowPortDirection.Output, WorkflowPortKind.Data, "not-a-type"),
                new("out", WorkflowPortDirection.Output, WorkflowPortKind.Execution, WorkflowDataTypes.String)
            ]
        };

        var issues = WorkflowNodeDescriptorConformance.Validate(invalid);

        Assert.Contains(issues, issue => issue.Code == "version");
        Assert.Contains(issues, issue => issue.Code == "configuration-kind");
        Assert.Contains(issues, issue => issue.Code == "port-id");
        Assert.Contains(issues, issue => issue.Code == "data-type");
        Assert.Contains(issues, issue => issue.Code == "execution-type");
    }

    [Fact]
    public void Validator_admits_valid_static_descriptors_without_descriptor_boundary_issues()
    {
        var definition = new WorkflowDefinition(
            "1.43", "descriptor-validation", "input",
            new Dictionary<string, NodeDefinition>
            {
                ["input"] = new(NodeTypes.Input, "1.0.0"),
                ["constant"] = new(NodeTypes.StringConstant, "1.0.0")
            }.ToImmutableSortedDictionary(StringComparer.Ordinal),
            new Dictionary<string, ConnectionDefinition>().ToImmutableSortedDictionary(StringComparer.Ordinal))
        {
            ExecutionConnections = new Dictionary<string, ConnectionDefinition>().ToImmutableSortedDictionary(StringComparer.Ordinal)
        };

        var issues = WorkflowValidator.Validate(definition);

        Assert.DoesNotContain(issues, issue => issue.Code == "node.descriptor");
        Assert.NotEmpty(issues);
    }

    [Fact]
    public void Dynamic_descriptor_resolution_preserves_authored_routes_and_sequence_order()
    {
        var agent = new NodeDefinition(NodeTypes.Agent, "1.0.0",
            Agent: new AgentConfiguration("provider", null, "instructions", [])
            {
                Routes = [new("review", "Review", "Review path"), new("publish", "Publish", "Publish path")]
            });
        var sequence = new NodeDefinition(NodeTypes.Sequence, "1.0.0")
        {
            Sequence = new([new("first", "First"), new("second", "Second")])
        };

        var agentPorts = WorkflowNodeDescriptorResolver.Resolve(agent).Ports;
        var sequencePorts = WorkflowNodeDescriptorResolver.Resolve(sequence).Ports;

        Assert.Equal(["out", "execute", "route.review", "route.publish"], agentPorts.Select(port => port.Id));
        Assert.Equal(["Review", "Publish"], agentPorts.Where(port => port.Kind == WorkflowPortKind.Execution &&
            port.Direction == WorkflowPortDirection.Output).Select(port => port.DisplayName));
        Assert.Equal(["execute", "then.first", "then.second"], sequencePorts.Select(port => port.Id));
        Assert.Equal(["First", "Second"], sequencePorts.Where(port => port.Direction == WorkflowPortDirection.Output)
            .Select(port => port.DisplayName));
        Assert.Equal(agentPorts.Select(port => (port.Id, port.Direction, port.Kind, port.DataType, port.DisplayName,
            port.Description, port.Required, port.Cardinality, port.SupportsStaticValue)),
            WorkflowPorts.Explicit(agent).Select(port => (port.Id, port.Direction, port.Kind, port.DataType, port.DisplayName,
                port.Description, port.Required, port.Cardinality, port.SupportsStaticValue)));
        Assert.Equal(sequencePorts.Select(port => (port.Id, port.Direction, port.Kind, port.DataType, port.DisplayName,
            port.Description, port.Required, port.Cardinality, port.SupportsStaticValue)),
            WorkflowPorts.Explicit(sequence).Select(port => (port.Id, port.Direction, port.Kind, port.DataType, port.DisplayName,
                port.Description, port.Required, port.Cardinality, port.SupportsStaticValue)));
    }

    [Fact]
    public void Dynamic_mcp_descriptor_uses_discovery_only_when_persisted_pins_are_absent()
    {
        var tool = new NodeDefinition(NodeTypes.McpTool, "1.0.0");
        var discovered = ImmutableArray.Create(new McpInputPortDefinition("arg.path", "path", "string", true));
        var descriptions = new Dictionary<string, string> { ["arg.path"] = "A repository path." };

        var before = tool;
        var resolved = WorkflowNodeDescriptorResolver.Resolve(tool, discovered, descriptions);
        var persisted = tool with { McpInputPorts = [new("arg.query", "query", "string", false)] };
        var persistedResolved = WorkflowNodeDescriptorResolver.Resolve(persisted, discovered, descriptions);
        var explicitlyEmpty = tool with { McpInputPorts = [] };

        var input = Assert.Single(resolved.Ports.Where(port => port.Direction == WorkflowPortDirection.Input &&
            port.Kind == WorkflowPortKind.Data));
        Assert.Equal("arg.path", input.Id);
        Assert.Equal("A repository path.", input.Description);
        Assert.True(input.Required);
        Assert.Equal(WorkflowPortCardinality.AtMostOne, input.Cardinality);
        Assert.True(input.SupportsStaticValue);
        Assert.Equal("arg.query", Assert.Single(persistedResolved.Ports.Where(port => port.Direction == WorkflowPortDirection.Input &&
            port.Kind == WorkflowPortKind.Data)).Id);
        Assert.Empty(WorkflowNodeDescriptorResolver.Resolve(explicitlyEmpty, discovered).Ports.Where(
            port => port.Direction == WorkflowPortDirection.Input && port.Kind == WorkflowPortKind.Data));
        Assert.Equal(before, tool);
    }

    [Fact]
    public void Unknown_dynamic_descriptor_fails_closed()
    {
        Assert.False(WorkflowNodeDescriptorResolver.TryResolve(new NodeDefinition("unknown.node", "1.0.0"), null, null, out _));
        Assert.Throws<KeyNotFoundException>(() => WorkflowNodeDescriptorResolver.Resolve(new NodeDefinition("unknown.node", "1.0.0")));
    }

    [Fact]
    public void Dynamic_descriptor_resolution_preserves_parallel_and_function_pin_contracts()
    {
        var fork = new NodeDefinition(NodeTypes.ParallelFork, "1.0.0")
        {
            ParallelFork = new([
                new("alpha", "Alpha", "anchor-alpha"),
                new("beta", "Beta", "anchor-beta")])
        };
        var join = new NodeDefinition(NodeTypes.ParallelJoin, "1.0.0")
        {
            ParallelJoin = new("fork", ["alpha", "beta"])
        };
        var function = new GraphFunctionDefinition("text.fn", 1, "Text function", "",
            new Dictionary<string, GraphFunctionPort> { ["input"] = new("Input", WorkflowDataTypes.String) }
                .ToImmutableSortedDictionary(StringComparer.Ordinal),
            new Dictionary<string, GraphFunctionPort> { ["output"] = new("Output", WorkflowDataTypes.String) }
                .ToImmutableSortedDictionary(StringComparer.Ordinal),
            ImmutableSortedDictionary<string, NodeDefinition>.Empty,
            ImmutableSortedDictionary<string, ConnectionDefinition>.Empty);
        var call = new NodeDefinition(NodeTypes.FunctionCall, "1.0.0")
        {
            GraphFunction = new(function, "hash")
        };

        Assert.Equal(["execute", "branch.alpha", "branch.beta"],
            WorkflowNodeDescriptorResolver.Resolve(fork).Ports.Select(port => port.Id));
        Assert.Equal(["out", "branch.alpha", "branch.beta", "then"],
            WorkflowNodeDescriptorResolver.Resolve(join).Ports.Select(port => port.Id));
        Assert.Equal(["input", "output"],
            WorkflowNodeDescriptorResolver.Resolve(call).Ports.Select(port => port.Id));
        Assert.Equal([WorkflowDataTypes.String, WorkflowDataTypes.String],
            WorkflowNodeDescriptorResolver.Resolve(call).Ports.Select(port => port.DataType));
    }
}
