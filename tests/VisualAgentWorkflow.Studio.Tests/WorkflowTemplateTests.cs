using VisualAgentWorkflow.Blazor;
using VisualAgentWorkflow.Core;
using Xunit;

namespace VisualAgentWorkflow.Studio.Tests;

public sealed class WorkflowTemplateTests
{
    [Theory]
    [InlineData("uppercase")]
    [InlineData("reverse")]
    [InlineData("uppercase-reverse")]
    public void New_text_templates_use_the_explicit_graph_contract(string operation)
    {
        var definition = WorkflowTemplates.Create(operation);

        Assert.Equal(WorkflowSchemaVersions.ExplicitGraph, definition.SchemaVersion);
        Assert.NotNull(definition.ExecutionConnections);
        Assert.Equal(
            new ConnectionDefinition(new("input", "then"), new("output", "execute"), "execution"),
            Assert.Single(definition.ExecutionConnections!).Value);
        Assert.Empty(WorkflowValidator.Validate(definition));
    }
}
