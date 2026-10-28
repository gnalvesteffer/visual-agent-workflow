using VisualAgentWorkflow.Core;

namespace VisualAgentWorkflow.Blazor;

/// <summary>One authoring edit. UI composition only; not an execution or wire-format contract.</summary>
public sealed record WorkflowGraphEdit(WorkflowDefinition Definition, WorkflowLayout Layout);
public sealed record WorkflowNodeLibraryEntry(string Key, string Label, string Category, string Description, bool Enabled = true)
{
    /// <summary>Non-authoritative family metadata supplied by the Core contract catalog.</summary>
    public WorkflowNodeEffectFlags Effects { get; init; }
    /// <summary>Editor-neutral capabilities supplied by the Core contract catalog.</summary>
    public WorkflowNodeAuthoringFlags Authoring { get; init; }
}
public sealed record GraphContextRequest(string Id, string? NodeId, string? ConnectionId, int Segment,
    GraphPoint Position, double ClientX, double ClientY, string? SourceNodeId = null, string? SourcePortId = null,
    string? TargetNodeId = null, string? TargetPortId = null);
public sealed record GraphNodeCreateRequest(string Key, GraphContextRequest Context);
