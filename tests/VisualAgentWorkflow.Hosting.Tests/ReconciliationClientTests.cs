using System.Net;
using System.Text.Json;
using VisualAgentWorkflow.Core;
using VisualAgentWorkflow.Observation.Http;
using Xunit;

namespace VisualAgentWorkflow.Hosting.Tests;

public sealed class ReconciliationClientTests
{
    private static RunReconciliation Report(Guid id) => new(id, Guid.NewGuid(), new string('a', 64), 1, "completed",
        RunRecoveryDisposition.Terminal, false, "Terminal evidence.", null, [], 0, false);

    [Fact]
    public async Task UnsupportedSourcesAndMissingRunsRemainDistinct()
    {
        using var unsupported = Client(() => new(HttpStatusCode.NotImplemented));
        await Assert.ThrowsAsync<NotSupportedException>(() => new HttpWorkflowRunObserver(unsupported).GetReconciliationAsync(Guid.NewGuid()));
        using var missing = Client(() => new(HttpStatusCode.NotFound));
        Assert.Null(await new HttpWorkflowRunObserver(missing).GetReconciliationAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task InvalidIdentityCursorAndRecoveryClaimsAreRejected()
    {
        var id = Guid.NewGuid();
        var valid = Report(id);
        foreach (var report in new[]
        {
            valid with { RunId = Guid.NewGuid() }, valid with { LastSequence = -1 }, valid with { ContentHash = "invalid" },
            valid with { CanResume = true }, valid with { Disposition = RunRecoveryDisposition.SavedSuspension, CanResume = true },
            valid with { UnresolvedWork = [new(UnresolvedWorkKind.Model, UnresolvedWorkState.AwaitingOutcome, "node", Guid.NewGuid(), Guid.NewGuid(), 2, 2)], TotalUnresolvedWork = 1 },
            valid with { TotalUnresolvedWork = 300 }
        })
        {
            using var http = Client(() => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(report, JsonSerializerOptions.Web)) });
            await Assert.ThrowsAsync<InvalidDataException>(() => new HttpWorkflowRunObserver(http).GetReconciliationAsync(id));
        }
        using var success = Client(() => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(valid, JsonSerializerOptions.Web)) });
        Assert.Equal(id, (await new HttpWorkflowRunObserver(success).GetReconciliationAsync(id))!.RunId);
        // Existing workflow contracts allow long node identities; the transport budget, not an invented ID cap, bounds them.
        var longNode = valid with { Disposition = RunRecoveryDisposition.UnresolvedWork,
            UnresolvedWork = [new(UnresolvedWorkKind.Node, UnresolvedWorkState.AwaitingOutcome, new string('n', 300), Guid.NewGuid(), null, 1, 1)], TotalUnresolvedWork = 1 };
        using var longIdentity = Client(() => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(longNode, JsonSerializerOptions.Web)) });
        Assert.Equal(300, (await new HttpWorkflowRunObserver(longIdentity).GetReconciliationAsync(id))!.UnresolvedWork[0].NodeId!.Length);
    }

    private static HttpClient Client(Func<HttpResponseMessage> respond) => new(new Handler(respond)) { BaseAddress = new Uri("https://consumer.invalid/observation/") };
    private sealed class Handler(Func<HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.EndsWith("/reconciliation", request.RequestUri!.AbsolutePath);
            return Task.FromResult(respond());
        }
    }
}
