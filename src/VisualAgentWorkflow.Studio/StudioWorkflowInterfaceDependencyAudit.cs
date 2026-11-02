using System.Collections.Immutable;
using VisualAgentWorkflow.Core;

namespace VisualAgentWorkflow.Studio;

public enum StudioWorkflowInterfaceDependencyIssueKind { Missing, Drift, Ambiguous, AuditIncomplete }

public sealed record StudioWorkflowInterfaceDependencyIssue(
    StudioWorkflowInterfaceDependencyIssueKind Kind, string WorkflowId, string WorkflowName,
    string NodeId, string NestedPath, string AssetKind, string AssetId, int Version, string Message);

/// <summary>Advisory source audit for immutable interface-call snapshots; it never changes execution inputs.</summary>
public static class StudioWorkflowInterfaceDependencyAudit
{
    public const int MaximumExpandedNodes = 1024;
    public const int MaximumDepth = WorkflowCallLimits.MaximumDepth;

    public static ImmutableArray<StudioWorkflowInterfaceDependencyIssue> Assess(
        IEnumerable<(string WorkflowId, string WorkflowName, WorkflowDefinition Definition)> workflows,
        IEnumerable<WorkflowInterfaceRevision> interfaces,
        IEnumerable<WorkflowInterfaceImplementationRevision> implementations)
    {
        ArgumentNullException.ThrowIfNull(workflows);
        ArgumentNullException.ThrowIfNull(interfaces);
        ArgumentNullException.ThrowIfNull(implementations);
        var sources = interfaces.ToArray();
        var implementationSources = implementations.ToArray();
        var issues = ImmutableArray.CreateBuilder<StudioWorkflowInterfaceDependencyIssue>();
        var expandedNodes = 0;
        var expansionLimitReported = false;
        foreach (var workflow in workflows)
        {
            if (expandedNodes >= MaximumExpandedNodes)
            {
                if (!expansionLimitReported)
                    Add(StudioWorkflowInterfaceDependencyIssueKind.AuditIncomplete, workflow.WorkflowId, workflow.WorkflowName,
                        "", [], "project", workflow.WorkflowId, 0,
                        $"Dependency audit stopped at the {MaximumExpandedNodes} node expansion limit.", issues);
                break;
            }
            var visited = new HashSet<string>(StringComparer.Ordinal);
            Visit(workflow.Definition, workflow.WorkflowId, workflow.WorkflowName, [], 0, visited,
                sources, implementationSources, issues, ref expandedNodes, ref expansionLimitReported);
        }
        return issues.ToImmutable();
    }

    private static void Visit(WorkflowDefinition definition, string rootId, string rootName, string[] path,
        int depth, HashSet<string> visited, WorkflowInterfaceRevision[] interfaces,
        WorkflowInterfaceImplementationRevision[] implementations,
        ImmutableArray<StudioWorkflowInterfaceDependencyIssue>.Builder issues, ref int expandedNodes,
        ref bool expansionLimitReported)
    {
        foreach (var (nodeId, node) in definition.Nodes)
        {
            if (expandedNodes >= MaximumExpandedNodes)
            {
                if (!expansionLimitReported)
                {
                    Add(StudioWorkflowInterfaceDependencyIssueKind.AuditIncomplete, rootId, rootName,
                        path.LastOrDefault() ?? "", path, "project", definition.WorkflowId, 0,
                        $"Dependency audit stopped at the {MaximumExpandedNodes} node expansion limit.", issues);
                    expansionLimitReported = true;
                }
                return;
            }
            expandedNodes++;
            if (node.WorkflowInterfaceCall is not { } call) continue;
            var callPath = path.Append($"{definition.WorkflowId}/{nodeId}").ToArray();
            var config = call.Implementation;
            CheckImplementation(config, rootId, rootName, nodeId, callPath, implementations, issues);
            CheckInterface(config.Definition.Interface, rootId, rootName, nodeId, callPath, interfaces, issues);

            var key = $"{config.RevisionId:N}:{config.ContentHash}";
            if (!visited.Add(key)) continue;
            if (depth >= MaximumDepth)
            {
                Add(StudioWorkflowInterfaceDependencyIssueKind.AuditIncomplete, rootId, rootName,
                    nodeId, callPath, "workflow-interface-implementation", config.Definition.ImplementationId,
                    config.Version, $"Nested dependency audit reached the maximum depth ({MaximumDepth}).", issues);
                continue;
            }
            foreach (var pin in config.Definition.Operations.Values)
                Visit(pin.Definition, rootId, rootName, callPath, depth + 1, visited, interfaces,
                    implementations, issues, ref expandedNodes, ref expansionLimitReported);
        }
    }

    private static void CheckImplementation(WorkflowInterfaceImplementationRevision embedded, string rootId,
        string rootName, string nodeId, string[] path, WorkflowInterfaceImplementationRevision[] sources,
        ImmutableArray<StudioWorkflowInterfaceDependencyIssue>.Builder issues)
    {
        var id = embedded.Definition.ImplementationId;
        var sameVersion = sources.Where(item => item.Definition.ImplementationId == id && item.Version == embedded.Version).ToArray();
        if (sameVersion.Length > 1)
            Add(StudioWorkflowInterfaceDependencyIssueKind.Ambiguous, rootId, rootName, nodeId, path,
                "workflow-interface-implementation", id, embedded.Version, "Multiple project assets declare this implementation ID and version.", issues);
        else if (sameVersion.Length == 1 && (sameVersion[0].RevisionId != embedded.RevisionId || sameVersion[0].ContentHash != embedded.ContentHash))
            Add(StudioWorkflowInterfaceDependencyIssueKind.Drift, rootId, rootName, nodeId, path,
                "workflow-interface-implementation", id, embedded.Version, "The project asset at this implementation version differs from the embedded revision.", issues);
        else if (sameVersion.Length == 0 && sources.Any(item => item.Definition.ImplementationId == id && item.Version > embedded.Version))
            return;
        else if (!sources.Any(item => item.RevisionId == embedded.RevisionId && item.ContentHash == embedded.ContentHash))
            Add(StudioWorkflowInterfaceDependencyIssueKind.Missing, rootId, rootName, nodeId, path,
                "workflow-interface-implementation", id, embedded.Version, "No matching implementation revision is available in the project assets; the embedded call remains self-contained.", issues);
    }

    private static void CheckInterface(WorkflowInterfaceRevision embedded, string rootId, string rootName,
        string nodeId, string[] path, WorkflowInterfaceRevision[] sources,
        ImmutableArray<StudioWorkflowInterfaceDependencyIssue>.Builder issues)
    {
        var id = embedded.Definition.InterfaceId;
        var sameVersion = sources.Where(item => item.Definition.InterfaceId == id && item.Version == embedded.Version).ToArray();
        if (sameVersion.Length > 1)
            Add(StudioWorkflowInterfaceDependencyIssueKind.Ambiguous, rootId, rootName, nodeId, path,
                "workflow-interface", id, embedded.Version, "Multiple project assets declare this interface ID and version.", issues);
        else if (sameVersion.Length == 1 && (sameVersion[0].RevisionId != embedded.RevisionId || sameVersion[0].ContentHash != embedded.ContentHash))
            Add(StudioWorkflowInterfaceDependencyIssueKind.Drift, rootId, rootName, nodeId, path,
                "workflow-interface", id, embedded.Version, "The project asset at this interface version differs from the embedded revision.", issues);
        else if (sameVersion.Length == 0 && sources.Any(item => item.Definition.InterfaceId == id && item.Version > embedded.Version))
            return;
        else if (!sources.Any(item => item.RevisionId == embedded.RevisionId && item.ContentHash == embedded.ContentHash))
            Add(StudioWorkflowInterfaceDependencyIssueKind.Missing, rootId, rootName, nodeId, path,
                "workflow-interface", id, embedded.Version, "No matching interface revision is available in the project assets; the embedded call remains self-contained.", issues);
    }

    private static void Add(StudioWorkflowInterfaceDependencyIssueKind kind, string workflowId, string workflowName,
        string nodeId, string[] path, string assetKind, string assetId, int version, string message,
        ImmutableArray<StudioWorkflowInterfaceDependencyIssue>.Builder issues) =>
        issues.Add(new(kind, workflowId, workflowName, nodeId, string.Join(" → ", path), assetKind, assetId, version, message));
}
