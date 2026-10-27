using VisualAgentWorkflow.Core;

namespace VisualAgentWorkflow.Blazor.Components;

public static class WorkflowRunInputGuidance
{
    public const string RepositoryKnowledgebaseWorkflowId = "repository-knowledgebase";

    public const string RepositoryKnowledgebaseDefaultInput =
        "Perform a comprehensive knowledgebase research pass over this repository. " +
        "Document its purpose, repository map, architecture and runtime flow, key modules, " +
        "build and test commands, extension points, security and operational boundaries, " +
        "dependencies, tradeoffs, risks, unanswered questions, and confidence-backed citations. " +
        "Use only evidence from the configured repository and clearly separate facts from inference.";

    public const string RepositoryKnowledgebaseInputHint =
        "Describe the repository research you want. For a complete knowledgebase, use the prefilled research brief or add a focused area to investigate.";

    public static string For(WorkflowDefinition definition) =>
        string.Equals(definition.WorkflowId, RepositoryKnowledgebaseWorkflowId, StringComparison.Ordinal)
            ? RepositoryKnowledgebaseDefaultInput
            : "Hello, workflows!";

    public static string HintFor(WorkflowDefinition definition) =>
        string.Equals(definition.WorkflowId, RepositoryKnowledgebaseWorkflowId, StringComparison.Ordinal)
            ? RepositoryKnowledgebaseInputHint
            : "The input and parameter values are recorded with the run.";
}
