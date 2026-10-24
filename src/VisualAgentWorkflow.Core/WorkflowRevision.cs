namespace VisualAgentWorkflow.Core;

/// <summary>An immutable release snapshot; publication/authorization/storage are host concerns.</summary>
public sealed record WorkflowRevision
{
    public Guid RevisionId { get; }
    public Guid? ParentRevisionId { get; }
    public string Author { get; }
    public DateTimeOffset CreatedAt { get; }
    public WorkflowDefinition Definition { get; }
    public string ContentHash { get; }

    private WorkflowRevision(WorkflowDefinition definition, string author, Guid? parentRevisionId)
        : this(Guid.NewGuid(), definition, author, parentRevisionId, DateTimeOffset.UtcNow) { }

    private WorkflowRevision(Guid revisionId, WorkflowDefinition definition, string author, Guid? parentRevisionId, DateTimeOffset createdAt)
    {
        if (string.IsNullOrWhiteSpace(author)) throw new ArgumentException("An author is required.", nameof(author));
        WorkflowValidator.ThrowIfInvalid(definition);
        if (revisionId == Guid.Empty) throw new ArgumentException("A revision ID is required.", nameof(revisionId));
        RevisionId = revisionId;
        ParentRevisionId = parentRevisionId;
        Author = author;
        CreatedAt = createdAt;
        // Round-trip also normalizes dictionary comparison/order independently of caller construction.
        Definition = WorkflowJson.Deserialize(WorkflowJson.Serialize(definition));
        ContentHash = WorkflowJson.ComputeHash(Definition);
    }

    public static WorkflowRevision Create(WorkflowDefinition definition, string author, Guid? parentRevisionId = null) =>
        new(definition, author, parentRevisionId);

    /// <summary>Reconstruct stored evidence without generating another revision identity.</summary>
    public static WorkflowRevision Rehydrate(Guid revisionId, WorkflowDefinition definition, string author,
        Guid? parentRevisionId, DateTimeOffset createdAt, string contentHash)
    {
        var revision = new WorkflowRevision(revisionId, definition, author, parentRevisionId, createdAt);
        if (!string.Equals(revision.ContentHash, contentHash, StringComparison.Ordinal))
            throw new ArgumentException("Stored revision content does not match its hash.", nameof(contentHash));
        return revision;
    }

    public static WorkflowRevision Restore(WorkflowRevision historical, WorkflowRevision current, string author)
    {
        ArgumentNullException.ThrowIfNull(historical);
        ArgumentNullException.ThrowIfNull(current);
        if (historical.Definition.WorkflowId != current.Definition.WorkflowId)
            throw new ArgumentException("Cannot restore a revision from a different workflow.");
        return Create(historical.Definition, author, current.RevisionId);
    }
}
