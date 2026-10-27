using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using VisualAgentWorkflow.Core;

namespace VisualAgentWorkflow.Hosting;

/// <summary>Validates the collaboration facts that are sufficient to reconstruct a journal prefix.</summary>
internal static class ParallelCollaborationJournalValidation
{
    private const int PayloadVersion = 1;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static void Validate(RunSnapshot snapshot)
    {
        var events = snapshot.Events.Where(evt => IsCollaborationEvent(evt.Kind)).ToArray();
        if (events.Length == 0) return;

        var definition = snapshot.Header.Definition;
        if (definition.SchemaVersion is not (WorkflowSchemaVersions.CollaborativeParallelGraph or WorkflowSchemaVersions.PhasedCollaborativeParallelGraph) ||
            definition.ParallelCollaborationRegions is not { Count: 1 } regions)
            throw Invalid("Collaboration events require one supported collaboration region.");

        var region = regions.Values.Single();
        // A future payload version is deliberately unavailable to this reader. Do not mistake its
        // data for a validated v1 history; Studio can still report that the evidence is unsupported.
        if (definition.SchemaVersion == WorkflowSchemaVersions.PhasedCollaborativeParallelGraph)
        {
            ValidatePhased(snapshot, events, region);
            return;
        }
        if (events.Any(evt => ReadVersion(evt) != PayloadVersion)) return;

        var branches = region.Branches.ToDictionary(branch => branch.BranchId, StringComparer.Ordinal);
        var messages = new SortedDictionary<string, List<CollaborationMessage>>(StringComparer.Ordinal);
        var arrivals = new HashSet<string>(StringComparer.Ordinal);
        Guid? regionInvocationId = null;
        var nextMessageSequence = 0L;
        var started = false;
        var released = false;
        var aborted = false;
        var pendingRelease = false;
        string? snapshotJson = null;
        string? snapshotHash = null;

        foreach (var evt in events)
        {
            using var payload = ParsePayload(evt);
            var data = payload.RootElement;
            if (!started && evt.Kind != ExecutionEventKinds.ParallelCollaborationRegionStarted)
                throw Invalid("Collaboration activity precedes its region-start event.");
            if (pendingRelease && evt.Kind != ExecutionEventKinds.ParallelCollaborationReleased)
                throw Invalid("The final branch arrival is not followed by a release snapshot.");
            if (aborted) throw Invalid("Collaboration activity follows the region abort.");

            switch (evt.Kind)
            {
                case ExecutionEventKinds.ParallelCollaborationRegionStarted:
                    ValidateStart(evt, data, snapshot.Header.RevisionId, region, ref started, ref regionInvocationId);
                    break;
                case ExecutionEventKinds.ParallelCollaborationMessageSent:
                    ValidateSend(evt, data, region, branches, regionInvocationId!.Value, messages,
                        arrivals, ref nextMessageSequence, released);
                    break;
                case ExecutionEventKinds.ParallelCollaborationArrival:
                    ValidateArrival(evt, data, region, branches, regionInvocationId!.Value, arrivals,
                        ref pendingRelease, released);
                    break;
                case ExecutionEventKinds.ParallelCollaborationReleased:
                    ValidateRelease(evt, data, region, regionInvocationId!.Value, messages, arrivals,
                        ref pendingRelease, ref released, ref snapshotJson, ref snapshotHash);
                    break;
                case ExecutionEventKinds.ParallelCollaborationMessagesRead:
                    ValidateRead(evt, data, region, branches, regionInvocationId!.Value, messages,
                        released, snapshotHash);
                    break;
                case ExecutionEventKinds.ParallelCollaborationAborted:
                    ValidateAbort(evt, data, region, regionInvocationId!.Value, released, snapshotHash,
                        ref aborted);
                    break;
                default:
                    throw Invalid($"Unknown collaboration event kind '{evt.Kind}'.");
            }
        }

        if (snapshot.IsTerminal && started)
        {
            switch (snapshot.Status)
            {
                case "completed":
                case "terminated":
                    if (!released || aborted || pendingRelease)
                        throw Invalid("A successful terminal run has incomplete or aborted collaboration evidence.");
                    break;
                case "failed":
                case "cancelled":
                    if (!aborted)
                        throw Invalid("A failed or cancelled collaboration run is missing its abort event.");
                    break;
                // Process loss can leave an honest partial prefix. It is readable as incomplete
                // evidence and is not upgraded to an assertion that the barrier released/aborted.
                case "interrupted":
                    break;
            }
        }
    }

    private static void ValidatePhased(RunSnapshot snapshot, ExecutionEvent[] events, ParallelCollaborationRegion region)
    {
        const int phasedPayloadVersion = 2;
        if (region.SecondRendezvousNodeId is null) throw Invalid("A schema-1.35 journal region has no second rendezvous.");
        if (events.Any(evt => ReadVersion(evt) != phasedPayloadVersion)) return;
        var branches = region.Branches.ToDictionary(branch => branch.BranchId, StringComparer.Ordinal);
        var messages = new Dictionary<string, SortedDictionary<string, List<CollaborationMessage>>>(StringComparer.Ordinal)
        {
            [ParallelCollaborationPhases.Phase1] = new(StringComparer.Ordinal),
            [ParallelCollaborationPhases.Phase2] = new(StringComparer.Ordinal)
        };
        var arrivals = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal)
        {
            [ParallelCollaborationPhases.Phase1] = new(StringComparer.Ordinal),
            [ParallelCollaborationPhases.Phase2] = new(StringComparer.Ordinal)
        };
        var released = new Dictionary<string, (string Json, string Hash, int Bytes)>(StringComparer.Ordinal);
        Guid? invocation = null;
        long sequence = 0;
        var started = false;
        var aborted = false;
        string? pendingRelease = null;
        foreach (var evt in events)
        {
            using var payload = ParsePayload(evt);
            var data = payload.RootElement;
            if (!started && evt.Kind != ExecutionEventKinds.ParallelCollaborationRegionStarted)
                throw Invalid("Collaboration activity precedes its region-start event.");
            if (pendingRelease is not null && (evt.Kind != ExecutionEventKinds.ParallelCollaborationReleased ||
                OptionalString(data, "phase") != pendingRelease))
                throw Invalid("The final branch arrival is not followed by its phase snapshot release.");
            if (aborted) throw Invalid("Collaboration activity follows the region abort.");

            switch (evt.Kind)
            {
                case ExecutionEventKinds.ParallelCollaborationRegionStarted:
                    if (started || evt.NodeId != region.RendezvousNodeId || evt.InvocationId is not { } regionInvocation || regionInvocation == Guid.Empty ||
                        RequiredGuid(data, "regionInvocationId") != regionInvocation || RequiredString(data, "revisionId") != snapshot.Header.RevisionId.ToString() ||
                        RequiredString(data, "regionNodeId") != region.RendezvousNodeId || RequiredString(data, "secondRendezvousNodeId") != region.SecondRendezvousNodeId ||
                        RequiredString(data, "forkNodeId") != region.ForkNodeId || RequiredString(data, "joinNodeId") != region.JoinNodeId ||
                        evt.ConnectionId != "collaboration:region") throw Invalid("The phased collaboration start event does not match the workflow region.");
                    var timeout = RequiredDouble(data, "timeoutSeconds");
                    var deadline = RequiredDateTimeOffset(data, "deadline");
                    if (!double.IsFinite(timeout) || timeout <= 0 || region.TimeoutSeconds is { } configured && timeout != configured ||
                        Math.Abs((deadline - evt.Timestamp).TotalSeconds - timeout) > 1)
                        throw Invalid("The phased collaboration start deadline is invalid.");
                    started = true;
                    invocation = regionInvocation;
                    break;
                case ExecutionEventKinds.ParallelCollaborationMessageSent:
                {
                    var phase = RequiredPhase(data);
                    var phaseIndex = phase == ParallelCollaborationPhases.Phase1 ? 1 : 2;
                    var phaseReleased = released.ContainsKey(phase);
                    var phaseArrivals = arrivals[phase];
                    var branchId = RequiredString(data, "branchId");
                    var channel = RequiredString(data, "channel");
                    var content = RequiredString(data, "content");
                    var messageSequence = RequiredInt64(data, "sequence");
                    if (phaseIndex == 2 && !released.ContainsKey(ParallelCollaborationPhases.Phase1) || phaseReleased ||
                        evt.NodeId is null || evt.InvocationId is null || evt.InvocationId == Guid.Empty ||
                        RequiredGuid(data, "regionInvocationId") != invocation || !branches.TryGetValue(branchId, out var branch) ||
                        !region.ChannelIds.Contains(channel, StringComparer.Ordinal) || phaseArrivals.Contains(branchId) ||
                        !HasGrant(branch, evt.NodeId, phase, ParallelCollaborationRoles.Publish, channel) || evt.ConnectionId != channel ||
                        string.IsNullOrWhiteSpace(content) || content.Length > ParallelCollaborationLimits.MaximumMessageCharacters ||
                        messageSequence != sequence + 1 || messageSequence > ParallelCollaborationLimits.MaximumMessagesPerRegion)
                        throw Invalid("A phased collaboration message violates its branch, phase, channel, ordering, or size grant.");
                    var board = messages[phase];
                    var channelMessages = board.GetValueOrDefault(channel);
                    var totalForChannel = messages.Values.Sum(value => value.GetValueOrDefault(channel)?.Count ?? 0);
                    if (totalForChannel >= ParallelCollaborationLimits.MaximumMessagesPerChannel)
                        throw Invalid("A collaboration channel exceeds its cumulative message count limit.");
                    (channelMessages ??= board[channel] = []).Add(new(messageSequence, branchId, channel, content));
                    sequence = messageSequence;
                    break;
                }
                case ExecutionEventKinds.ParallelCollaborationArrival:
                {
                    var phase = RequiredPhase(data);
                    var expectedNode = phase == ParallelCollaborationPhases.Phase1 ? region.RendezvousNodeId : region.SecondRendezvousNodeId;
                    var branchId = RequiredString(data, "branchId");
                    var phaseArrivals = arrivals[phase];
                    if (phase == ParallelCollaborationPhases.Phase2 && !released.ContainsKey(ParallelCollaborationPhases.Phase1) ||
                        evt.NodeId != expectedNode || evt.InvocationId is not { } arrivalInvocation || arrivalInvocation == Guid.Empty ||
                        RequiredGuid(data, "regionInvocationId") != invocation || !branches.ContainsKey(branchId) || !phaseArrivals.Add(branchId) ||
                        RequiredInt32(data, "participantCount") != branches.Count || RequiredInt32(data, "arrivedCount") != phaseArrivals.Count ||
                        evt.ConnectionId != $"collaboration:arrival:{branchId}" || RequiredBoolean(data, "released") != (phaseArrivals.Count == branches.Count))
                        throw Invalid("A phased collaboration arrival is duplicated or does not match a configured branch.");
                    if (phaseArrivals.Count == branches.Count) pendingRelease = phase;
                    break;
                }
                case ExecutionEventKinds.ParallelCollaborationReleased:
                {
                    var phase = RequiredPhase(data);
                    var expectedNode = phase == ParallelCollaborationPhases.Phase1 ? region.RendezvousNodeId : region.SecondRendezvousNodeId;
                    if (released.ContainsKey(phase) || pendingRelease != phase || arrivals[phase].Count != branches.Count ||
                        evt.NodeId != expectedNode || evt.InvocationId is not { } releaseInvocation || releaseInvocation == Guid.Empty ||
                        RequiredGuid(data, "regionInvocationId") != invocation || RequiredString(data, "regionNodeId") != expectedNode ||
                        RequiredString(data, "forkNodeId") != region.ForkNodeId || RequiredString(data, "joinNodeId") != region.JoinNodeId ||
                        evt.ConnectionId != "collaboration:snapshot") throw Invalid("A phased snapshot release does not follow every branch arrival.");
                    var json = RequiredString(data, "canonicalJson");
                    var bytes = Encoding.UTF8.GetByteCount(json);
                    var accumulatedBytes = released.Values.Sum(item => item.Bytes) + bytes;
                    if (bytes < 1 || RequiredInt32(data, "utf8Bytes") != bytes || accumulatedBytes > ParallelCollaborationLimits.MaximumSnapshotBytes)
                        throw Invalid("The cumulative phased snapshot byte limit or byte count is invalid.");
                    var expected = new SortedDictionary<string, CollaborationMessage[]>(StringComparer.Ordinal);
                    foreach (var entry in messages[phase]) expected.Add(entry.Key, entry.Value.ToArray());
                    if (json != JsonSerializer.Serialize(expected, JsonOptions)) throw Invalid("The released phase snapshot differs from its append evidence.");
                    var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(json)));
                    if (RequiredString(data, "sha256") != hash) throw Invalid("The phased snapshot digest is invalid.");
                    released.Add(phase, (json, hash, bytes));
                    pendingRelease = null;
                    break;
                }
                case ExecutionEventKinds.ParallelCollaborationMessagesRead:
                {
                    var phase = RequiredPhase(data);
                    var branchId = RequiredString(data, "branchId");
                    var channel = RequiredString(data, "channel");
                    var expectedBarrierReached = released.ContainsKey(phase);
                    if (phase == ParallelCollaborationPhases.Phase2 && !released.ContainsKey(ParallelCollaborationPhases.Phase1) || !expectedBarrierReached ||
                        evt.NodeId is null || evt.InvocationId is null || evt.InvocationId == Guid.Empty ||
                        RequiredGuid(data, "regionInvocationId") != invocation || !branches.TryGetValue(branchId, out var branch) ||
                        !region.ChannelIds.Contains(channel, StringComparer.Ordinal) ||
                        !HasGrant(branch, evt.NodeId, phase, ParallelCollaborationRoles.Read, channel) || evt.ConnectionId != channel ||
                        RequiredString(data, "snapshotSha256") != released[phase].Hash)
                        throw Invalid("A phased collaboration read is unauthorized or references the wrong snapshot.");
                    var expectedMessages = messages[phase].GetValueOrDefault(channel) ?? [];
                    if (RequiredString(data, "viewJson") != JsonSerializer.Serialize(expectedMessages, JsonOptions))
                        throw Invalid("A phased collaboration read differs from its referenced snapshot.");
                    break;
                }
                case ExecutionEventKinds.ParallelCollaborationAborted:
                {
                    var expectedPhase = released.ContainsKey(ParallelCollaborationPhases.Phase1)
                        ? ParallelCollaborationPhases.Phase2 : ParallelCollaborationPhases.Phase1;
                    var expectedNode = expectedPhase == ParallelCollaborationPhases.Phase2 ? region.SecondRendezvousNodeId : region.RendezvousNodeId;
                    var releasedPhases = data.TryGetProperty("releasedPhases", out var phases) && phases.ValueKind == JsonValueKind.Array
                        ? phases.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String).Select(item => item.GetString()).ToArray()
                        : Array.Empty<string?>();
                    var expectedPhases = new[] { released.ContainsKey(ParallelCollaborationPhases.Phase1) ? ParallelCollaborationPhases.Phase1 : null,
                        released.ContainsKey(ParallelCollaborationPhases.Phase2) ? ParallelCollaborationPhases.Phase2 : null }.Where(item => item is not null).ToArray();
                    if (RequiredPhase(data) != expectedPhase || evt.NodeId != expectedNode || evt.InvocationId != invocation || RequiredGuid(data, "regionInvocationId") != invocation ||
                        RequiredString(data, "regionNodeId") != expectedNode || RequiredString(data, "forkNodeId") != region.ForkNodeId ||
                        RequiredString(data, "joinNodeId") != region.JoinNodeId || evt.ConnectionId != "collaboration:abort" ||
                        RequiredBoolean(data, "released") != (released.Count > 0) ||
                        OptionalString(data, "snapshotSha256") != (released.ContainsKey(ParallelCollaborationPhases.Phase2)
                            ? released[ParallelCollaborationPhases.Phase2].Hash : released.GetValueOrDefault(ParallelCollaborationPhases.Phase1).Hash) ||
                        !releasedPhases.SequenceEqual(expectedPhases, StringComparer.Ordinal) ||
                        string.IsNullOrWhiteSpace(RequiredString(data, "reason")) || RequiredString(data, "reason").Length > 256)
                        throw Invalid("The phased collaboration abort event contradicts recorded snapshots or region state.");
                    aborted = true;
                    break;
                }
                default:
                    throw Invalid($"Unknown phased collaboration event kind '{evt.Kind}'.");
            }
        }
        if (snapshot.IsTerminal && started)
        {
            if ((snapshot.Status is "completed" or "terminated") && (!released.ContainsKey(ParallelCollaborationPhases.Phase1) ||
                !released.ContainsKey(ParallelCollaborationPhases.Phase2) || aborted || pendingRelease is not null))
                throw Invalid("A successful terminal run has incomplete phased collaboration evidence.");
            if ((snapshot.Status is "failed" or "cancelled") && !aborted)
                throw Invalid("A failed or cancelled phased collaboration run is missing its abort event.");
        }
    }

    private static void ValidateStart(ExecutionEvent evt, JsonElement data, Guid revisionId, ParallelCollaborationRegion region,
        ref bool started, ref Guid? regionInvocationId)
    {
        if (started || evt.NodeId != region.RendezvousNodeId || evt.InvocationId is not { } invocationId || invocationId == Guid.Empty ||
            RequiredGuid(data, "regionInvocationId") != invocationId || RequiredString(data, "revisionId") != revisionId.ToString() ||
            RequiredString(data, "regionNodeId") != region.RendezvousNodeId ||
            RequiredString(data, "forkNodeId") != region.ForkNodeId || RequiredString(data, "joinNodeId") != region.JoinNodeId ||
            evt.ConnectionId != "collaboration:region")
            throw Invalid("The collaboration region-start event does not match the workflow region.");

        var timeoutSeconds = RequiredDouble(data, "timeoutSeconds");
        if (!double.IsFinite(timeoutSeconds) || timeoutSeconds <= 0 ||
            (region.TimeoutSeconds is { } configuredTimeout && timeoutSeconds != configuredTimeout))
            throw Invalid("The collaboration region-start event timeout does not match the workflow definition.");
        var deadline = RequiredDateTimeOffset(data, "deadline");
        if (Math.Abs((deadline - evt.Timestamp).TotalSeconds - timeoutSeconds) > 1)
            throw Invalid("The collaboration region-start deadline does not match its timeout.");

        started = true;
        regionInvocationId = invocationId;
    }

    private static void ValidateSend(ExecutionEvent evt, JsonElement data, ParallelCollaborationRegion region,
        IReadOnlyDictionary<string, ParallelCollaborationBranch> branches, Guid regionInvocationId,
        SortedDictionary<string, List<CollaborationMessage>> messages, HashSet<string> arrivals,
        ref long nextMessageSequence, bool released)
    {
        var branchId = RequiredString(data, "branchId");
        var channel = RequiredString(data, "channel");
        var content = RequiredString(data, "content");
        var sequence = RequiredInt64(data, "sequence");
        if (released || evt.NodeId is null || evt.InvocationId is null || evt.InvocationId == Guid.Empty ||
            RequiredGuid(data, "regionInvocationId") != regionInvocationId || !branches.TryGetValue(branchId, out var branch) ||
            !region.ChannelIds.Contains(channel, StringComparer.Ordinal) || arrivals.Contains(branchId) ||
            !HasGrant(branch, evt.NodeId, ParallelCollaborationPhases.Publish, channel) ||
            evt.ConnectionId != channel || string.IsNullOrWhiteSpace(content) || content.Length > ParallelCollaborationLimits.MaximumMessageCharacters ||
            sequence != nextMessageSequence + 1 || sequence > ParallelCollaborationLimits.MaximumMessagesPerRegion)
            throw Invalid("A collaboration message violates its branch, phase, channel, ordering, or size grant.");

        var channelMessages = messages.GetValueOrDefault(channel);
        if (channelMessages is { Count: >= ParallelCollaborationLimits.MaximumMessagesPerChannel })
            throw Invalid("A collaboration channel exceeds its message count limit.");
        (channelMessages ??= messages[channel] = []).Add(new(sequence, branchId, channel, content));
        nextMessageSequence = sequence;
    }

    private static void ValidateArrival(ExecutionEvent evt, JsonElement data, ParallelCollaborationRegion region,
        IReadOnlyDictionary<string, ParallelCollaborationBranch> branches, Guid regionInvocationId,
        HashSet<string> arrivals, ref bool pendingRelease, bool released)
    {
        var branchId = RequiredString(data, "branchId");
        if (released || evt.NodeId != region.RendezvousNodeId || evt.InvocationId is not { } invocationId || invocationId == Guid.Empty ||
            RequiredGuid(data, "regionInvocationId") != regionInvocationId || !branches.ContainsKey(branchId) ||
            !arrivals.Add(branchId) || RequiredInt32(data, "participantCount") != branches.Count ||
            RequiredInt32(data, "arrivedCount") != arrivals.Count ||
            evt.ConnectionId != $"collaboration:arrival:{branchId}")
            throw Invalid("A collaboration arrival is duplicated or does not match a configured branch.");

        var isFinalArrival = arrivals.Count == branches.Count;
        if (RequiredBoolean(data, "released") != isFinalArrival)
            throw Invalid("A collaboration arrival reports an inconsistent release state.");
        pendingRelease = isFinalArrival;
    }

    private static void ValidateRelease(ExecutionEvent evt, JsonElement data, ParallelCollaborationRegion region,
        Guid regionInvocationId, SortedDictionary<string, List<CollaborationMessage>> messages, HashSet<string> arrivals,
        ref bool pendingRelease, ref bool released, ref string? snapshotJson, ref string? snapshotHash)
    {
        if (released || !pendingRelease || arrivals.Count != region.Branches.Length ||
            evt.NodeId != region.RendezvousNodeId || evt.InvocationId is not { } invocationId || invocationId == Guid.Empty ||
            RequiredGuid(data, "regionInvocationId") != regionInvocationId ||
            RequiredString(data, "regionNodeId") != region.RendezvousNodeId ||
            RequiredString(data, "forkNodeId") != region.ForkNodeId ||
            RequiredString(data, "joinNodeId") != region.JoinNodeId || evt.ConnectionId != "collaboration:snapshot")
            throw Invalid("The collaboration release event does not follow all configured branch arrivals.");

        snapshotJson = RequiredString(data, "canonicalJson");
        var snapshotBytes = Encoding.UTF8.GetByteCount(snapshotJson);
        if (snapshotBytes is < 1 or > ParallelCollaborationLimits.MaximumSnapshotBytes ||
            RequiredInt32(data, "utf8Bytes") != snapshotBytes)
            throw Invalid("The collaboration release byte count does not match its canonical UTF-8 snapshot.");

        var expected = new SortedDictionary<string, CollaborationMessage[]>(StringComparer.Ordinal);
        foreach (var entry in messages) expected.Add(entry.Key, entry.Value.ToArray());
        var expectedJson = JsonSerializer.Serialize(expected, JsonOptions);
        if (!string.Equals(snapshotJson, expectedJson, StringComparison.Ordinal))
            throw Invalid("The collaboration release snapshot does not match the preceding append events.");

        snapshotHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(snapshotJson)));
        if (!string.Equals(RequiredString(data, "sha256"), snapshotHash, StringComparison.Ordinal))
            throw Invalid("The collaboration release digest does not match its canonical snapshot.");

        released = true;
        pendingRelease = false;
    }

    private static void ValidateRead(ExecutionEvent evt, JsonElement data, ParallelCollaborationRegion region,
        IReadOnlyDictionary<string, ParallelCollaborationBranch> branches, Guid regionInvocationId,
        SortedDictionary<string, List<CollaborationMessage>> messages, bool released, string? snapshotHash)
    {
        var branchId = RequiredString(data, "branchId");
        var channel = RequiredString(data, "channel");
        if (!released || evt.NodeId is null || evt.InvocationId is null || evt.InvocationId == Guid.Empty ||
            RequiredGuid(data, "regionInvocationId") != regionInvocationId || !branches.TryGetValue(branchId, out var branch) ||
            !region.ChannelIds.Contains(channel, StringComparer.Ordinal) ||
            !HasGrant(branch, evt.NodeId, ParallelCollaborationPhases.Read, channel) || evt.ConnectionId != channel ||
            RequiredString(data, "snapshotSha256") != snapshotHash)
            throw Invalid("A collaboration read is not authorized or does not reference the released snapshot.");

        var expectedMessages = messages.GetValueOrDefault(channel) ?? [];
        var expectedJson = JsonSerializer.Serialize(expectedMessages, JsonOptions);
        if (!string.Equals(RequiredString(data, "viewJson"), expectedJson, StringComparison.Ordinal))
            throw Invalid("A collaboration read view does not match the messages in its referenced snapshot.");
    }

    private static void ValidateAbort(ExecutionEvent evt, JsonElement data, ParallelCollaborationRegion region,
        Guid regionInvocationId, bool released, string? snapshotHash, ref bool aborted)
    {
        var expectedHash = released ? snapshotHash : null;
        if (aborted || evt.NodeId != region.RendezvousNodeId || evt.InvocationId != regionInvocationId ||
            RequiredGuid(data, "regionInvocationId") != regionInvocationId ||
            RequiredString(data, "regionNodeId") != region.RendezvousNodeId ||
            RequiredString(data, "forkNodeId") != region.ForkNodeId ||
            RequiredString(data, "joinNodeId") != region.JoinNodeId || evt.ConnectionId != "collaboration:abort" ||
            RequiredBoolean(data, "released") != released ||
            (OptionalString(data, "snapshotSha256") is { } providedHash ? providedHash != expectedHash : expectedHash is not null) ||
            string.IsNullOrWhiteSpace(RequiredString(data, "reason")) || RequiredString(data, "reason").Length > 256)
            throw Invalid("The collaboration abort event contradicts the recorded region state.");
        aborted = true;
    }

    private static bool HasGrant(ParallelCollaborationBranch branch, string nodeId, string phase, string channel) =>
        branch.Agents.Any(grant => grant.AgentNodeId == nodeId && grant.Phase == phase &&
            grant.Channels.Contains(channel, StringComparer.Ordinal));

    private static bool HasGrant(ParallelCollaborationBranch branch, string nodeId, string phase, string role, string channel) =>
        branch.Agents.Any(grant => grant.AgentNodeId == nodeId && grant.Phase == phase && grant.Role == role &&
            grant.Channels.Contains(channel, StringComparer.Ordinal));

    private static string RequiredPhase(JsonElement data)
    {
        var phase = RequiredString(data, "phase");
        if (phase is not (ParallelCollaborationPhases.Phase1 or ParallelCollaborationPhases.Phase2))
            throw Invalid("A phased collaboration event has an unknown phase.");
        return phase;
    }

    private static int ReadVersion(ExecutionEvent evt)
    {
        using var payload = ParsePayload(evt);
        return RequiredInt32(payload.RootElement, "version");
    }

    private static JsonDocument ParsePayload(ExecutionEvent evt)
    {
        if (string.IsNullOrWhiteSpace(evt.Data)) throw Invalid("A collaboration event has no structured data payload.");
        try { return JsonDocument.Parse(evt.Data); }
        catch (JsonException error) { throw new InvalidDataException("A collaboration event has invalid JSON payload data.", error); }
    }

    private static bool IsCollaborationEvent(string kind) => kind is
        ExecutionEventKinds.ParallelCollaborationRegionStarted or ExecutionEventKinds.ParallelCollaborationMessageSent or
        ExecutionEventKinds.ParallelCollaborationMessagesRead or ExecutionEventKinds.ParallelCollaborationArrival or
        ExecutionEventKinds.ParallelCollaborationReleased or ExecutionEventKinds.ParallelCollaborationAborted;

    private static JsonElement Property(JsonElement value, string name)
    {
        if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(name, out var property))
            throw Invalid($"A collaboration event is missing '{name}'.");
        return property;
    }

    private static string RequiredString(JsonElement value, string name)
    {
        var property = Property(value, name);
        if (property.ValueKind != JsonValueKind.String || property.GetString() is not { } result)
            throw Invalid($"A collaboration event field '{name}' is not a string.");
        return result;
    }

    private static string? OptionalString(JsonElement value, string name)
    {
        var property = Property(value, name);
        if (property.ValueKind == JsonValueKind.Null) return null;
        if (property.ValueKind != JsonValueKind.String) throw Invalid($"A collaboration event field '{name}' is not a string or null.");
        return property.GetString();
    }

    private static Guid RequiredGuid(JsonElement value, string name)
    {
        var property = Property(value, name);
        if (property.ValueKind != JsonValueKind.String || !property.TryGetGuid(out var result) || result == Guid.Empty)
            throw Invalid($"A collaboration event field '{name}' is not a non-empty GUID.");
        return result;
    }

    private static double RequiredDouble(JsonElement value, string name)
    {
        var property = Property(value, name);
        if (property.ValueKind != JsonValueKind.Number || !property.TryGetDouble(out var result))
            throw Invalid($"A collaboration event field '{name}' is not a number.");
        return result;
    }

    private static int RequiredInt32(JsonElement value, string name)
    {
        var property = Property(value, name);
        if (property.ValueKind != JsonValueKind.Number || !property.TryGetInt32(out var result))
            throw Invalid($"A collaboration event field '{name}' is not an integer.");
        return result;
    }

    private static long RequiredInt64(JsonElement value, string name)
    {
        var property = Property(value, name);
        if (property.ValueKind != JsonValueKind.Number || !property.TryGetInt64(out var result))
            throw Invalid($"A collaboration event field '{name}' is not an integer.");
        return result;
    }

    private static bool RequiredBoolean(JsonElement value, string name)
    {
        var property = Property(value, name);
        return property.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw Invalid($"A collaboration event field '{name}' is not a Boolean.")
        };
    }

    private static DateTimeOffset RequiredDateTimeOffset(JsonElement value, string name)
    {
        var property = Property(value, name);
        if (property.ValueKind != JsonValueKind.String || !property.TryGetDateTimeOffset(out var result))
            throw Invalid($"A collaboration event field '{name}' is not a date-time.");
        return result;
    }

    private static InvalidDataException Invalid(string message) => new(message);

    private sealed record CollaborationMessage(long Sequence, string BranchId, string Channel, string Content);
}
