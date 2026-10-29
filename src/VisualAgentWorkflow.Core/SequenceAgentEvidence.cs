using System.Text.Json;

namespace VisualAgentWorkflow.Core;

/// <summary>Passive playback projection for the bounded Sequence terminal-Agent path.</summary>
public sealed record SequenceAgentPlaybackSnapshot(
    SequencePlaybackSnapshot Sequence,
    IReadOnlyList<SequenceAgentInvocationPlayback> Agents,
    IReadOnlyList<SequenceModelCallPlayback> ModelCalls);

public sealed record SequenceAgentInvocationPlayback(
    string NodeId,
    Guid InvocationId,
    string OutputId,
    int Ordinal,
    Guid ActivationId,
    string ConnectionId,
    string State);

public sealed record SequenceModelCallPlayback(
    string NodeId,
    Guid AgentInvocationId,
    Guid ModelCallId,
    string State);

/// <summary>Reads persisted Sequence/Agent/model lineage without invoking runtime services.</summary>
public static class SequenceAgentEvidence
{
    private static readonly HashSet<string> DescendantKinds = new(StringComparer.Ordinal)
    {
        ExecutionEventKinds.AgentStarted,
        ExecutionEventKinds.AgentCompleted,
        ExecutionEventKinds.AgentFailed,
        ExecutionEventKinds.AgentLimitExceeded,
        ExecutionEventKinds.ModelRequested,
        ExecutionEventKinds.ModelResponded,
        ExecutionEventKinds.AgentContextCompacted
    };

    public static bool TryReconstructPlayback(IEnumerable<ExecutionEvent> executionEvents,
        out SequenceAgentPlaybackSnapshot? snapshot)
    {
        ArgumentNullException.ThrowIfNull(executionEvents);
        snapshot = null;
        var events = executionEvents.OrderBy(evt => evt.Sequence).ToArray();
        if (!SequenceEvidence.TryReconstructPlayback(events, out var sequence) || sequence is null)
            return false;

        var outputByActivation = sequence.Outputs
            .Where(output => output.ActivationId is not null)
            .ToDictionary(output => output.ActivationId!.Value);
        var agents = new Dictionary<(string NodeId, Guid InvocationId), AgentState>();
        var models = new Dictionary<Guid, ModelState>();
        foreach (var executionEvent in events.Where(evt => DescendantKinds.Contains(evt.Kind)))
        {
            if (executionEvent.InvocationId is not { } invocationId ||
                !TryReadLineage(executionEvent.Data, out var lineage) ||
                lineage is null || lineage.OutputActivationId == Guid.Empty ||
                !outputByActivation.TryGetValue(lineage.OutputActivationId, out var output) ||
                output.OutputId != lineage.OutputId || output.Ordinal != lineage.Ordinal ||
                output.ConnectionId != lineage.ConnectionId || string.IsNullOrWhiteSpace(executionEvent.NodeId))
                return false;

            if (executionEvent.Kind is ExecutionEventKinds.ModelRequested or ExecutionEventKinds.ModelResponded)
            {
                if (!TryReadModelCallId(executionEvent.Data, out var modelCallId)) return false;
                if (!models.TryGetValue(modelCallId, out var model))
                {
                    model = new(executionEvent.NodeId!, invocationId, false, false);
                    models.Add(modelCallId, model);
                }
                else if (model.NodeId != executionEvent.NodeId || model.AgentInvocationId != invocationId)
                    return false;
                if (executionEvent.Kind == ExecutionEventKinds.ModelRequested)
                {
                    if (model.Requested) return false;
                    models[modelCallId] = model with { Requested = true };
                }
                else
                {
                    if (!model.Requested || model.Responded) return false;
                    models[modelCallId] = model with { Responded = true };
                }
                continue;
            }

            var key = (executionEvent.NodeId!, invocationId);
            if (!agents.TryGetValue(key, out var agent))
            {
                agent = new(executionEvent.NodeId!, invocationId, lineage.OutputId, lineage.Ordinal,
                    lineage.OutputActivationId, lineage.ConnectionId, false, null);
                agents.Add(key, agent);
            }
            else if (agent.OutputId != lineage.OutputId || agent.Ordinal != lineage.Ordinal ||
                agent.ActivationId != lineage.OutputActivationId || agent.ConnectionId != lineage.ConnectionId)
                return false;

            switch (executionEvent.Kind)
            {
                case ExecutionEventKinds.AgentStarted:
                    if (agent.Started) return false;
                    agents[key] = agent with { Started = true };
                    break;
                case ExecutionEventKinds.AgentCompleted:
                    if (!agent.Started || agent.TerminalState is not null) return false;
                    agents[key] = agent with { TerminalState = "completed" };
                    break;
                case ExecutionEventKinds.AgentFailed:
                    if (!agent.Started || agent.TerminalState is not null) return false;
                    agents[key] = agent with { TerminalState = "failed" };
                    break;
                case ExecutionEventKinds.AgentLimitExceeded:
                    if (!agent.Started) return false;
                    break;
            }
        }

        foreach (var agent in agents.Values)
            if (!agent.Started) return false;
        foreach (var model in models.Values)
            if (!model.Requested) return false;

        snapshot = new(sequence,
            agents.Values.Select(agent => new SequenceAgentInvocationPlayback(agent.NodeId, agent.InvocationId,
                agent.OutputId, agent.Ordinal, agent.ActivationId, agent.ConnectionId, agent.TerminalState ?? "running"))
                .OrderBy(agent => agent.Ordinal).ThenBy(agent => agent.InvocationId).ToArray(),
            models.Select(pair => new SequenceModelCallPlayback(pair.Value.NodeId, pair.Value.AgentInvocationId,
                pair.Key, pair.Value.Responded ? "completed" : "running"))
                .OrderBy(model => model.ModelCallId).ToArray());
        return true;
    }

    private static bool TryReadLineage(string? data, out SequenceLineage? lineage)
    {
        lineage = null;
        if (string.IsNullOrWhiteSpace(data)) return false;
        try
        {
            using var document = JsonDocument.Parse(data, new JsonDocumentOptions { MaxDepth = 32 });
            if (!document.RootElement.TryGetProperty("sequenceLineage", out var property)) return false;
            lineage = JsonSerializer.Deserialize<SequenceLineage>(property.GetRawText(), JsonSerializerOptions.Web);
            if (lineage is null) return false;
            return lineage.SequenceInvocationId != Guid.Empty && !string.IsNullOrWhiteSpace(lineage.SequenceNodeId) &&
                !string.IsNullOrWhiteSpace(lineage.OutputId) && lineage.Ordinal >= 0 &&
                lineage.OutputActivationId != Guid.Empty && !string.IsNullOrWhiteSpace(lineage.ConnectionId);
        }
        catch (JsonException) { return false; }
    }

    private static bool TryReadModelCallId(string? data, out Guid modelCallId)
    {
        modelCallId = Guid.Empty;
        if (string.IsNullOrWhiteSpace(data)) return false;
        try
        {
            using var document = JsonDocument.Parse(data, new JsonDocumentOptions { MaxDepth = 32 });
            return document.RootElement.TryGetProperty("modelCallId", out var property) &&
                property.ValueKind == JsonValueKind.String && Guid.TryParse(property.GetString(), out modelCallId) &&
                modelCallId != Guid.Empty;
        }
        catch (JsonException) { return false; }
    }

    private sealed record SequenceLineage(Guid SequenceInvocationId, string SequenceNodeId,
        string OutputId, int Ordinal, Guid OutputActivationId, string ConnectionId, string? DescendantScopeId);

    private sealed record AgentState(string NodeId, Guid InvocationId, string OutputId, int Ordinal,
        Guid ActivationId, string ConnectionId, bool Started, string? TerminalState);

    private sealed record ModelState(string NodeId, Guid AgentInvocationId, bool Requested, bool Responded);
}
