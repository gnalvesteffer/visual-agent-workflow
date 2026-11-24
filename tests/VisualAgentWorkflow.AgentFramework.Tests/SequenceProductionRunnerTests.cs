using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Text.Json;
using Microsoft.Extensions.AI;
using VisualAgentWorkflow.Core;
using VisualAgentWorkflow.Hosting;
using Xunit;

namespace VisualAgentWorkflow.AgentFramework.Tests;

public sealed class SequenceProductionRunnerTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Sequence_dispatch_commit_with_lost_ack_preserves_passive_store_prefix(bool sqlite)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"vaw-sequence-delivery-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var innerStore = sqlite
            ? (IWorkflowRunStore)new SqliteWorkflowRunStore(Path.Combine(directory, "runs.sqlite3"))
            : new FileRunJournal(directory);
        try
        {
            var revision = WorkflowRevision.Create(PersistedSequenceDefinition(), "sequence-delivery-prefix");
            var runId = innerStore.Create(revision, "input").Header.RunId;
            var store = new CommitThenThrowRunStore(innerStore, ExecutionEventKinds.SequenceOutputDispatched);
            ValueTask Record(ExecutionEvent evt)
            {
                store.Append(evt.RunId, evt.Sequence - 1, [evt]);
                return ValueTask.CompletedTask;
            }

            await Assert.ThrowsAnyAsync<Exception>(() => new WorkflowRunner().ExecuteWithOutcomeAsync(
                runId, revision, "input", null, Record));

            var persisted = innerStore.Get(runId)!.Events;
            Assert.Equal(Enumerable.Range(1, persisted.Length).Select(value => (long)value),
                persisted.Select(evt => evt.Sequence));
            Assert.Single(persisted, evt => evt.Kind == ExecutionEventKinds.SequenceOutputDispatched);
            Assert.DoesNotContain(persisted, evt => evt.Kind is ExecutionEventKinds.SequenceOutputCompleted or
                ExecutionEventKinds.SequenceFailed or ExecutionEventKinds.Completed or ExecutionEventKinds.Failed);
            Assert.True(SequenceEvidence.TryReconstructPlayback(persisted, out var playback));
            Assert.Equal("running", playback!.Status);
            Assert.Contains(playback.Outputs, output => output.State == "dispatched");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData(false, ExecutionEventKinds.SequenceOutputCompleted)]
    [InlineData(true, ExecutionEventKinds.SequenceOutputCompleted)]
    [InlineData(false, ExecutionEventKinds.SequenceCompleted)]
    [InlineData(true, ExecutionEventKinds.SequenceCompleted)]
    public async Task Sequence_settlement_commit_with_lost_ack_blocks_conflicting_root_terminal(bool sqlite,
        string eventKind)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"vaw-sequence-settlement-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var innerStore = sqlite
            ? (IWorkflowRunStore)new SqliteWorkflowRunStore(Path.Combine(directory, "runs.sqlite3"))
            : new FileRunJournal(directory);
        try
        {
            var revision = WorkflowRevision.Create(PersistedSequenceDefinition(), "sequence-settlement-prefix");
            var runId = innerStore.Create(revision, "input").Header.RunId;
            var store = new CommitThenThrowRunStore(innerStore, eventKind);
            var attempted = new List<ExecutionEvent>();
            ValueTask Record(ExecutionEvent evt)
            {
                attempted.Add(evt);
                store.Append(evt.RunId, evt.Sequence - 1, [evt]);
                return ValueTask.CompletedTask;
            }

            await Assert.ThrowsAsync<InvalidOperationException>(() => new WorkflowRunner()
                .ExecuteWithOutcomeAsync(runId, revision, "input", null, Record));

            var persisted = innerStore.Get(runId)!.Events;
            Assert.Equal(Enumerable.Range(1, persisted.Length).Select(value => (long)value),
                persisted.Select(evt => evt.Sequence));
            Assert.Contains(persisted, evt => evt.Kind == eventKind);
            Assert.DoesNotContain(attempted, evt => evt.NodeId is null && evt.Kind is
                ExecutionEventKinds.Completed or ExecutionEventKinds.Failed or ExecutionEventKinds.Cancelled or
                ExecutionEventKinds.Terminated or ExecutionEventKinds.OutcomeUnknown);
            Assert.DoesNotContain(persisted, evt => evt.NodeId is null && evt.Kind is
                ExecutionEventKinds.Completed or ExecutionEventKinds.Failed or ExecutionEventKinds.Cancelled or
                ExecutionEventKinds.Terminated or ExecutionEventKinds.OutcomeUnknown);
            Assert.True(SequenceEvidence.TryReconstructPlayback(persisted, out var playback));
            Assert.Equal(eventKind == ExecutionEventKinds.SequenceCompleted ? "completed" : "running", playback!.Status);
            Assert.Equal("running", innerStore.Get(runId)!.Status);
            Assert.Contains(playback.Outputs, output => output.State == "completed");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Public_schema_1_53_preserves_a_json_child_result_for_the_output_evaluator(bool unified)
    {
        var child = WorkflowRevision.Create(JsonChild("public-sequence-json-child"), "test");
        var call = new NodeDefinition(NodeTypes.WorkflowCall, "1.0.0")
        {
            WorkflowCall = new(child.Definition.WorkflowId, child.RevisionId, 1, child.ContentHash, child.Definition)
        };
        var definition = new WorkflowDefinition(WorkflowSchemaVersions.SequenceWorkflowCallJsonGraph,
            "public-sequence-json-parent", "input",
            new Dictionary<string, NodeDefinition>
            {
                ["input"] = new(NodeTypes.Input, "1.0.0"),
                ["sequence"] = new(NodeTypes.Sequence, "1.0.0") { Sequence = new([new("call", "Pinned child"), new("sibling", "Second child")]) },
                ["call"] = call,
                ["sibling"] = call with { WorkflowCall = call.WorkflowCall! with { WorkflowId = child.Definition.WorkflowId } },
                ["output"] = new(NodeTypes.Output, "1.0.0") { OutputDataType = WorkflowDataTypes.Json }
            }.ToImmutableSortedDictionary(),
            new Dictionary<string, ConnectionDefinition>
            {
                ["call-json-output"] = new(new("call", "out"), new("output", "in"), "data")
            }.ToImmutableSortedDictionary())
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
        if (unified) definition = definition with { SchemaVersion = WorkflowSchemaVersions.UnifiedWorkflowGraph };
        var events = new List<ExecutionEvent>();

        var result = await new WorkflowRunner().ExecuteWithOutcomeAsync(Guid.NewGuid(),
            WorkflowRevision.Create(WorkflowJson.Deserialize(WorkflowJson.Serialize(definition)), "test"),
            "{\"answer\":\"hello\"}", null,
            evt => { events.Add(evt); return ValueTask.CompletedTask; });

        Assert.False(result.Failed, string.Join(" | ", events.Select(evt => $"{evt.Kind}:{evt.NodeId}:{evt.Data}")));
        Assert.Equal("{\"answer\":\"hello\"}", result.Output);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Public_schema_1_54_evaluates_a_two_node_json_chain_and_records_each_value(bool unified)
    {
        var child = WorkflowRevision.Create(JsonChild("public-sequence-json-chain-child"), "test");
        var call = new NodeDefinition(NodeTypes.WorkflowCall, "1.0.0")
        {
            WorkflowCall = new(child.Definition.WorkflowId, child.RevisionId, 1, child.ContentHash, child.Definition)
        };
        var definition = new WorkflowDefinition(WorkflowSchemaVersions.SequenceWorkflowCallJsonChainGraph,
            "public-sequence-json-chain-parent", "input",
            new Dictionary<string, NodeDefinition>
            {
                ["input"] = new(NodeTypes.Input, "1.0.0"),
                ["sequence"] = new(NodeTypes.Sequence, "1.0.0") { Sequence = new([new("call", "Pinned child"), new("sibling", "Second child")]) },
                ["call"] = call,
                ["sibling"] = call with { WorkflowCall = call.WorkflowCall! with { WorkflowId = child.Definition.WorkflowId } },
                ["project"] = new(NodeTypes.JsonProject, "1.0.0")
                {
                    JsonProjection = new([new JsonProjectionField("answer", "/answer")])
                },
                ["select"] = new(NodeTypes.JsonSelectString, "1.0.0")
                {
                    JsonPropertySelection = new("/answer")
                },
                ["output"] = new(NodeTypes.Output, "1.0.0") { OutputDataType = WorkflowDataTypes.String }
            }.ToImmutableSortedDictionary(),
            new Dictionary<string, ConnectionDefinition>
            {
                ["call-project"] = new(new("call", "out"), new("project", "in"), "data"),
                ["project-select"] = new(new("project", "out"), new("select", "in"), "data"),
                ["select-output-data"] = new(new("select", "out"), new("output", "in"), "data")
            }.ToImmutableSortedDictionary())
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
        if (unified) definition = definition with { SchemaVersion = WorkflowSchemaVersions.UnifiedWorkflowGraph };
        var events = new List<ExecutionEvent>();

        var result = await new WorkflowRunner().ExecuteWithOutcomeAsync(Guid.NewGuid(),
            WorkflowRevision.Create(WorkflowJson.Deserialize(WorkflowJson.Serialize(definition)), "test"),
            "{\"answer\":\"hello\"}", null,
            evt => { events.Add(evt); return ValueTask.CompletedTask; });

        Assert.False(result.Failed, string.Join(" | ", events.Select(evt => $"{evt.Kind}:{evt.NodeId}:{evt.Data}")));
        Assert.Equal("hello", result.Output);
        Assert.Contains(events, evt => evt.Kind == ExecutionEventKinds.DataEvaluated && evt.NodeId == "project");
        Assert.Contains(events, evt => evt.Kind == ExecutionEventKinds.DataEvaluated && evt.NodeId == "select");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Public_schema_1_55_merges_two_consumers_of_one_json_child_output(bool unified)
    {
        var child = WorkflowRevision.Create(JsonChild("public-sequence-json-merge-child"), "test");
        var call = new NodeDefinition(NodeTypes.WorkflowCall, "1.0.0")
        {
            WorkflowCall = new(child.Definition.WorkflowId, child.RevisionId, 1, child.ContentHash, child.Definition)
        };
        var definition = new WorkflowDefinition(WorkflowSchemaVersions.SequenceWorkflowCallJsonMergeGraph,
            "public-sequence-json-merge-parent", "input",
            new Dictionary<string, NodeDefinition>
            {
                ["input"] = new(NodeTypes.Input, "1.0.0"),
                ["sequence"] = new(NodeTypes.Sequence, "1.0.0") { Sequence = new([new("call", "Pinned child"), new("sibling", "Sibling")]) },
                ["call"] = call,
                ["sibling"] = new(NodeTypes.McpTool, "1.0.0", new("sibling", new string('a', 64))) { McpArgumentsJson = "{}" },
                ["project"] = new(NodeTypes.JsonProject, "1.0.0")
                {
                    JsonProjection = new([new JsonProjectionField("answer_copy", "/answer")])
                },
                ["merge"] = new(NodeTypes.JsonMerge, "1.0.0"),
                ["output"] = new(NodeTypes.Output, "1.0.0") { OutputDataType = WorkflowDataTypes.Json }
            }.ToImmutableSortedDictionary(),
            new Dictionary<string, ConnectionDefinition>
            {
                ["call-merge"] = new(new("call", "out"), new("merge", "left"), "data"),
                ["call-project"] = new(new("call", "out"), new("project", "in"), "data"),
                ["project-merge"] = new(new("project", "out"), new("merge", "right"), "data"),
                ["merge-output"] = new(new("merge", "out"), new("output", "in"), "data")
            }.ToImmutableSortedDictionary())
        {
            ExecutionConnections = new Dictionary<string, ConnectionDefinition>
            {
                ["input-sequence"] = new(new("input", "then"), new("sequence", "execute"), "execution"),
                ["sequence-call"] = new(new("sequence", "then.call"), new("call", "execute"), "execution"),
                ["call-output"] = new(new("call", "out"), new("output", "execute"), "execution"),
                ["sequence-sibling"] = new(new("sequence", "then.sibling"), new("sibling", "execute"), "execution"),
                ["sibling-output"] = new(new("sibling", "then"), new("output", "execute"), "execution")
            }.ToImmutableSortedDictionary()
        };
        if (unified) definition = definition with { SchemaVersion = WorkflowSchemaVersions.UnifiedWorkflowGraph };
        var events = new List<ExecutionEvent>();

        var result = await new WorkflowRunner(new SequencePathBroker((_, _) => Task.FromResult("{}"))).ExecuteWithOutcomeAsync(Guid.NewGuid(),
            WorkflowRevision.Create(WorkflowJson.Deserialize(WorkflowJson.Serialize(definition)), "test"),
            "{\"base\":\"hello\",\"answer\":\"hello\"}", null,
            evt => { events.Add(evt); return ValueTask.CompletedTask; });

        Assert.False(result.Failed, string.Join(" | ", events.Select(evt => $"{evt.Kind}:{evt.NodeId}:{evt.Data}")));
        Assert.Equal("{\"answer\":\"hello\",\"answer_copy\":\"hello\",\"base\":\"hello\"}", result.Output);
        Assert.True(events.Count(evt => evt.Kind == ExecutionEventKinds.SubworkflowStarted) == 1,
            string.Join(" | ", events.Select(evt => $"{evt.Kind}:{evt.NodeId}:{evt.ScopeId}")));
        Assert.Contains(events, evt => evt.Kind == ExecutionEventKinds.DataEvaluated && evt.NodeId == "project");
        Assert.Contains(events, evt => evt.Kind == ExecutionEventKinds.DataEvaluated && evt.NodeId == "merge");
        // The two execution paths each resolve Output once; within each resolution the
        // merge's two consumers reuse the same cached child value, producing two
        // connection-correlated evaluations rather than re-running the child.
        Assert.Equal(4, events.Count(evt => evt.Kind == ExecutionEventKinds.DataEvaluated && evt.NodeId == "call"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Public_schema_1_56_refines_a_json_array_and_records_the_integer_length(bool unified)
    {
        var child = WorkflowRevision.Create(JsonChild("public-sequence-json-array-child"), "test");
        var call = new NodeDefinition(NodeTypes.WorkflowCall, "1.0.0")
        {
            WorkflowCall = new(child.Definition.WorkflowId, child.RevisionId, 1, child.ContentHash, child.Definition)
        };
        var definition = new WorkflowDefinition(WorkflowSchemaVersions.SequenceWorkflowCallJsonArrayGraph,
            "public-sequence-json-array-parent", "input",
            new Dictionary<string, NodeDefinition>
            {
                ["input"] = new(NodeTypes.Input, "1.0.0"),
                ["sequence"] = new(NodeTypes.Sequence, "1.0.0") { Sequence = new([new("call", "Pinned child"), new("sibling", "Sibling")]) },
                ["call"] = call,
                ["sibling"] = new(NodeTypes.McpTool, "1.0.0", new("sibling", new string('a', 64))) { McpArgumentsJson = "{}" },
                ["to-array"] = new(NodeTypes.JsonToArray, "1.0.0"),
                ["length"] = new(NodeTypes.ArrayLength, "1.0.0"),
                ["output"] = new(NodeTypes.Output, "1.0.0") { OutputDataType = WorkflowDataTypes.Integer }
            }.ToImmutableSortedDictionary(),
            new Dictionary<string, ConnectionDefinition>
            {
                ["call-array"] = new(new("call", "out"), new("to-array", "in"), "data"),
                ["array-length"] = new(new("to-array", "out"), new("length", "in"), "data"),
                ["length-output"] = new(new("length", "out"), new("output", "in"), "data")
            }.ToImmutableSortedDictionary())
        {
            ExecutionConnections = new Dictionary<string, ConnectionDefinition>
            {
                ["input-sequence"] = new(new("input", "then"), new("sequence", "execute"), "execution"),
                ["sequence-call"] = new(new("sequence", "then.call"), new("call", "execute"), "execution"),
                ["call-output"] = new(new("call", "out"), new("output", "execute"), "execution"),
                ["sequence-sibling"] = new(new("sequence", "then.sibling"), new("sibling", "execute"), "execution"),
                ["sibling-output"] = new(new("sibling", "then"), new("output", "execute"), "execution")
            }.ToImmutableSortedDictionary()
        };
        if (unified) definition = definition with { SchemaVersion = WorkflowSchemaVersions.UnifiedWorkflowGraph };
        var events = new List<ExecutionEvent>();

        var result = await new WorkflowRunner(new SequencePathBroker((_, _) => Task.FromResult("{}"))).ExecuteWithOutcomeAsync(
            Guid.NewGuid(), WorkflowRevision.Create(WorkflowJson.Deserialize(WorkflowJson.Serialize(definition)), "test"),
            "[1,2,3]", null, evt => { events.Add(evt); return ValueTask.CompletedTask; });

        Assert.False(result.Failed, string.Join(" | ", events.Select(evt => $"{evt.Kind}:{evt.NodeId}:{evt.Data}")));
        Assert.Equal("3", result.Output);
        Assert.Contains(events, evt => evt.Kind == ExecutionEventKinds.DataEvaluated && evt.NodeId == "to-array");
        Assert.Contains(events, evt => evt.Kind == ExecutionEventKinds.DataEvaluated && evt.NodeId == "length");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Public_schema_1_57_routes_equal_length_to_output_and_other_lengths_to_failure(bool unified)
    {
        var child = WorkflowRevision.Create(JsonChild("public-sequence-json-control-child"), "test");
        var call = new NodeDefinition(NodeTypes.WorkflowCall, "1.0.0")
        {
            WorkflowCall = new(child.Definition.WorkflowId, child.RevisionId, 1, child.ContentHash, child.Definition)
        };
        var definition = new WorkflowDefinition(WorkflowSchemaVersions.SequenceWorkflowCallJsonControlGraph,
            "public-sequence-json-control-parent", "input",
            new Dictionary<string, NodeDefinition>
            {
                ["input"] = new(NodeTypes.Input, "1.0.0"),
                ["sequence"] = new(NodeTypes.Sequence, "1.0.0") { Sequence = new([new("call", "Pinned child"), new("sibling", "Sibling")]) },
                ["call"] = call,
                ["sibling"] = new(NodeTypes.McpTool, "1.0.0", new("sibling", new string('a', 64))) { McpArgumentsJson = "{}" },
                ["to-array"] = new(NodeTypes.JsonToArray, "1.0.0"),
                ["length"] = new(NodeTypes.ArrayLength, "1.0.0"),
                ["equals"] = new(NodeTypes.IntegerEquals, "1.0.0"),
                ["constant"] = new(NodeTypes.IntegerConstant, "1.0.0") { IntegerConstant = new(3) },
                ["branch"] = new(NodeTypes.BooleanBranch, "1.0.0"),
                ["terminate"] = new(NodeTypes.Terminate, "1.0.0") { Termination = new(WorkflowTerminationOutcomes.Failure) },
                ["output"] = new(NodeTypes.Output, "1.0.0") { OutputDataType = WorkflowDataTypes.Integer }
            }.ToImmutableSortedDictionary(),
            new Dictionary<string, ConnectionDefinition>
            {
                ["call-array"] = new(new("call", "out"), new("to-array", "in"), "data"),
                ["array-length"] = new(new("to-array", "out"), new("length", "in"), "data"),
                ["length-equals"] = new(new("length", "out"), new("equals", "left"), "data"),
                ["constant-equals"] = new(new("constant", "out"), new("equals", "right"), "data"),
                ["equals-branch"] = new(new("equals", "out"), new("branch", "condition"), "data"),
                ["length-output"] = new(new("length", "out"), new("output", "in"), "data")
            }.ToImmutableSortedDictionary())
        {
            ExecutionConnections = new Dictionary<string, ConnectionDefinition>
            {
                ["input-sequence"] = new(new("input", "then"), new("sequence", "execute"), "execution"),
                ["sequence-call"] = new(new("sequence", "then.call"), new("call", "execute"), "execution"),
                ["call-branch"] = new(new("call", "out"), new("branch", "execute"), "execution"),
                ["branch-output"] = new(new("branch", "true"), new("output", "execute"), "execution"),
                ["branch-terminate"] = new(new("branch", "false"), new("terminate", "execute"), "execution"),
                ["sequence-sibling"] = new(new("sequence", "then.sibling"), new("sibling", "execute"), "execution"),
                ["sibling-output"] = new(new("sibling", "then"), new("output", "execute"), "execution")
            }.ToImmutableSortedDictionary()
        };

        if (unified) definition = definition with { SchemaVersion = WorkflowSchemaVersions.UnifiedWorkflowGraph };
        var equalEvents = new List<ExecutionEvent>();
        var equal = await new WorkflowRunner(new SequencePathBroker((_, _) => Task.FromResult("{}"))).ExecuteWithOutcomeAsync(
            Guid.NewGuid(), WorkflowRevision.Create(WorkflowJson.Deserialize(WorkflowJson.Serialize(definition)), "test"),
            "[1,2,3]", null, evt => { equalEvents.Add(evt); return ValueTask.CompletedTask; });
        Assert.False(equal.Failed, string.Join(" | ", equalEvents.Select(evt => $"{evt.Kind}:{evt.NodeId}:{evt.Data}")));
        Assert.Equal("3", equal.Output);
        Assert.Contains(equalEvents, evt => evt.Kind == ExecutionEventKinds.BranchSelected && evt.NodeId == "branch");

        var unequalEvents = new List<ExecutionEvent>();
        var unequal = await new WorkflowRunner(new SequencePathBroker((_, _) => Task.FromResult("{}"))).ExecuteWithOutcomeAsync(
            Guid.NewGuid(), WorkflowRevision.Create(WorkflowJson.Deserialize(WorkflowJson.Serialize(definition)), "test"),
            "[1,2]", null, evt => { unequalEvents.Add(evt); return ValueTask.CompletedTask; });
        Assert.True(unequal.Failed);
        Assert.Contains(unequalEvents, evt => evt.Kind == ExecutionEventKinds.SequenceOutputFailed);
        Assert.Contains(unequalEvents, evt => evt.Kind == ExecutionEventKinds.BranchSelected && evt.NodeId == "branch");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Public_schema_1_52_passes_a_pinned_workflow_call_result_to_one_terminal_agent(bool unified)
    {
        var child = WorkflowRevision.Create(
            WorkflowEditing.InsertAfter(LinearBaseline("public-sequence-agent-child"), "input", NodeTypes.Uppercase),
            "test");
        var call = new NodeDefinition(NodeTypes.WorkflowCall, "1.0.0")
        {
            WorkflowCall = new(child.Definition.WorkflowId, child.RevisionId, 1, child.ContentHash, child.Definition)
        };
        var sibling = call with { WorkflowCall = call.WorkflowCall! with { WorkflowId = child.Definition.WorkflowId } };
        var agent = new NodeDefinition(NodeTypes.Agent, "1.0.0", Agent: new AgentConfiguration(
            "sequence-proof", null, "Return the post-call text.", [] )
        { MaxTurns = 1, MaxTotalTokens = 4000, ProviderContractHash = new string('b', 64) });
        var definition = new WorkflowDefinition(WorkflowSchemaVersions.SequenceWorkflowCallAgentGraph,
            "public-sequence-agent-parent", "input",
            new Dictionary<string, NodeDefinition>
            {
                ["input"] = new(NodeTypes.Input, "1.0.0"),
                ["sequence"] = new(NodeTypes.Sequence, "1.0.0") { Sequence = new([new("call", "Pinned child"), new("sibling", "Second child")]) },
                ["call"] = call,
                ["sibling"] = sibling,
                ["agent"] = agent,
                ["output"] = new(NodeTypes.Output, "1.0.0")
            }.ToImmutableSortedDictionary(), ImmutableSortedDictionary<string, ConnectionDefinition>.Empty)
        {
            ExecutionConnections = new Dictionary<string, ConnectionDefinition>
            {
                ["input-sequence"] = new(new("input", "then"), new("sequence", "execute"), "execution"),
                ["sequence-call"] = new(new("sequence", "then.call"), new("call", "execute"), "execution"),
                ["call-agent"] = new(new("call", "out"), new("agent", "execute"), "execution"),
                ["agent-output"] = new(new("agent", "out"), new("output", "execute"), "execution"),
                ["sequence-sibling"] = new(new("sequence", "then.sibling"), new("sibling", "execute"), "execution"),
                ["sibling-output"] = new(new("sibling", "out"), new("output", "execute"), "execution")
            }.ToImmutableSortedDictionary()
        };
        if (unified) definition = definition with { SchemaVersion = WorkflowSchemaVersions.UnifiedWorkflowGraph };
        var events = new List<ExecutionEvent>();
        var agentExecutor = new SequenceProofAgentExecutor();

        var result = await new WorkflowRunner(new RecordingBroker(), agentExecutor).ExecuteWithOutcomeAsync(
            Guid.NewGuid(), WorkflowRevision.Create(WorkflowJson.Deserialize(WorkflowJson.Serialize(definition)), "test"),
            "hello", null, evt => { events.Add(evt); return ValueTask.CompletedTask; });

        Assert.False(result.Failed, string.Join(" | ", events.Select(evt => $"{evt.Kind}:{evt.NodeId}:{evt.Data}")));
        Assert.Equal("HELLO", result.Output);
        Assert.True(agentExecutor.InvocationObserved);
        Assert.Contains(events, evt => evt.Kind == ExecutionEventKinds.AgentStarted && evt.NodeId == "agent");
        Assert.Contains(events, evt => evt.Kind == ExecutionEventKinds.AgentCompleted && evt.NodeId == "agent");
        Assert.Contains(events, evt => evt.Kind == ExecutionEventKinds.SubworkflowCompleted && evt.ScopeId == "call");
    }

    private static WorkflowDefinition JsonChild(string workflowId) => new("1.13", workflowId, "input",
        new Dictionary<string, NodeDefinition>
        {
            ["input"] = new(NodeTypes.Input, "1.0.0") { InputDataType = WorkflowDataTypes.Json },
            ["output"] = new(NodeTypes.Output, "1.0.0") { OutputDataType = WorkflowDataTypes.Json }
        }.ToImmutableSortedDictionary(),
        new Dictionary<string, ConnectionDefinition>
        {
            ["value"] = new(new("input", "out"), new("output", "in"), "data")
        }.ToImmutableSortedDictionary());

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Public_schema_1_51_applies_one_transform_after_a_pinned_workflow_call(bool unified)
    {
        var child = WorkflowRevision.Create(
            WorkflowEditing.InsertAfter(LinearBaseline("public-sequence-transform-child"), "input", NodeTypes.Uppercase),
            "test");
        var call = new NodeDefinition(NodeTypes.WorkflowCall, "1.0.0")
        {
            WorkflowCall = new(child.Definition.WorkflowId, child.RevisionId, 1, child.ContentHash, child.Definition)
        };
        var sibling = call with { WorkflowCall = call.WorkflowCall! with { WorkflowId = child.Definition.WorkflowId } };
        var definition = new WorkflowDefinition(WorkflowSchemaVersions.SequenceWorkflowCallTransformGraph,
            "public-sequence-transform-parent", "input",
            new Dictionary<string, NodeDefinition>
            {
                ["input"] = new(NodeTypes.Input, "1.0.0"),
                ["sequence"] = new(NodeTypes.Sequence, "1.0.0") { Sequence = new([new("call", "Pinned child"), new("sibling", "Second child")]) },
                ["call"] = call,
                ["sibling"] = sibling,
                ["reverse"] = new(NodeTypes.SequenceReverse, "1.0.0"),
                ["output"] = new(NodeTypes.Output, "1.0.0")
            }.ToImmutableSortedDictionary(), ImmutableSortedDictionary<string, ConnectionDefinition>.Empty)
        {
            ExecutionConnections = new Dictionary<string, ConnectionDefinition>
            {
                ["input-sequence"] = new(new("input", "then"), new("sequence", "execute"), "execution"),
                ["sequence-call"] = new(new("sequence", "then.call"), new("call", "execute"), "execution"),
                ["call-reverse"] = new(new("call", "out"), new("reverse", "execute"), "execution"),
                ["reverse-output"] = new(new("reverse", "then"), new("output", "execute"), "execution"),
                ["sequence-sibling"] = new(new("sequence", "then.sibling"), new("sibling", "execute"), "execution"),
                ["sibling-output"] = new(new("sibling", "out"), new("output", "execute"), "execution")
            }.ToImmutableSortedDictionary()
        };
        if (unified) definition = definition with { SchemaVersion = WorkflowSchemaVersions.UnifiedWorkflowGraph };
        var revision = WorkflowRevision.Create(WorkflowJson.Deserialize(WorkflowJson.Serialize(definition)), "test");
        var events = new List<ExecutionEvent>();

        var result = await new WorkflowRunner().ExecuteWithOutcomeAsync(Guid.NewGuid(), revision, "hello", null,
            evt => { events.Add(evt); return ValueTask.CompletedTask; });

        Assert.False(result.Failed, string.Join(" | ", events.Select(evt => $"{evt.Kind}:{evt.Data}")));
        Assert.Equal("OLLEH", result.Output);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Public_schema_1_50_executes_a_pinned_workflow_call_through_the_normal_runner(bool unified)
    {
        var child = WorkflowRevision.Create(
            WorkflowEditing.InsertAfter(LinearBaseline("public-sequence-child"), "input", NodeTypes.Uppercase),
            "test");
        var call = new NodeDefinition(NodeTypes.WorkflowCall, "1.0.0")
        {
            WorkflowCall = new(child.Definition.WorkflowId, child.RevisionId, 1, child.ContentHash, child.Definition)
        };
        var siblingCall = call with
        {
            WorkflowCall = call.WorkflowCall! with { WorkflowId = child.Definition.WorkflowId }
        };
        var parent = new WorkflowDefinition(WorkflowSchemaVersions.SequenceWorkflowCallGraph,
            "public-sequence-parent", "input",
            new Dictionary<string, NodeDefinition>
            {
                ["input"] = new(NodeTypes.Input, "1.0.0"),
                ["sequence"] = new(NodeTypes.Sequence, "1.0.0")
                {
                    Sequence = new([new("call", "Pinned child"), new("sibling", "Pinned child again")])
                },
                ["call"] = call,
                ["sibling"] = siblingCall,
                ["output"] = new(NodeTypes.Output, "1.0.0")
            }.ToImmutableSortedDictionary(),
            ImmutableSortedDictionary<string, ConnectionDefinition>.Empty)
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
        if (unified) parent = parent with { SchemaVersion = WorkflowSchemaVersions.UnifiedWorkflowGraph };
        var revision = WorkflowRevision.Create(WorkflowJson.Deserialize(WorkflowJson.Serialize(parent)), "test");
        var events = new List<ExecutionEvent>();

        var result = await new WorkflowRunner().ExecuteWithOutcomeAsync(Guid.NewGuid(), revision, "hello", null,
            evt => { events.Add(evt); return ValueTask.CompletedTask; });

        Assert.False(result.Failed, string.Join(" | ", events.Select(evt => $"{evt.Kind}:{evt.NodeId}:{evt.Data}")));
        Assert.Equal("HELLO", result.Output);
        Assert.Contains(events, evt => evt.Kind == ExecutionEventKinds.SubworkflowCompleted && evt.ScopeId == "call");
        Assert.Contains(events, evt => evt.Kind == ExecutionEventKinds.SubworkflowCompleted && evt.ScopeId == "sibling");
        Assert.Contains(events, evt => evt.Kind == ExecutionEventKinds.Completed && evt.ScopeId is null);
    }

    private static WorkflowDefinition LinearBaseline(string workflowId) => new(
        "1.0", workflowId, "input",
        new Dictionary<string, NodeDefinition>
        {
            ["input"] = new(NodeTypes.Input, "1.0.0"),
            ["output"] = new(NodeTypes.Output, "1.0.0")
        }.ToImmutableSortedDictionary(),
        new Dictionary<string, ConnectionDefinition>
        {
            ["edge"] = new(new("input", "out"), new("output", "in"), "data")
        }.ToImmutableSortedDictionary());

    [Fact]
    public async Task Proof_sequence_output_can_settle_through_a_pinned_child_workflow_call()
    {
        var events = new List<ExecutionEvent>();
        var runId = Guid.NewGuid();

        var result = await new WorkflowRunner().ExecuteSequenceWorkflowCallProductionProofAsync(runId, "hello",
            evt => { events.Add(evt); return ValueTask.CompletedTask; });

        Assert.False(result.Failed);
        Assert.Equal("HELLO", result.Output);
        Assert.Contains(events, evt => evt.Kind == ExecutionEventKinds.SubworkflowStarted && evt.ScopeId == "call");
        Assert.Contains(events, evt => evt.Kind == ExecutionEventKinds.SubworkflowCompleted && evt.ScopeId == "call");
        var childEvidence = events.Where(evt => evt.Kind == ExecutionEventKinds.SubworkflowEvidence)
            .Select(ReadChildEvidence).ToArray();
        Assert.Contains(childEvidence, evidence => evidence.CallNodeId == "call" &&
            evidence.ChildWorkflowId == "sequence-call-proof-first");
        Assert.Contains(childEvidence, evidence => evidence.CallNodeId == "sibling" &&
            evidence.ChildWorkflowId == "sequence-call-proof-second");
        Assert.True(WorkflowCallEvidence.TryReconstructPlayback(events, out var playback));
        Assert.Equal("completed", playback!.Status);
        Assert.Equal(2, playback.Calls.Count);
        Assert.Equal(["call", "sibling"], events.Where(evt => evt.Kind == ExecutionEventKinds.SequenceOutputCompleted)
            .Select(ReadEvidence).Select(evidence => evidence.OutputId).OrderBy(id => id, StringComparer.Ordinal));
        Assert.Equal(ExecutionEventKinds.Completed, events[^1].Kind);
    }

    [Fact]
    public async Task Proof_sequence_failure_termination_settles_through_the_production_collector()
    {
        var events = new List<ExecutionEvent>();

        var result = await new WorkflowRunner().ExecuteSequenceTerminationProductionProofAsync(
            Guid.NewGuid(), "hello", evt =>
            {
                events.Add(evt);
                return ValueTask.CompletedTask;
            });

        Assert.True(result.Failed);
        Assert.Null(result.Output);
        Assert.Contains(events, evt => evt.Kind == ExecutionEventKinds.SequenceOutputFailed &&
            ReadEvidence(evt).OutputId == "terminate");
        Assert.Contains(events, evt => evt.Kind == ExecutionEventKinds.SequenceFailed);
        Assert.Contains(events, evt => evt.Kind == ExecutionEventKinds.Failed && evt.ScopeId is null);
        Assert.DoesNotContain(events, evt => evt.Kind == ExecutionEventKinds.Completed && evt.ScopeId is null);
    }

    [Fact]
    public async Task Proof_sequence_child_failure_settles_the_output_and_fails_the_parent()
    {
        var events = new List<ExecutionEvent>();
        // Hold the sibling's child at its input node so it is provably still running when the call
        // fails: the cascade-cancel projection below must be deterministic, not a scheduling coincidence.
        var gate = new ReleaseGate("sequence-call-proof-second-input");
        var outputFailed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var execution = new WorkflowRunner().ExecuteSequenceWorkflowCallProductionProofAsync(Guid.NewGuid(), "hello",
            evt =>
            {
                events.Add(evt);
                if (evt.Kind == ExecutionEventKinds.SequenceOutputFailed && ReadEvidence(evt).OutputId == "call")
                    outputFailed.TrySetResult(true);
                return ValueTask.CompletedTask;
            }, SequenceWorkflowCallProofOutcome.Failure, gate: gate);

        await gate.Blocked.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await outputFailed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        gate.Release.TrySetResult();
        var result = await execution;

        Assert.True(result.Failed);
        Assert.Null(result.Output);
        Assert.Contains(events, evt => evt.Kind == ExecutionEventKinds.SubworkflowFailed && evt.ScopeId == "call");
        Assert.Contains(events, evt => evt.Kind == ExecutionEventKinds.SequenceOutputFailed &&
            ReadEvidence(evt).OutputId == "call");
        // The sibling was still running when the barrier settled: it is cascade-cancelled at the sequence level...
        Assert.Contains(events, evt => evt.Kind == ExecutionEventKinds.SequenceOutputCancelled &&
            ReadEvidence(evt).OutputId == "sibling");
        Assert.Contains(events, evt => evt.Kind == ExecutionEventKinds.SequenceFailed);
        // ...while its child completes at the call level once the gate releases (the documented two-level divergence).
        Assert.Contains(events, evt => evt.Kind == ExecutionEventKinds.SubworkflowCompleted && evt.ScopeId == "sibling");
    }

    [Fact]
    public async Task Proof_sequence_child_successful_termination_does_not_prematurely_complete_the_parent()
    {
        var events = new List<ExecutionEvent>();

        var result = await new WorkflowRunner().ExecuteSequenceWorkflowCallProductionProofAsync(Guid.NewGuid(), "hello",
            evt => { events.Add(evt); return ValueTask.CompletedTask; }, SequenceWorkflowCallProofOutcome.Termination);

        Assert.False(result.Terminated);
        Assert.False(result.Failed);
        Assert.Equal("HELLO", result.Output);
        Assert.Contains(events, evt => evt.Kind == ExecutionEventKinds.SubworkflowTerminated && evt.ScopeId == "call");
        Assert.Contains(events, evt => evt.Kind == ExecutionEventKinds.SequenceOutputCompleted &&
            ReadEvidence(evt).OutputId == "call");
        Assert.Equal(ExecutionEventKinds.Completed, events[^1].Kind);
    }

    [Fact]
    public async Task Proof_sequence_barrier_waits_for_a_delayed_child_and_records_out_of_order_completion()
    {
        var events = new List<ExecutionEvent>();
        var gate = new ReleaseGate("sequence-call-proof-first-input");
        var execution = new WorkflowRunner().ExecuteSequenceWorkflowCallProductionProofAsync(Guid.NewGuid(), "hello",
            evt => { events.Add(evt); return ValueTask.CompletedTask; }, gate: gate);

        await gate.Blocked.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(50);
        Assert.True(events.Any(evt => evt.Kind == ExecutionEventKinds.SubworkflowCompleted &&
            evt.ScopeId == "sibling"),
            string.Join(" | ", events.Select(evt => $"{evt.Sequence}:{evt.Kind}:{evt.NodeId}:{evt.ScopeId}")));
        Assert.DoesNotContain(events, evt => evt.Kind == ExecutionEventKinds.Completed && evt.ScopeId is null);

        gate.Release.TrySetResult();
        var result = await execution;

        Assert.False(result.Failed);
        Assert.Equal("HELLO", result.Output);
        var completed = events.Where(evt => evt.Kind == ExecutionEventKinds.SubworkflowCompleted)
            .ToArray();
        Assert.True(Array.IndexOf(events.ToArray(), completed.Single(evt => evt.ScopeId == "sibling")) <
            Array.IndexOf(events.ToArray(), completed.Single(evt => evt.ScopeId == "call")));
        Assert.Equal(2, events.Count(evt => evt.Kind == ExecutionEventKinds.SequenceOutputCompleted));
        Assert.Equal(ExecutionEventKinds.Completed, events[^1].Kind);
    }

    [Fact]
    public async Task Proof_sequence_child_unknown_outcome_fails_closed_without_parent_success()
    {
        var events = new List<ExecutionEvent>();
        var result = await new WorkflowRunner(new UnknownOutcomeBroker())
            .ExecuteSequenceWorkflowCallProductionProofAsync(Guid.NewGuid(), "hello",
                evt => { events.Add(evt); return ValueTask.CompletedTask; }, SequenceWorkflowCallProofOutcome.Unknown);

        Assert.True(result.Failed);
        Assert.Contains(events, evt => evt.Kind == ExecutionEventKinds.SubworkflowOutcomeUnknown && evt.ScopeId == "call");
        Assert.Contains(events, evt => evt.Kind == ExecutionEventKinds.SequenceOutputFailed &&
            ReadEvidence(evt).OutputId == "call");
        Assert.DoesNotContain(events, evt => evt.Kind == ExecutionEventKinds.Completed && evt.ScopeId is null);
        Assert.DoesNotContain(events, evt => evt.Kind == ExecutionEventKinds.Terminated && evt.ScopeId is null);
    }

    [Fact]
    public async Task Proof_sequence_parent_cancellation_propagates_to_a_running_child_without_success()
    {
        var events = new List<ExecutionEvent>();
        var gate = new ReleaseGate("sequence-call-proof-first-input");
        using var cancellation = new CancellationTokenSource();
        var execution = new WorkflowRunner().ExecuteSequenceWorkflowCallProductionProofAsync(Guid.NewGuid(), "hello",
            evt => { events.Add(evt); return ValueTask.CompletedTask; }, gate: gate,
            cancellationToken: cancellation.Token);

        await gate.Blocked.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => execution);

        Assert.Contains(events, evt => evt.Kind == ExecutionEventKinds.SubworkflowCancelled && evt.ScopeId == "call");
        Assert.Contains(events, evt => evt.Kind == ExecutionEventKinds.SequenceOutputCancelled &&
            ReadEvidence(evt).OutputId == "call");
        Assert.Contains(events, evt => evt.Kind == ExecutionEventKinds.Cancelled && evt.ScopeId is null);
        Assert.DoesNotContain(events, evt => evt.Kind == ExecutionEventKinds.Completed && evt.ScopeId is null);
    }

    [Fact]
    public async Task Proof_child_local_cancellation_settles_the_call_cancelled_and_fails_the_parent_deterministically()
    {
        var events = new List<ExecutionEvent>();
        var gate = new ReleaseGate("sequence-call-proof-first-input");
        using var callScope = new CancellationTokenSource();
        var execution = new WorkflowRunner().ExecuteSequenceWorkflowCallProductionProofAsync(Guid.NewGuid(), "hello",
            evt => { events.Add(evt); return ValueTask.CompletedTask; }, gate: gate,
            callScopeCancellation: nodeId => nodeId == "call" ? callScope : null);

        await gate.Blocked.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(50);
        Assert.True(events.Any(evt => evt.Kind == ExecutionEventKinds.SubworkflowCompleted &&
            evt.ScopeId == "sibling"),
            string.Join(" | ", events.Select(evt => $"{evt.Sequence}:{evt.Kind}:{evt.NodeId}:{evt.ScopeId}")));

        callScope.Cancel();
        var result = await execution;

        // A child-local cancellation must not escape as an OCE: the parent run fails deterministically.
        Assert.True(result.Failed);
        Assert.Null(result.Output);
        Assert.Contains(events, evt => evt.Kind == ExecutionEventKinds.SubworkflowCancelled && evt.ScopeId == "call");
        Assert.DoesNotContain(events, evt => evt.Kind == ExecutionEventKinds.SubworkflowCompleted && evt.ScopeId == "call");
        Assert.Contains(events, evt => evt.Kind == ExecutionEventKinds.SequenceOutputCompleted &&
            ReadEvidence(evt).OutputId == "sibling");
        Assert.Contains(events, evt => evt.Kind == ExecutionEventKinds.SequenceOutputCancelled &&
            ReadEvidence(evt).OutputId == "call");
        Assert.Contains(events, evt => evt.Kind == ExecutionEventKinds.SequenceCancelled);
        // The sibling terminal lands before the root failure and the journal keeps exactly one terminal per call.
        var all = events.ToArray();
        Assert.True(Array.IndexOf(all, all.Single(evt => evt.Kind == ExecutionEventKinds.SubworkflowCompleted &&
            evt.ScopeId == "sibling")) < Array.IndexOf(all, all.Single(evt => evt.Kind == ExecutionEventKinds.Failed &&
            evt.ScopeId is null)));
        Assert.DoesNotContain(events, evt => evt.Kind == ExecutionEventKinds.Cancelled && evt.ScopeId is null);
        Assert.Equal(ExecutionEventKinds.Failed, events[^1].Kind);
        // Dual projection: the call itself is cancelled while the run-level status is failed.
        Assert.True(WorkflowCallEvidence.TryReconstructPlayback(events, out var playback));
        Assert.Equal("failed", playback!.Status);
        Assert.Equal("cancelled", playback.Calls.Single(call => call.CallNodeId == "call").State);
        Assert.Equal("completed", playback.Calls.Single(call => call.CallNodeId == "sibling").State);
    }

    [Fact]
    public async Task Proof_sequence_child_capability_denial_fails_the_call_without_uncertainty()
    {
        var events = new List<ExecutionEvent>();
        var result = await new WorkflowRunner(new DeniedOutcomeBroker())
            .ExecuteSequenceWorkflowCallProductionProofAsync(Guid.NewGuid(), "hello",
                evt => { events.Add(evt); return ValueTask.CompletedTask; }, SequenceWorkflowCallProofOutcome.CapabilityDenied);

        Assert.True(result.Failed);
        Assert.Contains(events, evt => evt.Kind == ExecutionEventKinds.SubworkflowFailed && evt.ScopeId == "call");
        Assert.Contains(events, evt => evt.Kind == ExecutionEventKinds.SequenceOutputFailed &&
            ReadEvidence(evt).OutputId == "call");
        Assert.DoesNotContain(events, evt => evt.Kind == ExecutionEventKinds.SubworkflowOutcomeUnknown);
        Assert.DoesNotContain(events, evt => evt.Kind == ExecutionEventKinds.Completed && evt.ScopeId is null);
    }

    [Fact]
    public async Task Production_runner_consumes_a_persisted_sequence_definition()
    {
        var events = new List<ExecutionEvent>();
        var revision = WorkflowRevision.Create(PersistedSequenceDefinition(), "public-sequence");

        var result = await new WorkflowRunner().ExecuteWithOutcomeAsync(Guid.NewGuid(), revision, "hello", null,
            executionEvent => { events.Add(executionEvent); return ValueTask.CompletedTask; });

        Assert.False(result.Failed);
        Assert.Equal("hello", result.Output);
        Assert.Equal(["inventory", "architecture", "quality"], events
            .Where(eventItem => eventItem.Kind == ExecutionEventKinds.SequenceOutputDispatched)
            .Select(ReadEvidence).Select(evidence => evidence.OutputId));
        Assert.All(events.Where(eventItem => eventItem.Kind == ExecutionEventKinds.SequenceOutputDispatched), eventItem =>
            Assert.Equal("sequence", ReadEvidence(eventItem).SequenceNodeId));
    }

    [Fact]
    public async Task Production_runner_records_ordered_sequence_lifecycle_through_normal_recorder()
    {
        var events = new List<ExecutionEvent>();
        var runId = Guid.NewGuid();
        var result = await new WorkflowRunner().ExecuteSequenceProductionProofAsync(runId, "hello",
            executionEvent => { events.Add(executionEvent); return ValueTask.CompletedTask; });

        Assert.False(result.Failed);
        Assert.False(string.IsNullOrEmpty(result.Output));
        Assert.Equal(1, events.Count(eventItem => eventItem.Kind == ExecutionEventKinds.SequenceStarted));
        Assert.Equal(1, events.Count(eventItem => eventItem.Kind == ExecutionEventKinds.SequenceCompleted));
        var dispatched = events.Where(eventItem => eventItem.Kind == ExecutionEventKinds.SequenceOutputDispatched).ToArray();
        Assert.Equal([0, 1, 2], dispatched.Select(ReadEvidence).Select(evidence => evidence.Ordinal));
        Assert.Equal(["first", "second", "third"], dispatched.Select(ReadEvidence).Select(evidence => evidence.OutputId));
        Assert.All(events.Where(eventItem => SequenceEvidence.IsLifecycleEvent(eventItem.Kind)), eventItem =>
        {
            Assert.True(SequenceEvidence.TryRead(eventItem, out var evidence));
            Assert.NotNull(evidence);
            Assert.Equal(runId, eventItem.RunId);
        });
        Assert.Equal(Enumerable.Range(1, events.Count).Select(index => (long)index), events.Select(eventItem => eventItem.Sequence));
        Assert.Contains(events, eventItem => eventItem.Kind == ExecutionEventKinds.Transfer &&
            eventItem.Data?.Contains("sequenceLineage", StringComparison.Ordinal) == true);
        Assert.True(SequenceEvidence.TryReconstructPlayback(events, out var playback));
        Assert.Equal("completed", playback!.Status);
        Assert.Equal(["first", "second", "third"], playback.Outputs.Select(output => output.OutputId));
    }

    [Theory]
    [InlineData(true, "true", "branch-true")]
    [InlineData(false, "false", "branch-false")]
    public async Task Sequence_branch_decision_evidence_keeps_output_activation_identity(
        bool condition, string expectedPort, string expectedConnection)
    {
        var runId = Guid.NewGuid();
        var events = new List<ExecutionEvent>();
        WorkflowExecutionResult result;
        try
        {
            result = await new WorkflowRunner().ExecuteSequenceBranchDecisionEvidenceProofAsync(runId,
                "route input", condition, evt => { events.Add(evt); return ValueTask.CompletedTask; });
        }
        catch (Exception exception)
        {
            throw new Xunit.Sdk.XunitException(exception.Message + Environment.NewLine +
                string.Join(Environment.NewLine, events.Select(evt => $"{evt.Sequence}:{evt.Kind}:{evt.NodeId}:{evt.ConnectionId}:{evt.Data}")));
        }

        Assert.False(result.Failed, string.Join(" | ", events.Select(evt => $"{evt.Kind}:{evt.NodeId}:{evt.Data}")));
        Assert.Equal(condition, result.HasOutput);
        Assert.Equal(condition ? string.Empty : null, result.Output);
        var decision = Assert.Single(events, evt => evt.Kind == ExecutionEventKinds.BranchSelected && evt.NodeId == "branch");
        using var evidence = JsonDocument.Parse(decision.Data!);
        Assert.Equal(expectedPort, evidence.RootElement.GetProperty("port").GetString());
        var lineage = evidence.RootElement.GetProperty("sequenceLineage");
        Assert.Equal("sequence", lineage.GetProperty("SequenceNodeId").GetString());
        Assert.Equal("decision", lineage.GetProperty("OutputId").GetString());
        Assert.Equal(0, lineage.GetProperty("Ordinal").GetInt32());
        Assert.NotEqual(Guid.Empty, lineage.GetProperty("OutputActivationId").GetGuid());

        var outcomeDecision = Assert.Single(events, evt => evt.Kind == ExecutionEventKinds.BranchSelected && evt.NodeId == "outcome");
        using var outcomeEvidence = JsonDocument.Parse(outcomeDecision.Data!);
        Assert.Equal(expectedPort, outcomeEvidence.RootElement.GetProperty("port").GetString());
        Assert.Equal(lineage.GetProperty("OutputActivationId").GetGuid(), outcomeEvidence.RootElement
            .GetProperty("sequenceLineage").GetProperty("OutputActivationId").GetGuid());

        var selectedTransfer = Assert.Single(events, evt => evt.Kind == ExecutionEventKinds.ExecutionActivated &&
            evt.ConnectionId == $"execution:{expectedConnection}");
        using var transferEvidence = JsonDocument.Parse(selectedTransfer.Data!);
        Assert.Equal(lineage.GetProperty("OutputActivationId").GetGuid(), transferEvidence.RootElement
            .GetProperty("sequenceLineage").GetProperty("OutputActivationId").GetGuid());
        Assert.DoesNotContain(events, evt => evt.Kind == ExecutionEventKinds.ExecutionActivated &&
            evt.ConnectionId == $"execution:{(expectedConnection == "branch-true" ? "branch-false" : "branch-true")}");
        Assert.Single(events, evt => evt.Kind == ExecutionEventKinds.NodeStarted && evt.NodeId == "merge");
        Assert.Equal(!condition, events.Any(evt => evt.Kind == ExecutionEventKinds.NodeStarted && evt.NodeId == "alternate"));
        var selectedMergeArrival = Assert.Single(events, evt => evt.Kind == ExecutionEventKinds.ExecutionActivated &&
            evt.ConnectionId == $"execution:{(condition ? "branch-true" : "alternate-merge")}");
        using var mergeEvidence = JsonDocument.Parse(selectedMergeArrival.Data!);
        Assert.Equal(lineage.GetProperty("OutputActivationId").GetGuid(), mergeEvidence.RootElement
            .GetProperty("sequenceLineage").GetProperty("OutputActivationId").GetGuid());
        Assert.Equal(condition ? 2 : 1, events.Count(evt => evt.Kind == ExecutionEventKinds.SequenceOutputCompleted));
        Assert.Single(events, evt => evt.Kind == ExecutionEventKinds.SequenceOutputCompleted &&
            SequenceEvidence.TryRead(evt, out var output) && output?.OutputId == "decision");
        Assert.Equal(!condition, events.Any(evt => evt.Kind == ExecutionEventKinds.SequenceOutputSkipped &&
            SequenceEvidence.TryRead(evt, out var output) && output?.OutputId == "suffix"));
        Assert.Equal(!condition, events.Any(evt => evt.Kind == ExecutionEventKinds.SequenceBreakCompleted));
        Assert.Equal(condition ? 2 : 1, events.Count(evt => evt.Kind == ExecutionEventKinds.NodeStarted &&
            evt.NodeId == "sequence-collector"));
        Assert.True(SequenceEvidence.TryReconstructPlayback(events, out var playback));
        Assert.Equal(condition ? "completed" : "break-completed", playback!.Status);
        Assert.Equal(["decision", "suffix"], playback.Outputs.Select(output => output.OutputId));
        var expectedStates = condition ? new[] { "completed", "completed" } : ["completed", "skipped"];
        Assert.Equal(expectedStates, playback.Outputs.Select(output => output.State));
    }

    [Theory]
    [InlineData("Failed", ExecutionEventKinds.SequenceFailed, ExecutionEventKinds.SequenceOutputFailed)]
    [InlineData("Cancelled", ExecutionEventKinds.SequenceCancelled, ExecutionEventKinds.SequenceOutputCancelled)]
    public async Task Production_runner_preserves_distinct_sequence_failure_outcomes(
        string terminalStateName, string sequenceTerminalKind, string outputTerminalKind)
    {
        var events = new List<ExecutionEvent>();
        var terminalState = Enum.Parse<SequenceTerminalState>(terminalStateName);
        var result = await new WorkflowRunner().ExecuteSequenceProductionProofAsync(Guid.NewGuid(), "hello",
            executionEvent => { events.Add(executionEvent); return ValueTask.CompletedTask; }, "second", terminalState);

        Assert.True(result.Failed);
        Assert.Contains(events, eventItem => eventItem.Kind == outputTerminalKind);
        Assert.Contains(events, eventItem => eventItem.Kind == sequenceTerminalKind);
        Assert.DoesNotContain(events, eventItem => eventItem.Kind == ExecutionEventKinds.SequenceCompleted);
    }

    [Fact]
    public async Task Production_runner_cancellation_at_dispatch_boundary_does_not_admit_next_output()
    {
        var events = new List<ExecutionEvent>();
        var gate = new BoundaryGate("sequence:second");
        using var cancellation = new CancellationTokenSource();
        var execution = new WorkflowRunner().ExecuteSequenceProductionProofAsync(Guid.NewGuid(), "hello",
            executionEvent => { events.Add(executionEvent); return ValueTask.CompletedTask; }, gate: gate,
            cancellationToken: cancellation.Token);

        await gate.Blocked.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, events.Count(eventItem => eventItem.Kind == ExecutionEventKinds.SequenceOutputDispatched));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => execution);

        Assert.DoesNotContain(events, eventItem => eventItem.Kind == ExecutionEventKinds.SequenceOutputDispatched &&
            ReadEvidence(eventItem).OutputId is "second" or "third");
        Assert.Contains(events, eventItem => eventItem.Kind == ExecutionEventKinds.SequenceCancelled);
    }

    [Fact]
    public async Task Production_runner_pause_at_dispatch_boundary_releases_once_without_redispatch()
    {
        var events = new List<ExecutionEvent>();
        var gate = new ReleaseGate("sequence:second");
        var execution = new WorkflowRunner().ExecuteSequenceProductionProofAsync(Guid.NewGuid(), "hello",
            executionEvent => { events.Add(executionEvent); return ValueTask.CompletedTask; }, gate: gate);

        await gate.Blocked.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, events.Count(eventItem => eventItem.Kind == ExecutionEventKinds.SequenceOutputDispatched));
        gate.Release.TrySetResult();
        var result = await execution.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(result.Failed);
        Assert.Equal(3, events.Count(eventItem => eventItem.Kind == ExecutionEventKinds.SequenceOutputDispatched));
        Assert.DoesNotContain(events, eventItem => eventItem.Kind == ExecutionEventKinds.SequenceCancelled);
    }

    [Fact]
    public async Task Production_sequence_source_holds_first_descendant_until_later_dispatch_boundary_releases()
    {
        var events = new List<ExecutionEvent>();
        var gate = new ReleaseGate("sequence:second");
        var execution = new WorkflowRunner().ExecuteSequenceProductionProofAsync(Guid.NewGuid(), "hello",
            evt => { events.Add(evt); return ValueTask.CompletedTask; }, gate: gate);

        await gate.Blocked.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Contains(events, evt => evt.Kind == ExecutionEventKinds.SequenceOutputDispatched &&
            ReadEvidence(evt).OutputId == "first");
        Assert.DoesNotContain(events, evt => evt.Kind == ExecutionEventKinds.NodeStarted && evt.NodeId == "first");

        gate.Release.TrySetResult();
        var result = await execution.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(result.Failed);
        Assert.Contains(events, evt => evt.Kind == ExecutionEventKinds.NodeStarted && evt.NodeId == "first");
    }

    [Fact]
    public async Task Unified_sequence_evaluates_pure_integer_addition_for_each_typed_mcp_argument()
    {
        var source = SequencePathDefinition();
        var nodes = source.Nodes
            .Add("left", new(NodeTypes.IntegerConstant, "1.0.0") { IntegerConstant = new(40) })
            .Add("right", new(NodeTypes.IntegerConstant, "1.0.0") { IntegerConstant = new(2) })
            .Add("sum", new(NodeTypes.IntegerAdd, "1.0.0"));
        foreach (var id in new[] { "inventory-tool", "architecture-tool" })
            nodes = nodes.SetItem(id, nodes[id] with
            { McpArgumentsJson = null, McpInputPorts = [new("arg.count", "count", WorkflowDataTypes.Integer, true)] });
        var definition = source with
        {
            SchemaVersion = WorkflowSchemaVersions.UnifiedWorkflowGraph,
            Nodes = nodes,
            Connections = source.Connections
                .Add("left-sum", new(new("left", "out"), new("sum", "left"), "data"))
                .Add("right-sum", new(new("right", "out"), new("sum", "right"), "data"))
                .Add("inventory-count", new(new("sum", "out"), new("inventory-tool", "arg.count"), "data"))
                .Add("architecture-count", new(new("sum", "out"), new("architecture-tool", "arg.count"), "data"))
        };
        var broker = new RecordingBroker();
        var events = new List<ExecutionEvent>();
        var result = await new WorkflowRunner(broker).ExecuteWithOutcomeAsync(Guid.NewGuid(),
            WorkflowRevision.Create(definition, "test"), "caller input", null,
            evt => { events.Add(evt); return ValueTask.CompletedTask; });

        Assert.False(result.Failed);
        Assert.Equal("tool-result", result.Output);
        Assert.Equal(2, broker.Arguments.Count);
        Assert.All(broker.Arguments, arguments => Assert.Equal(42, arguments.GetProperty("count").GetInt64()));
        var sumEvidence = events.Where(evt => evt.Kind == ExecutionEventKinds.DataEvaluated && evt.NodeId == "sum").ToArray();
        Assert.Equal(2, sumEvidence.Length);
        foreach (var evt in sumEvidence)
        {
            using var evidence = JsonDocument.Parse(evt.Data!);
            Assert.Equal("redacted", evidence.RootElement.GetProperty("payloadState").GetString());
            var expectedOutputId = evidence.RootElement.GetProperty("activatedByNodeId").GetString() switch
            {
                "inventory-tool" => "inventory",
                "architecture-tool" => "architecture",
                _ => throw new Xunit.Sdk.XunitException("Unexpected direct MCP consumer in data evidence.")
            };
            Assert.Equal(expectedOutputId, evidence.RootElement.GetProperty("sequenceLineage")
                .GetProperty("OutputId").GetString());
        }
        Assert.DoesNotContain(events, evt => evt.Kind == ExecutionEventKinds.NodeStarted && evt.NodeId is "sum" or "left" or "right");
        Assert.Equal(2, events.Count(evt => evt.Kind == ExecutionEventKinds.SequenceOutputCompleted));
        Assert.True(SequenceEvidence.TryReconstructPlayback(events, out var playback));
        Assert.Equal("completed", playback!.Status);
        Assert.Equal(["inventory", "architecture"], playback.Outputs.Select(output => output.OutputId));
        Assert.All(playback.Outputs, output => Assert.Equal("completed", output.State));
    }

    [Theory]
    [InlineData("string", """["alpha","beta"]""", """["alpha","beta"]""")]
    [InlineData("boolean", "[true,false]", "[true,false]")]
    [InlineData("integer", "[-1,42]", "[-1,42]")]
    [InlineData("number", "[1.5,-2]", "[1.5,-2]")]
    [InlineData("json", """[{"z":1.00,"a":[null,true]},-0]""", """[{"z":1.00,"a":[null,true]},-0]""")]
    public async Task Unified_sequence_passes_typed_array_values_to_direct_mcp_argument_pins(
        string elementType, string valuesJson, string expectedJson)
    {
        var source = SequencePathDefinition();
        var nodes = source.Nodes
            .Add("values", new(NodeTypes.ArrayLiteral, "1.0.0")
            { ArrayLiteral = new(elementType, valuesJson) });
        foreach (var id in new[] { "inventory-tool", "architecture-tool" })
            nodes = nodes.SetItem(id, nodes[id] with
            {
                McpArgumentsJson = null,
                McpInputPorts = [new("arg.values", "values", WorkflowDataTypes.ArrayOf(elementType), true)]
            });

        var definition = source with
        {
            SchemaVersion = WorkflowSchemaVersions.UnifiedWorkflowGraph,
            Nodes = nodes,
            Connections = source.Connections
                .Add("inventory-values", new(new("values", "out"), new("inventory-tool", "arg.values"), "data"))
                .Add("architecture-values", new(new("values", "out"), new("architecture-tool", "arg.values"), "data"))
        };
        WorkflowValidator.ThrowIfInvalid(definition);
        var package = WorkflowPackageJson.Create(definition, WorkflowLayout.Create(definition));
        var reopened = WorkflowPackageJson.Deserialize(WorkflowPackageJson.Serialize(definition, package.Layout));
        var broker = new RecordingBroker();

        var result = await new WorkflowRunner(broker).ExecuteWithOutcomeAsync(Guid.NewGuid(),
            WorkflowRevision.Create(reopened.Definition, "test"), "caller input", null,
            _ => ValueTask.CompletedTask);

        Assert.False(result.Failed);
        Assert.Equal(2, broker.Arguments.Count);
        Assert.All(broker.Arguments, arguments =>
        {
            Assert.Equal(expectedJson, arguments.GetProperty("values").GetRawText());
        });
    }

    [Fact]
    public async Task Unified_sequence_agent_result_flows_through_a_following_transform()
    {
        var source = SequenceAgentPathDefinition();
        var definition = source with
        {
            SchemaVersion = WorkflowSchemaVersions.UnifiedWorkflowGraph,
            Nodes = source.Nodes.Add("uppercase", new(NodeTypes.SequenceUppercase, "1.0.0")),
            Connections = source.Connections.Add("agent-transform-value", new(new("inventory-agent", "out"), new("uppercase", "in"), "data")),
            ExecutionConnections = source.ExecutionConnections!
                .SetItem("inventory-output", new(new("inventory-agent", "out"), new("uppercase", "execute"), "execution"))
                .Add("transform-output", new(new("uppercase", "then"), new("output", "execute"), "execution"))
        };
        var events = new List<ExecutionEvent>();
        var result = await new WorkflowRunner(new RecordingBroker(), new SequenceProofAgentExecutor())
            .ExecuteWithOutcomeAsync(Guid.NewGuid(), WorkflowRevision.Create(definition, "test"), "hello", null,
                evt => { events.Add(evt); return ValueTask.CompletedTask; });

        Assert.False(result.Failed);
        Assert.Equal("HELLO", result.Output);
        Assert.Contains(events, evt => evt.Kind == ExecutionEventKinds.NodeCompleted && evt.NodeId == "uppercase" && evt.Data == "HELLO");
        Assert.Equal(2, events.Count(evt => evt.Kind == ExecutionEventKinds.SequenceOutputCompleted));
        Assert.True(SequenceEvidence.TryReconstructPlayback(events, out var playback));
        Assert.Equal("completed", playback!.Status);
        Assert.All(playback.Outputs, output => Assert.Equal("completed", output.State));
    }

    [Fact]
    public async Task Unified_production_sequence_dispatches_are_admitted_and_barrier_waits_for_held_descendant()
    {
        var definition = SequenceAgentPathDefinition() with
        {
            SchemaVersion = WorkflowSchemaVersions.UnifiedWorkflowGraph
        };
        var revision = WorkflowRevision.Create(definition, "sequence-dispatch-admission");
        var releaseAgent = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var agent = new SequenceProofAgentExecutor(releaseWhenReady: releaseAgent);
        var timeline = new ConcurrentQueue<(string Kind, string NodeId)>();
        var events = new ConcurrentQueue<ExecutionEvent>();
        var otherBranchStepCompleted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var outputAdmitted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var execution = new WorkflowRunner(new RecordingBroker(), agent)
            .ExecuteWithProductionDispatchAdmissionProofAsync(Guid.NewGuid(), revision, "sequence input",
                ticket =>
                {
                    timeline.Enqueue(("admitted", ticket.TargetNodeId));
                    if (ticket.TargetNodeId == "output") outputAdmitted.TrySetResult(true);
                }, evt =>
                {
                    events.Enqueue(evt);
                    if (evt.Kind == ExecutionEventKinds.NodeStarted && evt.NodeId is { } nodeId)
                        timeline.Enqueue(("started", nodeId));
                    if (evt.Kind == ExecutionEventKinds.NodeCompleted && evt.NodeId == "architecture-tool")
                        otherBranchStepCompleted.TrySetResult(true);
                    return ValueTask.CompletedTask;
                });

        try
        {
            await Task.WhenAll(agent.Started.Task, otherBranchStepCompleted.Task, outputAdmitted.Task)
                .WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(execution.IsCompleted);

            var observed = timeline.ToArray();
            var outstanding = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var (kind, nodeId) in observed)
            {
                outstanding.TryGetValue(nodeId, out var available);
                if (kind == "admitted")
                {
                    outstanding[nodeId] = available + 1;
                    continue;
                }

                Assert.Equal("started", kind);
                Assert.True(available > 0, $"Sequence executor '{nodeId}' started before admission.");
                outstanding[nodeId] = available - 1;
            }

            Assert.Contains(observed, item => item == ("admitted", "inventory-agent"));
            Assert.Contains(observed, item => item == ("admitted", "architecture-tool"));
            Assert.Contains(observed, item => item == ("admitted", "output"));
            Assert.DoesNotContain(observed, item => item == ("started", "output"));
            Assert.DoesNotContain(events, evt => evt.Kind == ExecutionEventKinds.SequenceOutputCompleted);
            Assert.DoesNotContain(events, evt => evt.NodeId is null && evt.Kind == ExecutionEventKinds.Completed);
        }
        finally
        {
            releaseAgent.TrySetResult(true);
        }

        var result = await execution.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(result.Failed);
        Assert.Equal(2, events.Count(evt => evt.Kind == ExecutionEventKinds.SequenceOutputCompleted));
        Assert.Single(events, evt => evt.NodeId is null && evt.Kind == ExecutionEventKinds.Completed);
    }

    [Fact]
    public async Task Unified_production_sequence_rejected_admission_prevents_first_target_start()
    {
        var revision = WorkflowRevision.Create(SequenceAgentPathDefinition() with
        {
            SchemaVersion = WorkflowSchemaVersions.UnifiedWorkflowGraph
        }, "sequence-dispatch-admission-rejection");
        var events = new ConcurrentQueue<ExecutionEvent>();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new WorkflowRunner(new RecordingBroker(), new SequenceProofAgentExecutor())
                .ExecuteWithProductionDispatchAdmissionProofAsync(Guid.NewGuid(), revision, "sequence input",
                    ticket =>
                    {
                        if (ticket.TargetNodeId == "inventory-agent")
                            throw new InvalidOperationException("Sequence target admission rejected.");
                    }, evt =>
                    {
                        events.Enqueue(evt);
                        return ValueTask.CompletedTask;
                    }));

        Assert.Contains("Sequence target admission rejected.", exception.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(events, evt => evt.Kind == ExecutionEventKinds.NodeStarted &&
            evt.NodeId == "inventory-agent");
        Assert.DoesNotContain(events, evt => evt.NodeId is null && evt.Kind == ExecutionEventKinds.Completed);
    }

    [Fact]
    public async Task Unified_production_sequence_agent_failure_preserves_admission_before_start_and_failure_order()
    {
        var revision = WorkflowRevision.Create(SequenceAgentPathDefinition() with
        {
            SchemaVersion = WorkflowSchemaVersions.UnifiedWorkflowGraph
        }, "sequence-dispatch-admission-failure");
        var timeline = new ConcurrentQueue<(string Kind, string NodeId)>();
        var events = new ConcurrentQueue<ExecutionEvent>();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new WorkflowRunner(new RecordingBroker(), new SequenceProofAgentExecutor(fail: true))
                .ExecuteWithProductionDispatchAdmissionProofAsync(Guid.NewGuid(), revision, "sequence input",
                    ticket => timeline.Enqueue(("admitted", ticket.TargetNodeId)), evt =>
                    {
                        events.Enqueue(evt);
                        if (evt.Kind == ExecutionEventKinds.NodeStarted && evt.NodeId is { } nodeId)
                            timeline.Enqueue(("started", nodeId));
                        return ValueTask.CompletedTask;
                    }));

        AssertEveryStartedExecutorWasAdmitted(timeline);
        Assert.Contains(events, evt => evt.Kind == ExecutionEventKinds.NodeFailed && evt.NodeId == "inventory-agent");
        Assert.Contains(events, evt => evt.Kind == ExecutionEventKinds.SequenceOutputFailed &&
            ReadEvidence(evt).OutputId == "inventory");
        Assert.Contains(events, evt => evt.Kind == ExecutionEventKinds.SequenceFailed);
        Assert.DoesNotContain(events, evt => evt.Kind == ExecutionEventKinds.SequenceCompleted);
        Assert.DoesNotContain(events, evt => evt.NodeId is null && evt.Kind == ExecutionEventKinds.Completed);
        Assert.Contains(events, evt => evt.NodeId is null && evt.Kind == ExecutionEventKinds.Failed);
        var recorded = events.ToArray();
        Assert.True(Array.FindIndex(recorded, evt => evt.Kind == ExecutionEventKinds.SequenceFailed) <
            Array.FindIndex(recorded, evt => evt.NodeId is null && evt.Kind == ExecutionEventKinds.Failed));
    }

    [Fact]
    public async Task Unified_production_sequence_cancellation_preserves_admission_before_start_and_cancellation_order()
    {
        var revision = WorkflowRevision.Create(SequenceAgentPathDefinition() with
        {
            SchemaVersion = WorkflowSchemaVersions.UnifiedWorkflowGraph
        }, "sequence-dispatch-admission-cancel");
        var timeline = new ConcurrentQueue<(string Kind, string NodeId)>();
        var events = new ConcurrentQueue<ExecutionEvent>();
        var agent = new SequenceProofAgentExecutor(blockUntilCancellation: true);
        using var cancellation = new CancellationTokenSource();
        var execution = new WorkflowRunner(new RecordingBroker(), agent)
            .ExecuteWithProductionDispatchAdmissionProofAsync(Guid.NewGuid(), revision, "sequence input",
                ticket => timeline.Enqueue(("admitted", ticket.TargetNodeId)), evt =>
                {
                    events.Enqueue(evt);
                    if (evt.Kind == ExecutionEventKinds.NodeStarted && evt.NodeId is { } nodeId)
                        timeline.Enqueue(("started", nodeId));
                    return ValueTask.CompletedTask;
                }, cancellation.Token);

        await agent.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => execution);

        AssertEveryStartedExecutorWasAdmitted(timeline);
        Assert.Contains(events, evt => evt.Kind == ExecutionEventKinds.NodeCancelled && evt.NodeId == "inventory-agent");
        Assert.Contains(events, evt => evt.Kind == ExecutionEventKinds.SequenceOutputCancelled &&
            ReadEvidence(evt).OutputId == "inventory");
        Assert.Contains(events, evt => evt.Kind == ExecutionEventKinds.SequenceCancelled);
        Assert.DoesNotContain(events, evt => evt.Kind == ExecutionEventKinds.SequenceCompleted);
        Assert.DoesNotContain(events, evt => evt.NodeId is null && evt.Kind == ExecutionEventKinds.Completed);
        Assert.Contains(events, evt => evt.NodeId is null && evt.Kind == ExecutionEventKinds.Cancelled);
        var recorded = events.ToArray();
        Assert.True(Array.FindIndex(recorded, evt => evt.Kind == ExecutionEventKinds.SequenceCancelled) <
            Array.FindIndex(recorded, evt => evt.NodeId is null && evt.Kind == ExecutionEventKinds.Cancelled));
    }

    [Theory]
    [InlineData(false, "completed")]
    [InlineData(true, "completed")]
    [InlineData(false, "failed")]
    [InlineData(true, "failed")]
    [InlineData(false, "cancelled")]
    [InlineData(true, "cancelled")]
    public async Task Unified_production_sequence_admitted_run_reopens_from_store_for_passive_playback(
        bool sqlite, string expectedStatus)
    {
        var revision = WorkflowRevision.Create(SequenceAgentPathDefinition() with
        {
            SchemaVersion = WorkflowSchemaVersions.UnifiedWorkflowGraph
        }, $"sequence-dispatch-admission-store-{expectedStatus}");
        using var runStore = new ReopenableRunStore(revision, "sequence input", sqlite);
        var agent = expectedStatus switch
        {
            "failed" => new SequenceProofAgentExecutor(fail: true),
            "cancelled" => new SequenceProofAgentExecutor(blockUntilCancellation: true),
            _ => new SequenceProofAgentExecutor()
        };
        var timeline = new ConcurrentQueue<(string Kind, string NodeId)>();
        var recorded = new List<ExecutionEvent>();
        var recordLock = new object();
        using var cancellation = new CancellationTokenSource();

        ValueTask Record(ExecutionEvent evt)
        {
            lock (recordLock)
            {
                runStore.Store.Append(runStore.RunId, evt.Sequence - 1, [evt]);
                recorded.Add(evt);
            }
            if (evt.Kind == ExecutionEventKinds.NodeStarted && evt.NodeId is { } nodeId)
                timeline.Enqueue(("started", nodeId));
            return ValueTask.CompletedTask;
        }

        var execution = new WorkflowRunner(new RecordingBroker(), agent)
            .ExecuteWithProductionDispatchAdmissionProofAsync(runStore.RunId, revision, "sequence input",
                ticket => timeline.Enqueue(("admitted", ticket.TargetNodeId)), Record, cancellation.Token);
        switch (expectedStatus)
        {
            case "completed":
                Assert.False((await execution.WaitAsync(TimeSpan.FromSeconds(5))).Failed);
                break;
            case "failed":
                await Assert.ThrowsAsync<InvalidOperationException>(() => execution.WaitAsync(TimeSpan.FromSeconds(5)));
                break;
            default:
                await agent.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
                cancellation.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => execution.WaitAsync(TimeSpan.FromSeconds(5)));
                break;
        }

        AssertEveryStartedExecutorWasAdmitted(timeline);
        var reopened = runStore.ReopenStore().Get(runStore.RunId)!;
        Assert.Equal(expectedStatus, reopened.Status);
        Assert.Equal(recorded, reopened.Events);
        Assert.Equal(Enumerable.Range(1, reopened.Events.Length).Select(value => (long)value),
            reopened.Events.Select(evt => evt.Sequence));
        var rootTerminal = Assert.Single(reopened.Events, evt => evt.NodeId is null && evt.Kind is
            ExecutionEventKinds.Completed or ExecutionEventKinds.Failed or ExecutionEventKinds.Cancelled or
            ExecutionEventKinds.Terminated or ExecutionEventKinds.OutcomeUnknown);
        Assert.Equal($"run.{expectedStatus}", rootTerminal.Kind);
        var sequenceTerminal = Assert.Single(reopened.Events, evt => evt.Kind is
            ExecutionEventKinds.SequenceCompleted or ExecutionEventKinds.SequenceFailed or
            ExecutionEventKinds.SequenceCancelled);
        Assert.True(sequenceTerminal.Sequence < rootTerminal.Sequence);

        Assert.True(SequenceAgentEvidence.TryReconstructPlayback(reopened.Events, out var playback));
        Assert.Equal(expectedStatus, playback!.Sequence.Status);
        Assert.Equal(["inventory", "architecture"], playback.Sequence.Outputs.Select(output => output.OutputId));
        var inventory = Assert.Single(playback.Sequence.Outputs, output => output.OutputId == "inventory");
        Assert.Equal(expectedStatus, inventory.State);
        var inventoryAgent = Assert.Single(playback.Agents);
        Assert.Equal(("inventory-agent", "inventory", inventory.ActivationId),
            (inventoryAgent.NodeId, inventoryAgent.OutputId, (Guid?)inventoryAgent.ActivationId));
        Assert.Equal(expectedStatus == "completed", inventoryAgent.State == "completed");
        Assert.Equal(expectedStatus == "completed", Assert.Single(playback.ModelCalls).State == "completed");
    }

    [Theory]
    [InlineData(false, ExecutionEventKinds.SequenceOutputDispatched, false)]
    [InlineData(true, ExecutionEventKinds.SequenceOutputDispatched, false)]
    [InlineData(false, ExecutionEventKinds.SequenceOutputDispatched, true)]
    [InlineData(true, ExecutionEventKinds.SequenceOutputDispatched, true)]
    [InlineData(false, ExecutionEventKinds.SequenceOutputCompleted, false)]
    [InlineData(true, ExecutionEventKinds.SequenceOutputCompleted, false)]
    [InlineData(false, ExecutionEventKinds.SequenceOutputCompleted, true)]
    [InlineData(true, ExecutionEventKinds.SequenceOutputCompleted, true)]
    [InlineData(false, ExecutionEventKinds.SequenceCompleted, false)]
    [InlineData(true, ExecutionEventKinds.SequenceCompleted, false)]
    [InlineData(false, ExecutionEventKinds.SequenceCompleted, true)]
    [InlineData(true, ExecutionEventKinds.SequenceCompleted, true)]
    public async Task Unified_production_sequence_admitted_store_fault_preserves_reopened_nonterminal_prefix(
        bool sqlite, string faultedKind, bool commitBeforeFault)
    {
        var revision = WorkflowRevision.Create(SequenceAgentPathDefinition() with
        {
            SchemaVersion = WorkflowSchemaVersions.UnifiedWorkflowGraph
        }, "sequence-dispatch-admission-store-fault");
        using var runStore = new ReopenableRunStore(revision, "sequence input", sqlite);
        var store = new FaultOnceRunStore(runStore.Store, faultedKind, commitBeforeFault);
        var timeline = new ConcurrentQueue<(string Kind, string NodeId)>();
        var attempted = new List<ExecutionEvent>();
        var recordLock = new object();

        ValueTask Record(ExecutionEvent evt)
        {
            lock (recordLock)
            {
                attempted.Add(evt);
                store.Append(runStore.RunId, evt.Sequence - 1, [evt]);
            }
            if (evt.Kind == ExecutionEventKinds.NodeStarted && evt.NodeId is { } nodeId)
                timeline.Enqueue(("started", nodeId));
            return ValueTask.CompletedTask;
        }

        await Assert.ThrowsAnyAsync<Exception>(() => new WorkflowRunner(new RecordingBroker(),
                new SequenceProofAgentExecutor())
            .ExecuteWithProductionDispatchAdmissionProofAsync(runStore.RunId, revision, "sequence input",
                ticket => timeline.Enqueue(("admitted", ticket.TargetNodeId)), Record)
            .WaitAsync(TimeSpan.FromSeconds(5)));

        AssertEveryStartedExecutorWasAdmitted(timeline);
        var faulted = attempted[^1];
        Assert.Equal(faultedKind, faulted.Kind);
        Assert.Single(attempted, evt => evt.Kind == faultedKind);
        Assert.DoesNotContain(attempted, evt => evt.NodeId is null && evt.Kind is
            ExecutionEventKinds.Completed or ExecutionEventKinds.Failed or ExecutionEventKinds.Cancelled or
            ExecutionEventKinds.Terminated or ExecutionEventKinds.OutcomeUnknown);

        var reopened = runStore.ReopenStore().Get(runStore.RunId)!;
        var expectedPrefix = commitBeforeFault ? attempted : attempted[..^1];
        Assert.Equal(expectedPrefix, reopened.Events);
        Assert.Equal(Enumerable.Range(1, reopened.Events.Length).Select(value => (long)value),
            reopened.Events.Select(evt => evt.Sequence));
        Assert.Equal("running", reopened.Status);
        Assert.True(SequenceEvidence.TryReconstructPlayback(reopened.Events, out var playback));
        var expectedSequenceStatus = commitBeforeFault && faultedKind == ExecutionEventKinds.SequenceCompleted
            ? "completed" : "running";
        Assert.Equal(expectedSequenceStatus, playback!.Status);
        Assert.DoesNotContain(playback.Outputs, output => output.State is "failed" or "cancelled");
    }

    [Theory]
    [InlineData(false, "failure")]
    [InlineData(true, "failure")]
    [InlineData(false, "cancellation")]
    [InlineData(true, "cancellation")]
    public async Task Unified_production_sequence_sibling_race_settles_both_branches_before_root_terminal(
        bool sqlite, string scenario)
    {
        var failing = scenario == "failure";
        var expectedStatus = failing ? "failed" : "cancelled";
        var revision = WorkflowRevision.Create(SequenceAgentPathDefinition() with
        {
            SchemaVersion = WorkflowSchemaVersions.UnifiedWorkflowGraph
        }, $"sequence-dispatch-admission-sibling-{scenario}");
        using var runStore = new ReopenableRunStore(revision, "sequence input", sqlite);
        var toolStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var toolCancellationObserved = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var settleTool = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var agent = new SiblingRaceAgentExecutor(toolStarted.Task, failing);
        var broker = new HeldToolBroker(toolStarted, toolCancellationObserved, settleTool.Task);
        var timeline = new ConcurrentQueue<(string Kind, string NodeId)>();
        var recorded = new List<ExecutionEvent>();
        var recordLock = new object();
        var cancelRequestedAt = -1;
        using var cancellation = new CancellationTokenSource();

        ValueTask Record(ExecutionEvent evt)
        {
            lock (recordLock)
            {
                runStore.Store.Append(runStore.RunId, evt.Sequence - 1, [evt]);
                recorded.Add(evt);
            }
            if (evt.Kind == ExecutionEventKinds.NodeStarted && evt.NodeId is { } nodeId)
                timeline.Enqueue(("started", nodeId));
            return ValueTask.CompletedTask;
        }

        ExecutionEvent[] Snapshot()
        {
            lock (recordLock) return [.. recorded];
        }

        var execution = new WorkflowRunner(broker, agent)
            .ExecuteWithProductionDispatchAdmissionProofAsync(runStore.RunId, revision, "sequence input",
                ticket => timeline.Enqueue(("admitted", ticket.TargetNodeId)), Record, cancellation.Token);

        await toolStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await agent.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (failing)
        {
            agent.ReleaseFailure();
        }
        else
        {
            lock (recordLock) cancelRequestedAt = recorded.Count;
            cancellation.Cancel();
        }
        await toolCancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // The sibling observed cancellation but has not settled: the run must still be open.
        await Task.Delay(100);
        Assert.False(execution.IsCompleted);
        Assert.DoesNotContain(Snapshot(), evt => evt.NodeId is null && evt.Kind is ExecutionEventKinds.Completed or
            ExecutionEventKinds.Failed or ExecutionEventKinds.Cancelled);
        // Observed runtime behavior: the Sequence settles its outputs and records its own terminal while the
        // held descendant executor is still unsettled; only the root terminal waits for the descendant.
        var beforeRelease = Snapshot();
        Assert.DoesNotContain(beforeRelease, evt => evt.Kind == ExecutionEventKinds.SequenceCompleted);
        Assert.Single(beforeRelease, evt => evt.Kind == ExecutionEventKinds.SequenceOutputCancelled &&
            ReadEvidence(evt).OutputId == "architecture");
        Assert.DoesNotContain(beforeRelease, evt => evt.NodeId == "architecture-tool" && evt.Kind is
            ExecutionEventKinds.NodeCancelled or ExecutionEventKinds.NodeFailed or ExecutionEventKinds.NodeCompleted);

        settleTool.TrySetResult(true);
        if (failing)
            await Assert.ThrowsAsync<InvalidOperationException>(() => execution.WaitAsync(TimeSpan.FromSeconds(5)));
        else
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => execution.WaitAsync(TimeSpan.FromSeconds(5)));

        AssertEveryStartedExecutorWasAdmitted(timeline);
        var reopened = runStore.ReopenStore().Get(runStore.RunId)!;
        Assert.Equal(expectedStatus, reopened.Status);
        Assert.Equal(recorded, reopened.Events);
        Assert.Equal(Enumerable.Range(1, reopened.Events.Length).Select(value => (long)value),
            reopened.Events.Select(evt => evt.Sequence));
        if (!failing)
            Assert.DoesNotContain(reopened.Events.Skip(cancelRequestedAt), evt => evt.Kind == ExecutionEventKinds.NodeStarted);
        Assert.DoesNotContain(reopened.Events, evt => evt.Kind == ExecutionEventKinds.SequenceCompleted);
        Assert.DoesNotContain(reopened.Events, evt => evt.NodeId is null && evt.Kind == ExecutionEventKinds.Completed);
        var rootTerminal = Assert.Single(reopened.Events, evt => evt.NodeId is null && evt.Kind is
            ExecutionEventKinds.Completed or ExecutionEventKinds.Failed or ExecutionEventKinds.Cancelled or
            ExecutionEventKinds.Terminated or ExecutionEventKinds.OutcomeUnknown);
        Assert.Equal($"run.{expectedStatus}", rootTerminal.Kind);
        var sequenceTerminal = Assert.Single(reopened.Events, evt => evt.Kind is
            ExecutionEventKinds.SequenceCompleted or ExecutionEventKinds.SequenceFailed or
            ExecutionEventKinds.SequenceCancelled);
        Assert.Equal(failing ? ExecutionEventKinds.SequenceFailed : ExecutionEventKinds.SequenceCancelled,
            sequenceTerminal.Kind);
        Assert.True(sequenceTerminal.Sequence < rootTerminal.Sequence);
        // Every output-level settlement precedes the Sequence terminal.
        Assert.All(reopened.Events.Where(evt => evt.Kind is ExecutionEventKinds.SequenceOutputFailed or
                ExecutionEventKinds.SequenceOutputCancelled),
            evt => Assert.True(evt.Sequence < sequenceTerminal.Sequence));
        var toolSettled = Assert.Single(reopened.Events, evt => evt.NodeId == "architecture-tool" &&
            evt.Kind is ExecutionEventKinds.NodeCancelled or ExecutionEventKinds.NodeFailed or
            ExecutionEventKinds.NodeCompleted);
        Assert.Equal(ExecutionEventKinds.NodeCancelled, toolSettled.Kind);
        Assert.True(toolSettled.Sequence < rootTerminal.Sequence);

        Assert.True(SequenceAgentEvidence.TryReconstructPlayback(reopened.Events, out var playback));
        Assert.Equal(expectedStatus, playback!.Sequence.Status);
        var inventory = Assert.Single(playback.Sequence.Outputs, output => output.OutputId == "inventory");
        var architecture = Assert.Single(playback.Sequence.Outputs, output => output.OutputId == "architecture");
        Assert.Equal(expectedStatus, inventory.State);
        Assert.Equal("cancelled", architecture.State);
        var inventoryAgent = Assert.Single(playback.Agents);
        Assert.Equal(("inventory-agent", "inventory"), (inventoryAgent.NodeId, inventoryAgent.OutputId));
        // Observed: agent playback state comes from agent lifecycle events, which the throwing/cancelled proof
        // executor never emits, so it stays "running"; the output-level state carries failed/cancelled.
        Assert.NotEqual("completed", inventoryAgent.State);
    }

    private sealed class SiblingRaceAgentExecutor(Task siblingStarted, bool failOnRelease) : IAgentNodeExecutor
    {
        private readonly TaskCompletionSource<bool> release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void ReleaseFailure() => release.TrySetResult(true);

        public Task<IReadOnlyList<AgentProviderDescriptor>> ListProvidersAsync() =>
            Task.FromResult<IReadOnlyList<AgentProviderDescriptor>>([new("sequence-proof", "Proof", "proof", false)]);

        public async Task<string> ExecuteAsync(NodeInvocationContext invocation, string input,
            CancellationToken cancellationToken = default)
        {
            await invocation.RecordEventAsync(ExecutionEventKinds.AgentStarted,
                JsonSerializer.Serialize(new { provider = new { modelId = "sequence-proof" } }));
            var modelCallId = Guid.NewGuid();
            await invocation.RecordEventForAsync(modelCallId, ExecutionEventKinds.ModelRequested,
                JsonSerializer.Serialize(new { modelCallId, input }));
            Started.TrySetResult(true);
            await siblingStarted.WaitAsync(cancellationToken);
            if (failOnRelease)
            {
                await release.Task.WaitAsync(cancellationToken);
                throw new InvalidOperationException("sibling race agent failed");
            }
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return input;
        }
    }

    // Holds the tool branch until the test releases it; after cancellation is observed the tool refuses to
    // settle until `settle` completes, so the test can prove the root waits for the sibling.
    private sealed class HeldToolBroker : INodeToolBroker
    {
        private readonly TaskCompletionSource<bool> started;
        private readonly TaskCompletionSource<bool> cancellationObserved;
        private readonly Task settle;

        public HeldToolBroker(TaskCompletionSource<bool> started, TaskCompletionSource<bool> cancellationObserved,
            Task settle)
        {
            this.started = started;
            this.cancellationObserved = cancellationObserved;
            this.settle = settle;
        }

        public Task<IReadOnlyList<ToolBindingDescriptor>> ListBindingsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ToolBindingDescriptor>>([]);

        public INodeToolScope OpenScope(NodeInvocationContext invocation) => new Scope(this);

        private sealed class Scope(HeldToolBroker owner) : INodeToolScope
        {
            public Task<IReadOnlyList<ToolBindingDescriptor>> ListToolsAsync(CancellationToken cancellationToken = default) =>
                Task.FromResult<IReadOnlyList<ToolBindingDescriptor>>([]);

            public Task<string> InvokeAsync(string bindingId, string input, CancellationToken cancellationToken = default) =>
                HoldAsync(cancellationToken);

            public Task<string> InvokeJsonAsync(string bindingId, JsonElement arguments, CancellationToken cancellationToken = default) =>
                HoldAsync(cancellationToken);

            private async Task<string> HoldAsync(CancellationToken cancellationToken)
            {
                owner.started.TrySetResult(true);
                try { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); }
                catch (OperationCanceledException)
                {
                    owner.cancellationObserved.TrySetResult(true);
                    await owner.settle;
                    throw;
                }
                return "unreachable";
            }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    [Fact]
    public async Task Unified_public_sequence_failing_pinned_child_fails_the_run_without_internal_admission_errors()
    {
        var deniedChild = WorkflowRevision.Create(new WorkflowDefinition("1.1", "public-denied-child", "child-input",
            new Dictionary<string, NodeDefinition>
            {
                ["child-input"] = new(NodeTypes.Input, "1.0.0"),
                ["child-tool"] = new(NodeTypes.McpTool, "1.0.0",
                    new ToolBindingRequest("sequence-proof-denied", new string('d', 64))) { McpArgumentsJson = "{}" },
                ["child-output"] = new(NodeTypes.Output, "1.0.0")
            }.ToImmutableSortedDictionary(),
            new Dictionary<string, ConnectionDefinition>
            {
                ["input-tool"] = new(new("child-input", "out"), new("child-tool", "in"), "data"),
                ["tool-output"] = new(new("child-tool", "out"), new("child-output", "in"), "data")
            }.ToImmutableSortedDictionary()), "public-denied-child");
        var okChild = WorkflowRevision.Create(
            WorkflowEditing.InsertAfter(LinearBaseline("public-ok-child"), "input", NodeTypes.Uppercase), "test");
        var parent = PinnedChildParent("public-failing-child-parent", deniedChild, okChild);
        var events = new ConcurrentQueue<ExecutionEvent>();

        var result = await new WorkflowRunner(new DeniedOutcomeBroker()).ExecuteWithOutcomeAsync(Guid.NewGuid(),
            WorkflowRevision.Create(WorkflowJson.Deserialize(WorkflowJson.Serialize(parent)), "test"), "hello", null,
            evt => { events.Enqueue(evt); return ValueTask.CompletedTask; })
            .WaitAsync(TimeSpan.FromSeconds(10));

        var recorded = events.ToArray();
        Assert.True(result.Failed);
        Assert.Contains(recorded, evt => evt.Kind == ExecutionEventKinds.SubworkflowFailed && evt.ScopeId == "call");
        Assert.Contains(recorded, evt => evt.Kind == ExecutionEventKinds.SequenceOutputFailed &&
            ReadEvidence(evt).OutputId == "call");
        Assert.Contains(recorded, evt => evt.Kind == ExecutionEventKinds.SequenceFailed);
        Assert.DoesNotContain(recorded, evt => evt.NodeId is null && evt.Kind == ExecutionEventKinds.Completed);
        // The failure must be the child's, never an internal admission-ticket error.
        Assert.DoesNotContain(recorded, evt => evt.Data?.Contains("graph-dispatch ticket",
            StringComparison.OrdinalIgnoreCase) == true);
    }

    private static WorkflowDefinition PinnedChildParent(string workflowId, WorkflowRevision firstChild,
        WorkflowRevision secondChild)
    {
        NodeDefinition Call(WorkflowRevision child) => new(NodeTypes.WorkflowCall, "1.0.0")
        {
            WorkflowCall = new(child.Definition.WorkflowId, child.RevisionId, 1, child.ContentHash, child.Definition)
        };
        return new WorkflowDefinition(WorkflowSchemaVersions.UnifiedWorkflowGraph, workflowId, "input",
            new Dictionary<string, NodeDefinition>
            {
                ["input"] = new(NodeTypes.Input, "1.0.0"),
                ["sequence"] = new(NodeTypes.Sequence, "1.0.0")
                {
                    Sequence = new([new("call", "First child"), new("sibling", "Second child")])
                },
                ["call"] = Call(firstChild),
                ["sibling"] = Call(secondChild),
                ["output"] = new(NodeTypes.Output, "1.0.0")
            }.ToImmutableSortedDictionary(),
            ImmutableSortedDictionary<string, ConnectionDefinition>.Empty)
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
    }

    private static WorkflowRevision ToolChild(string workflowId, string bindingId, string contractHash) =>
        WorkflowRevision.Create(new WorkflowDefinition("1.1", workflowId, "child-input",
            new Dictionary<string, NodeDefinition>
            {
                ["child-input"] = new(NodeTypes.Input, "1.0.0"),
                ["child-tool"] = new(NodeTypes.McpTool, "1.0.0", new ToolBindingRequest(bindingId, contractHash))
                {
                    McpArgumentsJson = "{}"
                },
                ["child-output"] = new(NodeTypes.Output, "1.0.0")
            }.ToImmutableSortedDictionary(),
            new Dictionary<string, ConnectionDefinition>
            {
                ["input-tool"] = new(new("child-input", "out"), new("child-tool", "in"), "data"),
                ["tool-output"] = new(new("child-tool", "out"), new("child-output", "in"), "data")
            }.ToImmutableSortedDictionary()), workflowId);

    [Fact]
    public async Task Unified_production_sequence_failing_child_leaves_no_unsettled_tickets_in_any_owner()
    {
        var owners = new List<WorkflowDispatchAdmissionOwner>();
        var timeline = new ConcurrentQueue<(string Kind, string NodeId)>();
        var events = new ConcurrentQueue<ExecutionEvent>();
        var parent = PinnedChildParent("admission-failing-child-parent",
            ToolChild("admission-denied-child", "sequence-proof-denied", new string('d', 64)),
            WorkflowRevision.Create(WorkflowEditing.InsertAfter(LinearBaseline("admission-ok-child"), "input",
                NodeTypes.Uppercase), "test"));

        var result = await new WorkflowRunner(new DeniedOutcomeBroker())
            .ExecuteWithProductionDispatchAdmissionProofAsync(Guid.NewGuid(),
                WorkflowRevision.Create(WorkflowJson.Deserialize(WorkflowJson.Serialize(parent)), "test"), "hello",
                ticket => timeline.Enqueue(("admitted", ticket.TargetNodeId)), evt =>
                {
                    events.Enqueue(evt);
                    if (evt.Kind == ExecutionEventKinds.NodeStarted && evt.NodeId is { } nodeId)
                        timeline.Enqueue(("started", nodeId));
                    return ValueTask.CompletedTask;
                }, ownerProbe: owner => { lock (owners) owners.Add(owner); })
            .WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(result.Failed);
        AssertEveryStartedExecutorWasAdmitted(new ConcurrentQueue<(string Kind, string NodeId)>(timeline));
        Assert.Contains(timeline, item => item == ("admitted", "child-tool"));
        Assert.Equal(3, owners.Count);
        // EnsureQuiescent runs only when a run succeeds, so the probe is what proves a failed run settled.
        Assert.All(owners, owner => Assert.Empty(owner.Outstanding()));
    }

    [Fact]
    public async Task Unified_production_sequence_held_child_executor_keeps_the_parent_call_and_run_open()
    {
        var release = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var broker = new SequencePathBroker((_, cancellation) => release.Task.WaitAsync(cancellation));
        var owners = new List<WorkflowDispatchAdmissionOwner>();
        var events = new ConcurrentQueue<ExecutionEvent>();
        var parent = PinnedChildParent("admission-held-child-parent",
            ToolChild("admission-held-child", "demo.held", new string('a', 64)),
            WorkflowRevision.Create(WorkflowEditing.InsertAfter(LinearBaseline("admission-held-ok-child"), "input",
                NodeTypes.Uppercase), "test"));

        var execution = new WorkflowRunner(broker).ExecuteWithProductionDispatchAdmissionProofAsync(Guid.NewGuid(),
            WorkflowRevision.Create(WorkflowJson.Deserialize(WorkflowJson.Serialize(parent)), "test"), "hello",
            _ => { }, evt => { events.Enqueue(evt); return ValueTask.CompletedTask; },
            ownerProbe: owner => { lock (owners) owners.Add(owner); });

        await broker.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(100);
        Assert.False(execution.IsCompleted);
        Assert.DoesNotContain(events, evt => evt.NodeId is null && evt.Kind == ExecutionEventKinds.Completed);
        // The held child tool is still an open slot in the child's owner, and the parent's call slot stays open
        // because the call executor awaits its child; neither can be mistaken for quiescence.
        string[] outstanding;
        lock (owners) outstanding = owners.SelectMany(owner => owner.Outstanding()).ToArray();
        Assert.Contains("child-tool:Started", outstanding);
        Assert.Contains("call:Started", outstanding);

        release.TrySetResult("{}");
        var result = await execution.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.False(result.Failed);
        lock (owners) Assert.All(owners, owner => Assert.Empty(owner.Outstanding()));
    }

    private sealed record AdmittedRun(WorkflowExecutionResult Result, ExecutionEvent[] Events,
        (string Kind, string NodeId)[] Timeline);

    private static async Task<AdmittedRun> RunAdmittedAsync(WorkflowRunner runner, WorkflowDefinition definition,
        string input, string revisionLabel, Func<ExecutionEvent, bool>? isObservedStart = null,
        Action<WorkflowDispatchAdmissionOwner>? ownerProbe = null)
    {
        var revision = WorkflowRevision.Create(WorkflowJson.Deserialize(WorkflowJson.Serialize(definition)),
            revisionLabel);
        var timeline = new ConcurrentQueue<(string Kind, string NodeId)>();
        var events = new ConcurrentQueue<ExecutionEvent>();
        var result = await runner.ExecuteWithProductionDispatchAdmissionProofAsync(Guid.NewGuid(), revision, input,
                ticket => timeline.Enqueue(("admitted", ticket.TargetNodeId)), evt =>
                {
                    events.Enqueue(evt);
                    if (evt.Kind == ExecutionEventKinds.NodeStarted && evt.NodeId is { } nodeId &&
                        (isObservedStart?.Invoke(evt) ?? true))
                        timeline.Enqueue(("started", nodeId));
                    return ValueTask.CompletedTask;
                }, ownerProbe: ownerProbe)
            .WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(result.Failed, string.Join(" | ", events.Select(evt => $"{evt.Kind}:{evt.NodeId}:{evt.Data}")));
        AssertEveryStartedExecutorWasAdmitted(new ConcurrentQueue<(string Kind, string NodeId)>(timeline));
        return new(result, events.ToArray(), timeline.ToArray());
    }

    // A node reached from several branches (the Output barrier) is admitted once per arriving branch, so the
    // hop is "an admission of `to` after `from` started, then a start of `to` after that admission".
    private static void AssertHopOrder(AdmittedRun run, string from, string to)
    {
        var fromStarted = Array.IndexOf(run.Timeline, ("started", from));
        var toAdmitted = fromStarted < 0 ? -1 : Array.FindIndex(run.Timeline, fromStarted + 1,
            item => item == ("admitted", to));
        var toStarted = toAdmitted < 0 ? -1 : Array.FindIndex(run.Timeline, toAdmitted + 1,
            item => item == ("started", to));
        Assert.True(fromStarted >= 0 && toAdmitted > fromStarted && toStarted > toAdmitted,
            $"Expected '{from}' started, then '{to}' admitted, then '{to}' started; timeline: " +
            string.Join(", ", run.Timeline.Select(item => $"{item.Kind}:{item.NodeId}")));
    }

    [Fact]
    public async Task Unified_production_sequence_mcp_then_agent_path_admits_each_hop_in_order()
    {
        var run = await RunAdmittedAsync(new WorkflowRunner(new RecordingBroker(), new SequenceProofAgentExecutor()),
            SequenceMixedPathDefinition() with { SchemaVersion = WorkflowSchemaVersions.UnifiedWorkflowGraph },
            "hello", "sequence-admission-mixed");

        AssertHopOrder(run, "inventory-tool", "inventory-agent");
        AssertHopOrder(run, "inventory-agent", "output");
        Assert.Equal(2, run.Events.Count(evt => evt.Kind == ExecutionEventKinds.SequenceOutputCompleted));
        Assert.Single(run.Events, evt => evt.NodeId is null && evt.Kind == ExecutionEventKinds.Completed);
    }

    [Fact]
    public async Task Unified_production_sequence_transform_chain_admits_each_deterministic_step()
    {
        // Unified validation requires one data wire into each transform input; the historical 1.49 fixture
        // is execution-only.
        var source = SequenceTransformChainDefinition();
        var definition = source with
        {
            SchemaVersion = WorkflowSchemaVersions.UnifiedWorkflowGraph,
            Connections = source.Connections
                .Add("input-uppercase-first", new(new("input", "out"), new("uppercase-first", "in"), "data"))
                .Add("uppercase-reverse-first", new(new("uppercase-first", "out"), new("reverse-first", "in"), "data"))
                .Add("input-reverse-second", new(new("input", "out"), new("reverse-second", "in"), "data"))
                .Add("reverse-uppercase-second", new(new("reverse-second", "out"), new("uppercase-second", "in"), "data"))
        };
        var run = await RunAdmittedAsync(new WorkflowRunner(new RecordingBroker()), definition,
            "hello", "sequence-admission-transform-chain");

        Assert.Equal("OLLEH", run.Result.Output);
        AssertHopOrder(run, "uppercase-first", "reverse-first");
        AssertHopOrder(run, "reverse-second", "uppercase-second");
        AssertHopOrder(run, "reverse-first", "output");
        Assert.Equal(2, run.Events.Count(evt => evt.Kind == ExecutionEventKinds.SequenceOutputCompleted));
    }

    [Fact]
    public async Task Unified_production_sequence_workflow_call_branch_tickets_parent_and_child_executors()
    {
        var child = WorkflowRevision.Create(
            WorkflowEditing.InsertAfter(LinearBaseline("admission-sequence-child"), "input", NodeTypes.Uppercase),
            "test");
        var call = new NodeDefinition(NodeTypes.WorkflowCall, "1.0.0")
        {
            WorkflowCall = new(child.Definition.WorkflowId, child.RevisionId, 1, child.ContentHash, child.Definition)
        };
        var parent = new WorkflowDefinition(WorkflowSchemaVersions.UnifiedWorkflowGraph,
            "admission-sequence-parent", "input",
            new Dictionary<string, NodeDefinition>
            {
                ["input"] = new(NodeTypes.Input, "1.0.0"),
                ["sequence"] = new(NodeTypes.Sequence, "1.0.0")
                {
                    Sequence = new([new("call", "Pinned child"), new("sibling", "Pinned child again")])
                },
                ["call"] = call,
                ["sibling"] = call,
                ["output"] = new(NodeTypes.Output, "1.0.0")
            }.ToImmutableSortedDictionary(),
            ImmutableSortedDictionary<string, ConnectionDefinition>.Empty)
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

        var owners = new List<WorkflowDispatchAdmissionOwner>();
        var run = await RunAdmittedAsync(new WorkflowRunner(), parent, "hello", "sequence-admission-call",
            ownerProbe: owner => { lock (owners) owners.Add(owner); });

        Assert.Equal("HELLO", run.Result.Output);
        AssertHopOrder(run, "call", "output");
        AssertHopOrder(run, "sibling", "output");
        // Each pinned child runs through its own run-local owner that reports to the same observer. The parent
        // entry is not ticketed in a Sequence graph, so the two "input" admissions are the two children's
        // entry nodes, and every child executor start was admitted first (checked by RunAdmittedAsync).
        Assert.Equal(2, run.Timeline.Count(item => item == ("admitted", "input")));
        Assert.Equal(1, run.Timeline.Count(item => item == ("admitted", "call")));
        // One owner for the root run plus one per child, and none retains an unsettled slot.
        Assert.Equal(3, owners.Count);
        Assert.All(owners, owner => Assert.Empty(owner.Outstanding()));
        Assert.Contains(run.Events, evt => evt.Kind == ExecutionEventKinds.SubworkflowCompleted && evt.ScopeId == "call");
        Assert.Contains(run.Events, evt => evt.Kind == ExecutionEventKinds.SubworkflowCompleted && evt.ScopeId == "sibling");
    }

    [Fact]
    public async Task Unified_production_sequence_json_chain_admits_execution_nodes_and_evaluates_data_nodes_unticketed()
    {
        var child = WorkflowRevision.Create(JsonChild("admission-sequence-json-child"), "test");
        var call = new NodeDefinition(NodeTypes.WorkflowCall, "1.0.0")
        {
            WorkflowCall = new(child.Definition.WorkflowId, child.RevisionId, 1, child.ContentHash, child.Definition)
        };
        var definition = new WorkflowDefinition(WorkflowSchemaVersions.UnifiedWorkflowGraph,
            "admission-sequence-json-parent", "input",
            new Dictionary<string, NodeDefinition>
            {
                ["input"] = new(NodeTypes.Input, "1.0.0"),
                ["sequence"] = new(NodeTypes.Sequence, "1.0.0")
                {
                    Sequence = new([new("call", "Pinned child"), new("sibling", "Second child")])
                },
                ["call"] = call,
                ["sibling"] = call,
                ["project"] = new(NodeTypes.JsonProject, "1.0.0")
                {
                    JsonProjection = new([new JsonProjectionField("answer", "/answer")])
                },
                ["select"] = new(NodeTypes.JsonSelectString, "1.0.0") { JsonPropertySelection = new("/answer") },
                ["output"] = new(NodeTypes.Output, "1.0.0") { OutputDataType = WorkflowDataTypes.String }
            }.ToImmutableSortedDictionary(),
            new Dictionary<string, ConnectionDefinition>
            {
                ["call-project"] = new(new("call", "out"), new("project", "in"), "data"),
                ["project-select"] = new(new("project", "out"), new("select", "in"), "data"),
                ["select-output-data"] = new(new("select", "out"), new("output", "in"), "data")
            }.ToImmutableSortedDictionary())
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

        var run = await RunAdmittedAsync(new WorkflowRunner(), definition, "{\"answer\":\"hello\"}",
            "sequence-admission-json", evt => evt.ScopeId is null);

        Assert.Equal("hello", run.Result.Output);
        AssertHopOrder(run, "call", "output");
        // Pure JSON data nodes are evaluated with recorded evidence but are not dispatched executors.
        Assert.Contains(run.Events, evt => evt.Kind == ExecutionEventKinds.DataEvaluated && evt.NodeId == "project");
        Assert.Contains(run.Events, evt => evt.Kind == ExecutionEventKinds.DataEvaluated && evt.NodeId == "select");
        Assert.DoesNotContain(run.Timeline, item => item.NodeId is "project" or "select");
    }

    private sealed class FaultOnceRunStore(IWorkflowRunStore inner, string eventKind, bool commitBeforeFault)
        : IWorkflowRunStore
    {
        private bool faulted;

        public RunSnapshot Create(WorkflowRevision revision, string input) => inner.Create(revision, input);
        public RunSnapshot? Get(Guid runId) => inner.Get(runId);
        public IReadOnlyList<RunSnapshot> List() => inner.List();

        public RunSnapshot Append(Guid runId, long expectedSequence, IReadOnlyList<ExecutionEvent> events)
        {
            var injectFault = !faulted && events.Any(evt => evt.Kind == eventKind);
            if (injectFault && !commitBeforeFault)
            {
                faulted = true;
                throw new IOException($"Injected rejected {eventKind} append before commit.");
            }
            var snapshot = inner.Append(runId, expectedSequence, events);
            if (!injectFault) return snapshot;
            faulted = true;
            throw new IOException($"Injected committed {eventKind} append with lost acknowledgment.");
        }
    }

    private sealed class ReopenableRunStore : IDisposable
    {
        private readonly string directory = Path.Combine(Path.GetTempPath(), $"vaw-sequence-admission-{Guid.NewGuid():N}");
        private readonly bool sqlite;

        public ReopenableRunStore(WorkflowRevision revision, string input, bool sqlite)
        {
            this.sqlite = sqlite;
            Directory.CreateDirectory(directory);
            Store = ReopenStore();
            RunId = Store.Create(revision, input).Header.RunId;
        }

        public IWorkflowRunStore Store { get; }
        public Guid RunId { get; }

        public IWorkflowRunStore ReopenStore() => sqlite
            ? new SqliteWorkflowRunStore(Path.Combine(directory, "runs.sqlite3"))
            : new FileRunJournal(directory);

        public void Dispose() => Directory.Delete(directory, recursive: true);
    }

    private static void AssertEveryStartedExecutorWasAdmitted(
        ConcurrentQueue<(string Kind, string NodeId)> timeline)
    {
        var admitted = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (kind, nodeId) in timeline)
        {
            if (kind == "admitted")
            {
                admitted.Add(nodeId);
                continue;
            }

            Assert.Equal("started", kind);
            Assert.Contains(nodeId, admitted);
        }
    }

    [Fact]
    public async Task Schema_1_43_executes_each_sequence_path_to_the_output_barrier()
    {
        var broker = new RecordingBroker();
        var events = new List<ExecutionEvent>();
        var result = await new WorkflowRunner(broker).ExecuteWithOutcomeAsync(Guid.NewGuid(),
            WorkflowRevision.Create(SequencePathDefinition(), "sequence-paths"), "ignored", null,
            evt => { events.Add(evt); return ValueTask.CompletedTask; });

        Assert.False(result.Failed);
        Assert.Equal("tool-result", result.Output);
        foreach (var (nodeId, outputId) in new[]
        {
            ("inventory-tool", "inventory"), ("architecture-tool", "architecture")
        })
        {
            var started = Assert.Single(events, evt => evt.Kind == ExecutionEventKinds.NodeStarted && evt.NodeId == nodeId);
            var completed = Assert.Single(events, evt => evt.Kind == ExecutionEventKinds.NodeCompleted && evt.NodeId == nodeId);
            Assert.Equal(started.InvocationId, completed.InvocationId);
            foreach (var evt in new[] { started, completed })
            {
                using var evidence = JsonDocument.Parse(evt.Data!);
                Assert.Equal("redacted", evidence.RootElement.GetProperty("payloadState").GetString());
                var lineage = evidence.RootElement.GetProperty("sequenceLineage");
                Assert.Equal("sequence", lineage.GetProperty("SequenceNodeId").GetString());
                Assert.Equal(outputId, lineage.GetProperty("OutputId").GetString());
            }
        }
        Assert.Equal(2, events.Count(evt => evt.Kind == ExecutionEventKinds.SequenceOutputCompleted));
    }

    [Fact]
    public async Task Schema_1_47_executes_a_terminal_deterministic_uppercase_path_step()
    {
        var events = new List<ExecutionEvent>();
        var result = await new WorkflowRunner(new RecordingBroker()).ExecuteWithOutcomeAsync(
            Guid.NewGuid(), WorkflowRevision.Create(SequenceTransformPathDefinition(), "sequence-transform-path"), "hello", null,
            evt => { events.Add(evt); return ValueTask.CompletedTask; });

        Assert.False(result.Failed);
        Assert.Equal("HELLO", result.Output);
        Assert.Contains(events, evt => evt.Kind == ExecutionEventKinds.NodeCompleted && evt.NodeId == "uppercase");
        Assert.Contains(events, evt => evt.Kind == ExecutionEventKinds.SequenceOutputCompleted);
    }

    [Fact]
    public async Task Schema_1_48_executes_a_terminal_deterministic_reverse_path_step()
    {
        var events = new List<ExecutionEvent>();
        var source = SequenceTransformPathDefinition();
        var definition = source with
        {
            SchemaVersion = WorkflowSchemaVersions.SequenceReversePathGraph,
            Nodes = source.Nodes
                .SetItem("uppercase", new(NodeTypes.SequenceReverse, "1.0.0"))
                .SetItem("uppercase-second", new(NodeTypes.SequenceReverse, "1.0.0"))
        };
        var result = await new WorkflowRunner(new RecordingBroker()).ExecuteWithOutcomeAsync(
            Guid.NewGuid(), WorkflowRevision.Create(definition, "sequence-reverse-path"), "hello", null,
            evt => { events.Add(evt); return ValueTask.CompletedTask; });

        Assert.False(result.Failed);
        Assert.Equal("olleh", result.Output);
        Assert.Contains(events, evt => evt.Kind == ExecutionEventKinds.NodeCompleted && evt.NodeId == "uppercase");
    }

    [Fact]
    public async Task Schema_1_49_executes_two_ordered_deterministic_transform_steps()
    {
        var events = new List<ExecutionEvent>();
        var result = await new WorkflowRunner(new RecordingBroker()).ExecuteWithOutcomeAsync(
            Guid.NewGuid(), WorkflowRevision.Create(SequenceTransformChainDefinition(), "sequence-transform-chain"), "hello", null,
            evt => { events.Add(evt); return ValueTask.CompletedTask; });

        Assert.False(result.Failed);
        Assert.Equal("OLLEH", result.Output);
        Assert.True(events.IndexOf(events.Single(evt => evt.Kind == ExecutionEventKinds.NodeCompleted && evt.NodeId == "uppercase-first")) <
            events.IndexOf(events.Single(evt => evt.Kind == ExecutionEventKinds.NodeCompleted && evt.NodeId == "reverse-first")));
        Assert.Contains(events, evt => evt.Kind == ExecutionEventKinds.NodeCompleted && evt.NodeId == "reverse-first" && evt.Data == "OLLEH");
        Assert.Contains(events, evt => evt.Kind == ExecutionEventKinds.SequenceOutputCompleted);
    }

    private static WorkflowDefinition SequenceTransformPathDefinition()
    {
        var nodes = ImmutableSortedDictionary<string, NodeDefinition>.Empty
            .Add("input", new(NodeTypes.Input, "1.0.0"))
            .Add("sequence", new(NodeTypes.Sequence, "1.0.0") { Sequence = new([new("first", "First"), new("second", "Second")]) })
            .Add("uppercase", new(NodeTypes.SequenceUppercase, "1.0.0"))
            .Add("uppercase-second", new(NodeTypes.SequenceUppercase, "1.0.0"))
            .Add("output", new(NodeTypes.Output, "1.0.0"));
        var execution = ImmutableSortedDictionary<string, ConnectionDefinition>.Empty
            .Add("input-sequence", new(new("input", "then"), new("sequence", "execute"), "execution"))
            .Add("sequence-uppercase", new(new("sequence", "then.first"), new("uppercase", "execute"), "execution"))
            .Add("uppercase-output", new(new("uppercase", "then"), new("output", "execute"), "execution"))
            .Add("sequence-uppercase-second", new(new("sequence", "then.second"), new("uppercase-second", "execute"), "execution"))
            .Add("uppercase-second-output", new(new("uppercase-second", "then"), new("output", "execute"), "execution"));
        return new WorkflowDefinition(WorkflowSchemaVersions.SequenceTransformPathGraph, "sequence-transform-path", "input", nodes,
            ImmutableSortedDictionary<string, ConnectionDefinition>.Empty)
        {
            ExecutionConnections = execution
        };
    }

    private static WorkflowDefinition SequenceTransformChainDefinition()
    {
        var nodes = ImmutableSortedDictionary<string, NodeDefinition>.Empty
            .Add("input", new(NodeTypes.Input, "1.0.0"))
            .Add("sequence", new(NodeTypes.Sequence, "1.0.0") { Sequence = new([new("first", "First"), new("second", "Second")]) })
            .Add("uppercase-first", new(NodeTypes.SequenceUppercase, "1.0.0"))
            .Add("reverse-first", new(NodeTypes.SequenceReverse, "1.0.0"))
            .Add("reverse-second", new(NodeTypes.SequenceReverse, "1.0.0"))
            .Add("uppercase-second", new(NodeTypes.SequenceUppercase, "1.0.0"))
            .Add("output", new(NodeTypes.Output, "1.0.0"));
        var execution = ImmutableSortedDictionary<string, ConnectionDefinition>.Empty
            .Add("input-sequence", new(new("input", "then"), new("sequence", "execute"), "execution"))
            .Add("sequence-uppercase-first", new(new("sequence", "then.first"), new("uppercase-first", "execute"), "execution"))
            .Add("uppercase-first-reverse", new(new("uppercase-first", "then"), new("reverse-first", "execute"), "execution"))
            .Add("reverse-first-output", new(new("reverse-first", "then"), new("output", "execute"), "execution"))
            .Add("sequence-reverse-second", new(new("sequence", "then.second"), new("reverse-second", "execute"), "execution"))
            .Add("reverse-second-uppercase", new(new("reverse-second", "then"), new("uppercase-second", "execute"), "execution"))
            .Add("uppercase-second-output", new(new("uppercase-second", "then"), new("output", "execute"), "execution"));
        return new WorkflowDefinition(WorkflowSchemaVersions.SequenceTransformChainGraph, "sequence-transform-chain", "input", nodes,
            ImmutableSortedDictionary<string, ConnectionDefinition>.Empty)
        {
            ExecutionConnections = execution
        };
    }

    [Fact]
    public async Task Schema_1_46_executes_a_persisted_terminal_agent_path_with_sequence_lineage()
    {
        var events = new List<ExecutionEvent>();
        var agent = new SequenceProofAgentExecutor();
        var result = await new WorkflowRunner(new RecordingBroker(), agent).ExecuteWithOutcomeAsync(Guid.NewGuid(),
            WorkflowRevision.Create(SequenceAgentPathDefinition(), "sequence-agent-path"), "hello", null,
            evt => { events.Add(evt); return ValueTask.CompletedTask; });

        Assert.False(result.Failed);
        Assert.Equal("hello", result.Output);
        Assert.True(agent.InvocationObserved);
        Assert.Contains(events, evt => evt.Kind == ExecutionEventKinds.AgentCompleted && HasSequenceLineage(evt));
        Assert.Contains(events, evt => evt.Kind == ExecutionEventKinds.SequenceOutputCompleted &&
            ReadEvidence(evt).OutputId == "inventory");
        Assert.Equal(2, events.Count(evt => evt.Kind == ExecutionEventKinds.SequenceOutputCompleted));
    }

    [Fact]
    public async Task Schema_1_46_persisted_terminal_agent_path_admits_and_executes_its_finite_turn_limit()
    {
        var events = new List<ExecutionEvent>();
        var definition = SequenceAgentPathDefinition();
        var agentConfiguration = definition.Nodes["inventory-agent"].Agent!;
        Assert.Equal(1, agentConfiguration.MaxTurns);

        var provider = new AgentProviderDescriptor("sequence-proof", "Proof", "scripted-text-v1", true);
        var agents = new AgentNodeExecutor(
            [new AgentProviderRegistration(provider, static () => new OverBudgetChatClient())],
            new RecordingBroker());
        var result = await new WorkflowRunner(new RecordingBroker(), agents).ExecuteWithOutcomeAsync(
            Guid.NewGuid(), WorkflowRevision.Create(definition, "sequence-agent-turn-limit"), "hello", null,
            evt => { events.Add(evt); return ValueTask.CompletedTask; });

        Assert.False(result.Failed);
        Assert.Contains(events, evt => evt.Kind == ExecutionEventKinds.AgentStarted &&
            evt.Data?.Contains("maximumTurns", StringComparison.Ordinal) == true);
        Assert.Contains(events, evt => evt.Kind == ExecutionEventKinds.AgentCompleted);
        Assert.DoesNotContain(events, evt => evt.Kind == ExecutionEventKinds.AgentLimitExceeded);
    }

    [Fact]
    public async Task Schema_1_46_executes_direct_mcp_result_into_a_terminal_agent_path()
    {
        var events = new List<ExecutionEvent>();
        var chatClient = new CapturingChatClient();
        var broker = new RecordingBroker();
        var provider = new AgentProviderDescriptor("sequence-proof", "Proof", "scripted-text-v1", true);
        var agents = new AgentNodeExecutor(
            [new AgentProviderRegistration(provider, () => chatClient)], broker);

        var result = await new WorkflowRunner(broker, agents).ExecuteWithOutcomeAsync(
            Guid.NewGuid(), WorkflowRevision.Create(SequenceMixedPathDefinition(), "sequence-mcp-agent-path"), "hello", null,
            evt => { events.Add(evt); return ValueTask.CompletedTask; });

        Assert.False(result.Failed);
        Assert.Contains("tool-result", chatClient.Messages.SelectMany(message => message.Contents)
            .OfType<TextContent>().Select(content => content.Text));
        var toolCompleted = Assert.Single(events, evt => evt.Kind == ExecutionEventKinds.NodeCompleted && evt.NodeId == "inventory-tool");
        var agentStarted = Assert.Single(events, evt => evt.Kind == ExecutionEventKinds.AgentStarted && evt.NodeId == "inventory-agent");
        Assert.True(toolCompleted.Sequence < agentStarted.Sequence);
        Assert.Contains(events, evt => evt.Kind == ExecutionEventKinds.SequenceOutputCompleted && ReadEvidence(evt).OutputId == "inventory");
    }

    [Fact]
    public async Task Schema_1_46_records_agent_failure_before_sequence_failure()
    {
        var events = new List<ExecutionEvent>();
        var agent = new SequenceProofAgentExecutor(fail: true);

        await Assert.ThrowsAsync<InvalidOperationException>(() => new WorkflowRunner(new RecordingBroker(), agent)
            .ExecuteWithOutcomeAsync(Guid.NewGuid(), WorkflowRevision.Create(SequenceAgentPathDefinition(), "sequence-agent-path-failure"),
                "hello", null, evt => { events.Add(evt); return ValueTask.CompletedTask; }));

        Assert.Contains(events, evt => evt.Kind == ExecutionEventKinds.NodeFailed && evt.NodeId == "inventory-agent");
        Assert.Contains(events, evt => evt.Kind == ExecutionEventKinds.SequenceOutputFailed && ReadEvidence(evt).OutputId == "inventory");
        Assert.Contains(events, evt => evt.Kind == ExecutionEventKinds.SequenceFailed);
        Assert.DoesNotContain(events, evt => evt.Kind == ExecutionEventKinds.SequenceCompleted);
    }

    [Fact]
    public async Task Schema_1_46_records_agent_cancellation_before_sequence_cancellation()
    {
        var events = new List<ExecutionEvent>();
        var agent = new SequenceProofAgentExecutor(blockUntilCancellation: true);
        using var cancellation = new CancellationTokenSource();
        var execution = new WorkflowRunner(new RecordingBroker(), agent).ExecuteWithOutcomeAsync(Guid.NewGuid(),
            WorkflowRevision.Create(SequenceAgentPathDefinition(), "sequence-agent-path-cancel"), "hello", null,
            evt => { events.Add(evt); return ValueTask.CompletedTask; }, cancellation.Token);

        await agent.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => execution);

        Assert.Contains(events, evt => evt.Kind == ExecutionEventKinds.NodeCancelled && evt.NodeId == "inventory-agent");
        Assert.Contains(events, evt => evt.Kind == ExecutionEventKinds.SequenceOutputCancelled && ReadEvidence(evt).OutputId == "inventory");
        Assert.Contains(events, evt => evt.Kind == ExecutionEventKinds.SequenceCancelled);
        Assert.DoesNotContain(events, evt => evt.Kind == ExecutionEventKinds.SequenceCompleted);
    }

    [Fact]
    public async Task Schema_1_43_records_failed_tool_path_and_sequence_failure_evidence()
    {
        var events = new List<ExecutionEvent>();
        var broker = new SequencePathBroker((_, _) => throw new InvalidOperationException("simulated tool failure"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => new WorkflowRunner(broker).ExecuteWithOutcomeAsync(
            Guid.NewGuid(), WorkflowRevision.Create(SequencePathDefinition(), "sequence-path-failure"), "ignored", null,
            evt => { events.Add(evt); return ValueTask.CompletedTask; }));

        Assert.Contains(events, evt => evt.Kind == ExecutionEventKinds.NodeFailed &&
            evt.NodeId is "inventory-tool" or "architecture-tool");
        Assert.Contains(events, evt => evt.Kind == ExecutionEventKinds.SequenceOutputFailed);
        Assert.Contains(events, evt => evt.Kind == ExecutionEventKinds.SequenceFailed);
        Assert.DoesNotContain(events, evt => evt.Kind == ExecutionEventKinds.SequenceCompleted);
        Assert.True(SequenceEvidence.TryReconstructPlayback(events, out var playback));
        Assert.Equal("failed", playback!.Status);
        Assert.Contains(playback.Outputs, output => output.State == "failed");
        Assert.Contains(playback.Outputs, output => output.State == "cancelled");
    }

    [Fact]
    public async Task Schema_1_43_records_cancelled_tool_path_and_sequence_cancellation_evidence()
    {
        var events = new ConcurrentQueue<ExecutionEvent>();
        var broker = new SequencePathBroker(async (_, cancellationToken) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return "unreachable";
        });
        using var cancellation = new CancellationTokenSource();
        var execution = new WorkflowRunner(broker).ExecuteWithOutcomeAsync(
            Guid.NewGuid(), WorkflowRevision.Create(SequencePathDefinition(), "sequence-path-cancellation"), "ignored", null,
            evt => { events.Enqueue(evt); return ValueTask.CompletedTask; }, cancellation.Token);

        await broker.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => execution);

        Assert.Contains(events, evt => evt.Kind == ExecutionEventKinds.NodeCancelled &&
            evt.NodeId is "inventory-tool" or "architecture-tool");
        Assert.Contains(events, evt => evt.Kind == ExecutionEventKinds.SequenceOutputCancelled &&
            ReadEvidence(evt).OutputId == "inventory");
        Assert.Contains(events, evt => evt.Kind == ExecutionEventKinds.SequenceCancelled);
        Assert.DoesNotContain(events, evt => evt.Kind == ExecutionEventKinds.SequenceCompleted);
        Assert.True(SequenceEvidence.TryReconstructPlayback(events, out var playback));
        Assert.Equal("cancelled", playback!.Status);
        Assert.Equal("cancelled", Assert.Single(playback.Outputs, output => output.OutputId == "inventory").State);
    }

    [Fact]
    public async Task Private_agent_sequence_proof_records_nested_agent_events_with_sequence_lineage()
    {
        var events = new List<ExecutionEvent>();
        var agent = new SequenceProofAgentExecutor();
        var result = await new WorkflowRunner(agents: agent).ExecuteSequenceAgentProductionProofAsync(
            Guid.NewGuid(), "proof input", evt => { events.Add(evt); return ValueTask.CompletedTask; });

        Assert.False(result.Failed);
        Assert.Equal("proof input", result.Output);
        Assert.True(agent.InvocationObserved);
        Assert.Contains(events, evt => evt.Kind == ExecutionEventKinds.SequenceOutputDispatched);
        Assert.Contains(events, evt => evt.Kind == ExecutionEventKinds.SequenceOutputCompleted);
        Assert.Contains(events.Where(evt => evt.Kind == ExecutionEventKinds.ModelRequested), evt =>
            evt.Data?.Contains("sequenceLineage", StringComparison.Ordinal) == true);
        Assert.Contains(events.Where(evt => evt.Kind == ExecutionEventKinds.ModelResponded), evt =>
            evt.Data?.Contains("sequenceLineage", StringComparison.Ordinal) == true);
        Assert.Equal(ExecutionEventKinds.Completed, events[^1].Kind);
    }

    [Fact]
    public async Task Private_agent_sequence_proof_exercises_real_agent_executor_and_preserves_lineage()
    {
        var events = new List<ExecutionEvent>();
        var registration = new AgentProviderRegistration(
            new("sequence-proof", "Proof", "scripted-text-v1", true),
            () => new DeterministicDemoChatClient());
        var agents = new AgentNodeExecutor([registration], new RecordingBroker());
        var result = await new WorkflowRunner(agents: agents).ExecuteSequenceAgentProductionProofAsync(
            Guid.NewGuid(), "proof input", evt => { events.Add(evt); return ValueTask.CompletedTask; });

        Assert.False(result.Failed);
        Assert.StartsWith("Demo agent:", result.Output, StringComparison.Ordinal);
        Assert.Contains(events, evt => evt.Kind == ExecutionEventKinds.AgentStarted && HasSequenceLineage(evt));
        Assert.Contains(events, evt => evt.Kind == ExecutionEventKinds.ModelRequested && HasSequenceLineage(evt));
        Assert.Contains(events, evt => evt.Kind == ExecutionEventKinds.ModelResponded && HasSequenceLineage(evt));
        Assert.Contains(events, evt => evt.Kind == ExecutionEventKinds.AgentCompleted && HasSequenceLineage(evt));
        Assert.True(SequenceEvidence.TryReconstructPlayback(events, out var playback));
        Assert.Equal("completed", playback!.Status);
        Assert.Equal("agent", Assert.Single(playback.Outputs).OutputId);
    }

    [Fact]
    public async Task Private_agent_sequence_proof_fails_closed_when_provider_is_missing()
    {
        var events = new List<ExecutionEvent>();
        await Assert.ThrowsAsync<InvalidOperationException>(() => new WorkflowRunner(
            agents: new AgentNodeExecutor([], new RecordingBroker())).ExecuteSequenceAgentProductionProofAsync(
                Guid.NewGuid(), "proof input", evt => { events.Add(evt); return ValueTask.CompletedTask; }));

        Assert.Contains(events, evt => evt.Kind == ExecutionEventKinds.SequenceOutputFailed);
        Assert.Contains(events, evt => evt.Kind == ExecutionEventKinds.SequenceFailed);
        Assert.DoesNotContain(events, evt => evt.Kind == ExecutionEventKinds.ModelRequested);
    }

    [Fact]
    public async Task Agent_executor_rejects_provider_replacement_after_definition_admission()
    {
        var descriptor = new AgentProviderDescriptor("sequence-proof", "Proof", "scripted-text-v1", true);
        var agents = new AgentNodeExecutor(
            [new AgentProviderRegistration(descriptor, () => new DeterministicDemoChatClient())],
            new RecordingBroker());

        agents.ReplaceProviders([new AgentProviderRegistration(
            descriptor with { ModelId = "replacement-model" }, () => new DeterministicDemoChatClient())]);

        var revision = WorkflowRevision.Create(SequenceAgentPathDefinition(), "sequence-agent-contract-test");
        var context = new NodeInvocationContext(Guid.NewGuid(), revision, "inventory-agent", Guid.NewGuid(),
            (_, _) => ValueTask.CompletedTask);
        await Assert.ThrowsAsync<InvalidOperationException>(() => agents.ExecuteAsync(context, "proof input"));
    }

    [Fact]
    public async Task Persisted_terminal_agent_path_enforces_aggregate_token_limit_and_settles_sequence_failure()
    {
        var events = new List<ExecutionEvent>();
        var provider = new AgentProviderDescriptor("sequence-proof", "Proof", "scripted-text-v1", true);
        var agents = new AgentNodeExecutor(
            [new AgentProviderRegistration(provider, static () => new OverBudgetChatClient())],
            new RecordingBroker());
        var source = SequenceAgentPathDefinition();
        var definition = source with
        {
            Nodes = source.Nodes.SetItem("inventory-agent",
                source.Nodes["inventory-agent"] with
                {
                    Agent = source.Nodes["inventory-agent"].Agent! with { MaxTotalTokens = 5 }
                })
        };

        var failure = await Record.ExceptionAsync(() => new WorkflowRunner(new RecordingBroker(), agents)
            .ExecuteWithOutcomeAsync(Guid.NewGuid(), WorkflowRevision.Create(definition, "sequence-agent-budget"), "hello", null,
                evt => { events.Add(evt); return ValueTask.CompletedTask; }));

        Assert.NotNull(failure);
        Assert.Contains(events, evt => evt.Kind == ExecutionEventKinds.AgentLimitExceeded &&
            evt.Data!.Contains("tokens_exhausted", StringComparison.Ordinal));
        Assert.Contains(events, evt => evt.Kind == ExecutionEventKinds.SequenceOutputFailed);
        Assert.Contains(events, evt => evt.Kind == ExecutionEventKinds.SequenceFailed);
        Assert.DoesNotContain(events, evt => evt.Kind == ExecutionEventKinds.AgentCompleted);
    }

    [Fact]
    public async Task Persisted_terminal_agent_paths_settle_real_executor_failure_and_cancel_in_flight_sibling()
    {
        var events = new List<ExecutionEvent>();
        var source = SequenceAgentPathDefinition();
        var siblingProvider = new AgentProviderDescriptor("sequence-sibling", "Sibling", "scripted-text-v1", true);
        var sibling = new NodeDefinition(NodeTypes.Agent, "1.0.0")
        {
            Agent = new AgentConfiguration(siblingProvider.ProviderId, null, "Return the supplied text.", [])
            {
                ProviderContractHash = siblingProvider.ContractHash,
                MaxTurns = 1,
                MaxTotalTokens = 4000
            }
        };
        var definition = source with
        {
            Nodes = source.Nodes.Remove("architecture-tool").Add("architecture-agent", sibling),
            ExecutionConnections = source.ExecutionConnections!
                .Remove("sequence-architecture")
                .Remove("architecture-output")
                .Add("sequence-architecture", new(new("sequence", "then.architecture"), new("architecture-agent", "execute"), "execution"))
                .Add("architecture-output", new(new("architecture-agent", "out"), new("output", "execute"), "execution"))
        };
        var siblingStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var agents = new AgentNodeExecutor(
            [
                new AgentProviderRegistration(new("sequence-proof", "Failing", "scripted-failure-v1", true), static () => new FailingChatClient()),
                new AgentProviderRegistration(siblingProvider, () => new CancellationAwareChatClient(siblingStarted))
            ],
            new RecordingBroker());

        var failure = await Record.ExceptionAsync(() => new WorkflowRunner(new RecordingBroker(), agents)
            .ExecuteWithOutcomeAsync(Guid.NewGuid(), WorkflowRevision.Create(definition, "sequence-agent-settlement"), "hello", null,
                evt => { events.Add(evt); return ValueTask.CompletedTask; }));

        await siblingStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.NotNull(failure);
        Assert.Contains(events, evt => evt.Kind == ExecutionEventKinds.SequenceOutputFailed);
        Assert.Contains(events, evt => evt.Kind == ExecutionEventKinds.SequenceOutputCancelled);
        Assert.Contains(events, evt => evt.Kind == ExecutionEventKinds.SequenceFailed);
        Assert.DoesNotContain(events, evt => evt.Kind == ExecutionEventKinds.SequenceCompleted);
    }

    [Fact]
    public async Task Private_agent_sequence_proof_records_provider_timeout_as_failure_without_retry()
    {
        var events = new List<ExecutionEvent>();
        var registration = new AgentProviderRegistration(
            new("sequence-proof", "Blocking", "scripted-text-v1", true),
            () => new BlockingChatClient()) { InvocationTimeout = TimeSpan.FromMilliseconds(25) };
        var agents = new AgentNodeExecutor([registration], new RecordingBroker());

        await Assert.ThrowsAsync<InvalidOperationException>(() => new WorkflowRunner(agents: agents)
            .ExecuteSequenceAgentProductionProofAsync(Guid.NewGuid(), "proof input",
                evt => { events.Add(evt); return ValueTask.CompletedTask; }));

        Assert.Contains(events, evt => evt.Kind == ExecutionEventKinds.AgentLimitExceeded &&
            evt.Data?.Contains("timeout", StringComparison.Ordinal) == true);
        Assert.Contains(events, evt => evt.Kind == ExecutionEventKinds.AgentFailed &&
            evt.Data?.Contains("no automatic retry", StringComparison.Ordinal) == true);
        Assert.Contains(events, evt => evt.Kind == ExecutionEventKinds.SequenceOutputFailed);
        Assert.DoesNotContain(events, evt => evt.Kind == ExecutionEventKinds.ModelResponded);
    }

    [Fact]
    public async Task Private_agent_fanout_uses_authored_ordinal_for_result_when_responses_complete_out_of_order()
    {
        var events = new List<ExecutionEvent>();
        var client = new OutOfOrderChatClient();
        var registration = new AgentProviderRegistration(
            new("sequence-proof", "Out of order", "scripted-text-v1", true), () => client);
        var agents = new AgentNodeExecutor([registration], new RecordingBroker());
        var result = await new WorkflowRunner(agents: agents).ExecuteSequenceAgentFanoutProductionProofAsync(
            Guid.NewGuid(), "proof input", evt => { events.Add(evt); return ValueTask.CompletedTask; });

        Assert.False(result.Failed);
        Assert.Equal("response-1", result.Output);
        Assert.True(client.SecondResponseCompletedFirst);
        Assert.True(SequenceEvidence.TryReconstructPlayback(events, out var playback));
        Assert.Equal("completed", playback!.Status);
        Assert.Equal(2, playback.Outputs.Count);
    }

    [Fact]
    public async Task Private_agent_sequence_proof_settles_output_when_agent_fails()
    {
        var events = new List<ExecutionEvent>();
        var agent = new SequenceProofAgentExecutor(fail: true);
        await Assert.ThrowsAsync<InvalidOperationException>(() => new WorkflowRunner(agents: agent).ExecuteSequenceAgentProductionProofAsync(
            Guid.NewGuid(), "proof input", evt => { events.Add(evt); return ValueTask.CompletedTask; }));

        Assert.Contains(events, evt => evt.Kind == ExecutionEventKinds.SequenceOutputFailed);
        Assert.Contains(events, evt => evt.Kind == ExecutionEventKinds.SequenceFailed);
        Assert.DoesNotContain(events, evt => evt.Kind == ExecutionEventKinds.SequenceCompleted);
    }

    [Fact]
    public async Task Private_agent_sequence_proof_settles_output_when_agent_is_cancelled()
    {
        var events = new List<ExecutionEvent>();
        var agent = new SequenceProofAgentExecutor(blockUntilCancellation: true);
        using var cancellation = new CancellationTokenSource();
        var execution = new WorkflowRunner(agents: agent).ExecuteSequenceAgentProductionProofAsync(
            Guid.NewGuid(), "proof input", evt => { events.Add(evt); return ValueTask.CompletedTask; },
            cancellationToken: cancellation.Token);

        await agent.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => execution);

        Assert.Contains(events, evt => evt.Kind == ExecutionEventKinds.SequenceOutputCancelled);
        Assert.Contains(events, evt => evt.Kind == ExecutionEventKinds.SequenceCancelled);
        Assert.DoesNotContain(events, evt => evt.Kind == ExecutionEventKinds.SequenceCompleted);
    }

    [Fact]
    public async Task Private_agent_sequence_fanout_cancels_active_sibling_when_one_agent_fails()
    {
        var events = new List<ExecutionEvent>();
        var agent = new SequenceProofAgentExecutor(failFirstConcurrentInvocation: true);
        await Assert.ThrowsAsync<InvalidOperationException>(() => new WorkflowRunner(agents: agent)
            .ExecuteSequenceAgentFanoutProductionProofAsync(Guid.NewGuid(), "proof input",
                evt => { events.Add(evt); return ValueTask.CompletedTask; }));

        Assert.Contains(events, evt => evt.Kind == ExecutionEventKinds.SequenceOutputFailed &&
            ReadEvidence(evt).OutputId == "failing");
        Assert.Contains(events, evt => evt.Kind == ExecutionEventKinds.SequenceOutputCancelled &&
            ReadEvidence(evt).OutputId == "sibling");
        Assert.Contains(events, evt => evt.Kind == ExecutionEventKinds.SequenceFailed);
        Assert.DoesNotContain(events, evt => evt.Kind == ExecutionEventKinds.SequenceCompleted);
    }

    private static SequenceLifecycleEvidence ReadEvidence(ExecutionEvent executionEvent)
    {
        Assert.True(SequenceEvidence.TryRead(executionEvent, out var evidence));
        return evidence!;
    }

    private static WorkflowCallLifecycleEvidence ReadChildEvidence(ExecutionEvent executionEvent)
    {
        Assert.True(WorkflowCallLifecycleEvidence.TryRead(executionEvent.Data, out var evidence));
        return evidence!;
    }

    private static bool HasSequenceLineage(ExecutionEvent executionEvent) =>
        executionEvent.Data?.Contains("sequenceLineage", StringComparison.Ordinal) == true;

    private static WorkflowDefinition PersistedSequenceDefinition()
    {
        var nodes = ImmutableSortedDictionary<string, NodeDefinition>.Empty.WithComparers(StringComparer.Ordinal)
            .Add("input", new(NodeTypes.Input, "1.0.0"))
            .Add("sequence", new(NodeTypes.Sequence, "1.0.0")
            {
                Sequence = new SequenceConfiguration([
                    new("inventory", "Inventory"), new("architecture", "Architecture"), new("quality", "Quality")])
            })
            .Add("output", new(NodeTypes.Output, "1.0.0"));
        var edges = ImmutableSortedDictionary<string, ConnectionDefinition>.Empty.WithComparers(StringComparer.Ordinal)
            .Add("input-sequence", new(new("input", "then"), new("sequence", "execute"), "execution"))
            .Add("sequence-inventory", new(new("sequence", "then.inventory"), new("output", "execute"), "execution"))
            .Add("sequence-architecture", new(new("sequence", "then.architecture"), new("output", "execute"), "execution"))
            .Add("sequence-quality", new(new("sequence", "then.quality"), new("output", "execute"), "execution"));
        return new WorkflowDefinition(WorkflowSchemaVersions.SequenceGraph, "sequence-runtime", "input", nodes,
            ImmutableSortedDictionary<string, ConnectionDefinition>.Empty.WithComparers(StringComparer.Ordinal))
        {
            ExecutionConnections = edges
        };
    }

    private static WorkflowDefinition SequencePathDefinition()
    {
        var nodes = ImmutableSortedDictionary<string, NodeDefinition>.Empty.WithComparers(StringComparer.Ordinal)
            .Add("input", new(NodeTypes.Input, "1.0.0"))
            .Add("sequence", new(NodeTypes.Sequence, "1.0.0")
            {
                Sequence = new SequenceConfiguration([new("inventory", "Inventory"), new("architecture", "Architecture")])
            })
            .Add("inventory-tool", new(NodeTypes.McpTool, "1.0.0", new ToolBindingRequest("demo.inventory", new string('a', 64)))
            {
                McpArgumentsJson = "{}"
            })
            .Add("architecture-tool", new(NodeTypes.McpTool, "1.0.0", new ToolBindingRequest("demo.architecture", new string('b', 64)))
            {
                McpArgumentsJson = "{}"
            })
            .Add("output", new(NodeTypes.Output, "1.0.0"));
        var execution = ImmutableSortedDictionary<string, ConnectionDefinition>.Empty.WithComparers(StringComparer.Ordinal)
            .Add("input-sequence", new(new("input", "then"), new("sequence", "execute"), "execution"))
            .Add("sequence-inventory", new(new("sequence", "then.inventory"), new("inventory-tool", "execute"), "execution"))
            .Add("inventory-output", new(new("inventory-tool", "then"), new("output", "execute"), "execution"))
            .Add("sequence-architecture", new(new("sequence", "then.architecture"), new("architecture-tool", "execute"), "execution"))
            .Add("architecture-output", new(new("architecture-tool", "then"), new("output", "execute"), "execution"));
        return new WorkflowDefinition(WorkflowSchemaVersions.SequencePathGraph, "sequence-paths", "input", nodes,
            ImmutableSortedDictionary<string, ConnectionDefinition>.Empty.WithComparers(StringComparer.Ordinal))
        {
            ExecutionConnections = execution
        };
    }

    private static WorkflowDefinition SequenceAgentPathDefinition()
    {
        var source = SequencePathDefinition();
        var agent = new NodeDefinition(NodeTypes.Agent, "1.0.0")
        {
            Agent = new AgentConfiguration("sequence-proof", null, "Return the supplied text.", [])
            { ProviderContractHash = new AgentProviderDescriptor("sequence-proof", "Proof", "scripted-text-v1", true).ContractHash,
                MaxTurns = 1, MaxTotalTokens = 4000 }
        };
        return source with
        {
            SchemaVersion = WorkflowSchemaVersions.SequenceAgentPathGraph,
            Nodes = source.Nodes.Remove("inventory-tool").Add("inventory-agent", agent),
            ExecutionConnections = source.ExecutionConnections!
                .Remove("sequence-inventory")
                .Remove("inventory-output")
                .Add("sequence-inventory", new(new("sequence", "then.inventory"), new("inventory-agent", "execute"), "execution"))
                .Add("inventory-output", new(new("inventory-agent", "out"), new("output", "execute"), "execution"))
        };
    }

    private static WorkflowDefinition SequenceMixedPathDefinition()
    {
        var source = SequenceAgentPathDefinition();
        var tool = new NodeDefinition(NodeTypes.McpTool, "1.0.0", new ToolBindingRequest("demo.inventory", new string('a', 64)))
        {
            McpArgumentsJson = "{}"
        };
        return source with
        {
            Nodes = source.Nodes.Add("inventory-tool", tool),
            ExecutionConnections = source.ExecutionConnections!
                .Remove("sequence-inventory")
                .Add("sequence-inventory", new(new("sequence", "then.inventory"), new("inventory-tool", "execute"), "execution"))
                .Add("inventory-agent", new(new("inventory-tool", "then"), new("inventory-agent", "execute"), "execution"))
        };
    }

    private sealed class SequenceProofAgentExecutor : IAgentNodeExecutor
    {
        private readonly bool fail;
        private readonly bool blockUntilCancellation;
        private readonly bool failFirstConcurrentInvocation;
        private readonly TaskCompletionSource<bool>? releaseWhenReady;
        private int concurrentInvocationCount;

        public bool InvocationObserved { get; private set; }
        public TaskCompletionSource<bool> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private TaskCompletionSource<bool> BothConcurrentInvocationsStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public SequenceProofAgentExecutor(bool fail = false, bool blockUntilCancellation = false,
            bool failFirstConcurrentInvocation = false, TaskCompletionSource<bool>? releaseWhenReady = null)
        {
            this.fail = fail;
            this.blockUntilCancellation = blockUntilCancellation;
            this.failFirstConcurrentInvocation = failFirstConcurrentInvocation;
            this.releaseWhenReady = releaseWhenReady;
        }

        public Task<IReadOnlyList<AgentProviderDescriptor>> ListProvidersAsync() =>
            Task.FromResult<IReadOnlyList<AgentProviderDescriptor>>([new("sequence-proof", "Proof", "proof", false)]);

        public async Task<string> ExecuteAsync(NodeInvocationContext invocation, string input,
            CancellationToken cancellationToken = default)
        {
            InvocationObserved = true;
            Started.TrySetResult(true);
            var concurrentInvocationNumber = failFirstConcurrentInvocation
                ? Interlocked.Increment(ref concurrentInvocationCount) : 0;
            if (concurrentInvocationNumber == 2) BothConcurrentInvocationsStarted.TrySetResult(true);
            await invocation.RecordEventAsync(ExecutionEventKinds.AgentStarted,
                JsonSerializer.Serialize(new { provider = new { modelId = "sequence-proof" } }));
            var modelCallId = Guid.NewGuid();
            await invocation.RecordEventForAsync(modelCallId, ExecutionEventKinds.ModelRequested,
                JsonSerializer.Serialize(new { modelCallId, input }));
            if (fail)
                throw new InvalidOperationException("proof agent failed");
            if (failFirstConcurrentInvocation)
            {
                await BothConcurrentInvocationsStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
                if (concurrentInvocationNumber == 1)
                    throw new InvalidOperationException("proof agent failed");
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            else if (releaseWhenReady is not null)
                await releaseWhenReady.Task.WaitAsync(cancellationToken);
            else if (blockUntilCancellation)
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            await invocation.RecordEventForAsync(modelCallId, ExecutionEventKinds.ModelResponded,
                JsonSerializer.Serialize(new { modelCallId, output = input }));
            await invocation.RecordEventAsync(ExecutionEventKinds.AgentCompleted, input);
            return input;
        }
    }

    private sealed class BlockingChatClient : IChatClient
    {
        public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("Blocking client unexpectedly completed.");
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }

    private sealed class OutOfOrderChatClient : IChatClient
    {
        private readonly TaskCompletionSource<bool> secondStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> secondResponded = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int requestCount;
        public bool SecondResponseCompletedFirst { get; private set; }

        public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            var request = Interlocked.Increment(ref requestCount);
            if (request == 2)
            {
                secondStarted.TrySetResult(true);
                secondResponded.TrySetResult(true);
                SecondResponseCompletedFirst = true;
                return Response("response-2");
            }

            await secondStarted.Task.WaitAsync(cancellationToken);
            await secondResponded.Task.WaitAsync(cancellationToken);
            await Task.Delay(20, cancellationToken);
            return Response("response-1");
        }

        private static ChatResponse Response(string text) => new(new ChatMessage(ChatRole.Assistant, text))
        {
            ModelId = "out-of-order-v1",
            FinishReason = ChatFinishReason.Stop
        };

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }

    private sealed class OverBudgetChatClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) => Task.FromResult(new ChatResponse(
            new ChatMessage(ChatRole.Assistant, "bounded response"))
        {
            ModelId = "scripted-text-v1",
            FinishReason = ChatFinishReason.Stop,
            Usage = new UsageDetails { InputTokenCount = 4, OutputTokenCount = 3, TotalTokenCount = 7 }
        });

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }

    private sealed class CapturingChatClient : IChatClient
    {
        public List<ChatMessage> Messages { get; } = [];

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            Messages.AddRange(messages);
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "bounded response"))
            {
                ModelId = "scripted-text-v1",
                FinishReason = ChatFinishReason.Stop,
                Usage = new UsageDetails { InputTokenCount = 4, OutputTokenCount = 3, TotalTokenCount = 7 }
            });
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }

    private sealed class FailingChatClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            Task.FromException<ChatResponse>(new InvalidOperationException("scripted provider failure"));

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }

    private sealed class CancellationAwareChatClient(TaskCompletionSource<bool> started) : IChatClient
    {
        public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            started.TrySetResult(true);
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new OperationCanceledException(cancellationToken);
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }

    private sealed class RecordingBroker : INodeToolBroker
    {
        public List<JsonElement> Arguments { get; } = [];

        public Task<IReadOnlyList<ToolBindingDescriptor>> ListBindingsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ToolBindingDescriptor>>([]);

        public INodeToolScope OpenScope(NodeInvocationContext invocation) => new Scope(this);

        private sealed class Scope(RecordingBroker owner) : INodeToolScope
        {
            public Task<IReadOnlyList<ToolBindingDescriptor>> ListToolsAsync(CancellationToken cancellationToken = default) =>
                Task.FromResult<IReadOnlyList<ToolBindingDescriptor>>([]);

            public Task<string> InvokeAsync(string bindingId, string input, CancellationToken cancellationToken = default) =>
                Task.FromResult("tool-result");

            public Task<string> InvokeJsonAsync(string bindingId, JsonElement arguments, CancellationToken cancellationToken = default)
            {
                owner.Arguments.Add(arguments.Clone());
                return Task.FromResult("tool-result");
            }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private sealed class CommitThenThrowRunStore(IWorkflowRunStore inner, string eventKind) : IWorkflowRunStore
    {
        private bool committedAndThrown;

        public RunSnapshot Create(WorkflowRevision revision, string input) => inner.Create(revision, input);
        public RunSnapshot? Get(Guid runId) => inner.Get(runId);
        public IReadOnlyList<RunSnapshot> List() => inner.List();

        public RunSnapshot Append(Guid runId, long expectedSequence, IReadOnlyList<ExecutionEvent> events)
        {
            var snapshot = inner.Append(runId, expectedSequence, events);
            if (!committedAndThrown && events.Any(evt => evt.Kind == eventKind))
            {
                committedAndThrown = true;
                throw new IOException($"Injected committed {eventKind} append with lost acknowledgment.");
            }
            return snapshot;
        }
    }

    private sealed class BoundaryGate(string blockedBoundary) : IWorkflowExecutionGate
    {
        public TaskCompletionSource Blocked { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask WaitForBoundaryAsync(string? nextNodeId, CancellationToken cancellationToken)
        {
            if (nextNodeId != blockedBoundary) return ValueTask.CompletedTask;
            Blocked.TrySetResult();
            return new(WaitAsync(cancellationToken));
        }

        private static async Task WaitAsync(CancellationToken cancellationToken) => await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
    }

    private sealed class ReleaseGate(string blockedBoundary) : IWorkflowExecutionGate
    {
        public TaskCompletionSource Blocked { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask WaitForBoundaryAsync(string? nextNodeId, CancellationToken cancellationToken)
        {
            if (nextNodeId != blockedBoundary) return ValueTask.CompletedTask;
            Blocked.TrySetResult();
            return new(Release.Task.WaitAsync(cancellationToken));
        }
    }

    private sealed class SequencePathBroker(Func<string, CancellationToken, Task<string>> invoke) : INodeToolBroker
    {
        public TaskCompletionSource<bool> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<IReadOnlyList<ToolBindingDescriptor>> ListBindingsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ToolBindingDescriptor>>([]);

        public INodeToolScope OpenScope(NodeInvocationContext invocation) => new Scope(this, invoke);

        private sealed class Scope(SequencePathBroker owner, Func<string, CancellationToken, Task<string>> invoke) : INodeToolScope
        {
            public Task<IReadOnlyList<ToolBindingDescriptor>> ListToolsAsync(CancellationToken cancellationToken = default) =>
                Task.FromResult<IReadOnlyList<ToolBindingDescriptor>>([]);

            public Task<string> InvokeAsync(string bindingId, string input, CancellationToken cancellationToken = default)
            {
                owner.Started.TrySetResult(true);
                return invoke(input, cancellationToken);
            }

            public Task<string> InvokeJsonAsync(string bindingId, JsonElement arguments, CancellationToken cancellationToken = default)
            {
                owner.Started.TrySetResult(true);
                return invoke(arguments.GetRawText(), cancellationToken);
            }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private sealed class UnknownOutcomeBroker : INodeToolBroker
    {
        public Task<IReadOnlyList<ToolBindingDescriptor>> ListBindingsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ToolBindingDescriptor>>([new("sequence-proof-unknown", "Unknown", "proof", "unknown", new string('a', 64))]);

        public INodeToolScope OpenScope(NodeInvocationContext invocation) => new Scope();

        private sealed class Scope : INodeToolScope
        {
            public Task<IReadOnlyList<ToolBindingDescriptor>> ListToolsAsync(CancellationToken cancellationToken = default) =>
                Task.FromResult<IReadOnlyList<ToolBindingDescriptor>>([]);

            public Task<string> InvokeAsync(string bindingId, string input, CancellationToken cancellationToken = default) =>
                Task.FromException<string>(new ToolInvocationException("The proof tool outcome is unknown.", ToolInvocationFailureDisposition.OutcomeUnknown));

            public Task<string> InvokeJsonAsync(string bindingId, JsonElement arguments, CancellationToken cancellationToken = default) =>
                InvokeAsync(bindingId, arguments.GetRawText(), cancellationToken);

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private sealed class DeniedOutcomeBroker : INodeToolBroker
    {
        public Task<IReadOnlyList<ToolBindingDescriptor>> ListBindingsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ToolBindingDescriptor>>([new("sequence-proof-denied", "Denied", "proof", "denied", new string('d', 64))]);

        public INodeToolScope OpenScope(NodeInvocationContext invocation) => new Scope();

        private sealed class Scope : INodeToolScope
        {
            public Task<IReadOnlyList<ToolBindingDescriptor>> ListToolsAsync(CancellationToken cancellationToken = default) =>
                Task.FromResult<IReadOnlyList<ToolBindingDescriptor>>([]);

            public Task<string> InvokeAsync(string bindingId, string input, CancellationToken cancellationToken = default) =>
                Task.FromException<string>(new ToolAccessDeniedException());

            public Task<string> InvokeJsonAsync(string bindingId, JsonElement arguments, CancellationToken cancellationToken = default) =>
                InvokeAsync(bindingId, arguments.GetRawText(), cancellationToken);

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
