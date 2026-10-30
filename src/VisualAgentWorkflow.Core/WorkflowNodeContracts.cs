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
    SequenceProgress,
    ChildWorkflow,
    VariableState
}

[Flags]
public enum WorkflowNodeEffectFlags
{
    None = 0,
    RunScopedState = 1,
    HostInteraction = 2,
    ExternalCapability = 4,
    ChildWorkflow = 8
}

[Flags]
public enum WorkflowNodeAuthoringFlags
{
    None = 0,
    Configurable = 1,
    DynamicPins = 2,
    ContextInsertable = 4,
    InlineEditable = 8
}

public enum WorkflowDynamicPinIdentityKind
{
    None,
    AgentRouteId,
    McpArgumentId,
    ParallelBranchId,
    SequenceOutputId,
    GraphFunctionPortId
}

public sealed record WorkflowNodeLimit(string Id, int Maximum, string Description);

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
    bool SupportsCancellation = true,
    WorkflowDynamicPinIdentityKind DynamicPinIdentity = WorkflowDynamicPinIdentityKind.None)
{
    public ImmutableArray<WorkflowNodeLimit> Limits { get; init; } = [];

    /// <summary>
    /// Advisory description of the kind of state or host boundary this node may touch.
    /// It grants no authority and does not imply idempotence, rollback, retry safety,
    /// durability, or exactly-once external effects.
    /// </summary>
    public WorkflowNodeEffectFlags Effects { get; init; }

    /// <summary>
    /// Stable authoring capabilities understood by Core-aware editors. These flags do
    /// not name UI components or decide schema admission.
    /// </summary>
    public WorkflowNodeAuthoringFlags Authoring { get; init; }
}

public sealed record WorkflowNodeContractIssue(string Code, string NodeType, string Message);

/// <summary>Checks the non-authoritative metadata invariants of the canonical node catalog.</summary>
public static class WorkflowNodeContractConformance
{
    public static ImmutableArray<WorkflowNodeContractIssue> ValidateCatalog()
    {
        var issues = ImmutableArray.CreateBuilder<WorkflowNodeContractIssue>();
        foreach (var contract in WorkflowNodeContractCatalog.All)
            issues.AddRange(Validate(contract));

        foreach (var duplicate in WorkflowNodeContractCatalog.All.GroupBy(contract => contract.Type, StringComparer.Ordinal).Where(group => group.Count() > 1))
            issues.Add(new("metadata.duplicate", duplicate.Key, "Node contract types must be unique in the canonical catalog."));

        return issues.ToImmutable();
    }

    /// <summary>Validates one non-authoritative node contract, including lifecycle semantics.</summary>
    public static ImmutableArray<WorkflowNodeContractIssue> Validate(WorkflowNodeContract contract)
    {
        ArgumentNullException.ThrowIfNull(contract);
        var issues = ImmutableArray.CreateBuilder<WorkflowNodeContractIssue>();
        void Issue(string code, WorkflowNodeContract value, string message) => issues.Add(new(code, value.Type, message));

        if (string.IsNullOrWhiteSpace(contract.Type) || string.IsNullOrWhiteSpace(contract.TypeVersion) ||
            string.IsNullOrWhiteSpace(contract.DisplayName) || string.IsNullOrWhiteSpace(contract.Category) ||
            string.IsNullOrWhiteSpace(contract.Description))
            Issue("metadata.required", contract, "Node contracts require stable type, version, display name, category, and description metadata.");
        if (!IsDefined(contract.Effects) || !IsDefined(contract.Authoring))
            Issue("metadata.flags", contract, "Node contract metadata contains an undefined effect or authoring flag.");
        if (contract.Authoring == WorkflowNodeAuthoringFlags.None)
            Issue("metadata.authoring", contract, "Every node contract must declare at least one stable authoring capability.");
        if (contract.IsPure && contract.Effects != WorkflowNodeEffectFlags.None)
            Issue("metadata.pure-effects", contract, "Pure nodes cannot declare run, host, external-capability, or child-workflow effects.");
        if (contract.HasDynamicPins && !contract.Authoring.HasFlag(WorkflowNodeAuthoringFlags.DynamicPins))
            Issue("metadata.dynamic-pins", contract, "Dynamic-pin node contracts must expose the dynamic-pins authoring capability.");
        if (contract.Type is NodeTypes.Agent or NodeTypes.McpTool && !contract.Effects.HasFlag(WorkflowNodeEffectFlags.ExternalCapability))
            Issue("metadata.capability-effect", contract, "Host-authorized capability nodes must declare an external-capability effect.");
        if (contract.Type == NodeTypes.Approval && !contract.Effects.HasFlag(WorkflowNodeEffectFlags.HostInteraction))
            Issue("metadata.approval-effect", contract, "Approval nodes must declare a host-interaction effect.");
        if (contract.Type == NodeTypes.WorkflowCall && !contract.Effects.HasFlag(WorkflowNodeEffectFlags.ChildWorkflow))
            Issue("metadata.child-effect", contract, "Workflow-call nodes must declare a child-workflow effect.");

        ValidateLifecycle(contract, Issue);
        return issues.ToImmutable();
    }

    private static void ValidateLifecycle(WorkflowNodeContract contract,
        Action<string, WorkflowNodeContract, string> issue)
    {
        void Expect<T>(string code, T actual, T expected, string field) where T : notnull
        {
            if (!EqualityComparer<T>.Default.Equals(actual, expected))
                issue(code, contract, $"{contract.Type} must declare {field} as {expected}.");
        }

        if (contract.IsPure)
        {
            Expect("metadata.pure-activation", contract.Activation, WorkflowNodeActivationKind.DataEvaluation, "data-evaluation activation");
            Expect("metadata.pure-state", contract.State, WorkflowNodeStateKind.Stateless, "stateless state");
            Expect("metadata.pure-evidence", contract.Evidence, WorkflowNodeEvidenceKind.DataEvaluation, "data-evaluation evidence");
        }

        switch (contract.Type)
        {
            case NodeTypes.Condition:
            case NodeTypes.BooleanBranch:
                Expect("metadata.route-failure", contract.Failure, WorkflowNodeFailureKind.Routes, "route failure disposition");
                Expect("metadata.route-evidence", contract.Evidence, WorkflowNodeEvidenceKind.RouteDecision, "route-decision evidence");
                break;
            case NodeTypes.Terminate:
                Expect("metadata.terminal-failure", contract.Failure, WorkflowNodeFailureKind.Terminates, "terminal failure disposition");
                break;
            case NodeTypes.Approval:
                Expect("metadata.approval-activation", contract.Activation, WorkflowNodeActivationKind.ExternalApproval, "external-approval activation");
                Expect("metadata.approval-state", contract.State, WorkflowNodeStateKind.HostOwned, "host-owned state");
                Expect("metadata.approval-failure", contract.Failure, WorkflowNodeFailureKind.WaitsForHost, "host-wait failure disposition");
                Expect("metadata.approval-evidence", contract.Evidence, WorkflowNodeEvidenceKind.Approval, "approval evidence");
                break;
            case NodeTypes.ParallelFork:
            case NodeTypes.Sequence:
                Expect("metadata.fanout-activation", contract.Activation, WorkflowNodeActivationKind.SchedulerFanOut, "scheduler-fan-out activation");
                break;
            case NodeTypes.ParallelJoin:
            case NodeTypes.ParallelRendezvous:
                Expect("metadata.barrier-activation", contract.Activation, WorkflowNodeActivationKind.SchedulerBarrier, "scheduler-barrier activation");
                Expect("metadata.barrier-failure", contract.Failure, WorkflowNodeFailureKind.AbortsRegion, "region-abort failure disposition");
                break;
            case NodeTypes.Agent:
            case NodeTypes.McpTool:
                Expect("metadata.capability-state", contract.State, WorkflowNodeStateKind.HostOwned, "host-owned state");
                Expect("metadata.capability-evidence", contract.Evidence, WorkflowNodeEvidenceKind.CapabilityInvocation, "capability-invocation evidence");
                break;
            case NodeTypes.WorkflowCall:
                Expect("metadata.child-activation", contract.Activation, WorkflowNodeActivationKind.ChildWorkflow, "child-workflow activation");
                Expect("metadata.child-state", contract.State, WorkflowNodeStateKind.RunScoped, "run-scoped state");
                Expect("metadata.child-failure", contract.Failure, WorkflowNodeFailureKind.PropagatesChildOutcome, "child-outcome failure disposition");
                Expect("metadata.child-evidence", contract.Evidence, WorkflowNodeEvidenceKind.ChildWorkflow, "child-workflow evidence");
                break;
        }
    }

    private static bool IsDefined(WorkflowNodeEffectFlags flags) =>
        (flags & ~(
            WorkflowNodeEffectFlags.RunScopedState |
            WorkflowNodeEffectFlags.HostInteraction |
            WorkflowNodeEffectFlags.ExternalCapability |
            WorkflowNodeEffectFlags.ChildWorkflow)) == 0;

    private static bool IsDefined(WorkflowNodeAuthoringFlags flags) =>
        (flags & ~(
            WorkflowNodeAuthoringFlags.Configurable |
            WorkflowNodeAuthoringFlags.DynamicPins |
            WorkflowNodeAuthoringFlags.ContextInsertable |
            WorkflowNodeAuthoringFlags.InlineEditable)) == 0;
}

/// <summary>Canonical node-family metadata shared by Core-aware authoring surfaces.</summary>
public static class WorkflowNodeContractCatalog
{
    public static ImmutableArray<WorkflowNodeContract> All { get; } =
    [
        Contract(NodeTypes.Input, "Input", "Flow", "Starts a workflow with caller data.", true, true, effects: WorkflowNodeEffectFlags.RunScopedState, authoring: WorkflowNodeAuthoringFlags.Configurable | WorkflowNodeAuthoringFlags.ContextInsertable),
        Contract(NodeTypes.Output, "Output", "Flow", "Completes a workflow with a typed result.", true, true, effects: WorkflowNodeEffectFlags.RunScopedState),
        Contract(NodeTypes.Uppercase, "Uppercase text", "Text", "Converts text to uppercase.", false, true, pure: true, activation: WorkflowNodeActivationKind.DataEvaluation, evidence: WorkflowNodeEvidenceKind.DataEvaluation),
        Contract(NodeTypes.SequenceUppercase, "Sequence uppercase", "Text", "Uppercases the incoming text as a bounded Sequence path step.", true, true, pure: false, activation: WorkflowNodeActivationKind.Execution, evidence: WorkflowNodeEvidenceKind.ExecutionLifecycle),
        Contract(NodeTypes.SequenceReverse, "Sequence reverse", "Text", "Reverses the incoming text as a bounded Sequence path step.", true, true, pure: false, activation: WorkflowNodeActivationKind.Execution, evidence: WorkflowNodeEvidenceKind.ExecutionLifecycle),
        Contract(NodeTypes.Reverse, "Reverse text", "Text", "Reverses text deterministically.", false, true, pure: true, activation: WorkflowNodeActivationKind.DataEvaluation, evidence: WorkflowNodeEvidenceKind.DataEvaluation),
        Contract(NodeTypes.StringConstant, "String constant", "Values", "Provides a literal string value.", false, true, pure: true, activation: WorkflowNodeActivationKind.DataEvaluation, evidence: WorkflowNodeEvidenceKind.DataEvaluation),
        Contract(NodeTypes.BooleanConstant, "Boolean constant", "Values", "Provides a literal Boolean value.", false, true, pure: true, activation: WorkflowNodeActivationKind.DataEvaluation, evidence: WorkflowNodeEvidenceKind.DataEvaluation),
        Contract(NodeTypes.IntegerConstant, "Integer constant", "Values", "Provides a literal integer value.", false, true, pure: true, activation: WorkflowNodeActivationKind.DataEvaluation, evidence: WorkflowNodeEvidenceKind.DataEvaluation),
        Contract(NodeTypes.NumberConstant, "Number constant", "Values", "Provides a finite IEEE-754 double value.", false, true, pure: true, activation: WorkflowNodeActivationKind.DataEvaluation, evidence: WorkflowNodeEvidenceKind.DataEvaluation),
        Contract(NodeTypes.NumberAdd, "Add numbers", "Values", "Adds two finite IEEE-754 double values.", false, true, pure: true, activation: WorkflowNodeActivationKind.DataEvaluation, evidence: WorkflowNodeEvidenceKind.DataEvaluation),
        Contract(NodeTypes.StringToInteger, "String to integer", "Values", "Converts strict ASCII decimal text to a signed 64-bit integer.", false, true, pure: true, activation: WorkflowNodeActivationKind.DataEvaluation, evidence: WorkflowNodeEvidenceKind.DataEvaluation),
        Contract(NodeTypes.TextConcat, "Concatenate text", "Text", "Joins two string inputs deterministically.", false, true, pure: true, activation: WorkflowNodeActivationKind.DataEvaluation, evidence: WorkflowNodeEvidenceKind.DataEvaluation),
        Contract(NodeTypes.StringEquals, "Compare strings", "Text", "Compares two strings using ordinal equality.", false, true, pure: true, activation: WorkflowNodeActivationKind.DataEvaluation, evidence: WorkflowNodeEvidenceKind.DataEvaluation),
        Contract(NodeTypes.IntegerAdd, "Add integers", "Values", "Adds two checked integer inputs.", false, true, pure: true, activation: WorkflowNodeActivationKind.DataEvaluation, evidence: WorkflowNodeEvidenceKind.DataEvaluation),
        Contract(NodeTypes.IntegerEquals, "Compare integers", "Values", "Compares two integer inputs.", false, true, pure: true, activation: WorkflowNodeActivationKind.DataEvaluation, evidence: WorkflowNodeEvidenceKind.DataEvaluation),
        Contract(NodeTypes.BooleanNot, "Boolean NOT", "Values", "Inverts a Boolean input.", false, true, pure: true, activation: WorkflowNodeActivationKind.DataEvaluation, evidence: WorkflowNodeEvidenceKind.DataEvaluation),
        Contract(NodeTypes.JsonSelectString, "Select JSON text", "JSON", "Selects a bounded JSON pointer as text.", false, true, pure: true, activation: WorkflowNodeActivationKind.DataEvaluation, evidence: WorkflowNodeEvidenceKind.DataEvaluation),
        Contract(NodeTypes.JsonProject, "Project JSON fields", "JSON", "Projects bounded typed JSON fields.", false, true, pure: true, activation: WorkflowNodeActivationKind.DataEvaluation, evidence: WorkflowNodeEvidenceKind.DataEvaluation),
        Contract(NodeTypes.JsonMerge, "Merge JSON objects", "JSON", "Combines two bounded JSON objects without implicit key precedence.", false, true, pure: true, activation: WorkflowNodeActivationKind.DataEvaluation, evidence: WorkflowNodeEvidenceKind.DataEvaluation),
        Contract(NodeTypes.JsonToArray, "JSON to array", "Containers", "Refines a root JSON array into a bounded immutable array value.", false, true, pure: true, activation: WorkflowNodeActivationKind.DataEvaluation, evidence: WorkflowNodeEvidenceKind.DataEvaluation),
        Contract(NodeTypes.ArrayLength, "Array length", "Containers", "Returns the number of items in a bounded immutable JSON array.", false, true, pure: true, activation: WorkflowNodeActivationKind.DataEvaluation, evidence: WorkflowNodeEvidenceKind.DataEvaluation),
        Contract(NodeTypes.TextTemplate, "Text template", "Text", "Builds text from structured literal and value parts.", false, true, pure: true, activation: WorkflowNodeActivationKind.DataEvaluation, evidence: WorkflowNodeEvidenceKind.DataEvaluation),
        Contract(NodeTypes.WorkflowValue, "Workflow parameter", "Values", "Reads an immutable workflow parameter.", false, true, pure: true, activation: WorkflowNodeActivationKind.DataEvaluation, evidence: WorkflowNodeEvidenceKind.DataEvaluation),
        Contract(NodeTypes.Condition, "String condition", "Flow", "Routes a string through a bounded predicate.", true, true, effects: WorkflowNodeEffectFlags.RunScopedState, failure: WorkflowNodeFailureKind.Routes, evidence: WorkflowNodeEvidenceKind.RouteDecision),
        Contract(NodeTypes.BooleanBranch, "Boolean branch", "Flow", "Routes execution using a Boolean value.", true, true, effects: WorkflowNodeEffectFlags.RunScopedState, failure: WorkflowNodeFailureKind.Routes, evidence: WorkflowNodeEvidenceKind.RouteDecision),
        Contract(NodeTypes.Merge, "Merge", "Flow", "Reconverges compatible execution paths.", true, false, effects: WorkflowNodeEffectFlags.RunScopedState, evidence: WorkflowNodeEvidenceKind.ExecutionLifecycle),
        Contract(NodeTypes.Terminate, "Terminate", "Flow", "Ends a route with an explicit outcome.", true, true, effects: WorkflowNodeEffectFlags.RunScopedState, failure: WorkflowNodeFailureKind.Terminates),
        Contract(NodeTypes.Approval, "Approval", "Interaction", "Waits for an explicit human approval boundary.", true, true, effects: WorkflowNodeEffectFlags.RunScopedState | WorkflowNodeEffectFlags.HostInteraction, requiresHostCapability: true, activation: WorkflowNodeActivationKind.ExternalApproval, state: WorkflowNodeStateKind.HostOwned, failure: WorkflowNodeFailureKind.WaitsForHost, evidence: WorkflowNodeEvidenceKind.Approval),
        Contract(NodeTypes.ParallelFork, "Parallel fork", "Flow", "Starts a named bounded parallel region.", true, true, effects: WorkflowNodeEffectFlags.RunScopedState, hasDynamicPins: true, dynamicPinIdentity: WorkflowDynamicPinIdentityKind.ParallelBranchId, limits: [new("branch-count", ParallelGraphLimits.MaximumExplicitBranches, "Maximum named branches in the region.")], activation: WorkflowNodeActivationKind.SchedulerFanOut, state: WorkflowNodeStateKind.RunScoped, evidence: WorkflowNodeEvidenceKind.ParallelProgress),
        Contract(NodeTypes.ParallelBranch, "Parallel branch", "Flow", "Anchors one named parallel branch.", true, true, effects: WorkflowNodeEffectFlags.RunScopedState, state: WorkflowNodeStateKind.RunScoped, evidence: WorkflowNodeEvidenceKind.ParallelProgress),
        Contract(NodeTypes.ParallelJoin, "Parallel join", "Flow", "Waits for named parallel branch results.", true, true, effects: WorkflowNodeEffectFlags.RunScopedState, hasDynamicPins: true, dynamicPinIdentity: WorkflowDynamicPinIdentityKind.ParallelBranchId, limits: [new("branch-count", ParallelGraphLimits.MaximumExplicitBranches, "Maximum named branches accepted by the barrier.")], activation: WorkflowNodeActivationKind.SchedulerBarrier, state: WorkflowNodeStateKind.RunScoped, failure: WorkflowNodeFailureKind.AbortsRegion, evidence: WorkflowNodeEvidenceKind.ParallelProgress),
        Contract(NodeTypes.ParallelRendezvous, "Collaboration rendezvous", "Collaboration", "Publishes or reads a bounded branch snapshot.", true, true, effects: WorkflowNodeEffectFlags.RunScopedState | WorkflowNodeEffectFlags.ExternalCapability, hasDynamicPins: true, dynamicPinIdentity: WorkflowDynamicPinIdentityKind.ParallelBranchId, requiresHostCapability: true, limits: [new("branch-count", ParallelGraphLimits.MaximumExplicitBranches, "Maximum named branches accepted by the rendezvous.")], activation: WorkflowNodeActivationKind.SchedulerBarrier, state: WorkflowNodeStateKind.RunScoped, failure: WorkflowNodeFailureKind.AbortsRegion, evidence: WorkflowNodeEvidenceKind.ParallelProgress),
        Contract(NodeTypes.Sequence, "Sequence", "Flow", "Activates ordered execution outputs without waiting for earlier branches to complete.", true, false, effects: WorkflowNodeEffectFlags.RunScopedState, hasDynamicPins: true, dynamicPinIdentity: WorkflowDynamicPinIdentityKind.SequenceOutputId, limits: [new("output-count", SequenceOutputLimits.MaximumOutputs, "Maximum ordered outputs in the sequence.")], activation: WorkflowNodeActivationKind.SchedulerFanOut, state: WorkflowNodeStateKind.RunScoped, evidence: WorkflowNodeEvidenceKind.SequenceProgress),
        Contract(NodeTypes.Loop, "Bounded loop", "Flow", "Repeats a linear body up to a hard iteration limit.", true, true, effects: WorkflowNodeEffectFlags.RunScopedState, state: WorkflowNodeStateKind.RunScoped, limits: [new("iteration-count", WorkflowLoopLimits.MaximumIterations, "Maximum body iterations.")], failure: WorkflowNodeFailureKind.Routes, evidence: WorkflowNodeEvidenceKind.RouteDecision),
        Contract(NodeTypes.LoopBody, "Loop body", "Flow", "Marks the body entry of a bounded loop.", true, true, effects: WorkflowNodeEffectFlags.RunScopedState, state: WorkflowNodeStateKind.RunScoped, evidence: WorkflowNodeEvidenceKind.ExecutionLifecycle),
        Contract(NodeTypes.Agent, "Agent", "Capabilities", "Invokes a host-authorized model agent.", true, true, effects: WorkflowNodeEffectFlags.RunScopedState | WorkflowNodeEffectFlags.ExternalCapability, hasDynamicPins: true, dynamicPinIdentity: WorkflowDynamicPinIdentityKind.AgentRouteId, requiresHostCapability: true, limits: [new("route-count", AgentExecutionRouteLimits.MaximumRoutes, "Maximum authored execution routes."), new("output-tokens", AgentOutputTokenLimits.Maximum, "Maximum configured response tokens."), new("total-tokens", AgentTotalTokenLimits.Maximum, "Maximum provider-reported total tokens."), new("function-tools", AgentGraphFunctionToolLimits.MaximumAssignedFunctions, "Maximum assigned pure graph-function tools.")], state: WorkflowNodeStateKind.HostOwned, evidence: WorkflowNodeEvidenceKind.CapabilityInvocation),
        Contract(NodeTypes.McpTool, "MCP tool", "Capabilities", "Invokes one host-authorized MCP tool.", true, true, effects: WorkflowNodeEffectFlags.RunScopedState | WorkflowNodeEffectFlags.ExternalCapability, hasDynamicPins: true, dynamicPinIdentity: WorkflowDynamicPinIdentityKind.McpArgumentId, requiresHostCapability: true, limits: [new("arguments-json-characters", 65_536, "Maximum static argument JSON characters.")], state: WorkflowNodeStateKind.HostOwned, evidence: WorkflowNodeEvidenceKind.CapabilityInvocation),
        Contract(NodeTypes.WorkflowCall, "Workflow call", "Composition", "Calls an exact pinned child workflow revision.", true, true, effects: WorkflowNodeEffectFlags.RunScopedState | WorkflowNodeEffectFlags.ChildWorkflow, limits: [new("nested-depth", WorkflowCallLimits.MaximumDepth, "Maximum nested workflow-call depth."), new("expanded-nodes", WorkflowCallLimits.MaximumExpandedNodes, "Maximum expanded nodes across a call tree.")], activation: WorkflowNodeActivationKind.ChildWorkflow, state: WorkflowNodeStateKind.RunScoped, failure: WorkflowNodeFailureKind.PropagatesChildOutcome, evidence: WorkflowNodeEvidenceKind.ChildWorkflow),
        Contract(NodeTypes.FunctionInput, "Function input", "Functions", "Declares a pure graph-function input.", false, true, pure: true, activation: WorkflowNodeActivationKind.DataEvaluation, evidence: WorkflowNodeEvidenceKind.DataEvaluation),
        Contract(NodeTypes.FunctionOutput, "Function output", "Functions", "Declares a pure graph-function output.", false, true, pure: true, activation: WorkflowNodeActivationKind.DataEvaluation, evidence: WorkflowNodeEvidenceKind.DataEvaluation),
        Contract(NodeTypes.FunctionCall, "Function call", "Functions", "Evaluates an exact pure graph-function revision.", false, true, pure: true, hasDynamicPins: true, dynamicPinIdentity: WorkflowDynamicPinIdentityKind.GraphFunctionPortId, limits: [new("body-nodes", GraphFunctionLimits.MaximumNodes, "Maximum nodes in the pinned pure function body."), new("node-visits", GraphFunctionLimits.MaximumRunNodeVisits, "Maximum evaluated function-node visits per run.")], activation: WorkflowNodeActivationKind.DataEvaluation, evidence: WorkflowNodeEvidenceKind.DataEvaluation),
        Contract(NodeTypes.VariableGet, "Get variable", "Variables", "Reads a run-scoped scalar variable at an execution point.", true, true, effects: WorkflowNodeEffectFlags.RunScopedState, state: WorkflowNodeStateKind.RunScoped, evidence: WorkflowNodeEvidenceKind.VariableState),
        Contract(NodeTypes.VariableSet, "Set variable", "Variables", "Commits a scalar value to a run-scoped variable.", true, true, effects: WorkflowNodeEffectFlags.RunScopedState, state: WorkflowNodeStateKind.RunScoped, evidence: WorkflowNodeEvidenceKind.VariableState),
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
        WorkflowDynamicPinIdentityKind dynamicPinIdentity = WorkflowDynamicPinIdentityKind.None,
        WorkflowNodeActivationKind activation = WorkflowNodeActivationKind.Execution,
        WorkflowNodeStateKind state = WorkflowNodeStateKind.Stateless,
        WorkflowNodeFailureKind failure = WorkflowNodeFailureKind.Propagates,
        WorkflowNodeEvidenceKind evidence = WorkflowNodeEvidenceKind.ExecutionLifecycle,
        bool supportsCancellation = true,
        ImmutableArray<WorkflowNodeLimit> limits = default,
        WorkflowNodeEffectFlags effects = WorkflowNodeEffectFlags.None,
        WorkflowNodeAuthoringFlags authoring = WorkflowNodeAuthoringFlags.ContextInsertable)
    {
        var contract = new WorkflowNodeContract(type, "1.0.0", displayName, category, description, hasExecutionPins, hasDataPins, pure,
            hasDynamicPins, requiresHostCapability, activation, state, failure, evidence, supportsCancellation, dynamicPinIdentity);
        if (hasDynamicPins) authoring |= WorkflowNodeAuthoringFlags.DynamicPins;
        if (pure) authoring |= WorkflowNodeAuthoringFlags.InlineEditable;
        return contract with { Limits = limits.IsDefault ? [] : limits, Effects = effects, Authoring = authoring };
    }
}
