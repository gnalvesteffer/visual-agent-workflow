using System.Collections.Immutable;
using System.Text.Json;
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

    [Fact]
    public async Task InterfaceFieldAdapterRunsExactPinnedJsonWorkflowOnceAndProjectsMultipleOutputs()
    {
        var operation = new WorkflowInterfaceOperation("Lookup", WorkflowDataTypes.Json, WorkflowDataTypes.Json)
        {
            Inputs =
            [
                new("query", "Query", WorkflowDataTypes.Json),
                new("includeArchived", "Include archived", WorkflowDataTypes.Boolean),
                new("limit", "Limit", WorkflowDataTypes.Integer)
            ],
            Outputs =
            [
                new("query", "Query", WorkflowDataTypes.Json),
                new("includeArchived", "Include archived", WorkflowDataTypes.Boolean),
                new("limit", "Limit", WorkflowDataTypes.Integer)
            ]
        };
        var interfaceDefinition = new WorkflowInterfaceDefinition("lookup.api", "Lookup", "Lookup operation.",
            ImmutableSortedDictionary<string, WorkflowInterfaceOperation>.Empty.Add("lookup", operation))
        { SchemaVersion = WorkflowInterfaceJson.FieldContractSchemaVersion };
        var interfaceRevision = WorkflowInterfaceRevision.Create(interfaceDefinition);
        var childDefinition = Baseline("lookup-child", "1.19") with
        {
            Nodes = Baseline("lookup-child", "1.19").Nodes
                .SetItem("input", Baseline("lookup-child", "1.19").Nodes["input"] with { InputDataType = WorkflowDataTypes.Json })
                .SetItem("output", Baseline("lookup-child", "1.19").Nodes["output"] with { OutputDataType = WorkflowDataTypes.Json })
        };
        var childRevision = WorkflowRevision.Create(childDefinition, "proof");
        var childPin = WorkflowInterfaceWorkflowPin.From(new PublishedWorkflowRevision(1, childRevision));
        var implementation = WorkflowInterfaceImplementationRevision.Create(
            new WorkflowInterfaceImplementation("lookup.impl", interfaceRevision,
                ImmutableSortedDictionary<string, WorkflowInterfaceWorkflowPin>.Empty.Add("lookup", childPin)));
        var resolved = WorkflowInterfaceResolver.ResolveOperation(implementation, "lookup");
        var inputs = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
        {
            ["query"] = JsonDocument.Parse("null").RootElement.Clone(),
            ["includeArchived"] = JsonDocument.Parse("false").RootElement.Clone(),
            ["limit"] = JsonDocument.Parse("7").RootElement.Clone()
        };
        var packedInput = WorkflowInterfaceFields.PackInputs(resolved.Signature, inputs);
        var call = new NodeDefinition(NodeTypes.WorkflowCall, "1.0.0")
        {
            WorkflowCall = new(resolved.Workflow.WorkflowId, resolved.Workflow.RevisionId,
                resolved.Workflow.Version, resolved.Workflow.ContentHash, resolved.Workflow.Definition)
        };
        var parent = GraphEditing.InsertNodeAfter(Baseline("lookup-parent", "1.19") with
        {
            Nodes = Baseline("lookup-parent", "1.19").Nodes
                .SetItem("input", Baseline("lookup-parent", "1.19").Nodes["input"] with { InputDataType = WorkflowDataTypes.Json })
                .SetItem("output", Baseline("lookup-parent", "1.19").Nodes["output"] with { OutputDataType = WorkflowDataTypes.Json })
        }, "input", "out", call);
        var parentRevision = WorkflowRevision.Create(parent, "proof");
        var events = new List<ExecutionEvent>();

        var result = await new WorkflowRunner().ExecuteAsync(Guid.NewGuid(), parentRevision, packedInput,
            evt => { events.Add(evt); return ValueTask.CompletedTask; });
        var outputs = WorkflowInterfaceFields.ProjectOutputs(resolved.Signature, result);
        var childStarts = events.Where(evt => evt.Kind == ExecutionEventKinds.SubworkflowEvidence &&
            WorkflowCallLifecycleEvidence.TryRead(evt.Data, out var evidence) && evidence?.Lifecycle == ExecutionEventKinds.SubworkflowStarted).ToArray();

        Assert.Single(childStarts);
        Assert.True(WorkflowCallLifecycleEvidence.TryRead(childStarts[0].Data, out var childStarted));
        Assert.Equal(resolved.Workflow.RevisionId, childStarted!.ChildRevisionId);
        Assert.Equal(JsonValueKind.Null, outputs["query"].ValueKind);
        Assert.Equal("7", outputs["limit"].GetRawText());
        Assert.Equal(JsonValueKind.False, outputs["includeArchived"].ValueKind);
        Assert.True(WorkflowCallEvidence.TryReconstructPlayback(events, out var playback));
        Assert.Single(playback!.Calls);
    }

    [Fact]
    public async Task WorkflowInterfaceCallUsesTypedPinsAndInvokesItsPinnedChildOnce()
    {
        var childDefinition = Baseline("interface-child", "1.19") with
        {
            Nodes = Baseline("interface-child", "1.19").Nodes
                .SetItem("input", Baseline("interface-child", "1.19").Nodes["input"] with { InputDataType = WorkflowDataTypes.Json })
                .SetItem("output", Baseline("interface-child", "1.19").Nodes["output"] with { OutputDataType = WorkflowDataTypes.Json })
        };
        var childRevision = WorkflowRevision.Create(childDefinition, "test");
        var operation = new WorkflowInterfaceOperation("Lookup", WorkflowDataTypes.Json, WorkflowDataTypes.Json)
        {
            Inputs = [new("query", "Query", WorkflowDataTypes.String), new("includeArchived", "Include archived", WorkflowDataTypes.Boolean)],
            Outputs = [new("query", "Query", WorkflowDataTypes.String), new("includeArchived", "Include archived", WorkflowDataTypes.Boolean)]
        };
        var contract = WorkflowInterfaceRevision.Create(new WorkflowInterfaceDefinition("lookup.api", "Lookup", "Lookup operation.",
            ImmutableSortedDictionary<string, WorkflowInterfaceOperation>.Empty.Add("lookup", operation))
        { SchemaVersion = WorkflowInterfaceJson.FieldContractSchemaVersion });
        var pin = WorkflowInterfaceWorkflowPin.From(new PublishedWorkflowRevision(1, childRevision));
        var implementation = WorkflowInterfaceImplementationRevision.Create(new WorkflowInterfaceImplementation("lookup.impl", contract,
            ImmutableSortedDictionary<string, WorkflowInterfaceWorkflowPin>.Empty.Add("lookup", pin)));
        var call = new NodeDefinition(NodeTypes.WorkflowInterfaceCall, "1.0.0")
        {
            WorkflowInterfaceCall = new(implementation, "lookup")
        };
        var nodes = new Dictionary<string, NodeDefinition>
        {
            ["input"] = new(NodeTypes.Input, "1.0.0"),
            ["include"] = new(NodeTypes.BooleanConstant, "1.0.0") { BooleanConstant = new(true) },
            ["call"] = call,
            ["output"] = new(NodeTypes.Output, "1.0.0")
        }.ToImmutableSortedDictionary(StringComparer.Ordinal);
        var data = new Dictionary<string, ConnectionDefinition>
        {
            ["query"] = new(new("input", "out"), new("call", "query"), "data"),
            ["include"] = new(new("include", "out"), new("call", "includeArchived"), "data"),
            ["result"] = new(new("call", "query"), new("output", "in"), "data")
        }.ToImmutableSortedDictionary(StringComparer.Ordinal);
        var execution = new Dictionary<string, ConnectionDefinition>
        {
            ["start"] = new(new("input", "then"), new("call", "execute"), "execution"),
            ["finish"] = new(new("call", "then"), new("output", "execute"), "execution")
        }.ToImmutableSortedDictionary(StringComparer.Ordinal);
        var definition = new WorkflowDefinition(WorkflowSchemaVersions.UnifiedWorkflowGraph, "interface-parent", "input", nodes, data)
        { ExecutionConnections = execution };
        Assert.Empty(WorkflowValidator.Validate(definition));
        var packageRequirements = WorkflowPackageJson.Create(definition, WorkflowLayout.Create(definition)).Requirements;
        Assert.Contains(packageRequirements, requirement => requirement.Kind == "workflow-interface" && requirement.Id == "lookup.api");
        Assert.Contains(packageRequirements, requirement => requirement.Kind == "workflow-interface-implementation" && requirement.Id == "lookup.impl");
        Assert.Contains(WorkflowRunDependencyClosure.Create(definition).Dependencies,
            dependency => dependency.Kind == "workflow-interface-implementation" && dependency.Id == "lookup.impl");
        Assert.Equal(new[] { "includeArchived", "query" }, WorkflowPorts.ExplicitDataInputs(call).Select(port => port.Id).Order(StringComparer.Ordinal));
        Assert.DoesNotContain(WorkflowDefinitionDescriptorAudit.Audit(definition), issue => issue.Path == "nodes.call.descriptor");
        var revision = WorkflowRevision.Create(definition, "test");
        var events = new List<ExecutionEvent>();

        var output = await new WorkflowRunner().ExecuteAsync(Guid.NewGuid(), revision, "find records",
            evt => { events.Add(evt); return ValueTask.CompletedTask; });

        Assert.Equal("find records", output);
        var evidence = events.Where(evt => evt.Kind == ExecutionEventKinds.SubworkflowEvidence &&
            WorkflowCallLifecycleEvidence.TryRead(evt.Data, out var lifecycle) && lifecycle?.Lifecycle == ExecutionEventKinds.SubworkflowStarted).ToArray();
        Assert.Single(evidence);
        Assert.True(WorkflowCallLifecycleEvidence.TryRead(evidence[0].Data, out var started));
        Assert.Equal(childRevision.RevisionId, started!.ChildRevisionId);
        var childInput = Assert.Single(events, evt => evt.Kind == ExecutionEventKinds.NodeStarted &&
            evt.NodeId == "input" && evt.ScopeId == "call");
        using var packed = JsonDocument.Parse(childInput.Data!);
        Assert.Equal("find records", packed.RootElement.GetProperty("query").GetString());
        Assert.True(packed.RootElement.GetProperty("includeArchived").GetBoolean());
    }
}
