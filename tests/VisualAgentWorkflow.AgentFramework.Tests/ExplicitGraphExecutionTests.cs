using System.Collections.Immutable;
using System.Text.Json;
using VisualAgentWorkflow.AgentFramework;
using VisualAgentWorkflow.Core;
using Xunit;

namespace VisualAgentWorkflow.AgentFramework.Tests;

public sealed class ExplicitGraphExecutionTests
{
    [Fact]
    public async Task Data_wires_evaluate_pure_values_without_activating_them()
    {
        var definition = new WorkflowDefinition(
            "1.23", "explicit-data", "input",
            new Dictionary<string, NodeDefinition>
            {
                ["input"] = new(NodeTypes.Input, "1.0.0"),
                ["constant"] = new(NodeTypes.StringConstant, "1.0.0") { StringConstant = new("!") },
                ["concat"] = new(NodeTypes.TextConcat, "1.0.0"),
                ["output"] = new(NodeTypes.Output, "1.0.0")
            }.ToImmutableSortedDictionary(StringComparer.Ordinal),
            new Dictionary<string, ConnectionDefinition>
            {
                ["input-value"] = new(new("input", "out"), new("concat", "left"), "data"),
                ["suffix"] = new(new("constant", "out"), new("concat", "right"), "data"),
                ["result"] = new(new("concat", "out"), new("output", "in"), "data")
            }.ToImmutableSortedDictionary(StringComparer.Ordinal))
        {
            ExecutionConnections = new Dictionary<string, ConnectionDefinition>
            {
                ["activate-output"] = new(new("input", "then"), new("output", "execute"), "execution")
            }.ToImmutableSortedDictionary(StringComparer.Ordinal)
        };
        var events = new List<ExecutionEvent>();

        var result = await new PipelineRunner().ExecuteWithOutcomeAsync(Guid.NewGuid(),
            WorkflowRevision.Create(definition, "test"), "Hello",
            parameters: null,
            evt => { events.Add(evt); return ValueTask.CompletedTask; });

        Assert.Equal("Hello!", result.Output);
        Assert.False(result.Terminated);
        Assert.Single(events, evt => evt.Kind == ExecutionEventKinds.ExecutionActivated && evt.ConnectionId == "activate-output");
        Assert.DoesNotContain(events, evt => evt.Kind == ExecutionEventKinds.Transfer);
        Assert.DoesNotContain(events, evt => evt.Kind == ExecutionEventKinds.NodeStarted && evt.NodeId is "constant" or "concat");
        Assert.Equal(3, events.Count(evt => evt.Kind == ExecutionEventKinds.DataEvaluated));
        var output = Assert.Single(events, evt => evt.Kind == ExecutionEventKinds.Completed);
        Assert.Equal("Hello!", output.Data);

        var values = events.Where(evt => evt.Kind == ExecutionEventKinds.DataEvaluated)
            .Select(evt => JsonDocument.Parse(evt.Data!).RootElement.GetProperty("value").GetString())
            .ToArray();
        Assert.Equal(new[] { "Hello", "!", "Hello!" }, values.Select(value => value!));
    }
}
