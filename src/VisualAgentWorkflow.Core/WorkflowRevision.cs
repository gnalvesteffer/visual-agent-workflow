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
    {
        if (string.IsNullOrWhiteSpace(author)) throw new ArgumentException("An author is required.", nameof(author));
        WorkflowValidator.ThrowIfInvalid(definition);
        RevisionId = Guid.NewGuid();
        ParentRevisionId = parentRevisionId;
        Author = author;
        CreatedAt = DateTimeOffset.UtcNow;
        // Round-trip also normalizes dictionary comparison/order independently of caller construction.
        Definition = WorkflowJson.Deserialize(WorkflowJson.Serialize(definition));
        ContentHash = WorkflowJson.ComputeHash(Definition);
    }

    public static WorkflowRevision Create(WorkflowDefinition definition, string author, Guid? parentRevisionId = null) =>
        new(definition, author, parentRevisionId);

    public static WorkflowRevision Restore(WorkflowRevision historical, WorkflowRevision current, string author)
    {
        ArgumentNullException.ThrowIfNull(historical);
        ArgumentNullException.ThrowIfNull(current);
        if (historical.Definition.WorkflowId != current.Definition.WorkflowId)
            throw new ArgumentException("Cannot restore a revision from a different workflow.");
        return Create(historical.Definition, author, current.RevisionId);
    }
}
