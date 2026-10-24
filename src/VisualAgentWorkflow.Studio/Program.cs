using System.Net;
using System.Text.Json;
using VisualAgentWorkflow.Blazor;
using VisualAgentWorkflow.Core;
using VisualAgentWorkflow.Hosting;
using VisualAgentWorkflow.Studio.Components;
using VisualAgentWorkflow.Studio;
using VisualAgentWorkflow.Mcp;
using VisualAgentWorkflow.AgentFramework;

if (args.Contains("--mcp-demo-server", StringComparer.Ordinal))
{
    await DemoMcpServer.RunAsync();
    return;
}

var builder = WebApplication.CreateBuilder(args);
if (!builder.Environment.IsDevelopment())
    throw new InvalidOperationException("This unauthenticated prototype may run only in Development. Production hosting is not implemented.");
var studioPort = builder.Configuration.GetValue<int?>("WorkflowStudio:Port") ?? 5099;
if (studioPort is < 1 or > 65535) throw new InvalidOperationException("Invalid Studio port.");
builder.WebHost.UseUrls($"http://127.0.0.1:{studioPort}");
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 1_048_576);
builder.Services.AddRazorComponents().AddInteractiveServerComponents()
    // Bound JSON editing can duplicate field text in a Blazor event. Keep transport bounded
    // while permitting the 1 MiB definition import limit; domain validation remains stricter.
    .AddHubOptions(options => options.MaximumReceiveMessageSize = 8 * 1024 * 1024);
builder.Services.AddSingleton<INodeToolBroker>(new McpToolBroker(DemoMcpServer.ForCurrentExecutable(), new DemoToolPolicy()));
builder.Services.AddSingleton<IToolBindingCatalog>(services => services.GetRequiredService<INodeToolBroker>());
builder.Services.AddSingleton<IAgentNodeExecutor>(services => new AgentNodeExecutor(
    [DeterministicDemoChatClient.Registration()], services.GetRequiredService<INodeToolBroker>()));
builder.Services.AddSingleton<IAgentProviderCatalog>(services => services.GetRequiredService<IAgentNodeExecutor>());
var studioDataDirectory = Path.GetFullPath(builder.Configuration["WorkflowStudio:DataDirectory"] ?? Path.Combine(builder.Environment.ContentRootPath, ".local"));
builder.Services.AddLocalWorkflowRuntime(Path.Combine(studioDataDirectory, "runs"));
builder.Services.AddSingleton<IWorkflowCatalog>(new SqliteWorkflowCatalog(Path.Combine(studioDataDirectory, "workflows.db")));
builder.Services.AddSingleton<PublishedWorkflowClient>();
builder.Services.AddSingleton<IPublishedWorkflowClient>(services => services.GetRequiredService<PublishedWorkflowClient>());
builder.Services.AddSingleton<IWorkflowClient>(services => services.GetRequiredService<PublishedWorkflowClient>());
builder.Services.AddSingleton<IWorkflowRunControl>(services => services.GetRequiredService<PublishedWorkflowClient>());
builder.Services.AddSingleton<IPublishedWorkflowRunControl>(services => services.GetRequiredService<PublishedWorkflowClient>());
builder.Services.AddSingleton<ObservationSources>();
var app = builder.Build();

// Development containment, not a production authentication boundary. Do not put behind a public proxy.
app.Use(async (context, next) =>
{
    var peer = context.Connection.RemoteIpAddress;
    var host = context.Request.Host.Host;
    var localHost = host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
        IPAddress.TryParse(host, out var address) && IPAddress.IsLoopback(address);
    if (peer is null || !IPAddress.IsLoopback(peer) || !localHost)
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        return;
    }
    // Check browser origins on WebSocket upgrades too, not only state-changing HTTP methods.
    var origin = context.Request.Headers.Origin.ToString();
    var expected = $"{context.Request.Scheme}://{context.Request.Host}";
    if (context.Request.Headers["Sec-Fetch-Site"] == "cross-site" ||
        origin.Length > 0 && !string.Equals(origin, expected, StringComparison.OrdinalIgnoreCase))
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        return;
    }
    await next();
});
app.UseStaticFiles();
app.UseAntiforgery();
app.MapWorkflowEndpoints();
app.MapGet("/api/tool-bindings", async (IToolBindingCatalog catalog, CancellationToken cancellationToken) =>
    Results.Ok(await catalog.ListBindingsAsync(cancellationToken)));
app.MapGet("/api/agent-providers", async (IAgentProviderCatalog catalog) => Results.Ok(await catalog.ListProvidersAsync()));
app.MapGet("/api/templates/default", () => Results.Text(WorkflowJson.Serialize(PipelineTemplates.Create("uppercase-reverse")), "application/json"));
app.MapPost("/api/runs", async (StartRunRequest request, IWorkflowClient client) =>
{
    try
    {
        if (request.Input is null || request.Definition.ValueKind != JsonValueKind.Object)
            return Results.BadRequest(new { error = "An input string and definition object are required." });
        var definition = WorkflowJson.Deserialize(request.Definition.GetRawText());
        if (request.StartPaused && client is not IWorkflowRunControl) return Results.BadRequest();
        var snapshot = request.StartPaused
            ? await ((IWorkflowRunControl)client).StartPausedRunAsync(definition, request.Input)
            : await client.StartRunAsync(definition, request.Input);
        return Results.Accepted($"/api/runs/{snapshot.Header.RunId}", snapshot);
    }
    catch (Exception error) when (error is ArgumentException or JsonException)
    {
        return Results.BadRequest(new { error = "Invalid workflow definition or input. Only validated string pipelines are supported." });
    }
});
app.MapGet("/api/runs", async (IWorkflowClient client) => Results.Ok(await client.ListRunsAsync()));
app.MapGet("/api/runs/{id:guid}", async (Guid id, IWorkflowClient client) =>
    await client.GetRunAsync(id) is { } snapshot ? Results.Ok(snapshot) : Results.NotFound());
app.MapGet("/api/runs/{id:guid}/events", async (Guid id, long? after, IWorkflowClient client) =>
{
    if (after < 0) return Results.BadRequest();
    var snapshot = await client.GetRunAsync(id);
    return snapshot is null ? Results.NotFound() : Results.Ok(snapshot.Events.Where(e => e.Sequence > (after ?? 0)));
});
app.MapPost("/api/runs/{id:guid}/cancel", async (Guid id, IWorkflowClient client) =>
{
    if (await client.GetRunAsync(id) is null) return Results.NotFound();
    await client.CancelRunAsync(id);
    return Results.NoContent();
});
app.MapPost("/api/runs/{id:guid}/pause", async (Guid id, IWorkflowRunControl controls, IWorkflowClient client) =>
{
    try
    {
        await controls.PauseRunAsync(id);
        return Results.Accepted($"/api/runs/{id}", await client.GetRunAsync(id));
    }
    catch (KeyNotFoundException) { return Results.NotFound(); }
    catch (WorkflowRunControlException) { return Results.Conflict(new { error = "This runtime no longer owns an active continuation for that run." }); }
});
app.MapPost("/api/runs/{id:guid}/resume", async (Guid id, IWorkflowRunControl controls, IWorkflowClient client) =>
{
    try
    {
        await controls.ResumeRunAsync(id);
        return Results.Accepted($"/api/runs/{id}", await client.GetRunAsync(id));
    }
    catch (KeyNotFoundException) { return Results.NotFound(); }
    catch (WorkflowRunControlException) { return Results.Conflict(new { error = "This runtime no longer owns an active continuation for that run." }); }
});
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();
app.Run();

public sealed record StartRunRequest(JsonElement Definition, string? Input)
{
    public bool StartPaused { get; init; }
}
