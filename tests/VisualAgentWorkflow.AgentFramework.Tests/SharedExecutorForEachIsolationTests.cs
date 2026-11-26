using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Text.Json;
using VisualAgentWorkflow.Core;
using Xunit;

namespace VisualAgentWorkflow.AgentFramework.Tests;

/// <summary>
/// Decision 0070, "Prerequisite proofs: forEachItems isolation and nested-scope playback (2026-11-25)". The For Each
/// executor keeps its live arrays in <c>forEachItems</c>, keyed (parent frame id, node id). Production builds a fresh
/// executor set per run, so no production path activates one For Each instance twice at once; the internal
/// <c>ExecuteSharedExecutorConcurrentActivationsProofAsync</c> seam runs two streaming runs on ONE built workflow.
/// </summary>
public sealed class SharedExecutorForEachIsolationTests
{
    private static readonly TimeSpan Window = TimeSpan.FromSeconds(30);

    private sealed class GateAgent(Func<string, CancellationToken, Task> before) : IAgentNodeExecutor
    {
        public ConcurrentQueue<string> Inputs { get; } = new();

        public Task<IReadOnlyList<AgentProviderDescriptor>> ListProvidersAsync() =>
            Task.FromResult<IReadOnlyList<AgentProviderDescriptor>>([new("fixture", "Fixture", "fixture", false)]);

        public async Task<string> ExecuteAsync(NodeInvocationContext invocation, string input,
            CancellationToken cancellationToken = default)
        {
            Inputs.Enqueue(input);
            await before(input, cancellationToken);
            return input;
        }
    }

    /// <summary>Input (array of string, read from the activation frame's item) to For Each to one Agent body; completed to Output.</summary>
    private static WorkflowDefinition Definition() => new(WorkflowSchemaVersions.UnifiedWorkflowGraph,
        "shared-for-each", "input",
        new Dictionary<string, NodeDefinition>
        {
            ["input"] = new(NodeTypes.Input, "1.0.0") { InputDataType = WorkflowDataTypes.ArrayOf(WorkflowDataTypes.String) },
            ["each"] = new(NodeTypes.ForEach, "1.0.0") { ArrayElementType = WorkflowDataTypes.String },
            ["body"] = new(NodeTypes.LoopBody, "1.0.0"),
            ["agent"] = new(NodeTypes.Agent, "1.0.0", Agent: new("fixture", null, "Process input", [])),
            ["output"] = new(NodeTypes.Output, "1.0.0")
        }.ToImmutableSortedDictionary(),
        new Dictionary<string, ConnectionDefinition>
        {
            ["array"] = new(new("input", "out"), new("each", "array"), "data"),
            ["item"] = new(new("each", "item"), new("agent", "in"), "data"),
            ["result"] = new(new("agent", "out"), new("output", "in"), "data")
        }.ToImmutableSortedDictionary())
    {
        ExecutionConnections = new Dictionary<string, ConnectionDefinition>
        {
            ["start"] = new(new("input", "then"), new("each", "execute"), "execution"),
            ["body-start"] = new(new("each", "body"), new("body", "execute"), "execution"),
            ["process"] = new(new("body", "then"), new("agent", "execute"), "execution"),
            ["return"] = new(new("agent", "out"), new("each", "feedback"), "execution"),
            ["complete"] = new(new("each", "completed"), new("output", "execute"), "execution")
        }.ToImmutableSortedDictionary()
    };

    private static WorkflowRunner.StructuredActivationFrame Frame(int index, params string[] items) =>
        new(Guid.NewGuid(), null, "outer", index, JsonSerializer.Serialize(items),
            ImmutableSortedDictionary<string, string>.Empty.WithComparers(StringComparer.Ordinal));

    [Fact]
    public async Task One_for_each_executor_activated_twice_at_once_under_two_parent_frames_iterates_only_its_own_items()
    {
        var started = new ConcurrentDictionary<string, bool>();
        var bothFirstItemsRunning = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var liveEntriesWhileBothRun = -1;
        Func<string, int>? liveEntries = null;
        var agent = new GateAgent(async (input, token) =>
        {
            // The first item of each activation holds until the other activation's first item is also running, so
            // both activations are provably live on the one executor instance at the same moment.
            if (input is "a1" or "b1")
            {
                started[input] = true;
                if (started.Count == 2)
                {
                    Volatile.Write(ref liveEntriesWhileBothRun, liveEntries!("each"));
                    bothFirstItemsRunning.TrySetResult();
                }
                await bothFirstItemsRunning.Task.WaitAsync(token);
            }
        });
        var runner = new WorkflowRunner(agents: agent);
        var first = Frame(1, "a1", "a2");
        var second = Frame(2, "b1", "b2", "b3");
        var events = new ConcurrentQueue<ExecutionEvent>();

        var outputs = await runner.ExecuteSharedExecutorConcurrentActivationsProofAsync(Guid.NewGuid(),
            WorkflowRevision.Create(Definition(), "shared"), [first, second],
            evt => { events.Enqueue(evt); return ValueTask.CompletedTask; },
            probe => liveEntries = probe).WaitAsync(Window);

        // Each activation returned its own last item; nothing crossed over.
        Assert.Equal(["a2", "b3"], outputs);
        Assert.Equal(["a1", "a2", "b1", "b2", "b3"], agent.Inputs.Order());
        // Both entries lived in the one executor while the activations overlapped, and both are gone now.
        Assert.Equal(2, Volatile.Read(ref liveEntriesWhileBothRun));
        Assert.Equal(0, liveEntries!("each"));

        foreach (var (prefix, count) in new[] { ("outer/1/each/", 2), ("outer/2/each/", 3) })
        {
            var scopes = events.Where(evt => evt.Kind == ExecutionEventKinds.LoopIterationStarted && evt.ScopeId!.StartsWith(prefix, StringComparison.Ordinal))
                .Select(evt => evt.ScopeId!).ToArray();
            Assert.Equal(Enumerable.Range(1, count).Select(n => prefix + n), scopes);
            var completed = events.Where(evt => evt.Kind == ExecutionEventKinds.LoopIterationCompleted && evt.ScopeId!.StartsWith(prefix, StringComparison.Ordinal))
                .Select(evt => evt.ScopeId!).ToArray();
            Assert.Equal(scopes, completed);
        }
        // One array-exhausted stop per activation, each reporting its own item count.
        var stops = events.Where(evt => evt.Kind == ExecutionEventKinds.LoopStopped).Select(evt => evt.Data!).ToArray();
        Assert.Equal(2, stops.Length);
        Assert.Contains(stops, data => data.Contains("\"iterations\":2"));
        Assert.Contains(stops, data => data.Contains("\"iterations\":3"));
    }

    [Fact]
    public async Task A_finished_activation_removes_only_its_own_entry_while_the_other_is_still_running()
    {
        var firstFinished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var liveEntriesAfterFirstFinished = -1;
        Func<string, int>? liveEntries = null;
        var agent = new GateAgent(async (input, token) =>
        {
            // Activation two holds its second item until activation one has exhausted its array.
            if (input == "b2")
            {
                await firstFinished.Task.WaitAsync(token);
                Volatile.Write(ref liveEntriesAfterFirstFinished, liveEntries!("each"));
            }
        });
        var runner = new WorkflowRunner(agents: agent);
        var first = Frame(1, "a1");
        var second = Frame(2, "b1", "b2");
        var events = new ConcurrentQueue<ExecutionEvent>();

        var outputs = await runner.ExecuteSharedExecutorConcurrentActivationsProofAsync(Guid.NewGuid(),
            WorkflowRevision.Create(Definition(), "shared-removal"), [first, second],
            evt =>
            {
                events.Enqueue(evt);
                if (evt.Kind == ExecutionEventKinds.LoopStopped && evt.Data!.Contains("\"iterations\":1"))
                    firstFinished.TrySetResult();
                return ValueTask.CompletedTask;
            }, probe => liveEntries = probe).WaitAsync(Window);

        Assert.Equal(["a1", "b2"], outputs);
        // The first activation's exhaustion removed one entry; the second still had its own array for item two.
        Assert.Equal(1, Volatile.Read(ref liveEntriesAfterFirstFinished));
        Assert.Equal(0, liveEntries!("each"));
    }
}
