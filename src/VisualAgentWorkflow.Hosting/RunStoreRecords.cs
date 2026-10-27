using VisualAgentWorkflow.Core;

namespace VisualAgentWorkflow.Hosting;

internal static class RunStoreRecords
{
    public static RunSnapshot Create(WorkflowRevision revision, string input)
        => Create(revision, input, null);

    public static RunSnapshot Create(WorkflowRevision revision, string input,
        System.Collections.Immutable.ImmutableSortedDictionary<string, string>? parameters)
    {
        ArgumentNullException.ThrowIfNull(revision);
        ArgumentNullException.ThrowIfNull(input);
        if (input.Length > 16_384) throw new ArgumentException("Run input exceeds 16,384 characters.", nameof(input));
        parameters ??= WorkflowParameters.Resolve(revision.Definition, null);
        var header = new RunHeader(Guid.NewGuid(), revision.RevisionId, revision.ParentRevisionId, revision.Author,
            revision.CreatedAt, revision.ContentHash, revision.Definition, input)
        {
            Parameters = parameters,
            Layout = revision.Layout,
            SourceProvenance = revision.SourceProvenance
        };
        var snapshot = new RunSnapshot(header, []);
        Validate(snapshot, header.RunId);
        return snapshot;
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
        var next = snapshot with { Events = snapshot.Events.AddRange(batch) };
        ParallelCollaborationJournalValidation.Validate(next);
        return next;
    }

    public static void Validate(RunSnapshot snapshot, Guid runId)
    {
        if (snapshot.Header is null || snapshot.Header.Definition is null || snapshot.Events.IsDefault ||
            snapshot.Header.RunId != runId || snapshot.Header.RevisionId == Guid.Empty ||
            string.IsNullOrWhiteSpace(snapshot.Header.Author) || snapshot.Header.Input is null || snapshot.Header.Input.Length > 16_384 ||
            !WorkflowJson.MatchesHash(snapshot.Header.Definition, snapshot.Header.ContentHash))
            throw new InvalidDataException("Run header identity/content mismatch.");
        if (snapshot.Header.SourceProvenance is { } provenance)
        {
            if (snapshot.Header.Layout is null || provenance.SourceKind is not ("folder" or "restore-point") ||
                string.IsNullOrWhiteSpace(provenance.ProjectId) || provenance.ProjectId.Length > 80 ||
                provenance.WorkflowId != snapshot.Header.Definition.WorkflowId ||
                !IsSha256(provenance.ManifestHash) || !IsSha256(provenance.AssetHash) || !IsSha256(provenance.LayoutHash) ||
                !MatchesLayoutHash(snapshot.Header.Layout, snapshot.Header.Definition, provenance.LayoutHash) ||
                (provenance.SourceKind == "restore-point") != (provenance.SnapshotContentHash is not null) ||
                provenance.SnapshotContentHash is { } snapshotHash && !IsSha256(snapshotHash))
                throw new InvalidDataException("Run source provenance does not match its captured definition and layout.");
        }
        else if (snapshot.Header.Layout is { } presentation && !ValidPresentation(presentation, snapshot.Header.Definition))
            throw new InvalidDataException("Run layout does not match the captured definition or layout limits.");
        // This is immutable historical evidence. Do not reapply today's workflow validator to a
        // definition that was captured when the run was accepted; new runs are strictly validated
        // through WorkflowRevision.Create before they reach the store.
        var resolvedParameters = WorkflowParameters.Resolve(snapshot.Header.Definition, snapshot.Header.Parameters);
        if (resolvedParameters is null != (snapshot.Header.Parameters is null) ||
            resolvedParameters is not null && !resolvedParameters.SequenceEqual(snapshot.Header.Parameters!))
            throw new InvalidDataException("Run parameters do not match the pinned workflow parameter schema.");
        var terminal = false;
        long sequence = 0;
        foreach (var evt in snapshot.Events)
        {
            if (terminal || evt.RunId != runId || evt.RevisionId != snapshot.Header.RevisionId ||
                evt.Sequence != ++sequence || string.IsNullOrWhiteSpace(evt.Kind))
                throw new InvalidDataException("Invalid event identity/sequence or terminal history.");
            terminal = IsTerminal(evt.Kind);
        }
        ParallelCollaborationJournalValidation.Validate(snapshot);
    }

    private static bool IsTerminal(string kind) => kind is ExecutionEventKinds.Completed or ExecutionEventKinds.Terminated or ExecutionEventKinds.Failed or
        ExecutionEventKinds.Cancelled or ExecutionEventKinds.Interrupted;

    private static bool IsSha256(string value) => value is { Length: 64 } && value.All(character =>
        character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static bool ValidPresentation(WorkflowLayout layout, WorkflowDefinition definition)
    {
        try { _ = WorkflowLayoutJson.Serialize(layout, definition); return true; }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or NullReferenceException) { return false; }
    }

    private static bool MatchesLayoutHash(WorkflowLayout layout, WorkflowDefinition definition, string hash)
    {
        try { return WorkflowLayoutJson.MatchesHash(layout, definition, hash); }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or NullReferenceException) { return false; }
    }
}
