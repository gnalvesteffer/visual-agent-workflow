using System.Collections.Immutable;
using Microsoft.Agents.AI.Workflows;
using VisualAgentWorkflow.Core;

namespace VisualAgentWorkflow.AgentFramework;

internal enum SequenceWorkflowCallProofOutcome
{
    Success,
    Termination,
    Failure,
    Unknown,
    CapabilityDenied
}

public sealed partial class WorkflowRunner
{
    /// <summary>
    /// Runs a private Sequence-shaped plan through the production runner boundary.
    /// This is intentionally internal and cannot be created from persisted workflow content.
    /// </summary>
    internal Task<WorkflowExecutionResult> ExecuteSequenceProductionProofAsync(Guid runId, string input,
        Func<ExecutionEvent, ValueTask> record, string? terminalOutputId = null,
        SequenceTerminalState terminalState = SequenceTerminalState.Completed,
        IWorkflowExecutionGate? gate = null, CancellationToken cancellationToken = default)
    {
        var revision = SequenceProofRevision();
        var outputs = new[]
        {
            new SequenceProductionOutput("first", 0, "first", "sequence", "out", "sequence-proof:first"),
            new SequenceProductionOutput("second", 1, "second", "first", "out", "sequence-proof:second"),
            new SequenceProductionOutput("third", 2, "third", "second", "out", "sequence-proof:third")
        };
        if (terminalOutputId is not null && outputs.All(output => output.OutputId != terminalOutputId))
            throw new ArgumentException("The proof terminal output must be one of the declared outputs.", nameof(terminalOutputId));

        var plan = new SequenceProductionPlan("sequence", outputs, terminalOutputId, terminalState);
        return ExecuteCoreAsync(runId, revision, input, null, record, gate, cancellationToken,
            rootRevision: revision, sequencePlan: plan);
    }

    /// <summary>
    /// Runs the private Sequence descendant-termination proof. This verifies that a
    /// failure termination settles its Sequence activation through the production
    /// collector; it is not a public schema or authoring capability.
    /// </summary>
    internal Task<WorkflowExecutionResult> ExecuteSequenceTerminationProductionProofAsync(Guid runId, string input,
        Func<ExecutionEvent, ValueTask> record, CancellationToken cancellationToken = default)
    {
        var definition = new WorkflowDefinition(WorkflowSchemaVersions.SequencePathGraph,
            "sequence-termination-production-proof", "input",
            new Dictionary<string, NodeDefinition>
            {
                ["input"] = new(NodeTypes.Input, "1.0.0"),
                ["sequence"] = new(NodeTypes.Sequence, "1.0.0")
                {
                    Sequence = new([new("terminate", "Failure route")])
                },
                ["terminate"] = new(NodeTypes.Terminate, "1.0.0")
                {
                    Termination = new(WorkflowTerminationOutcomes.Failure)
                }
            }.ToImmutableSortedDictionary(),
            ImmutableSortedDictionary<string, ConnectionDefinition>.Empty)
        {
            ExecutionConnections = new Dictionary<string, ConnectionDefinition>
            {
                ["input-sequence"] = new(new("input", "then"), new("sequence", "execute"), "execution"),
                ["sequence-terminate"] = new(new("sequence", "then.terminate"), new("terminate", "execute"), "execution")
            }.ToImmutableSortedDictionary()
        };
        var revision = WorkflowRevision.CreateProofOnly(definition, "sequence-termination-production-proof");
        var plan = new SequenceProductionPlan("sequence", [
            new SequenceProductionOutput("terminate", 0, "terminate", "sequence", "then.terminate", "sequence-terminate")
        ], null, SequenceTerminalState.Completed, AllowSingleOutput: true);
        return ExecuteCoreAsync(runId, revision, input, null, record, null, cancellationToken,
            rootRevision: revision, sequencePlan: plan, skipValidationForInternalProof: true);
    }

    /// <summary>
    /// Exercises decision-event correlation through the production Sequence runner. The
    /// deliberately unvalidated graph is private proof input and does not enable branchable
    /// Sequence graphs in persisted workflows.
    /// </summary>
    internal Task<WorkflowExecutionResult> ExecuteSequenceBranchDecisionEvidenceProofAsync(Guid runId,
        string input, bool condition, Func<ExecutionEvent, ValueTask> record,
        CancellationToken cancellationToken = default)
        => ExecuteSequenceBranchDecisionEvidenceProofAsync(runId,
            CreateSequenceBranchDecisionEvidenceProofRevision(condition), input, record, cancellationToken);

    internal Task<WorkflowExecutionResult> ExecuteSequenceBranchDecisionEvidenceProofAsync(Guid runId,
        WorkflowRevision revision, string input, Func<ExecutionEvent, ValueTask> record,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(revision);
        return ExecuteSequenceBranchDecisionProofAsync(runId, revision, input, record, cancellationToken);
    }

    internal Task<WorkflowExecutionResult> ExecuteSequenceBranchDecisionToolOutcomeProofAsync(Guid runId,
        WorkflowRevision revision, string input, Func<ExecutionEvent, ValueTask> record,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(revision);
        return ExecuteSequenceBranchDecisionProofAsync(runId, revision, input, record, cancellationToken);
    }

    internal Task<WorkflowExecutionResult> ExecuteSequenceBranchDecisionCancellationProofAsync(Guid runId,
        WorkflowRevision revision, string input, Func<ExecutionEvent, ValueTask> record,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(revision);
        return ExecuteSequenceBranchDecisionProofAsync(runId, revision, input, record, cancellationToken);
    }

    private Task<WorkflowExecutionResult> ExecuteSequenceBranchDecisionProofAsync(Guid runId,
        WorkflowRevision revision, string input, Func<ExecutionEvent, ValueTask> record,
        CancellationToken cancellationToken)
    {
        var plan = new SequenceProductionPlan("sequence", [
            new SequenceProductionOutput("decision", 0, "branch", "sequence", "then.decision", "sequence-branch"),
            new SequenceProductionOutput("suffix", 1, "suffix-output", "sequence", "then.suffix", "sequence-suffix")
        ], null, SequenceTerminalState.Completed);
        var frame = new StructuredActivationFrame(Guid.NewGuid(), null, "array-loop", 0, input,
            ImmutableSortedDictionary<string, string>.Empty.WithComparers(StringComparer.Ordinal));
        var coordinator = new SequenceRuntimeCoordinator(Guid.NewGuid(), "sequence", ["decision", "suffix"],
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["decision"] = "sequence-branch",
                ["suffix"] = "sequence-suffix"
            }, scopeId: frame.ScopeId);
        var breakNodeIds = ImmutableHashSet.Create(StringComparer.Ordinal, "break");
        var session = new SequenceProductionDispatchSession(coordinator, frame, breakNodeIds, maximumConcurrentOutputs: 1);
        return ExecuteCoreAsync(runId, revision, input, null, record, null, cancellationToken,
            rootRevision: revision, sequencePlan: plan, allowOutcomeUnknown: true,
            skipValidationForInternalProof: true,
            activationFrame: frame, proofBreakNodeIds: breakNodeIds, sequenceDispatchSession: session,
            captureActivationBreak: _ => { });
    }

    internal static WorkflowRevision CreateSequenceBranchDecisionEvidenceProofRevision(bool condition)
    {
        var definition = new WorkflowDefinition(WorkflowSchemaVersions.UnifiedWorkflowGraph,
            "sequence-branch-evidence-proof", "input",
            new Dictionary<string, NodeDefinition>
            {
                ["input"] = new(NodeTypes.Input, "1.0.0"),
                ["sequence"] = new(NodeTypes.Sequence, "1.0.0")
                {
                    Sequence = new([new("decision", "Decision"), new("suffix", "After decision")])
                },
                ["condition"] = new(NodeTypes.BooleanConstant, "1.0.0")
                {
                    BooleanConstant = new(condition)
                },
                ["branch"] = new(NodeTypes.BooleanBranch, "1.0.0"),
                ["alternate"] = new(NodeTypes.SequenceUppercase, "1.0.0"),
                ["merge"] = new(NodeTypes.Merge, "1.0.0"),
                ["outcome"] = new(NodeTypes.BooleanBranch, "1.0.0"),
                ["break"] = new(StructuredProofBreakNodeType, "proof-only"),
                ["output"] = new(NodeTypes.Output, "1.0.0"),
                ["suffix-output"] = new(NodeTypes.Output, "1.0.0")
            }.ToImmutableSortedDictionary(),
            new Dictionary<string, ConnectionDefinition>
            {
                ["branch-condition"] = new(new("condition", "out"), new("branch", "condition"), "data"),
                ["outcome-condition"] = new(new("condition", "out"), new("outcome", "condition"), "data")
            }.ToImmutableSortedDictionary())
        {
            ExecutionConnections = new Dictionary<string, ConnectionDefinition>
            {
                ["input-sequence"] = new(new("input", "then"), new("sequence", "execute"), "execution"),
                ["sequence-branch"] = new(new("sequence", "then.decision"), new("branch", "execute"), "execution"),
                ["sequence-suffix"] = new(new("sequence", "then.suffix"), new("suffix-output", "execute"), "execution"),
                ["branch-true"] = new(new("branch", "true"), new("merge", "execute"), "execution"),
                ["branch-false"] = new(new("branch", "false"), new("alternate", "execute"), "execution"),
                ["alternate-merge"] = new(new("alternate", "then"), new("merge", "execute"), "execution"),
                ["merge-outcome"] = new(new("merge", "then"), new("outcome", "execute"), "execution"),
                ["outcome-true"] = new(new("outcome", "true"), new("output", "execute"), "execution"),
                ["outcome-false"] = new(new("outcome", "false"), new("break", "execute"), "execution"),
                ["break-output"] = new(new("break", "out"), new("output", "execute"), "execution")
            }.ToImmutableSortedDictionary()
        };
        return WorkflowRevision.CreateProofOnly(definition, "sequence-branch-evidence-proof");
    }

    internal static WorkflowRevision CreateSequenceBranchDecisionToolOutcomeProofRevision(bool condition)
    {
        const string bindingId = "proof.sequence-branch-tool";
        var definition = CreateSequenceBranchDecisionEvidenceProofRevision(condition).Definition;
        var nodes = definition.Nodes.Add("tool", new NodeDefinition(NodeTypes.McpTool, "1.0.0",
            new ToolBindingRequest(bindingId, new string('a', 64)))
        {
            McpArgumentsJson = "{}"
        });
        var executionConnections = definition.ExecutionConnections!
            .Remove("merge-outcome")
            .Add("merge-tool", new(new("merge", "then"), new("tool", "execute"), "execution"))
            .Add("tool-outcome", new(new("tool", "then"), new("outcome", "execute"), "execution"));
        return WorkflowRevision.CreateProofOnly(definition with
        {
            Nodes = nodes,
            ExecutionConnections = executionConnections
        }, "sequence-branch-tool-outcome-proof");
    }

    internal static WorkflowRevision CreateSequenceBranchDecisionCancellationProofRevision(bool condition)
    {
        var definition = CreateSequenceBranchDecisionEvidenceProofRevision(condition).Definition;
        var nodes = definition.Nodes.Add("agent", new NodeDefinition(NodeTypes.Agent, "1.0.0",
            Agent: new AgentConfiguration("proof.settlement", null, "Wait for host cancellation.", [])
            {
                MaxTurns = 1
            }));
        var executionConnections = definition.ExecutionConnections!
            .Remove("merge-outcome")
            .Add("merge-agent", new(new("merge", "then"), new("agent", "execute"), "execution"))
            .Add("agent-outcome", new(new("agent", "then"), new("outcome", "execute"), "execution"));
        return WorkflowRevision.CreateProofOnly(definition with
        {
            Nodes = nodes,
            ExecutionConnections = executionConnections
        }, "sequence-branch-cancellation-proof");
    }

    /// <summary>
    /// Runs the non-public Sequence-to-Agent lifecycle proof. This deliberately uses a
    /// validator-valid private definition and an injected host Agent executor; it is not
    /// reachable from persisted workflow schemas or Studio authoring.
    /// </summary>
    internal Task<WorkflowExecutionResult> ExecuteSequenceAgentProductionProofAsync(Guid runId, string input,
        Func<ExecutionEvent, ValueTask> record, IWorkflowExecutionGate? gate = null,
        CancellationToken cancellationToken = default)
    {
        var revision = SequenceAgentProofRevision();
        var plan = new SequenceProductionPlan("sequence", [
            new SequenceProductionOutput("agent", 0, "agent", "sequence", "then", "sequence-proof:agent")
        ], null, SequenceTerminalState.Completed, AllowSingleOutput: true);
        return ExecuteCoreAsync(runId, revision, input, null, record, gate, cancellationToken,
            rootRevision: revision, sequencePlan: plan);
    }

    /// <summary>Runs the private two-output Agent fan-out proof used to verify sibling settlement.</summary>
    internal Task<WorkflowExecutionResult> ExecuteSequenceAgentFanoutProductionProofAsync(Guid runId, string input,
        Func<ExecutionEvent, ValueTask> record, CancellationToken cancellationToken = default)
    {
        var revision = SequenceAgentFanoutProofRevision();
        var plan = new SequenceProductionPlan("sequence", [
            new SequenceProductionOutput("failing", 0, "agent-first", "anchor_first", "then", "sequence-proof:failing"),
            new SequenceProductionOutput("sibling", 1, "agent-second", "anchor_second", "then", "sequence-proof:sibling")
        ], null, SequenceTerminalState.Completed, DirectOutputCollection: true, BypassRevisionExecutionEdges: true);
        return ExecuteCoreAsync(runId, revision, input, null, record, null, cancellationToken,
            rootRevision: revision, sequencePlan: plan);
    }

    /// <summary>
    /// Runs the private Sequence-to-WorkflowCall seam through the production nested-workflow
    /// executor. This is a proof harness only: its deliberately non-admitted parent definition
    /// cannot be reached from persisted schemas, migration, or Studio authoring.
    /// </summary>
    internal Task<WorkflowExecutionResult> ExecuteSequenceWorkflowCallProductionProofAsync(Guid runId, string input,
        Func<ExecutionEvent, ValueTask> record, SequenceWorkflowCallProofOutcome outcome = SequenceWorkflowCallProofOutcome.Success,
        IWorkflowExecutionGate? gate = null, CancellationToken cancellationToken = default,
        Func<string, CancellationTokenSource?>? callScopeCancellation = null)
    {
        var firstChild = SequenceWorkflowCallProofChild(outcome, "sequence-call-proof-first");
        var secondChild = SequenceWorkflowCallProofChild(SequenceWorkflowCallProofOutcome.Success, "sequence-call-proof-second");
        var call = new NodeDefinition(NodeTypes.WorkflowCall, "1.0.0")
        {
            WorkflowCall = new(firstChild.Definition.WorkflowId, firstChild.RevisionId, 1, firstChild.ContentHash, firstChild.Definition)
        };
        var siblingCall = new NodeDefinition(NodeTypes.WorkflowCall, "1.0.0")
        {
            WorkflowCall = new(secondChild.Definition.WorkflowId, secondChild.RevisionId, 1, secondChild.ContentHash, secondChild.Definition)
        };
        var parent = SequenceWorkflowCallProofParent(call, siblingCall);
        var topology = SequenceWorkflowCallTopology.Analyze(parent.Definition);
        if (!topology.IsValid || topology.Paths.Length != 2)
            throw new InvalidOperationException("The private Sequence-to-WorkflowCall proof definition does not satisfy its topology contract.");
        var plan = new SequenceProductionPlan("sequence", [
            new SequenceProductionOutput("call", 0, "call", "sequence", "then.call", "sequence-call"),
            new SequenceProductionOutput("sibling", 1, "sibling", "sequence", "then.sibling", "sequence-sibling")
        ], null, SequenceTerminalState.Completed);
        return ExecuteCoreAsync(runId, parent, input, null, record, gate, cancellationToken,
            rootRevision: parent, sequencePlan: plan, sequenceWorkflowCallProof: true,
            callScopeCancellation: callScopeCancellation);
    }

    private static WorkflowRevision SequenceWorkflowCallProofChild(SequenceWorkflowCallProofOutcome outcome, string workflowId)
    {
        var inputNodeId = $"{workflowId}-input";
        var outputNodeId = $"{workflowId}-output";
        if (outcome is SequenceWorkflowCallProofOutcome.Unknown or SequenceWorkflowCallProofOutcome.CapabilityDenied)
        {
            var toolNodeId = $"{workflowId}-unknown-tool";
            var bindingId = outcome is SequenceWorkflowCallProofOutcome.CapabilityDenied
                ? "sequence-proof-denied" : "sequence-proof-unknown";
            var contractHash = outcome is SequenceWorkflowCallProofOutcome.CapabilityDenied
                ? new string('d', 64) : new string('a', 64);
            var toolDefinition = new WorkflowDefinition("1.1", workflowId, inputNodeId,
                new Dictionary<string, NodeDefinition>
                {
                    [inputNodeId] = new(NodeTypes.Input, "1.0.0"),
                    [toolNodeId] = new(NodeTypes.McpTool, "1.0.0",
                        new ToolBindingRequest(bindingId, contractHash))
                    { McpArgumentsJson = "{}" },
                    [outputNodeId] = new(NodeTypes.Output, "1.0.0")
                }.ToImmutableSortedDictionary(),
                new Dictionary<string, ConnectionDefinition>
                {
                    ["input-tool"] = new(new(inputNodeId, "out"), new(toolNodeId, "in"), "data"),
                    ["tool-output"] = new(new(toolNodeId, "out"), new(outputNodeId, "in"), "data")
                }.ToImmutableSortedDictionary());
            return WorkflowRevision.Create(toolDefinition, workflowId);
        }
        var definition = new WorkflowDefinition("1.0", workflowId, inputNodeId,
            new Dictionary<string, NodeDefinition>
            {
                [inputNodeId] = new(NodeTypes.Input, "1.0.0"),
                [outputNodeId] = new(NodeTypes.Output, "1.0.0")
            }.ToImmutableSortedDictionary(),
            new Dictionary<string, ConnectionDefinition>
            {
                ["edge"] = new(new(inputNodeId, "out"), new(outputNodeId, "in"), "data")
            }.ToImmutableSortedDictionary());
        if (outcome is SequenceWorkflowCallProofOutcome.Success)
            definition = GraphEditing.InsertNodeAfter(definition, inputNodeId, "out", new(NodeTypes.Uppercase, "1.0.0"));
        else
        {
            definition = definition with { SchemaVersion = "1.20" };
            var termination = new NodeDefinition(NodeTypes.Terminate, "1.0.0")
            {
                Termination = new(outcome is SequenceWorkflowCallProofOutcome.Termination
                    ? WorkflowTerminationOutcomes.Success : WorkflowTerminationOutcomes.Failure)
            };
            definition = GraphEditing.InsertTerminationAfter(definition, inputNodeId, "out");
            var terminateId = definition.Nodes.Single(entry => entry.Value.Type == NodeTypes.Terminate).Key;
            definition = definition with
            {
                Nodes = definition.Nodes.SetItem(terminateId, definition.Nodes[terminateId] with
                {
                    Termination = termination.Termination
                })
            };
        }
        return WorkflowRevision.Create(definition, workflowId);
    }

    private static WorkflowRevision SequenceWorkflowCallProofParent(NodeDefinition call, NodeDefinition siblingCall)
    {
        var definition = new WorkflowDefinition(WorkflowSchemaVersions.SequenceTransformChainGraph,
            "sequence-call-proof-parent", "input",
            new Dictionary<string, NodeDefinition>
            {
                ["input"] = new(NodeTypes.Input, "1.0.0"),
                ["sequence"] = new(NodeTypes.Sequence, "1.0.0")
                {
                    Sequence = new([new("call", "First child workflow"), new("sibling", "Second child workflow")])
                },
                ["call"] = call,
                ["sibling"] = siblingCall,
                ["output"] = new(NodeTypes.Output, "1.0.0")
            }.ToImmutableSortedDictionary(), ImmutableSortedDictionary<string, ConnectionDefinition>.Empty)
        {
            ExecutionConnections = new Dictionary<string, ConnectionDefinition>
            {
                ["input-sequence"] = new(new("input", "then"), new("sequence", "execute"), "execution"),
                ["sequence-call"] = new(new("sequence", "then.call"), new("call", "execute"), "execution"),
                ["call-output"] = new(new("call", "out"), new("output", "execute"), "execution"),
                ["sequence-sibling"] = new(new("sequence", "then.sibling"), new("sibling", "execute"), "execution"),
                ["sibling-output"] = new(new("sibling", "out"), new("output", "execute"), "execution")
            }.ToImmutableSortedDictionary()
        };
        return WorkflowRevision.CreateProofOnly(definition, "sequence-call-proof-parent");
    }

    private Workflow BuildSequenceProductionWorkflow(Guid runId, WorkflowRevision revision,
        SequenceProductionPlan plan, string input, IReadOnlyDictionary<string, string>? parameters,
        Func<string, string?, Guid?, string?, string?, string?, ValueTask> emit,
        Action markRecorderUncertain,
        IWorkflowExecutionGate? gate, WorkflowRevision rootRevision,
        ImmutableArray<WorkflowApprovalScope> workflowPath,
        Func<string, CancellationTokenSource?>? callScopeCancellation = null,
        Func<string, CancellationToken, ValueTask>? afterNodeStarted = null,
        SequenceProductionDispatchSession? sequenceDispatchSession = null,
        WorkflowDispatchAdmissionOwner? dispatchAdmissionOwner = null)
    {
        var sequenceInvocationId = Guid.NewGuid();
        var connectionIds = plan.Outputs.ToDictionary(output => output.OutputId, output => output.ConnectionId,
            StringComparer.Ordinal);
        var sequenceDefinition = revision.Definition.SchemaVersion == WorkflowSchemaVersions.UnifiedWorkflowGraph
            ? revision.Definition with { SchemaVersion = WorkflowExecutionShape.SequenceContract(revision.Definition) }
            : revision.Definition;
        var callPaths = sequenceDefinition.SchemaVersion switch
        {
            WorkflowSchemaVersions.SequenceWorkflowCallTransformGraph =>
                SequenceWorkflowCallTransformTopology.Analyze(sequenceDefinition).Paths.Select(path =>
                    new SequenceWorkflowCallPath(path.OutputId, path.NodeIds, path.CallNodeId, path.ChildWorkflowId,
                        path.ChildRevisionId, path.ChildContentHash)),
            WorkflowSchemaVersions.SequenceWorkflowCallAgentGraph =>
                SequenceWorkflowCallAgentTopology.Analyze(sequenceDefinition).Paths.Select(path =>
                    new SequenceWorkflowCallPath(path.OutputId, path.NodeIds, path.CallNodeId, path.ChildWorkflowId,
                        path.ChildRevisionId, path.ChildContentHash)),
            WorkflowSchemaVersions.SequenceWorkflowCallJsonGraph when SequenceWorkflowCallJsonTopology.IsJsonCompositionGraph(sequenceDefinition) =>
                SequenceWorkflowCallJsonTopology.Analyze(sequenceDefinition).Paths.Select(path =>
                    new SequenceWorkflowCallPath(path.OutputId, path.NodeIds, path.CallNodeId, path.ChildWorkflowId,
                        path.ChildRevisionId, path.ChildContentHash)),
            WorkflowSchemaVersions.SequenceWorkflowCallJsonGraph =>
                SequenceWorkflowCallAgentTopology.Analyze(sequenceDefinition).Paths.Select(path =>
                    new SequenceWorkflowCallPath(path.OutputId, path.NodeIds, path.CallNodeId, path.ChildWorkflowId,
                        path.ChildRevisionId, path.ChildContentHash)),
            WorkflowSchemaVersions.SequenceWorkflowCallJsonChainGraph =>
                SequenceWorkflowCallJsonChainTopology.Analyze(sequenceDefinition).Paths.Select(path =>
                    new SequenceWorkflowCallPath(path.OutputId, path.NodeIds, path.CallNodeId, path.ChildWorkflowId,
                        path.ChildRevisionId, path.ChildContentHash)),
            WorkflowSchemaVersions.SequenceWorkflowCallJsonMergeGraph =>
                SequenceWorkflowCallJsonMergeTopology.Analyze(sequenceDefinition).Paths.Select(path =>
                    new SequenceWorkflowCallPath(path.OutputId, path.NodeIds, path.CallNodeId, path.ChildWorkflowId,
                        path.ChildRevisionId, path.ChildContentHash)),
            WorkflowSchemaVersions.SequenceWorkflowCallJsonArrayGraph =>
                SequenceWorkflowCallJsonArrayTopology.Analyze(sequenceDefinition).Paths.Select(path =>
                    new SequenceWorkflowCallPath(path.OutputId, path.NodeIds, path.CallNodeId, path.ChildWorkflowId,
                        path.ChildRevisionId, path.ChildContentHash)),
            WorkflowSchemaVersions.SequenceWorkflowCallJsonControlGraph =>
                SequenceWorkflowCallJsonControlTopology.Analyze(sequenceDefinition).Paths.Select(path =>
                    new SequenceWorkflowCallPath(path.OutputId, path.NodeIds, path.CallNodeId, path.ChildWorkflowId,
                        path.ChildRevisionId, path.ChildContentHash)),
            WorkflowSchemaVersions.IntegerStringConversionGraph when sequenceDefinition.Nodes.Values.Any(node => node?.Type == NodeTypes.Sequence) =>
                SequenceWorkflowCallJsonControlTopology.Analyze(sequenceDefinition with { SchemaVersion = WorkflowSchemaVersions.SequenceWorkflowCallJsonControlGraph }).Paths.Select(path =>
                    new SequenceWorkflowCallPath(path.OutputId, path.NodeIds, path.CallNodeId, path.ChildWorkflowId,
                        path.ChildRevisionId, path.ChildContentHash)),
            _ => SequenceWorkflowCallTopology.Analyze(sequenceDefinition).Paths
        };
        var callSettlements = callPaths
            .ToDictionary(path => path.CallNodeId, path => new SequenceWorkflowCallSettlement(
                new SequenceWorkflowCallIdentity(path.OutputId, path.CallNodeId, path.ChildWorkflowId,
                    path.ChildRevisionId, path.ChildContentHash)), StringComparer.Ordinal);
        var coordinator = sequenceDispatchSession?.Coordinator ?? new SequenceRuntimeCoordinator(sequenceInvocationId, plan.SequenceNodeId,
            plan.Outputs.Select(output => output.OutputId).ToArray(), connectionIds, plan.AllowSingleOutput,
            callSettlements, sequenceDispatchSession?.ActivationFrame.ScopeId);
        var sequenceEventGate = new SemaphoreSlim(1, 1);

        async ValueTask FlushSequenceEvents()
        {
            await sequenceEventGate.WaitAsync();
            try
            {
                var batch = coordinator.BeginEventDelivery();
                foreach (var runtimeEvent in batch.Events)
                {
                    var data = runtimeEvent.BreakAcceptance?.Serialize() ?? SequenceEvidence.Serialize(
                        new SequenceLifecycleEvidence(runtimeEvent.SequenceInvocationId,
                            runtimeEvent.SequenceNodeId, runtimeEvent.OutputId, runtimeEvent.Ordinal,
                            runtimeEvent.ActivationId, runtimeEvent.ConnectionId, Reason: runtimeEvent.Reason));
                    try
                    {
                        await emit(runtimeEvent.Kind, runtimeEvent.SequenceNodeId, runtimeEvent.SequenceInvocationId,
                            runtimeEvent.ConnectionId, data, runtimeEvent.ScopeId);
                        coordinator.AcceptDeliveredEvent(batch.DeliveryId, runtimeEvent);
                    }
                    catch
                    {
                        coordinator.MarkEventDeliveryUncertain(batch.DeliveryId);
                        markRecorderUncertain();
                        throw;
                    }
                }
            }
            finally
            {
                sequenceEventGate.Release();
            }
        }

        var dataEvaluator = new ExplicitGraphDataEvaluator(revision.Definition, input, parameters);
        var branchNodeIds = revision.Definition.ExecutionConnections is null
            ? plan.Outputs.Select(output => output.OutputNodeId).Distinct(StringComparer.Ordinal)
            : revision.Definition.Nodes.Where(entry => entry.Value.Type is not (NodeTypes.Input or NodeTypes.Sequence))
                .Select(entry => entry.Key);
        string? ResolveSequenceDispatchTarget(GraphMessage message)
        {
            if (message.ActivationBreakRequest is not null || message.TerminationSignal ||
                message.SourceNode is { } sourceNode && revision.Definition.Nodes.TryGetValue(sourceNode, out var sourceDefinition) &&
                sourceDefinition?.Type == NodeTypes.Output && message.SourcePort == "out")
                return "sequence-collector";
            if (message.SourceNode is null || message.SourcePort is null) return null;
            return (revision.Definition.ExecutionConnections ?? ImmutableSortedDictionary<string, ConnectionDefinition>.Empty)
                .Values.SingleOrDefault(edge => edge.Source.NodeId == message.SourceNode &&
                    edge.Source.PortId == message.SourcePort)?.Target.NodeId;
        }

        var branches = branchNodeIds
            .ToDictionary(outputNodeId => outputNodeId,
            outputNodeId => (Executor<GraphMessage, GraphMessage>)new StringExecutor(outputNodeId,
                revision.Definition.Nodes[outputNodeId], runId, revision, parameters, tools, agents, dataEvaluator, null,
                emit, gate, null, rootRevision, workflowPath, collaborationOptions, null,
                async (lineage, terminalState) =>
                {
                    if (callSettlements.TryGetValue(outputNodeId, out var settlement))
                        settlement.TrySettleOutput();
                    if (coordinator.IsActivationTerminal(lineage.OutputActivationId)) return;
                    coordinator.Complete(lineage.OutputActivationId, terminalState);
                    await FlushSequenceEvents();
                }, null, callSettlements.GetValueOrDefault(outputNodeId), true, callScopeCancellation,
                afterNodeStarted: afterNodeStarted,
                activationBreakNode: sequenceDispatchSession?.BreakNodeIds.Contains(outputNodeId) == true,
                proofBreakNodeIds: sequenceDispatchSession?.BreakNodeIds,
                onSequenceBreakRequested: sequenceDispatchSession is null ? null : async (lineage, request) =>
                {
                    sequenceDispatchSession.RequestBreak(lineage, request);
                    await FlushSequenceEvents();
                }, dispatchAdmissionOwner: dispatchAdmissionOwner,
                dispatchTargetResolver: dispatchAdmissionOwner is null ? null : ResolveSequenceDispatchTarget), StringComparer.Ordinal);
        var source = new SequenceProductionSource(plan.SequenceNodeId, plan, coordinator, FlushSequenceEvents, emit,
            gate, sequenceDispatchSession, dispatchAdmissionOwner);
        var collector = new SequenceProductionCollector("sequence-collector", plan, coordinator,
            FlushSequenceEvents, emit, dispatchAdmissionOwner);
        var sink = new SequenceProductionSink(dispatchAdmissionOwner);
        var terminal = new SequenceProductionTerminal("sequence-terminal", emit, dispatchAdmissionOwner);
        var builder = new WorkflowBuilder(source);

        foreach (var output in plan.Outputs)
        {
            builder.AddEdge<GraphMessage>((ExecutorBinding)source, branches[output.OutputNodeId],
                message => message?.SequenceLineage?.OutputId == output.OutputId &&
                    message.SourceNode == output.SourceNodeId && message.SourcePort == output.SourcePortId);
            if (revision.Definition.ExecutionConnections is null)
                builder.AddEdge<GraphMessage>(branches[output.OutputNodeId], (ExecutorBinding)collector,
                    message => message?.SequenceLineage?.OutputId == output.OutputId);
        }
        var revisionExecutionEdges = plan.BypassRevisionExecutionEdges
            ? Enumerable.Empty<ConnectionDefinition>()
            : (revision.Definition.ExecutionConnections ?? ImmutableSortedDictionary<string, ConnectionDefinition>.Empty).Values;
        foreach (var edge in revisionExecutionEdges.Where(edge =>
                     edge.Source.NodeId != plan.SequenceNodeId && edge.Target.NodeId != plan.SequenceNodeId &&
                     edge.Source.NodeId != revision.Definition.EntryNodeId))
        {
            builder.AddEdge<GraphMessage>(branches[edge.Source.NodeId], branches[edge.Target.NodeId],
                message => message?.SourceNode == edge.Source.NodeId && message.SourcePort == edge.Source.PortId);
        }
        foreach (var outputNodeId in plan.BypassRevisionExecutionEdges ? [] : revision.Definition.ExecutionConnections is null
                     ? []
                     : revision.Definition.Nodes.Where(entry => entry.Value.Type == NodeTypes.Output).Select(entry => entry.Key))
        {
            builder.AddEdge<GraphMessage>(branches[outputNodeId], (ExecutorBinding)collector,
                message => message?.SourceNode == outputNodeId &&
                    message.SourcePort == "out" || sequenceDispatchSession is not null &&
                    message?.ActivationBreakRequest is { } breakRequest &&
                    message.SourceNode == breakRequest.SourceNodeId && message.SourcePort == "break" &&
                    sequenceDispatchSession.BreakNodeIds.Contains(breakRequest.SourceNodeId));
        }
        foreach (var terminateNodeId in plan.BypassRevisionExecutionEdges ? [] : revision.Definition.Nodes
                     .Where(entry => entry.Value.Type == NodeTypes.Terminate)
                     .Select(entry => entry.Key))
        {
            builder.AddEdge<GraphMessage>(branches[terminateNodeId], (ExecutorBinding)collector,
                message => message?.SourceNode == terminateNodeId && message.SourcePort == "terminated" && message.TerminationSignal);
        }
        foreach (var callNodeId in revision.Definition.Nodes
                     .Where(entry => entry.Value.Type == NodeTypes.WorkflowCall)
                     .Select(entry => entry.Key))
        {
            foreach (var terminalOutputNodeId in revision.Definition.Nodes
                         .Where(entry => entry.Value.Type == NodeTypes.Output)
                         .Select(entry => entry.Key))
                builder.AddEdge<GraphMessage>(branches[callNodeId], branches[terminalOutputNodeId],
                    message => message?.SourceNode == callNodeId && message.SourcePort == "terminated" && message.TerminationSignal);
            builder.AddEdge<GraphMessage>(branches[callNodeId], (ExecutorBinding)collector,
                message => message?.SourceNode == callNodeId && message.SourcePort == "terminated" && message.TerminationSignal);
        }
        if (plan.DirectOutputCollection)
            foreach (var output in plan.Outputs)
                builder.AddEdge<GraphMessage>(branches[output.OutputNodeId], (ExecutorBinding)collector,
                    message => message?.SequenceLineage?.OutputId == output.OutputId);
        builder.AddEdge<GraphMessage>((ExecutorBinding)source, (ExecutorBinding)sink,
            message => message?.SourcePort == "continue");
        builder.AddEdge<GraphMessage>((ExecutorBinding)source, (ExecutorBinding)terminal,
            message => message?.SourcePort == "terminal");
        builder.AddEdge<GraphMessage>((ExecutorBinding)collector, (ExecutorBinding)source,
            message => message?.SourcePort == "completed");
        builder.WithOutputFrom(terminal);
        return builder.Build();
    }

    private static WorkflowRevision SequenceProofRevision()
    {
        var definition = new WorkflowDefinition("1.0", "sequence-production-proof", "sequence",
            new Dictionary<string, NodeDefinition>
            {
                ["sequence"] = new(NodeTypes.Input, "1.0.0"),
                ["first"] = new(NodeTypes.Uppercase, "1.0.0"),
                ["second"] = new(NodeTypes.Reverse, "1.0.0"),
                ["third"] = new(NodeTypes.Uppercase, "1.0.0"),
                ["output"] = new(NodeTypes.Output, "1.0.0")
            }.ToImmutableSortedDictionary(),
            new Dictionary<string, ConnectionDefinition>
            {
                ["a"] = new(new("sequence", "out"), new("first", "in"), "data"),
                ["b"] = new(new("first", "out"), new("second", "in"), "data"),
                ["c"] = new(new("second", "out"), new("third", "in"), "data"),
                ["d"] = new(new("third", "out"), new("output", "in"), "data")
            }.ToImmutableSortedDictionary());
        return WorkflowRevision.Create(definition, "private-sequence-production-proof");
    }

    private static WorkflowRevision SequenceAgentProofRevision()
    {
        var agent = new NodeDefinition(NodeTypes.Agent, "1.0.0", Agent: new AgentConfiguration(
            "sequence-proof", null, "Return the input unchanged.", []
        ) { MaxTurns = 1 });
        var definition = new WorkflowDefinition(WorkflowSchemaVersions.AgentExecutionRoutes, "sequence-agent-production-proof", "sequence",
            new Dictionary<string, NodeDefinition>
            {
                ["sequence"] = new(NodeTypes.Input, "1.0.0"),
                ["agent"] = agent,
                ["output"] = new(NodeTypes.Output, "1.0.0")
            }.ToImmutableSortedDictionary(),
            new Dictionary<string, ConnectionDefinition>
            {
                ["agent-output"] = new(new("agent", "out"), new("output", "in"), "data")
            }.ToImmutableSortedDictionary())
        {
            ExecutionConnections = new Dictionary<string, ConnectionDefinition>
            {
                ["sequence-agent"] = new(new("sequence", "then"), new("agent", "execute"), "execution"),
                ["agent-output"] = new(new("agent", "out"), new("output", "execute"), "execution")
            }.ToImmutableSortedDictionary()
        };
        return WorkflowRevision.Create(definition, "private-sequence-agent-production-proof");
    }

    private static WorkflowRevision SequenceAgentFanoutProofRevision()
    {
        var branches = ImmutableArray.Create(
            new ParallelBranchDescriptor("branch_first", "First authored path", "anchor_first"),
            new ParallelBranchDescriptor("branch_second", "Second authored path", "anchor_second"));
        var firstAgent = new NodeDefinition(NodeTypes.Agent, "1.0.0", Agent: new AgentConfiguration(
            "sequence-proof", null, "Return the first authored response.", []) { MaxTurns = 1 });
        var secondAgent = new NodeDefinition(NodeTypes.Agent, "1.0.0", Agent: new AgentConfiguration(
            "sequence-proof", null, "Return the second authored response.", []) { MaxTurns = 1 });
        var nodes = ImmutableSortedDictionary<string, NodeDefinition>.Empty
            .Add("input", new(NodeTypes.Input, "1.0.0"))
            .Add("fork", new(NodeTypes.ParallelFork, "1.0.0") { ParallelFork = new(branches) })
            .Add("anchor_first", new(NodeTypes.ParallelBranch, "1.0.0") { ParallelBranch = new("fork", "branch_first") })
            .Add("anchor_second", new(NodeTypes.ParallelBranch, "1.0.0") { ParallelBranch = new("fork", "branch_second") })
            .Add("agent-first", firstAgent)
            .Add("agent-second", secondAgent)
            .Add("join", new(NodeTypes.ParallelJoin, "1.0.0") { ParallelJoin = new("fork", branches.Select(branch => branch.Id).ToImmutableArray()) })
            .Add("output", new(NodeTypes.Output, "1.0.0") { OutputDataType = "json" });
        var execution = ImmutableSortedDictionary<string, ConnectionDefinition>.Empty
            .Add("input-fork", new(new("input", "then"), new("fork", "execute"), "execution"))
            .Add("fork-first", new(new("fork", ParallelBranchLimits.PinId("branch_first")), new("anchor_first", "execute"), "execution"))
            .Add("fork-second", new(new("fork", ParallelBranchLimits.PinId("branch_second")), new("anchor_second", "execute"), "execution"))
            .Add("first-agent", new(new("anchor_first", "then"), new("agent-first", "execute"), "execution"))
            .Add("second-agent", new(new("anchor_second", "then"), new("agent-second", "execute"), "execution"))
            .Add("first-join", new(new("agent-first", "out"), new("join", ParallelBranchLimits.PinId("branch_first")), "execution"))
            .Add("second-join", new(new("agent-second", "out"), new("join", ParallelBranchLimits.PinId("branch_second")), "execution"))
            .Add("join-output", new(new("join", "then"), new("output", "execute"), "execution"));
        var data = ImmutableSortedDictionary<string, ConnectionDefinition>.Empty
            .Add("join-output-data", new(new("join", "out"), new("output", "in"), "data"));
        var definition = new WorkflowDefinition(WorkflowSchemaVersions.ConfigurableParallelGraph,
            "sequence-agent-fanout-production-proof", "input", nodes, data)
        {
            ExecutionConnections = execution
        };
        return WorkflowRevision.Create(definition, "private-sequence-agent-fanout-production-proof");
    }

    private SequenceProductionPlan? CreateSequenceProductionPlan(WorkflowRevision revision)
        => CreateSequenceProductionPlan(revision.Definition);

    private SequenceProductionPlan? CreateSequenceProductionPlan(WorkflowDefinition definition)
    {
        if (definition.SchemaVersion == WorkflowSchemaVersions.UnifiedWorkflowGraph)
        {
            if (!definition.Nodes.Values.Any(node => node.Type == NodeTypes.Sequence)) return null;
            var contract = WorkflowExecutionShape.SequenceContract(definition);
            // Only planning uses the historical topology contract; execution retains the authored revision.
            return CreateSequenceProductionPlan(definition with { SchemaVersion = contract });
        }
        var promotedControl = definition.SchemaVersion == WorkflowSchemaVersions.IntegerStringConversionGraph && definition.Nodes.Values.Any(node => node?.Type == NodeTypes.Sequence);
        if (definition.SchemaVersion is not (WorkflowSchemaVersions.SequenceGraph or WorkflowSchemaVersions.SequencePathGraph or WorkflowSchemaVersions.SequenceAgentPathGraph or WorkflowSchemaVersions.SequenceTransformPathGraph or WorkflowSchemaVersions.SequenceReversePathGraph or WorkflowSchemaVersions.SequenceTransformChainGraph or WorkflowSchemaVersions.SequenceWorkflowCallGraph or WorkflowSchemaVersions.SequenceWorkflowCallTransformGraph or WorkflowSchemaVersions.SequenceWorkflowCallAgentGraph or WorkflowSchemaVersions.SequenceWorkflowCallJsonGraph or WorkflowSchemaVersions.SequenceWorkflowCallJsonChainGraph or WorkflowSchemaVersions.SequenceWorkflowCallJsonMergeGraph or WorkflowSchemaVersions.SequenceWorkflowCallJsonArrayGraph or WorkflowSchemaVersions.SequenceWorkflowCallJsonControlGraph) && !promotedControl) return null;
        if (definition.SchemaVersion == WorkflowSchemaVersions.SequenceWorkflowCallGraph &&
            !SequenceWorkflowCallTopology.Analyze(definition).IsValid)
            throw new InvalidOperationException("Schema 1.50 requires a valid bounded WorkflowCall topology before execution.");
        if (definition.SchemaVersion == WorkflowSchemaVersions.SequenceWorkflowCallTransformGraph &&
            !SequenceWorkflowCallTransformTopology.Analyze(definition).IsValid)
            throw new InvalidOperationException("Schema 1.51 requires a valid bounded WorkflowCall transform topology before execution.");
        if (definition.SchemaVersion == WorkflowSchemaVersions.SequenceWorkflowCallAgentGraph &&
            !SequenceWorkflowCallAgentTopology.Analyze(definition).IsValid)
            throw new InvalidOperationException("Schema 1.52 requires a valid bounded WorkflowCall Agent topology before execution.");
        if (definition.SchemaVersion == WorkflowSchemaVersions.SequenceWorkflowCallJsonGraph &&
            SequenceWorkflowCallJsonTopology.IsJsonCompositionGraph(definition) &&
            !SequenceWorkflowCallJsonTopology.Analyze(definition).IsValid)
            throw new InvalidOperationException("Schema 1.53 requires a valid bounded WorkflowCall JSON topology before execution.");
        if (definition.SchemaVersion == WorkflowSchemaVersions.SequenceWorkflowCallJsonGraph &&
            !SequenceWorkflowCallJsonTopology.IsJsonCompositionGraph(definition) &&
            !SequenceWorkflowCallAgentTopology.Analyze(definition).IsValid)
            throw new InvalidOperationException("Schema 1.53 preserves only a valid prior WorkflowCall Agent topology when no JSON composition is present.");
        if (definition.SchemaVersion == WorkflowSchemaVersions.SequenceWorkflowCallJsonChainGraph &&
            !SequenceWorkflowCallJsonChainTopology.Analyze(definition).IsValid)
            throw new InvalidOperationException("Schema 1.54 requires a valid bounded WorkflowCall JSON chain topology before execution.");
        if (definition.SchemaVersion == WorkflowSchemaVersions.SequenceWorkflowCallJsonMergeGraph &&
            !SequenceWorkflowCallJsonMergeTopology.Analyze(definition).IsValid)
            throw new InvalidOperationException("Schema 1.55 requires a valid bounded WorkflowCall JSON merge topology before execution.");
        if (definition.SchemaVersion == WorkflowSchemaVersions.SequenceWorkflowCallJsonArrayGraph &&
            !SequenceWorkflowCallJsonArrayTopology.Analyze(definition).IsValid)
            throw new InvalidOperationException("Schema 1.56 requires a valid bounded WorkflowCall JSON-array topology before execution.");
        if ((definition.SchemaVersion == WorkflowSchemaVersions.SequenceWorkflowCallJsonControlGraph || promotedControl) &&
            !SequenceWorkflowCallJsonControlTopology.Analyze(promotedControl ? definition with { SchemaVersion = WorkflowSchemaVersions.SequenceWorkflowCallJsonControlGraph } : definition).IsValid)
            throw new InvalidOperationException("Schema 1.57 requires a valid bounded WorkflowCall JSON-control topology before execution.");
        var sequence = definition.Nodes.SingleOrDefault(entry => entry.Value.Type == NodeTypes.Sequence);
        if (sequence.Value?.Sequence is not { } configuration || definition.ExecutionConnections is not { } edges)
            throw new InvalidOperationException("The Sequence definition is missing its ordered outputs or execution connections.");
        var outputs = configuration.Outputs.Select((output, ordinal) =>
        {
            var edge = edges.Single(edge => edge.Value.Source.NodeId == sequence.Key && edge.Value.Source.PortId == SequenceOutputLimits.PinId(output.Id));
            return new SequenceProductionOutput(output.Id, ordinal, edge.Value.Target.NodeId, sequence.Key,
                edge.Value.Source.PortId, edge.Key);
        }).ToArray();
        return new SequenceProductionPlan(sequence.Key, outputs, null, SequenceTerminalState.Completed);
    }

    internal sealed record SequenceProductionPlan(string SequenceNodeId, IReadOnlyList<SequenceProductionOutput> Outputs,
        string? TerminalOutputId, SequenceTerminalState TerminalState, bool AllowSingleOutput = false,
        bool DirectOutputCollection = false, bool BypassRevisionExecutionEdges = false);

    internal sealed record SequenceProductionOutput(string OutputId, int Ordinal, string OutputNodeId,
        string SourceNodeId, string SourcePortId, string ConnectionId);

    internal sealed class SequenceProductionDispatchSession(SequenceRuntimeCoordinator coordinator,
        StructuredActivationFrame activationFrame, IReadOnlySet<string> breakNodeIds, int maximumConcurrentOutputs)
    {
        private readonly object gate = new();
        private StructuredActivationBreakRequest? breakRequest;

        public SequenceRuntimeCoordinator Coordinator { get; } = coordinator;
        public StructuredActivationFrame ActivationFrame { get; } = activationFrame;
        public IReadOnlySet<string> BreakNodeIds { get; } = breakNodeIds;

        public StructuredActivationBreakRequest? BreakRequest
        {
            get { lock (gate) return breakRequest; }
        }

        public SequenceActivationSnapshot? TryAdmit()
        {
            lock (gate)
            {
                if (breakRequest is not null) return null;
                var activation = ActivationFrame.BreakScope.TryAdmit(() =>
                    Coordinator.TryDispatchNext(Guid.NewGuid(), maximumConcurrentOutputs));
                if (activation is null && ActivationFrame.BreakScope.Request is { } request)
                {
                    breakRequest ??= request;
                    if (!Coordinator.HasAcceptedBreak)
                        throw new InvalidOperationException("The active Sequence did not record the accepted Break request.");
                }
                return activation;
            }
        }

        public void RequestBreak(SequenceMessageLineage lineage, StructuredActivationBreakRequest request)
        {
            if (!BreakNodeIds.Contains(request.SourceNodeId) || request.SourceFrameId != ActivationFrame.Id ||
                request.ScopeId != ActivationFrame.ScopeId)
                throw new InvalidOperationException("The Sequence Break request does not belong to its active frame.");
            var signal = ActivationFrame.ConsumeNearestBreak(request);
            lock (gate) breakRequest ??= request;
            Coordinator.AcceptBreak(lineage, request, signal.TargetFrameId);
            if (!Coordinator.IsActivationTerminal(lineage.OutputActivationId))
                Coordinator.Complete(lineage.OutputActivationId, SequenceTerminalState.Completed);
        }
    }

    private sealed class SequenceProductionSource(string id, SequenceProductionPlan plan,
        SequenceRuntimeCoordinator coordinator, Func<ValueTask> flushSequenceEvents,
        Func<string, string?, Guid?, string?, string?, string?, ValueTask> emit, IWorkflowExecutionGate? gate,
        SequenceProductionDispatchSession? sequenceDispatchSession,
        WorkflowDispatchAdmissionOwner? dispatchAdmissionOwner)
        : Executor<GraphMessage, GraphMessage>(id, declareCrossRunShareable: true)
    {
        public override async ValueTask<GraphMessage> HandleAsync(GraphMessage message, IWorkflowContext context,
            CancellationToken cancellationToken = default)
        {
            dispatchAdmissionOwner?.Begin(message.DispatchTicketId, Id);
            var invocation = Guid.NewGuid();
            try
            {
                await emit(ExecutionEventKinds.NodeStarted, Id, invocation, null, message.Text, null);
                if (message.SourcePort is "completed")
                {
                    if (message.SequenceLineage is not { } completedLineage)
                        throw new InvalidOperationException("A Sequence completion is missing its lineage.");
                    var aggregateCompletion = message.SequenceAggregateCompletion;
                    if (aggregateCompletion is not null &&
                        !aggregateCompletion.Matches(new(completedLineage.SequenceInvocationId,
                            completedLineage.SequenceNodeId, completedLineage.OutputId, completedLineage.Ordinal,
                            completedLineage.OutputActivationId, completedLineage.ConnectionId,
                            completedLineage.DescendantScopeId), message.ActivationFrame?.Id))
                        throw new InvalidOperationException("The aggregate completion signal does not match its Sequence output activation.");
                    await flushSequenceEvents();
                    var nextActivation = sequenceDispatchSession?.TryAdmit();
                    if (nextActivation is { } admitted)
                    {
                        await flushSequenceEvents();
                        var nextOutput = plan.Outputs[admitted.Ordinal];
                        var nextLineage = new SequenceMessageLineage(coordinator.SequenceInvocationId,
                            coordinator.SequenceNodeId, admitted.OutputId, admitted.Ordinal,
                            admitted.ActivationId!.Value, nextOutput.ConnectionId,
                            $"sequence/{admitted.OutputId}");
                        await SendAdmittedAsync(context, new GraphMessage(message.Text, nextOutput.SourceNodeId,
                            nextOutput.SourcePortId, DataType: "string", SequenceLineage: nextLineage,
                            ActivationFrame: sequenceDispatchSession!.ActivationFrame), nextOutput.OutputNodeId,
                            cancellationToken);
                    }
                    var terminal = aggregateCompletion is not null;
                    await emit(ExecutionEventKinds.NodeCompleted, Id, invocation, null,
                        terminal ? "All Sequence outputs completed." : "Waiting for remaining Sequence outputs.",
                        null);
                    return message with
                    {
                        Text = terminal ? coordinator.DeterministicResult ?? message.Text : message.Text,
                        SourceNode = Id,
                        SourcePort = terminal ? "terminal" : "continue",
                        DispatchTicketId = dispatchAdmissionOwner?.Admit(terminal ? "sequence-terminal" : "sequence-sink"),
                        FailureSignal = terminal && aggregateCompletion!.TerminalEventKind is
                            ExecutionEventKinds.SequenceFailed or ExecutionEventKinds.SequenceCancelled,
                        ActivationBreakRequest = terminal ? sequenceDispatchSession?.BreakRequest : null
                    };
                }

                await flushSequenceEvents();
                var pendingOutputs = new List<ValueTask>();
                if (sequenceDispatchSession is null)
                {
                    foreach (var output in plan.Outputs)
                    {
                        if (gate is not null) await gate.WaitForBoundaryAsync($"sequence:{output.OutputId}", cancellationToken);
                        var activation = coordinator.DispatchNext(Guid.NewGuid());
                        var lineage = new SequenceMessageLineage(coordinator.SequenceInvocationId,
                            coordinator.SequenceNodeId, activation.OutputId, activation.Ordinal,
                            activation.ActivationId!.Value, output.ConnectionId,
                            $"sequence/{activation.OutputId}");
                        await flushSequenceEvents();
                        pendingOutputs.Add(SendAdmittedAsync(context, new GraphMessage(message.Text, output.SourceNodeId,
                            output.SourcePortId, DataType: "string", SequenceLineage: lineage), output.OutputNodeId,
                            cancellationToken));
                    }
                }
                else
                {
                    while (sequenceDispatchSession.TryAdmit() is { } activation)
                    {
                        var output = plan.Outputs[activation.Ordinal];
                        if (gate is not null) await gate.WaitForBoundaryAsync($"sequence:{output.OutputId}", cancellationToken);
                        var lineage = new SequenceMessageLineage(coordinator.SequenceInvocationId,
                            coordinator.SequenceNodeId, activation.OutputId, activation.Ordinal,
                            activation.ActivationId!.Value, output.ConnectionId,
                            $"sequence/{activation.OutputId}");
                        await flushSequenceEvents();
                        pendingOutputs.Add(SendAdmittedAsync(context, new GraphMessage(message.Text, output.SourceNodeId,
                            output.SourcePortId, DataType: "string", SequenceLineage: lineage,
                            ActivationFrame: sequenceDispatchSession.ActivationFrame), output.OutputNodeId,
                            cancellationToken));
                    }
                }
                await Task.WhenAll(pendingOutputs.Select(pending => pending.AsTask()));
                await emit(ExecutionEventKinds.NodeCompleted, Id, invocation, null, "All Sequence outputs dispatched.", null);
                return new(message.Text, Id, "continue")
                {
                    DispatchTicketId = dispatchAdmissionOwner?.Admit("sequence-sink")
                };
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                coordinator.CancelPending();
                await flushSequenceEvents();
                await emit(ExecutionEventKinds.NodeCancelled, Id, invocation, null,
                    "Sequence dispatch cancelled.", null);
                throw;
            }
            catch
            {
                coordinator.CancelPending();
                await flushSequenceEvents();
                await emit(ExecutionEventKinds.NodeFailed, Id, invocation, null,
                    "Sequence dispatch failed.", null);
                throw;
            }
            finally
            {
                dispatchAdmissionOwner?.Settle(message.DispatchTicketId, Id);
            }
        }

        private async ValueTask SendAdmittedAsync(IWorkflowContext context, GraphMessage message,
            string targetNodeId, CancellationToken cancellationToken)
        {
            var ticket = dispatchAdmissionOwner?.Admit(targetNodeId);
            try
            {
                await context.SendMessageAsync(ticket is { } id ? message with { DispatchTicketId = id } : message,
                    targetNodeId, cancellationToken);
            }
            catch
            {
                if (ticket is { } id) dispatchAdmissionOwner!.Revoke(id);
                throw;
            }
        }
    }

    private sealed class SequenceProductionCollector(string id, SequenceProductionPlan plan,
        SequenceRuntimeCoordinator coordinator, Func<ValueTask> flushSequenceEvents,
        Func<string, string?, Guid?, string?, string?, string?, ValueTask> emit,
        WorkflowDispatchAdmissionOwner? dispatchAdmissionOwner)
        : Executor<GraphMessage, GraphMessage>(id, declareCrossRunShareable: true)
    {
        public override async ValueTask<GraphMessage> HandleAsync(GraphMessage message, IWorkflowContext context,
            CancellationToken cancellationToken = default)
        {
            dispatchAdmissionOwner?.Begin(message.DispatchTicketId, Id);
            if (message.SequenceLineage is not { } lineage)
                throw new InvalidOperationException("A Sequence descendant completion is missing its lineage.");
            var invocation = Guid.NewGuid();
            try
            {
                await emit(ExecutionEventKinds.NodeStarted, Id, invocation, lineage.ConnectionId,
                    message.Text, lineage.DescendantScopeId);
                var terminalState = message.FailureSignal
                    ? SequenceTerminalState.Failed
                    : plan.TerminalOutputId == lineage.OutputId
                    ? plan.TerminalState : SequenceTerminalState.Completed;
                coordinator.Complete(lineage.OutputActivationId, terminalState,
                    terminalState == SequenceTerminalState.Completed && message.HasValue && !message.TerminationSignal ? message.Text : null);
                await flushSequenceEvents();
                var aggregateCompletion = SequenceAggregateCompletionSignal.Create(coordinator,
                    new(lineage.SequenceInvocationId, lineage.SequenceNodeId, lineage.OutputId, lineage.Ordinal,
                        lineage.OutputActivationId, lineage.ConnectionId,
                        lineage.DescendantScopeId), message.ActivationFrame?.Id);
                await emit(ExecutionEventKinds.NodeCompleted, Id, invocation, lineage.ConnectionId,
                    terminalState == SequenceTerminalState.Completed ? "Sequence output completed." :
                    $"Sequence output ended with {terminalState}.", lineage.DescendantScopeId);
                return message with
                {
                    SourceNode = Id,
                    SourcePort = "completed",
                    SequenceAggregateCompletion = aggregateCompletion,
                    DispatchTicketId = dispatchAdmissionOwner?.Admit(coordinator.SequenceNodeId)
                };
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                coordinator.CancelPending();
                await flushSequenceEvents();
                await emit(ExecutionEventKinds.NodeCancelled, Id, invocation, lineage.ConnectionId,
                    "Sequence output cancelled.", lineage.DescendantScopeId);
                throw;
            }
            catch
            {
                coordinator.CancelPending();
                await flushSequenceEvents();
                await emit(ExecutionEventKinds.NodeFailed, Id, invocation, lineage.ConnectionId,
                    "Sequence output failed.", lineage.DescendantScopeId);
                throw;
            }
            finally
            {
                dispatchAdmissionOwner?.Settle(message.DispatchTicketId, Id);
            }
        }
    }

    private sealed class SequenceProductionSink(WorkflowDispatchAdmissionOwner? dispatchAdmissionOwner)
        : Executor<GraphMessage, GraphMessage>("sequence-sink", declareCrossRunShareable: true)
    {
        public override ValueTask<GraphMessage> HandleAsync(GraphMessage message, IWorkflowContext context,
            CancellationToken cancellationToken = default)
        {
            dispatchAdmissionOwner?.Begin(message.DispatchTicketId, Id);
            try { return ValueTask.FromResult(message); }
            finally { dispatchAdmissionOwner?.Settle(message.DispatchTicketId, Id); }
        }
    }

    private sealed class SequenceProductionTerminal(string id,
        Func<string, string?, Guid?, string?, string?, string?, ValueTask> emit,
        WorkflowDispatchAdmissionOwner? dispatchAdmissionOwner)
        : Executor<GraphMessage, GraphMessage>(id, declareCrossRunShareable: true)
    {
        public override async ValueTask<GraphMessage> HandleAsync(GraphMessage message, IWorkflowContext context,
            CancellationToken cancellationToken = default)
        {
            dispatchAdmissionOwner?.Begin(message.DispatchTicketId, Id);
            var invocation = Guid.NewGuid();
            try
            {
                await emit(ExecutionEventKinds.NodeStarted, Id, invocation, null, message.Text, null);
                await emit(ExecutionEventKinds.NodeCompleted, Id, invocation, null, message.Text, null);
                return message with { SourceNode = Id, SourcePort = "out" };
            }
            finally
            {
                dispatchAdmissionOwner?.Settle(message.DispatchTicketId, Id);
            }
        }
    }
}
