using System.Collections.Concurrent;
using Microsoft.Agents.AI.Workflows;
using Xunit;

namespace VisualAgentWorkflow.AgentFramework.Tests;

/// <summary>
/// Architecture proof for the Agent Framework subworkflow-per-activation candidate.
/// It intentionally adds no public workflow node or schema behavior.
/// </summary>
public sealed class ArrayIterationSubworkflowProofTests
{
    [Fact]
    public async Task FreshChildWorkflowPerItem_IsolatesConditionalAndParallelJoinState()
    {
        var trace = new ConcurrentQueue<ProofEvent>();
        var parent = new IterationExecutor(trace);
        var workflow = new WorkflowBuilder(parent).WithOutputFrom(parent).Build();

        await using var run = await InProcessExecution.Concurrent.RunStreamingAsync(workflow, "[1,2,3,4]");
        string? output = null;
        await foreach (var evt in run.WatchStreamAsync())
            if (evt is WorkflowOutputEvent { Data: string value }) output = value;

        Assert.Equal("1:single;2:left+right;3:single;4:left+right", output);
        var events = trace.ToArray();
        var activations = events.Where(item => item.Kind == "body.created")
            .Select(item => item.ActivationId).Distinct().ToArray();
        Assert.Equal(4, activations.Length);

        foreach (var activationId in activations)
        {
            var activationEvents = events.Where(item => item.ActivationId == activationId).ToArray();
            var item = Assert.Single(activationEvents, evt => evt.Kind == "body.created").Item;
            var expectedBranches = item % 2 == 0 ? new[] { "left", "right" } : Array.Empty<string>();
            Assert.Equal(expectedBranches.Order(StringComparer.Ordinal), activationEvents
                .Where(evt => evt.Kind == "branch.completed")
                .Select(evt => evt.BranchId!)
                .Order(StringComparer.Ordinal));
            Assert.Equal(expectedBranches.Length, activationEvents.Count(evt => evt.Kind == "join.arrived"));
            Assert.Single(activationEvents, evt => evt.Kind == "body.completed");
            var nestedEvents = events.Where(evt => evt.ParentActivationId == activationId).ToArray();
            Assert.Equal([0, 1], nestedEvents.Where(evt => evt.Kind == "nested.iteration.started")
                .Select(evt => evt.Index).Order());
            Assert.Single(nestedEvents, evt => evt.Kind == "nested.break.requested");
            Assert.Single(nestedEvents, evt => evt.Kind == "nested.break.consumed");
            Assert.DoesNotContain(nestedEvents, evt => evt.Kind == "nested.iteration.started" && evt.Index == 2);
        }

        Assert.Equal(4, events.Where(evt => evt.Kind == "body.created")
            .Select(evt => evt.ChildWorkflow).ToHashSet(ReferenceEqualityComparer.Instance).Count);
    }

    private sealed class IterationExecutor(ConcurrentQueue<ProofEvent> trace)
        : Executor<string, string>("iteration", declareCrossRunShareable: true)
    {
        public override async ValueTask<string> HandleAsync(string json, IWorkflowContext context,
            CancellationToken cancellationToken = default)
        {
            var values = System.Text.Json.JsonSerializer.Deserialize<int[]>(json)
                ?? throw new InvalidOperationException("The proof input must be an array.");
            var results = new List<string>(values.Length);
            foreach (var (item, index) in values.Select((value, index) => (value, index)))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var activationId = Guid.NewGuid();
                var (child, workflow) = BuildPerItemWorkflow(trace);
                trace.Enqueue(new("body.created", activationId, index, item, child, null));
                await using var childRun = await InProcessExecution.Concurrent.RunStreamingAsync(workflow,
                    new BodyInput(activationId, item, index), cancellationToken: cancellationToken);
                string? result = null;
                await foreach (var evt in childRun.WatchStreamAsync(cancellationToken))
                    if (evt is WorkflowOutputEvent { Data: string value }) result = value;
                cancellationToken.ThrowIfCancellationRequested();
                if (result is null) throw new InvalidOperationException("An iteration body produced no result.");
                trace.Enqueue(new("body.completed", activationId, index, item, child, null));
                results.Add($"{item}:{result}");
            }
            return string.Join(';', results);
        }
    }

    private static (Workflow Child, Workflow Parent) BuildPerItemWorkflow(ConcurrentQueue<ProofEvent> trace)
    {
        var child = BuildBodyWorkflow(trace);
        var body = child.BindAsExecutor($"array-item-body-{Guid.NewGuid():N}");
        var start = new BodyStartExecutor();
        var builder = new WorkflowBuilder(start);
        builder.AddEdge<BodyInput>((ExecutorBinding)start, body, _ => true);
        builder.WithOutputFrom(body);
        return (child, builder.Build());
    }

    private static Workflow BuildBodyWorkflow(ConcurrentQueue<ProofEvent> trace)
    {
        var route = new RouteExecutor(trace);
        var falsePath = new SinglePathExecutor(trace);
        var left = new BranchExecutor("left", trace);
        var right = new BranchExecutor("right", trace);
        var join = new JoinExecutor(trace);
        var complete = new CompletionExecutor(trace);
        var builder = new WorkflowBuilder(route);

        builder.AddEdge<RouteMessage>((ExecutorBinding)route, (ExecutorBinding)falsePath,
            message => message is { UseParallel: false });
        builder.AddEdge<RouteMessage>((ExecutorBinding)route, (ExecutorBinding)left,
            message => message is { UseParallel: true });
        builder.AddEdge<RouteMessage>((ExecutorBinding)route, (ExecutorBinding)right,
            message => message is { UseParallel: true });
        builder.AddEdge<CompletionMessage>((ExecutorBinding)falsePath, (ExecutorBinding)complete, _ => true);
        builder.AddFanInBarrierEdge(new ExecutorBinding[] { left, right }, (ExecutorBinding)join);
        builder.AddEdge<CompletionMessage>((ExecutorBinding)join, (ExecutorBinding)complete, _ => true);
        builder.WithOutputFrom((ExecutorBinding)complete);
        return builder.Build();
    }

    private sealed class BodyStartExecutor()
        : Executor<BodyInput, BodyInput>("body-start", declareCrossRunShareable: true)
    {
        public override ValueTask<BodyInput> HandleAsync(BodyInput message, IWorkflowContext context,
            CancellationToken cancellationToken = default) => ValueTask.FromResult(message);
    }

    private sealed class RouteExecutor(ConcurrentQueue<ProofEvent> trace)
        : Executor<BodyInput, RouteMessage>("route", declareCrossRunShareable: true)
    {
        public override async ValueTask<RouteMessage> HandleAsync(BodyInput message, IWorkflowContext context,
            CancellationToken cancellationToken = default)
        {
            trace.Enqueue(new("condition.routed", message.ActivationId, message.Index, message.Item, null, null));
            for (var innerIndex = 0; innerIndex < 3; innerIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var inner = new InnerInput(Guid.NewGuid(), message.ActivationId, message.Item,
                    innerIndex, message.Item + innerIndex);
                var nestedWorkflow = BuildInnerWorkflow(trace);
                trace.Enqueue(new("nested.iteration.started", inner.ActivationId, inner.InnerIndex, inner.Item, null, null)
                {
                    ParentActivationId = message.ActivationId
                });
                await using var nestedRun = await InProcessExecution.Concurrent.RunStreamingAsync(nestedWorkflow, inner,
                    cancellationToken: cancellationToken);
                string? nestedResult = null;
                await foreach (var evt in nestedRun.WatchStreamAsync(cancellationToken))
                    if (evt is WorkflowOutputEvent { Data: string value }) nestedResult = value;
                if (nestedResult == "break")
                {
                    trace.Enqueue(new("nested.break.consumed", inner.ActivationId, inner.InnerIndex, inner.Item, null, null)
                    {
                        ParentActivationId = message.ActivationId
                    });
                    break;
                }
                if (nestedResult != "continue") throw new InvalidOperationException("Nested loop body returned an invalid result.");
                trace.Enqueue(new("nested.iteration.completed", inner.ActivationId, inner.InnerIndex, inner.Item, null, null)
                {
                    ParentActivationId = message.ActivationId
                });
            }
            var useParallel = message.Item % 2 == 0;
            if (!useParallel)
                return new RouteMessage(message.ActivationId, message.Item, message.Index, false);

            // Both outgoing edges receive the same typed branch message. The AF barrier
            // releases the join only after both independently activated branches return.
            return new RouteMessage(message.ActivationId, message.Item, message.Index, true);
        }
    }

    private static Workflow BuildInnerWorkflow(ConcurrentQueue<ProofEvent> trace)
    {
        var decision = new InnerDecisionExecutor(trace);
        var child = new WorkflowBuilder(decision).WithOutputFrom(decision).Build();
        var binding = child.BindAsExecutor($"nested-body-{Guid.NewGuid():N}");
        var start = new InnerStartExecutor();
        var parent = new WorkflowBuilder(start);
        parent.AddEdge<InnerInput>((ExecutorBinding)start, binding, _ => true);
        parent.WithOutputFrom(binding);
        return parent.Build();
    }

    private sealed class InnerStartExecutor()
        : Executor<InnerInput, InnerInput>("nested-start", declareCrossRunShareable: true)
    {
        public override ValueTask<InnerInput> HandleAsync(InnerInput message, IWorkflowContext context,
            CancellationToken cancellationToken = default) => ValueTask.FromResult(message);
    }

    private sealed class InnerDecisionExecutor(ConcurrentQueue<ProofEvent> trace)
        : Executor<InnerInput, string>("nested-break-decision", declareCrossRunShareable: true)
    {
        public override ValueTask<string> HandleAsync(InnerInput message, IWorkflowContext context,
            CancellationToken cancellationToken = default)
        {
            var shouldBreak = message.Item > message.OuterItem;
            if (shouldBreak)
                trace.Enqueue(new("nested.break.requested", message.ActivationId, message.InnerIndex, message.Item, null, null)
                {
                    ParentActivationId = message.OuterActivationId
                });
            return ValueTask.FromResult(shouldBreak ? "break" : "continue");
        }
    }

    private sealed class SinglePathExecutor(ConcurrentQueue<ProofEvent> trace)
        : Executor<RouteMessage, CompletionMessage>("single", declareCrossRunShareable: true)
    {
        public override ValueTask<CompletionMessage> HandleAsync(RouteMessage message, IWorkflowContext context,
            CancellationToken cancellationToken = default)
        {
            trace.Enqueue(new("single.completed", message.ActivationId, message.Index, message.Item, null, null));
            return ValueTask.FromResult(new CompletionMessage(message.ActivationId, message.Item, message.Index, "single", 1));
        }
    }

    private sealed class BranchExecutor(string branchId, ConcurrentQueue<ProofEvent> trace)
        : Executor<RouteMessage, BranchMessage>($"branch-{branchId}", declareCrossRunShareable: true)
    {
        public override ValueTask<BranchMessage> HandleAsync(RouteMessage message, IWorkflowContext context,
            CancellationToken cancellationToken = default)
        {
            trace.Enqueue(new("branch.completed", message.ActivationId, message.Index, message.Item, null, branchId));
            return ValueTask.FromResult(new BranchMessage(message.ActivationId, message.Item, message.Index, branchId));
        }
    }

    private sealed class JoinExecutor(ConcurrentQueue<ProofEvent> trace)
        : Executor<BranchMessage, CompletionMessage>("join", declareCrossRunShareable: true)
    {
        private readonly ConcurrentDictionary<Guid, ConcurrentDictionary<string, byte>> arrivals = new();

        public override ValueTask<CompletionMessage> HandleAsync(BranchMessage message, IWorkflowContext context,
            CancellationToken cancellationToken = default)
        {
            var activationArrivals = arrivals.GetOrAdd(message.ActivationId, _ => new(StringComparer.Ordinal));
            if (!activationArrivals.TryAdd(message.BranchId, 0))
                throw new InvalidOperationException("A branch arrived at the join more than once.");
            trace.Enqueue(new("join.arrived", message.ActivationId, message.Index, message.Item, null, message.BranchId));
            return ValueTask.FromResult(new CompletionMessage(message.ActivationId, message.Item, message.Index,
                message.BranchId, 2));
        }
    }

    private sealed class CompletionExecutor(ConcurrentQueue<ProofEvent> trace)
        : Executor<CompletionMessage, string>("complete", declareCrossRunShareable: true)
    {
        private readonly ConcurrentDictionary<Guid, ConcurrentDictionary<string, byte>> arrivals = new();
        public override ValueTask<string> HandleAsync(CompletionMessage message, IWorkflowContext context,
            CancellationToken cancellationToken = default)
        {
            var activationArrivals = arrivals.GetOrAdd(message.ActivationId, _ => new(StringComparer.Ordinal));
            activationArrivals.TryAdd(message.BranchId, 0);
            if (activationArrivals.Count == message.ExpectedCount)
            {
                var value = message.ExpectedCount == 1 ? "single" : "left+right";
                trace.Enqueue(new("join.completed", message.ActivationId, message.Index, message.Item, null, null));
                return ValueTask.FromResult(value);
            }
            return ValueTask.FromResult(string.Empty);
        }
    }

    private sealed record BodyInput(Guid ActivationId, int Item, int Index);
    private sealed record InnerInput(Guid ActivationId, Guid OuterActivationId, int OuterItem, int InnerIndex, int Item);
    private sealed record RouteMessage(Guid ActivationId, int Item, int Index, bool UseParallel);
    private sealed record BranchMessage(Guid ActivationId, int Item, int Index, string BranchId);
    private sealed record CompletionMessage(Guid ActivationId, int Item, int Index, string BranchId, int ExpectedCount);
    private sealed record ProofEvent(string Kind, Guid ActivationId, int Index, int Item, Workflow? ChildWorkflow, string? BranchId)
    {
        public Guid? ParentActivationId { get; init; }
    }
}
