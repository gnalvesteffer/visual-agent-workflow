using System.Collections.Immutable;

namespace VisualAgentWorkflow.Core;

public enum WorkflowNodeActivationKind
{
    DataEvaluation,
    Execution,
    ExternalApproval,
    SchedulerFanOut,
    SchedulerBarrier,
    ChildWorkflow
}

public enum WorkflowNodeStateKind
{
    Stateless,
    RunScoped,
    HostOwned
}

public enum WorkflowNodeFailureKind
{
    Propagates,
    Routes,
    Terminates,
    WaitsForHost,
    AbortsRegion,
    PropagatesChildOutcome
}

public enum WorkflowNodeEvidenceKind
{
    DataEvaluation,
    ExecutionLifecycle,
    RouteDecision,
    CapabilityInvocation,
    Approval,
    ParallelProgress,
    ChildWorkflow
}

/// <summary>Stable, framework-independent metadata for a workflow node family.</summary>
/// <remarks>
/// This catalog describes authoring and contract boundaries only. It does not replace
/// version-specific validation or grant host authority to a workflow.
/// </remarks>
public sealed record WorkflowNodeContract(
    string Type,
    string TypeVersion,
    string DisplayName,
    string Category,
    string Description,
    bool HasExecutionPins,
    bool HasDataPins,
    bool IsPure,
    bool HasDynamicPins,
    bool RequiresHostCapability,
    WorkflowNodeActivationKind Activation = WorkflowNodeActivationKind.Execution,
    WorkflowNodeStateKind State = WorkflowNodeStateKind.Stateless,
    WorkflowNodeFailureKind Failure = WorkflowNodeFailureKind.Propagates,
    WorkflowNodeEvidenceKind Evidence = WorkflowNodeEvidenceKind.ExecutionLifecycle,
    bool SupportsCancellation = true);

/// <summary>Canonical node-family metadata shared by Core-aware authoring surfaces.</summary>
public static class WorkflowNodeContractCatalog
{
    public static ImmutableArray<WorkflowNodeContract> All { get; } =
    [
        Contract(NodeTypes.Input, "Input", "Flow", "Starts a workflow with caller data.", true, true, evidence: WorkflowNodeEvidenceKind.ExecutionLifecycle),
        Contract(NodeTypes.Output, "Output", "Flow", "Completes a workflow with a typed result.", true, true),
        Contract(NodeTypes.Uppercase, "Uppercase text", "Text", "Converts text to uppercase.", false, true, pure: true, activation: WorkflowNodeActivationKind.DataEvaluation, evidence: WorkflowNodeEvidenceKind.DataEvaluation),
        Contract(NodeTypes.Reverse, "Reverse text", "Text", "Reverses text deterministically.", false, true, pure: true, activation: WorkflowNodeActivationKind.DataEvaluation, evidence: WorkflowNodeEvidenceKind.DataEvaluation),
        Contract(NodeTypes.StringConstant, "String constant", "Values", "Provides a literal string value.", false, true, pure: true, activation: WorkflowNodeActivationKind.DataEvaluation, evidence: WorkflowNodeEvidenceKind.DataEvaluation),
        Contract(NodeTypes.BooleanConstant, "Boolean constant", "Values", "Provides a literal Boolean value.", false, true, pure: true, activation: WorkflowNodeActivationKind.DataEvaluation, evidence: WorkflowNodeEvidenceKind.DataEvaluation),
        Contract(NodeTypes.IntegerConstant, "Integer constant", "Values", "Provides a literal integer value.", false, true, pure: true, activation: WorkflowNodeActivationKind.DataEvaluation, evidence: WorkflowNodeEvidenceKind.DataEvaluation),
        Contract(NodeTypes.TextConcat, "Concatenate text", "Text", "Joins two string inputs deterministically.", false, true, pure: true, activation: WorkflowNodeActivationKind.DataEvaluation, evidence: WorkflowNodeEvidenceKind.DataEvaluation),
        Contract(NodeTypes.StringEquals, "Compare strings", "Text", "Compares two strings using ordinal equality.", false, true, pure: true, activation: WorkflowNodeActivationKind.DataEvaluation, evidence: WorkflowNodeEvidenceKind.DataEvaluation),
        Contract(NodeTypes.IntegerAdd, "Add integers", "Values", "Adds two checked integer inputs.", false, true, pure: true, activation: WorkflowNodeActivationKind.DataEvaluation, evidence: WorkflowNodeEvidenceKind.DataEvaluation),
        Contract(NodeTypes.IntegerEquals, "Compare integers", "Values", "Compares two integer inputs.", false, true, pure: true, activation: WorkflowNodeActivationKind.DataEvaluation, evidence: WorkflowNodeEvidenceKind.DataEvaluation),
        Contract(NodeTypes.BooleanNot, "Boolean NOT", "Values", "Inverts a Boolean input.", false, true, pure: true, activation: WorkflowNodeActivationKind.DataEvaluation, evidence: WorkflowNodeEvidenceKind.DataEvaluation),
        Contract(NodeTypes.JsonSelectString, "Select JSON text", "JSON", "Selects a bounded JSON pointer as text.", false, true, pure: true, activation: WorkflowNodeActivationKind.DataEvaluation, evidence: WorkflowNodeEvidenceKind.DataEvaluation),
        Contract(NodeTypes.JsonProject, "Project JSON fields", "JSON", "Projects bounded typed JSON fields.", false, true, pure: true, activation: WorkflowNodeActivationKind.DataEvaluation, evidence: WorkflowNodeEvidenceKind.DataEvaluation),
        Contract(NodeTypes.TextTemplate, "Text template", "Text", "Builds text from structured literal and value parts.", false, true, pure: true, activation: WorkflowNodeActivationKind.DataEvaluation, evidence: WorkflowNodeEvidenceKind.DataEvaluation),
        Contract(NodeTypes.WorkflowValue, "Workflow parameter", "Values", "Reads an immutable workflow parameter.", false, true, pure: true, activation: WorkflowNodeActivationKind.DataEvaluation, evidence: WorkflowNodeEvidenceKind.DataEvaluation),
        Contract(NodeTypes.Condition, "String condition", "Flow", "Routes a string through a bounded predicate.", true, true, failure: WorkflowNodeFailureKind.Routes, evidence: WorkflowNodeEvidenceKind.RouteDecision),
        Contract(NodeTypes.BooleanBranch, "Boolean branch", "Flow", "Routes execution using a Boolean value.", true, true, failure: WorkflowNodeFailureKind.Routes, evidence: WorkflowNodeEvidenceKind.RouteDecision),
        Contract(NodeTypes.Merge, "Merge", "Flow", "Reconverges compatible execution paths.", true, false, evidence: WorkflowNodeEvidenceKind.ExecutionLifecycle),
        Contract(NodeTypes.Terminate, "Terminate", "Flow", "Ends a route with an explicit outcome.", true, true, failure: WorkflowNodeFailureKind.Terminates),
        Contract(NodeTypes.Approval, "Approval", "Interaction", "Waits for an explicit human approval boundary.", true, true, requiresHostCapability: true, activation: WorkflowNodeActivationKind.ExternalApproval, state: WorkflowNodeStateKind.HostOwned, failure: WorkflowNodeFailureKind.WaitsForHost, evidence: WorkflowNodeEvidenceKind.Approval),
        Contract(NodeTypes.ParallelFork, "Parallel fork", "Flow", "Starts a named bounded parallel region.", true, true, activation: WorkflowNodeActivationKind.SchedulerFanOut, state: WorkflowNodeStateKind.RunScoped, evidence: WorkflowNodeEvidenceKind.ParallelProgress),
        Contract(NodeTypes.ParallelBranch, "Parallel branch", "Flow", "Anchors one named parallel branch.", true, true, state: WorkflowNodeStateKind.RunScoped, evidence: WorkflowNodeEvidenceKind.ParallelProgress),
        Contract(NodeTypes.ParallelJoin, "Parallel join", "Flow", "Waits for named parallel branch results.", true, true, activation: WorkflowNodeActivationKind.SchedulerBarrier, state: WorkflowNodeStateKind.RunScoped, failure: WorkflowNodeFailureKind.AbortsRegion, evidence: WorkflowNodeEvidenceKind.ParallelProgress),
        Contract(NodeTypes.ParallelRendezvous, "Collaboration rendezvous", "Collaboration", "Publishes or reads a bounded branch snapshot.", true, true, requiresHostCapability: true, activation: WorkflowNodeActivationKind.SchedulerBarrier, state: WorkflowNodeStateKind.RunScoped, failure: WorkflowNodeFailureKind.AbortsRegion, evidence: WorkflowNodeEvidenceKind.ParallelProgress),
        Contract(NodeTypes.Loop, "Bounded loop", "Flow", "Repeats a linear body up to a hard iteration limit.", true, true, state: WorkflowNodeStateKind.RunScoped, failure: WorkflowNodeFailureKind.Routes, evidence: WorkflowNodeEvidenceKind.RouteDecision),
        Contract(NodeTypes.LoopBody, "Loop body", "Flow", "Marks the body entry of a bounded loop.", true, true, state: WorkflowNodeStateKind.RunScoped, evidence: WorkflowNodeEvidenceKind.ExecutionLifecycle),
        Contract(NodeTypes.Agent, "Agent", "Capabilities", "Invokes a host-authorized model agent.", true, true, hasDynamicPins: true, requiresHostCapability: true, state: WorkflowNodeStateKind.HostOwned, evidence: WorkflowNodeEvidenceKind.CapabilityInvocation),
        Contract(NodeTypes.McpTool, "MCP tool", "Capabilities", "Invokes one host-authorized MCP tool.", true, true, hasDynamicPins: true, requiresHostCapability: true, state: WorkflowNodeStateKind.HostOwned, evidence: WorkflowNodeEvidenceKind.CapabilityInvocation),
        Contract(NodeTypes.WorkflowCall, "Workflow call", "Composition", "Calls an exact pinned child workflow revision.", true, true, activation: WorkflowNodeActivationKind.ChildWorkflow, state: WorkflowNodeStateKind.RunScoped, failure: WorkflowNodeFailureKind.PropagatesChildOutcome, evidence: WorkflowNodeEvidenceKind.ChildWorkflow),
        Contract(NodeTypes.FunctionInput, "Function input", "Functions", "Declares a pure graph-function input.", false, true, pure: true, activation: WorkflowNodeActivationKind.DataEvaluation, evidence: WorkflowNodeEvidenceKind.DataEvaluation),
        Contract(NodeTypes.FunctionOutput, "Function output", "Functions", "Declares a pure graph-function output.", false, true, pure: true, activation: WorkflowNodeActivationKind.DataEvaluation, evidence: WorkflowNodeEvidenceKind.DataEvaluation),
        Contract(NodeTypes.FunctionCall, "Function call", "Functions", "Evaluates an exact pure graph-function revision.", false, true, pure: true, activation: WorkflowNodeActivationKind.DataEvaluation, evidence: WorkflowNodeEvidenceKind.DataEvaluation),
    ];

    public static WorkflowNodeContract Get(string type) =>
        All.FirstOrDefault(contract => string.Equals(contract.Type, type, StringComparison.Ordinal))
        ?? throw new KeyNotFoundException($"Unknown workflow node type '{type}'.");

    public static bool TryGet(string? type, out WorkflowNodeContract contract)
    {
        var found = All.FirstOrDefault(candidate => string.Equals(candidate.Type, type, StringComparison.Ordinal));
        contract = found!;
        return found is not null;
    }

    private static WorkflowNodeContract Contract(string type, string displayName, string category, string description,
        bool hasExecutionPins, bool hasDataPins, bool pure = false, bool hasDynamicPins = false,
        bool requiresHostCapability = false,
        WorkflowNodeActivationKind activation = WorkflowNodeActivationKind.Execution,
        WorkflowNodeStateKind state = WorkflowNodeStateKind.Stateless,
        WorkflowNodeFailureKind failure = WorkflowNodeFailureKind.Propagates,
        WorkflowNodeEvidenceKind evidence = WorkflowNodeEvidenceKind.ExecutionLifecycle,
        bool supportsCancellation = true) =>
        new(type, "1.0.0", displayName, category, description, hasExecutionPins, hasDataPins, pure,
            hasDynamicPins, requiresHostCapability, activation, state, failure, evidence, supportsCancellation);
}
