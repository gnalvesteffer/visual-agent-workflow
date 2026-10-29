using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using VisualAgentWorkflow.Core;

namespace VisualAgentWorkflow.Blazor.Components;

internal sealed record CanvasConnection(string Id, ConnectionDefinition Definition, string Kind);

public partial class WorkflowGraphCanvas
{
    [Parameter, EditorRequired] public WorkflowDefinition Definition { get; set; } = default!;
    [Parameter, EditorRequired] public WorkflowLayout Layout { get; set; } = default!;
    [Parameter, EditorRequired] public RenderFragment<string> NodeContent { get; set; } = default!;
    [Parameter] public RenderFragment? NodeLibrary { get; set; }
    [Parameter] public bool IdeMode { get; set; }
    [Parameter] public bool ShowNodeLibrary { get; set; } = true;
    [Parameter] public RenderFragment<GraphContextRequest>? ContextMenu { get; set; }
    [Parameter] public bool EnableContextMenu { get; set; } = true;
    [Parameter] public EventCallback<GraphNodeCreateRequest> NodeCreateRequested { get; set; }
    [Parameter] public EventCallback<string> DeleteNodeRequested { get; set; }
    [Parameter] public EventCallback<string> ConnectionSelected { get; set; }
    [Parameter] public EventCallback<string> ConnectionDisconnectRequested { get; set; }
    [Parameter] public EventCallback<(string SourceId, string SourcePort, string TargetId, string TargetPort)> ConnectionRequested { get; set; }
    [Parameter] public EventCallback<string[]> ClipboardCopyRequested { get; set; }
    [Parameter] public EventCallback<GraphPoint> ClipboardPasteRequested { get; set; }
    private GraphContextRequest? contextMenu;
    private string? activeConnection;
    [Parameter] public string InspectorTitle { get; set; } = "Node properties";
    [Parameter] public RenderFragment<string>? NodeInspector { get; set; }
    [Parameter] public RenderFragment? ToolbarContent { get; set; }
    [Parameter] public string? SelectedNodeId { get; set; }
    [Parameter] public EventCallback<string> SelectedNodeIdChanged { get; set; }
    [Parameter] public EventCallback<WorkflowLayout> LayoutChanged { get; set; }
    [Parameter] public bool ReadOnly { get; set; }
    [Parameter] public bool CompactReadOnly { get; set; }
    [Parameter] public bool Disabled { get; set; }
    [Parameter] public string NodesLabel { get; set; } = "Editable workflow nodes";
    [Parameter] public Func<string, string>? NodeClass { get; set; }
    [Parameter] public Func<NodeDefinition, ImmutableArray<WorkflowPortDescriptor>>? PortResolver { get; set; }
    [Parameter] public IReadOnlySet<string>? DeliveredConnections { get; set; }
    [Inject] private IServiceProvider Services { get; set; } = default!;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private ElementReference root;
    private IJSObjectReference? module;
    private IJSObjectReference? browserSession;
    private IJSObjectReference? paneModule;
    private IJSObjectReference? paneSession;
    private DotNetObjectReference<WorkflowGraphCanvas>? callback;
    private bool disposed;
    private bool ready;
    private double zoom = 1;
    private string? selectedNode;
    private string inspectorTab = "configure";
    private string? CurrentSelection => SelectedNodeId is not null && Definition.Nodes.ContainsKey(SelectedNodeId) ? SelectedNodeId : selectedNode;
    private string? selectedWire;
    private string sectionLabel = "New section";
    private HashSet<string> members = new(StringComparer.Ordinal);
    private string? error;
    private string pinHintText = "Drag an output pin to a compatible input pin, or release on empty canvas to create a step.";
    private WorkflowDefinition? orderedDefinition;
    private WorkflowLayout? lastLayout;
    private bool revealUpdatedReadOnlyLayout;
    private ImmutableArray<string> Order = [];
    private bool CanEdit => !ReadOnly && !Disabled && LayoutChanged.HasDelegate && !disposed;
    private double Width => Math.Max(1200, Layout.Nodes.Values.Max(p => p.Position.X) + 480);
    private double Height => Math.Max(800, Layout.Nodes.Values.Max(p => p.Position.Y) + 900);
    private const double CanvasMargin = 1600;
    private string WorldStyle => $"left:{Number(CanvasMargin)}px;top:{Number(CanvasMargin)}px;width:{Number(Width)}px;height:{Number(Height)}px;transform:scale({Number(zoom)})";
    private string ExtentStyle => $"width:{Number(CanvasMargin * 2 + Width * zoom)}px;height:{Number(CanvasMargin * 2 + Height * zoom)}px";
    private static string Number(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
    private static string PositionStyle(GraphPoint point) => $"left:{Number(point.X)}px;top:{Number(point.Y)}px";
    private string SectionStyle(GraphSection section)
    {
        var points = section.NodeIds.Select(id => Layout.Nodes[id].Position).ToArray();
        var left = Math.Max(0, points.Min(p => p.X) - 24);
        var top = Math.Max(0, points.Min(p => p.Y) - 64);
        return $"left:{Number(left)}px;top:{Number(top)}px;width:{Number(points.Max(p => p.X) + 304 - left)}px;height:{Number(points.Max(p => p.Y) + 600 - top)}px";
    }
    private static string PointsJson(WirePresentation route) => JsonSerializer.Serialize(route.Points, JsonOptions);
    private WirePresentation Route(string id) => Layout.Wires.GetValueOrDefault(id) ?? new("curve", []);
    private IEnumerable<CanvasConnection> CanvasConnections => WorkflowSchemaVersions.UsesExplicitGraph(Definition)
        ? Definition.Connections.Select(edge => new CanvasConnection($"data:{edge.Key}", edge.Value, "data"))
            .Concat((Definition.ExecutionConnections ?? ImmutableSortedDictionary<string, ConnectionDefinition>.Empty)
                .Select(edge => new CanvasConnection($"execution:{edge.Key}", edge.Value, "execution")))
        : Definition.Connections.Select(edge => new CanvasConnection(edge.Key, edge.Value, "data"));
    private CanvasConnection? FindConnection(string? id) => id is null ? null : CanvasConnections.FirstOrDefault(edge => edge.Id == id);
    private ImmutableArray<WorkflowPortDescriptor> Ports(NodeDefinition node) => PortResolver?.Invoke(node) ?? (WorkflowSchemaVersions.UsesExplicitGraph(Definition)
        ? WorkflowPorts.Explicit(node)
        : [.. WorkflowPorts.Inputs(node).Select(port => LegacyDataPort(port, WorkflowPortDirection.Input)),
           .. WorkflowPorts.Outputs(node).Select(port => LegacyDataPort(port, WorkflowPortDirection.Output))]);
    private static WorkflowPortDescriptor LegacyDataPort(WorkflowPort port, WorkflowPortDirection direction) =>
        new(port.Id, direction, WorkflowPortKind.Data, port.DataType)
        {
            Required = direction == WorkflowPortDirection.Input,
            Cardinality = direction == WorkflowPortDirection.Input
                ? WorkflowPortCardinality.ExactlyOne
                : WorkflowPortCardinality.Many
        };
    private string Title(string id) => string.IsNullOrWhiteSpace(Layout.Nodes[id].Label) ? FriendlyName(Definition.Nodes[id]) : Layout.Nodes[id].Label;
    private static string TooltipId(string id) => $"vaw-node-comment-{id}";

    private enum InputBindingStatus
    {
        Connected,
        Static,
        Optional,
        Missing,
        TooMany
    }

    private InputBindingStatus InputStatusKind(string nodeId, WorkflowPortDescriptor port)
    {
        var node = Definition.Nodes[nodeId];
        var incomingCount = Definition.Connections.Values.Count(connection =>
                connection.Target.NodeId == nodeId && connection.Target.PortId == port.Id);
        if (port.Cardinality != WorkflowPortCardinality.Many && incomingCount > 1)
            return InputBindingStatus.TooMany;
        if (incomingCount > 0)
            return InputBindingStatus.Connected;

        if (node.Type == NodeTypes.McpTool && port.Kind == WorkflowPortKind.Data &&
            WorkflowNodeDescriptorResolver.TryResolveMcpArguments(node, out var arguments) &&
            arguments.Any(argument => argument.Port.Id == port.Id && HasStaticArgument(node.McpArgumentsJson, argument.Argument.Name)))
            return InputBindingStatus.Static;

        return port.Required ? InputBindingStatus.Missing : InputBindingStatus.Optional;
    }

    private string InputStatusLabel(string nodeId, WorkflowPortDescriptor port) => InputStatusKind(nodeId, port) switch
    {
        InputBindingStatus.Connected => "Connected",
        InputBindingStatus.Static => "Static",
        InputBindingStatus.Missing => "Required · missing",
        InputBindingStatus.TooMany => "Too many",
        _ => "Optional"
    };

    private static bool HasStaticArgument(string? arguments, string name)
    {
        if (string.IsNullOrWhiteSpace(arguments)) return false;
        try
        {
            using var document = JsonDocument.Parse(arguments);
            return document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty(name, out _);
        }
        catch (JsonException) { return false; }
    }

    internal static string FriendlyName(NodeDefinition node) => node.Type switch
    {
        NodeTypes.Input => "Input", NodeTypes.Output => "Output", NodeTypes.Uppercase => "Uppercase", NodeTypes.Reverse => "Reverse", NodeTypes.TextTemplate => "Text template",
        NodeTypes.JsonProject => "JSON projection", NodeTypes.JsonSelectString => "JSON selector",
        NodeTypes.Condition => "Condition", NodeTypes.Merge => "Merge", NodeTypes.Approval => "Approval",
        NodeTypes.ParallelFork => "Parallel fork", NodeTypes.ParallelBranch => "Parallel branch", NodeTypes.ParallelJoin => "Parallel join", NodeTypes.ParallelRendezvous => "Parallel rendezvous",
        NodeTypes.Sequence => "Sequence",
        NodeTypes.Loop => "Bounded loop", NodeTypes.LoopBody => "Loop body", NodeTypes.Terminate => "Terminate workflow",
        NodeTypes.WorkflowCall => $"Workflow · {node.WorkflowCall?.WorkflowId} v{node.WorkflowCall?.Version}",
        NodeTypes.StringConstant => "String constant", NodeTypes.TextConcat => "Text concatenation",
        NodeTypes.IntegerConstant => "Integer constant", NodeTypes.NumberConstant => "Number constant", NodeTypes.IntegerAdd => "Add integers", NodeTypes.IntegerEquals => "Compare integers",
        NodeTypes.StringEquals => "Compare strings", NodeTypes.BooleanBranch => "Boolean Branch",
        NodeTypes.BooleanConstant => "Boolean constant", NodeTypes.BooleanNot => "Boolean NOT", NodeTypes.WorkflowValue => "Workflow value",
        NodeTypes.VariableGet => "Get variable", NodeTypes.VariableSet => "Set variable",
        NodeTypes.FunctionInput => "Function input", NodeTypes.FunctionOutput => "Function output",
        NodeTypes.FunctionCall => node.GraphFunction?.Definition.DisplayName is { Length: > 0 } name
            ? $"Function · {name} v{node.GraphFunction.Definition.Version}" : "Pure function",
        NodeTypes.Agent => "Agent", _ => "MCP tool"
    };
    private async Task SelectNode(string id)
    {
        if (!Definition.Nodes.ContainsKey(id)) return;
        if (selectedNode != id) inspectorTab = "configure";
        selectedNode = id;
        activeConnection = null;
        if (browserSession is not null) await browserSession.InvokeVoidAsync("select", id);
        await SelectedNodeIdChanged.InvokeAsync(id);
    }
    private static string Kind(NodeDefinition node) => node.Type == NodeTypes.Input ? "Input" : node.Type == NodeTypes.Output ? "Output" :
        node.Type == NodeTypes.Approval ? "Approval" : node.Type == NodeTypes.Condition ? "Condition" : node.Type == NodeTypes.Merge ? "Exclusive merge" :
        node.Type == NodeTypes.ParallelFork ? "Fork" : node.Type == NodeTypes.ParallelBranch ? "Branch" : node.Type == NodeTypes.ParallelJoin ? "Barrier join" : node.Type == NodeTypes.Sequence ? "Sequence" :
        node.Type == NodeTypes.Loop ? "Bounded loop" : node.Type == NodeTypes.LoopBody ? "Loop body" : node.Type == NodeTypes.Terminate ? "Successful exit · no result" :
        node.Type == NodeTypes.WorkflowCall ? "Pinned workflow call" : node.Type == NodeTypes.FunctionCall ? "Pure function" :
        node.Type is NodeTypes.FunctionInput or NodeTypes.FunctionOutput ? "Function boundary" :
        node.Type is NodeTypes.StringConstant or NodeTypes.TextConcat or NodeTypes.StringEquals or NodeTypes.IntegerConstant or NodeTypes.NumberConstant or NodeTypes.IntegerAdd or NodeTypes.IntegerEquals ? "Pure value" :
        node.Type == NodeTypes.BooleanBranch ? "Boolean routing" : node.Type is NodeTypes.BooleanConstant or NodeTypes.BooleanNot or NodeTypes.WorkflowValue ? "Pure value" :
        node.Type is NodeTypes.VariableGet or NodeTypes.VariableSet ? "Run-scoped state" :
        node.Agent is not null ? "Agent" : node.Tool is not null ? "MCP tool" : "Transform";

    private static string NodeIcon(string type)
    {
        var shape = type switch
        {
            NodeTypes.Input => "<path d=\"M4 4h12v12H4zM10 1v5m-3-2 3 2 3-2\"/>",
            NodeTypes.Output or NodeTypes.Terminate => "<path d=\"M4 10h11m-4-4 4 4-4 4\"/><circle cx=\"17\" cy=\"10\" r=\"2\"/>",
            NodeTypes.Agent => "<rect x=\"4\" y=\"5\" width=\"12\" height=\"11\" rx=\"3\"/><path d=\"M8 9h.01M12 9h.01M8 13h4M10 2v3m-7 4H4m12 0h3\"/>",
            NodeTypes.VariableGet or NodeTypes.VariableSet => "<path d=\"M4 5h12v10H4zM7 8h6M7 12h4\"/><path d=\"M2 10h2m12 0h2\"/>",
            NodeTypes.McpTool => "<path d=\"M10 2v5m-3-3h6M7 7v4a3 3 0 0 0 6 0V7m-3 7v4m-3 0h6\"/>",
            NodeTypes.Condition or NodeTypes.BooleanBranch or NodeTypes.Merge => "<path d=\"m10 2 8 8-8 8-8-8z\"/><path d=\"M10 6v5m0 3h.01\"/>",
            NodeTypes.Approval => "<path d=\"M3 10 8 15 17 5\"/><circle cx=\"10\" cy=\"10\" r=\"8\"/>",
            NodeTypes.ParallelFork or NodeTypes.ParallelBranch or NodeTypes.ParallelJoin or NodeTypes.ParallelRendezvous => "<circle cx=\"4\" cy=\"10\" r=\"1.5\"/><circle cx=\"16\" cy=\"4\" r=\"1.5\"/><circle cx=\"16\" cy=\"16\" r=\"1.5\"/><path d=\"M5.5 10h3a3 3 0 0 0 3-3V4m-3 6h3a3 3 0 0 1 3 3v1.5\"/>",
            NodeTypes.Sequence => "<path d=\"M3 5h4m-4 5h8m-8 5h12\"/><path d=\"m7 3 2 2-2 2m4 3 2 2-2 2m4 3 2 2-2 2\"/>",
            NodeTypes.Loop or NodeTypes.LoopBody => "<path d=\"M16 7h3V4m-1 3a8 8 0 0 0-14 2m0 6H1v3m1-3a8 8 0 0 0 14-2\"/>",
            NodeTypes.WorkflowCall or NodeTypes.FunctionCall => "<rect x=\"3\" y=\"3\" width=\"14\" height=\"14\" rx=\"2\"/><path d=\"M7 7h6m-6 3h6m-6 3h4\"/>",
            NodeTypes.StringConstant or NodeTypes.IntegerConstant or NodeTypes.NumberConstant or NodeTypes.BooleanConstant or NodeTypes.WorkflowValue => "<path d=\"M4 4h12v12H4zM7 8h6M7 12h4\"/>",
            NodeTypes.FunctionInput or NodeTypes.FunctionOutput => "<path d=\"M3 10h14m-5-5 5 5-5 5\"/><circle cx=\"4\" cy=\"10\" r=\"2\"/>",
            NodeTypes.JsonProject or NodeTypes.JsonSelectString => "<path d=\"M7 3H5v5l-2 2 2 2v5h2m6-14h2v5l2 2-2 2v5h-2M9 7h2m-2 3h2m-2 3h2\"/>",
            _ => "<path d=\"M4 5h12M4 10h12M4 15h8\"/><circle cx=\"2.5\" cy=\"5\" r=\".5\"/><circle cx=\"2.5\" cy=\"10\" r=\".5\"/><circle cx=\"2.5\" cy=\"15\" r=\".5\"/>"
        };
        return $"<svg viewBox=\"0 0 20 20\" focusable=\"false\">{shape}</svg>";
    }

    protected override void OnParametersSet()
    {
        WorkflowLayoutJson.Validate(Layout, Definition);
        if (ReadOnly && lastLayout is not null && !ReferenceEquals(lastLayout, Layout))
            revealUpdatedReadOnlyLayout = true;
        lastLayout = Layout;
        if (!ReferenceEquals(orderedDefinition, Definition))
        {
            Order = WorkflowGraph.DisplayOrder(Definition);
            orderedDefinition = Definition;
        }
        if (SelectedNodeId is not null && Definition.Nodes.ContainsKey(SelectedNodeId)) selectedNode = SelectedNodeId;
        if (selectedNode is null || !Definition.Nodes.ContainsKey(selectedNode)) selectedNode = Order[0];
        if (FindConnection(selectedWire) is null) selectedWire = CanvasConnections.Select(edge => edge.Id).FirstOrDefault();
        if (contextMenu is { } context && (context.NodeId is not null && !Definition.Nodes.ContainsKey(context.NodeId) ||
            context.ConnectionId is not null && FindConnection(context.ConnectionId) is null)) contextMenu = null;
        members.RemoveWhere(id => !Definition.Nodes.ContainsKey(id));
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (disposed) return;
        try
        {
            if (firstRender && Services.GetService(typeof(IJSRuntime)) is IJSRuntime js)
            {
                var assetVersion = typeof(WorkflowGraphCanvas).Assembly.ManifestModule.ModuleVersionId.ToString("N");
                module = await js.InvokeAsync<IJSObjectReference>("import", $"./_content/VisualAgentWorkflow.Blazor/graphCanvas.js?v={assetVersion}");
                if (disposed) { await module.DisposeAsync(); module = null; return; }
                callback = DotNetObjectReference.Create(this);
                var session = await module.InvokeAsync<IJSObjectReference>("attach", root, callback);
                if (disposed)
                {
                    await session.InvokeVoidAsync("dispose");
                    await session.DisposeAsync();
                    return;
                }
                browserSession = session;
                paneModule = await js.InvokeAsync<IJSObjectReference>("import", $"./_content/VisualAgentWorkflow.Blazor/paneLayout.js?v={assetVersion}");
                paneSession = await paneModule.InvokeAsync<IJSObjectReference>("attach", root);
                zoom = Math.Clamp(await browserSession.InvokeAsync<double>("center"), 0.25, 1.5);
                ready = true;
                StateHasChanged();
            }
            if (browserSession is not null)
            {
                await browserSession.InvokeVoidAsync("refresh");
                if (revealUpdatedReadOnlyLayout)
                {
                    revealUpdatedReadOnlyLayout = false;
                    zoom = Math.Clamp(await browserSession.InvokeAsync<double>("fit"), 0.25, 1.5);
                    StateHasChanged();
                }
            }
        }
        catch (JSDisconnectedException) { }
        catch (ObjectDisposedException) when (disposed) { }
        catch (JSException)
        { if (!disposed && error is null) { error = "Canvas gestures are unavailable. Use the node and wire coordinate controls below."; StateHasChanged(); } }
    }

    private async Task Emit(WorkflowLayout next)
    {
        if (!CanEdit) return;
        try
        {
            WorkflowLayoutJson.Validate(next, Definition);
            error = null;
            await LayoutChanged.InvokeAsync(next);
        }
        catch (ArgumentException) { error = "Invalid layout. Check coordinate bounds, labels, and section membership."; }
    }

    private void Zoom(double delta) => zoom = Math.Clamp(Math.Round(zoom + delta, 2), 0.25, 1.5);
    private async Task FitView()
    {
        if (browserSession is not null) zoom = Math.Clamp(await browserSession.InvokeAsync<double>("fit"), 0.25, 1.5);
    }
    private async Task ResetView()
    {
        if (browserSession is not null) zoom = Math.Clamp(await browserSession.InvokeAsync<double>("reset"), 0.25, 1.5);
    }

    public async Task<string[]> SelectedNodeIds()
    {
        if (browserSession is not null)
            return await browserSession.InvokeAsync<string[]>("selection");
        return selectedNode is { } id ? [id] : [];
    }

    [JSInvokable]
    public Task CopySelection(string[] ids) => ClipboardCopyRequested.InvokeAsync(ids);

    [JSInvokable]
    public Task PasteAt(double x, double y) => ClipboardPasteRequested.InvokeAsync(new GraphPoint(x, y));
    private Task AutoLayout()
    {
        var arranged = WorkflowLayout.Create(Definition);
        return Emit(arranged with
        {
            Nodes = arranged.Nodes.ToImmutableSortedDictionary(p => p.Key,
                p => Layout.Nodes[p.Key] with { Position = p.Value.Position }, StringComparer.Ordinal),
            Sections = Layout.Sections,
            Wires = Layout.Wires.ToImmutableSortedDictionary(p => p.Key, p => p.Value with { Points = [] }, StringComparer.Ordinal)
        });
    }

    [JSInvokable]
    public Task MoveNode(string id, double x, double y) => CanEdit && Layout.Nodes.TryGetValue(id, out var node)
        ? Emit(Layout with { Nodes = Layout.Nodes.SetItem(id, node with { Position = RelativeSnap(node.Position, x, y) }) }) : Task.CompletedTask;

    [JSInvokable]
    public async Task SelectNodes(string[] ids)
    {
        if (disposed) return;
        var first = ids.FirstOrDefault(id => Definition.Nodes.ContainsKey(id));
        if (first is not null)
        {
            selectedNode = first;
            activeConnection = null;
            await SelectedNodeIdChanged.InvokeAsync(first);
        }
        await InvokeAsync(StateHasChanged);
    }

    [JSInvokable]
    public Task MoveNodes(string[] ids, double dx, double dy)
    {
        if (!CanEdit || ids.Length == 0 || ids.Any(id => !Layout.Nodes.ContainsKey(id)) || !double.IsFinite(dx) || !double.IsFinite(dy)) return Task.CompletedTask;
        var selected = ids.Distinct(StringComparer.Ordinal).ToArray();
        var points = selected.Select(id => Layout.Nodes[id].Position).ToArray();
        dx = Math.Clamp(Math.Round(dx / 8) * 8, -points.Min(point => point.X), WorkflowLayoutJson.MaximumCoordinate - points.Max(point => point.X));
        dy = Math.Clamp(Math.Round(dy / 8) * 8, -points.Min(point => point.Y), WorkflowLayoutJson.MaximumCoordinate - points.Max(point => point.Y));
        var nodes = Layout.Nodes;
        foreach (var id in selected)
        {
            var node = nodes[id];
            nodes = nodes.SetItem(id, node with { Position = new(node.Position.X + dx, node.Position.Y + dy) });
        }
        return Emit(Layout with { Nodes = nodes });
    }

    [JSInvokable]
    public Task MoveSection(string id, double dx, double dy)
    {
        if (!CanEdit || !Layout.Sections.TryGetValue(id, out var section) || !double.IsFinite(dx) || !double.IsFinite(dy)) return Task.CompletedTask;
        var nodes = section.NodeIds.Select(nodeId => Layout.Nodes[nodeId].Position).ToArray();
        dx = Math.Clamp(Math.Round(dx / 8) * 8, -nodes.Min(p => p.X), WorkflowLayoutJson.MaximumCoordinate - nodes.Max(p => p.X));
        dy = Math.Clamp(Math.Round(dy / 8) * 8, -nodes.Min(p => p.Y), WorkflowLayoutJson.MaximumCoordinate - nodes.Max(p => p.Y));
        var next = Layout.Nodes;
        foreach (var nodeId in section.NodeIds)
        {
            var node = next[nodeId];
            next = next.SetItem(nodeId, node with { Position = new(node.Position.X + dx, node.Position.Y + dy) });
        }
        return Emit(Layout with { Nodes = next });
    }

    [JSInvokable]
    public Task AddWaypoint(string id, int index, double x, double y)
    {
        if (!CanEdit || FindConnection(id) is null) return Task.CompletedTask;
        var route = Route(id);
        if (index < 0 || index > route.Points.Length || route.Points.Length >= 32) return Task.CompletedTask;
        selectedWire = id;
        return Emit(Layout with { Wires = Layout.Wires.SetItem(id, route with { Points = route.Points.Insert(index, new(Snap(x, y))) }) });
    }

    [JSInvokable]
    public Task MoveWaypoint(string id, int index, double x, double y)
    {
        if (!CanEdit || !Layout.Wires.TryGetValue(id, out var route) || index < 0 || index >= route.Points.Length) return Task.CompletedTask;
        selectedWire = id;
        return Emit(Layout with { Wires = Layout.Wires.SetItem(id, route with
            { Points = route.Points.SetItem(index, route.Points[index] with { Position = RelativeSnap(route.Points[index].Position, x, y) }) }) });
    }

    [JSInvokable]
    public async Task SelectWire(string? id)
    {
        if (disposed) return;
        if (FindConnection(id) is { } connection)
        {
            selectedWire = id; activeConnection = id; selectedNode = connection.Definition.Source.NodeId;
            await ConnectionSelected.InvokeAsync(id);
        }
        await InvokeAsync(StateHasChanged);
    }

    [JSInvokable]
    public async Task OpenContextMenu(string? nodeId, string? connectionId, int segment, double x, double y, double clientX, double clientY,
        string? sourceNodeId = null, string? sourcePortId = null, string? targetPortId = null)
    {
        if (disposed || Disabled || !EnableContextMenu || ContextMenu is null || !double.IsFinite(x) || !double.IsFinite(y)) return;
        nodeId = nodeId is not null && Definition.Nodes.ContainsKey(nodeId) ? nodeId : null;
        sourceNodeId = sourceNodeId is not null && Definition.Nodes.ContainsKey(sourceNodeId) ? sourceNodeId : null;
        sourcePortId = sourceNodeId is null || string.IsNullOrWhiteSpace(sourcePortId) ? null : sourcePortId;
        targetPortId = nodeId is null || string.IsNullOrWhiteSpace(targetPortId) ? null : targetPortId;
        connectionId = FindConnection(connectionId) is not null ? connectionId : null;
        var targetNodeId = nodeId;
        if (connectionId is not null && FindConnection(connectionId) is { } connection)
        {
            // A wire context menu carries both endpoints so the editor can offer
            // typed Blueprint-style insertion actions without guessing from a
            // previously selected node or a stale drag gesture.
            nodeId = null;
            if (connection.Kind == "data")
            {
                sourceNodeId = connection.Definition.Source.NodeId;
                sourcePortId = connection.Definition.Source.PortId;
                targetNodeId = connection.Definition.Target.NodeId;
                targetPortId = connection.Definition.Target.PortId;
            }
            await SelectWire(connectionId);
        }
        else if (nodeId is not null) await SelectNode(nodeId);
        contextMenu = new(Guid.NewGuid().ToString("N"), nodeId, connectionId, segment, Snap(x, y), clientX, clientY,
            sourceNodeId, sourcePortId, targetNodeId, targetPortId);
        await InvokeAsync(StateHasChanged);
    }

    [JSInvokable]
    public Task CloseContextMenu(string id)
    {
        if (contextMenu?.Id == id) contextMenu = null;
        return disposed ? Task.CompletedTask : InvokeAsync(StateHasChanged);
    }

    [JSInvokable]
    public Task CreateDroppedNode(string key, string? nodeId, string? connectionId, double x, double y) =>
        disposed || Disabled || !EnableContextMenu || !double.IsFinite(x) || !double.IsFinite(y) ? Task.CompletedTask :
        NodeCreateRequested.InvokeAsync(new(key, new(Guid.NewGuid().ToString("N"), nodeId is not null && Definition.Nodes.ContainsKey(nodeId) ? nodeId : null,
            FindConnection(connectionId) is not null ? connectionId : null, 0, Snap(x, y), 0, 0)));

    [JSInvokable]
    public async Task ConnectPins(string sourceId, string sourcePort, string targetId, string targetPort)
    {
        if (disposed || Disabled || !CanEdit) return;
        try
        {
            await ConnectionRequested.InvokeAsync((sourceId, sourcePort, targetId, targetPort));
            pinHintText = "Connection updated.";
        }
        catch (ArgumentException exception)
        {
            pinHintText = exception.Message;
        }
        await InvokeAsync(StateHasChanged);
    }

    [JSInvokable]
    public Task DeleteSelection(string? focusedNodeId = null)
    {
        var target = focusedNodeId is not null && Definition.Nodes.ContainsKey(focusedNodeId) ? focusedNodeId : selectedNode;
        return !disposed && !Disabled && EnableContextMenu && target is not null
            ? DeleteNodeRequested.InvokeAsync(target) : Task.CompletedTask;
    }

    private static GraphPoint RelativeSnap(GraphPoint origin, double x, double y) => new(
        Math.Clamp(origin.X + Math.Round((x - origin.X) / 8) * 8, 0, WorkflowLayoutJson.MaximumCoordinate),
        Math.Clamp(origin.Y + Math.Round((y - origin.Y) / 8) * 8, 0, WorkflowLayoutJson.MaximumCoordinate));

    private static GraphPoint Snap(double x, double y) => new(
        Math.Clamp(Math.Round(x / 8) * 8, 0, WorkflowLayoutJson.MaximumCoordinate),
        Math.Clamp(Math.Round(y / 8) * 8, 0, WorkflowLayoutJson.MaximumCoordinate));

    private static bool Parse(ChangeEventArgs args, out double value) =>
        double.TryParse(args.Value?.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out value) && double.IsFinite(value);
    private Task Label(string id, ChangeEventArgs args) => Emit(Layout with
        { Nodes = Layout.Nodes.SetItem(id, Layout.Nodes[id] with { Label = args.Value?.ToString() ?? "" }) });
    private Task Comment(string id, ChangeEventArgs args) => Emit(Layout with
        { Nodes = Layout.Nodes.SetItem(id, Layout.Nodes[id] with { Comment = args.Value?.ToString() ?? "" }) });
    private Task ToggleCommentExpanded(string id) => Layout.Nodes.TryGetValue(id, out var node)
        ? Emit(Layout with { Nodes = Layout.Nodes.SetItem(id, node with { CommentExpanded = !node.CommentExpanded }) })
        : Task.CompletedTask;
    private Task Coordinate(string id, ChangeEventArgs args, bool x) => Parse(args, out var value)
        ? MoveNode(id, x ? value : Layout.Nodes[id].Position.X, x ? Layout.Nodes[id].Position.Y : value) : Task.CompletedTask;
    private void ToggleMember(string id, ChangeEventArgs args) { if (args.Value is true) members.Add(id); else members.Remove(id); }
    private async Task CreateSection()
    {
        if (members.Count == 0) return;
        var occupied = Layout.Sections.Values.SelectMany(s => s.NodeIds).ToHashSet(StringComparer.Ordinal);
        if (members.Any(occupied.Contains)) { error = "Ungroup nodes before assigning them to another section."; return; }
        await Emit(Layout with { Sections = Layout.Sections.Add("section-" + Guid.NewGuid().ToString("N"),
            new(sectionLabel, members.Order(StringComparer.Ordinal).ToImmutableArray())) });
        members.Clear();
    }
    private Task RenameSection(string id, ChangeEventArgs args) => Emit(Layout with
        { Sections = Layout.Sections.SetItem(id, Layout.Sections[id] with { Label = args.Value?.ToString() ?? "" }) });
    private Task RemoveSection(string id) => Emit(Layout with { Sections = Layout.Sections.Remove(id) });
    private Task Style(string id, ChangeEventArgs args) => Emit(Layout with { Wires = Layout.Wires.SetItem(id,
        Route(id) with { Style = args.Value?.ToString() ?? "curve", Points = Route(id).Points.Select(p => p with { Angle = null }).ToImmutableArray() }) });
    private Task AddMidpoint(string id)
    {
        if (FindConnection(id) is not { } connection) return Task.CompletedTask;
        var edge = connection.Definition;
        var from = Layout.Nodes[edge.Source.NodeId].Position;
        var to = Layout.Nodes[edge.Target.NodeId].Position;
        var count = Route(id).Points.Length;
        return AddWaypoint(id, count, (from.X + 280 + to.X) / 2, (from.Y + to.Y) / 2 + 96 + count * 40);
    }
    private Task ClearRoute(string id) => Emit(Layout with { Wires = Layout.Wires.Remove(id) });
    private Task RemoveWaypoint(string id, int index) => Emit(Layout with { Wires = Layout.Wires.SetItem(id,
        Route(id) with { Points = Route(id).Points.RemoveAt(index) }) });
    private Task WaypointCoordinate(string id, int index, ChangeEventArgs args, bool x) => Parse(args, out var value)
        ? MoveWaypoint(id, index, x ? value : Route(id).Points[index].Position.X, x ? Route(id).Points[index].Position.Y : value) : Task.CompletedTask;
    private Task Angle(string id, int index, ChangeEventArgs args)
    {
        var route = Route(id);
        double? angle = Parse(args, out var value) ? value : null;
        return Emit(Layout with { Wires = Layout.Wires.SetItem(id,
            route with { Points = route.Points.SetItem(index, route.Points[index] with { Angle = angle }) }) });
    }
    private static (int X, int Y) Delta(KeyboardEventArgs args) => args.Key switch
    {
        "ArrowLeft" => (-1, 0), "ArrowRight" => (1, 0), "ArrowUp" => (0, -1), "ArrowDown" => (0, 1), _ => (0, 0)
    };
    private Task NodeKey(string id, KeyboardEventArgs args)
    {
        var (x, y) = Delta(args);
        if (x == 0 && y == 0 || !Layout.Nodes.TryGetValue(id, out var node)) return Task.CompletedTask;
        var step = args.ShiftKey ? 40 : 8;
        return MoveNode(id, node.Position.X + x * step, node.Position.Y + y * step);
    }
    private Task SectionKey(string id, KeyboardEventArgs args)
    { var (x, y) = Delta(args); return x == 0 && y == 0 ? Task.CompletedTask : MoveSection(id, x * (args.ShiftKey ? 40 : 8), y * (args.ShiftKey ? 40 : 8)); }
    private Task WaypointKey(string id, int index, KeyboardEventArgs args)
    {
        var (x, y) = Delta(args);
        var points = Route(id).Points;
        if (x == 0 && y == 0 || index < 0 || index >= points.Length) return Task.CompletedTask;
        var point = points[index].Position;
        return MoveWaypoint(id, index, point.X + x * 8, point.Y + y * 8);
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed) return;
        disposed = true;
        if (module is not null)
        {
            try
            {
                // The session retains its root even after Blazor removes the element from the DOM.
                if (browserSession is not null)
                {
                    await browserSession.InvokeVoidAsync("dispose");
                    await browserSession.DisposeAsync();
                }
                if (paneSession is not null) { await paneSession.InvokeVoidAsync("dispose"); await paneSession.DisposeAsync(); }
                if (paneModule is not null) await paneModule.DisposeAsync();
                await module.DisposeAsync();
            }
            catch (JSDisconnectedException) { }
            catch (ObjectDisposedException) { }
        }
        callback?.Dispose();
    }
}
