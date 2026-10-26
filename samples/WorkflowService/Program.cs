using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using VisualAgentWorkflow.AgentFramework;
using VisualAgentWorkflow.Core;
using VisualAgentWorkflow.Hosting;
using VisualAgentWorkflow.Mcp;
using VisualAgentWorkflow.Providers.OpenAI;
using WorkflowService;

if (args is ["--mcp-demo-server"])
{
    await DemoMcpServer.RunAsync();
    return 0;
}
if (args.Length > 0 && args[0] is "checkpoint" or "recover")
    return await CheckpointCommands.RunAsync(args);
if (args.Length > 0 && args[0] == "pack")
    return await PackageCommands.RunAsync(args);

var isBundleRun = args.Length > 0 && args[0] == "bundle";
var mode = isBundleRun && args.Length > 1 ? args[1] : args.FirstOrDefault();
var validMode = mode is "direct" or "queued" or "paused" or "serve";
if (!validMode || isBundleRun && args.Length is not (6 or 7) ||
    !isBundleRun && args.Length is not (4 or 5) ||
    isBundleRun && args.Length == 7 && args[6] != "--grant-demo-tool" ||
    !isBundleRun && args.Length == 5 && args[4] != "--grant-demo-tool")
{
    Console.Error.WriteLine("Usage: WorkflowService direct|queued|paused|serve DEPLOYMENT.json JOURNAL_DIRECTORY INPUT [--grant-demo-tool]");
    Console.Error.WriteLine("   or: WorkflowService bundle direct|queued|paused|serve RELEASE.vawpkg WORKFLOW_ID JOURNAL_DIRECTORY INPUT [--grant-demo-tool]");
    return 1;
}

try
{
    var builder = Host.CreateApplicationBuilder();
    builder.Logging.ClearProviders();
    var providers = OpenAIProviders.FromConfiguration(builder.Configuration.GetSection("WorkflowAgents:OpenAI"));
    var artifactPath = isBundleRun ? args[2] : args[1];
    var workflowId = isBundleRun ? args[3] : null;
    var journalDirectory = isBundleRun ? args[4] : args[2];
    var input = isBundleRun ? args[5] : args[3];
    var grantDemoTool = isBundleRun ? args.Length == 7 : args.Length == 5;
    if (input.Length > 16_384) throw new ArgumentException("Input exceeds the workflow limit.");
    var revision = isBundleRun
        ? await WorkflowProjectDeployment.LoadAsync(artifactPath, workflowId!, CancellationToken.None)
        : await Deployment.LoadAsync(artifactPath, CancellationToken.None);
    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
    var broker = await DeploymentDependencies.ResolveAsync(revision, grantDemoTool, providers, deadline.Token);
    var agents = new AgentNodeExecutor(providers, broker);
    IWorkflowRunStore journal = (builder.Configuration["WorkflowService:RunStore"] ?? "file") switch
    {
        "file" => new FileRunJournal(journalDirectory),
        "sqlite" => new SqliteWorkflowRunStore(Path.Combine(journalDirectory, "runs.sqlite3")),
        _ => throw new InvalidOperationException("RunStore must be file or sqlite.")
    };
    if (mode == "serve")
    {
        await ObservableService.RunAsync(revision, broker, agents, journal, journalDirectory, input);
        return 0;
    }
    builder.Services.AddSingleton<IWorkflowRunStore>(journal);
    builder.Services.AddSingleton<INodeToolBroker>(broker);
    builder.Services.AddSingleton<IAgentNodeExecutor>(agents);
    builder.Services.AddLocalWorkflowRuntime(journalDirectory);
    using var host = builder.Build();
    await host.StartAsync();
    var lifetime = host.Services.GetRequiredService<IHostApplicationLifetime>();
    var runtime = host.Services.GetRequiredService<LocalWorkflowRuntime>();
    Guid? runId = null;
    try
    {
        if (mode == "direct")
        {
            var initial = journal.Create(revision, input);
            runId = initial.Header.RunId;
            await new WorkflowRunner(broker, agents).ExecuteAsync(runId.Value, revision, input, evt =>
            {
                journal.Append(evt);
                return ValueTask.CompletedTask;
            }, lifetime.ApplicationStopping);
        }
        else
        {
            // This returns an identity before any node executes. A request handler can return here.
            var accepted = await runtime.StartRevisionPausedAsync(revision, input);
            runId = accepted.Header.RunId;
            Write(new { kind = "accepted", runId, revisionId = revision.RevisionId, accepted.Status });
            if (mode == "queued") await runtime.ResumeRunAsync(runId.Value);
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
