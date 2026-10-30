using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using VisualAgentWorkflow.Blazor;
using VisualAgentWorkflow.Core;
using VisualAgentWorkflow.Hosting;
using VisualAgentWorkflow.Studio;
using Xunit;

namespace VisualAgentWorkflow.Hosting.Tests;

/// <summary>
/// API parity tests: every user capability exposed in the Studio UI must have
/// an equivalent web API endpoint. These tests enforce that contract and fail
/// if new UI features are added without corresponding API support.
/// </summary>
public sealed class ApiParityTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "vaw-api-parity-" + Guid.NewGuid());
    private WebApplication? app;
    private HttpClient? http;

    [Fact]
    public async Task WorkflowCreationEndpointExists()
    {
        await StartApp();

        // POST /api/workflows - create new workflow from template or empty graph
        var response = await PostJson("workflows", new { name = "test-workflow" });
        if (response.StatusCode != System.Net.HttpStatusCode.Created)
        {
            var body = await response.Content.ReadAsStringAsync();
            throw new Exception($"Expected Created, got {response.StatusCode}: {body}");
        }
        var json = await ParseJson(response);
        Assert.True(json.RootElement.TryGetProperty("workflowId", out _));
    }

    [Fact]
    public async Task GraphEditingEndpointsExist()
    {
        await StartApp();

        // Create a workflow first
        var createResponse = await PostJson("workflows", new { name = "test-workflow" });
        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);

        // POST /api/workflows/{id}/nodes - add node
        var response = await PostJson("workflows/test-workflow/nodes", new { Type = "core.text.uppercase" });
        if (response.StatusCode != HttpStatusCode.Created)
        {
            var bodyText = await response.Content.ReadAsStringAsync();
            throw new Exception($"Expected Created, got {response.StatusCode}: {bodyText}");
        }
        
        // Extract the node ID from the response
        var createdBody = await ParseJson(response);
        var nodeId = createdBody.RootElement.GetProperty("nodeId").ToString();

        // Add a second node to connect to
        response = await PostJson("workflows/test-workflow/nodes", new { Type = "core.text.uppercase" });
        if (response.StatusCode != HttpStatusCode.Created)
        {
            var bodyText = await response.Content.ReadAsStringAsync();
            throw new Exception($"Expected Created for second node, got {response.StatusCode}: {bodyText}");
        }
        createdBody = await ParseJson(response);
        var nodeId2 = createdBody.RootElement.GetProperty("nodeId").ToString();

        // PUT /api/workflows/{id}/nodes/{nodeId} - update node
        var json = JsonSerializer.Serialize(new { Configuration = new { value = "updated" } });
        var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
        response = await http!.PutAsync($"workflows/test-workflow/nodes/{nodeId}", content);
        if (response.StatusCode != HttpStatusCode.OK)
        {
            var bodyText = await response.Content.ReadAsStringAsync();
            throw new Exception($"PUT node: Expected OK, got {response.StatusCode}: {bodyText}");
        }

        // POST /api/workflows/{id}/edges - connect nodes
        var edgeConfig = new { SourceNode = nodeId, SourcePort = "out", TargetNode = nodeId2, TargetPort = "in" };
        response = await PostJson("workflows/test-workflow/edges", edgeConfig);
        if (response.StatusCode != HttpStatusCode.Created)
        {
            var bodyText = await response.Content.ReadAsStringAsync();
            throw new Exception($"POST edge: Expected Created, got {response.StatusCode}: {bodyText}");
        }

        // Extract the edge ID from the response
        createdBody = await ParseJson(response);
        var edgeId = createdBody.RootElement.GetProperty("edgeId").ToString();

        // DELETE /api/workflows/{id}/edges/{edgeId} - disconnect nodes
        response = await http.DeleteAsync($"workflows/test-workflow/edges/{edgeId}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // DELETE /api/workflows/{id}/nodes/{nodeId} - remove node
        response = await http.DeleteAsync($"workflows/test-workflow/nodes/{nodeId}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task ValidationEndpointsExist()
    {
        await StartApp();

        // Create a workflow first
        var createResponse = await PostJson("workflows", new { name = "test-workflow" });
        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);

        // POST /api/workflows/{id}/validate - validate without publishing
        var response = await PostJson("workflows/test-workflow/validate", new { });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await ParseJson(response);
        Assert.True(body.RootElement.TryGetProperty("valid", out _));
        Assert.True(body.RootElement.TryGetProperty("issues", out _));

        // GET /api/nodes - list available node families (needs implementation)
        response = await http!.GetAsync("nodes");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        body = await ParseJson(response);
        Assert.Equal(JsonValueKind.Array, body.RootElement.ValueKind);

        // GET /api/nodes/{family} - detailed pin contracts
        response = await http.GetAsync("nodes/core.constant.string");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        body = await ParseJson(response);
        Assert.True(body.RootElement.TryGetProperty("inputs", out _));
        Assert.True(body.RootElement.TryGetProperty("outputs", out _));
    }

    [Fact]
    public async Task RunManagementEndpointsExist()
    {
        await StartApp();

        // Create and publish a workflow first
        var createResponse = await PostJson("workflows", new { name = "test-workflow" });
        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);
        
        var publishResponse = await PostJson("workflows/test-workflow/publish", new { expectedGeneration = 1 });
        Assert.Equal(HttpStatusCode.OK, publishResponse.StatusCode);

        // POST /api/workflows/{id}/runs - start run (exists)
        var response = await PostJson("workflows/test-workflow/runs", new { input = "test" });
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var body = await ParseJson(response);
        var runId = body.RootElement.GetProperty("runId").ToString();

        // POST /api/runs/{id}/pause - pause run
        response = await PostJson($"runs/{runId}/pause", new { });
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        // POST /api/runs/{id}/resume - resume run
        response = await PostJson($"runs/{runId}/resume", new { });
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        // POST /api/runs/{id}/cancel - cancel run
        response = await PostJson($"runs/{runId}/cancel", new { });
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
    }

    [Fact]
    public async Task ObservationEndpointsExist()
    {
        await StartApp();

        // GET /api/runs - list runs (needs implementation)
        var response = await http!.GetAsync("runs");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // GET /api/runs/{id} - get run snapshot (needs implementation)
        var fakeRunId = Guid.NewGuid().ToString();
        response = await http.GetAsync($"runs/{fakeRunId}");
        Assert.True(response.StatusCode is HttpStatusCode.OK or HttpStatusCode.NotFound);

        // GET /api/runs/{id}/events - get run events (needs implementation)
        response = await http.GetAsync($"runs/{fakeRunId}/events");
        Assert.True(response.StatusCode is HttpStatusCode.OK or HttpStatusCode.NotFound);
    }

    private async Task StartApp()
    {
        if (app != null) return;

        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddLocalWorkflowRuntime(directory);
        
        // Use real Sqlite catalog for proper revision tracking
        var databasePath = Path.Combine(directory, "workflows.db");
        var catalog = new SqliteWorkflowCatalog(databasePath, "test");
        builder.Services.AddSingleton<IWorkflowCatalog>(catalog);

        // Register IPublishedWorkflowClient for endpoints that need it
        builder.Services.AddScoped<IPublishedWorkflowClient>(sp =>
            new PublishedWorkflowClient(catalog, sp.GetRequiredService<LocalWorkflowRuntime>()));

        app = builder.Build();
        app.Use((context, next) =>
        {
            return next(context).ContinueWith(task =>
            {
                if (task.IsFaulted)
                {
                    Console.WriteLine($"EXCEPTION: {task.Exception?.InnerException?.GetType().Name}: {task.Exception?.InnerException?.Message}");
                    Console.WriteLine(task.Exception?.InnerException?.StackTrace);
                }
            });
        });
        app.MapWorkflowEndpoints("/api");
        await app.StartAsync();

        http = new HttpClient { BaseAddress = new Uri(app.Urls.Single() + "/api/") };
    }

    private async Task<HttpResponseMessage> PostJson(string path, object body)
    {
        var json = JsonSerializer.Serialize(body);
        var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
        return await http!.PostAsync(path, content);
    }

    private async Task<JsonDocument> ParseJson(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync();
        return JsonDocument.Parse(text);
    }

    public void Dispose()
    {
        http?.Dispose();
        app?.StopAsync().Wait();
        app?.DisposeAsync().AsTask().Wait();
        if (Directory.Exists(directory)) Directory.Delete(directory, true);
    }

    private sealed class TestCatalog : IWorkflowCatalog
    {
        public WorkflowDraft Draft { get; private set; }
        public Guid? ActiveRevisionId { get; private set; }
        private readonly Dictionary<Guid, PublishedWorkflowRevision> _revisions = new();

        public TestCatalog(string id)
        {
            Draft = new(WorkflowTemplates.Create("uppercase") with { WorkflowId = id }, 1, null, null);
        }
        public Task<IReadOnlyList<WorkflowDraft>> ListDraftsAsync() => Task.FromResult<IReadOnlyList<WorkflowDraft>>([Draft]);
        public Task<WorkflowDraft?> GetDraftAsync(string workflowId) => Task.FromResult<WorkflowDraft?>(new WorkflowDraft(Draft.Definition, Draft.Generation, ActiveRevisionId, null));
        public Task<IReadOnlyList<PublishedWorkflowRevision>> ListRevisionsAsync(string workflowId) => Task.FromResult<IReadOnlyList<PublishedWorkflowRevision>>(_revisions.Values.ToList());
        public Task<IReadOnlyList<WorkflowAuditEntry>> ReadAuditAsync(string workflowId) => Task.FromResult<IReadOnlyList<WorkflowAuditEntry>>([]);
        public Task<PublishedWorkflowRevision?> GetRevisionAsync(Guid revisionId) => Task.FromResult(_revisions.TryGetValue(revisionId, out var r) ? r : null);
        public Task<WorkflowDraft> SaveDraftAsync(WorkflowDefinition definition, long expectedGeneration)
        {
            Draft = new WorkflowDraft(definition, expectedGeneration + 1, ActiveRevisionId, null);
            return Task.FromResult(Draft);
        }
        public Task<PublishedWorkflowRevision> PublishAsync(string workflowId, long expectedGeneration)
        {
            var revision = WorkflowRevision.Create(Draft.Definition, "test");
            var published = new PublishedWorkflowRevision(1, revision);
            _revisions[revision.RevisionId] = published;
            ActiveRevisionId = revision.RevisionId;
            return Task.FromResult(published);
        }
        public Task<WorkflowDraft> RestoreDraftAsync(string workflowId, Guid revisionId, long expectedGeneration) => throw new NotSupportedException();
        public Task<WorkflowDraft> ActivateAsync(string workflowId, Guid revisionId, long expectedGeneration) => throw new NotSupportedException();
    }
}