using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using VisualAgentWorkflow.Core;

namespace VisualAgentWorkflow.Blazor.Components;

public partial class WorkflowGraphCanvas
{
    [Parameter, EditorRequired] public WorkflowDefinition Definition { get; set; } = default!;
    [Parameter, EditorRequired] public WorkflowLayout Layout { get; set; } = default!;
    [Parameter, EditorRequired] public RenderFragment<string> NodeContent { get; set; } = default!;
    [Parameter] public RenderFragment? NodeLibrary { get; set; }
    [Parameter] public bool ShowNodeLibrary { get; set; } = true;
    [Parameter] public RenderFragment<GraphContextRequest>? ContextMenu { get; set; }
    [Parameter] public bool EnableContextMenu { get; set; } = true;
    [Parameter] public EventCallback<GraphNodeCreateRequest> NodeCreateRequested { get; set; }
    [Parameter] public EventCallback<string> DeleteNodeRequested { get; set; }
    [Parameter] public EventCallback<string> ConnectionSelected { get; set; }
    [Parameter] public EventCallback<string> ConnectionDisconnectRequested { get; set; }
    [Parameter] public EventCallback<(string SourceId, string SourcePort, string TargetId, string TargetPort)> ConnectionRequested { get; set; }
    private GraphContextRequest? contextMenu;
    private string? activeConnection;
    [Parameter] public string InspectorTitle { get; set; } = "Node properties";
    [Parameter] public RenderFragment<string>? NodeInspector { get; set; }
    [Parameter] public RenderFragment? ToolbarContent { get; set; }
    [Parameter] public string? SelectedNodeId { get; set; }
    [Parameter] public EventCallback<string> SelectedNodeIdChanged { get; set; }
    [Parameter] public EventCallback<WorkflowLayout> LayoutChanged { get; set; }
    [Parameter] public bool ReadOnly { get; set; }
    [Parameter] public bool Disabled { get; set; }
    [Parameter] public string NodesLabel { get; set; } = "Editable pipeline nodes";
    [Parameter] public Func<string, string>? NodeClass { get; set; }
    [Parameter] public IReadOnlySet<string>? DeliveredConnections { get; set; }
    [Inject] private IServiceProvider Services { get; set; } = default!;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private ElementReference root;
    private IJSObjectReference? module;
    private IJSObjectReference? browserSession;
    private DotNetObjectReference<WorkflowGraphCanvas>? callback;
    private bool disposed;
    private bool ready;
    private double zoom = 1;
    private string? selectedNode;
    private string? selectedWire;
    private string sectionLabel = "New section";
    private HashSet<string> members = new(StringComparer.Ordinal);
    private string? error;
    private string pinHintText = "Drag an output pin to a compatible input pin, or release on empty canvas to create a step.";
    private WorkflowDefinition? orderedDefinition;
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
    private string Title(string id) => string.IsNullOrWhiteSpace(Layout.Nodes[id].Label) ? FriendlyName(Definition.Nodes[id]) : Layout.Nodes[id].Label;
    internal static string FriendlyName(NodeDefinition node) => node.Type switch
    {
        NodeTypes.Input => "Input", NodeTypes.Output => "Output", NodeTypes.Uppercase => "Uppercase", NodeTypes.Reverse => "Reverse", NodeTypes.TextTemplate => "Text template",
        NodeTypes.JsonProject => "JSON projection", NodeTypes.JsonSelectString => "JSON selector",
        NodeTypes.Condition => "Condition", NodeTypes.Merge => "Merge", NodeTypes.Approval => "Approval",
        NodeTypes.ParallelFork => "Parallel fork", NodeTypes.ParallelBranch => "Parallel branch", NodeTypes.ParallelJoin => "Parallel join",
        NodeTypes.Loop => "Bounded loop", NodeTypes.LoopBody => "Loop body",
        NodeTypes.Agent => "Agent", _ => "MCP tool"
    };
    private async Task SelectNode(string id)
    {
        if (!Definition.Nodes.ContainsKey(id)) return;
        selectedNode = id;
        activeConnection = null;
        await SelectedNodeIdChanged.InvokeAsync(id);
    }
    private static string Kind(NodeDefinition node) => node.Type == NodeTypes.Input ? "Input" : node.Type == NodeTypes.Output ? "Output" :
        node.Type == NodeTypes.Approval ? "Approval" : node.Type == NodeTypes.Condition ? "Condition" : node.Type == NodeTypes.Merge ? "Exclusive merge" :
        node.Type == NodeTypes.ParallelFork ? "Fork" : node.Type == NodeTypes.ParallelBranch ? "Branch" : node.Type == NodeTypes.ParallelJoin ? "Barrier join" :
        node.Type == NodeTypes.Loop ? "Bounded loop" : node.Type == NodeTypes.LoopBody ? "Loop body" :
        node.Agent is not null ? "Agent" : node.Tool is not null ? "MCP tool" : "Transform";

    protected override void OnParametersSet()
    {
        WorkflowLayoutJson.Validate(Layout, Definition);
        if (!ReferenceEquals(orderedDefinition, Definition))
        {
            Order = WorkflowGraph.DisplayOrder(Definition);
            orderedDefinition = Definition;
        }
        if (SelectedNodeId is not null && Definition.Nodes.ContainsKey(SelectedNodeId)) selectedNode = SelectedNodeId;
        if (selectedNode is null || !Definition.Nodes.ContainsKey(selectedNode)) selectedNode = Order[0];
        if (selectedWire is null || !Definition.Connections.ContainsKey(selectedWire)) selectedWire = Definition.Connections.Keys.FirstOrDefault();
        if (contextMenu is { } context && (context.NodeId is not null && !Definition.Nodes.ContainsKey(context.NodeId) ||
            context.ConnectionId is not null && !Definition.Connections.ContainsKey(context.ConnectionId))) contextMenu = null;
        members.RemoveWhere(id => !Definition.Nodes.ContainsKey(id));
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (disposed) return;
        try
        {
            if (firstRender && Services.GetService(typeof(IJSRuntime)) is IJSRuntime js)
            {
                module = await js.InvokeAsync<IJSObjectReference>("import", "./_content/VisualAgentWorkflow.Blazor/graphCanvas.js");
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
                zoom = Math.Clamp(await browserSession.InvokeAsync<double>("fit"), 0.25, 1.5);
                ready = true;
                StateHasChanged();
            }
            if (browserSession is not null) await browserSession.InvokeVoidAsync("refresh");
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
        if (!CanEdit || !Definition.Connections.ContainsKey(id)) return Task.CompletedTask;
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
        if (id is not null && Definition.Connections.TryGetValue(id, out var edge))
        {
            selectedWire = id; activeConnection = id; selectedNode = edge.Source.NodeId;
            await ConnectionSelected.InvokeAsync(id);
        }
        await InvokeAsync(StateHasChanged);
    }

    [JSInvokable]
    public async Task OpenContextMenu(string? nodeId, string? connectionId, int segment, double x, double y, double clientX, double clientY)
    {
        if (disposed || Disabled || !EnableContextMenu || ContextMenu is null || !double.IsFinite(x) || !double.IsFinite(y)) return;
        nodeId = nodeId is not null && Definition.Nodes.ContainsKey(nodeId) ? nodeId : null;
        connectionId = connectionId is not null && Definition.Connections.ContainsKey(connectionId) ? connectionId : null;
        if (connectionId is not null) await SelectWire(connectionId);
        else if (nodeId is not null) await SelectNode(nodeId);
        contextMenu = new(Guid.NewGuid().ToString("N"), nodeId, connectionId, segment, Snap(x, y), clientX, clientY);
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
            connectionId is not null && Definition.Connections.ContainsKey(connectionId) ? connectionId : null, 0, Snap(x, y), 0, 0)));

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
    public Task DeleteSelection() => !disposed && !Disabled && EnableContextMenu && selectedNode is not null ? DeleteNodeRequested.InvokeAsync(selectedNode) : Task.CompletedTask;

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
        var edge = Definition.Connections[id];
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
                await module.DisposeAsync();
            }
            catch (JSDisconnectedException) { }
            catch (ObjectDisposedException) { }
        }
        callback?.Dispose();
    }
}
