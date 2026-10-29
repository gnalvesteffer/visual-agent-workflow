using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace VisualAgentWorkflow.Core;

/// <summary>One immutable reusable dependency identity captured with a run.</summary>
public sealed record WorkflowRunDependencyEvidence(string Kind, string Id, string Version, string ContentHash);

/// <summary>
/// Deterministic, bounded identity evidence for the reusable snapshots embedded in a run definition.
/// The run header already contains the exact executable definition; this manifest avoids resolving
/// mutable project files during playback without duplicating potentially sensitive source bytes.
/// </summary>
public sealed record WorkflowRunDependencyClosure(string FormatVersion,
    ImmutableArray<WorkflowRunDependencyEvidence> Dependencies, string ClosureHash)
{
    public const string CurrentFormatVersion = "1.0";
    public const int MaximumDependencies = 256;
    public const int MaximumSerializedBytes = 256 * 1024;

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 8
    };

    public static WorkflowRunDependencyClosure Create(WorkflowDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var dependencies = new Dictionary<(string Kind, string Id, string Version, string Hash), WorkflowRunDependencyEvidence>();
        var visitedWorkflows = new HashSet<string>(StringComparer.Ordinal);

        void Add(string kind, string id, string version, string? hash)
        {
            if (string.IsNullOrWhiteSpace(hash)) return;
            var item = new WorkflowRunDependencyEvidence(kind, id, version, hash);
            dependencies[(kind, id, version, hash)] = item;
            if (dependencies.Count > MaximumDependencies)
                throw new InvalidDataException($"Run dependency closure exceeds {MaximumDependencies} entries.");
        }

        void VisitGraphFunction(GraphFunctionDefinition function)
        {
            foreach (var node in function.Nodes.Values)
            {
                if (node.GraphFunction is { } nested)
                {
                    Add("graph-function", nested.Definition.FunctionId,
                        nested.Definition.Version.ToString(System.Globalization.CultureInfo.InvariantCulture), nested.ContentHash);
                    VisitGraphFunction(nested.Definition);
                }
            }
        }

        void VisitWorkflow(WorkflowDefinition current)
        {
            var identity = current.WorkflowId + ":" + WorkflowJson.ComputeHash(current);
            if (!visitedWorkflows.Add(identity)) return;

            foreach (var skill in current.Skills?.Values ?? [])
                Add("skill", skill.Id, skill.Version, skill.ContentHash);
            foreach (var profile in current.AgentProfiles?.Values ?? [])
            {
                Add("agent-profile", profile.Id, profile.Version, profile.ContentHash);
                foreach (var skill in profile.Skills ?? [])
                    Add("skill", skill.Id, skill.Version, skill.ContentHash);
            }
            foreach (var node in current.Nodes.Values)
            {
                if (node.GraphFunction is { } function)
                {
                    Add("graph-function", function.Definition.FunctionId,
                        function.Definition.Version.ToString(System.Globalization.CultureInfo.InvariantCulture), function.ContentHash);
                    VisitGraphFunction(function.Definition);
                }
                if (node.WorkflowCall is { } call)
                {
                    Add("workflow", call.WorkflowId, call.Version.ToString(System.Globalization.CultureInfo.InvariantCulture), call.ContentHash);
                    if (call.Definition is { } child) VisitWorkflow(child);
                }
                if (node.Agent is { } agent)
                {
                    foreach (var skill in agent.Skills ?? []) Add("skill", skill.Id, skill.Version, skill.ContentHash);
                    foreach (var profile in agent.Delegates ?? [])
                        Add("agent-profile", profile.Id, profile.Version, profile.ContentHash);
                    foreach (var agentFunction in agent.GraphFunctionTools ?? [])
                    {
                        Add("graph-function", agentFunction.Definition.FunctionId,
                            agentFunction.Definition.Version.ToString(System.Globalization.CultureInfo.InvariantCulture), agentFunction.ContentHash);
                        VisitGraphFunction(agentFunction.Definition);
                    }
                }
            }
        }

        VisitWorkflow(definition);
        var ordered = dependencies.Values.OrderBy(item => item.Kind, StringComparer.Ordinal)
            .ThenBy(item => item.Id, StringComparer.Ordinal).ThenBy(item => item.Version, StringComparer.Ordinal)
            .ThenBy(item => item.ContentHash, StringComparer.Ordinal).ToImmutableArray();
        var closureHash = ComputeHash(CurrentFormatVersion, ordered);
        var closure = new WorkflowRunDependencyClosure(CurrentFormatVersion, ordered, closureHash);
        Validate(closure);
        return closure;
    }

    public static void Validate(WorkflowRunDependencyClosure closure)
    {
        ArgumentNullException.ThrowIfNull(closure);
        if (closure.FormatVersion != CurrentFormatVersion || closure.Dependencies.IsDefault ||
            closure.Dependencies.Length > MaximumDependencies || string.IsNullOrWhiteSpace(closure.ClosureHash) ||
            closure.Dependencies.Any(item => item is null || string.IsNullOrWhiteSpace(item.Kind) ||
                string.IsNullOrWhiteSpace(item.Id) || string.IsNullOrWhiteSpace(item.Version) || !IsSha256(item.ContentHash)) ||
            closure.Dependencies.Select(item => (item.Kind, item.Id, item.Version, item.ContentHash)).Distinct().Count() != closure.Dependencies.Length ||
            !string.Equals(closure.ClosureHash, ComputeHash(closure.FormatVersion, closure.Dependencies), StringComparison.Ordinal) ||
            JsonSerializer.SerializeToUtf8Bytes(closure.Dependencies, Options).Length > MaximumSerializedBytes)
            throw new InvalidDataException("Run dependency closure is invalid or exceeds its bounds.");
    }

    private static string ComputeHash(string formatVersion, ImmutableArray<WorkflowRunDependencyEvidence> dependencies)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new ClosureHashInput(formatVersion, dependencies), Options);
        return Convert.ToHexStringLower(SHA256.HashData(bytes));
    }

    private sealed record ClosureHashInput(string FormatVersion, ImmutableArray<WorkflowRunDependencyEvidence> Dependencies);

    private static bool IsSha256(string value) => value is { Length: 64 } && value.All(character =>
        character is >= '0' and <= '9' or >= 'a' and <= 'f');
}
