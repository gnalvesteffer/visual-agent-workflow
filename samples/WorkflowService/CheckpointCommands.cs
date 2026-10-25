using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using VisualAgentWorkflow.AgentFramework;
using VisualAgentWorkflow.Core;
using VisualAgentWorkflow.Hosting;

namespace WorkflowService;

internal static class CheckpointCommands
{
    public static async Task<int> RunAsync(string[] args)
    {
        try
        {
            var starting = args[0] == "checkpoint";
            if (starting && (args.Length is < 5 or > 6 || args.Length == 6 && args[5] != "--hold") || !starting && args.Length != 4)
                throw new ArgumentException("Invalid checkpoint command.");
            WorkflowRevision? revision = null;
            var threshold = 0;
            if (starting)
            {
                threshold = int.Parse(args[4], System.Globalization.CultureInfo.InvariantCulture);
                if (threshold < 1) throw new ArgumentOutOfRangeException(nameof(args));
                revision = await Deployment.LoadAsync(args[1], CancellationToken.None);
                WorkflowRunner.ValidateCheckpointGraph(revision.Definition);
                if (args[3].Length > 16_384) throw new ArgumentException("Input exceeds the workflow limit.");
            }
            var store = new SqliteWorkflowRunStore(args[starting ? 2 : 1]);
            await using var runtime = new CheckpointWorkflowHost(store);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var result = starting
                ? await runtime.StartAsync(revision!, args[3], threshold, deadline.Token)
                : await runtime.ResumeAsync(Guid.Parse(args[2]), args[3], cancellationToken: deadline.Token);
            var checkpoint = store.GetCheckpoint(result.Header.RunId);
            Console.WriteLine(JsonSerializer.Serialize(new { result.Header.RunId, result.Header.RevisionId, result.Header.ContentHash,
                result.Status, checkpointId = checkpoint?.Native.CheckpointId,
                output = result.Events.LastOrDefault(e => e.Kind == ExecutionEventKinds.Completed)?.Data,
                nodes = result.Events.Where(e => e.Kind == ExecutionEventKinds.NodeStarted).Select(e => e.NodeId),
                sequence = result.Events.Length, reconciliation = store.GetReconciliation(result.Header.RunId) }, JsonSerializerOptions.Web));
            if (starting && args.Length == 6)
            {
                var builder = Host.CreateApplicationBuilder();
                builder.Logging.ClearProviders();
                using var lifetime = builder.Build();
                await lifetime.RunAsync(); // Keep the saved boundary's exclusive owner alive for the process-loss proof.
            }
            return result.Status is "suspended" or "completed" ? 0 : 2;
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            Console.Error.WriteLine($"Checkpoint command failed: {error.GetType().Name}.");
            return 1;
        }
    }
}
