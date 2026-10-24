using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using VisualAgentWorkflow.AgentFramework;
using VisualAgentWorkflow.Core;

namespace VisualAgentWorkflow.Hosting;

/// <summary>Bounded, server-owned development execution queue. UI request cancellation never owns runs.</summary>
public sealed class LocalWorkflowRuntime(FileRunJournal journal, ILogger<LocalWorkflowRuntime> logger)
    : BackgroundService, IWorkflowClient
{
    private sealed record Request(Guid RunId, WorkflowRevision Revision, string Input, CancellationTokenSource Cancellation);
    private readonly Channel<Request> queue = Channel.CreateBounded<Request>(32);
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> cancellations = new();

    public override Task StartAsync(CancellationToken cancellationToken)
    {
        journal.MarkUnfinishedAsInterrupted();
        return base.StartAsync(cancellationToken);
    }

    public Task<RunSnapshot> StartRunAsync(WorkflowDefinition definition, string input)
        => StartRevisionAsync(WorkflowRevision.Create(definition, "local-developer"), input);

    public Task<RunSnapshot> StartRevisionAsync(WorkflowRevision revision, string input)
    {
        ArgumentNullException.ThrowIfNull(revision);
        ArgumentNullException.ThrowIfNull(input);
        if (input.Length > 16_384) throw new ArgumentException("Input exceeds 16,384 characters.", nameof(input));
        var snapshot = journal.Create(revision, input);
        var cancellation = new CancellationTokenSource();
        cancellations[snapshot.Header.RunId] = cancellation;
        if (!queue.Writer.TryWrite(new(snapshot.Header.RunId, revision, input, cancellation)))
        {
            cancellations.TryRemove(snapshot.Header.RunId, out _);
            cancellation.Dispose();
            journal.Append(new(snapshot.Header.RunId, revision.RevisionId, 1, DateTimeOffset.UtcNow,
                ExecutionEventKinds.Failed, Data: "The local execution queue is full or stopping."));
            return Task.FromResult(journal.Get(snapshot.Header.RunId)!);
        }
        return Task.FromResult(snapshot);
    }

    public Task<RunSnapshot?> GetRunAsync(Guid runId) => Task.FromResult(journal.Get(runId));
    public Task<IReadOnlyList<RunSnapshot>> ListRunsAsync() => Task.FromResult(journal.List());

    public Task CancelRunAsync(Guid runId)
    {
        if (cancellations.TryGetValue(runId, out var cancellation))
        {
            try { cancellation.Cancel(); }
            catch (ObjectDisposedException) { /* Run finished concurrently. */ }
        }
        return Task.CompletedTask;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var request in queue.Reader.ReadAllAsync(stoppingToken))
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, request.Cancellation.Token);
                try
                {
                    await new PipelineRunner().ExecuteAsync(request.RunId, request.Revision, request.Input,
                        evt => { journal.Append(evt); return ValueTask.CompletedTask; }, linked.Token);
                }
                catch (OperationCanceledException) when (linked.IsCancellationRequested) { }
                catch (Exception error)
                {
                    // Payloads and exception text are intentionally omitted from host logs.
                    logger.LogError("Run {RunId} failed with {ExceptionType}.", request.RunId, error.GetType().Name);
                }
                finally
                {
                    cancellations.TryRemove(request.RunId, out _);
                    request.Cancellation.Dispose();
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally
        {
            queue.Writer.TryComplete();
            while (queue.Reader.TryRead(out var pending))
            {
                cancellations.TryRemove(pending.RunId, out _);
                pending.Cancellation.Dispose();
            }
            journal.MarkUnfinishedAsInterrupted();
        }
    }
}
