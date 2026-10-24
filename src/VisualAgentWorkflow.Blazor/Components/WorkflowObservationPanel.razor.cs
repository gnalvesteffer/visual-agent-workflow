using Microsoft.AspNetCore.Components;
using VisualAgentWorkflow.Core;

namespace VisualAgentWorkflow.Blazor.Components;

public partial class WorkflowObservationPanel
{
    [Parameter, EditorRequired] public IWorkflowRunObserver Observer { get; set; } = default!;
    [Parameter] public string SourceLabel { get; set; } = "Workflow runs";
    private IReadOnlyList<RunSummary> runs = [];
    private RunSnapshot? snapshot;
    private Guid? selectedId;
    private string? error;
    private bool interactive;
    private bool disposed;
    private long sourceVersion;
    private long selectionVersion;
    private IWorkflowRunObserver? boundObserver;
    private readonly CancellationTokenSource lifetime = new();
    private CancellationTokenSource? sourceLifetime;
    private Task? polling;

    protected override async Task OnParametersSetAsync()
    {
        if (disposed || ReferenceEquals(boundObserver, Observer)) return;
        sourceLifetime?.Cancel();
        sourceLifetime?.Dispose();
        sourceLifetime = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        boundObserver = Observer;
        sourceVersion++;
        selectionVersion++;
        runs = []; snapshot = null; selectedId = null; error = null;
        await RefreshAsync();
    }

    protected override void OnAfterRender(bool firstRender)
    {
        if (!firstRender || disposed) return;
        interactive = true;
        polling = PollAsync();
        StateHasChanged();
    }

    private bool Current(IWorkflowRunObserver observer, long version) =>
        !disposed && version == sourceVersion && ReferenceEquals(observer, boundObserver);

    private async Task RefreshAsync()
    {
        var observer = Observer;
        var version = sourceVersion;
        var selection = selectionVersion;
        var token = sourceLifetime!.Token;
        try
        {
            var latest = await observer.ListRunsAsync(token).WaitAsync(token);
            if (!Current(observer, version)) return;
            runs = latest.Take(50).ToArray();
            if (selection != selectionVersion) return;
            if (selectedId is null && runs.FirstOrDefault() is { } first)
            {
                selectedId = first.RunId;
                selection = ++selectionVersion;
            }
            if (selectedId is { } id && (snapshot is null || runs.FirstOrDefault(r => r.RunId == id)?.LastSequence != snapshot.Events.Length))
                await LoadSelectedAsync(observer, version, selection, id, token);
            if (Current(observer, version) && selection == selectionVersion) error = null;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch
        {
            if (Current(observer, version) && selection == selectionVersion) error = "Observation unavailable. Showing the last received evidence. Check the consumer connection and access policy.";
        }
    }

    private async Task SelectAsync(Guid id)
    {
        selectedId = id;
        snapshot = null;
        var observer = Observer;
        var version = sourceVersion;
        var selection = ++selectionVersion;
        var token = sourceLifetime!.Token;
        try
        {
            await LoadSelectedAsync(observer, version, selection, id, token);
            if (Current(observer, version) && selection == selectionVersion) error = null;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch
        {
            if (Current(observer, version) && selection == selectionVersion)
                error = "The selected run could not be observed. Check the consumer connection and access policy.";
        }
    }

    private async Task LoadSelectedAsync(IWorkflowRunObserver observer, long version, long selection, Guid id, CancellationToken token)
    {
        var received = await observer.GetRunAsync(id, token).WaitAsync(token);
        if (!Current(observer, version) || selection != selectionVersion || selectedId != id) return;
        if (snapshot is not null && received is not null)
        {
            if (received.Header.RevisionId != snapshot.Header.RevisionId || received.Header.ContentHash != snapshot.Header.ContentHash)
                throw new InvalidDataException("A recorded run changed identity.");
            if (received.Events.Length < snapshot.Events.Length) return; // A slower refresh cannot rewind live evidence.
            if (!received.Events.Take(snapshot.Events.Length).SequenceEqual(snapshot.Events))
                throw new InvalidDataException("Recorded evidence changed.");
        }
        snapshot = received;
    }

    private async Task PollAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(500));
        try
        {
            while (await timer.WaitForNextTickAsync(lifetime.Token))
                await InvokeAsync(async () => { await RefreshAsync(); if (!disposed) StateHasChanged(); });
        }
        catch (OperationCanceledException) when (disposed) { }
        catch (ObjectDisposedException) when (disposed) { }
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed) return;
        disposed = true;
        await lifetime.CancelAsync();
        sourceLifetime?.Cancel();
        if (polling is not null) await polling;
        sourceLifetime?.Dispose();
        lifetime.Dispose();
    }
}
