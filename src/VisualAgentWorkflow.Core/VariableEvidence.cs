using System.Text.Json;

namespace VisualAgentWorkflow.Core;

/// <summary>Versioned, passive evidence for schema-1.44 variable reads and writes.</summary>
public sealed record VariableStateEvidence(string VariableId, string DataType, string Value,
    string? PreviousValue, long Version, int LoopIteration = 0, int EvidenceVersion = 1);

public static class VariableEvidence
{
    private const int MaximumValueLength = 16_384;

    public static bool IsStateEvent(string kind) => kind is ExecutionEventKinds.VariableRead or ExecutionEventKinds.VariableWritten;

    public static bool TryRead(ExecutionEvent executionEvent, out VariableStateEvidence? evidence)
    {
        ArgumentNullException.ThrowIfNull(executionEvent);
        evidence = null;
        if (!IsStateEvent(executionEvent.Kind) || string.IsNullOrWhiteSpace(executionEvent.Data)) return false;
        try
        {
            using var document = JsonDocument.Parse(executionEvent.Data);
            var root = document.RootElement;
            if (!TryString(root, "variableId", out var variableId) || !TryString(root, "dataType", out var dataType) ||
                !TryValue(root, "value", out var value) || value.Length > MaximumValueLength ||
                !root.TryGetProperty("version", out var versionElement) || !versionElement.TryGetInt64(out var version) || version < 1)
                return false;
            string? previous = null;
            if (root.TryGetProperty("previousValue", out var previousElement))
            {
                if (!TryValue(previousElement, out previous) || previous.Length > MaximumValueLength) return false;
            }
            if (executionEvent.Kind == ExecutionEventKinds.VariableWritten && !root.TryGetProperty("previousValue", out _)) return false;
            var loopIteration = 0;
            if (root.TryGetProperty("loopIteration", out var loopElement) &&
                (!loopElement.TryGetInt32(out loopIteration) || loopIteration < 0)) return false;
            evidence = new(variableId, dataType, value, previous, version, loopIteration);
            return WorkflowVariableStore.Parse(dataType, value) is not null &&
                (previous is null || WorkflowVariableStore.Parse(dataType, previous) is not null);
        }
        catch (JsonException) { return false; }
        catch (FormatException) { return false; }
        catch (OverflowException) { return false; }
        catch (ArgumentException) { return false; }
    }

    /// <summary>Reconstructs the latest variable state from an event prefix only.</summary>
    public static bool TryReconstructPlayback(IEnumerable<ExecutionEvent> executionEvents,
        out VariablePlaybackSnapshot? snapshot)
    {
        ArgumentNullException.ThrowIfNull(executionEvents);
        snapshot = null;
        var events = executionEvents.Where(evt => IsStateEvent(evt.Kind)).OrderBy(evt => evt.Sequence).ToArray();
        if (events.Length == 0) return false;
        var values = new Dictionary<string, VariablePlaybackValue>(StringComparer.Ordinal);
        var operations = new List<VariablePlaybackOperation>();
        string? scope = null;
        foreach (var executionEvent in events)
        {
            if (!TryRead(executionEvent, out var evidence) || evidence is null || executionEvent.NodeId is null) return false;
            if (scope is null) scope = executionEvent.ScopeId;
            else if (!string.Equals(scope, executionEvent.ScopeId, StringComparison.Ordinal)) return false;
            if (values.TryGetValue(evidence.VariableId, out var current))
            {
                if (!string.Equals(current.DataType, evidence.DataType, StringComparison.Ordinal) || evidence.Version < current.Version)
                    return false;
            }
            if (executionEvent.Kind == ExecutionEventKinds.VariableWritten)
            {
                if (evidence.PreviousValue is null || values.TryGetValue(evidence.VariableId, out current) && evidence.PreviousValue != current.Value ||
                    values.TryGetValue(evidence.VariableId, out current) && evidence.Version <= current.Version)
                    return false;
                values[evidence.VariableId] = new(evidence.DataType, evidence.Value, evidence.Version);
            }
            else
            {
                if (values.TryGetValue(evidence.VariableId, out current) &&
                    (evidence.Version != current.Version || evidence.Value != current.Value)) return false;
                values[evidence.VariableId] = new(evidence.DataType, evidence.Value, evidence.Version);
            }
            operations.Add(new(executionEvent.Sequence, executionEvent.Kind == ExecutionEventKinds.VariableWritten ? "write" : "read",
                evidence.VariableId, evidence.DataType, evidence.Value, evidence.PreviousValue, evidence.Version,
                evidence.LoopIteration, executionEvent.NodeId));
        }
        snapshot = new(scope, values, operations);
        return true;
    }

    private static bool TryString(JsonElement parent, string property, out string value)
    {
        value = "";
        if (!parent.TryGetProperty(property, out var element) || element.ValueKind != JsonValueKind.String) return false;
        value = element.GetString() ?? "";
        return !string.IsNullOrWhiteSpace(value) && value.Length <= 128;
    }

    private static bool TryValue(JsonElement parent, string property, out string value)
    {
        value = "";
        return parent.TryGetProperty(property, out var element) && TryValue(element, out value);
    }

    private static bool TryValue(JsonElement element, out string value)
    {
        value = element.ValueKind switch
        {
            JsonValueKind.String => element.GetString() ?? "",
            JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => element.ToString(),
            _ => ""
        };
        return element.ValueKind is JsonValueKind.String or JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False;
    }
}

public sealed record VariablePlaybackValue(string DataType, string Value, long Version);
public sealed record VariablePlaybackOperation(long Sequence, string Kind, string VariableId, string DataType,
    string Value, string? PreviousValue, long Version, int LoopIteration, string NodeId);
public sealed record VariablePlaybackSnapshot(string? ScopeId,
    IReadOnlyDictionary<string, VariablePlaybackValue> Values,
    IReadOnlyList<VariablePlaybackOperation> Operations);
