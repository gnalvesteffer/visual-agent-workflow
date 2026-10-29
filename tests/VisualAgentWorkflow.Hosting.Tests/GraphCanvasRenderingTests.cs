using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Reflection;
using System.Collections.Immutable;
using VisualAgentWorkflow.Blazor;
using VisualAgentWorkflow.Blazor.Components;
using VisualAgentWorkflow.Core;
using Xunit;

namespace VisualAgentWorkflow.Hosting.Tests;

public sealed class GraphCanvasRenderingTests
{
    [Fact]
    public async Task InputsInspectorShowsBindingStateForEachDataPin()
    {
        var definition = WorkflowTemplates.Create("uppercase") with { SchemaVersion = "1.14" };
        using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        RenderFragment<string> summary = id => builder => builder.AddContent(0, $"Summary {id}");
        RenderFragment<string> inspector = id => builder => builder.AddContent(0, $"Properties {id}");
        WorkflowGraphCanvas? canvas = null;

        var root = await renderer.Dispatcher.InvokeAsync(() => renderer.BeginRenderingComponent<ComponentLifecycleTests.CaptureHost<WorkflowGraphCanvas>>(
            ParameterView.FromDictionary(new Dictionary<string, object?>
            {
                ["ChildParameters"] = new Dictionary<string, object?>
                {
                    [nameof(WorkflowGraphCanvas.Definition)] = definition,
                    [nameof(WorkflowGraphCanvas.Layout)] = WorkflowLayout.Create(definition),
                    [nameof(WorkflowGraphCanvas.NodeContent)] = summary,
                    [nameof(WorkflowGraphCanvas.NodeInspector)] = inspector,
                    [nameof(WorkflowGraphCanvas.IdeMode)] = true,
                    [nameof(WorkflowGraphCanvas.SelectedNodeId)] = "step-1"
                },
                ["Capture"] = (Action<WorkflowGraphCanvas>)(component => canvas = component)
            })));
        await root.QuiescenceTask;

        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            typeof(WorkflowGraphCanvas).GetField("inspectorTab", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(canvas, "inputs");
            await canvas!.SetParametersAsync(ParameterView.Empty);
        });

        var html = await renderer.Dispatcher.InvokeAsync(root.ToHtmlString);
        Assert.Contains("data-input-status=\"Connected\">Connected</span>", html);
        Assert.Contains("Connected values take precedence over configured values.", html);
    }

    [Fact]
    public async Task InputsInspectorSurfacesOverConnectedSingleValuePins()
    {
        var baseline = WorkflowTemplates.Create("uppercase") with { SchemaVersion = "1.14" };
        var definition = baseline with
        {
            Connections = baseline.Connections.Add("duplicate-input", new ConnectionDefinition(
                new("input", "out"), new("step-1", "in"), "data"))
        };
        using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        RenderFragment<string> summary = id => builder => builder.AddContent(0, $"Summary {id}");
        RenderFragment<string> inspector = id => builder => builder.AddContent(0, $"Properties {id}");
        WorkflowGraphCanvas? canvas = null;

        var root = await renderer.Dispatcher.InvokeAsync(() => renderer.BeginRenderingComponent<ComponentLifecycleTests.CaptureHost<WorkflowGraphCanvas>>(
            ParameterView.FromDictionary(new Dictionary<string, object?>
            {
                ["ChildParameters"] = new Dictionary<string, object?>
                {
                    [nameof(WorkflowGraphCanvas.Definition)] = definition,
                    [nameof(WorkflowGraphCanvas.Layout)] = WorkflowLayout.Create(definition),
                    [nameof(WorkflowGraphCanvas.NodeContent)] = summary,
                    [nameof(WorkflowGraphCanvas.NodeInspector)] = inspector,
                    [nameof(WorkflowGraphCanvas.IdeMode)] = true,
                    [nameof(WorkflowGraphCanvas.SelectedNodeId)] = "step-1"
                },
                ["Capture"] = (Action<WorkflowGraphCanvas>)(component => canvas = component)
            })));
        await root.QuiescenceTask;

        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            typeof(WorkflowGraphCanvas).GetField("inspectorTab", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(canvas, "inputs");
            await canvas!.SetParametersAsync(ParameterView.Empty);
        });

        var html = await renderer.Dispatcher.InvokeAsync(root.ToHtmlString);
        Assert.Contains("data-input-status=\"TooMany\">Too many</span>", html);
        Assert.Contains("This input accepts only one connection.", html);
    }

    [Fact]
    public async Task ContextNodeLibraryPutsMcpToolsBeforeOtherActions()
    {
        var definition = WorkflowTemplates.Create("uppercase") with { SchemaVersion = "1.14" };
        var binding = new ToolBindingDescriptor("demo.reverse", "MCP · reverse", "demo-text", "reverse",
            "a") { InputSchemaJson = "{}", ToolDescription = "Reverses text", InputSummary = "text: string required", DescriptionHash = "b" };
        using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());

        var html = await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var component = await renderer.RenderComponentAsync<WorkflowEditor>(ParameterView.FromDictionary(
                new Dictionary<string, object?>
                {
                    [nameof(WorkflowEditor.Definition)] = definition,
                    [nameof(WorkflowEditor.Bindings)] = new[] { binding }
                }));
            return component.ToHtmlString();
        });

        var mcpIndex = html.IndexOf("data-node-key=\"mcp:demo.reverse:a:b\"", StringComparison.Ordinal);
        var valuesIndex = html.IndexOf("data-node-key=\"core.text.uppercase\"", StringComparison.Ordinal);
        Assert.True(mcpIndex >= 0);
        Assert.True(valuesIndex >= 0);
        Assert.True(mcpIndex < valuesIndex);
    }

    [Fact]
    public void Wire_context_library_offers_only_unary_pure_bridges()
    {
        var definition = TypedBooleanInsertionGraph();
        var editor = new WorkflowEditor();
        typeof(WorkflowEditor).GetProperty(nameof(WorkflowEditor.Definition))!.SetValue(editor, definition);
        var context = new GraphContextRequest("wire-menu", null, "equals-invert", 0, new(400, 240), 0, 0,
            "equals", "out", "invert", "value");
        var method = typeof(WorkflowEditor).GetMethod("LibraryEntries", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var entries = (IReadOnlyList<WorkflowNodeLibraryEntry>)method.Invoke(editor, [context])!;

        Assert.Contains(entries, entry => entry.Key == NodeTypes.BooleanNot);
        Assert.DoesNotContain(entries, entry => entry.Key == NodeTypes.BooleanConstant);
        Assert.DoesNotContain(entries, entry => entry.Key == NodeTypes.TextConcat);
    }

    [Fact]
    public async Task InsertionOutputPickerUsesResolvedAgentRouteDescriptors()
    {
        var baseline = WorkflowTemplates.Create("uppercase") with
        {
            SchemaVersion = WorkflowSchemaVersions.AgentExecutionRoutes
        };
        var definition = baseline with
        {
            Nodes = baseline.Nodes.SetItem("step-1", new NodeDefinition(NodeTypes.Agent, "1.0.0",
                Agent: new("connection.current", null, "Answer the question.", [])
                {
                    Routes = [
                        new AgentExecutionRoute("inventory", "Inventory", "Inventory path"),
                        new AgentExecutionRoute("quality", "Quality", "Quality path")]
                }))
        };
        using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        WorkflowEditor? editor = null;

        var root = await renderer.Dispatcher.InvokeAsync(() => renderer.BeginRenderingComponent<ComponentLifecycleTests.CaptureHost<WorkflowEditor>>(
            ParameterView.FromDictionary(new Dictionary<string, object?>
            {
                ["ChildParameters"] = new Dictionary<string, object?>
                {
                    [nameof(WorkflowEditor.Definition)] = definition,
                    [nameof(WorkflowEditor.ShowWorkflowSettings)] = false
                },
                ["Capture"] = (Action<WorkflowEditor>)(component => editor = component)
            })));
        await root.QuiescenceTask;

        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var selectNode = typeof(WorkflowEditor).GetMethod("SelectNode", BindingFlags.Instance | BindingFlags.NonPublic)!;
            await (Task)selectNode.Invoke(editor, ["step-1"])!;
            await editor!.SetParametersAsync(ParameterView.Empty);
        });

        var html = await renderer.Dispatcher.InvokeAsync(root.ToHtmlString);
        Assert.Contains("aria-label=\"Insert on output\"", html);
        Assert.Contains("value=\"route.inventory\"", html);
        Assert.Contains("value=\"route.quality\"", html);
    }

    private static WorkflowDefinition TypedBooleanInsertionGraph() => new(WorkflowSchemaVersions.TypedBooleanGraph, "typed-boolean", "input",
        ImmutableSortedDictionary<string, NodeDefinition>.Empty
            .Add("input", new(NodeTypes.Input, "1.0.0"))
            .Add("expected", new(NodeTypes.StringConstant, "1.0.0") { StringConstant = new("yes") })
            .Add("equals", new(NodeTypes.StringEquals, "1.0.0"))
            .Add("invert", new(NodeTypes.BooleanNot, "1.0.0"))
            .Add("branch", new(NodeTypes.BooleanBranch, "1.0.0"))
            .Add("output", new(NodeTypes.Output, "1.0.0"))
            .Add("terminate", new(NodeTypes.Terminate, "1.0.0") { Termination = new(WorkflowTerminationOutcomes.Failure) }),
        ImmutableSortedDictionary<string, ConnectionDefinition>.Empty
            .Add("input-left", new(new("input", "out"), new("equals", "left"), "data"))
            .Add("expected-right", new(new("expected", "out"), new("equals", "right"), "data"))
            .Add("equals-invert", new(new("equals", "out"), new("invert", "value"), "data"))
            .Add("invert-branch", new(new("invert", "out"), new("branch", "condition"), "data") )
            .Add("output-value", new(new("input", "out"), new("output", "in"), "data")))
    {
        ExecutionConnections = ImmutableSortedDictionary<string, ConnectionDefinition>.Empty
            .Add("activate-branch", new(new("input", "then"), new("branch", "execute"), "execution"))
            .Add("true-route", new(new("branch", "true"), new("output", "execute"), "execution"))
            .Add("false-route", new(new("branch", "false"), new("terminate", "execute"), "execution"))
    };

    [Fact]
    public async Task IdeGraphMinimapIncludesViewportIndicator()
    {
        var definition = WorkflowTemplates.Create("uppercase") with { SchemaVersion = "1.14" };
        using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());

        var html = await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var component = await renderer.RenderComponentAsync<WorkflowEditor>(ParameterView.FromDictionary(
                new Dictionary<string, object?>
                {
                    [nameof(WorkflowEditor.Definition)] = definition,
                    [nameof(WorkflowEditor.IdeMode)] = true,
                    [nameof(WorkflowEditor.ShowWorkflowSettings)] = false
                }));
            return component.ToHtmlString();
        });

        Assert.Contains("data-canvas-minimap", html);
        Assert.Contains("data-minimap-viewport", html);
        Assert.Contains("vaw-minimap-node", html);
    }

    [Fact]
    public async Task CommentExpansionStaysOnNodeAndDoesNotResizeInspectorEditor()
    {
        var definition = WorkflowTemplates.Create("uppercase") with { SchemaVersion = "1.14" };
        var layout = WorkflowLayout.Create(definition);
        layout = layout with
        {
            Nodes = layout.Nodes.SetItem("step-1",
                new NodePresentation(new(440, 100), Comment: "A long node comment that is expanded on the graph.", CommentExpanded: true))
        };
        using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());

        var html = await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var component = await renderer.RenderComponentAsync<WorkflowEditor>(ParameterView.FromDictionary(
                new Dictionary<string, object?>
                {
                    [nameof(WorkflowEditor.Definition)] = definition,
                    [nameof(WorkflowEditor.Layout)] = layout
                }));
            return component.ToHtmlString();
        });

        Assert.Contains("Collapse node comment", html);
        Assert.Contains("rows=\"6\"", html);
    }

    [Fact]
    public async Task AgentConnectionIsNotReportedUnavailableWhileProviderChoicesAreLoading()
    {
        var baseline = WorkflowTemplates.Create("uppercase") with { SchemaVersion = "1.2" };
        var definition = baseline with
        {
            EntryNodeId = "step-1",
            Nodes = baseline.Nodes.SetItem("step-1", new(NodeTypes.Agent, "1.0.0",
                Agent: new("connection.current", null, "Answer the question.", [])))
        };
        using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());

        var loadingHtml = await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var component = await renderer.RenderComponentAsync<WorkflowEditor>(ParameterView.FromDictionary(
                new Dictionary<string, object?>
                {
                    [nameof(WorkflowEditor.Definition)] = definition,
                    [nameof(WorkflowEditor.ProviderChoicesLoading)] = true
                }));
            return component.ToHtmlString();
        });

        Assert.Contains("Loading model connections", loadingHtml);
        Assert.DoesNotContain("Model connection unavailable: connection.current", loadingHtml);

        var completedHtml = await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var component = await renderer.RenderComponentAsync<WorkflowEditor>(ParameterView.FromDictionary(
                new Dictionary<string, object?> { [nameof(WorkflowEditor.Definition)] = definition }));
            return component.ToHtmlString();
        });

        Assert.Contains("Model connection unavailable: connection.current", completedHtml);
    }

    [Fact]
    public async Task RunningAgentNodeShowsRecordedUsageContextAndEstimatedCost()
    {
        var baseline = WorkflowTemplates.Create("uppercase") with { SchemaVersion = "1.2" };
        var definition = baseline with { Nodes = baseline.Nodes.SetItem("step-1", new(NodeTypes.Agent, "1.0.0",
            Agent: new("connection.current", null, "Answer the question.", []))) };
        var revision = WorkflowRevision.Create(definition, "test");
        var runId = Guid.NewGuid();
        var invocationId = Guid.NewGuid();
        var events = ImmutableArray.Create(
            new ExecutionEvent(runId, revision.RevisionId, 1, DateTimeOffset.UtcNow, ExecutionEventKinds.NodeStarted, "step-1", invocationId),
            new ExecutionEvent(runId, revision.RevisionId, 2, DateTimeOffset.UtcNow, ExecutionEventKinds.AgentStarted, "step-1", invocationId,
                Data: """{"provider":{"ModelId":"test-model","ContextWindowTokens":8000,"InputCostPerMillionTokens":1.5,"OutputCostPerMillionTokens":3.0,"CacheReadCostPerMillionTokens":0.2,"CacheWriteCostPerMillionTokens":0.4},"maximumTurns":8}"""),
            new ExecutionEvent(runId, revision.RevisionId, 3, DateTimeOffset.UtcNow, ExecutionEventKinds.ModelRequested, "step-1", invocationId,
                Data: """{"modelCallId":"request-1"}"""),
            new ExecutionEvent(runId, revision.RevisionId, 4, DateTimeOffset.UtcNow, ExecutionEventKinds.ModelResponded, "step-1", invocationId,
                Data: """{"modelCallId":"response-1","ModelId":"test-model","usage":{"InputTokenCount":1200,"OutputTokenCount":200,"TotalTokenCount":1400,"CachedInputTokenCount":200,"cacheWriteInputTokenCount":100,"ReasoningTokenCount":50}}"""),
            new ExecutionEvent(runId, revision.RevisionId, 5, DateTimeOffset.UtcNow, ExecutionEventKinds.AgentContextCompacted, "step-1", invocationId));
        var snapshot = new RunSnapshot(new(runId, revision.RevisionId, null, "test", DateTimeOffset.UtcNow,
            revision.ContentHash, definition, "question"), events);
        using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());

        var html = await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var component = await renderer.RenderComponentAsync<WorkflowRunViewer>(ParameterView.FromDictionary(
                new Dictionary<string, object?> { [nameof(WorkflowRunViewer.Snapshot)] = snapshot }));
            return component.ToHtmlString();
        });

        Assert.Contains("Tokens · 1,200 in / 200 out", html);
        Assert.Contains("Model turns · 1 / 8", html);
        Assert.Contains("Cache · 200 read / 100 write", html);
        Assert.Contains("Reasoning · 50 tokens (included in output)", html);
        Assert.Contains("Last completed context · 1,200 / 8,000 (15%)", html);
        Assert.Contains("Host context compactions · 1", html);
        Assert.Contains("Estimated cost · $0.002030", html);
        Assert.Contains("vaw-agent-context-meter", html);
        Assert.Contains("Cost · $0.002030", html);
        Assert.Contains("test-model", html);
    }

    [Fact]
    public async Task RunViewerAddsNodeEventActionToEachRecordedNode()
    {
        var definition = WorkflowTemplates.Create("uppercase");
        var revision = WorkflowRevision.Create(definition, "test");
        var runId = Guid.NewGuid();
        var invocationId = Guid.NewGuid();
        var events = ImmutableArray.Create(
            new ExecutionEvent(runId, revision.RevisionId, 1, DateTimeOffset.UtcNow, ExecutionEventKinds.NodeStarted, definition.EntryNodeId, invocationId, Data: "input"),
            new ExecutionEvent(runId, revision.RevisionId, 2, DateTimeOffset.UtcNow, ExecutionEventKinds.NodeCompleted, definition.EntryNodeId, invocationId, Data: "output"));
        var snapshot = new RunSnapshot(new(runId, revision.RevisionId, null, "test", DateTimeOffset.UtcNow,
            revision.ContentHash, definition, "question"), events);
        using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());

        var html = await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var component = await renderer.RenderComponentAsync<WorkflowRunViewer>(ParameterView.FromDictionary(
                new Dictionary<string, object?> { [nameof(WorkflowRunViewer.Snapshot)] = snapshot }));
            return component.ToHtmlString();
        });

        Assert.Contains($"View events for {definition.EntryNodeId}", html);
        Assert.Contains("vaw-node-events-button", html);
        Assert.Contains("vaw-node-status-chip", html);
        Assert.Contains("data-pane-resizer=\"bottom\"", html);
        Assert.DoesNotContain("vaw-node-events-dialog", html);
    }

    [Fact]
    public async Task RunViewerEventEvidenceExposesInvocationAndConnectionIdentity()
    {
        var definition = WorkflowTemplates.Create("uppercase");
        var revision = WorkflowRevision.Create(definition, "test");
        var runId = Guid.NewGuid();
        var invocationId = Guid.NewGuid();
        var events = ImmutableArray.Create(
            new ExecutionEvent(runId, revision.RevisionId, 1, DateTimeOffset.UtcNow, ExecutionEventKinds.NodeStarted,
                definition.EntryNodeId, invocationId, ConnectionId: "input.then", Data: "input"));
        var snapshot = new RunSnapshot(new(runId, revision.RevisionId, null, "test", DateTimeOffset.UtcNow,
            revision.ContentHash, definition, "question"), events);
        using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());

        var html = await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var component = await renderer.RenderComponentAsync<WorkflowRunViewer>(ParameterView.FromDictionary(
                new Dictionary<string, object?> { [nameof(WorkflowRunViewer.Snapshot)] = snapshot }));
            return component.ToHtmlString();
        });

        Assert.Contains("Invocation", html);
        Assert.Contains("Connection", html);
        Assert.Contains(invocationId.ToString(), html);
        Assert.Contains("input.then", html);
    }

    [Fact]
    public async Task RunViewerDistinguishesCancellationAndShowsRecordedCauseAndRecoveryGuidance()
    {
        var baseline = WorkflowTemplates.Create("uppercase") with { SchemaVersion = "1.2" };
        var definition = baseline with { Nodes = baseline.Nodes.SetItem("step-1", new(NodeTypes.Agent, "1.0.0",
            Agent: new("connection.current", null, "Answer the question.", []))) };
        var revision = WorkflowRevision.Create(definition, "test");
        var runId = Guid.NewGuid();
        var invocationId = Guid.NewGuid();
        var events = ImmutableArray.Create(
            new ExecutionEvent(runId, revision.RevisionId, 1, DateTimeOffset.UtcNow, ExecutionEventKinds.NodeStarted, "step-1", invocationId),
            new ExecutionEvent(runId, revision.RevisionId, 2, DateTimeOffset.UtcNow, ExecutionEventKinds.AgentFailed, "step-1", invocationId,
                Data: "{\"outcome\":\"cancelled\",\"reason\":\"The host cancelled the agent invocation.\"}"),
            new ExecutionEvent(runId, revision.RevisionId, 3, DateTimeOffset.UtcNow, ExecutionEventKinds.NodeCancelled, "step-1", invocationId,
                Data: "Node execution cancelled."),
            new ExecutionEvent(runId, revision.RevisionId, 4, DateTimeOffset.UtcNow, ExecutionEventKinds.Cancelled, "step-1", invocationId,
                Data: "Run cancelled by host."));
        var snapshot = new RunSnapshot(new(runId, revision.RevisionId, null, "test", DateTimeOffset.UtcNow,
            revision.ContentHash, definition, "question"), events);
        using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());

        var html = await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var component = await renderer.RenderComponentAsync<WorkflowRunViewer>(ParameterView.FromDictionary(
                new Dictionary<string, object?> { [nameof(WorkflowRunViewer.Snapshot)] = snapshot }));
            return component.ToHtmlString();
        });

        Assert.Contains("vaw-cancelled", html);
        Assert.Contains("Cancelled by host while the agent was running.", html);
        Assert.Contains("Run ended · start a new run to retry.", html);
        Assert.DoesNotContain("vaw-failed", html);
    }

    [Fact]
    public async Task RunViewerExplainsGenericAgentFailureWithRecordedToolContext()
    {
        var baseline = WorkflowTemplates.Create("uppercase") with { SchemaVersion = "1.2" };
        var definition = baseline with { Nodes = baseline.Nodes.SetItem("step-1", new(NodeTypes.Agent, "1.0.0",
            Agent: new("connection.current", null, "Answer the question.", []))) };
        var revision = WorkflowRevision.Create(definition, "test");
        var runId = Guid.NewGuid();
        var invocationId = Guid.NewGuid();
        var toolCallId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var events = ImmutableArray.Create(
            new ExecutionEvent(runId, revision.RevisionId, 1, now, ExecutionEventKinds.NodeStarted, "step-1", invocationId),
            new ExecutionEvent(runId, revision.RevisionId, 2, now.AddMilliseconds(1), ExecutionEventKinds.ToolAuthorized, "step-1", invocationId,
                Data: $"{{\"toolCallId\":\"{toolCallId}\",\"bindingId\":\"sandbox.read_file\",\"input\":{{\"path\":\"visual-agent-workflow-source/docs/architecture.md\"}}}}"),
            new ExecutionEvent(runId, revision.RevisionId, 3, now.AddMilliseconds(2), ExecutionEventKinds.ToolFailed, "step-1", invocationId,
                Data: $"{{\"toolCallId\":\"{toolCallId}\",\"bindingId\":\"sandbox.read_file\",\"reason\":\"Tool operation failed.\"}}"),
            new ExecutionEvent(runId, revision.RevisionId, 4, now.AddMilliseconds(3), ExecutionEventKinds.ModelResponded, "step-1", invocationId,
                Data: """{"messages":[{"content":[{"kind":"function-result","result":"Error: Function failed."}]}]}"""),
            new ExecutionEvent(runId, revision.RevisionId, 5, now.AddMilliseconds(4), ExecutionEventKinds.AgentFailed, "step-1", invocationId,
                Data: "{\"reason\":\"Agent execution failed.\"}"),
            new ExecutionEvent(runId, revision.RevisionId, 6, now.AddMilliseconds(5), ExecutionEventKinds.NodeFailed, "step-1", invocationId,
                Data: "Node execution failed."));
        var snapshot = new RunSnapshot(new(runId, revision.RevisionId, null, "test", now,
            revision.ContentHash, definition, "question"), events);
        using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());

        var html = await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var component = await renderer.RenderComponentAsync<WorkflowRunViewer>(ParameterView.FromDictionary(
                new Dictionary<string, object?> { [nameof(WorkflowRunViewer.Snapshot)] = snapshot }));
            return component.ToHtmlString();
        });

        Assert.Contains("Tool call", html);
        Assert.Contains("sandbox.read_file", html);
        Assert.Contains("visual-agent-workflow-source/docs/architecture.md", html);
        Assert.Contains("Function failed.", html);
        Assert.DoesNotContain("Execution failed without a recorded detail.", html);
    }

    [Fact]
    public async Task AgentNodeDoesNotRenderMissingProviderUsageAsZeroes()
    {
        var baseline = WorkflowTemplates.Create("uppercase") with { SchemaVersion = "1.2" };
        var definition = baseline with { Nodes = baseline.Nodes.SetItem("step-1", new(NodeTypes.Agent, "1.0.0",
            Agent: new("connection.current", null, "Answer the question.", []))) };
        var revision = WorkflowRevision.Create(definition, "test");
        var runId = Guid.NewGuid();
        var invocationId = Guid.NewGuid();
        var events = ImmutableArray.Create(
            new ExecutionEvent(runId, revision.RevisionId, 1, DateTimeOffset.UtcNow, ExecutionEventKinds.NodeStarted, "step-1", invocationId),
            new ExecutionEvent(runId, revision.RevisionId, 2, DateTimeOffset.UtcNow, ExecutionEventKinds.AgentStarted, "step-1", invocationId,
                Data: """{"provider":{"ModelId":"test-model","ContextWindowTokens":128000},"maximumTurns":16}"""),
            new ExecutionEvent(runId, revision.RevisionId, 3, DateTimeOffset.UtcNow, ExecutionEventKinds.ModelRequested, "step-1", invocationId,
                Data: """{"modelCallId":"request-1"}"""),
            new ExecutionEvent(runId, revision.RevisionId, 4, DateTimeOffset.UtcNow, ExecutionEventKinds.ModelResponded, "step-1", invocationId,
                Data: """{"modelCallId":"request-1","ModelId":"test-model","finishReason":"stop"}"""));
        var snapshot = new RunSnapshot(new(runId, revision.RevisionId, null, "test", DateTimeOffset.UtcNow,
            revision.ContentHash, definition, "question"), events);
        using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());

        var html = await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var component = await renderer.RenderComponentAsync<WorkflowRunViewer>(ParameterView.FromDictionary(
                new Dictionary<string, object?> { [nameof(WorkflowRunViewer.Snapshot)] = snapshot }));
            return component.ToHtmlString();
        });

        Assert.Contains("Model turns · 1 / 16", html);
        Assert.Contains("Usage · not reported by provider", html);
        Assert.Contains("Context window · 128,000 tokens; prompt usage pending", html);
        Assert.DoesNotContain("Tokens · 0 in / 0 out", html);
        Assert.DoesNotContain("Context · 0 / 0", html);
    }

    [Fact]
    public async Task AgentNodeRendersUsageWhenProviderSerializesOptionalCountsAsNull()
    {
        var baseline = WorkflowTemplates.Create("uppercase") with { SchemaVersion = "1.2" };
        var definition = baseline with { Nodes = baseline.Nodes.SetItem("step-1", new(NodeTypes.Agent, "1.0.0",
            Agent: new("connection.current", null, "Answer the question.", []))) };
        var revision = WorkflowRevision.Create(definition, "test");
        var runId = Guid.NewGuid();
        var invocationId = Guid.NewGuid();
        var events = ImmutableArray.Create(
            new ExecutionEvent(runId, revision.RevisionId, 1, DateTimeOffset.UtcNow, ExecutionEventKinds.NodeStarted, "step-1", invocationId),
            new ExecutionEvent(runId, revision.RevisionId, 2, DateTimeOffset.UtcNow, ExecutionEventKinds.AgentStarted, "step-1", invocationId,
                Data: """{"provider":{"ModelId":"test-model","ContextWindowTokens":128000},"maximumTurns":10}"""),
            new ExecutionEvent(runId, revision.RevisionId, 3, DateTimeOffset.UtcNow, ExecutionEventKinds.ModelResponded, "step-1", invocationId,
                Data: """{"modelCallId":"model-1","ModelId":"test-model","usage":{"InputTokenCount":602,"OutputTokenCount":111,"TotalTokenCount":713,"CachedInputTokenCount":null,"ReasoningTokenCount":65,"cacheWriteInputTokenCount":0}}"""));
        var snapshot = new RunSnapshot(new(runId, revision.RevisionId, null, "test", DateTimeOffset.UtcNow,
            revision.ContentHash, definition, "question"), events);
        using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());

        var html = await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var component = await renderer.RenderComponentAsync<WorkflowRunViewer>(ParameterView.FromDictionary(
                new Dictionary<string, object?> { [nameof(WorkflowRunViewer.Snapshot)] = snapshot }));
            return component.ToHtmlString();
        });

        Assert.Contains("Tokens · 602 in / 111 out", html);
        Assert.Contains("Model turns · 0 / 10", html);
        Assert.Contains("Reasoning · 65 tokens (included in output)", html);
        Assert.DoesNotContain("Usage · not reported by provider", html);
    }

    [Fact]
    public async Task AgentNodeRendersOpenAiCompatibleUsageAliases()
    {
        var baseline = WorkflowTemplates.Create("uppercase") with { SchemaVersion = "1.2" };
        var definition = baseline with { Nodes = baseline.Nodes.SetItem("step-1", new(NodeTypes.Agent, "1.0.0",
            Agent: new("connection.current", null, "Answer the question.", []))) };
        var revision = WorkflowRevision.Create(definition, "test");
        var runId = Guid.NewGuid();
        var invocationId = Guid.NewGuid();
        var events = ImmutableArray.Create(
            new ExecutionEvent(runId, revision.RevisionId, 1, DateTimeOffset.UtcNow, ExecutionEventKinds.NodeStarted, "step-1", invocationId),
            new ExecutionEvent(runId, revision.RevisionId, 2, DateTimeOffset.UtcNow, ExecutionEventKinds.AgentStarted, "step-1", invocationId,
                Data: """{"provider":{"ModelId":"test-model","ContextWindowTokens":10000},"maximumTurns":30}"""),
            new ExecutionEvent(runId, revision.RevisionId, 3, DateTimeOffset.UtcNow, ExecutionEventKinds.ModelResponded, "step-1", invocationId,
                Data: """{"ModelId":"test-model","usage":{"prompt_tokens":400,"completion_tokens":50,"prompt_tokens_details":{"cached_tokens":25},"completion_tokens_details":{"reasoning_tokens":10}}}"""));
        var snapshot = new RunSnapshot(new(runId, revision.RevisionId, null, "test", DateTimeOffset.UtcNow,
            revision.ContentHash, definition, "question"), events);
        using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());

        var html = await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var component = await renderer.RenderComponentAsync<WorkflowRunViewer>(ParameterView.FromDictionary(
                new Dictionary<string, object?> { [nameof(WorkflowRunViewer.Snapshot)] = snapshot }));
            return component.ToHtmlString();
        });

        Assert.Contains("Tokens · 400 in / 50 out", html);
        Assert.Contains("Cache · 25 read", html);
        Assert.Contains("write", html);
        Assert.Contains("Reasoning · 10 tokens (included in output)", html);
        Assert.Contains("Context · 400 / 10,000 (4%)", html);
        Assert.DoesNotContain("Usage · not reported by provider", html);
    }

    [Fact]
    public async Task RunningAgentNodeExplainsThatUsageIsPendingBeforeFirstResponse()
    {
        var baseline = WorkflowTemplates.Create("uppercase") with { SchemaVersion = "1.2" };
        var definition = baseline with { Nodes = baseline.Nodes.SetItem("step-1", new(NodeTypes.Agent, "1.0.0",
            Agent: new("connection.current", null, "Answer the question.", []))) };
        var revision = WorkflowRevision.Create(definition, "test");
        var runId = Guid.NewGuid();
        var invocationId = Guid.NewGuid();
        var events = ImmutableArray.Create(
            new ExecutionEvent(runId, revision.RevisionId, 1, DateTimeOffset.UtcNow, ExecutionEventKinds.NodeStarted, "step-1", invocationId),
            new ExecutionEvent(runId, revision.RevisionId, 2, DateTimeOffset.UtcNow, ExecutionEventKinds.AgentStarted, "step-1", invocationId,
                Data: """{"provider":{"ModelId":"test-model","ContextWindowTokens":128000},"maximumTurns":30}"""),
            new ExecutionEvent(runId, revision.RevisionId, 3, DateTimeOffset.UtcNow, ExecutionEventKinds.ModelRequested, "step-1", invocationId,
                Data: """{"modelCallId":"request-1"}"""));
        var snapshot = new RunSnapshot(new(runId, revision.RevisionId, null, "test", DateTimeOffset.UtcNow,
            revision.ContentHash, definition, "question"), events);
        using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());

        var html = await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var component = await renderer.RenderComponentAsync<WorkflowRunViewer>(ParameterView.FromDictionary(
                new Dictionary<string, object?> { [nameof(WorkflowRunViewer.Snapshot)] = snapshot }));
            return component.ToHtmlString();
        });

        Assert.Contains("Usage · waiting for the provider response", html);
        Assert.Contains("Context window · 128,000 tokens; awaiting first response usage", html);
        Assert.DoesNotContain("Usage · not reported by provider", html);
    }

    [Fact]
    public async Task AgentNodeRendersWhenProviderMetricsAreNull()
    {
        var baseline = WorkflowTemplates.Create("uppercase") with { SchemaVersion = "1.2" };
        var definition = baseline with { Nodes = baseline.Nodes.SetItem("step-1", new(NodeTypes.Agent, "1.0.0",
            Agent: new("connection.current", null, "Answer the question.", []))) };
        var revision = WorkflowRevision.Create(definition, "test");
        var runId = Guid.NewGuid();
        var invocationId = Guid.NewGuid();
        var events = ImmutableArray.Create(
            new ExecutionEvent(runId, revision.RevisionId, 1, DateTimeOffset.UtcNow, ExecutionEventKinds.NodeStarted, "step-1", invocationId),
            new ExecutionEvent(runId, revision.RevisionId, 2, DateTimeOffset.UtcNow, ExecutionEventKinds.AgentStarted, "step-1", invocationId,
                Data: """{"provider":{"ModelId":"test-model","ContextWindowTokens":null,"InputCostPerMillionTokens":null,"OutputCostPerMillionTokens":null}}"""),
            new ExecutionEvent(runId, revision.RevisionId, 3, DateTimeOffset.UtcNow, ExecutionEventKinds.ModelResponded, "step-1", invocationId,
                Data: """{"ModelId":"test-model","usage":{"InputTokenCount":12,"OutputTokenCount":2,"TotalTokenCount":14}}"""));
        var snapshot = new RunSnapshot(new(runId, revision.RevisionId, null, "test", DateTimeOffset.UtcNow,
            revision.ContentHash, definition, "question"), events);
        using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());

        var html = await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var component = await renderer.RenderComponentAsync<WorkflowRunViewer>(ParameterView.FromDictionary(
                new Dictionary<string, object?> { [nameof(WorkflowRunViewer.Snapshot)] = snapshot }));
            return component.ToHtmlString();
        });

        Assert.Contains("Tokens · 12 in / 2 out", html);
        Assert.Contains("Cost estimate · unavailable", html);
        Assert.Contains("test-model", html);
        Assert.Contains("Context window · not configured in this model connection", html);
        Assert.Contains("Host context compactions · 0", html);
    }

    [Fact]
    public async Task RunningAgentNodeMarksContextUsageAsStaleUntilCurrentRequestResponds()
    {
        var baseline = WorkflowTemplates.Create("uppercase") with { SchemaVersion = "1.2" };
        var definition = baseline with { Nodes = baseline.Nodes.SetItem("step-1", new(NodeTypes.Agent, "1.0.0",
            Agent: new("connection.current", null, "Answer the question.", []))) };
        var revision = WorkflowRevision.Create(definition, "test");
        var runId = Guid.NewGuid();
        var invocationId = Guid.NewGuid();
        var events = ImmutableArray.Create(
            new ExecutionEvent(runId, revision.RevisionId, 1, DateTimeOffset.UtcNow, ExecutionEventKinds.NodeStarted, "step-1", invocationId),
            new ExecutionEvent(runId, revision.RevisionId, 2, DateTimeOffset.UtcNow, ExecutionEventKinds.AgentStarted, "step-1", invocationId,
                Data: """{"provider":{"ModelId":"test-model","ContextWindowTokens":8000}}"""),
            new ExecutionEvent(runId, revision.RevisionId, 3, DateTimeOffset.UtcNow, ExecutionEventKinds.ModelResponded, "step-1", invocationId,
                Data: """{"modelCallId":"response-1","usage":{"InputTokenCount":400,"OutputTokenCount":50,"TotalTokenCount":450}}"""),
            new ExecutionEvent(runId, revision.RevisionId, 4, DateTimeOffset.UtcNow, ExecutionEventKinds.ModelRequested, "step-1", invocationId,
                Data: """{"modelCallId":"request-2"}"""));
        var snapshot = new RunSnapshot(new(runId, revision.RevisionId, null, "test", DateTimeOffset.UtcNow,
            revision.ContentHash, definition, "question"), events);
        using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());

        var html = await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var component = await renderer.RenderComponentAsync<WorkflowRunViewer>(ParameterView.FromDictionary(
                new Dictionary<string, object?> { [nameof(WorkflowRunViewer.Snapshot)] = snapshot }));
            return component.ToHtmlString();
        });

        Assert.Contains("Last completed context · 400 / 8,000 (5%)", html);
        Assert.Contains("Model request in progress · current usage updates when it responds", html);
        Assert.Contains("Tokens · 400 in / 50 out", html);
    }

    [Fact]
    public async Task RecordedLayoutEncodesUntrustedLabelsAndCommentsWithoutExecutionServices()
    {
        var definition = WorkflowTemplates.Create("uppercase");
        var layout = WorkflowLayout.Create(definition);
        layout = layout with
        {
            Nodes = layout.Nodes.SetItem("step-1", new(new(600, 250), "<img src=x onerror=alert(1)>", "<script>comment</script>")),
            Sections = layout.Sections.Add("section", new("<script>section</script>", ["step-1"]))
        };
        var revision = WorkflowRevision.Create(definition, "test");
        var snapshot = new RunSnapshot(new(Guid.NewGuid(), revision.RevisionId, null, "test", DateTimeOffset.UtcNow,
            revision.ContentHash, definition, "hello"), []);
        using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        var html = await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var component = await renderer.RenderComponentAsync<WorkflowRunViewer>(ParameterView.FromDictionary(new Dictionary<string, object?>
                { [nameof(WorkflowRunViewer.Snapshot)] = snapshot, [nameof(WorkflowRunViewer.Layout)] = layout }));
            return component.ToHtmlString();
        });
        Assert.Contains("&lt;img", html);
        Assert.Contains("&lt;script&gt;comment&lt;/script&gt;", html);
        Assert.Contains("&lt;script&gt;section&lt;/script&gt;", html);
        Assert.DoesNotContain("<script>", html);
        Assert.DoesNotContain("<img", html);
        Assert.Contains("left:600px;top:250px", html);
        Assert.Contains("data-disabled=\"true\"", html);
    }
}
