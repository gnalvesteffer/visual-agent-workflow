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
    public void Catalog_effect_and_authoring_metadata_is_conformant()
    {
        Assert.Empty(WorkflowNodeContractConformance.ValidateCatalog());

        var pure = WorkflowNodeContractCatalog.Get(NodeTypes.StringConstant);
        var branch = WorkflowNodeContractCatalog.Get(NodeTypes.BooleanBranch);
        var approval = WorkflowNodeContractCatalog.Get(NodeTypes.Approval);
        var agent = WorkflowNodeContractCatalog.Get(NodeTypes.Agent);
        var tool = WorkflowNodeContractCatalog.Get(NodeTypes.McpTool);
        var call = WorkflowNodeContractCatalog.Get(NodeTypes.WorkflowCall);

        Assert.Equal(WorkflowNodeEffectFlags.None, pure.Effects);
        Assert.True(pure.Authoring.HasFlag(WorkflowNodeAuthoringFlags.InlineEditable));
        Assert.True(branch.Effects.HasFlag(WorkflowNodeEffectFlags.RunScopedState));
        Assert.True(approval.Effects.HasFlag(WorkflowNodeEffectFlags.HostInteraction));
        Assert.True(agent.Effects.HasFlag(WorkflowNodeEffectFlags.ExternalCapability));
        Assert.True(tool.Effects.HasFlag(WorkflowNodeEffectFlags.ExternalCapability));
        Assert.True(call.Effects.HasFlag(WorkflowNodeEffectFlags.ChildWorkflow));
        Assert.True(agent.Authoring.HasFlag(WorkflowNodeAuthoringFlags.DynamicPins));
    }

    [Fact]
    public void Catalog_lifecycle_metadata_is_conformant()
    {
        Assert.Empty(WorkflowNodeContractConformance.ValidateCatalog());

        Assert.Equal(WorkflowNodeActivationKind.ExternalApproval,
            WorkflowNodeContractCatalog.Get(NodeTypes.Approval).Activation);
        Assert.Equal(WorkflowNodeFailureKind.AbortsRegion,
            WorkflowNodeContractCatalog.Get(NodeTypes.ParallelJoin).Failure);
        Assert.Equal(WorkflowNodeEvidenceKind.CapabilityInvocation,
            WorkflowNodeContractCatalog.Get(NodeTypes.McpTool).Evidence);
    }

    [Fact]
    public void Catalog_lifecycle_drift_is_reported_without_changing_definition_contracts()
    {
        var original = WorkflowNodeContractCatalog.Get(NodeTypes.Approval);
        var drifted = original with { Activation = WorkflowNodeActivationKind.Execution };

        var issues = WorkflowNodeContractConformance.Validate(drifted);
        Assert.Contains(issues, issue => issue.Code == "metadata.approval-activation");
        Assert.Equal(WorkflowNodeActivationKind.ExternalApproval, original.Activation);
        Assert.Equal(WorkflowNodeActivationKind.Execution, drifted.Activation);
    }

    [Fact]
    public void Capability_projection_exposes_catalog_effects_and_authoring_capabilities()
    {
        var projection = WorkflowNodeCapabilityProjectionResolver.Resolve(new NodeDefinition(NodeTypes.Agent, "1.0.0"));

        Assert.True(projection.Effects.HasFlag(WorkflowNodeEffectFlags.ExternalCapability));
        Assert.True(projection.Authoring.HasFlag(WorkflowNodeAuthoringFlags.DynamicPins));
        Assert.True(projection.Authoring.HasFlag(WorkflowNodeAuthoringFlags.ContextInsertable));
    }

    [Fact]
    public void Contract_metadata_is_not_workflow_definition_data_or_hash_input()
    {
        var definition = new WorkflowDefinition(
            WorkflowSchemaVersions.ExplicitGraph, "metadata-boundary", "input",
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
        var before = WorkflowJson.ComputeHash(definition);

        _ = WorkflowNodeContractCatalog.Get(NodeTypes.Agent).Effects;

        Assert.Equal(before, WorkflowJson.ComputeHash(definition));
        Assert.DoesNotContain("ExternalCapability", WorkflowJson.Serialize(definition), StringComparison.Ordinal);
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
                descriptor.Ports.Where(port => port.Direction == WorkflowPortDirection.Input && port.Kind == WorkflowPortKind.Data).Select(port => (port.Id, port.DataType!)),
                WorkflowPorts.ExplicitDataInputs(node).Select(port => (port.Id, port.DataType)));
            Assert.Equal(
                descriptor.Ports.Where(port => port.Direction == WorkflowPortDirection.Output && port.Kind == WorkflowPortKind.Data).Select(port => (port.Id, port.DataType!)),
                WorkflowPorts.ExplicitDataOutputs(node).Select(port => (port.Id, port.DataType)));
        }

        Assert.False(WorkflowNodeDescriptorRegistry.TryGet(NodeTypes.StringConstant, "9.9.9", out _));
        Assert.Throws<KeyNotFoundException>(() => WorkflowNodeDescriptorRegistry.Get(NodeTypes.StringConstant, "9.9.9"));
    }

    [Fact]
    public void Descriptor_resolution_requires_the_node_implementation_version_and_never_falls_back()
    {
        var current = new NodeDefinition(NodeTypes.StringConstant, "1.0.0");
        var historical = current with { TypeVersion = "0.9.0" };
        var dynamicHistorical = new NodeDefinition(NodeTypes.Agent, "0.9.0", Agent: new("demo.scripted", null, "Use no tools.", []));

        Assert.True(WorkflowNodeDescriptorRegistry.TryGetForNode(current, out var descriptor));
        Assert.Equal(WorkflowNodeDescriptorRegistry.CurrentVersion, descriptor.DescriptorVersion);
        Assert.False(WorkflowNodeDescriptorRegistry.TryGetForNode(historical, out _));
        Assert.False(WorkflowNodeDescriptorResolver.TryResolve(historical, null, null, out _));
        Assert.False(WorkflowNodeDescriptorResolver.TryResolve(dynamicHistorical, null, null, out _));
        Assert.Empty(WorkflowPorts.ExplicitDataOutputs(historical));
        Assert.Empty(WorkflowPorts.ExplicitExecutionOutputs(dynamicHistorical));
        Assert.Contains(WorkflowValidator.Validate(new WorkflowDefinition("1.2", "unsupported-version", "input",
            new Dictionary<string, NodeDefinition>
            {
                ["input"] = new(NodeTypes.Input, "1.0.0"),
                ["constant"] = historical,
                ["output"] = new(NodeTypes.Output, "1.0.0")
            }.ToImmutableSortedDictionary(),
            new Dictionary<string, ConnectionDefinition>().ToImmutableSortedDictionary())),
            issue => issue.Code == "node.unsupported");
    }

    [Fact]
    public void Static_descriptor_registry_is_explicit_and_matches_independent_pin_facts()
    {
        var expected = new Dictionary<string, (string ConfigurationKind, (string Id, WorkflowPortDirection Direction, WorkflowPortKind Kind, string? DataType)[] Ports)>(StringComparer.Ordinal)
        {
            [NodeTypes.Uppercase] = ("uppercase", [("in", WorkflowPortDirection.Input, WorkflowPortKind.Data, WorkflowDataTypes.String), ("out", WorkflowPortDirection.Output, WorkflowPortKind.Data, WorkflowDataTypes.String)]),
            [NodeTypes.Reverse] = ("reverse", [("in", WorkflowPortDirection.Input, WorkflowPortKind.Data, WorkflowDataTypes.String), ("out", WorkflowPortDirection.Output, WorkflowPortKind.Data, WorkflowDataTypes.String)]),
            [NodeTypes.StringConstant] = ("string-constant", [("out", WorkflowPortDirection.Output, WorkflowPortKind.Data, WorkflowDataTypes.String)]),
            [NodeTypes.BooleanConstant] = ("boolean-constant", [("out", WorkflowPortDirection.Output, WorkflowPortKind.Data, WorkflowDataTypes.Boolean)]),
            [NodeTypes.IntegerConstant] = ("integer-constant", [("out", WorkflowPortDirection.Output, WorkflowPortKind.Data, WorkflowDataTypes.Integer)]),
            [NodeTypes.NumberConstant] = ("number-constant", [("out", WorkflowPortDirection.Output, WorkflowPortKind.Data, WorkflowDataTypes.Number)]),
            [NodeTypes.NumberAdd] = ("number-add", [("left", WorkflowPortDirection.Input, WorkflowPortKind.Data, WorkflowDataTypes.Number), ("right", WorkflowPortDirection.Input, WorkflowPortKind.Data, WorkflowDataTypes.Number), ("out", WorkflowPortDirection.Output, WorkflowPortKind.Data, WorkflowDataTypes.Number)]),
            [NodeTypes.StringToInteger] = ("string-to-integer", [("in", WorkflowPortDirection.Input, WorkflowPortKind.Data, WorkflowDataTypes.String), ("out", WorkflowPortDirection.Output, WorkflowPortKind.Data, WorkflowDataTypes.Integer)]),
            [NodeTypes.TextConcat] = ("text-concat", [("left", WorkflowPortDirection.Input, WorkflowPortKind.Data, WorkflowDataTypes.String), ("right", WorkflowPortDirection.Input, WorkflowPortKind.Data, WorkflowDataTypes.String), ("out", WorkflowPortDirection.Output, WorkflowPortKind.Data, WorkflowDataTypes.String)]),
            [NodeTypes.StringEquals] = ("string-equals", [("left", WorkflowPortDirection.Input, WorkflowPortKind.Data, WorkflowDataTypes.String), ("right", WorkflowPortDirection.Input, WorkflowPortKind.Data, WorkflowDataTypes.String), ("out", WorkflowPortDirection.Output, WorkflowPortKind.Data, WorkflowDataTypes.Boolean)]),
            [NodeTypes.IntegerAdd] = ("integer-add", [("left", WorkflowPortDirection.Input, WorkflowPortKind.Data, WorkflowDataTypes.Integer), ("right", WorkflowPortDirection.Input, WorkflowPortKind.Data, WorkflowDataTypes.Integer), ("out", WorkflowPortDirection.Output, WorkflowPortKind.Data, WorkflowDataTypes.Integer)]),
            [NodeTypes.IntegerEquals] = ("integer-equals", [("left", WorkflowPortDirection.Input, WorkflowPortKind.Data, WorkflowDataTypes.Integer), ("right", WorkflowPortDirection.Input, WorkflowPortKind.Data, WorkflowDataTypes.Integer), ("out", WorkflowPortDirection.Output, WorkflowPortKind.Data, WorkflowDataTypes.Boolean)]),
            [NodeTypes.BooleanNot] = ("boolean-not", [("value", WorkflowPortDirection.Input, WorkflowPortKind.Data, WorkflowDataTypes.Boolean), ("out", WorkflowPortDirection.Output, WorkflowPortKind.Data, WorkflowDataTypes.Boolean)]),
            [NodeTypes.BooleanBranch] = ("boolean-branch", [("condition", WorkflowPortDirection.Input, WorkflowPortKind.Data, WorkflowDataTypes.Boolean), ("execute", WorkflowPortDirection.Input, WorkflowPortKind.Execution, null), ("true", WorkflowPortDirection.Output, WorkflowPortKind.Execution, null), ("false", WorkflowPortDirection.Output, WorkflowPortKind.Execution, null)])
        };

        Assert.Empty(WorkflowNodeDescriptorConformance.ValidateRegistry());
        Assert.Equal(expected.Keys, WorkflowNodeDescriptorRegistry.All.Select(descriptor => descriptor.NodeType));
        Assert.All(WorkflowNodeDescriptorRegistry.All, descriptor =>
        {
            var facts = expected[descriptor.NodeType];
            Assert.Equal(facts.ConfigurationKind, descriptor.ConfigurationKind);
            Assert.Equal(facts.Ports, descriptor.Ports.Select(port => (port.Id, port.Direction, port.Kind, port.DataType)).ToArray());
            Assert.False(descriptor.NodeContract.HasDynamicPins);
        });
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
    public void Boolean_branch_uses_one_canonical_static_descriptor_for_typed_graph_pins()
    {
        var node = new NodeDefinition(NodeTypes.BooleanBranch, "1.0.0");
        var descriptor = WorkflowNodeDescriptorRegistry.Get(NodeTypes.BooleanBranch);

        Assert.Empty(WorkflowNodeDescriptorConformance.Validate(descriptor));
        Assert.Equal(
            [("condition", WorkflowPortDirection.Input, WorkflowPortKind.Data, WorkflowDataTypes.Boolean),
             ("execute", WorkflowPortDirection.Input, WorkflowPortKind.Execution, (string?)null),
             ("true", WorkflowPortDirection.Output, WorkflowPortKind.Execution, (string?)null),
             ("false", WorkflowPortDirection.Output, WorkflowPortKind.Execution, (string?)null)],
            WorkflowPorts.Explicit(node).Select(port => (port.Id, port.Direction, port.Kind, port.DataType)));
        Assert.Equal([("condition", WorkflowDataTypes.Boolean)], WorkflowPorts.ExplicitDataInputs(node).Select(port => (port.Id, port.DataType)));
        Assert.Empty(WorkflowPorts.ExplicitDataOutputs(node));
        Assert.Equal(["execute"], WorkflowPorts.ExplicitExecutionInputs(node).ToArray());
        Assert.Equal(["true", "false"], WorkflowPorts.ExplicitExecutionOutputs(node).ToArray());
    }

    [Fact]
    public void String_integer_conversion_uses_the_canonical_static_descriptor()
    {
        var node = new NodeDefinition(NodeTypes.StringToInteger, "1.0.0");

        var descriptor = WorkflowNodeDescriptorRegistry.Get(NodeTypes.StringToInteger);

        Assert.Equal("string-to-integer", descriptor.ConfigurationKind);
        Assert.Empty(WorkflowNodeDescriptorConformance.Validate(descriptor));
        Assert.Equal(
            [("in", WorkflowPortDirection.Input, WorkflowPortKind.Data, WorkflowDataTypes.String),
             ("out", WorkflowPortDirection.Output, WorkflowPortKind.Data, WorkflowDataTypes.Integer)],
            WorkflowPorts.Explicit(node).Select(port => (port.Id, port.Direction, port.Kind, port.DataType)));
    }

    [Theory]
    [InlineData(NodeTypes.Uppercase, "uppercase")]
    [InlineData(NodeTypes.Reverse, "reverse")]
    public void Static_text_transformations_use_canonical_data_descriptors(string nodeType, string configurationKind)
    {
        var descriptor = WorkflowNodeDescriptorRegistry.Get(nodeType);

        Assert.Equal(configurationKind, descriptor.ConfigurationKind);
        Assert.Empty(WorkflowNodeDescriptorConformance.Validate(descriptor));
        Assert.Equal(
            [("in", WorkflowPortDirection.Input, WorkflowPortKind.Data, WorkflowDataTypes.String),
             ("out", WorkflowPortDirection.Output, WorkflowPortKind.Data, WorkflowDataTypes.String)],
            WorkflowPorts.Explicit(new NodeDefinition(nodeType, "1.0.0"))
                .Select(port => (port.Id, port.Direction, port.Kind, port.DataType)));
    }

    [Fact]
    public void Resolved_dynamic_descriptors_validate_pin_metadata_without_live_discovery()
    {
        var agent = new NodeDefinition(NodeTypes.Agent, "1.0.0");
        var tool = new NodeDefinition(NodeTypes.McpTool, "1.0.0")
        {
            Tool = new ToolBindingRequest("demo.search", new string('a', 64)),
            McpInputPorts = [new("arg.query", "query", WorkflowDataTypes.String, true)]
        };
        var sequence = new NodeDefinition(NodeTypes.Sequence, "1.0.0")
        {
            Sequence = new SequenceConfiguration([new("first", "First"), new("second", "Second")])
        };

        Assert.Empty(WorkflowNodeDescriptorConformance.ValidateResolved(agent));
        Assert.Empty(WorkflowNodeDescriptorConformance.ValidateResolved(tool));
        Assert.Empty(WorkflowNodeDescriptorConformance.ValidateResolved(sequence));
        Assert.Empty(WorkflowNodeDescriptorConformance.ValidateResolved(
            new NodeDefinition(NodeTypes.McpTool, "1.0.0")
            {
                Tool = new ToolBindingRequest("demo.search", new string('a', 64))
            },
            [new("arg.query", "query", WorkflowDataTypes.String, true)]));
    }

    [Fact]
    public void Descriptor_resolver_covers_every_canonical_node_family_with_default_configuration()
    {
        foreach (var contract in WorkflowNodeContractCatalog.All)
        {
            var node = new NodeDefinition(contract.Type, contract.TypeVersion);
            var issues = WorkflowNodeDescriptorConformance.ValidateResolved(node);

            Assert.True(issues.IsEmpty,
                $"{contract.Type} descriptor issues: {string.Join("; ", issues.Select(issue => issue.Code + ": " + issue.Message))}");
            Assert.True(WorkflowNodeDescriptorResolver.TryResolve(node, null, null, out var descriptor));
            Assert.Equal(contract.Type, descriptor.NodeType);
            Assert.Equal(WorkflowNodeDescriptorRegistry.CurrentVersion, descriptor.DescriptorVersion);
        }
    }

    [Fact]
    public void Advisory_descriptor_audit_covers_every_canonical_node_family()
    {
        Assert.Empty(WorkflowNodeDescriptorAudit.AuditCatalog());
        Assert.Empty(WorkflowNodeDescriptorAudit.AuditNodeFamilies());
    }

    [Fact]
    public void Advisory_descriptor_audit_reports_port_projection_drift_without_mutating_workflow_data()
    {
        var node = new NodeDefinition(NodeTypes.StringConstant, "1.0.0");
        var descriptor = WorkflowNodeDescriptorRegistry.Get(NodeTypes.StringConstant) with
        {
            Ports = [new("wrong", WorkflowPortDirection.Output, WorkflowPortKind.Data, WorkflowDataTypes.String)]
        };

        var issues = WorkflowNodeDescriptorAudit.Audit(node, descriptor);

        Assert.Contains(issues, issue => issue.Code == "ports.data-outputs" && issue.Authority == "port-projection");
        Assert.Contains(issues, issue => issue.Code == "projection.descriptor" && issue.Authority == "capability-projection");
        Assert.Equal("wrong", descriptor.Ports.Single().Id);
        Assert.Equal("out", WorkflowPorts.ExplicitDataOutputs(node).Single().Id);
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
    public void Validator_uses_the_canonical_contract_version_for_node_identity()
    {
        var definition = new WorkflowDefinition(
            "1.0", "contract-version", "input",
            new Dictionary<string, NodeDefinition>
            {
                ["input"] = new(NodeTypes.Input, "1.0.0"),
                ["output"] = new(NodeTypes.Output, "9.9.9")
            }.ToImmutableSortedDictionary(StringComparer.Ordinal),
            new Dictionary<string, ConnectionDefinition>
            {
                ["value"] = new(new("input", "out"), new("output", "in"), "data")
            }.ToImmutableSortedDictionary(StringComparer.Ordinal));

        var issues = WorkflowValidator.Validate(definition);

        Assert.Contains(issues, issue => issue.Code == "node.unsupported" && issue.Path == "nodes.output");
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
    public void Variable_descriptors_project_typed_execution_and_data_pins()
    {
        var get = new NodeDefinition(NodeTypes.VariableGet, "1.0.0")
        {
            VariableId = "research-count",
            OutputDataType = WorkflowDataTypes.Integer
        };
        var set = new NodeDefinition(NodeTypes.VariableSet, "1.0.0")
        {
            VariableId = "research-count",
            InputDataType = WorkflowDataTypes.Integer
        };

        var getDescriptor = WorkflowNodeDescriptorResolver.Resolve(get);
        var setDescriptor = WorkflowNodeDescriptorResolver.Resolve(set);

        Assert.Equal("variable-get", getDescriptor.ConfigurationKind);
        Assert.Equal("variable-set", setDescriptor.ConfigurationKind);
        Assert.Equal(
            [("out", WorkflowPortDirection.Output, WorkflowPortKind.Data, WorkflowDataTypes.Integer),
             ("execute", WorkflowPortDirection.Input, WorkflowPortKind.Execution, (string?)null),
             ("then", WorkflowPortDirection.Output, WorkflowPortKind.Execution, (string?)null)],
            getDescriptor.Ports.Select(port => (port.Id, port.Direction, port.Kind, port.DataType)));
        Assert.Equal(
            [("value", WorkflowPortDirection.Input, WorkflowPortKind.Data, WorkflowDataTypes.Integer),
             ("execute", WorkflowPortDirection.Input, WorkflowPortKind.Execution, (string?)null),
             ("then", WorkflowPortDirection.Output, WorkflowPortKind.Execution, (string?)null)],
            setDescriptor.Ports.Select(port => (port.Id, port.Direction, port.Kind, port.DataType)));
        Assert.Empty(WorkflowNodeDescriptorConformance.ValidateResolved(get));
        Assert.Empty(WorkflowNodeDescriptorConformance.ValidateResolved(set));
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
    public void Persisted_mcp_argument_descriptor_preserves_pin_and_property_identity()
    {
        var node = new NodeDefinition(NodeTypes.McpTool, "1.0.0")
        {
            McpInputPorts = [new("arg.path", "path", WorkflowDataTypes.String, true),
                new("arg.maxBytes", "maxBytes", WorkflowDataTypes.Integer, false)]
        };

        Assert.True(WorkflowNodeDescriptorResolver.TryResolveMcpArguments(node, out var arguments));
        Assert.Equal(
            [("arg.path", "path", WorkflowDataTypes.String, true),
             ("arg.maxBytes", "maxBytes", WorkflowDataTypes.Integer, false)],
            arguments.Select(argument => (argument.Port.Id, argument.Argument.Name,
                argument.Port.DataType!, argument.Port.Required)));
    }

    [Fact]
    public void Persisted_mcp_argument_descriptor_fails_closed_on_duplicate_pin_identity()
    {
        var node = new NodeDefinition(NodeTypes.McpTool, "1.0.0")
        {
            McpInputPorts = [new("arg.path", "path", WorkflowDataTypes.String, true),
                new("arg.path", "other", WorkflowDataTypes.String, false)]
        };

        Assert.False(WorkflowNodeDescriptorResolver.TryResolveMcpArguments(node, out _));
    }

    [Fact]
    public void Unknown_dynamic_descriptor_fails_closed()
    {
        Assert.False(WorkflowNodeDescriptorResolver.TryResolve(new NodeDefinition("unknown.node", "1.0.0"), null, null, out _));
        Assert.Throws<KeyNotFoundException>(() => WorkflowNodeDescriptorResolver.Resolve(new NodeDefinition("unknown.node", "1.0.0")));
    }

    [Fact]
    public void Capability_projection_composes_static_and_configured_contracts_without_authority()
    {
        var pure = WorkflowNodeCapabilityProjectionResolver.Resolve(new NodeDefinition(NodeTypes.BooleanBranch, "1.0.0"));
        var agent = new NodeDefinition(NodeTypes.Agent, "1.0.0",
            Agent: new AgentConfiguration("provider", null, "instructions", [])
            {
                Routes = [new("review", "Review", "Review path")]
            });
        var sequence = new NodeDefinition(NodeTypes.Sequence, "1.0.0")
        {
            Sequence = new([new("first", "First"), new("second", "Second")])
        };
        var configured = WorkflowNodeCapabilityProjectionResolver.Resolve(agent);
        var ordered = WorkflowNodeCapabilityProjectionResolver.Resolve(sequence);

        Assert.Equal(WorkflowNodeCapabilityResolutionKind.Static, pure.ResolutionKind);
        Assert.Equal(NodeTypes.BooleanBranch, pure.NodeType);
        Assert.Contains(pure.Ports, port => port.Id == "true" && port.Kind == WorkflowPortKind.Execution);
        Assert.Equal(WorkflowNodeCapabilityResolutionKind.Configured, configured.ResolutionKind);
        Assert.True(configured.RequiresHostCapability);
        Assert.Contains(configured.Ports, port => port.Id == "route.review");
        Assert.Equal(["execute", "then.first", "then.second"], ordered.Ports.Select(port => port.Id));
        Assert.Contains(ordered.Limits, limit => limit.Id == "output-count");
    }

    [Fact]
    public void Capability_projection_marks_transient_mcp_discovery_without_replacing_persisted_pins_or_hashes()
    {
        var tool = new NodeDefinition(NodeTypes.McpTool, "1.0.0")
        {
            Tool = new ToolBindingRequest("demo.search", new string('a', 64)),
            McpInputPorts = [new("arg.query", "query", WorkflowDataTypes.String, false)]
        };
        var discovered = ImmutableArray.Create(new McpInputPortDefinition("arg.path", "path", WorkflowDataTypes.String, true));
        var descriptions = new Dictionary<string, string> { ["arg.path"] = "A repository path." };
        var definition = new WorkflowDefinition(
            WorkflowSchemaVersions.ExplicitGraph, "projection", "input",
            new Dictionary<string, NodeDefinition>
            {
                ["input"] = new(NodeTypes.Input, "1.0.0"),
                ["output"] = new(NodeTypes.Output, "1.0.0")
            }
                .ToImmutableSortedDictionary(StringComparer.Ordinal),
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
        var hash = WorkflowJson.ComputeHash(definition);

        var projection = WorkflowNodeCapabilityProjectionResolver.Resolve(tool,
            discoveredMcpInputPorts: discovered, discoveredMcpInputDescriptions: descriptions);

        Assert.Equal(WorkflowNodeCapabilityResolutionKind.DiscoveryEnriched, projection.ResolutionKind);
        Assert.True(projection.RequiresHostCapability);
        Assert.Equal("arg.query", Assert.Single(projection.Ports.Where(port =>
            port.Direction == WorkflowPortDirection.Input && port.Kind == WorkflowPortKind.Data)).Id);
        Assert.Equal(hash, WorkflowJson.ComputeHash(definition));
        Assert.Equal("arg.query", tool.McpInputPorts!.Value.Single().Id);
        Assert.Equal("A repository path.", WorkflowNodeDescriptorResolver.Resolve(
            tool with { McpInputPorts = null }, discovered, descriptions).Ports.Single(port => port.Id == "arg.path").Description);
    }

    [Fact]
    public void Capability_projection_rejects_unknown_projection_versions_without_fallback()
    {
        var node = new NodeDefinition(NodeTypes.StringConstant, "1.0.0");

        Assert.False(WorkflowNodeCapabilityProjectionResolver.TryResolve(node, out _, "9.9.9"));
        Assert.Throws<KeyNotFoundException>(() => WorkflowNodeCapabilityProjectionResolver.Resolve(node, "9.9.9"));
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

    [Fact]
    public void Function_call_data_ports_are_consistent_across_legacy_and_explicit_port_apis()
    {
        var function = new GraphFunctionDefinition("typed.fn", 1, "Typed function", "",
            new Dictionary<string, GraphFunctionPort>
            {
                ["count"] = new("Count", WorkflowDataTypes.Integer),
                ["query"] = new("Query", WorkflowDataTypes.String)
            }.ToImmutableSortedDictionary(StringComparer.Ordinal),
            new Dictionary<string, GraphFunctionPort>
            {
                ["answer"] = new("Answer", WorkflowDataTypes.String),
                ["score"] = new("Score", WorkflowDataTypes.Number)
            }.ToImmutableSortedDictionary(StringComparer.Ordinal),
            ImmutableSortedDictionary<string, NodeDefinition>.Empty,
            ImmutableSortedDictionary<string, ConnectionDefinition>.Empty);
        var call = new NodeDefinition(NodeTypes.FunctionCall, "1.0.0")
        {
            GraphFunction = new(function, GraphFunctionJson.ComputeHash(function))
        };

        var expectedInputs = new[]
        {
            new WorkflowPort("count", WorkflowDataTypes.Integer),
            new WorkflowPort("query", WorkflowDataTypes.String)
        };
        var expectedOutputs = new[]
        {
            new WorkflowPort("answer", WorkflowDataTypes.String),
            new WorkflowPort("score", WorkflowDataTypes.Number)
        };

        Assert.Equal(expectedInputs, WorkflowPorts.Inputs(call));
        Assert.Equal(expectedInputs, WorkflowPorts.ExplicitDataInputs(call));
        Assert.Equal(expectedOutputs, WorkflowPorts.Outputs(call));
        Assert.Equal(expectedOutputs, WorkflowPorts.ExplicitDataOutputs(call));
        Assert.Equal(expectedInputs.Select(port => (port.Id, port.DataType)),
            WorkflowPorts.Explicit(call).Where(port => port.Direction == WorkflowPortDirection.Input)
                .Select(port => (port.Id, port.DataType!)));
        Assert.Equal(expectedOutputs.Select(port => (port.Id, port.DataType)),
            WorkflowPorts.Explicit(call).Where(port => port.Direction == WorkflowPortDirection.Output)
                .Select(port => (port.Id, port.DataType!)));
    }

    [Fact]
    public void Function_call_without_valid_configuration_does_not_receive_generic_fallback_ports()
    {
        var missing = new NodeDefinition(NodeTypes.FunctionCall, "1.0.0");
        var unsupported = missing with { TypeVersion = "0.0.1" };

        Assert.Empty(WorkflowPorts.Inputs(missing));
        Assert.Empty(WorkflowPorts.Outputs(missing));
        Assert.Empty(WorkflowPorts.Inputs(unsupported));
        Assert.Empty(WorkflowPorts.Outputs(unsupported));
    }

    [Fact]
    public void Dynamic_port_projection_ignores_malformed_null_members_without_losing_valid_ordered_pins()
    {
        var agent = new NodeDefinition(NodeTypes.Agent, "1.0.0",
            Agent: new AgentConfiguration("provider", null, "instructions", [])
            {
                Routes = [null!, new("review", "Review", "Review path")]
            });
        var sequence = new NodeDefinition(NodeTypes.Sequence, "1.0.0")
        {
            Sequence = new([null!, new("first", "First")])
        };
        var fork = new NodeDefinition(NodeTypes.ParallelFork, "1.0.0")
        {
            ParallelFork = new([null!, new("branch", "Branch", "anchor")])
        };
        var tool = new NodeDefinition(NodeTypes.McpTool, "1.0.0")
        {
            McpInputPorts = [null!, new("arg.path", "path", WorkflowDataTypes.String, true)]
        };

        Assert.Equal(["execute", "route.review"], WorkflowNodeDescriptorResolver.Resolve(agent).Ports
            .Where(port => port.Kind == WorkflowPortKind.Execution).Select(port => port.Id));
        Assert.Equal(["execute", "then.first"], WorkflowNodeDescriptorResolver.Resolve(sequence).Ports.Select(port => port.Id));
        Assert.Equal(["execute", "branch.branch"], WorkflowNodeDescriptorResolver.Resolve(fork).Ports.Select(port => port.Id));
        Assert.Equal("arg.path", Assert.Single(WorkflowNodeDescriptorResolver.Resolve(tool).Ports
            .Where(port => port.Direction == WorkflowPortDirection.Input && port.Kind == WorkflowPortKind.Data)).Id);
        Assert.Equal("route.review", Assert.Single(WorkflowPorts.ExplicitExecutionOutputs(agent)));
    }

    [Fact]
    public void Malformed_dynamic_nodes_remain_validator_inputs_instead_of_throwing()
    {
        var definition = new WorkflowDefinition(
            WorkflowSchemaVersions.AgentExecutionRoutes, "malformed-dynamic", "input",
            new Dictionary<string, NodeDefinition>
            {
                ["input"] = new(NodeTypes.Input, "1.0.0"),
                ["agent"] = new(NodeTypes.Agent, "1.0.0",
                    Agent: new AgentConfiguration("provider", null, "instructions", []) { Routes = [null!] }),
                ["output"] = new(NodeTypes.Output, "1.0.0")
            }.ToImmutableSortedDictionary(StringComparer.Ordinal),
            new Dictionary<string, ConnectionDefinition>().ToImmutableSortedDictionary(StringComparer.Ordinal))
        {
            ExecutionConnections = new Dictionary<string, ConnectionDefinition>().ToImmutableSortedDictionary(StringComparer.Ordinal)
        };

        var exception = Record.Exception(() => WorkflowValidator.Validate(definition));

        Assert.Null(exception);
        Assert.Contains(WorkflowValidator.Validate(definition), issue => issue.Code == "agent.routes");
    }
}
