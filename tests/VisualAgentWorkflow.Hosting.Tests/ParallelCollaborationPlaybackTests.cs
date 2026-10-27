using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using VisualAgentWorkflow.Blazor.Components;
using VisualAgentWorkflow.Core;
using Xunit;

namespace VisualAgentWorkflow.Hosting.Tests;

public sealed class ParallelCollaborationPlaybackTests
{
    [Fact]
    public async Task RendersVersionOneCollaborationEvidenceAndExactRecordedValues()
    {
        var regionInvocation = Guid.NewGuid();
        var events = new[]
        {
            Event(1, ExecutionEventKinds.ParallelCollaborationRegionStarted,
                $$"""{"version":1,"regionNodeId":"region","forkNodeId":"fork","joinNodeId":"join","regionInvocationId":"{{regionInvocation}}","timeoutSeconds":45,"deadline":"2026-10-26T14:00:00+00:00"}"""),
            Event(2, ExecutionEventKinds.ParallelCollaborationMessageSent,
                $$"""{"version":1,"regionInvocationId":"{{regionInvocation}}","sequence":1,"branchId":"researcher","channel":"findings","content":"Found three sources."}"""),
            Event(3, ExecutionEventKinds.ParallelCollaborationArrival,
                $$"""{"version":1,"regionInvocationId":"{{regionInvocation}}","branchId":"researcher","arrivedCount":1,"participantCount":2,"released":false}"""),
            Event(4, ExecutionEventKinds.ParallelCollaborationReleased,
                """{"version":1,"regionNodeId":"region","forkNodeId":"fork","joinNodeId":"join","regionInvocationId":"00000000-0000-0000-0000-000000000001","sha256":"abc123","utf8Bytes":38,"canonicalJson":"{\"findings\":[\"source A\"]}"}"""),
            Event(5, ExecutionEventKinds.ParallelCollaborationMessagesRead,
                """{"version":1,"regionInvocationId":"00000000-0000-0000-0000-000000000001","branchId":"reviewer","channel":"findings","snapshotSha256":"abc123","viewJson":"[{\"content\":\"source A\"}]"}"""),
            Event(6, ExecutionEventKinds.ParallelCollaborationAborted,
                """{"version":1,"regionNodeId":"region","forkNodeId":"fork","joinNodeId":"join","regionInvocationId":"00000000-0000-0000-0000-000000000001","reason":"branch timed out","released":true,"snapshotSha256":"abc123"}""")
        };

        var html = await Render(events);

        Assert.Contains("Parallel collaboration", html);
        Assert.Contains("Effective timeout", html);
        Assert.Contains("45 seconds", html);
        Assert.Contains("Deadline", html);
        Assert.Contains("researcher", html);
        Assert.Contains("findings", html);
        Assert.Contains("Found three sources.", html);
        Assert.Contains("Arrived branches", html);
        Assert.Contains("Snapshot released", html);
        Assert.Contains("abc123", html);
        Assert.Contains("38 bytes", html);
        Assert.Contains("Digest is shown as recorded; this view does not recompute it.", html);
        Assert.Contains("Exact recorded snapshot value", html);
        Assert.Contains("source A", html);
        Assert.Contains("Exact recorded read result", html);
        Assert.Contains("reviewer", html);
        Assert.Contains("branch timed out", html);
        Assert.Contains("Abort records the region outcome", html);
    }

    [Fact]
    public async Task UnsupportedAndMalformedPayloadsAreExplicitlyUnavailable()
    {
        var events = new[]
        {
            Event(1, ExecutionEventKinds.ParallelCollaborationMessageSent,
                """{"version":3,"content":"must not be presented as supported"}"""),
            Event(2, ExecutionEventKinds.ParallelCollaborationReleased, "{not-json"),
            Event(3, ExecutionEventKinds.ParallelCollaborationArrival,
                """{"version":1,"regionInvocationId":"00000000-0000-0000-0000-000000000001","branchId":"branch"}""")
        };

        var html = await Render(events);

        Assert.Contains("payload version 3 is unsupported", html);
        Assert.Contains("the payload is not valid JSON", html);
        Assert.Contains("one or more required arrival fields are missing or malformed", html);
        Assert.Equal(3, Count(html, "Evidence unavailable or incomplete:"));
        Assert.DoesNotContain("must not be presented as supported", html);
    }

    [Fact]
    public async Task RendersBothRecordedPhasesAndRetainsPhaseOneEvidenceAfterLaterAbort()
    {
        var invocation = Guid.NewGuid();
        var events = new[]
        {
            Event(1, ExecutionEventKinds.ParallelCollaborationRegionStarted,
                $$"""{"version":2,"regionNodeId":"r1","secondRendezvousNodeId":"r2","forkNodeId":"fork","joinNodeId":"join","regionInvocationId":"{{invocation}}","timeoutSeconds":30,"deadline":"2026-10-26T14:00:00+00:00"}"""),
            Event(2, ExecutionEventKinds.ParallelCollaborationReleased,
                $$"""{"version":2,"phase":"phase1","regionNodeId":"r1","forkNodeId":"fork","joinNodeId":"join","regionInvocationId":"{{invocation}}","sha256":"phase-one-hash","utf8Bytes":2,"canonicalJson":"{}"}"""),
            Event(3, ExecutionEventKinds.ParallelCollaborationMessagesRead,
                $$"""{"version":2,"phase":"phase1","regionInvocationId":"{{invocation}}","branchId":"a","channel":"work","snapshotSha256":"phase-one-hash","viewJson":"[]"}"""),
            Event(4, ExecutionEventKinds.ParallelCollaborationReleased,
                $$"""{"version":2,"phase":"phase2","regionNodeId":"r2","forkNodeId":"fork","joinNodeId":"join","regionInvocationId":"{{invocation}}","sha256":"phase-two-hash","utf8Bytes":2,"canonicalJson":"{}"}"""),
            Event(5, ExecutionEventKinds.ParallelCollaborationAborted,
                $$"""{"version":2,"phase":"phase2","regionNodeId":"r2","forkNodeId":"fork","joinNodeId":"join","regionInvocationId":"{{invocation}}","reason":"branch failed","released":true,"snapshotSha256":"phase-two-hash","releasedPhases":["phase1","phase2"]}""")
        };

        var html = await Render(events);

        Assert.Contains("Second rendezvous", html);
        Assert.Contains("Phase 1", html);
        Assert.Contains("Phase 2", html);
        Assert.Contains("phase-one-hash", html);
        Assert.Contains("phase-two-hash", html);
        Assert.Contains("Released phases", html);
        Assert.Contains("branch failed", html);
    }

    [Fact]
    public async Task OmitsCollaborationSectionWhenSelectedReplayPrefixHasNoCollaborationEvents()
    {
        var html = await Render([Event(1, ExecutionEventKinds.NodeStarted, "{}")]);
        Assert.DoesNotContain("Parallel collaboration", html);
    }

    private static ExecutionEvent Event(long sequence, string kind, string? data) =>
        new(Guid.NewGuid(), Guid.NewGuid(), sequence, DateTimeOffset.Parse("2026-10-26T12:00:00Z"), kind, Data: data);

    private static async Task<string> Render(IEnumerable<ExecutionEvent> events)
    {
        using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        return await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var component = await renderer.RenderComponentAsync<ParallelCollaborationPlayback>(ParameterView.FromDictionary(
                new Dictionary<string, object?> { [nameof(ParallelCollaborationPlayback.Events)] = events }));
            return component.ToHtmlString();
        });
    }

    private static int Count(string value, string match)
    {
        var count = 0;
        var offset = 0;
        while ((offset = value.IndexOf(match, offset, StringComparison.Ordinal)) >= 0)
        {
            count++;
            offset += match.Length;
        }
        return count;
    }
}
