using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;

namespace VisualAgentWorkflow.Core;

public sealed record ValidationIssue(string Code, string Path, string Message);

public static class WorkflowValidator
{
    public static ImmutableArray<ValidationIssue> Validate(WorkflowDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var issues = ImmutableArray.CreateBuilder<ValidationIssue>();
        void Error(string code, string path, string message) => issues.Add(new(code, path, message));

        if (definition.SchemaVersion is not ("1.0" or "1.1" or "1.2" or "1.3" or "1.4" or "1.5" or "1.6" or "1.7"))
            Error("schema.unsupported", "schemaVersion", "Supported schema versions are 1.0 through 1.7.");
        if (string.IsNullOrWhiteSpace(definition.WorkflowId))
            Error("workflow.id", "workflowId", "A workflow ID is required.");
        if (definition.Nodes is null || definition.Connections is null)
        {
            Error("graph.missing", "nodes", "Nodes and connections must be present.");
            return issues.ToImmutable();
        }
        if (definition.Nodes.Count is < 2 or > 256 || definition.Connections.Count > (definition.SchemaVersion is "1.4" or "1.5" or "1.6" or "1.7" ? 510 : 255))
            Error("graph.limit", "nodes", "A graph requires 2–256 nodes and at most 255 connections (510 for schemas 1.4–1.7).");

        var parameters = definition.Parameters ?? ImmutableSortedDictionary<string, WorkflowParameterDefinition>.Empty;
        if (parameters.Count > 64)
            Error("parameters.limit", "parameters", "A workflow can define at most 64 parameters.");
        if (definition.Parameters is { Count: > 0 } && definition.SchemaVersion is not ("1.6" or "1.7"))
            Error("parameters.schema", "parameters", "Workflow parameters require schema 1.6 or later.");
        foreach (var (id, parameter) in parameters)
            if (!ValidParameterId(id) || parameter is null || string.IsNullOrWhiteSpace(parameter.DisplayName) ||
                parameter.DisplayName.Length > 128 || parameter.DefaultValue is null || parameter.DefaultValue.Length > 16_384)
                Error("parameter.definition", $"parameters.{id}", "Parameters require a safe ID, display name up to 128 characters, and a string default up to 16,384 characters.");
        if (parameters.Sum(parameter => (long)parameter.Key.Length + (parameter.Value?.DisplayName.Length ?? 0) + (parameter.Value?.DefaultValue.Length ?? 0)) > 128_000)
            Error("parameters.size", "parameters", "Combined parameter IDs, display names, and defaults must not exceed 128,000 characters.");
        var skills = definition.Skills ?? ImmutableSortedDictionary<string, WorkflowSkill>.Empty;
        if (skills.Count > 32 || skills.Values.Sum(skill => (long)(skill?.Instructions?.Length ?? 0)) > 65_536)
            Error("skills.limit", "skills", "A workflow can include at most 32 skills and 65,536 instruction characters.");
        if (skills.Count > 0 && definition.SchemaVersion != "1.7")
            Error("skills.schema", "skills", "Portable skills require schema 1.7.");
        foreach (var (key, skill) in skills)
            if (skill is null || key != skill.Id + "@" + skill.Version || !ValidParameterId(skill.Id) ||
                string.IsNullOrWhiteSpace(skill.Version) || skill.Version.Length > 64 ||
                string.IsNullOrWhiteSpace(skill.Name) || skill.Name.Length > 128 ||
                string.IsNullOrWhiteSpace(skill.Instructions) || skill.Instructions.Length > 8192 ||
                !string.Equals(skill.ContentHash, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(skill.Instructions))), StringComparison.Ordinal))
                Error("skill.definition", $"skills.{key}", "A skill needs a valid identity, bounded text and matching content hash.");

        var incoming = definition.Nodes.Keys.ToDictionary(id => id, _ => 0);
        var outgoing = definition.Nodes.Keys.ToDictionary(id => id, _ => new List<string>());
        foreach (var (id, node) in definition.Nodes)
        {
            if (string.IsNullOrWhiteSpace(id)) Error("node.id", "nodes", "Node IDs must be nonempty.");
            if (node is null || !NodeTypes.IsSupported(node.Type) || node.TypeVersion != "1.0.0")
                Error("node.unsupported", $"nodes.{id}", "Unknown node type or implementation version.");
            if (node is null) continue;
            if (node.Type is NodeTypes.Condition or NodeTypes.Merge && definition.SchemaVersion is not ("1.4" or "1.5" or "1.6" or "1.7"))
                Error("node.schema", $"nodes.{id}", "Conditional and merge nodes require schema 1.4.");
            if (node.Type == NodeTypes.WorkflowValue)
            {
                if (definition.SchemaVersion is not ("1.6" or "1.7")) Error("node.schema", $"nodes.{id}", "Workflow value nodes require schema 1.6 or later.");
                if (node.ParameterId is not { } parameterId || !parameters.ContainsKey(parameterId))
                    Error("parameter.reference", $"nodes.{id}.parameterId", "Select a parameter defined by this workflow.");
            }
            else if (node.ParameterId is not null)
                Error("parameter.unexpected", $"nodes.{id}.parameterId", "Only workflow value nodes may reference a parameter.");
            if (node.Type == NodeTypes.Approval)
            {
                if (definition.SchemaVersion is not ("1.5" or "1.6" or "1.7")) Error("node.schema", $"nodes.{id}", "Approval nodes require schema 1.5 or later.");
                if (node.Approval is not { } approval || string.IsNullOrWhiteSpace(approval.Prompt) || approval.Prompt.Length > 2000)
                    Error("approval.configuration", $"nodes.{id}.approval", "An approval prompt of 1–2000 characters is required.");
                var targets = definition.Connections.Values.Where(e => e?.Source?.NodeId == id).ToArray();
                if (targets.Length != 1 || targets[0]?.Target?.NodeId is not { } targetId ||
                    !definition.Nodes.TryGetValue(targetId, out var targetNode) || targetNode?.Type != NodeTypes.McpTool)
                    Error("approval.target", $"nodes.{id}", "An approval must immediately precede exactly one MCP tool node.");
            }
            else if (node.Approval is not null)
                Error("approval.unexpected", $"nodes.{id}.approval", "Only approval nodes may declare an approval prompt.");
            if (node.Type == NodeTypes.Condition)
            {
                if (node.Condition is not { } condition || condition.Operator is not ("equals" or "contains" or "starts-with" or "is-empty") ||
                    condition.Operand is null || condition.Operand.Length > 1024 || condition.Operator == "is-empty" && condition.Operand.Length != 0)
                    Error("condition.configuration", $"nodes.{id}.condition", "Use an approved ordinal string predicate and an operand up to 1024 characters; is-empty requires an empty operand.");
            }
            else if (node.Condition is not null)
                Error("condition.unexpected", $"nodes.{id}.condition", "Only conditional nodes may declare a predicate.");
            if (node.Origin is { } origin && (definition.SchemaVersion is not ("1.3" or "1.4" or "1.5" or "1.6" or "1.7") ||
                string.IsNullOrWhiteSpace(origin.WorkflowId) || origin.WorkflowId.Length > 256 ||
                string.IsNullOrWhiteSpace(origin.NodeId) || origin.NodeId.Length > 256 || origin.RevisionId == Guid.Empty ||
                origin.Version < 1 || !ValidHash(origin.ContentHash)))
                Error("node.origin", $"nodes.{id}.origin", "Reusable steps require schema 1.3 and a bounded published source identity.");
            if (node.Type == NodeTypes.McpTool)
            {
                if (definition.SchemaVersion == "1.0")
                    Error("node.schema", $"nodes.{id}", "MCP nodes require schema 1.1.");
                if (node.Tool is not { } tool || !ValidBinding(tool))
                    Error("tool.binding", $"nodes.{id}.tool", "A logical binding and SHA-256 contract hash are required.");
            }
            else if (node.Tool is not null)
                Error("tool.unexpected", $"nodes.{id}.tool", "This node type cannot declare an MCP tool.");
            if (node.Type == NodeTypes.Agent)
            {
                if (definition.SchemaVersion is not ("1.2" or "1.3" or "1.4" or "1.5" or "1.6" or "1.7")) Error("node.schema", $"nodes.{id}", "Agent nodes require schema 1.2 or later.");
                if (node.Agent is not { } agent || string.IsNullOrWhiteSpace(agent.ProviderId) || agent.ProviderId.Length > 128 ||
                    string.IsNullOrWhiteSpace(agent.ProviderVersion) || agent.ProviderVersion.Length > 64 ||
                    agent.Instructions is null || agent.Instructions.Length > 8192 || agent.Tools.IsDefault || agent.Tools.Length > 8 ||
                    agent.Tools.Any(t => t is null || !ValidBinding(t)) || agent.Tools.Select(t => t.BindingId).Distinct(StringComparer.Ordinal).Count() != agent.Tools.Length)
                    Error("agent.configuration", $"nodes.{id}.agent", "An agent requires a provider/version, bounded instructions, and up to eight unique tool requirements.");
                if (node.Agent is { } configured)
                {
                    if (configured.UserMessage is { } template)
                    {
                        if (definition.SchemaVersion != "1.7" || template.Parts.IsDefault || template.Parts.Length is < 1 or > 64 ||
                            template.Parts.Any(part => part is null || part.Value is null || part.Value.Length > 8192 ||
                                part.Kind is not ("literal" or "input" or "parameter") ||
                                part.Kind == "parameter" && !parameters.ContainsKey(part.Value)))
                            Error("agent.prompt", $"nodes.{id}.agent.userMessage", "Use up to 64 literal, input or declared-parameter prompt parts in schema 1.7.");
                    }
                    if (configured.Skills is { } references)
                    {
                        if (definition.SchemaVersion != "1.7" || references.IsDefault || references.Length > 8 ||
                            references.Distinct().Count() != references.Length || references.Any(reference => reference is null ||
                                !skills.TryGetValue(reference.Id + "@" + reference.Version, out var skill) || skill.ContentHash != reference.ContentHash))
                            Error("agent.skills", $"nodes.{id}.agent.skills", "Each assigned skill must match an embedded immutable revision.");
                    }
                }
            }
            else if (node.Agent is not null)
                Error("agent.unexpected", $"nodes.{id}.agent", "Only agent nodes may declare agent configuration.");
        }

        if (string.IsNullOrWhiteSpace(definition.EntryNodeId) ||
            !definition.Nodes.TryGetValue(definition.EntryNodeId, out var entry) || entry?.Type != NodeTypes.Input)
            Error("entry.invalid", "entryNodeId", "The entry must reference an input node.");

        var pairs = new HashSet<(string, string, string, string)>();
        foreach (var (id, edge) in definition.Connections)
        {
            var path = $"connections.{id}";
            if (string.IsNullOrWhiteSpace(id)) Error("connection.id", path, "Connection IDs must be nonempty.");
            if (edge?.Source?.NodeId is not { } source || edge.Target?.NodeId is not { } target ||
                !definition.Nodes.ContainsKey(source) || !definition.Nodes.ContainsKey(target))
            {
                Error("connection.reference", path, "Both endpoints must reference existing nodes.");
                continue;
            }
            if (edge.Kind != "data" || definition.Nodes[source] is not { } sourceNode || definition.Nodes[target] is not { } targetNode ||
                !WorkflowPorts.Outputs(sourceNode).Any(p => p.Id == edge.Source.PortId) || !WorkflowPorts.Inputs(targetNode).Any(p => p.Id == edge.Target.PortId))
                Error("connection.port", path, "Connect an available string output to a string input using a data link.");
            if (!pairs.Add((source, edge.Source.PortId, target, edge.Target.PortId))) Error("connection.duplicate", path, "Duplicate connection.");
            incoming[target]++;
            outgoing[source].Add(target);
        }

        foreach (var (id, node) in definition.Nodes)
        {
            if (node is null) continue;
            if (definition.SchemaVersion is "1.4" or "1.5" or "1.6" or "1.7")
            {
                var validIncoming = node.Type == NodeTypes.Input ? incoming[id] == 0 : node.Type == NodeTypes.Merge
                    ? incoming[id] is >= 2 and <= 16 : incoming[id] == 1;
                var expected = WorkflowPorts.Outputs(node);
                var edges = definition.Connections.Values.Where(e => e?.Source?.NodeId == id).ToArray();
                if (!validIncoming || edges.Length != expected.Length || expected.Any(p => edges.Count(e => e.Source.PortId == p.Id) != 1))
                    Error("graph.degree", $"nodes.{id}", "Each output must connect once; normal nodes have one input, exclusive merges have 2–16, and input has none.");
                continue;
            }
            var expectedIncoming = node.Type == NodeTypes.Input ? 0 : 1;
            var expectedOutgoing = node.Type == NodeTypes.Output ? 0 : 1;
            if (incoming[id] != expectedIncoming || outgoing[id].Count != expectedOutgoing)
                Error("pipeline.degree", $"nodes.{id}", "Only a single input-to-output pipeline is supported; branches and joins are not yet supported.");
        }
        if (definition.Nodes.Values.Count(n => n?.Type == NodeTypes.Input) != 1 ||
            definition.Nodes.Values.Count(n => n?.Type == NodeTypes.Output) != 1)
            Error("pipeline.terminals", "nodes", "Exactly one input and one output node are required.");

        if (WorkflowGraph.TopologicalOrder(definition).Length != definition.Nodes.Count)
            Error("graph.cycle", "connections", "Cycles are not supported.");
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Stack<string>();
        if (definition.EntryNodeId is { } entryId && outgoing.ContainsKey(entryId)) pending.Push(entryId);
        while (pending.TryPop(out var id))
        {
            if (!visited.Add(id)) continue;
            foreach (var next in outgoing[id]) pending.Push(next);
        }
        if (visited.Count != definition.Nodes.Count)
            Error("graph.unreachable", "nodes", "Every node must be reachable from the entry.");
        return issues.ToImmutable();
    }

    public static void ThrowIfInvalid(WorkflowDefinition definition)
    {
        var issues = Validate(definition);
        if (issues.Length > 0)
            throw new ArgumentException(string.Join("; ", issues.Select(i => $"{i.Code}: {i.Message}")), nameof(definition));
    }

    private static bool ValidBinding(ToolBindingRequest tool) =>
        !string.IsNullOrWhiteSpace(tool.BindingId) && tool.BindingId.Length <= 128 &&
        ValidHash(tool.ContractHash);

    private static bool ValidParameterId(string id) => id.Length is > 0 and <= 64 &&
        id.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-');

    private static bool ValidHash(string? hash) => hash is { Length: 64 } && hash.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
}
