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

    /// <summary>Optional authoring provenance for runs started from a local source workspace.</summary>
    public WorkflowRunProvenance? SourceProvenance { get; init; }

    /// <summary>Optional captured presentation layout. It never affects executable content hashing.</summary>
    public WorkflowLayout? Layout { get; init; }

    private WorkflowRevision(WorkflowDefinition definition, string author, Guid? parentRevisionId)
        : this(Guid.NewGuid(), definition, author, parentRevisionId, DateTimeOffset.UtcNow) { }

    private WorkflowRevision(Guid revisionId, WorkflowDefinition definition, string author, Guid? parentRevisionId, DateTimeOffset createdAt)
        : this(revisionId, definition, author, parentRevisionId, createdAt, null) { }

    private WorkflowRevision(Guid revisionId, WorkflowDefinition definition, string author, Guid? parentRevisionId,
        DateTimeOffset createdAt, string? storedContentHash, bool validate = true)
    {
        if (string.IsNullOrWhiteSpace(author)) throw new ArgumentException("An author is required.", nameof(author));
        if (validate) WorkflowValidator.ThrowIfInvalid(definition);
        if (revisionId == Guid.Empty) throw new ArgumentException("A revision ID is required.", nameof(revisionId));
        RevisionId = revisionId;
        ParentRevisionId = parentRevisionId;
        Author = author;
        CreatedAt = createdAt;
        if (!validate)
        {
            Definition = definition;
            ContentHash = storedContentHash ?? string.Empty;
            return;
        }
        // Round-trip also normalizes dictionary comparison/order independently of caller construction.
        Definition = WorkflowJson.Deserialize(WorkflowJson.Serialize(definition));
        var computedHash = WorkflowJson.ComputeHash(Definition);
        if (storedContentHash is not null && !WorkflowJson.MatchesHash(Definition, storedContentHash))
            throw new ArgumentException("Stored revision content does not match its hash.", nameof(storedContentHash));
        ContentHash = storedContentHash ?? computedHash;
    }

    public static WorkflowRevision Create(WorkflowDefinition definition, string author, Guid? parentRevisionId = null) =>
        new(definition, author, parentRevisionId);

    /// <summary>Creates an internal, non-publishable revision for runtime proof harnesses.</summary>
    internal static WorkflowRevision CreateProofOnly(WorkflowDefinition definition, string author) =>
        new(Guid.NewGuid(), definition, author, null, DateTimeOffset.UtcNow, null, validate: false);

    /// <summary>Reconstruct stored evidence without generating another revision identity.</summary>
    public static WorkflowRevision Rehydrate(Guid revisionId, WorkflowDefinition definition, string author,
        Guid? parentRevisionId, DateTimeOffset createdAt, string contentHash)
    {
        return new WorkflowRevision(revisionId, definition, author, parentRevisionId, createdAt, contentHash);
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
