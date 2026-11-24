namespace VisualAgentWorkflow.Hosting;

/// <summary>
/// Host-owned settings for how a production Sequence waits for in-flight outputs after a failure or cancellation.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="AbandonGrace"/> is a single host-wide bound: workflow authors and run input cannot set or override it,
/// so it applies equally to every workflow the host runs, including ones with side-effecting tools. <c>null</c>
/// (the default) keeps today's behavior: the run waits for every in-flight output indefinitely.
/// </para>
/// <para>
/// When the bound elapses after a failure or cancellation, each output that never reported is recorded
/// <c>abandoned</c>, the Sequence ends failed, and the run ends <c>run.outcome-unknown</c>, even if the host also
/// cancelled it. An abandoned executor is not stopped: it keeps running in-process, its external effects may still
/// land, its admission slot stays open, and anything it writes after the run terminal is dropped. Reconciliation
/// reports the abandoned output as unresolved work in the unknown-outcome state and never as resumable.
/// </para>
/// <para>
/// A child workflow's own Sequence does not inherit the bound. The checkpoint host and the standalone service
/// sample build their own runners and ignore this option.
/// </para>
/// </remarks>
public sealed record SequenceDrainOptions
{
    /// <summary>The largest accepted <see cref="AbandonGrace"/>.</summary>
    public static readonly TimeSpan MaximumAbandonGrace = TimeSpan.FromHours(24);

    /// <summary>Creates options; a non-null grace must be positive, finite, and no larger than <see cref="MaximumAbandonGrace"/>.</summary>
    public SequenceDrainOptions(TimeSpan? abandonGrace = null)
    {
        if (abandonGrace is { } grace &&
            (grace <= TimeSpan.Zero || grace == Timeout.InfiniteTimeSpan || grace > MaximumAbandonGrace))
            throw new ArgumentOutOfRangeException(nameof(abandonGrace), abandonGrace,
                $"The abandon grace must be positive, finite, and at most {MaximumAbandonGrace}.");
        AbandonGrace = abandonGrace;
    }

    /// <summary>How long to wait for in-flight outputs before abandoning them, or <c>null</c> to wait indefinitely.</summary>
    public TimeSpan? AbandonGrace { get; }
}
