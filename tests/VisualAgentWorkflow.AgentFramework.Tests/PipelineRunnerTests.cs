using System.Collections.Immutable;
using VisualAgentWorkflow.Core;
using Xunit;

namespace VisualAgentWorkflow.AgentFramework.Tests;

public class PipelineRunnerTests
{
    private static WorkflowRevision Revision() => WorkflowRevision.Create(new("1.0", "demo", "input",
        new Dictionary<string, NodeDefinition>
        {
            ["input"] = new(NodeTypes.Input, "1.0.0"),
            ["uppercase"] = new(NodeTypes.Uppercase, "1.0.0"),
            ["reverse"] = new(NodeTypes.Reverse, "1.0.0"),
            ["output"] = new(NodeTypes.Output, "1.0.0")
        }.ToImmutableSortedDictionary(),
        new Dictionary<string, ConnectionDefinition>
        {
            ["a"] = new(new("input", "out"), new("uppercase", "in"), "data"),
            ["b"] = new(new("uppercase", "out"), new("reverse", "in"), "data"),
            ["c"] = new(new("reverse", "out"), new("output", "in"), "data")
        }.ToImmutableSortedDictionary()), "test");

    [Fact]
    public async Task ExecutesUsingFrameworkAndRecordsActualTransfers()
    {
        var revision = Revision();
        var runId = Guid.NewGuid();
        var events = new List<ExecutionEvent>();
        var output = await new PipelineRunner().ExecuteAsync(runId, revision, "Hello 🌍", e => { events.Add(e); return ValueTask.CompletedTask; });
        Assert.Equal("🌍 OLLEH", output);
        Assert.Equal(ExecutionEventKinds.Started, events[0].Kind);
        Assert.Equal(ExecutionEventKinds.Completed, events[^1].Kind);
        Assert.Equal(Enumerable.Range(1, events.Count).Select(i => (long)i), events.Select(e => e.Sequence));
        Assert.All(events, e => { Assert.Equal(runId, e.RunId); Assert.Equal(revision.RevisionId, e.RevisionId); });
        Assert.Equal(4, events.Count(e => e.Kind == ExecutionEventKinds.NodeStarted));
        Assert.Equal(4, events.Where(e => e.Kind == ExecutionEventKinds.NodeStarted).Select(e => e.InvocationId).Distinct().Count());
        var transfers = events.Where(e => e.Kind == ExecutionEventKinds.Transfer).ToArray();
        Assert.Equal(new[] { "a", "b", "c" }, transfers.Select(e => e.ConnectionId));
        Assert.Equal(new[] { "Hello 🌍", "HELLO 🌍", "🌍 OLLEH" }, transfers.Select(e => e.Data));
    }

    [Fact]
    public async Task WorkflowValueNodePassesDefaultOrRunOverrideAsRecordedGraphData()
    {
        var definition = new WorkflowDefinition("1.6", "parameter-runtime", "input",
            new Dictionary<string, NodeDefinition>
            {
                ["input"] = new(NodeTypes.Input, "1.0.0"),
                ["parameter"] = new(NodeTypes.WorkflowValue, "1.0.0") { ParameterId = "message" },
                ["output"] = new(NodeTypes.Output, "1.0.0")
            }.ToImmutableSortedDictionary(),
            new Dictionary<string, ConnectionDefinition>
            {
                ["a"] = new(new("input", "out"), new("parameter", "in"), "data"),
                ["b"] = new(new("parameter", "out"), new("output", "in"), "data")
            }.ToImmutableSortedDictionary())
        {
            Parameters = ImmutableSortedDictionary<string, WorkflowParameterDefinition>.Empty.Add("message", new("Message", "default"))
        };
        var revision = WorkflowRevision.Create(definition, "test");
        var events = new List<ExecutionEvent>();

        var defaultOutput = await new PipelineRunner().ExecuteAsync(Guid.NewGuid(), revision, "ignored",
            evt => { events.Add(evt); return ValueTask.CompletedTask; });
        Assert.Equal("default", defaultOutput);
        events.Clear();

        var output = await new PipelineRunner().ExecuteWithParametersAsync(Guid.NewGuid(), revision, "ignored",
            new Dictionary<string, string> { ["message"] = "override" }, evt => { events.Add(evt); return ValueTask.CompletedTask; });

        Assert.Equal("override", output);
        Assert.Contains(events, evt => evt.Kind == ExecutionEventKinds.NodeCompleted && evt.NodeId == "parameter" && evt.Data == "override");
        Assert.Contains(events, evt => evt.Kind == ExecutionEventKinds.Transfer && evt.ConnectionId == "b" && evt.Data == "override");
    }

    [Fact]
    public async Task TextTemplateNodeRendersInputAndResolvedParametersAsRecordedData()
    {
        var definition = new WorkflowDefinition("1.7", "text-template-runtime", "input",
            new Dictionary<string, NodeDefinition>
            {
                ["input"] = new(NodeTypes.Input, "1.0.0"),
                ["format"] = new(NodeTypes.TextTemplate, "1.0.0")
                {
                    TextTemplate = new([new("literal", "Tell "), new("input", ""), new("literal", " to greet "), new("parameter", "name")])
                },
                ["output"] = new(NodeTypes.Output, "1.0.0")
            }.ToImmutableSortedDictionary(),
            new Dictionary<string, ConnectionDefinition>
            {
                ["a"] = new(new("input", "out"), new("format", "in"), "data"),
                ["b"] = new(new("format", "out"), new("output", "in"), "data")
            }.ToImmutableSortedDictionary())
        {
            Parameters = ImmutableSortedDictionary<string, WorkflowParameterDefinition>.Empty.Add("name", new("Name", "Ada"))
        };
        var events = new List<ExecutionEvent>();

        var result = await new PipelineRunner().ExecuteWithParametersAsync(Guid.NewGuid(), WorkflowRevision.Create(definition, "test"),
            "answer the question", new Dictionary<string, string> { ["name"] = "Grace" },
            evt => { events.Add(evt); return ValueTask.CompletedTask; });

        Assert.Equal("Tell answer the question to greet Grace", result);
        Assert.Contains(events, evt => evt.Kind == ExecutionEventKinds.NodeCompleted && evt.NodeId == "format" && evt.Data == result);
        Assert.Contains(events, evt => evt.Kind == ExecutionEventKinds.Transfer && evt.ConnectionId == "b" && evt.Data == result);
    }

    [Theory]
    [InlineData("{\"user\":{\"name\":\"Ada\"},\"items\":[{\"id\":7}]}", "/user/name", "Ada")]
    [InlineData("{\"items\":[{\"id\":7}]}", "/items/0/id", "7")]
    [InlineData("{\"name\":\"Ada\",\"age\":3}", "", "{\"name\":\"Ada\",\"age\":3}")]
    public async Task JsonInputAndPointerSelectorExecuteAndRecordSelectedString(string input, string pointer, string expected)
    {
        var definition = new WorkflowDefinition("1.9", "json-run", "input",
            new Dictionary<string, NodeDefinition>
            {
                ["input"] = new(NodeTypes.Input, "1.0.0") { InputDataType = "json" },
                ["select"] = new(NodeTypes.JsonSelectString, "1.0.0") { JsonPropertySelection = new(pointer) },
                ["output"] = new(NodeTypes.Output, "1.0.0")
            }.ToImmutableSortedDictionary(),
            new Dictionary<string, ConnectionDefinition>
            {
                ["json"] = new(new("input", "out"), new("select", "in"), "data"),
                ["text"] = new(new("select", "out"), new("output", "in"), "data")
            }.ToImmutableSortedDictionary());
        var revision = WorkflowRevision.Create(definition, "test");
        var events = new List<ExecutionEvent>();

        var result = await new PipelineRunner().ExecuteAsync(Guid.NewGuid(), revision, input,
            evt => { events.Add(evt); return ValueTask.CompletedTask; });

        Assert.Equal(expected, result);
        Assert.Contains(events, evt => evt.Kind == ExecutionEventKinds.NodeCompleted && evt.NodeId == "select" && evt.Data == expected);
        Assert.Contains(events, evt => evt.Kind == ExecutionEventKinds.Transfer && evt.ConnectionId == "text" && evt.Data == expected);
    }

    [Fact]
    public async Task JsonProjectionBuildsBoundedTypedObjectAndRecordsProjectedValue()
    {
        const string expected = "{\"person\":\"Ada\",\"amount\":42}";
        var definition = new WorkflowDefinition("1.13", "json-project-run", "input",
            new Dictionary<string, NodeDefinition>
            {
                ["input"] = new(NodeTypes.Input, "1.0.0") { InputDataType = "json" },
                ["project"] = new(NodeTypes.JsonProject, "1.0.0")
                {
                    JsonProjection = new([new("person", "/customer/name"), new("amount", "/orders/0/amount")])
                },
                ["output"] = new(NodeTypes.Output, "1.0.0") { OutputDataType = "json" }
            }.ToImmutableSortedDictionary(),
            new Dictionary<string, ConnectionDefinition>
            {
                ["json"] = new(new("input", "out"), new("project", "in"), "data"),
                ["result"] = new(new("project", "out"), new("output", "in"), "data")
            }.ToImmutableSortedDictionary());
        var events = new List<ExecutionEvent>();

        var result = await new PipelineRunner().ExecuteAsync(Guid.NewGuid(), WorkflowRevision.Create(definition, "test"),
            "{\"customer\":{\"name\":\"Ada\"},\"orders\":[{\"amount\":42,\"secret\":\"omit\"}]}",
            evt => { events.Add(evt); return ValueTask.CompletedTask; });

        Assert.Equal(expected, result);
        Assert.Contains(events, evt => evt.Kind == ExecutionEventKinds.NodeCompleted && evt.NodeId == "project" && evt.Data == expected);
        Assert.Contains(events, evt => evt.Kind == ExecutionEventKinds.Transfer && evt.ConnectionId == "result" && evt.Data == expected);
        Assert.DoesNotContain("secret", events.Single(evt => evt.Kind == ExecutionEventKinds.NodeCompleted && evt.NodeId == "project").Data);
    }

    [Fact]
    public async Task JsonProjectionRejectsOutputBeyondPayloadLimitAndRecordsFailure()
    {
        var definition = new WorkflowDefinition("1.13", "json-project-limit", "input",
            new Dictionary<string, NodeDefinition>
            {
                ["input"] = new(NodeTypes.Input, "1.0.0") { InputDataType = "json" },
                ["project"] = new(NodeTypes.JsonProject, "1.0.0")
                {
                    JsonProjection = new([new(new string('x', JsonProjectionLimits.MaximumNameCharacters), "/value")])
                },
                ["output"] = new(NodeTypes.Output, "1.0.0") { OutputDataType = "json" }
            }.ToImmutableSortedDictionary(),
            new Dictionary<string, ConnectionDefinition>
            {
                ["json"] = new(new("input", "out"), new("project", "in"), "data"),
                ["result"] = new(new("project", "out"), new("output", "in"), "data")
            }.ToImmutableSortedDictionary());
        var events = new List<ExecutionEvent>();
        var input = "{\"value\":\"" + new string('x', 16_260) + "\"}";

        await Assert.ThrowsAsync<InvalidOperationException>(() => new PipelineRunner().ExecuteAsync(Guid.NewGuid(),
            WorkflowRevision.Create(definition, "test"), input,
            evt => { events.Add(evt); return ValueTask.CompletedTask; }));

        Assert.Contains(events, evt => evt.Kind == ExecutionEventKinds.NodeFailed && evt.NodeId == "project");
        Assert.Equal(ExecutionEventKinds.Failed, events[^1].Kind);
    }

    [Fact]
    public async Task TypedJsonProjectionChecksMappedValueKindsBeforeEmittingOutput()
    {
        var definition = new WorkflowDefinition("1.15", "json-projection-types", "input",
            new Dictionary<string, NodeDefinition>
            {
                ["input"] = new(NodeTypes.Input, "1.0.0") { InputDataType = "json" },
                ["project"] = new(NodeTypes.JsonProject, "1.0.0")
                {
                    JsonProjection = new([new JsonProjectionField("name", "/name") { ValueType = "string" }, new JsonProjectionField("items", "/items") { ValueType = "array" }])
                },
                ["output"] = new(NodeTypes.Output, "1.0.0") { OutputDataType = "json" }
            }.ToImmutableSortedDictionary(),
            new Dictionary<string, ConnectionDefinition>
            {
                ["json"] = new(new("input", "out"), new("project", "in"), "data"),
                ["result"] = new(new("project", "out"), new("output", "in"), "data")
            }.ToImmutableSortedDictionary());
        var events = new List<ExecutionEvent>();

        var output = await new PipelineRunner().ExecuteAsync(Guid.NewGuid(), WorkflowRevision.Create(definition, "test"),
            "{\"name\":\"Ada\",\"items\":[1,2]}", evt => { events.Add(evt); return ValueTask.CompletedTask; });

        Assert.Equal("{\"name\":\"Ada\",\"items\":[1,2]}", output);
        Assert.Contains(events, evt => evt.Kind == ExecutionEventKinds.NodeCompleted && evt.NodeId == "project" && evt.Data == output);

        events.Clear();
        await Assert.ThrowsAsync<InvalidOperationException>(() => new PipelineRunner().ExecuteAsync(Guid.NewGuid(),
            WorkflowRevision.Create(definition, "test"), "{\"name\":42,\"items\":[]}",
            evt => { events.Add(evt); return ValueTask.CompletedTask; }));

        Assert.Contains(events, evt => evt.Kind == ExecutionEventKinds.NodeFailed && evt.NodeId == "project" && evt.Data!.Contains("expected string but received number", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RecursiveJsonProjectionMapsNestedObjectsAndArrayItemsWithPlaybackEvidence()
    {
        var schema = new JsonValueSchema("object")
        {
            Properties = System.Collections.Immutable.ImmutableArray.Create(
                new JsonValueProperty("displayName", "/fullName", new("string")),
                new JsonValueProperty("active", "/metadata/active", new("boolean")) { Optional = true },
                new JsonValueProperty("orders", "/orders", new("array")
                {
                    Items = new("object")
                    {
                        Properties = System.Collections.Immutable.ImmutableArray.Create(
                            new JsonValueProperty("orderId", "/id", new("integer")),
                            new JsonValueProperty("total", "/amount", new("number")))
                    }
                }))
        };
        var definition = new WorkflowDefinition("1.16", "recursive-json-project", "input",
            new Dictionary<string, NodeDefinition>
            {
                ["input"] = new(NodeTypes.Input, "1.0.0") { InputDataType = "json" },
                ["project"] = new(NodeTypes.JsonProject, "1.0.0")
                {
                    JsonProjection = new([new JsonProjectionField("customer", "/customer") { ValueSchema = schema }])
                },
                ["output"] = new(NodeTypes.Output, "1.0.0") { OutputDataType = "json" }
            }.ToImmutableSortedDictionary(),
            new Dictionary<string, ConnectionDefinition>
            {
                ["json"] = new(new("input", "out"), new("project", "in"), "data"),
                ["result"] = new(new("project", "out"), new("output", "in"), "data")
            }.ToImmutableSortedDictionary());
        var events = new List<ExecutionEvent>();

        var output = await new PipelineRunner().ExecuteAsync(Guid.NewGuid(), WorkflowRevision.Create(definition, "test"),
            "{\"customer\":{\"fullName\":\"Ada\",\"metadata\":{\"active\":true},\"secret\":\"drop\",\"orders\":[{\"id\":7,\"amount\":12.5}]}}",
            evt => { events.Add(evt); return ValueTask.CompletedTask; });

        Assert.Equal("{\"customer\":{\"displayName\":\"Ada\",\"active\":true,\"orders\":[{\"orderId\":7,\"total\":12.5}]}}", output);
        Assert.Contains(events, evt => evt.Kind == ExecutionEventKinds.NodeCompleted && evt.NodeId == "project" && evt.Data == output);
        Assert.Contains(events, evt => evt.Kind == ExecutionEventKinds.Transfer && evt.ConnectionId == "result" && evt.Data == output);
        Assert.DoesNotContain("secret", output);

        events.Clear();
        await Assert.ThrowsAsync<InvalidOperationException>(() => new PipelineRunner().ExecuteAsync(Guid.NewGuid(),
            WorkflowRevision.Create(definition, "test"), "{\"customer\":{\"metadata\":{},\"orders\":[]}}",
            evt => { events.Add(evt); return ValueTask.CompletedTask; }));
        Assert.Contains(events, evt => evt.Kind == ExecutionEventKinds.NodeFailed && evt.Data!.Contains("Required mapped property 'customer.displayName' was not found.", StringComparison.Ordinal));

        var tooManyItems = "{\"customer\":{\"fullName\":\"Ada\",\"orders\":[" + string.Join(',', Enumerable.Repeat("{}", JsonProjectionLimits.MaximumArrayItems + 1)) + "]}}";
        events.Clear();
        await Assert.ThrowsAsync<InvalidOperationException>(() => new PipelineRunner().ExecuteAsync(Guid.NewGuid(),
            WorkflowRevision.Create(definition, "test"), tooManyItems,
            evt => { events.Add(evt); return ValueTask.CompletedTask; }));
        Assert.Contains(events, evt => evt.Kind == ExecutionEventKinds.NodeFailed && evt.Data!.Contains("exceeds 64 items", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ExpandedPublishedPipelineExecutesWithoutSourceCatalogOrRuntimePrefabLookup()
    {
        var source = Revision();
        var composition = PipelineReuse.Insert(source.Definition, WorkflowLayout.Create(source.Definition), "input",
            new(1, source), WorkflowLayout.Create(source.Definition));
        var revision = WorkflowRevision.Create(composition.Definition, "consumer");
        var events = new List<ExecutionEvent>();
        var output = await new PipelineRunner().ExecuteAsync(Guid.NewGuid(), revision, "abc", e => { events.Add(e); return ValueTask.CompletedTask; });
        Assert.Equal("ABC", output); // uppercase/reverse from the copy, followed by uppercase/reverse from the target.
        Assert.Equal(6, events.Count(e => e.Kind == ExecutionEventKinds.NodeStarted));
        Assert.Equal(5, events.Count(e => e.Kind == ExecutionEventKinds.Transfer));
        Assert.Equal(ExecutionEventKinds.Completed, events[^1].Kind);
    }

    [Fact]
    public async Task CancellationRecordsTerminalOutcomeWithoutNodeExecution()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var events = new List<ExecutionEvent>();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new PipelineRunner().ExecuteAsync(
            Guid.NewGuid(), Revision(), "input", e => { events.Add(e); return ValueTask.CompletedTask; }, cancellation.Token));
        Assert.Equal(new[] { ExecutionEventKinds.Started, ExecutionEventKinds.Cancelled }, events.Select(e => e.Kind));
    }

    [Fact]
    public async Task InvalidDefinitionNeverStartsAFrameworkRun()
    {
        var events = new List<ExecutionEvent>();
        await Assert.ThrowsAsync<ArgumentException>(() => new PipelineRunner().ExecuteAsync(
            Guid.NewGuid(), Revision(), new string('x', 16_385), e => { events.Add(e); return ValueTask.CompletedTask; }));
        Assert.Empty(events);
    }
    [Fact]
    public async Task CompletionBoundaryWaitsWithoutRepeatingFinalNode()
    {
        var gate = new CompletionGate();
        var events = new List<ExecutionEvent>();
        var execution = new PipelineRunner().ExecuteControlledAsync(Guid.NewGuid(), Revision(), "abc",
            evt => { events.Add(evt); return ValueTask.CompletedTask; }, gate);
        await gate.Reached.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(4, events.Count(e => e.Kind == ExecutionEventKinds.NodeCompleted));
        Assert.DoesNotContain(events, e => e.Kind == ExecutionEventKinds.Completed);
        gate.Release.TrySetResult();
        Assert.Equal("CBA", await execution.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(4, events.Count(e => e.Kind == ExecutionEventKinds.NodeStarted));
        Assert.Equal(ExecutionEventKinds.Completed, events[^1].Kind);
    }

    private sealed class CompletionGate : IWorkflowExecutionGate
    {
        public TaskCompletionSource Reached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async ValueTask WaitForBoundaryAsync(string? nextNodeId, CancellationToken cancellationToken)
        {
            if (nextNodeId is not null) return;
            Reached.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
        }
    }

}
