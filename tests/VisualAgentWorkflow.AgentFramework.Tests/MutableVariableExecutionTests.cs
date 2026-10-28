using System.Collections.Immutable;
using System.Text.Json;
using VisualAgentWorkflow.Core;
using Xunit;

namespace VisualAgentWorkflow.AgentFramework.Tests;

public sealed class MutableVariableExecutionTests
{
    [Fact]
    public async Task Set_then_get_reads_the_committed_value_and_records_state_transitions()
    {
        var revision = WorkflowRevision.Create(Definition(), "variable-proof");
        var events = new List<ExecutionEvent>();

        var result = await new WorkflowRunner().ExecuteAsync(Guid.NewGuid(), revision, "from input",
            evt => { events.Add(evt); return ValueTask.CompletedTask; });

        Assert.Equal("from input", result);
        var written = events.Single(evt => evt.Kind == ExecutionEventKinds.VariableWritten);
        var read = events.Single(evt => evt.Kind == ExecutionEventKinds.VariableRead);
        Assert.Equal("set", written.NodeId);
        Assert.Equal("get", read.NodeId);
        Assert.Equal("message", JsonDocument.Parse(written.Data!).RootElement.GetProperty("variableId").GetString());
        Assert.Equal("from input", JsonDocument.Parse(read.Data!).RootElement.GetProperty("value").GetString());
        Assert.Equal(0, JsonDocument.Parse(written.Data!).RootElement.GetProperty("loopIteration").GetInt32());
        Assert.Equal(0, JsonDocument.Parse(read.Data!).RootElement.GetProperty("loopIteration").GetInt32());
        Assert.Equal(JsonDocument.Parse(written.Data!).RootElement.GetProperty("version").GetInt64(),
            JsonDocument.Parse(read.Data!).RootElement.GetProperty("version").GetInt64());
    }

    [Fact]
    public async Task Separate_runs_have_isolated_variable_stores()
    {
        var revision = WorkflowRevision.Create(Definition(), "variable-isolation");
        var first = new List<ExecutionEvent>();
        var second = new List<ExecutionEvent>();

        var results = await Task.WhenAll(
            new WorkflowRunner().ExecuteAsync(Guid.NewGuid(), revision, "first", evt => { first.Add(evt); return ValueTask.CompletedTask; }),
            new WorkflowRunner().ExecuteAsync(Guid.NewGuid(), revision, "second", evt => { second.Add(evt); return ValueTask.CompletedTask; }));

        Assert.Equal(new[] { "first", "second" }, results.OrderBy(value => value).ToArray());
        Assert.Equal("first", JsonDocument.Parse(first.Single(evt => evt.Kind == ExecutionEventKinds.VariableRead).Data!).RootElement.GetProperty("value").GetString());
        Assert.Equal("second", JsonDocument.Parse(second.Single(evt => evt.Kind == ExecutionEventKinds.VariableRead).Data!).RootElement.GetProperty("value").GetString());
    }

    [Fact]
    public async Task Cancellation_after_a_committed_write_preserves_the_recorded_variable_prefix()
    {
        var revision = WorkflowRevision.Create(Definition(), "variable-cancellation-prefix");
        using var cancellation = new CancellationTokenSource();
        var events = new List<ExecutionEvent>();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new WorkflowRunner().ExecuteAsync(
            Guid.NewGuid(), revision, "committed", evt =>
            {
                events.Add(evt);
                if (evt.Kind == ExecutionEventKinds.VariableWritten) cancellation.Cancel();
                return ValueTask.CompletedTask;
            }, cancellation.Token));

        var written = events.Single(evt => evt.Kind == ExecutionEventKinds.VariableWritten);
        using var payload = JsonDocument.Parse(written.Data!);
        Assert.Equal("committed", payload.RootElement.GetProperty("value").GetString());
        Assert.True(VariableEvidence.TryReconstructPlayback(events, out var playback));
        Assert.Equal("committed", playback!.Values["message"].Value);
        Assert.Equal(2, playback.Values["message"].Version);
    }

    private static WorkflowDefinition Definition()
    {
        var nodes = new Dictionary<string, NodeDefinition>
        {
            ["input"] = new(NodeTypes.Input, "1.0.0"),
            ["set"] = new(NodeTypes.VariableSet, "1.0.0") { VariableId = "message", InputDataType = WorkflowDataTypes.String },
            ["get"] = new(NodeTypes.VariableGet, "1.0.0") { VariableId = "message", OutputDataType = WorkflowDataTypes.String },
            ["output"] = new(NodeTypes.Output, "1.0.0")
        };
        var data = new Dictionary<string, ConnectionDefinition>
        {
            ["input-value"] = new(new("input", "out"), new("set", "value"), "data"),
            ["get-value"] = new(new("get", "out"), new("output", "in"), "data")
        };
        var execution = new Dictionary<string, ConnectionDefinition>
        {
            ["input-set"] = new(new("input", "then"), new("set", "execute"), "execution"),
            ["set-get"] = new(new("set", "then"), new("get", "execute"), "execution"),
            ["get-output"] = new(new("get", "then"), new("output", "execute"), "execution")
        };
        var definition = new WorkflowDefinition(WorkflowSchemaVersions.MutableVariablesGraph, "mutable-variable-proof", "input",
            nodes.ToImmutableSortedDictionary(), data.ToImmutableSortedDictionary())
        {
            Variables = ImmutableSortedDictionary<string, WorkflowVariableDefinition>.Empty
                .Add("message", new("Message", "default")),
            ExecutionConnections = execution.ToImmutableSortedDictionary()
        };
        return definition;
    }
}
