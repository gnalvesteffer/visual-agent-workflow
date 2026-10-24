using System.Net;
using System.Text.Json;
using VisualAgentWorkflow.Core;

namespace VisualAgentWorkflow.Observation.Http;

/// <summary>Read-only HTTP evidence client. The host owns HttpClient lifetime, credentials and TLS policy.
/// Configure its handler to reject redirects so credentials never follow an endpoint redirect.</summary>
public sealed class HttpWorkflowRunObserver : IWorkflowRunObserver, IWorkflowRunReconciliationObserver
{
    public const int MaximumResponseBytes = 8 * 1024 * 1024;
    private readonly HttpClient client;
    private readonly Uri endpoint;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public HttpWorkflowRunObserver(HttpClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
        endpoint = client.BaseAddress ?? throw new ArgumentException("An observation base address is required.", nameof(client));
        if (!endpoint.IsAbsoluteUri || endpoint.Scheme is not ("https" or "http") ||
            endpoint.UserInfo.Length != 0 || endpoint.Query.Length != 0 || endpoint.Fragment.Length != 0 || !endpoint.AbsolutePath.EndsWith('/'))
            throw new ArgumentException("Use an absolute HTTP(S) base address ending in '/', without credentials, query or fragment.", nameof(client));
        this.client = client;
    }

    public async Task<IReadOnlyList<RunSummary>> ListRunsAsync(CancellationToken cancellationToken = default)
    {
        var summaries = await ReadAsync<RunSummary[]>("runs", cancellationToken) ?? throw new InvalidDataException("Missing run summaries.");
        if (summaries.Length > 50 || summaries.Any(r => r is null || r.RunId == Guid.Empty || r.RevisionId == Guid.Empty || r.LastSequence < 0) ||
            summaries.Select(r => r.RunId).Distinct().Count() != summaries.Length)
            throw new InvalidDataException("Invalid run summaries.");
        return summaries;
    }

    public async Task<RunSnapshot?> GetRunAsync(Guid runId, CancellationToken cancellationToken = default)
    {
        var snapshot = await ReadAsync<RunSnapshot>($"runs/{runId:D}", cancellationToken);
        if (snapshot is null) return null;
        if (snapshot.Header is null || snapshot.Header.Definition is null || snapshot.Header.RunId != runId ||
            snapshot.Header.RevisionId == Guid.Empty || snapshot.Events.IsDefault ||
            WorkflowJson.ComputeHash(snapshot.Header.Definition) != snapshot.Header.ContentHash ||
            snapshot.Events.Where((e, i) => e is null || e.RunId != runId || e.RevisionId != snapshot.Header.RevisionId || e.Sequence != i + 1).Any())
            throw new InvalidDataException("Invalid run identity, definition hash or event sequence.");
        return snapshot;
    }

    public async Task<RunEventPage?> GetEventsAsync(Guid runId, long afterSequence, int limit = 256, CancellationToken cancellationToken = default)
    {
        if (afterSequence < 0 || limit is < 1 or > 256) throw new ArgumentOutOfRangeException(nameof(afterSequence));
        var page = await ReadAsync<RunEventPage>($"runs/{runId:D}/events?after={afterSequence}&limit={limit}", cancellationToken);
        if (page is null) return null;
        if (page.RunId != runId || page.RevisionId == Guid.Empty || page.AfterSequence != afterSequence || page.Events.IsDefault ||
            page.LastSequence < afterSequence || page.Events.Length > limit ||
            page.Events.Where((e, i) => e is null || e.RunId != runId || e.RevisionId != page.RevisionId || e.Sequence != afterSequence + i + 1).Any() ||
            page.LastSequence < afterSequence + page.Events.Length ||
            page.HasMore != (afterSequence + page.Events.Length < page.LastSequence) || page.HasMore && page.Events.IsEmpty)
            throw new InvalidDataException("Invalid observation cursor page.");
        return page;
    }

    public async Task<RunReconciliation?> GetReconciliationAsync(Guid runId, CancellationToken cancellationToken = default)
    {
        var report = await ReadAsync<RunReconciliation>($"runs/{runId:D}/reconciliation", cancellationToken, optional: true);
        if (report is null) return null;
        var validHash = report.ContentHash is { Length: 64 } && report.ContentHash.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
        if (report.RunId != runId || report.RevisionId == Guid.Empty || report.LastSequence < 0 || !validHash ||
            !Enum.IsDefined(report.Disposition) || string.IsNullOrWhiteSpace(report.Status) || report.Status.Length > 64 ||
            string.IsNullOrWhiteSpace(report.Reason) || report.Reason.Length > 1024 || report.UnresolvedWork.IsDefault ||
            report.UnresolvedWork.Length > WorkflowWorkEvidence.MaximumReturnedWork || report.TotalUnresolvedWork < report.UnresolvedWork.Length ||
            report.UnresolvedWork.Length != Math.Min(report.TotalUnresolvedWork, WorkflowWorkEvidence.MaximumReturnedWork) ||
            report.UnresolvedWork.Any(w => w is null || !Enum.IsDefined(w.Kind) || !Enum.IsDefined(w.State) ||
                w.RequestedSequence < 1 || w.LastObservedSequence < w.RequestedSequence || w.LastObservedSequence > report.LastSequence ||
                w.InvocationId == Guid.Empty || w.CallId == Guid.Empty) ||
            report.Checkpoint is { } frame && (string.IsNullOrWhiteSpace(frame.CheckpointId) || frame.CheckpointId.Length > 128 ||
                string.IsNullOrWhiteSpace(frame.RuntimeVersion) || frame.RuntimeVersion.Length > 128 || frame.Sequence < 1 || frame.Sequence > report.LastSequence) ||
            report.CanResume != (report.Disposition == RunRecoveryDisposition.SavedSuspension) ||
            report.CanResume && (report.Status != "suspended" || report.Checkpoint is null || report.Checkpoint.Sequence != report.LastSequence ||
                report.TotalUnresolvedWork != 0 || report.HasIncompleteCorrelation))
            throw new InvalidDataException("Invalid reconciliation identity, cursor or recovery claim.");
        return report;
    }

    private async Task<T?> ReadAsync<T>(string relativePath, CancellationToken cancellationToken, bool optional = false) where T : class
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(endpoint, relativePath));
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        if (optional && response.StatusCode == HttpStatusCode.NotImplemented) throw new NotSupportedException("The consumer does not provide reconciliation assessments.");
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > MaximumResponseBytes) throw new InvalidDataException("Observation response exceeds 8 MiB.");
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream();
        var bytes = new byte[16_384];
        while (true)
        {
            var read = await stream.ReadAsync(bytes, cancellationToken);
            if (read == 0) break;
            if (buffer.Length + read > MaximumResponseBytes) throw new InvalidDataException("Observation response exceeds 8 MiB.");
            buffer.Write(bytes, 0, read);
        }
        return JsonSerializer.Deserialize<T>(buffer.GetBuffer().AsSpan(0, (int)buffer.Length), Json)
            ?? throw new InvalidDataException("Empty observation response.");
    }
}
