using System.Collections.Immutable;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using VisualAgentWorkflow.Core;
using Xunit;

namespace VisualAgentWorkflow.Hosting.Tests;

/// <summary>Node stepping and breakpoints through a For Each and Break (Decision 0070, For Each node stepping).</summary>
public sealed class ForEachSteppingTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "vaw-foreach-step-" + Guid.NewGuid());

    private sealed class EchoAgent : IAgentNodeExecutor
    {
        public Task<IReadOnlyList<AgentProviderDescriptor>> ListProvidersAsync() =>
            Task.FromResult<IReadOnlyList<AgentProviderDescriptor>>([]);
        public Task<string> ExecuteAsync(NodeInvocationContext invocation, string input, CancellationToken cancellationToken = default) =>
            Task.FromResult(input);
    }

    private LocalWorkflowRuntime Runtime() =>
        new(new FileRunJournal(directory), NullLogger<LocalWorkflowRuntime>.Instance, agents: new EchoAgent());

    private static ConnectionDefinition Edge(string from, string fromPort, string to, string toPort, string kind) =>
        new(new(from, fromPort), new(to, toPort), kind);

    /// <summary>input → For Each over [a,b,c] → body → agent (item) → feedback; completed → output.</summary>
    private static WorkflowDefinition Workflow(bool withBreak)
    {
        var nodes = new Dictionary<string, NodeDefinition>
        {
            ["input"] = new(NodeTypes.Input, "1.0.0"),
            ["items"] = new(NodeTypes.ArrayLiteral, "1.0.0") { ArrayLiteral = new(WorkflowDataTypes.String, "[\"a\",\"b\",\"c\"]") },
            ["each"] = new(NodeTypes.ForEach, "1.0.0") { ArrayElementType = WorkflowDataTypes.String },
            ["body"] = new(NodeTypes.LoopBody, "1.0.0"),
            ["agent"] = new(NodeTypes.Agent, "1.0.0", Agent: new("controlled-test", null, "Process input", [])),
            ["done"] = new(NodeTypes.StringConstant, "1.0.0") { StringConstant = new("done") },
            ["output"] = new(NodeTypes.Output, "1.0.0")
        };
        var data = new Dictionary<string, ConnectionDefinition>
        {
            ["items-each"] = Edge("items", "out", "each", "array", "data"),
            ["item-agent"] = Edge("each", "item", "agent", "in", "data"),
            ["result"] = Edge("done", "out", "output", "in", "data")
        };
        var execution = new Dictionary<string, ConnectionDefinition>
        {
            ["start"] = Edge("input", "then", "each", "execute", "execution"),
            ["body-start"] = Edge("each", "body", "body", "execute", "execution"),
            ["return"] = Edge("agent", "out", "each", "feedback", "execution"),
            ["complete"] = Edge("each", "completed", "output", "execute", "execution")
        };
        if (withBreak)
        {
            nodes["cond"] = new(NodeTypes.Condition, "1.0.0") { Condition = new("contains", "b") };
            nodes["break"] = new(NodeTypes.Break, "1.0.0");
            data["item-cond"] = Edge("each", "item", "cond", "in", "data");
            execution["body-cond"] = Edge("body", "then", "cond", "execute", "execution");
            execution["cond-break"] = Edge("cond", "true", "break", "execute", "execution");
            execution["cond-agent"] = Edge("cond", "false", "agent", "execute", "execution");
        }
        else execution["process"] = Edge("body", "then", "agent", "execute", "execution");
        return new(WorkflowSchemaVersions.UnifiedWorkflowGraph, "for-each-stepping", "input",
            nodes.ToImmutableSortedDictionary(), data.ToImmutableSortedDictionary())
        { ExecutionConnections = execution.ToImmutableSortedDictionary() };
    }

    private static async Task<RunSnapshot> WaitFor(LocalWorkflowRuntime runtime, Guid id, Func<RunSnapshot, bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        while (true)
        {
            var snapshot = (await runtime.GetRunAsync(id))!;
            if (condition(snapshot)) return snapshot;
            await Task.Delay(10, timeout.Token);
        }
    }

    private static int Pauses(RunSnapshot run) => run.Events.Count(e => e.Kind == ExecutionEventKinds.Paused);

    /// <summary>Starts paused, then steps until the final boundary (no node), recording each paused node.</summary>
    private static async Task<(List<string?> Boundaries, RunSnapshot Final)> StepThroughAsync(LocalWorkflowRuntime runtime, Guid id)
    {
        var boundaries = new List<string?>();
        var seen = 0;
        while (true)
        {
            var paused = await WaitFor(runtime, id, r => r.Status == "paused" && Pauses(r) > seen);
            seen = Pauses(paused);
            var node = paused.Events.Last(e => e.Kind == ExecutionEventKinds.Paused).NodeId;
            boundaries.Add(node);
            if (node is null)
            {
                await runtime.ResumeRunAsync(id);
                return (boundaries, await WaitFor(runtime, id, r => r.IsTerminal));
            }
            await runtime.StepRunAsync(id);
        }
    }

    [Fact]
    public async Task Stepping_through_a_for_each_visits_each_iteration_body_in_order_and_ends_on_completed()
    {
        using var runtime = Runtime();
        await runtime.StartAsync(CancellationToken.None);
        try
        {
            var run = await runtime.StartPausedRunAsync(Workflow(withBreak: false), "start");
            var (boundaries, final) = await StepThroughAsync(runtime, run.Header.RunId);

            Assert.Equal(["input", "each", "body", "agent", "each", "body", "agent", "each", "body", "agent", "each", "output", null],
                boundaries);
            Assert.Equal("completed", final.Status);
            Assert.Equal(["each/1", "each/2", "each/3"],
                final.Events.Where(e => e.Kind == ExecutionEventKinds.NodeStarted && e.NodeId == "agent").Select(e => e.ScopeId));
            Assert.Equal(Enumerable.Range(1, final.Events.Length).Select(i => (long)i), final.Events.Select(e => e.Sequence));
        }
        finally { await runtime.StopAsync(CancellationToken.None); }
    }

    [Fact]
    public async Task A_body_breakpoint_hits_every_iteration_and_continue_skips_only_the_current_one()
    {
        using var runtime = Runtime();
        await runtime.StartAsync(CancellationToken.None);
        try
        {
            var run = await runtime.StartPausedRunAsync(Workflow(withBreak: false), "start");
            var id = run.Header.RunId;
            await runtime.SetBreakpointsAsync(id, ["agent"]);
            await runtime.ResumeRunAsync(id);

            for (var iteration = 1; iteration <= 3; iteration++)
            {
                var hit = await WaitFor(runtime, id, r => r.Status == "paused" &&
                    r.Events.Count(e => e.Kind == ExecutionEventKinds.BreakpointHit) == iteration);
                var scopes = hit.Events.Where(e => e.Kind == ExecutionEventKinds.BreakpointHit)
                    .Select(e => JsonDocument.Parse(e.Data!).RootElement.GetProperty("scopeId").GetString()).ToArray();
                Assert.Equal(Enumerable.Range(1, iteration).Select(i => $"each/{i}"), scopes);
                Assert.Equal(iteration - 1, hit.Events.Count(e => e.Kind == ExecutionEventKinds.NodeStarted && e.NodeId == "agent"));
                await runtime.ResumeRunAsync(id);
            }

            var completed = await WaitFor(runtime, id, r => r.IsTerminal);
            Assert.Equal("completed", completed.Status);
            Assert.Equal(3, completed.Events.Count(e => e.Kind == ExecutionEventKinds.BreakpointHit));
            Assert.Equal(3, completed.Events.Count(e => e.Kind == ExecutionEventKinds.NodeStarted && e.NodeId == "agent"));
        }
        finally { await runtime.StopAsync(CancellationToken.None); }
    }

    [Fact]
    public async Task Stepping_across_a_break_lands_on_the_completed_continuation_without_another_iteration()
    {
        using var runtime = Runtime();
        await runtime.StartAsync(CancellationToken.None);
        try
        {
            var run = await runtime.StartPausedRunAsync(Workflow(withBreak: true), "start");
            var (boundaries, final) = await StepThroughAsync(runtime, run.Header.RunId);

            Assert.Equal(["input", "each", "body", "cond", "agent", "each", "body", "cond", "break", "output", null], boundaries);
            Assert.Equal("completed", final.Status);
            Assert.Contains(final.Events, e => e.Kind == ExecutionEventKinds.LoopStopped && e.Data!.Contains("\"break\"", StringComparison.Ordinal));
        }
        finally { await runtime.StopAsync(CancellationToken.None); }
    }

    [Fact]
    public async Task Cancelling_while_paused_inside_a_stepped_iteration_ends_the_run_without_further_nodes()
    {
        using var runtime = Runtime();
        await runtime.StartAsync(CancellationToken.None);
        try
        {
            var run = await runtime.StartPausedRunAsync(Workflow(withBreak: false), "start");
            var id = run.Header.RunId;
            var seen = 0;
            foreach (var expected in new[] { "input", "each", "body" })
            {
                var paused = await WaitFor(runtime, id, r => r.Status == "paused" && Pauses(r) > seen);
                seen = Pauses(paused);
                Assert.Equal(expected, paused.Events.Last(e => e.Kind == ExecutionEventKinds.Paused).NodeId);
                await runtime.StepRunAsync(id);
            }
            await WaitFor(runtime, id, r => r.Status == "paused" && Pauses(r) > seen);

            await runtime.CancelRunAsync(id);
            var terminal = await WaitFor(runtime, id, r => r.IsTerminal);
            Assert.Equal("cancelled", terminal.Status);
            Assert.DoesNotContain(terminal.Events, e => e.Kind == ExecutionEventKinds.NodeStarted && e.NodeId == "agent");
        }
        finally { await runtime.StopAsync(CancellationToken.None); }
    }

    [Fact]
    public async Task A_pause_requested_during_a_step_is_acknowledged_at_the_next_boundary_inside_the_iteration()
    {
        using var runtime = Runtime();
        await runtime.StartAsync(CancellationToken.None);
        try
        {
            var run = await runtime.StartPausedRunAsync(Workflow(withBreak: false), "start");
            var id = run.Header.RunId;
            await runtime.SetBreakpointsAsync(id, ["body"]);
            await runtime.ResumeRunAsync(id);
            var hit = await WaitFor(runtime, id, r => r.Status == "paused" && r.Events.Any(e => e.Kind == ExecutionEventKinds.BreakpointHit));
            await runtime.StepRunAsync(id);
            var next = await WaitFor(runtime, id, r => r.Status == "paused" && Pauses(r) > Pauses(hit));
            Assert.Equal("agent", next.Events.Last(e => e.Kind == ExecutionEventKinds.Paused).NodeId);
            await runtime.PauseRunAsync(id); // already paused: idempotent
            await runtime.SetBreakpointsAsync(id, []);
            await runtime.ResumeRunAsync(id);
            Assert.Equal("completed", (await WaitFor(runtime, id, r => r.IsTerminal)).Status);
        }
        finally { await runtime.StopAsync(CancellationToken.None); }
    }

    public void Dispose()
    {
        try { Directory.Delete(directory, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
