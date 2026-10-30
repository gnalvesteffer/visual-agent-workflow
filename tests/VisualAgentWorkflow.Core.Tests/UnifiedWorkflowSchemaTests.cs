using System.Collections.Immutable;
using VisualAgentWorkflow.Core;
using Xunit;

namespace VisualAgentWorkflow.Core.Tests;

public sealed class UnifiedWorkflowSchemaTests
{
    [Theory]
    [InlineData("kind")]
    [InlineData("source")]
    [InlineData("target")]
    [InlineData("port")]
    public void Loop_feedback_edges_are_validated_before_cycle_analysis(string mutation)
    {
        var graph = LoopGraph();
        Assert.Empty(WorkflowValidator.Validate(graph));
        var edge = graph.ExecutionConnections!["return"];
        var malformed = mutation switch
        {
            "kind" => edge with { Kind = "data" },
            "source" => edge with { Source = null! },
            "target" => edge with { Target = null! },
            _ => edge with { Source = edge.Source with { PortId = "undeclared" } }
        };
        var invalid = graph with { ExecutionConnections = graph.ExecutionConnections.SetItem("return", malformed) };
        Assert.Contains(WorkflowValidator.Validate(invalid), issue => issue.Code is "execution.reference" or "execution.port");
        Assert.Throws<ArgumentException>(() => WorkflowRevision.Create(invalid, "test"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Malformed_child_entry_reports_configuration_diagnostics(bool missingNodes)
    {
        var child = LoopGraph() with { WorkflowId = "child" };
        child = missingNodes ? child with { Nodes = null! } : child with { Nodes = child.Nodes.SetItem("input", null!) };
        var graph = MixedGraph();
        graph = graph with
        {
            Nodes = graph.Nodes.Add("call", new(NodeTypes.WorkflowCall, "1.0.0")
            { WorkflowCall = new("child", Guid.NewGuid(), 1, new string('a', 64), child) })
        };
        Assert.Contains(WorkflowValidator.Validate(graph), issue => issue.Code == "workflowCall.configuration");
    }

    [Fact]
    public void Unconfigured_current_nodes_remain_saveable_drafts_and_cannot_execute()
    {
        var graph = MixedGraph();
        var draft = GraphEditing.AddUnconnectedNode(graph, new(NodeTypes.Sequence, "1.0.0"));
        var roundTrip = WorkflowJson.DeserializeDraft(WorkflowJson.SerializeDraft(draft));
        Assert.Equal(WorkflowSchemaVersions.CurrentAuthoringSchema, roundTrip.SchemaVersion);
        Assert.Contains(WorkflowValidator.Validate(roundTrip), issue => issue.Code == "sequence.configuration");
        Assert.Throws<ArgumentException>(() => WorkflowRevision.Create(roundTrip, "test"));
    }

    [Fact]
    public void Current_graph_combines_both_conversions_integer_math_and_typed_mcp_without_promotions()
    {
        var graph = MixedGraph();

        Assert.Empty(WorkflowValidator.Validate(graph));
        Assert.Empty(WorkflowDefinitionDescriptorAudit.Audit(graph));
        Assert.Equal(WorkflowJson.ComputeHash(graph), WorkflowJson.ComputeHash(WorkflowJson.Deserialize(WorkflowJson.Serialize(graph))));
    }

    [Fact]
    public void Current_graph_preserves_agent_limits_and_requires_valid_configuration()
    {
        var graph = MixedGraph();
        var agent = new NodeDefinition(NodeTypes.Agent, "1.0.0")
        {
            Agent = new("fixture", null, "Answer", [])
            {
                MaxTurns = 5, MaxOutputTokens = 100, MaxTotalTokens = 1000,
                TimeoutSeconds = 30, ReasoningEffort = AgentReasoningEffort.Off,
                UserMessage = new([new("input", "")])
            }
        };
        graph = graph with
        {
            Nodes = graph.Nodes.Add("agent", agent),
            Connections = graph.Connections.SetItem("result", Data("agent", "out", "output", "in"))
                .Add("agent-input", Data("tool", "out", "agent", "in")),
            ExecutionConnections = graph.ExecutionConnections!.SetItem("complete", Execution("tool", "then", "agent"))
                .Add("agent-complete", Execution("agent", "out", "output"))
        };
        Assert.Empty(WorkflowValidator.Validate(graph));

        var invalid = graph with { Nodes = graph.Nodes.SetItem("agent", agent with { Agent = agent.Agent! with { MaxTurns = 0 } }) };
        Assert.Contains(WorkflowValidator.Validate(invalid), issue => issue.Code == "agent.maxTurns");
    }

    [Fact]
    public void Current_graph_checks_mcp_required_arguments_static_values_and_pin_cardinality()
    {
        var graph = MixedGraph();
        var missing = graph with { Connections = graph.Connections.Remove("argument") };
        Assert.Contains(WorkflowValidator.Validate(missing), issue => issue.Code == "data.degree" && issue.Path.Contains("arg.query"));

        var configured = missing with { Nodes = missing.Nodes.SetItem("tool", missing.Nodes["tool"] with { McpArgumentsJson = "{\"query\":\"hello\"}" }) };
        Assert.DoesNotContain(WorkflowValidator.Validate(configured), issue => issue.Path.Contains("arg.query"));
        var duplicate = graph with { Connections = graph.Connections.Add("duplicate", graph.Connections["argument"]) };
        Assert.Contains(WorkflowValidator.Validate(duplicate), issue => issue.Code == "data.degree");
    }

    [Fact]
    public void Current_graph_retains_type_cycle_and_input_configuration_validation()
    {
        var graph = MixedGraph();
        var wrongType = graph with { Connections = graph.Connections.SetItem("argument", Data("parse", "out", "tool", "arg.query")) };
        Assert.Contains(WorkflowValidator.Validate(wrongType), issue => issue.Code == "connection.port");
        var badInput = graph with { Nodes = graph.Nodes.SetItem("input", graph.Nodes["input"] with { InputDataType = "arbitrary" }) };
        Assert.Contains(WorkflowValidator.Validate(badInput), issue => issue.Code == "input.dataType");
    }

    [Fact]
    public void Current_graph_json_entry_and_projection_have_matching_explicit_pins()
    {
        var graph = new WorkflowDefinition(WorkflowSchemaVersions.CurrentAuthoringSchema, "json", "input",
            new Dictionary<string, NodeDefinition>
            {
                ["input"] = new(NodeTypes.Input, "1.0.0") { InputDataType = "json" },
                ["select"] = new(NodeTypes.JsonSelectString, "1.0.0") { JsonPropertySelection = new("/name") },
                ["output"] = new(NodeTypes.Output, "1.0.0")
            }.ToImmutableSortedDictionary(),
            new Dictionary<string, ConnectionDefinition>
            {
                ["select"] = Data("input", "out", "select", "in"),
                ["result"] = Data("select", "out", "output", "in")
            }.ToImmutableSortedDictionary())
        {
            ExecutionConnections = ImmutableSortedDictionary<string, ConnectionDefinition>.Empty.Add("complete", Execution("input", "then", "output"))
        };
        Assert.Empty(WorkflowValidator.Validate(graph));
        Assert.Empty(WorkflowDefinitionDescriptorAudit.Audit(graph));
    }

    private static WorkflowDefinition MixedGraph() => new(WorkflowSchemaVersions.CurrentAuthoringSchema, "mixed", "input",
        new Dictionary<string, NodeDefinition>
        {
            ["input"] = new(NodeTypes.Input, "1.0.0"),
            ["parse"] = new(NodeTypes.StringToInteger, "1.0.0"),
            ["one"] = new(NodeTypes.IntegerConstant, "1.0.0") { IntegerConstant = new(1) },
            ["add"] = new(NodeTypes.IntegerAdd, "1.0.0"),
            ["format"] = new(NodeTypes.IntegerToString, "1.0.0"),
            ["tool"] = new(NodeTypes.McpTool, "1.0.0", new("fixture.search", new string('a', 64)))
            { McpInputPorts = [new("arg.query", "query", "string", true)] },
            ["output"] = new(NodeTypes.Output, "1.0.0")
        }.ToImmutableSortedDictionary(),
        new Dictionary<string, ConnectionDefinition>
        {
            ["parse"] = Data("input", "out", "parse", "in"),
            ["left"] = Data("parse", "out", "add", "left"),
            ["right"] = Data("one", "out", "add", "right"),
            ["format"] = Data("add", "out", "format", "in"),
            ["argument"] = Data("format", "out", "tool", "arg.query"),
            ["result"] = Data("tool", "out", "output", "in")
        }.ToImmutableSortedDictionary())
    {
        ExecutionConnections = new Dictionary<string, ConnectionDefinition>
        {
            ["tool"] = Execution("input", "then", "tool"),
            ["complete"] = Execution("tool", "then", "output")
        }.ToImmutableSortedDictionary()
    };

    private static WorkflowDefinition LoopGraph() => new(WorkflowSchemaVersions.CurrentAuthoringSchema, "loop", "input",
        new Dictionary<string, NodeDefinition>
        {
            ["input"] = new(NodeTypes.Input, "1.0.0"),
            ["loop"] = new(NodeTypes.Loop, "1.0.0") { Loop = new(new("contains", "yes"), 2) },
            ["body"] = new(NodeTypes.LoopBody, "1.0.0"),
            ["output"] = new(NodeTypes.Output, "1.0.0")
        }.ToImmutableSortedDictionary(),
        ImmutableSortedDictionary<string, ConnectionDefinition>.Empty.Add("result", Data("input", "out", "output", "in")))
    {
        ExecutionConnections = new Dictionary<string, ConnectionDefinition>
        {
            ["start"] = Execution("input", "then", "loop"),
            ["body"] = Execution("loop", "body", "body"),
            ["return"] = Execution("body", "then", "loop"),
            ["done"] = Execution("loop", "done", "output")
        }.ToImmutableSortedDictionary()
    };

    private static ConnectionDefinition Data(string source, string pin, string target, string input) => new(new(source, pin), new(target, input), "data");
    private static ConnectionDefinition Execution(string source, string pin, string target) => new(new(source, pin), new(target, "execute"), "execution");
}
