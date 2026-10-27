using System.Collections.Immutable;
using Xunit;
using VisualAgentWorkflow.Blazor.Components;
using VisualAgentWorkflow.Core;

namespace VisualAgentWorkflow.Studio.Tests;

public sealed class WorkflowRunInputGuidanceTests
{
    [Fact]
    public void RepositoryKnowledgebase_UsesResearchBriefAndHint()
    {
        var definition = Definition(WorkflowRunInputGuidance.RepositoryKnowledgebaseWorkflowId);

        Assert.Contains("comprehensive knowledgebase research", WorkflowRunInputGuidance.For(definition), StringComparison.Ordinal);
        Assert.Contains("prefilled research brief", WorkflowRunInputGuidance.HintFor(definition), StringComparison.Ordinal);
    }

    [Fact]
    public void OtherWorkflows_KeepGenericRunInput()
    {
        var definition = Definition("other-workflow");

        Assert.Equal("Hello, workflows!", WorkflowRunInputGuidance.For(definition));
    }

    private static WorkflowDefinition Definition(string workflowId) => new(
        "1.0", workflowId, "input",
        ImmutableSortedDictionary<string, NodeDefinition>.Empty
            .Add("input", new(NodeTypes.Input, "1.0.0")),
        ImmutableSortedDictionary<string, ConnectionDefinition>.Empty);
}
