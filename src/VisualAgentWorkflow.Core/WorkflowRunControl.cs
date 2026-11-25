namespace VisualAgentWorkflow.Core;

/// <summary>Optional host capability. Commands request cooperative pauses, never rollback of in-flight work.</summary>
public interface IWorkflowRunControl
{
    Task<RunSnapshot> StartPausedRunAsync(WorkflowDefinition definition, string input);
    Task PauseRunAsync(Guid runId);
    Task ResumeRunAsync(Guid runId);
}

public interface IPublishedWorkflowRunControl : IWorkflowRunControl
{
    Task<RunSnapshot> StartPublishedPausedRunAsync(Guid revisionId, string input);
}

/// <summary>Optional live debugger controls. Breakpoints are process-owned run controls, not workflow content.</summary>
public interface IWorkflowDebugControl
{
    Task SetBreakpointsAsync(Guid runId, IReadOnlyCollection<string> nodeIds);
    Task StepRunAsync(Guid runId);
}

/// <summary>Reports whether node stepping has one unambiguous next node at each graph boundary.</summary>
public static class WorkflowDebugging
{
    private static readonly HashSet<string> UnsupportedNodeTypes = new(StringComparer.Ordinal)
    {
        NodeTypes.Condition, NodeTypes.BooleanBranch, NodeTypes.Merge,
        NodeTypes.ParallelFork, NodeTypes.ParallelBranch, NodeTypes.ParallelJoin, NodeTypes.ParallelRendezvous,
        NodeTypes.Sequence, NodeTypes.Loop, NodeTypes.ForEach, NodeTypes.LoopBody,
        NodeTypes.WorkflowCall, NodeTypes.WorkflowInterfaceCall, NodeTypes.Wait
    };

    public static bool SupportsBreakpoint(WorkflowDefinition definition, string nodeId) =>
        definition.Nodes.TryGetValue(nodeId, out var node) &&
        (nodeId == definition.EntryNodeId || WorkflowPorts.ExplicitExecutionInputs(node).Length > 0);

    public static bool SupportsNodeStepping(WorkflowDefinition definition) =>
        !definition.Nodes.Values.Any(node => UnsupportedNodeTypes.Contains(node.Type) ||
            node.Type == NodeTypes.Agent && node.Agent?.Routes is { IsDefaultOrEmpty: false });
}

/// <summary>Trusted host gate before a node is admitted, and before final completion (null node ID).
/// Cancellation must unblock waits. Implementations must not execute or reconstruct workflow nodes.</summary>
public interface IWorkflowExecutionGate
{
    ValueTask WaitForBoundaryAsync(string? nextNodeId, CancellationToken cancellationToken);
}

public sealed class WorkflowRunControlException()
    : InvalidOperationException("The run is terminal or its execution is no longer owned by this runtime.");
