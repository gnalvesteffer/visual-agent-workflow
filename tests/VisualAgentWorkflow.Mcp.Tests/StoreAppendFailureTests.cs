using System.Collections.Immutable;
using VisualAgentWorkflow.AgentFramework;
using VisualAgentWorkflow.Core;
using VisualAgentWorkflow.Hosting;
using Xunit;

namespace VisualAgentWorkflow.Mcp.Tests;

[Collection(McpStdioCollection.Name)]
public sealed class StoreAppendFailureTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ToolCompletionAppendFailure_RecordsUnknownOutcomeAndKeepsContiguousTerminalHistory(bool sqlite)
    {
        var registrations = BrokerTests.Registrations()
            .Where(registration => registration.BindingId == "demo.reverse").ToArray();
        var broker = BrokerTests.CreateBroker(registrations);
        var binding = Assert.Single(await broker.ListBindingsAsync());
        var toolNode = new NodeDefinition(NodeTypes.McpTool, "1.0.0",
            new ToolBindingRequest(binding.BindingId, binding.ContractHash)
            {
                DescriptionHash = binding.DescriptionHash
            })
        {
            McpArgumentsJson = "{\"text\":\"append failure after dispatch\"}"
        };
        var nodes = ImmutableSortedDictionary<string, NodeDefinition>.Empty.WithComparers(StringComparer.Ordinal)
            .Add("input", new(NodeTypes.Input, "1.0.0"))
            .Add("sequence", new(NodeTypes.Sequence, "1.0.0")
            {
                Sequence = new SequenceConfiguration([new("reverse", "Reverse"), new("done", "Done")])
            })
            .Add("reverse", toolNode)
            .Add("output", new(NodeTypes.Output, "1.0.0"));
        var execution = ImmutableSortedDictionary<string, ConnectionDefinition>.Empty.WithComparers(StringComparer.Ordinal)
            .Add("input-sequence", new(new("input", "then"), new("sequence", "execute"), "execution"))
            .Add("sequence-reverse", new(new("sequence", "then.reverse"), new("reverse", "execute"), "execution"))
            .Add("sequence-done", new(new("sequence", "then.done"), new("output", "execute"), "execution"))
            .Add("reverse-output", new(new("reverse", "then"), new("output", "execute"), "execution"));
        var definition = new WorkflowDefinition(WorkflowSchemaVersions.SequencePathGraph,
            "tool-completion-append-failure", "input", nodes,
            ImmutableSortedDictionary<string, ConnectionDefinition>.Empty.WithComparers(StringComparer.Ordinal))
        {
            ExecutionConnections = execution
        };
        var revision = WorkflowRevision.Create(definition, "store-append-failure-proof");
        using var directory = new TemporaryDirectory();
        IWorkflowRunStore underlying = sqlite
            ? new SqliteWorkflowRunStore(Path.Combine(directory.Path, "runs.sqlite3"))
            : new FileRunJournal(directory.Path);
        var store = new RejectingAppendStore(underlying, ExecutionEventKinds.ToolCompleted);
        var runId = store.Create(revision, "not used by the fixed MCP argument").Header.RunId;
        var recorded = new List<ExecutionEvent>();

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new WorkflowRunner(broker).ExecuteWithOutcomeAsync(runId, revision,
                "not used by the fixed MCP argument", null, evt =>
                {
                    store.Append(runId, evt.Sequence - 1, [evt]);
                    recorded.Add(evt);
                    return ValueTask.CompletedTask;
                }));

        Assert.NotNull(failure);
        var persisted = store.Get(runId)!;
        Assert.Equal("failed", persisted.Status);
        Assert.Equal(recorded, persisted.Events);
        Assert.Equal(Enumerable.Range(1, recorded.Count).Select(index => (long)index),
            recorded.Select(evt => evt.Sequence));
        Assert.Contains(recorded, evt => evt.Kind == ExecutionEventKinds.ToolAuthorized);
        Assert.Contains(recorded, evt => evt.Kind == ExecutionEventKinds.ToolUncertain);
        Assert.DoesNotContain(recorded, evt => evt.Kind == ExecutionEventKinds.ToolCompleted);
        Assert.Contains(recorded, evt => evt.Kind == ExecutionEventKinds.SequenceOutputFailed);
        Assert.Contains(recorded, evt => evt.Kind == ExecutionEventKinds.Failed);
    }

    private sealed class RejectingAppendStore(IWorkflowRunStore inner, string rejectedEventKind) : IWorkflowRunStore
    {
        public RunSnapshot Create(WorkflowRevision revision, string input) => inner.Create(revision, input);
        public RunSnapshot? Get(Guid runId) => inner.Get(runId);
        public IReadOnlyList<RunSnapshot> List() => inner.List();

        public RunSnapshot Append(Guid runId, long expectedSequence, IReadOnlyList<ExecutionEvent> events)
        {
            if (events.Any(evt => evt.Kind == rejectedEventKind))
                throw new IOException($"Injected persistence failure for {rejectedEventKind}.");
            return inner.Append(runId, expectedSequence, events);
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
            $"vaw-store-append-failure-{Guid.NewGuid():N}");

        public TemporaryDirectory() => Directory.CreateDirectory(Path);
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
