using System.Collections.Immutable;
using VisualAgentWorkflow.Core;
using Xunit;

namespace VisualAgentWorkflow.AgentFramework.Tests;

/// <summary>Per-frame late-event suppression on the frame owner (Decision 0070).</summary>
public sealed class StructuredActivationFrameCloseTests
{
    private static (WorkflowRunner.StructuredActivationFrameOwner Owner, List<ExecutionEvent> Events) NewOwner()
    {
        var definition = new WorkflowDefinition(WorkflowSchemaVersions.CurrentAuthoringSchema, "close-frame", "input",
            new Dictionary<string, NodeDefinition> { ["input"] = new(NodeTypes.Input, "1.0.0") }
                .ToImmutableSortedDictionary(),
            ImmutableSortedDictionary<string, ConnectionDefinition>.Empty);
        var owner = new WorkflowRunner.StructuredActivationFrameOwner(null, null, definition);
        var events = new List<ExecutionEvent>();
        owner.BindRootEventRecorder(Guid.NewGuid(), Guid.NewGuid(), evt => { events.Add(evt); return ValueTask.CompletedTask; },
            new ExecutionActivationLedger());
        return (owner, events);
    }

    private static ValueTask Record(WorkflowRunner.StructuredActivationFrameOwner owner, string? scope) =>
        owner.RecordEventAsync(ExecutionEventKinds.NodeStarted, "n", Guid.NewGuid(), scopeId: scope);

    [Fact]
    public async Task ClosedFrameDropsItselfAndDescendantsOnly()
    {
        var (owner, events) = NewOwner();
        using var _ = owner;
        owner.CloseFrame("outer/2");
        Assert.True(owner.IsFrameClosed("outer/2"));
        Assert.True(owner.IsFrameClosed("outer/2/inner/1"));
        Assert.False(owner.IsFrameClosed("outer/20"));
        Assert.False(owner.IsFrameClosed("outer/1"));
        Assert.False(owner.IsFrameClosed("outer"));
        Assert.False(owner.IsFrameClosed(null));

        foreach (var scope in new string?[] { "outer/2", "outer/2/inner/1", "outer/3", "outer/20", "outer/1/inner/1", "outer", null })
            await Record(owner, scope);

        Assert.Equal(new string?[] { "outer/3", "outer/20", "outer/1/inner/1", "outer", null },
            events.Select(evt => evt.ScopeId));
    }

    [Fact]
    public async Task RunLevelClosureStillDropsEverything()
    {
        var (owner, events) = NewOwner();
        using var _ = owner;
        await Record(owner, null);
        owner.CloseLateEvents();
        await Record(owner, null);
        await Record(owner, "outer/1");
        Assert.Single(events);
        Assert.False(owner.IsFrameClosed("outer/1"));
    }
}
