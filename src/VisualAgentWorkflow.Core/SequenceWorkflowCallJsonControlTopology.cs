using System.Collections.Immutable;

namespace VisualAgentWorkflow.Core;

/// <summary>One bounded JSON-array predicate route with explicit typed data and execution control.</summary>
public sealed record SequenceWorkflowCallJsonControlPath(
    string OutputId,
    ImmutableArray<string> NodeIds,
    string CallNodeId,
    string ToArrayNodeId,
    string LengthNodeId,
    string EqualsNodeId,
    string ConstantNodeId,
    string BranchNodeId,
    string TerminateNodeId,
    string ChildWorkflowId,
    Guid ChildRevisionId,
    string ChildContentHash);

public sealed record SequenceWorkflowCallJsonControlTopologyAnalysis(
    ImmutableArray<SequenceWorkflowCallJsonControlPath> Paths,
    ImmutableArray<SequenceWorkflowCallTopologyIssue> Issues)
{
    public bool IsValid => Issues.IsDefaultOrEmpty;
}

/// <summary>
/// Schema 1.57 admits one fixed data-to-control route. The existing schema-1.56
/// terminal array paths remain valid under the promoted schema without adding
/// predicate nodes.
/// </summary>
public static class SequenceWorkflowCallJsonControlTopology
{
    public static SequenceWorkflowCallJsonControlTopologyAnalysis Analyze(WorkflowDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var paths = ImmutableArray.CreateBuilder<SequenceWorkflowCallJsonControlPath>();
        var issues = ImmutableArray.CreateBuilder<SequenceWorkflowCallTopologyIssue>();
        void Issue(string code, string path, string message) => issues.Add(new(code, path, message));

        if (definition.SchemaVersion != WorkflowSchemaVersions.SequenceWorkflowCallJsonControlGraph)
        {
            Issue("sequence.call.json.control.schema", "schemaVersion", "JSON control topology requires schema 1.57.");
            return new(paths.ToImmutable(), issues.ToImmutable());
        }

        var controlNodes = definition.Nodes.Where(entry => entry.Value?.Type is NodeTypes.IntegerEquals or NodeTypes.IntegerConstant or NodeTypes.BooleanBranch or NodeTypes.Terminate).ToArray();
        if (controlNodes.Length == 0)
        {
            var legacy = SequenceWorkflowCallJsonArrayTopology.Analyze(definition with
            { SchemaVersion = WorkflowSchemaVersions.SequenceWorkflowCallJsonArrayGraph });
            foreach (var issue in legacy.Issues) issues.Add(issue);
            return new(paths.ToImmutable(), issues.ToImmutable());
        }

        var toArrays = definition.Nodes.Where(entry => entry.Value?.Type == NodeTypes.JsonToArray).ToArray();
        var lengths = definition.Nodes.Where(entry => entry.Value?.Type == NodeTypes.ArrayLength).ToArray();
        var equals = definition.Nodes.Where(entry => entry.Value?.Type == NodeTypes.IntegerEquals).ToArray();
        var constants = definition.Nodes.Where(entry => entry.Value?.Type == NodeTypes.IntegerConstant).ToArray();
        var branches = definition.Nodes.Where(entry => entry.Value?.Type == NodeTypes.BooleanBranch).ToArray();
        var terminates = definition.Nodes.Where(entry => entry.Value?.Type == NodeTypes.Terminate).ToArray();
        if (toArrays.Length != 1 || lengths.Length != 1 || equals.Length != 1 || constants.Length != 1 || branches.Length != 1 || terminates.Length != 1)
        {
            Issue("sequence.call.json.control.cardinality", "nodes", "The bounded JSON control topology requires exactly one array conversion, length, integer comparison, integer constant, Boolean Branch, and failure Terminate.");
            return new(paths.ToImmutable(), issues.ToImmutable());
        }

        var sequences = definition.Nodes.Where(entry => entry.Value?.Type == NodeTypes.Sequence).ToArray();
        var outputs = definition.Nodes.Where(entry => entry.Value?.Type == NodeTypes.Output).ToArray();
        var calls = definition.Nodes.Where(entry => entry.Value?.Type == NodeTypes.WorkflowCall).ToArray();
        if (sequences.Length != 1 || outputs.Length != 1 || calls.Length != 1)
            Issue("sequence.call.json.control.graph", "nodes", "The bounded JSON control topology requires one Sequence, one JSON WorkflowCall, and one Output.");
        if (sequences.Length != 1 || outputs.Length != 1 || calls.Length != 1 || definition.ExecutionConnections is not { } execution)
            return new(paths.ToImmutable(), issues.ToImmutable());

        var sequence = sequences[0];
        var output = outputs[0];
        var outputId = output.Key;
        var call = calls[0];
        var toArray = toArrays[0].Key;
        var length = lengths[0].Key;
        var compare = equals[0].Key;
        var constant = constants[0].Key;
        var branch = branches[0].Key;
        var terminate = terminates[0].Key;
        if (terminates[0].Value!.Termination?.Outcome != WorkflowTerminationOutcomes.Failure)
            Issue("sequence.call.json.control.termination", $"nodes.{terminate}", "The bounded JSON control topology uses failure termination for the false branch.");

        var edges = execution.Values.Where(edge => edge is not null).ToArray();
        var outgoing = edges.GroupBy(edge => edge.Source.NodeId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        var descriptor = sequence.Value.Sequence?.Outputs.FirstOrDefault();
        if (descriptor is null)
            Issue("sequence.call.json.control.sequence", $"nodes.{sequence.Key}.sequence", "The bounded JSON control topology requires one Sequence output.");
        if (descriptor is null)
            return new(paths.ToImmutable(), issues.ToImmutable());

        var roots = edges.Where(edge => edge.Source.NodeId == sequence.Key && edge.Source.PortId == SequenceOutputLimits.PinId(descriptor.Id)).ToArray();
        if (roots.Length != 1)
            Issue("sequence.call.json.control.execution", "executionConnections", "The Sequence output must have exactly one execution root.");
        var current = roots.Length == 1 ? roots[0].Target.NodeId : string.Empty;
        var nodeIds = ImmutableArray.CreateBuilder<string>();
        var visited = new HashSet<string>(StringComparer.Ordinal);
        string? callId = null;
        while (!string.IsNullOrWhiteSpace(current) && current != branch && visited.Add(current))
        {
            if (!definition.Nodes.TryGetValue(current, out var node) || node is null)
            {
                Issue("sequence.call.json.control.reference", $"nodes.{current}", "The execution path reaches a missing node.");
                break;
            }
            if (node.Type is not (NodeTypes.McpTool or NodeTypes.WorkflowCall))
                Issue("sequence.call.json.control.path", $"nodes.{current}", "Only direct MCP nodes may precede the pinned JSON WorkflowCall.");
            if (node.Type == NodeTypes.WorkflowCall)
            {
                callId = current;
                ValidateJsonCall(node, current, Issue);
            }
            if (callId is not null && node.Type == NodeTypes.McpTool)
                Issue("sequence.call.json.control.order", $"nodes.{current}", "Direct MCP nodes must precede the WorkflowCall.");
            nodeIds.Add(current);
            var successors = outgoing.GetValueOrDefault(current) ?? [];
            if (successors.Length != 1)
            {
                Issue("sequence.call.json.control.linear", $"nodes.{current}", "The pre-branch execution path must be linear.");
                break;
            }
            var edge = successors[0];
            if (edge.Target.PortId != "execute" || (node.Type == NodeTypes.McpTool && edge.Source.PortId != "then") ||
                (node.Type == NodeTypes.WorkflowCall && edge.Source.PortId != "out"))
                Issue("sequence.call.json.control.port", $"nodes.{current}", "MCP paths leave through then and WorkflowCall paths leave through out into execute.");
            current = edge.Target.NodeId;
        }
        if (callId is null)
            Issue("sequence.call.json.control.call", "nodes", "The bounded JSON control topology requires one WorkflowCall before the Boolean Branch.");
        if (current != branch)
            Issue("sequence.call.json.control.branch", $"nodes.{branch}", "The WorkflowCall execution path must reach the Boolean Branch.");

        RequireExecutionEdge(edges, branch, "true", outputId, "execute", Issue, "Boolean Branch.true must execute Output.");
        RequireExecutionEdge(edges, branch, "false", terminate, "execute", Issue, "Boolean Branch.false must execute failure Terminate.");
        if ((outgoing.GetValueOrDefault(branch) ?? []).Length != 2)
            Issue("sequence.call.json.control.branch", $"nodes.{branch}", "The Boolean Branch must have exactly true and false execution successors.");

        var data = definition.Connections.Values.Where(edge => edge is not null).ToArray();
        RequireDataEdge(data, callId ?? string.Empty, "out", toArray, "in", Issue, "WorkflowCall.out must feed JsonToArray.in.");
        RequireDataEdge(data, toArray, "out", length, "in", Issue, "JsonToArray.out must feed ArrayLength.in.");
        RequireDataEdge(data, length, "out", compare, "left", Issue, "ArrayLength.out must feed IntegerEquals.left.");
        RequireDataEdge(data, constant, "out", compare, "right", Issue, "IntegerConstant.out must feed IntegerEquals.right.");
        RequireDataEdge(data, compare, "out", branch, "condition", Issue, "IntegerEquals.out must feed BooleanBranch.condition.");
        RequireDataEdge(data, length, "out", outputId, "in", Issue, "ArrayLength.out must feed Output.in.");

        var expected = new HashSet<(string Source, string SourcePort, string Target, string TargetPort)>
        {
            (callId ?? string.Empty, "out", toArray, "in"),
            (toArray, "out", length, "in"),
            (length, "out", compare, "left"),
            (constant, "out", compare, "right"),
            (compare, "out", branch, "condition"),
            (length, "out", outputId, "in")
        };
        foreach (var edge in data)
            if (!expected.Contains((edge.Source.NodeId, edge.Source.PortId, edge.Target.NodeId, edge.Target.PortId)))
                Issue("sequence.call.json.control.data", "connections", "Schema 1.57 permits only the fixed array-length predicate data topology.");
        if (output.Value!.OutputDataType != WorkflowDataTypes.Integer)
            Issue("sequence.call.json.control.output", $"nodes.{output.Key}", "The true branch Output must be integer-valued.");
        if (callId is not null)
        {
            var child = definition.Nodes[callId].WorkflowCall!;
            paths.Add(new(descriptor.Id, nodeIds.ToImmutable(), callId, toArray, length, compare, constant, branch, terminate,
                child.WorkflowId, child.RevisionId, child.ContentHash));
        }
        return new(paths.ToImmutable(), issues.ToImmutable());
    }

    private static void ValidateJsonCall(NodeDefinition node, string nodeId, Action<string, string, string> issue)
    {
        var call = node.WorkflowCall;
        if (call is null || call.Definition is null || call.RevisionId == Guid.Empty || string.IsNullOrWhiteSpace(call.WorkflowId) ||
            !WorkflowJson.MatchesHash(call.Definition, call.ContentHash))
        {
            issue("sequence.call.json.control.binding", $"nodes.{nodeId}.workflowCall", "WorkflowCall must pin a child with a matching identity and content hash.");
            return;
        }
        var input = call.Definition.Nodes.GetValueOrDefault(call.Definition.EntryNodeId);
        var output = call.Definition.Nodes.Values.FirstOrDefault(candidate => candidate?.Type == NodeTypes.Output);
        if (input is null || output is null || WorkflowPorts.Outputs(input).SingleOrDefault()?.DataType != WorkflowDataTypes.Json ||
            WorkflowPorts.Inputs(output).SingleOrDefault()?.DataType != WorkflowDataTypes.Json)
            issue("sequence.call.json.control.contract", $"nodes.{nodeId}.workflowCall", "WorkflowCall must pin a JSON-input/JSON-output child.");
    }

    private static void RequireExecutionEdge(IEnumerable<ConnectionDefinition> edges, string source, string sourcePort, string target, string targetPort,
        Action<string, string, string> issue, string message)
    {
        if (edges.Count(edge => edge.Source == new PortReference(source, sourcePort) && edge.Target == new PortReference(target, targetPort)) != 1)
            issue("sequence.call.json.control.edge", "executionConnections", message);
    }

    private static void RequireDataEdge(IEnumerable<ConnectionDefinition> edges, string source, string sourcePort, string target, string targetPort,
        Action<string, string, string> issue, string message)
    {
        if (edges.Count(edge => edge.Source == new PortReference(source, sourcePort) && edge.Target == new PortReference(target, targetPort)) != 1)
            issue("sequence.call.json.control.edge", "connections", message);
    }
}
