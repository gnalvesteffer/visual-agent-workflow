using System.Net;
using System.Text;
using System.Text.Json;
using VisualAgentWorkflow.Blazor;
using VisualAgentWorkflow.Core;
using VisualAgentWorkflow.Observation.Http;
using Xunit;

namespace VisualAgentWorkflow.Hosting.Tests;

public sealed class ObservationClientTests
{
    [Theory]
    [InlineData("http://localhost/observation")]
    [InlineData("http://user:password@localhost/observation/")]
    [InlineData("http://localhost/observation/?token=secret")]
    [InlineData("http://localhost/observation/#fragment")]
    public void AmbiguousOrCredentialBearingBaseAddressesAreRejected(string address)
    {
        using var http = new HttpClient { BaseAddress = new Uri(address) };
        Assert.Throws<ArgumentException>(() => new HttpWorkflowRunObserver(http));
    }

    [Fact]
    public async Task InvalidIdentityAndSequenceAreRejectedWithoutAnyExecutionInfrastructure()
    {
        var revision = WorkflowRevision.Create(WorkflowTemplates.Create("uppercase"), "fixture");
        var id = Guid.NewGuid();
        var snapshot = new RunSnapshot(new(id, revision.RevisionId, null, "fixture", revision.CreatedAt, revision.ContentHash, revision.Definition, "abc"), []);
        foreach (var invalid in new[]
        {
            snapshot with { Header = snapshot.Header with { RunId = Guid.NewGuid() } },
            snapshot with { Header = snapshot.Header with { ContentHash = new string('0', 64) } },
            snapshot with { Events = [new(id, revision.RevisionId, 2, DateTimeOffset.UtcNow, ExecutionEventKinds.Started)] }
        })
        {
            using var http = Client(() => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(invalid, new JsonSerializerOptions(JsonSerializerDefaults.Web))) });
            await Assert.ThrowsAsync<InvalidDataException>(() => new HttpWorkflowRunObserver(http).GetRunAsync(id));
        }
    }

    [Fact]
    public async Task OversizedBodiesAndRedirectsAreRejected()
    {
        using var oversized = Client(() =>
        {
            var content = new StringContent("{}");
            content.Headers.ContentLength = HttpWorkflowRunObserver.MaximumResponseBytes + 1;
            return new(HttpStatusCode.OK) { Content = content };
        });
        await Assert.ThrowsAsync<InvalidDataException>(() => new HttpWorkflowRunObserver(oversized).ListRunsAsync());
        using var redirected = Client(() => new(HttpStatusCode.Redirect) { Headers = { Location = new Uri("https://unrelated.invalid/") } });
        await Assert.ThrowsAsync<HttpRequestException>(() => new HttpWorkflowRunObserver(redirected).ListRunsAsync());
    }

    private static HttpClient Client(Func<HttpResponseMessage> respond) => new(new Handler(respond)) { BaseAddress = new Uri("https://consumer.invalid/observation/") };
    private sealed class Handler(Func<HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            return Task.FromResult(respond());
        }
    }
}
