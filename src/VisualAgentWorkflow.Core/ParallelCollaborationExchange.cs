using System.Collections.Immutable;
using System.Text.Json;

namespace VisualAgentWorkflow.Core;

/// <summary>
/// Non-blocking state for one bounded, single-rendezvous exchange in a parallel region.
/// The workflow scheduler owns parking and resuming branches; this type never waits for peers.
/// </summary>
internal sealed class ParallelCollaborationExchange
{
    public const int MaximumChannels = 8;
    public const int MaximumMessagesPerChannel = 32;
    public const int MaximumMessagesPerExchange = 64;
    public const int MaximumMessageCharacters = 4_000;
    public const int MaximumSnapshotCharacters = ParallelGraphLimits.MaximumAggregateCharacters;

    private readonly object sync = new();
    private readonly ImmutableHashSet<string> participants;
    private readonly ImmutableHashSet<string> channels;
    private readonly Dictionary<string, List<ParallelCollaborationMessage>> messages = new(StringComparer.Ordinal);
    private readonly HashSet<string> arrivals = new(StringComparer.Ordinal);
    private long nextSequence;
    private ParallelCollaborationSnapshot? snapshot;
    private string? abortReason;

    public ParallelCollaborationExchange(Guid runId, Guid regionInvocationId,
        IEnumerable<string> branchIds, IEnumerable<string> channelIds)
    {
        if (runId == Guid.Empty) throw new ArgumentException("A run identity is required.", nameof(runId));
        if (regionInvocationId == Guid.Empty) throw new ArgumentException("A region invocation identity is required.", nameof(regionInvocationId));

        var branches = branchIds.ToArray();
        if (branches.Length is < ParallelBranchLimits.MinimumBranches or > ParallelGraphLimits.MaximumExplicitBranches ||
            branches.Any(branch => !ParallelBranchLimits.IsValidId(branch)) ||
            branches.Distinct(StringComparer.Ordinal).Count() != branches.Length)
            throw new ArgumentException("A collaboration exchange requires 2–8 unique stable branch IDs.", nameof(branchIds));

        var configuredChannels = channelIds.ToArray();
        if (configuredChannels.Length is < 1 or > MaximumChannels ||
            configuredChannels.Any(channel => !IsValidChannelId(channel)) ||
            configuredChannels.Distinct(StringComparer.Ordinal).Count() != configuredChannels.Length)
            throw new ArgumentException($"A collaboration exchange requires 1–{MaximumChannels} unique channel IDs.", nameof(channelIds));

        RunId = runId;
        RegionInvocationId = regionInvocationId;
        participants = [.. branches];
        channels = [.. configuredChannels];
    }

    public Guid RunId { get; }
    public Guid RegionInvocationId { get; }
    public bool IsReleased { get { lock (sync) return snapshot is not null; } }
    public bool IsAborted { get { lock (sync) return abortReason is not null; } }
    public string? AbortReason { get { lock (sync) return abortReason; } }

    /// <summary>Appends one bounded message while its branch is still in the publish phase.</summary>
    public ParallelCollaborationMessage Send(string branchId, string channel, string content)
    {
        lock (sync)
        {
            ThrowIfAborted();
            RequireParticipant(branchId);
            RequireChannel(channel);
            if (snapshot is not null || arrivals.Contains(branchId))
                throw new InvalidOperationException("Messages can only be sent before the branch reaches the rendezvous.");
            if (string.IsNullOrWhiteSpace(content) || content.Length > MaximumMessageCharacters)
                throw new ArgumentException($"A message must contain 1–{MaximumMessageCharacters} characters.", nameof(content));

            var channelMessages = messages.GetValueOrDefault(channel);
            if (nextSequence >= MaximumMessagesPerExchange || channelMessages is { Count: >= MaximumMessagesPerChannel })
                throw new InvalidOperationException("The collaboration message limit was reached.");

            var message = new ParallelCollaborationMessage(++nextSequence, branchId, channel, content);
            (channelMessages ??= messages[channel] = []).Add(message);
            return message;
        }
    }

    /// <summary>
    /// Records a participant arrival and returns immediately. The scheduler parks the branch when
    /// <see cref="ParallelCollaborationArrival.Released"/> is false, without occupying a worker.
    /// </summary>
    public ParallelCollaborationArrival Arrive(string branchId)
    {
        lock (sync)
        {
            ThrowIfAborted();
            if (!participants.Contains(branchId))
                throw AbortExchange("A branch outside the collaboration group reached the rendezvous.");
            if (snapshot is not null)
                throw AbortExchange("A branch arrived after the collaboration rendezvous released.");
            if (!arrivals.Add(branchId))
                throw AbortExchange("A collaboration branch arrived at the rendezvous more than once.");

            if (arrivals.Count != participants.Count)
                return new(false, null, arrivals.Count, participants.Count);

            var capturedMessages = messages.ToImmutableSortedDictionary(
                entry => entry.Key,
                entry => entry.Value.ToImmutableArray(),
                StringComparer.Ordinal);
            var serializedSnapshot = JsonSerializer.Serialize(capturedMessages);
            if (serializedSnapshot.Length > MaximumSnapshotCharacters)
            {
                abortReason = "The collaboration snapshot exceeded its size limit.";
                throw new InvalidOperationException(abortReason);
            }

            snapshot = new(capturedMessages, serializedSnapshot.Length);
            return new(true, snapshot, arrivals.Count, participants.Count);
        }
    }

    /// <summary>Returns the immutable release snapshot for an authorized participant and channel.</summary>
    public ImmutableArray<ParallelCollaborationMessage> ReadSnapshot(string branchId, string channel)
    {
        lock (sync)
        {
            ThrowIfAborted();
            RequireParticipant(branchId);
            RequireChannel(channel);
            if (snapshot is null)
                throw new InvalidOperationException("The collaboration snapshot is not available before all branches arrive.");
            return snapshot.MessagesByChannel.GetValueOrDefault(channel, ImmutableArray<ParallelCollaborationMessage>.Empty);
        }
    }

    /// <summary>Aborts the exchange so it cannot release or expose a partial snapshot.</summary>
    public void Abort(string reason)
    {
        if (string.IsNullOrWhiteSpace(reason) || reason.Length > 256)
            throw new ArgumentException("An abort reason must contain 1–256 characters.", nameof(reason));

        lock (sync)
        {
            abortReason ??= reason;
        }
    }

    private void RequireParticipant(string branchId)
    {
        if (!participants.Contains(branchId))
            throw new InvalidOperationException("The branch is not a participant in this collaboration exchange.");
    }

    private void RequireChannel(string channel)
    {
        if (!channels.Contains(channel))
            throw new InvalidOperationException("The channel is not configured for this collaboration exchange.");
    }

    private void ThrowIfAborted()
    {
        if (abortReason is not null)
            throw new InvalidOperationException($"The collaboration exchange was aborted: {abortReason}");
    }

    private InvalidOperationException AbortExchange(string reason)
    {
        abortReason = reason;
        return new(reason);
    }

    private static bool IsValidChannelId(string? id) => id is { Length: > 0 and <= 64 } && id[0] is >= 'a' and <= 'z' &&
        id.All(character => character is >= 'a' and <= 'z' or >= '0' and <= '9' or '.' or '-' or '_');
}

internal sealed record ParallelCollaborationMessage(long Sequence, string BranchId, string Channel, string Content);

internal sealed record ParallelCollaborationSnapshot(
    ImmutableSortedDictionary<string, ImmutableArray<ParallelCollaborationMessage>> MessagesByChannel,
    int SerializedCharacters);

internal sealed record ParallelCollaborationArrival(
    bool Released,
    ParallelCollaborationSnapshot? Snapshot,
    int ArrivedCount,
    int ParticipantCount);
