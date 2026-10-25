using System.Collections.Immutable;
using System.Text.Json;
using VisualAgentWorkflow.AgentFramework;
using VisualAgentWorkflow.Core;
using Xunit;

namespace VisualAgentWorkflow.AgentFramework.Tests;

public sealed class ExplicitGraphExecutionTests
{
    [Theory]
    [InlineData("yes", "true", "yes", false)]
    [InlineData("no", "false", null, true)]
    public async Task Boolean_branch_records_the_selected_route_and_only_evaluates_reached_output(
        string input, string expectedRoute, string? expectedOutput, bool failed)
    {
        var definition = BranchDefinition();
        WorkflowValidator.ThrowIfInvalid(definition);
        var events = new List<ExecutionEvent>();

        var result = await new PipelineRunner().ExecuteWithOutcomeAsync(Guid.NewGuid(),
            WorkflowRevision.Create(definition, "test"), input, null,
            evt => { events.Add(evt); return ValueTask.CompletedTask; });

        var branchDecision = Assert.Single(events, evt => evt.Kind == ExecutionEventKinds.BranchSelected && evt.NodeId == "branch");
        Assert.Contains($"\"port\":\"{expectedRoute}\"", branchDecision.Data, StringComparison.Ordinal);
        var activatedRoute = Assert.Single(events, evt => evt.Kind == ExecutionEventKinds.ExecutionActivated &&
            evt.ConnectionId == $"execution:{expectedRoute}-route");
        Assert.Equal(expectedRoute == "true" ? "output" : "terminate", activatedRoute.NodeId);
        var comparison = Assert.Single(events, evt => evt.Kind == ExecutionEventKinds.DataEvaluated && evt.NodeId == "equals");
        using (var comparisonData = JsonDocument.Parse(comparison.Data!))
        {
            Assert.Equal("boolean", comparisonData.RootElement.GetProperty("dataType").GetString());
            Assert.Equal(expectedRoute == "true", comparisonData.RootElement.GetProperty("value").GetBoolean());
        }
        Assert.Equal(failed, result.Failed);
        Assert.True(result.Terminated == failed);
        Assert.Equal(expectedOutput, result.Output);
        Assert.Equal(expectedRoute == "true", events.Any(evt => evt.Kind == ExecutionEventKinds.NodeStarted && evt.NodeId == "output"));
        var outputEvaluations = events.Where(evt => evt.Kind == ExecutionEventKinds.DataEvaluated).Where(evt =>
        {
            using var data = JsonDocument.Parse(evt.Data!);
            return data.RootElement.GetProperty("activatedByNodeId").GetString() == "output";
        });
        Assert.Equal(expectedRoute == "true", outputEvaluations.Any());
    }

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
        Assert.Single(events, evt => evt.Kind == ExecutionEventKinds.ExecutionActivated && evt.ConnectionId == "execution:activate-output");
        Assert.DoesNotContain(events, evt => evt.Kind == ExecutionEventKinds.Transfer);
        Assert.DoesNotContain(events, evt => evt.Kind == ExecutionEventKinds.NodeStarted && evt.NodeId is "constant" or "concat");
        Assert.Equal(3, events.Count(evt => evt.Kind == ExecutionEventKinds.DataEvaluated));
        var output = Assert.Single(events, evt => evt.Kind == ExecutionEventKinds.Completed);
        Assert.Equal("Hello!", output.Data);

        var values = events.Where(evt => evt.Kind == ExecutionEventKinds.DataEvaluated)
            .Select(evt => JsonDocument.Parse(evt.Data!).RootElement.GetProperty("value").GetString())
            .ToArray();
        Assert.Equal(new[] { "Hello", "!", "Hello!" }, values.Select(value => value!));
        Assert.Equal(new[] { "data:input-value", "data:result", "data:suffix" }, events
            .Where(evt => evt.Kind == ExecutionEventKinds.DataEvaluated)
            .Select(evt => evt.ConnectionId)
            .Order(StringComparer.Ordinal));
    }

    private static WorkflowDefinition BranchDefinition() => new(
        "1.24", "boolean-branch", "input",
        new Dictionary<string, NodeDefinition>
        {
            ["input"] = new(NodeTypes.Input, "1.0.0"),
            ["expected"] = new(NodeTypes.StringConstant, "1.0.0") { StringConstant = new("yes") },
            ["equals"] = new(NodeTypes.StringEquals, "1.0.0"),
            ["branch"] = new(NodeTypes.BooleanBranch, "1.0.0"),
            ["output"] = new(NodeTypes.Output, "1.0.0"),
            ["terminate"] = new(NodeTypes.Terminate, "1.0.0") { Termination = new(WorkflowTerminationOutcomes.Failure) }
        }.ToImmutableSortedDictionary(StringComparer.Ordinal),
        new Dictionary<string, ConnectionDefinition>
        {
            ["input-left"] = new(new("input", "out"), new("equals", "left"), "data"),
            ["expected-right"] = new(new("expected", "out"), new("equals", "right"), "data"),
            ["comparison"] = new(new("equals", "out"), new("branch", "condition"), "data"),
            ["output-value"] = new(new("input", "out"), new("output", "in"), "data")
        }.ToImmutableSortedDictionary(StringComparer.Ordinal))
    {
        ExecutionConnections = new Dictionary<string, ConnectionDefinition>
        {
            ["activate-branch"] = new(new("input", "then"), new("branch", "execute"), "execution"),
            ["true-route"] = new(new("branch", "true"), new("output", "execute"), "execution"),
            ["false-route"] = new(new("branch", "false"), new("terminate", "execute"), "execution")
        }.ToImmutableSortedDictionary(StringComparer.Ordinal)
    };
}
