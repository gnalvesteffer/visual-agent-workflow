using System.Net;
using System.Text.Json;
using VisualAgentWorkflow.Blazor;
using VisualAgentWorkflow.Core;
using VisualAgentWorkflow.Providers.OpenAI;
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

var migrationArgumentIndex = Array.IndexOf(args, "--migrate-project-to-folder");
string? migrationProjectId = null;
string? migrationDestination = null;
var hostArguments = args;
if (migrationArgumentIndex >= 0)
{
    if (migrationArgumentIndex + 2 >= args.Length)
        throw new ArgumentException("Usage: --migrate-project-to-folder <project-id> <new-folder>");
    migrationProjectId = args[migrationArgumentIndex + 1];
    migrationDestination = args[migrationArgumentIndex + 2];
    hostArguments = args.Take(migrationArgumentIndex).Concat(args.Skip(migrationArgumentIndex + 3)).ToArray();
}

var builder = WebApplication.CreateBuilder(hostArguments);
var studioDataDirectory = Path.GetFullPath(builder.Configuration["WorkflowStudio:DataDirectory"] ?? Path.Combine(builder.Environment.ContentRootPath, ".local"));
if (migrationProjectId is not null)
{
    Console.WriteLine($"Migrating project '{migrationProjectId}' to {migrationDestination}.");
    Console.WriteLine("Only current editable drafts and reusable assets are copied. Workflow history, publications, run evidence, credentials, and host permissions stay in Studio data.");
    var migrated = await StudioProjectMigration.MigrateToFolderAsync(studioDataDirectory, migrationProjectId,
        migrationDestination!);
    Console.WriteLine($"Migrated '{migrated.Name}' ({migrated.Id}) to {migrated.FolderPath}.");
    Console.WriteLine("The Studio data and run history remain in the configured data directory. Restart Studio to edit the folder project.");
    return;
}

if (!builder.Environment.IsDevelopment())
    throw new InvalidOperationException("This unauthenticated prototype may run only in Development. Production hosting is not implemented.");
var studioPort = builder.Configuration.GetValue<int?>("WorkflowStudio:Port") ?? 5099;
if (studioPort is < 1 or > 65535) throw new InvalidOperationException("Invalid Studio port.");
builder.WebHost.UseUrls($"http://127.0.0.1:{studioPort}");
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 1_048_576);
builder.Services.Configure<Microsoft.AspNetCore.Http.Features.FormOptions>(options =>
    options.MultipartBodyLengthLimit = StudioProjectSourceArchive.MaximumArchiveBytes + 1024 * 1024);

using var studioHostLock = StudioProjectMigration.AcquireHostLock(studioDataDirectory);
var projectStore = new StudioProjectStore(studioDataDirectory);
StudioProjectMigration.Recover(studioDataDirectory, projectStore);
builder.Services.AddDataProtection();
builder.Services.AddRazorComponents().AddInteractiveServerComponents()
    // Bound JSON editing can duplicate field text in a Blazor event. Keep transport bounded
    // while permitting the 1 MiB definition import limit; domain validation remains stricter.
    .AddHubOptions(options => options.MaximumReceiveMessageSize = 8 * 1024 * 1024);
var builtInTools = DemoMcpServer.ForCurrentExecutable();
var studioToolPolicy = new StudioToolPolicy();
builder.Services.AddSingleton(studioToolPolicy);
builder.Services.AddSingleton(new McpToolBroker(builtInTools, studioToolPolicy, TimeSpan.FromSeconds(30)));
builder.Services.AddSingleton<StudioMcpLibrary>(services => new StudioMcpLibrary(studioDataDirectory,
    services.GetRequiredService<Microsoft.AspNetCore.DataProtection.IDataProtectionProvider>(),
    services.GetRequiredService<McpToolBroker>(), studioToolPolicy, builtInTools));
builder.Services.AddSingleton<INodeToolBroker>(services => { _ = services.GetRequiredService<StudioMcpLibrary>(); return services.GetRequiredService<McpToolBroker>(); });
builder.Services.AddSingleton<IToolBindingCatalog>(services => services.GetRequiredService<INodeToolBroker>());
var agentProviders = new[] { DeterministicDemoChatClient.Registration() }
    .Concat(OpenAIProviders.FromConfiguration(builder.Configuration.GetSection("WorkflowAgents:OpenAI"))).ToArray();
builder.Services.AddSingleton(services => new AgentNodeExecutor(agentProviders, services.GetRequiredService<INodeToolBroker>()));
builder.Services.AddSingleton<IAgentNodeExecutor>(services => services.GetRequiredService<AgentNodeExecutor>());
builder.Services.AddSingleton<StudioModelProviderStore>(services => new StudioModelProviderStore(studioDataDirectory,
    services.GetRequiredService<Microsoft.AspNetCore.DataProtection.IDataProtectionProvider>(), services.GetRequiredService<AgentNodeExecutor>(),
    services.GetRequiredService<INodeToolBroker>(), agentProviders));
builder.Services.AddSingleton<IAgentProviderCatalog>(services => { _ = services.GetRequiredService<StudioModelProviderStore>(); return services.GetRequiredService<IAgentNodeExecutor>(); });
builder.Services.AddSingleton(projectStore);
builder.Services.AddSingleton(new StudioOpenWorkspaceStore(studioDataDirectory, projectStore));
builder.Services.AddSingleton<StudioProjectSnapshotStore>();
builder.Services.AddScoped<StudioProjectWorkspace>(services => new StudioProjectWorkspace(studioDataDirectory,
    services.GetRequiredService<StudioProjectStore>(), services.GetRequiredService<LocalWorkflowRuntime>()));
builder.Services.AddScoped<StudioFolderWorkflowRunner>();
var runStoreKind = builder.Configuration["WorkflowStudio:RunStore"] ?? "file";
if (runStoreKind == "sqlite")
    builder.Services.AddSingleton<IWorkflowRunStore>(_ => new SqliteWorkflowRunStore(Path.Combine(studioDataDirectory, "runs.sqlite3")));
else if (runStoreKind != "file") throw new InvalidOperationException("RunStore must be file or sqlite.");
builder.Services.AddLocalWorkflowRuntime(Path.Combine(studioDataDirectory, "runs"));
builder.Services.AddScoped<IWorkflowCatalog>(services => services.GetRequiredService<StudioProjectWorkspace>().Catalog);
builder.Services.AddScoped<PublishedWorkflowClient>(services => (PublishedWorkflowClient)services.GetRequiredService<StudioProjectWorkspace>().Client);
builder.Services.AddScoped<IPublishedWorkflowClient>(services => services.GetRequiredService<PublishedWorkflowClient>());
builder.Services.AddScoped<IWorkflowClient>(services => services.GetRequiredService<PublishedWorkflowClient>());
builder.Services.AddScoped<IWorkflowRunControl>(services => services.GetRequiredService<PublishedWorkflowClient>());
builder.Services.AddScoped<IPublishedWorkflowRunControl>(services => services.GetRequiredService<PublishedWorkflowClient>());
builder.Services.AddScoped<IWorkflowApprovalClient>(services => services.GetRequiredService<PublishedWorkflowClient>());
builder.Services.AddScoped<ISkillLibrary>(services => services.GetRequiredService<StudioProjectWorkspace>().Skills);
builder.Services.AddScoped<IAgentProfileLibrary>(services => services.GetRequiredService<StudioProjectWorkspace>().AgentProfiles);
builder.Services.AddScoped<IGraphFunctionCatalog>(services => services.GetRequiredService<StudioProjectWorkspace>().Functions);
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
app.UseRouting();
app.Use(async (context, next) =>
{
    var selectedProject = context.Request.RouteValues.TryGetValue("projectId", out var value) ? value?.ToString() : null;
    if (selectedProject is not null)
    {
        var store = context.RequestServices.GetRequiredService<StudioProjectStore>();
        StudioProject project;
        try { project = store.GetActive(selectedProject); }
        catch (KeyNotFoundException) { context.Response.StatusCode = StatusCodes.Status404NotFound; return; }

        if (project.IsFolderBacked)
        {
            var sourceArchive = HttpMethods.IsGet(context.Request.Method) &&
                string.Equals(context.Request.Path.Value, $"/api/projects/{selectedProject}/source.zip", StringComparison.OrdinalIgnoreCase);
            var workflowRelease = HttpMethods.IsGet(context.Request.Method) &&
                string.Equals(context.Request.Path.Value, $"/api/projects/{selectedProject}/release.vawpkg", StringComparison.OrdinalIgnoreCase);
            if (sourceArchive || workflowRelease)
            {
                await next();
                return;
            }

            if (context.Request.Path.StartsWithSegments("/api"))
            {
                context.Response.StatusCode = StatusCodes.Status409Conflict;
                await context.Response.WriteAsJsonAsync(new
                {
                    error = "This project is folder-backed. Catalog publish and execution APIs are not available for folder projects yet.",
                    projectId = project.Id,
                    editor = $"/source-project/folder/{Uri.EscapeDataString(project.Id)}"
                });
                return;
            }

            context.Response.Redirect($"/source-project/folder/{Uri.EscapeDataString(project.Id)}");
            return;
        }

        try { context.RequestServices.GetRequiredService<StudioProjectWorkspace>().Select(selectedProject); }
        catch (KeyNotFoundException) { context.Response.StatusCode = StatusCodes.Status404NotFound; return; }
    }
    else
    {
        var defaultProject = context.RequestServices.GetRequiredService<StudioProjectStore>()
            .GetActive(StudioProjectStore.DefaultProjectId);
        if (defaultProject.IsFolderBacked)
        {
            var folderEditor = $"/source-project/folder/{Uri.EscapeDataString(defaultProject.Id)}";
            if (context.Request.Path.Value is "/" or "/agents" or "/skills" or "/functions")
            {
                context.Response.Redirect(folderEditor);
                return;
            }
            if (context.Request.Path.StartsWithSegments("/api/workflows") ||
                context.Request.Path.StartsWithSegments("/api/revisions") ||
                context.Request.Path.StartsWithSegments("/api/runs"))
            {
                context.Response.StatusCode = StatusCodes.Status409Conflict;
                await context.Response.WriteAsJsonAsync(new
                {
                    error = "The default project is folder-backed. Catalog authoring and execution APIs are not available for folder projects yet.",
                    projectId = defaultProject.Id,
                    editor = folderEditor
                });
                return;
            }
        }
    }
    await next();
});
app.UseStaticFiles();
app.UseAntiforgery();
app.MapWorkflowEndpoints();
app.MapWorkflowEndpoints("/api/projects/{projectId}");
app.MapGet("/api/projects/{projectId}/source.zip", async (string projectId, StudioProjectStore store,
    StudioProjectWorkspace workspace, CancellationToken cancellationToken) =>
{
    var project = store.GetActive(projectId);
    byte[] archive;
    if (project.IsFolderBacked)
    {
        var (manifest, files) = StudioProjectSourceDirectory.Read(store.FolderProjectPath(projectId));
        archive = StudioProjectSourceArchive.CreateArchive(manifest, files);
    }
    else archive = await StudioProjectSourceArchive.CreateAsync(workspace, cancellationToken);
    return Results.File(archive, "application/zip", $"{project.Name}.vawsrc.zip");
});
app.MapGet("/api/projects/{projectId}/release.vawpkg", (string projectId, HttpRequest request, StudioProjectStore store) =>
{
    try
    {
        var project = store.GetActive(projectId);
        if (!project.IsFolderBacked)
            return Results.Conflict(new { error = "Consumer releases are currently built from validated folder projects." });
        var selectedIds = request.Query["workflowId"].SelectMany(value => (value ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)).ToArray();
        if (selectedIds.Length is < 1 or > WorkflowProjectPackageJson.MaximumWorkflows ||
            selectedIds.Distinct(StringComparer.Ordinal).Count() != selectedIds.Length)
            return Results.BadRequest(new { error = $"Select between 1 and {WorkflowProjectPackageJson.MaximumWorkflows} unique workflow entry points." });

        var (manifest, files) = StudioProjectSourceDirectory.Read(store.FolderProjectPath(projectId));
        var workflowFiles = manifest.Files.Where(file => file.Kind == "workflow" && selectedIds.Contains(file.Id, StringComparer.Ordinal)).ToArray();
        if (workflowFiles.Length != selectedIds.Length)
            return Results.BadRequest(new { error = "Every selected workflow must exist in the validated project source." });

        var packages = workflowFiles.Select(file =>
        {
            var source = StudioProjectSourceArchive.DeserializeWorkflowSource(files[file.Path]);
            if (source.Definition.WorkflowId != file.Id)
                throw new InvalidDataException($"Workflow source '{file.Id}' does not match its project index.");
            return WorkflowPackageJson.Create(source.Definition, source.Layout);
        }).ToArray();
        var archive = WorkflowProjectPackageJson.CreateArchive(manifest.ProjectId, manifest.ProjectName, packages);
        var fileName = string.Concat(project.Name.Select(character => Path.GetInvalidFileNameChars().Contains(character) || char.IsControl(character) ? '-' : character));
        return Results.File(archive, "application/zip", $"{fileName}.vawpkg");
    }
    catch (Exception exception) when (exception is InvalidDataException or ArgumentException or InvalidOperationException or KeyNotFoundException)
    {
        return Results.BadRequest(new { error = exception.Message });
    }
});
app.MapPost("/api/projects/import", async (HttpContext context, StudioProjectStore store,
    StudioProjectWorkspace workspace, CancellationToken cancellationToken) =>
{
    try
    {
        context.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>()?.MaxRequestBodySize =
            StudioProjectSourceArchive.MaximumArchiveBytes + 1024 * 1024;
        await context.RequestServices.GetRequiredService<Microsoft.AspNetCore.Antiforgery.IAntiforgery>()
            .ValidateRequestAsync(context);
        var request = context.Request;
        var form = await request.ReadFormAsync(cancellationToken);
        var upload = form.Files.GetFile("sourceArchive");
        if (upload is null || upload.Length is <= 0 or > StudioProjectSourceArchive.MaximumArchiveBytes)
            return Results.Redirect("/projects?importError=Choose%20a%20source%20archive%20up%20to%2032%20MiB.");
        await using var input = upload.OpenReadStream();
        using var contents = new MemoryStream();
        await input.CopyToAsync(contents, cancellationToken);
        var project = await StudioProjectSourceArchive.ImportAsNewProjectAsync(contents.ToArray(), store, workspace, cancellationToken);
        return Results.Redirect($"/projects/{Uri.EscapeDataString(project.Id)}");
    }
    catch (Exception exception) when (exception is InvalidDataException or ArgumentException or InvalidOperationException)
    {
        return Results.Redirect($"/projects?importError={Uri.EscapeDataString(exception.Message)}");
    }
});
app.MapGet("/api/tool-bindings", async (IToolBindingCatalog catalog, CancellationToken cancellationToken) =>
    Results.Ok(await catalog.ListBindingsAsync(cancellationToken)));
app.MapGet("/api/agent-providers", async (IAgentProviderCatalog catalog) => Results.Ok(await catalog.ListProvidersAsync()));
app.MapGet("/api/settings/model-providers", (StudioModelProviderStore store) => Results.Ok(store.List()));
app.MapDelete("/api/settings/model-providers/{providerId}", (string providerId, StudioModelProviderStore store) =>
    store.Delete(providerId) ? Results.NoContent() : Results.NotFound());
app.MapPost("/api/settings/model-providers", (AddModelProviderRequest request, StudioModelProviderStore store) =>
{
    try
    {
        store.Save(request.ProviderId, request.DisplayName, request.Model, request.Endpoint,
            request.TimeoutSeconds, request.ApiKey, request.AllowInsecureHttp, removeApiKey: request.RemoveApiKey,
            contextWindowTokens: request.ContextWindowTokens, inputCostPerMillionTokens: request.InputCostPerMillionTokens,
            outputCostPerMillionTokens: request.OutputCostPerMillionTokens);
        return Results.Ok(store.List());
    }
    catch (ArgumentException error) { return Results.BadRequest(new { error = error.Message }); }
    catch (InvalidOperationException error) { return Results.BadRequest(new { error = error.Message }); }
});
app.MapGet("/api/templates/default", () => Results.Text(WorkflowJson.Serialize(WorkflowTemplates.Create("uppercase-reverse")), "application/json"));
app.MapPost("/api/runs", async (StartRunRequest request, IWorkflowClient client) =>
{
    try
    {
        if (request.Input is null || request.Definition.ValueKind != JsonValueKind.Object)
            return Results.BadRequest(new { error = "An input string and definition object are required." });
        var definition = WorkflowJson.Deserialize(request.Definition.GetRawText());
        if (request.StartPaused && client is not IWorkflowRunControl) return Results.BadRequest();
        if (request.Parameters is not null && client is not IParameterizedWorkflowClient) return Results.BadRequest();
        RunSnapshot snapshot;
        if (request.Parameters is not null)
        {
            var parameterized = (IParameterizedWorkflowClient)client;
            snapshot = request.StartPaused
                ? await parameterized.StartPausedRunWithParametersAsync(definition, request.Input, request.Parameters)
                : await parameterized.StartRunWithParametersAsync(definition, request.Input, request.Parameters);
        }
        else
        {
            snapshot = request.StartPaused
                ? await ((IWorkflowRunControl)client).StartPausedRunAsync(definition, request.Input)
                : await client.StartRunAsync(definition, request.Input);
        }
        return Results.Accepted($"/api/runs/{snapshot.Header.RunId}", snapshot);
    }
    catch (Exception error) when (error is ArgumentException or JsonException)
    {
        return Results.BadRequest(new { error = "Invalid workflow definition or input. Only validated string workflows are supported." });
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
app.MapPost("/api/runs/{id:guid}/approvals/{requestId:guid}/decision", async (Guid id, Guid requestId, ApprovalDecisionRequest request,
    IWorkflowApprovalClient approvals, IWorkflowClient client) =>
{
    if (request.Approve is not { } approve || request.ActionHash is null) return Results.BadRequest();
    try
    {
        await approvals.DecideApprovalAsync(id, requestId, request.ActionHash, approve);
        return Results.Accepted($"/api/runs/{id}", await client.GetRunAsync(id));
    }
    catch (ArgumentException) { return Results.BadRequest(); }
    catch (KeyNotFoundException) { return Results.NotFound(); }
    catch (WorkflowApprovalConflictException) { return Results.Conflict(new { error = "The recorded action is no longer awaiting this decision." }); }
    catch (WorkflowRunControlException) { return Results.Conflict(new { error = "This runtime no longer owns that continuation." }); }
});
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();
app.Run();

public sealed record StartRunRequest(JsonElement Definition, string? Input)
{
    public bool StartPaused { get; init; }
    public Dictionary<string, string>? Parameters { get; init; }
}

[System.Text.Json.Serialization.JsonUnmappedMemberHandling(System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow)]
public sealed record ApprovalDecisionRequest(string? ActionHash, bool? Approve);

public sealed record AddModelProviderRequest(string ProviderId, string DisplayName, string Model, string Endpoint,
    int TimeoutSeconds, string ApiKey, bool AllowInsecureHttp, bool RemoveApiKey, int? ContextWindowTokens = null,
    decimal? InputCostPerMillionTokens = null, decimal? OutputCostPerMillionTokens = null);
