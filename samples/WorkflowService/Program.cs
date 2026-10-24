using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using VisualAgentWorkflow.AgentFramework;
using VisualAgentWorkflow.Core;
using VisualAgentWorkflow.Hosting;
using VisualAgentWorkflow.Mcp;
using WorkflowService;

if (args is ["--mcp-demo-server"])
{
    await DemoMcpServer.RunAsync();
    return 0;
}
if (args.Length is < 4 or > 5 || args[0] is not ("direct" or "queued" or "paused" or "serve") ||
    args.Length == 5 && args[4] != "--grant-demo-tool")
{
    Console.Error.WriteLine("Usage: WorkflowService direct|queued|paused|serve DEPLOYMENT.json JOURNAL_DIRECTORY INPUT [--grant-demo-tool]");
    return 1;
}

try
{
    var revision = await Deployment.LoadAsync(args[1], CancellationToken.None);
    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
    var broker = await DeploymentDependencies.ResolveAsync(revision, args.Length == 5, deadline.Token);
    if (args[3].Length > 16_384) throw new ArgumentException("Input exceeds the pipeline limit.");
    if (args[0] == "serve")
    {
        await ObservableService.RunAsync(revision, broker, args[2], args[3]);
        return 0;
    }
    var journal = new FileRunJournal(args[2]);
    var builder = Host.CreateApplicationBuilder();
    builder.Logging.ClearProviders();
    builder.Services.AddSingleton(journal);
    builder.Services.AddSingleton<INodeToolBroker>(broker);
    builder.Services.AddLocalWorkflowRuntime(args[2]);
    using var host = builder.Build();
    await host.StartAsync();
    var lifetime = host.Services.GetRequiredService<IHostApplicationLifetime>();
    var runtime = host.Services.GetRequiredService<LocalWorkflowRuntime>();
    Guid? runId = null;
    try
    {
        if (args[0] == "direct")
        {
            var initial = journal.Create(revision, args[3]);
            runId = initial.Header.RunId;
            await new PipelineRunner(broker).ExecuteAsync(runId.Value, revision, args[3], evt =>
            {
                journal.Append(evt);
                return ValueTask.CompletedTask;
            }, lifetime.ApplicationStopping);
        }
        else
        {
            // This returns an identity before any node executes. A request handler can return here.
            var accepted = await runtime.StartRevisionPausedAsync(revision, args[3]);
            runId = accepted.Header.RunId;
            Write(new { kind = "accepted", runId, revisionId = revision.RevisionId, accepted.Status });
            if (args[0] == "queued") await runtime.ResumeRunAsync(runId.Value);
            while (!(await runtime.GetRunAsync(runId.Value))!.IsTerminal)
                await Task.Delay(25, lifetime.ApplicationStopping);
        }
    }
    catch (OperationCanceledException) when (lifetime.ApplicationStopping.IsCancellationRequested) { }
    finally
    {
        using var stopDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await host.StopAsync(stopDeadline.Token);
    }
    var result = runId is { } id ? journal.Get(id) : null;
    if (result is null) return 1;
    Write(new { kind = "result", result.Header.RunId, result.Header.RevisionId, result.Header.ContentHash,
        result.Status, output = result.Events.LastOrDefault(e => e.Kind == ExecutionEventKinds.Completed)?.Data,
        events = result.Events.Select(e => e.Kind) });
    return result.Status == "completed" ? 0 : 2;
}
catch (Exception error) when (error is not OutOfMemoryException)
{
    // Do not log arbitrary artifact text, provider responses, credentials, or exception messages.
    Console.Error.WriteLine($"Workflow service failed: {error.GetType().Name}.");
    return 1;
}

static void Write<T>(T value) => Console.WriteLine(JsonSerializer.Serialize(value, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
