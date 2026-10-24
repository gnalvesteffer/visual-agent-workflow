using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using VisualAgentWorkflow.Blazor;
using VisualAgentWorkflow.Core;
using VisualAgentWorkflow.Observation.Http;
using Xunit;

namespace VisualAgentWorkflow.Hosting.Tests;

public sealed class ObservationApiTests
{
    [Fact]
    public async Task EndpointsAreAbsentUnlessExplicitlyMapped()
    {
        await using var fixture = await Consumer.StartAsync(enabled: false);
        using var response = await fixture.Http.GetAsync("runs");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task ConsumerPolicyProtectsDiscoveryPayloadsAndCursors()
    {
        await using var fixture = await Consumer.StartAsync();
        var run = await fixture.StartPausedAsync();
        foreach (var path in new[] { "runs", $"runs/{run.Header.RunId}", $"runs/{run.Header.RunId}/events", $"runs/{run.Header.RunId}/reconciliation" })
        {
            using var anonymous = await fixture.Http.GetAsync(path);
            Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
            fixture.Http.DefaultRequestHeaders.Add("X-Test-Actor", "other");
            using var denied = await fixture.Http.GetAsync(path);
            Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
            fixture.Http.DefaultRequestHeaders.Remove("X-Test-Actor");
        }
        Assert.Equal("paused", (await fixture.Runtime.GetRunAsync(run.Header.RunId))!.Status);
    }

    [Fact]
    public async Task RemoteSnapshotsAndCursorPagesFollowSameRunWithoutExecutingOrControllingIt()
    {
        await using var fixture = await Consumer.StartAsync();
        var started = await fixture.StartPausedAsync();
        fixture.Http.DefaultRequestHeaders.Add("X-Test-Actor", "observer");
        var observer = new HttpWorkflowRunObserver(fixture.Http);
        var summary = Assert.Single(await observer.ListRunsAsync());
        Assert.Equal(started.Header.RunId, summary.RunId);
        using (var response = await fixture.Http.GetAsync("runs"))
        {
            Assert.DoesNotContain("private fixture input", await response.Content.ReadAsStringAsync());
            Assert.Contains("no-store", response.Headers.CacheControl!.ToString());
        }
        var snapshot = (await observer.GetRunAsync(summary.RunId))!;
        Assert.Equal("paused", snapshot.Status);
        var assessment = (await observer.GetReconciliationAsync(summary.RunId))!;
        Assert.Equal(snapshot.Events.Length, assessment.LastSequence);
        Assert.False(assessment.CanResume);
        using (var response = await fixture.Http.GetAsync($"runs/{summary.RunId}/reconciliation"))
        {
            Assert.DoesNotContain("private fixture input", await response.Content.ReadAsStringAsync());
            Assert.Contains("no-store", response.Headers.CacheControl!.ToString());
        }
        Assert.Equal("private fixture input", snapshot.Header.Input);
        Assert.DoesNotContain(snapshot.Events, e => e.Kind == ExecutionEventKinds.NodeStarted);
        var eventsBeforeRead = snapshot.Events.Length;
        await observer.GetRunAsync(summary.RunId);
        Assert.Equal(eventsBeforeRead, (await fixture.Runtime.GetRunAsync(summary.RunId))!.Events.Length);
        await fixture.Runtime.ResumeRunAsync(summary.RunId); // Consumer code, not observation API.
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        do
        {
            snapshot = (await observer.GetRunAsync(summary.RunId, deadline.Token))!;
            if (!snapshot.IsTerminal) await Task.Delay(10, deadline.Token);
        } while (!snapshot.IsTerminal);
        Assert.Equal("completed", snapshot.Status);
        Assert.Equal(started.Header.RevisionId, snapshot.Header.RevisionId);
        var recorded = new List<ExecutionEvent>();
        long cursor = 0;
        while (true)
        {
            var page = (await observer.GetEventsAsync(summary.RunId, cursor, limit: 2))!;
            recorded.AddRange(page.Events);
            cursor += page.Events.Length;
            if (!page.HasMore) break;
        }
        Assert.Equal(snapshot.Events, recorded);
        Assert.Empty((await observer.GetEventsAsync(summary.RunId, cursor))!.Events);
        Assert.Null(await observer.GetRunAsync(Guid.NewGuid()));
        Assert.Null(await observer.GetReconciliationAsync(Guid.NewGuid()));
        Assert.Null(await observer.GetEventsAsync(Guid.NewGuid(), 0));
        foreach (var suffix in new[] { "events?after=-1", "events?limit=257", "events?limit=0", "events?after=9999" })
        {
            using var response = await fixture.Http.GetAsync($"runs/{summary.RunId}/{suffix}");
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }
        foreach (var suffix in new[] { "", "/reconciliation", "/pause", "/resume", "/cancel", $"/approvals/{Guid.NewGuid()}/decision" })
        {
            using var response = await fixture.Http.PostAsync($"runs/{summary.RunId}{suffix}", null);
            Assert.True(response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed);
        }
        Assert.Equal(snapshot.Events.ToArray(), (await fixture.Runtime.GetRunAsync(summary.RunId))!.Events.ToArray());
    }

    [Fact]
    public async Task SnapshotOnlyObserversRemainUsableWhenAssessmentIsUnsupported()
    {
        await using var fixture = await Consumer.StartAsync(reconciliation: false);
        var run = await fixture.StartPausedAsync();
        fixture.Http.DefaultRequestHeaders.Add("X-Test-Actor", "observer");
        var observer = new HttpWorkflowRunObserver(fixture.Http);
        Assert.NotNull(await observer.GetRunAsync(run.Header.RunId));
        await Assert.ThrowsAsync<NotSupportedException>(() => observer.GetReconciliationAsync(run.Header.RunId));
        Assert.Equal("paused", (await fixture.Runtime.GetRunAsync(run.Header.RunId))!.Status);
    }

    private sealed class SnapshotOnlyObserver(IWorkflowRunObserver inner) : IWorkflowRunObserver
    {
        public Task<IReadOnlyList<RunSummary>> ListRunsAsync(CancellationToken cancellationToken = default) => inner.ListRunsAsync(cancellationToken);
        public Task<RunSnapshot?> GetRunAsync(Guid runId, CancellationToken cancellationToken = default) => inner.GetRunAsync(runId, cancellationToken);
        public Task<RunEventPage?> GetEventsAsync(Guid runId, long afterSequence, int limit = 256, CancellationToken cancellationToken = default) =>
            inner.GetEventsAsync(runId, afterSequence, limit, cancellationToken);
    }

    private sealed class Consumer : IAsyncDisposable
    {
        private readonly WebApplication app;
        private readonly string directory;
        public HttpClient Http { get; }
        public LocalWorkflowRuntime Runtime => app.Services.GetRequiredService<LocalWorkflowRuntime>();
        private Consumer(WebApplication app, string directory, HttpClient http) => (this.app, this.directory, Http) = (app, directory, http);
        public static async Task<Consumer> StartAsync(bool enabled = true, bool reconciliation = true)
        {
            var directory = Path.Combine(Path.GetTempPath(), "vaw-observer-" + Guid.NewGuid());
            var builder = WebApplication.CreateBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Services.AddLocalWorkflowRuntime(directory);
            builder.Services.AddSingleton<IWorkflowRunObserver>(services =>
            {
                var observer = new JournalRunObserver(services.GetRequiredService<FileRunJournal>());
                return reconciliation ? observer : new SnapshotOnlyObserver(observer);
            });
            builder.Services.AddAuthentication("test").AddScheme<AuthenticationSchemeOptions, TestAuthentication>("test", _ => { });
            builder.Services.AddAuthorization(options => options.AddPolicy("observe", p => p.RequireAuthenticatedUser().RequireClaim("scope", "observe")));
            var app = builder.Build();
            app.UseAuthentication(); app.UseAuthorization();
            if (enabled) app.MapWorkflowObservation("/observation", "observe");
            await app.StartAsync();
            var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { BaseAddress = new Uri(app.Urls.Single() + "/observation/") };
            return new(app, directory, http);
        }
        public Task<RunSnapshot> StartPausedAsync() => Runtime.StartPausedRunAsync(PipelineTemplates.Create("uppercase"), "private fixture input");
        public async ValueTask DisposeAsync()
        {
            Http.Dispose();
            await app.StopAsync();
            await app.DisposeAsync();
            Directory.Delete(directory, true);
        }
    }

    private sealed class TestAuthentication(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var actor = Request.Headers["X-Test-Actor"].ToString();
            if (actor.Length == 0) return Task.FromResult(AuthenticateResult.NoResult());
            var identity = new ClaimsIdentity([new Claim("scope", actor == "observer" ? "observe" : "other")], "test");
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), "test")));
        }
    }
}
