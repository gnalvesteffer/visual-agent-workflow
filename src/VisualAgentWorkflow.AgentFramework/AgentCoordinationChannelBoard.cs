using System.Collections.Immutable;
using System.Text.Json;
using VisualAgentWorkflow.Core;

namespace VisualAgentWorkflow.AgentFramework;

/// <summary>Append-only, parent- or team-invocation-local messages with exact-profile membership checks.</summary>
internal sealed class AgentCoordinationChannelBoard(Guid coordinationId, string coordinationScope, Guid parentInvocationId,
    IEnumerable<AgentProfile> profiles)
{
    public const int MaximumMessagesPerChannel = 32;
    public const int MaximumMessagesPerScope = 64;
    public const int MaximumMessageCharacters = 4_000;

    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly ImmutableDictionary<(string Id, string Version), AgentProfile> members =
        profiles.ToImmutableDictionary(profile => (profile.Id, profile.Version));
    private readonly Dictionary<string, List<AgentChannelMessage>> messages = new(StringComparer.Ordinal);
    private long sequence;

    public ImmutableArray<string> ChannelsFor(AgentProfile profile) =>
        members.TryGetValue((profile.Id, profile.Version), out var member) ? member.Channels ?? [] : [];

    public async ValueTask<string> SendAsync(NodeInvocationContext invocation, AgentProfile profile, string channel,
        string message, CancellationToken cancellationToken)
    {
        var sender = RequireMember(profile, channel);
        if (string.IsNullOrWhiteSpace(message) || message.Length > MaximumMessageCharacters)
            throw new InvalidOperationException($"A channel message must contain 1–{MaximumMessageCharacters} characters.");

        await gate.WaitAsync(cancellationToken);
        try
        {
            var channelMessages = messages.GetValueOrDefault(channel);
            if (sequence >= MaximumMessagesPerScope || channelMessages is { Count: >= MaximumMessagesPerChannel })
                throw new InvalidOperationException("The coordination channel message limit was reached.");

            var recordedMessage = new AgentChannelMessage(sequence + 1, sender.Id, sender.Version, sender.Name, message);
            await invocation.RecordEventAsync(ExecutionEventKinds.AgentChannelMessageSent, JsonSerializer.Serialize(new
            {
                coordinationId,
                coordinationScope,
                parentInvocationId,
                channel,
                message = recordedMessage
            }));
            sequence = recordedMessage.Sequence;
            (channelMessages ??= messages[channel] = []).Add(recordedMessage);
            return JsonSerializer.Serialize(new { sent = true, recordedMessage.Sequence, channel });
        }
        finally { gate.Release(); }
    }

    public async ValueTask<string> ReadAsync(NodeInvocationContext invocation, AgentProfile profile, string channel,
        CancellationToken cancellationToken)
    {
        RequireMember(profile, channel);
        await gate.WaitAsync(cancellationToken);
        try
        {
            var available = messages.GetValueOrDefault(channel)?.ToImmutableArray() ?? [];
            var output = JsonSerializer.Serialize(available);
            await invocation.RecordEventAsync(ExecutionEventKinds.AgentChannelMessagesRead, JsonSerializer.Serialize(new
            {
                coordinationId,
                coordinationScope,
                parentInvocationId,
                channel,
                readerProfileId = profile.Id,
                readerProfileVersion = profile.Version,
                messages = available
            }));
            return output;
        }
        finally { gate.Release(); }
    }

    private AgentProfile RequireMember(AgentProfile profile, string channel)
    {
        if (!members.TryGetValue((profile.Id, profile.Version), out var member) || member.Channels?.Contains(channel, StringComparer.Ordinal) != true)
            throw new InvalidOperationException("The agent profile is not a member of this coordination channel.");
        return member;
    }
}

internal sealed record AgentChannelMessage(long Sequence, string SenderProfileId, string SenderProfileVersion, string SenderName, string Content);
internal sealed record AgentCoordinationChannelAccess(AgentCoordinationChannelBoard Board, AgentProfile Profile);
