using System.Collections.Immutable;
using VisualAgentWorkflow.Core;
using Xunit;

namespace VisualAgentWorkflow.AgentFramework.Tests;

public sealed class UnifiedGraphExecutionTests
{
    [Theory]
    [InlineData("yes", false)]
    [InlineData("no", true)]
    public async Task Condition_uses_connected_data_and_merge_continues_the_selected_route(string value, bool terminated)
    {
        var definition = new WorkflowDefinition(WorkflowSchemaVersions.UnifiedWorkflowGraph, "unified-condition", "input",
            new Dictionary<string, NodeDefinition>
            {
                ["input"] = new(NodeTypes.Input, "1.0.0"),
                ["value"] = new(NodeTypes.StringConstant, "1.0.0") { StringConstant = new(value) },
                ["condition"] = new(NodeTypes.Condition, "1.0.0") { Condition = new("contains", "yes") },
                ["merge"] = new(NodeTypes.Merge, "1.0.0"),
                ["terminate"] = new(NodeTypes.Terminate, "1.0.0") { Termination = new(WorkflowTerminationOutcomes.Success) },
                ["output"] = new(NodeTypes.Output, "1.0.0")
            }.ToImmutableSortedDictionary(),
            new Dictionary<string, ConnectionDefinition>
            {
                ["condition-value"] = new(new("value", "out"), new("condition", "in"), "data"),
                ["merge-value"] = new(new("condition", "true"), new("merge", "in"), "data"),
                ["output-value"] = new(new("merge", "out"), new("output", "in"), "data")
            }.ToImmutableSortedDictionary())
        {
            ExecutionConnections = new Dictionary<string, ConnectionDefinition>
            {
                ["start"] = new(new("input", "then"), new("condition", "execute"), "execution"),
                ["true"] = new(new("condition", "true"), new("merge", "execute"), "execution"),
                ["false"] = new(new("condition", "false"), new("terminate", "execute"), "execution"),
                ["complete"] = new(new("merge", "then"), new("output", "execute"), "execution")
            }.ToImmutableSortedDictionary()
        };
        var events = new List<ExecutionEvent>();
        var result = await new WorkflowRunner().ExecuteWithOutcomeAsync(Guid.NewGuid(),
            WorkflowRevision.Create(definition, "test"), "different caller input", null,
            evt => { events.Add(evt); return ValueTask.CompletedTask; });

        Assert.Equal(terminated, result.Terminated);
        Assert.False(result.Failed);
        Assert.Equal(terminated ? null : value, result.Output);
        Assert.Equal(!terminated, events.Any(evt => evt.NodeId == "merge" && evt.Kind == ExecutionEventKinds.NodeStarted));
    }
    [Fact]
    public async Task Agent_consumes_connected_data_and_commits_only_its_selected_route()
    {
        var definition = new WorkflowDefinition(WorkflowSchemaVersions.UnifiedWorkflowGraph, "unified-agent-route", "input",
            new Dictionary<string, NodeDefinition>
            {
                ["input"] = new(NodeTypes.Input, "1.0.0"),
                ["value"] = new(NodeTypes.StringConstant, "1.0.0") { StringConstant = new("connected") },
                ["agent"] = new(NodeTypes.Agent, "1.0.0", Agent: new("fixture", null, "Choose a route", [])
                { Routes = [new("return", "Return", "Return result"), new("stop", "Stop", "Stop execution")] }),
                ["terminate"] = new(NodeTypes.Terminate, "1.0.0") { Termination = new(WorkflowTerminationOutcomes.Success) },
                ["output"] = new(NodeTypes.Output, "1.0.0")
            }.ToImmutableSortedDictionary(),
            new Dictionary<string, ConnectionDefinition>
            {
                ["agent-value"] = new(new("value", "out"), new("agent", "in"), "data"),
                ["output-value"] = new(new("agent", "out"), new("output", "in"), "data")
            }.ToImmutableSortedDictionary())
        {
            ExecutionConnections = new Dictionary<string, ConnectionDefinition>
            {
                ["start"] = new(new("input", "then"), new("agent", "execute"), "execution"),
                ["return"] = new(new("agent", "route.return"), new("output", "execute"), "execution"),
                ["stop"] = new(new("agent", "route.stop"), new("terminate", "execute"), "execution")
            }.ToImmutableSortedDictionary()
        };
        var events = new List<ExecutionEvent>();
        var result = await new WorkflowRunner(agents: new RoutingAgent()).ExecuteWithOutcomeAsync(Guid.NewGuid(),
            WorkflowRevision.Create(definition, "test"), "caller input", null,
            evt => { events.Add(evt); return ValueTask.CompletedTask; });

        Assert.Equal("connected:answer", result.Output);
        Assert.False(result.Failed);
        Assert.DoesNotContain(events, evt => evt.NodeId == "terminate" && evt.Kind == ExecutionEventKinds.NodeStarted);
        Assert.Contains(events, evt => evt.Kind == ExecutionEventKinds.AgentRouteProposed);
    }

    [Theory]
    [InlineData(false, 3, "continue!!!", "maximum-iterations")]
    [InlineData(true, 2, "stop", "predicate-false")]
    public async Task Bounded_agent_loop_without_variables_returns_the_latest_processing_result(
        bool stopEarly, int expectedIterations, string expectedOutput, string expectedStopReason)
    {
        var definition = new WorkflowDefinition(WorkflowSchemaVersions.UnifiedWorkflowGraph, "unified-agent-loop", "input",
            new Dictionary<string, NodeDefinition>
            {
                ["input"] = new(NodeTypes.Input, "1.0.0"),
                ["loop"] = new(NodeTypes.Loop, "1.0.0") { Loop = new(new("contains", "continue"), 3) },
                ["body"] = new(NodeTypes.LoopBody, "1.0.0"),
                ["agent"] = new(NodeTypes.Agent, "1.0.0", Agent: new("fixture", null, "Process input", [])),
                ["output"] = new(NodeTypes.Output, "1.0.0")
            }.ToImmutableSortedDictionary(),
            new Dictionary<string, ConnectionDefinition>
            {
                ["latest-result"] = new(new("agent", "out"), new("output", "in"), "data")
            }.ToImmutableSortedDictionary())
        {
            ExecutionConnections = new Dictionary<string, ConnectionDefinition>
            {
                ["start"] = new(new("input", "then"), new("loop", "execute"), "execution"),
                ["body"] = new(new("loop", "body"), new("body", "execute"), "execution"),
                ["process"] = new(new("body", "then"), new("agent", "execute"), "execution"),
                ["return"] = new(new("agent", "out"), new("loop", "execute"), "execution"),
                ["complete"] = new(new("loop", "done"), new("output", "execute"), "execution")
            }.ToImmutableSortedDictionary()
        };
        var processor = new LoopAgent(stopEarly);
        var events = new List<ExecutionEvent>();
        var revision = WorkflowRevision.Create(definition, "test");
        Assert.Null(revision.Definition.Variables);

        var result = await new WorkflowRunner(agents: processor).ExecuteWithOutcomeAsync(Guid.NewGuid(),
            revision, "continue", null, evt => { events.Add(evt); return ValueTask.CompletedTask; });

        Assert.False(result.Failed);
        Assert.Equal(expectedOutput, result.Output);
        Assert.Equal(new[] { "continue", "continue!", "continue!!" }.Take(expectedIterations), processor.Inputs);
        Assert.Equal(expectedIterations, events.Count(evt => evt.Kind == ExecutionEventKinds.LoopIterationStarted));
        Assert.Equal(expectedIterations, events.Count(evt => evt.Kind == ExecutionEventKinds.LoopIterationCompleted));
        var stopped = Assert.Single(events, evt => evt.Kind == ExecutionEventKinds.LoopStopped);
        using var data = System.Text.Json.JsonDocument.Parse(stopped.Data!);
        Assert.Equal(expectedStopReason, data.RootElement.GetProperty("reason").GetString());
        Assert.Single(events, evt => evt.NodeId == "output" && evt.Kind == ExecutionEventKinds.NodeStarted);
    }

    private sealed class LoopAgent(bool stopEarly) : IAgentNodeExecutor
    {
        public List<string> Inputs { get; } = [];
        public Task<IReadOnlyList<AgentProviderDescriptor>> ListProvidersAsync() =>
            Task.FromResult<IReadOnlyList<AgentProviderDescriptor>>([]);
        public Task<string> ExecuteAsync(NodeInvocationContext invocation, string input,
            CancellationToken cancellationToken = default)
        {
            Inputs.Add(input);
            return Task.FromResult(stopEarly && Inputs.Count == 2 ? "stop" : input + "!");
        }
    }

    private sealed class RoutingAgent : IAgentNodeExecutor
    {
        public Task<IReadOnlyList<AgentProviderDescriptor>> ListProvidersAsync() =>
            Task.FromResult<IReadOnlyList<AgentProviderDescriptor>>([]);
        public async Task<string> ExecuteAsync(NodeInvocationContext invocation, string input,
            CancellationToken cancellationToken = default)
        {
            await invocation.ProposeAgentRoute!("return");
            return input + ":answer";
        }
    }

}
