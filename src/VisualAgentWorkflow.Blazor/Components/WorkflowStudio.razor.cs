using Microsoft.AspNetCore.Components;
using VisualAgentWorkflow.Core;

namespace VisualAgentWorkflow.Blazor.Components;

public partial class WorkflowStudio
{
    [Parameter, EditorRequired] public IWorkflowClient Client { get; set; } = default!;
    [Parameter] public IWorkflowCatalog? Catalog { get; set; }
    [Parameter] public IToolBindingCatalog? ToolCatalog { get; set; }
    [Parameter] public IAgentProviderCatalog? AgentCatalog { get; set; }
    private string operation = "uppercase-reverse";
    private string input = "Hello, workflows!";
    private IReadOnlyList<RunSnapshot> runs = [];
    private RunSnapshot? snapshot;
    private bool starting;
    private bool interactive;
    private bool disposed;
    private string? historyError;
    private string? actionError;
    private readonly CancellationTokenSource lifetime = new();
    private CancellationTokenSource? contextLifetime;
    private CancellationToken contextToken;
    private IWorkflowClient? boundClient;
    private long clientVersion;
    private long selectionVersion;
    private Task? polling;

    protected override async Task OnParametersSetAsync()
    {
        if (disposed || ReferenceEquals(boundClient, Client)) return;
        boundClient = Client;
        clientVersion++;
        contextLifetime?.Cancel();
        contextLifetime?.Dispose();
        contextLifetime = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        contextToken = contextLifetime.Token;
        selectionVersion++;
        runs = [];
        snapshot = null;
        historyError = null;
        actionError = null;
        starting = false;
        operation = "uppercase-reverse";
        input = "Hello, workflows!";
        await RefreshHistory();
    }

    protected override void OnAfterRender(bool firstRender)
    {
        if (!firstRender || disposed) return;
        interactive = true;
        // Prerendering never starts a timer. Disposal cancels observation, not backend execution.
        polling = PollAsync();
        StateHasChanged();
    }

    private bool Current(IWorkflowClient client, long version) =>
        !disposed && ReferenceEquals(boundClient, client) && version == clientVersion;

    private async Task PollAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(500));
        try
        {
            while (await timer.WaitForNextTickAsync(lifetime.Token))
                await InvokeAsync(async () => { if (await RefreshHistory()) StateHasChanged(); });
        }
        catch (OperationCanceledException) when (disposed) { }
        catch (ObjectDisposedException) when (disposed) { }
    }

    private async Task Refresh() => await RefreshHistory();

    private async Task<bool> RefreshHistory()
    {
        var client = Client;
        var version = clientVersion;
        var selection = selectionVersion;
        var selectedId = snapshot?.Header.RunId;
        var token = contextToken;
        try
        {
            var latest = (await client.ListRunsAsync().WaitAsync(token)).Take(50).ToArray();
            var selected = selectedId is { } id ? await client.GetRunAsync(id).WaitAsync(token) : null;
            if (!Current(client, version)) return false;
            var changed = historyError is not null || !runs.Select(Summary).SequenceEqual(latest.Select(Summary));
            if (changed) runs = latest;
            historyError = null;
            if (selection == selectionVersion && selectedId == snapshot?.Header.RunId && !SameSnapshot(snapshot, selected))
            {
                snapshot = selected;
                changed = true;
            }
            return changed;
        }
        catch (OperationCanceledException) when (disposed) { return false; }
        catch
        {
            if (!Current(client, version)) return false;
            const string message = "Could not load run history. Check the backend diagnostics.";
            var changed = historyError != message;
            historyError = message;
            return changed;
        }
    }

    private static (Guid Id, int Count, string Status) Summary(RunSnapshot run) =>
        (run.Header.RunId, run.Events.Length, run.Status);

    private static bool SameSnapshot(RunSnapshot? first, RunSnapshot? second) =>
        first is null ? second is null : second is not null && Summary(first) == Summary(second);

    private async Task StartRun()
    {
        var client = Client;
        var version = clientVersion;
        starting = true;
        actionError = null;
        try
        {
            var run = await client.StartRunAsync(PipelineTemplates.Create(operation), input).WaitAsync(contextToken);
            if (!Current(client, version)) return;
            selectionVersion++;
            snapshot = run;
            await RefreshHistory();
        }
        catch (OperationCanceledException) when (disposed) { }
        catch
        {
            if (Current(client, version)) actionError = "The run could not be started. Check the definition, input limit, and backend diagnostics.";
        }
        finally { if (Current(client, version)) starting = false; }
    }

    private async Task SelectRun(Guid id)
    {
        var client = Client;
        var version = clientVersion;
        var selection = ++selectionVersion;
        actionError = null;
        try
        {
            var run = await client.GetRunAsync(id).WaitAsync(contextToken);
            if (Current(client, version) && selection == selectionVersion) snapshot = run;
        }
        catch (OperationCanceledException) when (disposed) { }
        catch { if (Current(client, version)) actionError = "Could not load the selected run. Try refreshing history."; }
    }

    private EventCallback<RunSnapshot> PublishedCallback(IWorkflowClient client)
    {
        var version = clientVersion;
        return EventCallback.Factory.Create<RunSnapshot>(this, async run =>
        {
            if (!Current(client, version)) return;
            selectionVersion++;
            snapshot = run;
            await RefreshHistory();
        });
    }

    private async Task Cancel()
    {
        if (snapshot is null) return;
        var client = Client;
        var version = clientVersion;
        var id = snapshot.Header.RunId;
        actionError = null;
        try
        {
            await client.CancelRunAsync(id).WaitAsync(contextToken);
            if (Current(client, version)) await RefreshHistory();
        }
        catch (OperationCanceledException) when (disposed) { }
        catch { if (Current(client, version)) actionError = "Could not request run cancellation. Check its recorded status before retrying."; }
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed) return;
        disposed = true;
        await lifetime.CancelAsync();
        if (polling is not null) await polling;
        contextLifetime?.Dispose();
        lifetime.Dispose();
    }
}
