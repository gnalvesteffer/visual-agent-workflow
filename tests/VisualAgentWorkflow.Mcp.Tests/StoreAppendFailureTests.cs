using System.Collections.Immutable;
using System.Text.Json;
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
    public async Task ToolAuthorizationEvidenceBudgetExhaustion_PreventsDispatchAndPersistsFailure(bool sqlite)
    {
        var registrations = BrokerTests.Registrations()
            .Where(registration => registration.BindingId == "demo.reverse").ToArray();
        var discoveryBroker = BrokerTests.CreateBroker(registrations);
        var binding = Assert.Single(await discoveryBroker.ListBindingsAsync());
        var revision = WorkflowRevision.Create(ToolWorkflow(binding), "mcp-evidence-budget-proof");

        using var baselineDirectory = new TemporaryDirectory();
        IWorkflowRunStore baselineStore = sqlite
            ? new SqliteWorkflowRunStore(Path.Combine(baselineDirectory.Path, "baseline.sqlite3"))
            : new FileRunJournal(baselineDirectory.Path);
        var baselineRunId = baselineStore.Create(revision, "unused").Header.RunId;
        var baselineEvents = new List<ExecutionEvent>();
        await new WorkflowRunner(discoveryBroker).ExecuteWithOutcomeAsync(baselineRunId, revision, "unused", null, evt =>
        {
            baselineStore.Append(baselineRunId, evt.Sequence - 1, [evt]);
            baselineEvents.Add(evt);
            return ValueTask.CompletedTask;
        });
        var authorizationIndex = baselineEvents.FindIndex(evt => evt.Kind == ExecutionEventKinds.ToolAuthorized);
        Assert.True(authorizationIndex > 0);
        var prefix = baselineEvents.Take(authorizationIndex).ToArray();
        var authorization = baselineEvents[authorizationIndex];
        var outcomeBytes = EventBytes(Assert.Single(baselineEvents, evt => evt.Kind == ExecutionEventKinds.ToolCompleted));

        using var limitedDirectory = new TemporaryDirectory();
        IWorkflowRunStore limitedStore = sqlite
            ? new SqliteWorkflowRunStore(Path.Combine(limitedDirectory.Path, "limited.sqlite3"))
            : new FileRunJournal(limitedDirectory.Path);
        var limitedRunId = limitedStore.Create(revision, "unused").Header.RunId;
        var recorder = new McpEvidenceBudgetProofRecorder(limitedStore, limitedRunId,
            maximumEvents: prefix.Length + McpEvidenceBudgetProofRecorder.ReservedFailureEvents,
            maximumBytes: prefix.Sum(EventBytes) + EventBytes(authorization) + outcomeBytes +
                McpEvidenceBudgetProofRecorder.ReservedFailureBytes - 1,
            McpEvidenceBudgetProofRecorder.ReservedFailureEvents,
            McpEvidenceBudgetProofRecorder.ReservedFailureBytes,
            outcomeBytes);

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new WorkflowRunner(BrokerTests.CreateBroker(registrations)).ExecuteWithOutcomeAsync(
                limitedRunId, revision, "unused", null, recorder.RecordAsync));

        Assert.Contains("evidence budget", failure.ToString(), StringComparison.OrdinalIgnoreCase);
        var events = recorder.Snapshot();
        var persisted = limitedStore.Get(limitedRunId)!;
        Assert.Equal("failed", persisted.Status);
        Assert.Equal(events, persisted.Events);
        Assert.Equal(Enumerable.Range(1, events.Length).Select(index => (long)index), events.Select(evt => evt.Sequence));
        Assert.Equal(prefix.Select(evt => evt.Kind), events.Take(prefix.Length).Select(evt => evt.Kind));
        Assert.Contains(events, evt => evt.Kind == ExecutionEventKinds.ToolRequested);
        Assert.DoesNotContain(events, evt => evt.Kind == ExecutionEventKinds.ToolAuthorized);
        Assert.DoesNotContain(events, evt => evt.Kind is ExecutionEventKinds.ToolCompleted or ExecutionEventKinds.ToolUncertain);
        Assert.Contains(events, evt => evt.Kind == ExecutionEventKinds.ToolFailed);
        Assert.Contains(events, evt => evt.Kind == ExecutionEventKinds.Failed);
        Assert.Equal(0, recorder.PendingOutcomeReservations);
        Assert.Equal(events.Sum(EventBytes), recorder.PersistedEventBytes);
        Console.WriteLine($"{(sqlite ? "sqlite" : "file")} MCP authorization reservation rejection: {events.Length} events, " +
            $"{recorder.PersistedEventBytes} UTF-8 bytes, no authorization or tool outcome persisted.");
    }

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
        var recorder = new McpEvidenceBudgetProofRecorder(store, runId,
            maximumEvents: 512,
            maximumBytes: 1_000_000,
            McpEvidenceBudgetProofRecorder.ReservedFailureEvents,
            McpEvidenceBudgetProofRecorder.ReservedFailureBytes,
            maximumOutcomeBytes: 64_000);

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new WorkflowRunner(broker).ExecuteWithOutcomeAsync(runId, revision,
                "not used by the fixed MCP argument", null, recorder.RecordAsync));

        Assert.NotNull(failure);
        var persisted = store.Get(runId)!;
        var recorded = recorder.Snapshot();
        Assert.Equal("failed", persisted.Status);
        Assert.Equal(recorded, persisted.Events);
        Assert.Equal(Enumerable.Range(1, recorded.Length).Select(index => (long)index),
            recorded.Select(evt => evt.Sequence));
        Assert.Contains(recorded, evt => evt.Kind == ExecutionEventKinds.ToolAuthorized);
        Assert.Contains(recorded, evt => evt.Kind == ExecutionEventKinds.ToolUncertain);
        Assert.DoesNotContain(recorded, evt => evt.Kind == ExecutionEventKinds.ToolCompleted);
        Assert.Contains(recorded, evt => evt.Kind == ExecutionEventKinds.SequenceOutputFailed);
        Assert.Contains(recorded, evt => evt.Kind == ExecutionEventKinds.Failed);
        Assert.Equal(0, recorder.PendingOutcomeReservations);
        Assert.Equal(1, recorder.PendingReservationsObservedOnUncertain);
        Assert.Equal(recorded.Sum(EventBytes), recorder.PersistedEventBytes);
        Console.WriteLine($"{(sqlite ? "sqlite" : "file")} uncertain MCP settlement: {recorded.Length} events, " +
            $"{recorder.PersistedEventBytes} UTF-8 bytes; reservation held through rejected completion and settled by uncertainty evidence.");
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

    private static WorkflowDefinition ToolWorkflow(ToolBindingDescriptor binding)
    {
        var nodes = ImmutableSortedDictionary<string, NodeDefinition>.Empty.WithComparers(StringComparer.Ordinal)
            .Add("input", new(NodeTypes.Input, "1.0.0"))
            .Add("sequence", new(NodeTypes.Sequence, "1.0.0")
            {
                Sequence = new SequenceConfiguration([new("reverse", "Reverse"), new("done", "Done")])
            })
            .Add("reverse", new NodeDefinition(NodeTypes.McpTool, "1.0.0",
                new ToolBindingRequest(binding.BindingId, binding.ContractHash)
                {
                    DescriptionHash = binding.DescriptionHash
                })
            {
                McpArgumentsJson = "{\"text\":\"reservation proof\"}"
            })
            .Add("output", new(NodeTypes.Output, "1.0.0"));
        var execution = ImmutableSortedDictionary<string, ConnectionDefinition>.Empty.WithComparers(StringComparer.Ordinal)
            .Add("input-sequence", new(new("input", "then"), new("sequence", "execute"), "execution"))
            .Add("sequence-reverse", new(new("sequence", "then.reverse"), new("reverse", "execute"), "execution"))
            .Add("sequence-done", new(new("sequence", "then.done"), new("output", "execute"), "execution"))
            .Add("reverse-output", new(new("reverse", "then"), new("output", "execute"), "execution"));
        return new WorkflowDefinition(WorkflowSchemaVersions.SequencePathGraph,
            "mcp-evidence-budget-proof", "input", nodes,
            ImmutableSortedDictionary<string, ConnectionDefinition>.Empty.WithComparers(StringComparer.Ordinal))
        {
            ExecutionConnections = execution
        };
    }

    private static long EventBytes(ExecutionEvent evt) =>
        JsonSerializer.SerializeToUtf8Bytes(evt, JsonSerializerOptions.Web).Length;

    private sealed class McpEvidenceBudgetProofRecorder(
        IWorkflowRunStore store,
        Guid runId,
        int maximumEvents,
        long maximumBytes,
        int reservedFailureEvents,
        long reservedFailureBytes,
        long maximumOutcomeBytes)
    {
        public const int ReservedFailureEvents = 8;
        public const long ReservedFailureBytes = 64_000;
        private static readonly HashSet<string> FailureKinds =
        [
            ExecutionEventKinds.ToolFailed,
            ExecutionEventKinds.NodeFailed,
            ExecutionEventKinds.SequenceOutputFailed,
            ExecutionEventKinds.SequenceFailed,
            ExecutionEventKinds.Failed
        ];
        private readonly object gate = new();
        private readonly List<ExecutionEvent> events = [];
        private int ordinaryEvents;
        private int failureEvents;
        private long ordinaryBytes;
        private long failureBytes;
        private long pendingOutcomeBytes;
        private int pendingOutcomeReservations;
        private long persistedEventBytes;
        private int pendingReservationsObservedOnUncertain;

        public int PendingOutcomeReservations { get { lock (gate) return pendingOutcomeReservations; } }
        public long PersistedEventBytes { get { lock (gate) return persistedEventBytes; } }
        public int PendingReservationsObservedOnUncertain { get { lock (gate) return pendingReservationsObservedOnUncertain; } }

        public ValueTask RecordAsync(ExecutionEvent evt)
        {
            lock (gate)
            {
                var bytes = EventBytes(evt);
                var isAuthorization = evt.Kind == ExecutionEventKinds.ToolAuthorized;
                var isOutcome = evt.Kind is ExecutionEventKinds.ToolCompleted or
                    ExecutionEventKinds.ToolFailed or ExecutionEventKinds.ToolUncertain;
                var isFailure = FailureKinds.Contains(evt.Kind);
                if (evt.Kind == ExecutionEventKinds.ToolUncertain)
                    pendingReservationsObservedOnUncertain = pendingOutcomeReservations;

                if (isAuthorization && (ordinaryEvents + 2 > maximumEvents - reservedFailureEvents ||
                    ordinaryBytes + bytes + maximumOutcomeBytes > maximumBytes - reservedFailureBytes))
                    throw new InvalidOperationException("MCP run evidence budget cannot reserve the tool outcome and failure evidence.");
                if (isOutcome && evt.Kind != ExecutionEventKinds.ToolFailed && pendingOutcomeReservations == 0)
                    throw new InvalidOperationException("MCP tool outcome has no proof reservation.");
                if (isOutcome && evt.Kind != ExecutionEventKinds.ToolFailed && bytes > maximumOutcomeBytes)
                    throw new InvalidOperationException("MCP tool outcome exceeded the proof reservation.");
                if (isFailure && (failureEvents >= reservedFailureEvents || failureBytes + bytes > reservedFailureBytes))
                    throw new InvalidOperationException("MCP failure evidence exceeded its proof reserve.");
                if (!isAuthorization && !isOutcome && !isFailure &&
                    (ordinaryEvents + pendingOutcomeReservations + 1 > maximumEvents - reservedFailureEvents ||
                     ordinaryBytes + pendingOutcomeBytes + bytes > maximumBytes - reservedFailureBytes))
                    throw new InvalidOperationException("MCP run evidence budget exhausted.");

                store.Append(runId, evt.Sequence - 1, [evt]);
                events.Add(evt);
                persistedEventBytes += bytes;
                if (isAuthorization)
                {
                    ordinaryEvents++;
                    ordinaryBytes += bytes;
                    pendingOutcomeReservations++;
                    pendingOutcomeBytes += maximumOutcomeBytes;
                }
                else if (isOutcome && evt.Kind != ExecutionEventKinds.ToolFailed)
                {
                    ordinaryEvents++;
                    ordinaryBytes += bytes;
                    pendingOutcomeReservations--;
                    pendingOutcomeBytes -= maximumOutcomeBytes;
                }
                else if (isFailure)
                {
                    failureEvents++;
                    failureBytes += bytes;
                }
                else
                {
                    ordinaryEvents++;
                    ordinaryBytes += bytes;
                }
                return ValueTask.CompletedTask;
            }
        }

        public ExecutionEvent[] Snapshot() { lock (gate) return [.. events]; }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
            $"vaw-store-append-failure-{Guid.NewGuid():N}");

        public TemporaryDirectory() => Directory.CreateDirectory(Path);
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
