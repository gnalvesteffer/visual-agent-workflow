using VisualAgentWorkflow.Core;

namespace VisualAgentWorkflow.Hosting;

internal static class RunStoreRecords
{
    public static RunSnapshot Create(WorkflowRevision revision, string input)
    {
        ArgumentNullException.ThrowIfNull(revision);
        ArgumentNullException.ThrowIfNull(input);
        if (input.Length > 16_384) throw new ArgumentException("Run input exceeds 16,384 characters.", nameof(input));
        return new(new(Guid.NewGuid(), revision.RevisionId, revision.ParentRevisionId, revision.Author,
            revision.CreatedAt, revision.ContentHash, revision.Definition, input), []);
    }

    public static RunSnapshot Apply(RunSnapshot snapshot, long expectedSequence, IReadOnlyList<ExecutionEvent> events)
    {
        ArgumentNullException.ThrowIfNull(events);
        if (expectedSequence < 0 || events.Count is < 1 or > 256) throw new ArgumentOutOfRangeException(nameof(expectedSequence));
        var batch = events.ToArray();
        if (snapshot.Events.Length != expectedSequence) throw new WorkflowRunStoreConflictException();
        if (snapshot.IsTerminal) throw new InvalidOperationException("Run history is terminal.");
        var terminal = false;
        for (var index = 0; index < batch.Length; index++)
        {
            var evt = batch[index];
            if (evt is null || evt.RunId != snapshot.Header.RunId || evt.RevisionId != snapshot.Header.RevisionId ||
                evt.Sequence != expectedSequence + index + 1 || string.IsNullOrWhiteSpace(evt.Kind))
                throw new InvalidOperationException("Event identity, revision or sequence does not match the run.");
            if (terminal) throw new InvalidOperationException("No event may follow a terminal outcome.");
            terminal = IsTerminal(evt.Kind);
        }
        return snapshot with { Events = snapshot.Events.AddRange(batch) };
    }

    public static void Validate(RunSnapshot snapshot, Guid runId)
    {
        if (snapshot.Header.RunId != runId || snapshot.Header.RevisionId == Guid.Empty ||
            WorkflowJson.ComputeHash(snapshot.Header.Definition) != snapshot.Header.ContentHash)
            throw new InvalidDataException("Run header identity/content mismatch.");
        WorkflowValidator.ThrowIfInvalid(snapshot.Header.Definition);
        var terminal = false;
        long sequence = 0;
        foreach (var evt in snapshot.Events)
        {
            if (terminal || evt.RunId != runId || evt.RevisionId != snapshot.Header.RevisionId ||
                evt.Sequence != ++sequence || string.IsNullOrWhiteSpace(evt.Kind))
                throw new InvalidDataException("Invalid event identity/sequence or terminal history.");
            terminal = IsTerminal(evt.Kind);
        }
    }

    private static bool IsTerminal(string kind) => kind is ExecutionEventKinds.Completed or ExecutionEventKinds.Failed or
        ExecutionEventKinds.Cancelled or ExecutionEventKinds.Interrupted;
}
