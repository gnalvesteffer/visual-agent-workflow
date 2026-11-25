using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace VisualAgentWorkflow.Core;

/// <summary>
/// Author-chosen configuration of a <see cref="NodeTypes.Wait"/> node (Decision 0081). Duration only: there is no
/// absolute deadline, and the value is a bounded count of milliseconds measured on a monotonic clock by the host.
/// </summary>
public sealed record WaitConfiguration(
    [property: JsonPropertyName("durationMilliseconds")] long DurationMilliseconds);

/// <summary>Fixed, workflow-independent bounds for the Wait node. The host may only tighten them.</summary>
public static class WorkflowWaitLimits
{
    public const long MinimumDurationMilliseconds = 1;

    /// <summary>Absolute ceiling for one wait: 24 hours. Validation rejects, and never clamps, a larger value.</summary>
    public const long MaximumDurationMilliseconds = 24L * 60 * 60 * 1000;

    /// <summary>Absolute ceiling for the cumulative waits of one run: 24 hours.</summary>
    public const long MaximumCumulativeMilliseconds = 24L * 60 * 60 * 1000;

    /// <summary>Hard ceiling for runs allowed to hold waits at once. The runtime admits 32 owned runs in all.</summary>
    public const int MaximumWaitingRunsCeiling = 16;
}

/// <summary>
/// Host-owned Wait policy: the per-wait maximum, the cumulative per-run wait budget, and the number of concurrently
/// admitted Wait-capable runs. Workflows, run input, and tool output cannot raise any of these values.
/// </summary>
public sealed record WorkflowWaitOptions
{
    public static WorkflowWaitOptions Default { get; } = new();

    /// <summary>Defaults are deliberately small: one hour per wait, two hours per run, four waiting runs.</summary>
    public WorkflowWaitOptions()
        : this(TimeSpan.FromHours(1), TimeSpan.FromHours(2), 4) { }

    public WorkflowWaitOptions(TimeSpan maximumWait, TimeSpan cumulativeBudget, int maximumWaitingRuns)
    {
        if (maximumWait <= TimeSpan.Zero || maximumWait.TotalMilliseconds > WorkflowWaitLimits.MaximumDurationMilliseconds)
            throw new ArgumentOutOfRangeException(nameof(maximumWait), "The per-wait maximum must be positive and at most 24 hours.");
        if (cumulativeBudget < maximumWait || cumulativeBudget.TotalMilliseconds > WorkflowWaitLimits.MaximumCumulativeMilliseconds)
            throw new ArgumentOutOfRangeException(nameof(cumulativeBudget), "The cumulative budget must be at least the per-wait maximum and at most 24 hours.");
        if (maximumWaitingRuns is < 1 or > WorkflowWaitLimits.MaximumWaitingRunsCeiling)
            throw new ArgumentOutOfRangeException(nameof(maximumWaitingRuns), $"The waiting-run cap must be between 1 and {WorkflowWaitLimits.MaximumWaitingRunsCeiling}.");
        MaximumWait = maximumWait;
        CumulativeBudget = cumulativeBudget;
        MaximumWaitingRuns = maximumWaitingRuns;
    }

    /// <summary>Largest duration one Wait node may request.</summary>
    public TimeSpan MaximumWait { get; }

    /// <summary>Largest sum of Wait durations one run may request.</summary>
    public TimeSpan CumulativeBudget { get; }

    /// <summary>Largest number of concurrently admitted runs whose definition contains a Wait node.</summary>
    public int MaximumWaitingRuns { get; }

    /// <summary>
    /// Host admission check. An author duration above a bound is rejected here and is never clamped. Each Wait runs at
    /// most once (it cannot sit in a loop, branch, or Sequence output), so the static sum bounds the run's waiting.
    /// </summary>
    public ImmutableArray<ValidationIssue> Check(WorkflowDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var issues = ImmutableArray.CreateBuilder<ValidationIssue>();
        long total = 0;
        foreach (var (id, node) in definition.Nodes)
        {
            if (node?.Type != NodeTypes.Wait || node.Wait is null) continue;
            var milliseconds = node.Wait.DurationMilliseconds;
            if (milliseconds > MaximumWait.TotalMilliseconds)
                issues.Add(new("wait.bound", $"nodes.{id}.wait.durationMilliseconds",
                    $"A wait of {milliseconds} ms exceeds the host per-wait maximum of {(long)MaximumWait.TotalMilliseconds} ms."));
            total = checked(total + Math.Max(milliseconds, 0));
        }
        if (total > CumulativeBudget.TotalMilliseconds)
            issues.Add(new("wait.budget", "nodes",
                $"The workflow's waits total {total} ms, above the host per-run budget of {(long)CumulativeBudget.TotalMilliseconds} ms."));
        return issues.ToImmutable();
    }

    /// <summary>True when the definition would hold a wait slot at the host's waiting-run cap.</summary>
    public static bool ContainsWait(WorkflowDefinition definition) =>
        definition.Nodes.Values.Any(node => node?.Type == NodeTypes.Wait);
}

/// <summary>Placement analysis for Wait nodes. Legal only on top-level execution paths.</summary>
public static class WorkflowWaitPlacement
{
    /// <summary>
    /// Returns the Wait nodes that sit inside a Sequence output path, a Loop or For Each body, or a Fork/Join branch.
    /// A region is everything execution-reachable from its opening pins. Reaching a Loop or For Each through its
    /// feedback pin ends the region walk, and a Join continues the walk only when its Fork is itself inside a region.
    /// Sub-workflow bodies are rejected separately because they are validated as child definitions.
    /// </summary>
    public static ImmutableArray<string> NodesInsideRegions(WorkflowDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (definition.Nodes is null) return [];
        if (definition.ExecutionConnections is not { } edges)
        {
            // Fail closed: with no edge table the walk cannot prove a Wait is top-level, so any region-capable node
            // alongside a Wait places every Wait inside a region.
            var hasRegionNode = definition.Nodes.Values.Any(node => node?.Type is NodeTypes.Sequence or NodeTypes.Loop or
                NodeTypes.ForEach or NodeTypes.ParallelFork or NodeTypes.ParallelBranch or NodeTypes.ParallelJoin or
                NodeTypes.LoopBody or NodeTypes.Break);
            return hasRegionNode
                ? [.. definition.Nodes.Where(entry => entry.Value?.Type == NodeTypes.Wait).Select(entry => entry.Key).Order(StringComparer.Ordinal)]
                : [];
        }
        var outgoing = edges.Values.Where(edge => edge?.Source is not null && edge.Target is not null)
            .ToLookup(edge => edge.Source.NodeId, StringComparer.Ordinal);
        var inside = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Queue<string>();

        void Enter(string nodeId)
        {
            if (inside.Add(nodeId)) pending.Enqueue(nodeId);
        }

        foreach (var (id, node) in definition.Nodes)
            foreach (var edge in outgoing[id])
            {
                var opens = node?.Type is NodeTypes.Sequence or NodeTypes.ParallelFork ||
                    node?.Type is NodeTypes.Loop or NodeTypes.ForEach && edge.Source.PortId == "body";
                if (opens) Enter(edge.Target.NodeId);
            }

        // A Join belongs to the region of its Fork. Branch pins can be walked before the Fork itself is reached
        // through an enclosing region, so a skipped Join is retried once its Fork is known to be inside.
        var deferredJoins = new List<(string JoinId, string ForkId)>();
        while (true)
        {
            while (pending.TryDequeue(out var current))
                foreach (var edge in outgoing[current])
                {
                    var targetId = edge.Target.NodeId;
                    if (!definition.Nodes.TryGetValue(targetId, out var target) || target is null) continue;
                    if (target.Type is NodeTypes.Loop or NodeTypes.ForEach && edge.Target.PortId == "feedback") continue;
                    if (target.Type == NodeTypes.ParallelJoin)
                    {
                        if (target.ParallelJoin?.ForkNodeId is not { } forkId) continue;
                        if (!inside.Contains(forkId)) { deferredJoins.Add((targetId, forkId)); continue; }
                    }
                    Enter(targetId);
                }
            var released = deferredJoins.Where(join => inside.Contains(join.ForkId)).ToArray();
            if (released.Length == 0) break;
            foreach (var join in released) Enter(join.JoinId);
            deferredJoins.RemoveAll(join => inside.Contains(join.ForkId));
        }

        return [.. definition.Nodes.Where(entry => entry.Value?.Type == NodeTypes.Wait && inside.Contains(entry.Key))
            .Select(entry => entry.Key).Order(StringComparer.Ordinal)];
    }
}

/// <summary>One host-run wait requested by a Wait node. The host re-derives the duration from the captured definition.</summary>
public sealed record WorkflowWaitRequest(string NodeId, Guid InvocationId, TimeSpan Duration);

/// <summary>
/// Optional trusted capability of an <see cref="IWorkflowExecutionGate"/>: hold the run for a bounded duration measured
/// on the host's monotonic clock. A gate that does not implement it cannot run a workflow containing a Wait node, and
/// the runner fails closed before starting. Cancellation must unblock the wait. The implementation records
/// <c>wait.requested</c> and exactly one settlement, releases the execution slot while waiting, and reacquires it
/// before returning. An elapsed wait returns normally; any other settlement throws <see cref="OperationCanceledException"/>.
/// </summary>
public interface IWorkflowWaitGate
{
    ValueTask WaitAsync(WorkflowWaitRequest request, CancellationToken cancellationToken);
}

/// <summary>A workflow's waits exceed a host-owned bound. The definition is rejected, never clamped.</summary>
public sealed class WorkflowWaitAdmissionException(ImmutableArray<ValidationIssue> issues)
    : ArgumentException("The workflow's waits exceed a host-owned bound: " + string.Join(" ", issues.Select(issue => issue.Message)))
{
    public ImmutableArray<ValidationIssue> Issues { get; } = issues;
}
