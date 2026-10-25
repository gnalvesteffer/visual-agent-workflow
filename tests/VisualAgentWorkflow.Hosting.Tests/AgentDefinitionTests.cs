using VisualAgentWorkflow.Blazor;
using VisualAgentWorkflow.Core;
using Xunit;

namespace VisualAgentWorkflow.Hosting.Tests;

public class AgentDefinitionTests
{
    private static NodeDefinition Agent() => new(NodeTypes.Agent, "1.0.0", Agent: new("demo.scripted", "1.0.0", "Instructions", []));

    [Fact]
    public void AgentRoundTripAndStructuralComparisonPreserveToolRequirements()
    {
        var agent = Agent();
        agent = agent with { Agent = agent.Agent! with { Tools = [new("demo.reverse", new string('a', 64))] } };
        var definition = WorkflowEditing.ReplaceNode(WorkflowTemplates.Create("uppercase"), "step-1", agent);
        Assert.Equal("1.2", definition.SchemaVersion);
        var loaded = WorkflowJson.Deserialize(WorkflowJson.Serialize(definition));
        Assert.Equal(WorkflowJson.ComputeHash(definition), WorkflowJson.ComputeHash(loaded));
        Assert.Empty(WorkflowDifference.Compare(definition, loaded));
        Assert.Equal("demo.reverse", Assert.Single(loaded.Nodes["step-1"].Agent!.Tools).BindingId);
    }

    [Fact]
    public void DuplicateToolRequirementsAndLegacySchemaAreRejected()
    {
        var definition = WorkflowEditing.ReplaceNode(WorkflowTemplates.Create("uppercase"), "step-1", Agent());
        Assert.Contains(WorkflowValidator.Validate(definition with { SchemaVersion = "1.0" }), issue => issue.Code == "node.schema");
        var node = Agent();
        node = node with { Agent = node.Agent! with { Tools = [new("same", new string('a', 64)), new("same", new string('b', 64))] } };
        Assert.Throws<ArgumentException>(() => WorkflowEditing.ReplaceNode(definition, "step-1", node));
        Assert.Throws<ArgumentException>(() => WorkflowEditing.ReplaceNode(definition, "step-1", Agent() with { Tool = new("unexpected", new string('a', 64)) }));
    }

    [Fact]
    public void AddingAnMcpNodeDoesNotDowngradeAnExistingAgentSchema()
    {
        var definition = WorkflowEditing.ReplaceNode(WorkflowTemplates.Create("uppercase"), "step-1", Agent());
        definition = WorkflowEditing.InsertNodeAfter(definition, "step-1", new(NodeTypes.McpTool, "1.0.0", new("demo.reverse", new string('a', 64))));
        Assert.Equal("1.2", definition.SchemaVersion);
        Assert.Empty(WorkflowValidator.Validate(definition));
    }
}
