using System.Collections.Immutable;
using System.Reflection;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using VisualAgentWorkflow.Blazor.Components;
using VisualAgentWorkflow.Core;
using Xunit;

namespace VisualAgentWorkflow.Hosting.Tests;

/// <summary>Rendered Studio authoring for the shaped json Input and core.object.get (Decision 0082 authoring slice).</summary>
public sealed class ShapedJsonEditorRenderingTests
{
    private static WorkflowDefinition Definition(bool shaped = true) => new(WorkflowSchemaVersions.UnifiedWorkflowGraph, "shape", "input",
        ImmutableSortedDictionary<string, NodeDefinition>.Empty
            .Add("input", new(NodeTypes.Input, "1.0.0")
            {
                InputDataType = "json",
                InputShape = shaped ? new JsonObjectShape([new("name", "Name", "string", true), new("age", "Age", "integer", false)]) : null
            })
            .Add("get", new(NodeTypes.ObjectGet, "1.0.0") { ObjectGet = new("age", "integer") })
            .Add("output", new(NodeTypes.Output, "1.0.0") { OutputDataType = "integer" }),
        ImmutableSortedDictionary<string, ConnectionDefinition>.Empty
            .Add("a", new(new("input", "out"), new("get", "in"), "data"))
            .Add("b", new(new("get", "out"), new("output", "in"), "data")))
    {
        ExecutionConnections = ImmutableSortedDictionary<string, ConnectionDefinition>.Empty.Add("x", new(new("input", "then"), new("output", "execute"), "execution"))
    };

    private static async Task<string> RenderSelected(WorkflowDefinition definition, string selected, bool readOnly = false)
    {
        using var services = new ServiceCollection().AddLogging().AddSingleton<Microsoft.JSInterop.IJSRuntime, NoopJsRuntime>().BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        WorkflowEditor? editor = null;
        var root = await renderer.Dispatcher.InvokeAsync(() => renderer.BeginRenderingComponent<ComponentLifecycleTests.CaptureHost<WorkflowEditor>>(
            ParameterView.FromDictionary(new Dictionary<string, object?>
            {
                ["ChildParameters"] = new Dictionary<string, object?>
                {
                    [nameof(WorkflowEditor.Definition)] = definition,
                    [nameof(WorkflowEditor.ReadOnly)] = readOnly
                },
                ["Capture"] = (Action<WorkflowEditor>)(component => editor = component)
            })));
        await root.QuiescenceTask;
        await renderer.Dispatcher.InvokeAsync(async () =>
            await (Task)typeof(WorkflowEditor).GetMethod("SelectNode", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(editor, [selected])!);
        await renderer.Dispatcher.InvokeAsync(() => typeof(ComponentBase).GetMethod("StateHasChanged", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(editor, null));
        return await renderer.Dispatcher.InvokeAsync(root.ToHtmlString);
    }

    [Fact]
    public async Task The_library_offers_object_get_and_the_canvas_labels_it()
    {
        var html = await RenderSelected(Definition(), "input");
        Assert.Contains($"data-node-key=\"{NodeTypes.ObjectGet}\"", html);
        Assert.Contains("Get object field", html);
    }

    [Fact]
    public async Task A_shaped_input_inspector_lists_each_field_with_its_stable_id_and_ordered_controls()
    {
        var html = await RenderSelected(Definition(), "input");
        Assert.Contains("Object shape (2 of 32 fields)", html);
        foreach (var label in new[] { "Display name for field name", "Type for field age", "Field age is required", "Move field name down", "Remove field age" })
            Assert.Contains(label, html);
        Assert.Contains("<code>name</code>", html);
        Assert.Contains("Add field", html);
        var firstUp = html[html.IndexOf("Move field name up", StringComparison.Ordinal)..];
        Assert.StartsWith("Move field name up\" disabled", firstUp[..Math.Min(firstUp.Length, 40)]);
    }

    [Fact]
    public async Task A_plain_json_input_offers_to_declare_a_shape()
    {
        var html = await RenderSelected(Definition(shaped: false), "input");
        Assert.Contains("Declare object shape", html);
        Assert.DoesNotContain("Display name for field", html);
    }

    [Fact]
    public async Task A_read_only_editor_shows_the_shape_without_mutating_controls()
    {
        var html = await RenderSelected(Definition(), "input", readOnly: true);
        Assert.DoesNotContain("Move field name", html);
        Assert.DoesNotContain("Declare object shape", html);
        Assert.DoesNotContain(">Add field<", html);
    }

    [Fact]
    public async Task The_object_get_inspector_picks_the_field_from_the_connected_shape()
    {
        var html = await RenderSelected(Definition(), "get");
        Assert.Contains("Field to read for get", html);
        Assert.Contains("Name (string)", html);
        Assert.Contains("Age (integer)", html);
        Assert.Contains("Outputs integer", html);
        Assert.Contains("optional", html);
    }

    [Fact]
    public async Task An_unconnected_object_get_explains_how_to_connect_instead_of_offering_fields()
    {
        var definition = Definition();
        definition = definition with { Connections = definition.Connections.Remove("a") };
        var html = await RenderSelected(definition, "get");
        Assert.DoesNotContain("Field to read for get", html);
        Assert.Contains("Wire the", html);
    }

    [Fact]
    public async Task A_rejected_inspector_edit_recreates_the_inspector_controls_so_they_show_the_stored_value()
    {
        var definition = Definition();
        definition = definition with { Nodes = definition.Nodes.Add("length", new(NodeTypes.ArrayLength, "1.0.0") { ArrayElementType = WorkflowDataTypes.String }) };
        using var services = new ServiceCollection().AddLogging().AddSingleton<Microsoft.JSInterop.IJSRuntime, NoopJsRuntime>().BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        WorkflowEditor? editor = null;
        var root = await renderer.Dispatcher.InvokeAsync(() => renderer.BeginRenderingComponent<ComponentLifecycleTests.CaptureHost<WorkflowEditor>>(
            ParameterView.FromDictionary(new Dictionary<string, object?>
            {
                ["ChildParameters"] = new Dictionary<string, object?> { [nameof(WorkflowEditor.Definition)] = definition },
                ["Capture"] = (Action<WorkflowEditor>)(component => editor = component)
            })));
        await root.QuiescenceTask;
        var counter = typeof(WorkflowEditor).GetField("rejectedEdits", BindingFlags.Instance | BindingFlags.NonPublic)!;
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            await (Task)typeof(WorkflowEditor).GetMethod("SelectNode", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(editor, ["length"])!;
            await (Task)typeof(WorkflowEditor).GetMethod("ChangeArrayElementType", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(editor, ["length", new ChangeEventArgs { Value = "not-a-type" }])!;
        });
        Assert.Equal(1, counter.GetValue(editor));
        await renderer.Dispatcher.InvokeAsync(() => typeof(ComponentBase).GetMethod("StateHasChanged", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(editor, null));
        var html = await renderer.Dispatcher.InvokeAsync(root.ToHtmlString);
        Assert.Contains("The graph edit is invalid", html);
        Assert.Contains("Array element type for length", html);
    }
}
