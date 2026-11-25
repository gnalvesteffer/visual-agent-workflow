using System.Reflection;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using VisualAgentWorkflow.Blazor;
using VisualAgentWorkflow.Blazor.Components;
using VisualAgentWorkflow.Core;
using Xunit;

namespace VisualAgentWorkflow.Hosting.Tests;

public sealed class UnifiedEditorRenderingTests
{
    [Theory]
    [InlineData("1.23")]
    [InlineData("1.31")]
    [InlineData("1.43")]
    [InlineData("2.0")]
    public async Task LibraryHasImplementedNodesWithoutFeaturePromotionControls(string schema)
    {
        var definition = WorkflowTemplates.Create("uppercase") with { SchemaVersion = schema };
        using var services = Services();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        var html = await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var root = await renderer.RenderComponentAsync<WorkflowEditor>(ParameterView.FromDictionary(new Dictionary<string, object?>
            {
                [nameof(WorkflowEditor.Definition)] = definition
            }));
            return root.ToHtmlString();
        });

        foreach (var type in new[] { NodeTypes.Uppercase, NodeTypes.Reverse, NodeTypes.TextTemplate,
            NodeTypes.WorkflowValue, NodeTypes.JsonSelectString, NodeTypes.JsonProject, NodeTypes.JsonMerge,
            NodeTypes.JsonToArray, NodeTypes.ArrayLength, NodeTypes.Loop, NodeTypes.LoopBody,
            NodeTypes.Sequence, NodeTypes.ParallelFork, NodeTypes.ParallelRendezvous, NodeTypes.Approval,
            NodeTypes.VariableGet, NodeTypes.VariableSet, NodeTypes.Wait })
            Assert.Contains($"data-node-key=\"{type}\"", html);
        Assert.DoesNotContain("graph preview", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Promote", html);
        Assert.DoesNotContain(">Enable ", html);
        Assert.Equal(schema, definition.SchemaVersion);
    }

    [Theory]
    [InlineData(NodeTypes.ParallelRendezvous)]
    [InlineData(NodeTypes.Loop)]
    [InlineData(NodeTypes.JsonProject)]
    [InlineData(NodeTypes.JsonSelectString)]
    [InlineData(NodeTypes.JsonMerge)]
    [InlineData(NodeTypes.Approval)]
    [InlineData(NodeTypes.Wait)]
    public async Task CreatingNodeThroughRenderedEditorPreservesCurrentSchema(string type)
    {
        var definition = WorkflowTemplates.Create("uppercase") with { SchemaVersion = WorkflowSchemaVersions.UnifiedWorkflowGraph };
        using var services = Services();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        WorkflowEditor? editor = null;
        WorkflowGraphEdit? edit = null;
        var root = await renderer.Dispatcher.InvokeAsync(() => renderer.BeginRenderingComponent<ComponentLifecycleTests.CaptureHost<WorkflowEditor>>(
            ParameterView.FromDictionary(new Dictionary<string, object?>
            {
                ["ChildParameters"] = new Dictionary<string, object?>
                {
                    [nameof(WorkflowEditor.Definition)] = definition,
                    [nameof(WorkflowEditor.GraphEdited)] = EventCallback.Factory.Create<WorkflowGraphEdit>(this, value => edit = value)
                },
                ["Capture"] = (Action<WorkflowEditor>)(component => editor = component)
            })));
        await root.QuiescenceTask;

        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            await (Task)typeof(WorkflowEditor).GetMethod("AddLibraryNode", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(editor, [type, null])!;
            if (edit is not null)
                await editor!.SetParametersAsync(ParameterView.FromDictionary(new Dictionary<string, object?>
                {
                    [nameof(WorkflowEditor.Definition)] = edit.Definition,
                    [nameof(WorkflowEditor.Layout)] = edit.Layout
                }));
        });

        Assert.NotNull(edit);
        Assert.Equal(WorkflowSchemaVersions.UnifiedWorkflowGraph, edit.Definition.SchemaVersion);
        var added = Assert.Single(edit.Definition.Nodes, pair => !definition.Nodes.ContainsKey(pair.Key));
        Assert.Equal(type, added.Value.Type);
        Assert.Contains($"data-node-key=\"{type}\"", await renderer.Dispatcher.InvokeAsync(root.ToHtmlString));
        Assert.Equal(WorkflowSchemaVersions.UnifiedWorkflowGraph, definition.SchemaVersion);
    }

    [Theory]
    [InlineData("MaxOutputTokens", "512")]
    [InlineData("MaxTotalTokens", "4096")]
    [InlineData("MaxTurns", "2")]
    [InlineData("AgentTimeout", "30")]
    [InlineData("ReasoningEffort", "Off")]
    [InlineData("RequireStructuredResult", "true")]
    [InlineData("AgentRoutes", "next | Next | Continue")]
    public async Task AgentSettingsRemainEditableInAnIncompleteCurrentDraft(string method, string value)
    {
        var baseline = WorkflowTemplates.CreateExplicit("uppercase");
        var definition = baseline with
        {
            Nodes = baseline.Nodes.SetItem("step-1", new(NodeTypes.Agent, "1.0.0",
                Agent: new("demo", null, "Answer the question.", [])))
                .Add("unfinished", new(NodeTypes.IntegerAdd, "1.0.0"))
        };
        using var services = Services();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        WorkflowEditor? editor = null;
        WorkflowGraphEdit? edit = null;
        var root = await renderer.Dispatcher.InvokeAsync(() => renderer.BeginRenderingComponent<ComponentLifecycleTests.CaptureHost<WorkflowEditor>>(
            ParameterView.FromDictionary(new Dictionary<string, object?>
            {
                ["ChildParameters"] = new Dictionary<string, object?>
                {
                    [nameof(WorkflowEditor.Definition)] = definition,
                    [nameof(WorkflowEditor.GraphEdited)] = EventCallback.Factory.Create<WorkflowGraphEdit>(this, change => edit = change)
                },
                ["Capture"] = (Action<WorkflowEditor>)(component => editor = component)
            })));
        await root.QuiescenceTask;
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var args = new ChangeEventArgs { Value = method == "RequireStructuredResult" ? true : value };
            await (Task)typeof(WorkflowEditor).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(editor, ["step-1", args])!;
        });
        Assert.NotNull(edit);
        Assert.Equal(WorkflowSchemaVersions.UnifiedWorkflowGraph, edit.Definition.SchemaVersion);
        Assert.NotEqual(definition.Nodes["step-1"].Agent, edit.Definition.Nodes["step-1"].Agent);
        Assert.Contains("unfinished", edit.Definition.Nodes.Keys);
    }

    [Fact]
    public async Task RendezvousConfigurationKeepsCurrentSchemaAndShowsSingleBarrierGrantPhases()
    {
        var baseline = WorkflowTemplates.CreateExplicit("uppercase");
        var branches = System.Collections.Immutable.ImmutableArray.Create(
            new ParallelBranchDescriptor("a", "A", "anchor-a"), new ParallelBranchDescriptor("b", "B", "anchor-b"));
        var definition = baseline with
        {
            Nodes = baseline.Nodes
                .Add("fork", new(NodeTypes.ParallelFork, "1.0.0") { ParallelFork = new(branches) })
                .Add("join", new(NodeTypes.ParallelJoin, "1.0.0") { ParallelJoin = new("fork", ["a", "b"]) })
                .Add("barrier", new(NodeTypes.ParallelRendezvous, "1.0.0"))
                .Add("agent", new(NodeTypes.Agent, "1.0.0", Agent: new("demo", null, "Answer.", [])))
        };
        using var services = Services();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        WorkflowEditor? editor = null;
        WorkflowGraphEdit? edit = null;
        var root = await renderer.Dispatcher.InvokeAsync(() => renderer.BeginRenderingComponent<ComponentLifecycleTests.CaptureHost<WorkflowEditor>>(
            ParameterView.FromDictionary(new Dictionary<string, object?>
            {
                ["ChildParameters"] = new Dictionary<string, object?>
                {
                    [nameof(WorkflowEditor.Definition)] = definition,
                    [nameof(WorkflowEditor.GraphEdited)] = EventCallback.Factory.Create<WorkflowGraphEdit>(this, change => edit = change)
                },
                ["Capture"] = (Action<WorkflowEditor>)(component => editor = component)
            })));
        await root.QuiescenceTask;
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            await (Task)typeof(WorkflowEditor).GetMethod("SelectNode", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(editor, ["barrier"])!;
            await (Task)typeof(WorkflowEditor).GetMethod("ConfigureCollaborationRegion", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(editor, ["barrier"])!;
            Assert.NotNull(edit);
            await editor!.SetParametersAsync(ParameterView.FromDictionary(new Dictionary<string, object?>
            {
                [nameof(WorkflowEditor.Definition)] = edit.Definition,
                [nameof(WorkflowEditor.Layout)] = edit.Layout
            }));
            await (Task)typeof(WorkflowEditor).GetMethod("AddCollaborationGrant", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(editor, ["barrier", "a"])!;
            await editor.SetParametersAsync(ParameterView.FromDictionary(new Dictionary<string, object?>
            {
                [nameof(WorkflowEditor.Definition)] = edit!.Definition,
                [nameof(WorkflowEditor.Layout)] = edit.Layout
            }));
        });
        Assert.NotNull(edit);
        Assert.Equal(WorkflowSchemaVersions.UnifiedWorkflowGraph, edit.Definition.SchemaVersion);
        Assert.Equal(new[] { "a", "b" }, edit.Definition.Nodes["barrier"].ParallelRendezvous!.BranchIds);
        var html = await renderer.Dispatcher.InvokeAsync(root.ToHtmlString);
        Assert.Contains("Publish before rendezvous", html);
        Assert.DoesNotContain(">Phase 1</option>", html);
        Assert.Contains("Second rendezvous", html);
    }

    [Theory]
    [InlineData("2500", 2_500L)]
    [InlineData("90000000", null)]
    public async Task WaitDurationInspectorStoresTheTypedValueOrRejectsItWithoutClamping(string typed, long? expected)
    {
        var definition = new WorkflowDefinition(WorkflowSchemaVersions.UnifiedWorkflowGraph, "wait-editor", "input",
            System.Collections.Immutable.ImmutableSortedDictionary<string, NodeDefinition>.Empty
                .Add("input", new(NodeTypes.Input, "1.0.0"))
                .Add("pause", new(NodeTypes.Wait, "1.0.0") { Wait = new(1_000) })
                .Add("output", new(NodeTypes.Output, "1.0.0")),
            System.Collections.Immutable.ImmutableSortedDictionary<string, ConnectionDefinition>.Empty
                .Add("value", new(new("input", "out"), new("output", "in"), "data")))
        {
            ExecutionConnections = System.Collections.Immutable.ImmutableSortedDictionary<string, ConnectionDefinition>.Empty
                .Add("a", new(new("input", "then"), new("pause", "execute"), "execution"))
                .Add("b", new(new("pause", "then"), new("output", "execute"), "execution"))
        };
        using var services = Services();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        WorkflowEditor? editor = null;
        WorkflowGraphEdit? edit = null;
        var root = await renderer.Dispatcher.InvokeAsync(() => renderer.BeginRenderingComponent<ComponentLifecycleTests.CaptureHost<WorkflowEditor>>(
            ParameterView.FromDictionary(new Dictionary<string, object?>
            {
                ["ChildParameters"] = new Dictionary<string, object?>
                {
                    [nameof(WorkflowEditor.Definition)] = definition,
                    [nameof(WorkflowEditor.GraphEdited)] = EventCallback.Factory.Create<WorkflowGraphEdit>(this, change => edit = change)
                },
                ["Capture"] = (Action<WorkflowEditor>)(component => editor = component)
            })));
        await root.QuiescenceTask;

        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            await (Task)typeof(WorkflowEditor).GetMethod("SelectNode", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(editor, ["pause"])!;
            await (Task)typeof(WorkflowEditor).GetMethod("WaitDuration", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(editor, ["pause", new ChangeEventArgs { Value = typed }])!;
        });

        await renderer.Dispatcher.InvokeAsync(() => typeof(ComponentBase).GetMethod("StateHasChanged", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(editor, null));
        if (expected is null)
        {
            // Over the 24-hour ceiling: the edit is refused and the stored definition keeps its old value, never a clamped one.
            Assert.Null(edit);
            Assert.Equal(1_000, definition.Nodes["pause"].Wait!.DurationMilliseconds);
        }
        else
        {
            Assert.NotNull(edit);
            Assert.Equal(expected, edit.Definition.Nodes["pause"].Wait!.DurationMilliseconds);
        }
        var html = await renderer.Dispatcher.InvokeAsync(root.ToHtmlString);
        Assert.Contains("Wait duration in milliseconds for pause", html);
    }

    private static ServiceProvider Services() => new ServiceCollection().AddLogging()
        .AddSingleton<Microsoft.JSInterop.IJSRuntime, NoopJsRuntime>().BuildServiceProvider();
}
