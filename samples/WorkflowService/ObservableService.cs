using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using VisualAgentWorkflow.Core;
using VisualAgentWorkflow.Hosting;
using VisualAgentWorkflow.Mcp;

namespace WorkflowService;

internal static class ObservableService
{
    public static async Task RunAsync(WorkflowRevision revision, McpToolBroker broker, IAgentNodeExecutor agents, string directory, string input)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        var port = builder.Configuration.GetValue<int?>("WorkflowService:Port") ?? 5100;
        if (port is < 1 or > 65535) throw new InvalidOperationException("Invalid consumer port.");
        builder.WebHost.UseUrls($"http://127.0.0.1:{port}");
        builder.Services.AddSingleton<INodeToolBroker>(broker);
        builder.Services.AddSingleton(agents);
        builder.Services.AddLocalWorkflowRuntime(directory);
        var enabled = builder.Configuration.GetValue<bool>("WorkflowObservation:Enabled");
        if (enabled)
        {
            var token = builder.Configuration["WorkflowObservation:Token"];
            if (token is null || token.Length is < 32 or > 4096 || token.Any(c => c <= ' ' || c > '~'))
                throw new InvalidOperationException("Enabled observation requires a strong operator-configured token.");
            builder.Services.AddAuthentication().AddScheme<AuthenticationSchemeOptions, ObservationAuthentication>(ObservationAuthentication.SchemeName, _ => { });
            builder.Services.AddAuthorization(options => options.AddPolicy("observe", policy =>
                policy.AddAuthenticationSchemes(ObservationAuthentication.SchemeName).RequireAuthenticatedUser()));
            builder.Services.AddSingleton<IWorkflowRunObserver, JournalRunObserver>();
        }
        await using var app = builder.Build();
        app.MapGet("/health", () => Results.Ok(new { status = "ready" }));
        if (enabled)
        {
            app.UseAuthentication();
            app.UseAuthorization();
            app.MapWorkflowObservation("/workflow-observation", "observe");
        }
        await app.StartAsync();
        var runtime = app.Services.GetRequiredService<LocalWorkflowRuntime>();
        var run = await runtime.StartRevisionAsync(revision, input);
        Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { kind = "serving", runId = run.Header.RunId, observationEnabled = enabled }));
        await app.WaitForShutdownAsync();
    }
}
