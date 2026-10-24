using System.Collections.Immutable;

namespace VisualAgentWorkflow.Core;

public sealed record ValidationIssue(string Code, string Path, string Message);

public static class WorkflowValidator
{
    public static ImmutableArray<ValidationIssue> Validate(WorkflowDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var issues = ImmutableArray.CreateBuilder<ValidationIssue>();
        void Error(string code, string path, string message) => issues.Add(new(code, path, message));

        if (definition.SchemaVersion is not ("1.0" or "1.1" or "1.2"))
            Error("schema.unsupported", "schemaVersion", "Supported schema versions are 1.0, 1.1 and 1.2.");
        if (string.IsNullOrWhiteSpace(definition.WorkflowId))
            Error("workflow.id", "workflowId", "A workflow ID is required.");
        if (definition.Nodes is null || definition.Connections is null)
        {
            Error("graph.missing", "nodes", "Nodes and connections must be present.");
            return issues.ToImmutable();
        }
        if (definition.Nodes.Count is < 2 or > 256 || definition.Connections.Count > 255)
            Error("graph.limit", "nodes", "A pipeline requires 2–256 nodes and at most 255 connections.");

        var incoming = definition.Nodes.Keys.ToDictionary(id => id, _ => 0);
        var outgoing = definition.Nodes.Keys.ToDictionary(id => id, _ => new List<string>());
        foreach (var (id, node) in definition.Nodes)
        {
            if (string.IsNullOrWhiteSpace(id)) Error("node.id", "nodes", "Node IDs must be nonempty.");
            if (node is null || !NodeTypes.IsSupported(node.Type) || node.TypeVersion != "1.0.0")
                Error("node.unsupported", $"nodes.{id}", "Unknown node type or implementation version.");
            if (node is null) continue;
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
                if (definition.SchemaVersion != "1.2") Error("node.schema", $"nodes.{id}", "Agent nodes require schema 1.2.");
                if (node.Agent is not { } agent || string.IsNullOrWhiteSpace(agent.ProviderId) || agent.ProviderId.Length > 128 ||
                    string.IsNullOrWhiteSpace(agent.ProviderVersion) || agent.ProviderVersion.Length > 64 ||
                    agent.Instructions is null || agent.Instructions.Length > 8192 || agent.Tools.IsDefault || agent.Tools.Length > 8 ||
                    agent.Tools.Any(t => t is null || !ValidBinding(t)) || agent.Tools.Select(t => t.BindingId).Distinct(StringComparer.Ordinal).Count() != agent.Tools.Length)
                    Error("agent.configuration", $"nodes.{id}.agent", "An agent requires a provider/version, bounded instructions, and up to eight unique tool requirements.");
            }
            else if (node.Agent is not null)
                Error("agent.unexpected", $"nodes.{id}.agent", "Only agent nodes may declare agent configuration.");
        }

        if (string.IsNullOrWhiteSpace(definition.EntryNodeId) ||
            !definition.Nodes.TryGetValue(definition.EntryNodeId, out var entry) || entry?.Type != NodeTypes.Input)
            Error("entry.invalid", "entryNodeId", "The entry must reference an input node.");

        var pairs = new HashSet<(string, string)>();
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
            if (edge.Kind != "data" || edge.Source.PortId != "out" || edge.Target.PortId != "in")
                Error("connection.port", path, "String pipelines connect an out port to an in port using a data link.");
            if (!pairs.Add((source, target))) Error("connection.duplicate", path, "Duplicate connection.");
            incoming[target]++;
            outgoing[source].Add(target);
        }

        foreach (var (id, node) in definition.Nodes)
        {
            if (node is null) continue;
            var expectedIncoming = node.Type == NodeTypes.Input ? 0 : 1;
            var expectedOutgoing = node.Type == NodeTypes.Output ? 0 : 1;
            if (incoming[id] != expectedIncoming || outgoing[id].Count != expectedOutgoing)
                Error("pipeline.degree", $"nodes.{id}", "Only a single input-to-output pipeline is supported; branches and joins are not yet supported.");
        }
        if (definition.Nodes.Values.Count(n => n?.Type == NodeTypes.Input) != 1 ||
            definition.Nodes.Values.Count(n => n?.Type == NodeTypes.Output) != 1)
            Error("pipeline.terminals", "nodes", "Exactly one input and one output node are required.");

        // Walk from the entry without recursive traversal; detect cycles and disconnected components.
        var visited = new HashSet<string>();
        var current = definition.EntryNodeId;
        while (current is not null && outgoing.TryGetValue(current, out var next))
        {
            if (!visited.Add(current))
            {
                Error("graph.cycle", "connections", "Cycles are not supported in a string pipeline.");
                break;
            }
            current = next.Count == 1 ? next[0] : null;
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
        tool.ContractHash is { Length: 64 } && tool.ContractHash.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
}
