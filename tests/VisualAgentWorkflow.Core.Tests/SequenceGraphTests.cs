using System.Collections.Immutable;
using System.Text.Json;
using VisualAgentWorkflow.Core;
using Xunit;

namespace VisualAgentWorkflow.Core.Tests;

public sealed class SequenceGraphTests
{
    [Fact]
    public void Sequence_exposes_ordered_execution_pins_and_round_trips()
    {
        var definition = Definition();
        var sequence = definition.Nodes["sequence"];

        Assert.Equal(["then.inventory", "then.architecture", "then.quality"], WorkflowPorts.ExplicitExecutionOutputs(sequence).ToArray());
        Assert.Equal("Architecture research", WorkflowPorts.Explicit(sequence).Single(port => port.Id == "then.architecture").DisplayName);

        var restored = JsonSerializer.Deserialize<WorkflowDefinition>(JsonSerializer.Serialize(definition));
        Assert.NotNull(restored);
        Assert.Equal(sequence.Sequence!.Outputs.ToArray(), restored!.Nodes["sequence"].Sequence!.Outputs.ToArray());
        Assert.Empty(WorkflowValidator.Validate(restored));
    }

    [Fact]
    public void Sequence_requires_two_to_four_unique_bounded_outputs()
    {
        var invalid = Definition() with
        {
            Nodes = Definition().Nodes.SetItem("sequence", Definition().Nodes["sequence"] with
            {
                Sequence = new SequenceConfiguration([
                    new("first", "First"),
                    new("first", "Duplicate"),
                    new("third", "Third"),
                    new("fourth", "Fourth"),
                    new("fifth", "Fifth")])
            })
        };

        Assert.Contains(WorkflowValidator.Validate(invalid), issue => issue.Code == "sequence.configuration");
    }

    [Fact]
    public void Sequence_is_rejected_by_legacy_schema_versions()
    {
        var invalid = Definition() with { SchemaVersion = WorkflowSchemaVersions.DirectMcpPinsGraph };

        Assert.Contains(WorkflowValidator.Validate(invalid), issue => issue.Code is "execution.node.unsupported" or "node.schema");
    }

    [Fact]
    public void Sequence_requires_each_ordered_output_to_be_connected_once()
    {
        var missing = Definition() with { ExecutionConnections = Definition().ExecutionConnections!.Remove("sequence-quality") };

        Assert.Contains(WorkflowValidator.Validate(missing), issue => issue.Code == "graph.degree" && issue.Path.Contains("then.quality", StringComparison.Ordinal));
    }

    [Fact]
    public void Sequence_editing_preserves_stable_ids_and_wires()
    {
        var renamed = GraphEditing.RenameSequenceOutput(Definition(), "sequence", "architecture", "System architecture");
        var moved = GraphEditing.MoveSequenceOutput(renamed, "sequence", "quality", -1);
        var added = GraphEditing.AddSequenceOutput(moved, "sequence");

        Assert.Equal(["inventory", "quality", "architecture", "output_1"], added.Nodes["sequence"].Sequence!.Outputs.Select(output => output.Id));
        Assert.Contains(added.ExecutionConnections!.Values, edge => edge.Source == new PortReference("sequence", "then.output_1"));
        Assert.Equal("System architecture", added.Nodes["sequence"].Sequence!.Outputs.Single(output => output.Id == "architecture").Label);
        Assert.Empty(WorkflowValidator.Validate(added));

        var removed = GraphEditing.RemoveSequenceOutput(added, "sequence", "output_1");
        Assert.DoesNotContain(removed.ExecutionConnections!.Values, edge => edge.Source == new PortReference("sequence", "then.output_1"));
        Assert.Empty(WorkflowValidator.Validate(removed));
    }

    [Fact]
    public void Sequence_path_schema_accepts_disjoint_linear_tool_paths()
    {
        var definition = PathDefinition();

        Assert.Empty(WorkflowValidator.Validate(definition));
    }

    [Fact]
    public void Sequence_path_promotion_preserves_the_existing_graph_contract()
    {
        var promoted = GraphEditing.EnableSequencePathGraph(Definition());

        Assert.Equal(WorkflowSchemaVersions.SequencePathGraph, promoted.SchemaVersion);
        Assert.Equal(Definition().Nodes["sequence"].Sequence!.Outputs.ToArray(), promoted.Nodes["sequence"].Sequence!.Outputs.ToArray());
        Assert.Equal(Definition().ExecutionConnections!.Select(edge => edge.Value).ToArray(), promoted.ExecutionConnections!.Select(edge => edge.Value).ToArray());
        Assert.Empty(WorkflowValidator.Validate(promoted));
    }

    [Fact]
    public void Sequence_path_promotion_preserves_source_hash_and_rejects_invalid_sources()
    {
        var source = Definition();
        var sourceHash = WorkflowJson.ComputeHash(source);
        var promoted = GraphEditing.EnableSequencePathGraph(source);

        Assert.Equal(sourceHash, WorkflowJson.ComputeHash(source));
        Assert.NotEqual(sourceHash, WorkflowJson.ComputeHash(promoted));

        var invalid = source with
        {
            ExecutionConnections = source.ExecutionConnections!.Remove("sequence-quality")
        };
        Assert.Throws<ArgumentException>(() => GraphEditing.EnableSequencePathGraph(invalid));
    }

    [Fact]
    public void Sequence_agent_path_promotion_preserves_the_valid_direct_mcp_contract()
    {
        var source = PathDefinition();
        var promoted = GraphEditing.EnableSequenceAgentPathGraph(source);

        Assert.Equal(WorkflowSchemaVersions.SequenceAgentPathGraph, promoted.SchemaVersion);
        Assert.Equal(source.ExecutionConnections, promoted.ExecutionConnections);
        Assert.Empty(WorkflowValidator.Validate(promoted));
    }

    [Fact]
    public void Sequence_path_package_round_trip_preserves_schema_outputs_and_execution_connections()
    {
        var definition = PathDefinition();
        var package = WorkflowPackageJson.Create(definition, WorkflowLayout.Create(definition));
        var restored = WorkflowPackageJson.Deserialize(WorkflowPackageJson.Serialize(definition, package.Layout)).Definition;

        Assert.Equal(WorkflowSchemaVersions.SequencePathGraph, restored.SchemaVersion);
        Assert.Equal(definition.Nodes["sequence"].Sequence!.Outputs.ToArray(),
            restored.Nodes["sequence"].Sequence!.Outputs.ToArray());
        Assert.Equal(definition.ExecutionConnections, restored.ExecutionConnections);
        Assert.Equal(WorkflowJson.ComputeHash(definition), WorkflowJson.ComputeHash(restored));
        Assert.Empty(WorkflowValidator.Validate(restored));
    }

    [Fact]
    public void Sequence_agent_path_package_round_trip_preserves_agent_contract_and_hash()
    {
        var definition = AgentPathDefinition();
        var package = WorkflowPackageJson.Create(definition, WorkflowLayout.Create(definition));
        var restored = WorkflowPackageJson.Deserialize(WorkflowPackageJson.Serialize(definition, package.Layout));

        Assert.Equal(WorkflowSchemaVersions.SequenceAgentPathGraph, restored.Definition.SchemaVersion);
        Assert.Equal(definition.Nodes["research-agent"].Agent, restored.Definition.Nodes["research-agent"].Agent);
        Assert.Equal(definition.ExecutionConnections, restored.Definition.ExecutionConnections);
        Assert.Equal(WorkflowJson.ComputeHash(definition), WorkflowJson.ComputeHash(restored.Definition));
        Assert.Contains(restored.Requirements, requirement => requirement.Kind == "agent-provider-contract" &&
            requirement.Id == "sequence-proof");
        Assert.Empty(WorkflowValidator.Validate(restored.Definition));
    }

    [Fact]
    public void Older_sequence_path_schema_cannot_silently_downgrade_a_terminal_agent_package()
    {
        var agentPath = AgentPathDefinition();
        var downgraded = agentPath with { SchemaVersion = WorkflowSchemaVersions.SequencePathGraph };

        Assert.Throws<ArgumentException>(() => WorkflowPackageJson.Serialize(
            downgraded, WorkflowLayout.Create(downgraded)));
        Assert.Contains(WorkflowValidator.Validate(downgraded), issue => issue.Code == "node.schema");
    }

    [Fact]
    public void Sequence_path_schema_rejects_shared_downstream_nodes()
    {
        var definition = PathDefinition() with
        {
            ExecutionConnections = PathDefinition().ExecutionConnections!
                .Remove("sequence-architecture")
                .Add("sequence-architecture", new(new("sequence", "then.architecture"), new("inventory-tool", "execute"), "execution"))
        };

        Assert.Contains(WorkflowValidator.Validate(definition), issue => issue.Code == "sequence.path.shared");
    }

    [Fact]
    public void Sequence_path_diagnostics_name_the_unconnected_output()
    {
        var definition = PathDefinition() with
        {
            ExecutionConnections = PathDefinition().ExecutionConnections!.Remove("sequence-architecture")
        };

        var issue = Assert.Single(WorkflowValidator.Validate(definition), issue => issue.Code == "sequence.path.root");

        Assert.Contains("Architecture research", issue.Message, StringComparison.Ordinal);
        Assert.Contains("then.architecture", issue.Message, StringComparison.Ordinal);
        Assert.Contains("bounded direct-MCP path", issue.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Sequence_path_diagnostics_identify_unsupported_nodes_on_their_path()
    {
        var definition = PathDefinition() with
        {
            Nodes = PathDefinition().Nodes.SetItem("inventory-tool", new NodeDefinition(NodeTypes.TextConcat, "1.0.0"))
        };

        var issue = Assert.Single(WorkflowValidator.Validate(definition), issue => issue.Code == "sequence.path.node");

        Assert.Contains("Inventory research", issue.Message, StringComparison.Ordinal);
        Assert.Contains("inventory-tool", issue.Message, StringComparison.Ordinal);
        Assert.Contains("direct MCP tool", issue.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Sequence_path_diagnostics_identify_the_other_owner_of_shared_nodes()
    {
        var path = PathDefinition();
        var definition = path with
        {
            ExecutionConnections = path.ExecutionConnections!
                .Remove("sequence-architecture")
                .Add("sequence-architecture", new(new("sequence", "then.architecture"), new("inventory-tool", "execute"), "execution"))
        };

        var issue = Assert.Single(WorkflowValidator.Validate(definition), issue => issue.Code == "sequence.path.shared");

        Assert.Contains("Architecture research", issue.Message, StringComparison.Ordinal);
        Assert.Contains("Inventory research", issue.Message, StringComparison.Ordinal);
        Assert.Contains("disjoint", issue.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Sequence_path_topology_analyzer_accepts_a_single_terminal_plain_text_agent()
    {
        var definition = PathDefinition();
        var agent = new NodeDefinition(NodeTypes.Agent, "1.0.0")
        {
            Agent = new AgentConfiguration("qwen", null, "Summarize the supplied research.", [])
            { ProviderContractHash = new string('a', 64), MaxTurns = 1, MaxTotalTokens = 4000 }
        };
        var candidate = definition with
        {
            Nodes = definition.Nodes.Remove("inventory-tool").Add("inventory-agent", agent),
            ExecutionConnections = definition.ExecutionConnections!
                .Remove("sequence-inventory")
                .Remove("inventory-output")
                .Add("sequence-inventory", new(new("sequence", "then.inventory"), new("inventory-agent", "execute"), "execution"))
                .Add("inventory-output", new(new("inventory-agent", "out"), new("output", "execute"), "execution"))
        };

        var analysis = SequencePathTopologyAnalyzer.Analyze(candidate);

        Assert.True(analysis.IsValid, string.Join(Environment.NewLine, analysis.Issues.Select(issue => issue.Message)));
        var path = Assert.Single(analysis.Paths, path => path.OutputId == "inventory");
        Assert.True(path.ContainsPlainTextAgent);
        Assert.Equal("inventory-agent", Assert.Single(path.NodeIds));
        Assert.Equal("output", path.TerminalNodeId);
        Assert.Contains(WorkflowValidator.Validate(candidate), issue => issue.Code is "execution.node.unsupported" or "sequence.path.node");
    }

    [Fact]
    public void Sequence_path_topology_analyzer_accepts_direct_mcp_tools_before_a_terminal_agent()
    {
        var definition = PathDefinition();
        var agent = new NodeDefinition(NodeTypes.Agent, "1.0.0")
        {
            Agent = new AgentConfiguration("qwen", null, "Summarize the supplied research.", [])
            { ProviderContractHash = new string('a', 64), MaxTurns = 1, MaxTotalTokens = 4000 }
        };
        var candidate = definition with
        {
            SchemaVersion = WorkflowSchemaVersions.SequenceAgentPathGraph,
            Nodes = definition.Nodes.Add("inventory-agent", agent),
            ExecutionConnections = definition.ExecutionConnections!
                .Remove("inventory-output")
                .Add("inventory-agent", new(new("inventory-tool", "then"), new("inventory-agent", "execute"), "execution"))
                .Add("inventory-output", new(new("inventory-agent", "out"), new("output", "execute"), "execution"))
        };

        var analysis = SequencePathTopologyAnalyzer.Analyze(candidate);

        Assert.True(analysis.IsValid, string.Join(Environment.NewLine, analysis.Issues.Select(issue => issue.Message)));
        var path = Assert.Single(analysis.Paths, path => path.OutputId == "inventory");
        Assert.Equal(["inventory-tool", "inventory-agent"], path.NodeIds.ToArray());
        Assert.True(path.ContainsPlainTextAgent);
        Assert.Empty(WorkflowValidator.Validate(candidate));
        var package = WorkflowPackageJson.Create(candidate, WorkflowLayout.Create(candidate));
        var restored = WorkflowPackageJson.Deserialize(WorkflowPackageJson.Serialize(candidate, package.Layout)).Definition;
        Assert.Equal(candidate.ExecutionConnections, restored.ExecutionConnections);
        Assert.Equal(WorkflowJson.ComputeHash(candidate), WorkflowJson.ComputeHash(restored));
    }

    [Fact]
    public void Sequence_path_topology_analyzer_rejects_agent_capabilities_that_widen_the_bounded_contract()
    {
        var definition = PathDefinition();
        var agent = new NodeDefinition(NodeTypes.Agent, "1.0.0")
        {
            Agent = new AgentConfiguration("qwen", null, "Summarize the supplied research.",
                [new ToolBindingRequest("tool", new string('a', 64))])
        };
        var candidate = definition with
        {
            Nodes = definition.Nodes.Remove("inventory-tool").Add("inventory-agent", agent),
            ExecutionConnections = definition.ExecutionConnections!
                .Remove("sequence-inventory")
                .Remove("inventory-output")
                .Add("sequence-inventory", new(new("sequence", "then.inventory"), new("inventory-agent", "execute"), "execution"))
                .Add("inventory-output", new(new("inventory-agent", "out"), new("output", "execute"), "execution"))
        };

        var analysis = SequencePathTopologyAnalyzer.Analyze(candidate);

        Assert.Contains(analysis.Issues, issue => issue.Code == "sequence.agent.restriction");
    }

    [Fact]
    public void Sequence_agent_path_schema_accepts_mixed_direct_mcp_and_terminal_plain_text_agent_paths()
    {
        var source = PathDefinition();
        var agent = new NodeDefinition(NodeTypes.Agent, "1.0.0")
        {
            Agent = new AgentConfiguration("qwen", null, "Summarize the supplied research.", [])
            { ProviderContractHash = new string('a', 64), MaxTurns = 1, MaxTotalTokens = 4000 }
        };
        var definition = source with
        {
            SchemaVersion = WorkflowSchemaVersions.SequenceAgentPathGraph,
            Nodes = source.Nodes.Remove("inventory-tool").Add("inventory-agent", agent),
            ExecutionConnections = source.ExecutionConnections!
                .Remove("sequence-inventory")
                .Remove("inventory-output")
                .Add("sequence-inventory", new(new("sequence", "then.inventory"), new("inventory-agent", "execute"), "execution"))
                .Add("inventory-output", new(new("inventory-agent", "out"), new("output", "execute"), "execution"))
        };

        Assert.Empty(WorkflowValidator.Validate(definition));
    }

    [Fact]
    public void Sequence_transform_path_schema_accepts_a_terminal_uppercase_step()
    {
        var source = PathDefinition();
        var definition = source with
        {
            SchemaVersion = WorkflowSchemaVersions.SequenceTransformPathGraph,
            Nodes = source.Nodes.Remove("inventory-tool").Add("inventory-uppercase", new(NodeTypes.SequenceUppercase, "1.0.0")),
            ExecutionConnections = source.ExecutionConnections!
                .Remove("sequence-inventory")
                .Remove("inventory-output")
                .Add("sequence-inventory", new(new("sequence", "then.inventory"), new("inventory-uppercase", "execute"), "execution"))
                .Add("inventory-output", new(new("inventory-uppercase", "then"), new("output", "execute"), "execution"))
        };

        Assert.Empty(WorkflowValidator.Validate(definition));
        var analysis = SequencePathTopologyAnalyzer.Analyze(definition);
        Assert.True(analysis.IsValid, string.Join(Environment.NewLine, analysis.Issues.Select(issue => issue.Message)));
    }

    [Fact]
    public void Sequence_reverse_path_schema_accepts_a_terminal_reverse_step()
    {
        var source = PathDefinition();
        var definition = source with
        {
            SchemaVersion = WorkflowSchemaVersions.SequenceReversePathGraph,
            Nodes = source.Nodes.Remove("inventory-tool").Add("inventory-reverse", new(NodeTypes.SequenceReverse, "1.0.0")),
            ExecutionConnections = source.ExecutionConnections!
                .Remove("sequence-inventory")
                .Remove("inventory-output")
                .Add("sequence-inventory", new(new("sequence", "then.inventory"), new("inventory-reverse", "execute"), "execution"))
                .Add("inventory-output", new(new("inventory-reverse", "then"), new("output", "execute"), "execution"))
        };

        Assert.Empty(WorkflowValidator.Validate(definition));
    }

    [Fact]
    public void Sequence_reverse_path_promotion_preserves_an_existing_uppercase_path()
    {
        var source = PathDefinition();
        var uppercase = source with
        {
            SchemaVersion = WorkflowSchemaVersions.SequenceTransformPathGraph,
            Nodes = source.Nodes.Remove("inventory-tool").Add("inventory-uppercase", new(NodeTypes.SequenceUppercase, "1.0.0")),
            ExecutionConnections = source.ExecutionConnections!
                .Remove("sequence-inventory")
                .Remove("inventory-output")
                .Add("sequence-inventory", new(new("sequence", "then.inventory"), new("inventory-uppercase", "execute"), "execution"))
                .Add("inventory-output", new(new("inventory-uppercase", "then"), new("output", "execute"), "execution"))
        };

        var promoted = GraphEditing.EnableSequenceReversePathGraph(uppercase);

        Assert.Equal(WorkflowSchemaVersions.SequenceReversePathGraph, promoted.SchemaVersion);
        Assert.Empty(WorkflowValidator.Validate(promoted));
    }

    [Fact]
    public void Sequence_agent_path_schema_rejects_unbounded_agent_limits_and_capabilities()
    {
        var source = PathDefinition();
        var agent = new NodeDefinition(NodeTypes.Agent, "1.0.0")
        {
            Agent = new AgentConfiguration("qwen", null, "Summarize the supplied research.",
                [new ToolBindingRequest("tool", new string('a', 64))])
            { ProviderContractHash = new string('a', 64), MaxTurns = AgentExecutionLimits.UnlimitedTurns, MaxTotalTokens = 4000 }
        };
        var definition = source with
        {
            SchemaVersion = WorkflowSchemaVersions.SequenceAgentPathGraph,
            Nodes = source.Nodes.Remove("inventory-tool").Add("inventory-agent", agent),
            ExecutionConnections = source.ExecutionConnections!
                .Remove("sequence-inventory")
                .Remove("inventory-output")
                .Add("sequence-inventory", new(new("sequence", "then.inventory"), new("inventory-agent", "execute"), "execution"))
                .Add("inventory-output", new(new("inventory-agent", "out"), new("output", "execute"), "execution"))
        };

        var issues = WorkflowValidator.Validate(definition);
        Assert.Contains(issues, issue => issue.Code == "sequence.agent.limits");
        Assert.Contains(issues, issue => issue.Code == "sequence.agent.restriction");
    }

    [Fact]
    public void Sequence_agent_path_schema_requires_the_agent_to_be_the_terminal_path_node()
    {
        var source = PathDefinition();
        var agent = new NodeDefinition(NodeTypes.Agent, "1.0.0")
        {
            Agent = new AgentConfiguration("qwen", null, "Summarize the supplied research.", [])
            { ProviderContractHash = new string('a', 64), MaxTurns = 1, MaxTotalTokens = 4000 }
        };
        var definition = source with
        {
            SchemaVersion = WorkflowSchemaVersions.SequenceAgentPathGraph,
            Nodes = source.Nodes.Remove("inventory-tool").Add("inventory-agent", agent),
            ExecutionConnections = source.ExecutionConnections!
                .Remove("sequence-inventory")
                .Remove("inventory-output")
                .Add("sequence-inventory", new(new("sequence", "then.inventory"), new("inventory-agent", "execute"), "execution"))
                .Add("inventory-agent-next", new(new("inventory-agent", "out"), new("architecture-tool", "execute"), "execution"))
                .Add("inventory-output", new(new("architecture-tool", "then"), new("output", "execute"), "execution"))
        };

        var issues = WorkflowValidator.Validate(definition);

        Assert.Contains(issues, issue => issue.Code == "sequence.agent.terminal");
    }

    private static WorkflowDefinition Definition()
    {
        var nodes = ImmutableSortedDictionary<string, NodeDefinition>.Empty.WithComparers(StringComparer.Ordinal)
            .Add("input", new(NodeTypes.Input, "1.0.0"))
            .Add("sequence", new(NodeTypes.Sequence, "1.0.0")
            {
                Sequence = new SequenceConfiguration([
                    new("inventory", "Inventory research"),
                    new("architecture", "Architecture research"),
                    new("quality", "Quality research")])
            })
            .Add("output", new(NodeTypes.Output, "1.0.0"));
        var execution = ImmutableSortedDictionary<string, ConnectionDefinition>.Empty.WithComparers(StringComparer.Ordinal)
            .Add("input-sequence", new(new("input", "then"), new("sequence", "execute"), "execution"))
            .Add("sequence-inventory", new(new("sequence", "then.inventory"), new("output", "execute"), "execution"))
            .Add("sequence-architecture", new(new("sequence", "then.architecture"), new("output", "execute"), "execution"))
            .Add("sequence-quality", new(new("sequence", "then.quality"), new("output", "execute"), "execution"));
        return new WorkflowDefinition(WorkflowSchemaVersions.SequenceGraph, "sequence", "input", nodes, ImmutableSortedDictionary<string, ConnectionDefinition>.Empty.WithComparers(StringComparer.Ordinal))
        {
            ExecutionConnections = execution
        };
    }

    private static WorkflowDefinition PathDefinition()
    {
        var definition = Definition();
        var nodes = definition.Nodes
            .Add("inventory-tool", new(NodeTypes.McpTool, "1.0.0", new ToolBindingRequest("demo.inventory", new string('a', 64)))
            {
                McpArgumentsJson = "{}"
            })
            .Add("architecture-tool", new(NodeTypes.McpTool, "1.0.0", new ToolBindingRequest("demo.architecture", new string('b', 64)))
            {
                McpArgumentsJson = "{}"
            });
        var execution = definition.ExecutionConnections!
            .Remove("sequence-inventory")
            .Remove("sequence-architecture")
            .Add("sequence-inventory", new(new("sequence", "then.inventory"), new("inventory-tool", "execute"), "execution"))
            .Add("inventory-output", new(new("inventory-tool", "then"), new("output", "execute"), "execution"))
            .Add("sequence-architecture", new(new("sequence", "then.architecture"), new("architecture-tool", "execute"), "execution"))
            .Add("architecture-output", new(new("architecture-tool", "then"), new("output", "execute"), "execution"));
        return definition with
        {
            SchemaVersion = WorkflowSchemaVersions.SequencePathGraph,
            Nodes = nodes,
            ExecutionConnections = execution
        };
    }

    private static WorkflowDefinition AgentPathDefinition()
    {
        var source = PathDefinition();
        var agent = new NodeDefinition(NodeTypes.Agent, "1.0.0")
        {
            Agent = new AgentConfiguration("sequence-proof", null, "Return the supplied text.", [])
            { ProviderContractHash = new string('a', 64), MaxTurns = 1, MaxTotalTokens = 4000 }
        };
        return source with
        {
            SchemaVersion = WorkflowSchemaVersions.SequenceAgentPathGraph,
            Nodes = source.Nodes.Remove("inventory-tool").Add("research-agent", agent),
            ExecutionConnections = source.ExecutionConnections!
                .Remove("sequence-inventory")
                .Remove("inventory-output")
                .Add("sequence-inventory", new(new("sequence", "then.inventory"), new("research-agent", "execute"), "execution"))
                .Add("inventory-output", new(new("research-agent", "out"), new("output", "execute"), "execution"))
        };
    }
}
