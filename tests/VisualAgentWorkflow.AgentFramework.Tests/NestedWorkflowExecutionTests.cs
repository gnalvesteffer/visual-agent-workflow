using System.Collections.Immutable;
using VisualAgentWorkflow.Core;
using Xunit;

namespace VisualAgentWorkflow.AgentFramework.Tests;

public sealed class NestedWorkflowExecutionTests
{
    private static WorkflowDefinition Baseline(string workflowId, string schema = "1.0") => new(schema, workflowId, "input",
        new Dictionary<string, NodeDefinition>
        {
            ["input"] = new(NodeTypes.Input, "1.0.0"),
            ["output"] = new(NodeTypes.Output, "1.0.0")
        }.ToImmutableSortedDictionary(StringComparer.Ordinal),
        new Dictionary<string, ConnectionDefinition>
        {
            ["edge"] = new(new("input", "out"), new("output", "in"), "data")
        }.ToImmutableSortedDictionary(StringComparer.Ordinal));

    private static WorkflowDefinition WithCall(WorkflowDefinition parent, WorkflowRevision child)
    {
        var call = new NodeDefinition(NodeTypes.WorkflowCall, "1.0.0")
        {
            WorkflowCall = new(child.Definition.WorkflowId, child.RevisionId, 1, child.ContentHash, child.Definition)
        };
        return GraphEditing.InsertNodeAfter(parent, "input", "out", call);
    }

    [Fact]
    public async Task CallsPinnedChildAndRecordsItsEventsInsideOneRunScope()
    {
        var childDefinition = WorkflowEditing.InsertAfter(Baseline("child"), "input", NodeTypes.Uppercase);
        var child = WorkflowRevision.Create(childDefinition, "test");
        var parent = WithCall(Baseline("parent"), child);
        var revision = WorkflowRevision.Create(parent, "test");
        var events = new List<ExecutionEvent>();
        var runId = Guid.NewGuid();

        var output = await new WorkflowRunner().ExecuteAsync(runId, revision, "hello",
            evt => { events.Add(evt); return ValueTask.CompletedTask; });

        Assert.Equal("HELLO", output);
        Assert.Equal(events.Select(evt => evt.Sequence).Order().ToArray(), events.Select(evt => evt.Sequence).ToArray());
        Assert.Equal(Enumerable.Range(1, events.Count).Select(value => (long)value), events.Select(evt => evt.Sequence));
        Assert.All(events, evt => { Assert.Equal(runId, evt.RunId); Assert.Equal(revision.RevisionId, evt.RevisionId); });
        var callId = parent.Nodes.Single(entry => entry.Value.Type == NodeTypes.WorkflowCall).Key;
        var childEvents = events.Where(evt => evt.ScopeId == callId).ToArray();
        Assert.Contains(childEvents, evt => evt.Kind == ExecutionEventKinds.SubworkflowStarted);
        Assert.Contains(childEvents, evt => evt.Kind == ExecutionEventKinds.SubworkflowCompleted && evt.Data == "HELLO");
        Assert.Contains(childEvents, evt => evt.Kind == ExecutionEventKinds.NodeStarted && evt.NodeId == "input");
        Assert.Contains(events, evt => evt.Kind == ExecutionEventKinds.Completed && evt.ScopeId is null);
        Assert.DoesNotContain(events, evt => evt.Kind == ExecutionEventKinds.Completed && evt.ScopeId is not null);
    }

    [Fact]
    public async Task ChildSuccessfulTerminationTerminatesTheParentRoute()
    {
        var childDefinition = GraphEditing.InsertTerminationAfter(Baseline("child"), "input", "out");
        var child = WorkflowRevision.Create(childDefinition, "test");
        var parent = WithCall(Baseline("parent"), child);
        var events = new List<ExecutionEvent>();

        var result = await new WorkflowRunner().ExecuteWithOutcomeAsync(Guid.NewGuid(), WorkflowRevision.Create(parent, "test"), "skip",
            null, evt => { events.Add(evt); return ValueTask.CompletedTask; });

        Assert.True(result.Terminated);
        Assert.Null(result.Output);
        Assert.Contains(events, evt => evt.Kind == ExecutionEventKinds.SubworkflowTerminated && evt.ScopeId is not null);
        Assert.Contains(events, evt => evt.Kind == ExecutionEventKinds.Terminated && evt.ScopeId is null);
        Assert.DoesNotContain(events, evt => evt.NodeId == "output" && evt.ScopeId is null && evt.Kind == ExecutionEventKinds.NodeCompleted);
    }

    [Fact]
    public async Task ParentParameterOverridesAreMappedIntoPinnedChildParameters()
    {
        var childDefinition = WorkflowParameterEditing.Add(Baseline("child"), "tone", "Tone", "child default");
        childDefinition = GraphEditing.InsertNodeAfter(childDefinition, "input", "out",
            new NodeDefinition(NodeTypes.WorkflowValue, "1.0.0") { ParameterId = "tone" });
        var child = WorkflowRevision.Create(childDefinition, "test");
        var parentDefinition = WorkflowParameterEditing.Add(Baseline("parent"), "style", "Style", "parent default");
        var call = new NodeDefinition(NodeTypes.WorkflowCall, "1.0.0")
        {
            WorkflowCall = new(child.Definition.WorkflowId, child.RevisionId, 1, child.ContentHash, child.Definition)
            { ParameterMappings = [new("tone", "style")] }
        };
        var parent = GraphEditing.InsertNodeAfter(parentDefinition, "input", "out", call);
        var revision = WorkflowRevision.Create(parent, "test");

        var output = await new WorkflowRunner().ExecuteWithParametersAsync(Guid.NewGuid(), revision, "ignored",
            new Dictionary<string, string> { ["style"] = "mapped value" }, _ => ValueTask.CompletedTask);

        Assert.Equal("mapped value", output);
    }

    [Fact]
    public async Task WorkflowCallPassesJsonBetweenMatchingTypedChildBoundaries()
    {
        static WorkflowDefinition JsonBoundary(WorkflowDefinition definition) => definition with
        {
            SchemaVersion = "1.19",
            Nodes = definition.Nodes
                .SetItem("input", definition.Nodes["input"] with { InputDataType = "json" })
                .SetItem("output", definition.Nodes["output"] with { OutputDataType = "json" })
        };

        var child = WorkflowRevision.Create(JsonBoundary(Baseline("json-child")), "test");
        var call = new NodeDefinition(NodeTypes.WorkflowCall, "1.0.0")
        {
            WorkflowCall = new(child.Definition.WorkflowId, child.RevisionId, 1, child.ContentHash, child.Definition)
        };
        var parent = GraphEditing.InsertNodeAfter(JsonBoundary(Baseline("json-parent")), "input", "out", call);

        var output = await new WorkflowRunner().ExecuteAsync(Guid.NewGuid(), WorkflowRevision.Create(parent, "test"),
            "{ \"answer\": 42 }", _ => ValueTask.CompletedTask);

        Assert.Equal("{\"answer\":42}", output);
    }
}
