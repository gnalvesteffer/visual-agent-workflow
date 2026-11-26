using System.Collections.Immutable;
using VisualAgentWorkflow.Core;
using Xunit;

namespace VisualAgentWorkflow.AgentFramework.Tests;

/// <summary>
/// Decision 0070, "Slice (b) unblockers 1 and 2 (2026-11-26)", unblocker 2. A Sequence graph runs on the Sequence
/// builder, which has no For Each or Loop runtime: its executor for an iterator recorded <c>node.started</c> and then threw
/// "has no activation frame owner". The schema 1.57 JSON control topology checks only the first Sequence output, so a For
/// Each (or Loop) on a second output passed validation and reached that throw. The validator now rejects every Sequence
/// graph that contains an iterator, and the runner refuses such a definition before its first event.
/// </summary>
public sealed class SequenceForEachRejectionTests
{
    private static ConnectionDefinition Execution(string from, string fromPin, string to, string toPin) =>
        new(new(from, fromPin), new(to, toPin), "execution");

    private static ConnectionDefinition Data(string from, string fromPin, string to, string toPin) =>
        new(new(from, fromPin), new(to, toPin), "data");

    private static WorkflowDefinition JsonChild(string workflowId) => new("1.13", workflowId, "input",
        new Dictionary<string, NodeDefinition>
        {
            ["input"] = new(NodeTypes.Input, "1.0.0") { InputDataType = WorkflowDataTypes.Json },
            ["output"] = new(NodeTypes.Output, "1.0.0") { OutputDataType = WorkflowDataTypes.Json }
        }.ToImmutableSortedDictionary(),
        new Dictionary<string, ConnectionDefinition>
        {
            ["value"] = new(new("input", "out"), new("output", "in"), "data")
        }.ToImmutableSortedDictionary());

    /// <summary>
    /// The public schema 1.57 control graph (first output: pinned JSON call, length predicate, Boolean Branch) with its
    /// second output routed through an iterator whose body returns at once and whose completed pin reaches Output.
    /// </summary>
    private static WorkflowDefinition ControlGraphWithIterator(string schema, NodeDefinition iterator)
    {
        var child = WorkflowRevision.Create(JsonChild("sequence-iterator-child"), "test");
        return new WorkflowDefinition(schema, "sequence-iterator-parent", "input",
            new Dictionary<string, NodeDefinition>
            {
                ["input"] = new(NodeTypes.Input, "1.0.0"),
                ["sequence"] = new(NodeTypes.Sequence, "1.0.0") { Sequence = new([new("call", "Pinned child"), new("sibling", "Sibling")]) },
                ["call"] = new(NodeTypes.WorkflowCall, "1.0.0")
                {
                    WorkflowCall = new(child.Definition.WorkflowId, child.RevisionId, 1, child.ContentHash, child.Definition)
                },
                ["each"] = iterator,
                ["body"] = new(NodeTypes.LoopBody, "1.0.0"),
                ["to-array"] = new(NodeTypes.JsonToArray, "1.0.0"),
                ["length"] = new(NodeTypes.ArrayLength, "1.0.0"),
                ["equals"] = new(NodeTypes.IntegerEquals, "1.0.0"),
                ["constant"] = new(NodeTypes.IntegerConstant, "1.0.0") { IntegerConstant = new(3) },
                ["branch"] = new(NodeTypes.BooleanBranch, "1.0.0"),
                ["terminate"] = new(NodeTypes.Terminate, "1.0.0") { Termination = new(WorkflowTerminationOutcomes.Failure) },
                ["output"] = new(NodeTypes.Output, "1.0.0") { OutputDataType = WorkflowDataTypes.Integer }
            }.ToImmutableSortedDictionary(),
            new Dictionary<string, ConnectionDefinition>
            {
                ["call-array"] = Data("call", "out", "to-array", "in"),
                ["array-length"] = Data("to-array", "out", "length", "in"),
                ["length-equals"] = Data("length", "out", "equals", "left"),
                ["constant-equals"] = Data("constant", "out", "equals", "right"),
                ["equals-branch"] = Data("equals", "out", "branch", "condition"),
                ["length-output"] = Data("length", "out", "output", "in")
            }.ToImmutableSortedDictionary())
        {
            ExecutionConnections = new Dictionary<string, ConnectionDefinition>
            {
                ["input-sequence"] = Execution("input", "then", "sequence", "execute"),
                ["sequence-call"] = Execution("sequence", "then.call", "call", "execute"),
                ["call-branch"] = Execution("call", "out", "branch", "execute"),
                ["branch-output"] = Execution("branch", "true", "output", "execute"),
                ["branch-terminate"] = Execution("branch", "false", "terminate", "execute"),
                ["sequence-each"] = Execution("sequence", "then.sibling", "each", "execute"),
                ["each-body"] = Execution("each", "body", "body", "execute"),
                ["body-return"] = Execution("body", "then", "each", "feedback"),
                ["each-output"] = Execution("each", "completed", "output", "execute")
            }.ToImmutableSortedDictionary()
        };
    }

    public static TheoryData<string, string, string> Shapes => new()
    {
        // (schema, iterator kind, expected code)
        { WorkflowSchemaVersions.UnifiedWorkflowGraph, "typed-for-each", "foreach.sequence.unsupported" },
        { WorkflowSchemaVersions.SequenceWorkflowCallJsonControlGraph, "untyped-for-each", "foreach.sequence.unsupported" },
        { WorkflowSchemaVersions.UnifiedWorkflowGraph, "loop", "loop.sequence.unsupported" }
    };

    private static NodeDefinition Iterator(string kind) => kind switch
    {
        "typed-for-each" => new(NodeTypes.ForEach, "1.0.0") { ArrayElementType = WorkflowDataTypes.String },
        "untyped-for-each" => new(NodeTypes.ForEach, "1.0.0"),
        _ => new(NodeTypes.Loop, "1.0.0") { Loop = new(new("contains", "continue"), 2) }
    };

    [Theory]
    [MemberData(nameof(Shapes))]
    public async Task A_sequence_graph_with_an_iterator_on_a_sibling_output_is_rejected_before_any_event(string schema,
        string kind, string code)
    {
        var definition = ControlGraphWithIterator(schema, Iterator(kind));

        // Before the fix the validator returned no issue for this shape, and the run recorded run.started, the Sequence
        // dispatch and node.started for "each" before the iterator threw "has no activation frame owner".
        var issue = Assert.Single(WorkflowValidator.Validate(definition));
        Assert.Equal(code, issue.Code);
        Assert.Equal("nodes.each", issue.Path);

        var events = new List<ExecutionEvent>();
        var failure = await Assert.ThrowsAnyAsync<ArgumentException>(() => new WorkflowRunner().ExecuteWithOutcomeAsync(
            Guid.NewGuid(), WorkflowRevision.Create(WorkflowJson.Deserialize(WorkflowJson.Serialize(definition)), "test"),
            "[1,2,3]", null, evt => { events.Add(evt); return ValueTask.CompletedTask; }));

        Assert.Empty(events);
        Assert.Contains(code, failure.Message, StringComparison.Ordinal);
        Assert.Contains("nodes.each", failure.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(NodeTypes.ForEach, "For Each")]
    [InlineData(NodeTypes.Loop, "Loop")]
    public async Task The_sequence_route_refuses_an_iterator_before_the_first_event_when_validation_is_skipped(string type,
        string label)
    {
        // The private Sequence decision proof skips validation and runs the Sequence builder. Before the fix an iterator
        // that no edge activates was carried into that run unnoticed and the run completed; one that an edge reached
        // recorded node.started and then threw. The runner now refuses the definition before run.started.
        var proof = WorkflowRunner.CreateSequenceBranchDecisionEvidenceProofRevision(true).Definition;
        var iterator = type == NodeTypes.ForEach
            ? new NodeDefinition(NodeTypes.ForEach, "1.0.0") { ArrayElementType = WorkflowDataTypes.String }
            : new NodeDefinition(NodeTypes.Loop, "1.0.0") { Loop = new(new("contains", "continue"), 2) };
        var revision = WorkflowRevision.CreateProofOnly(proof with { Nodes = proof.Nodes.Add("each", iterator) },
            "sequence-iterator-precondition");
        var events = new List<ExecutionEvent>();

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => new WorkflowRunner()
            .ExecuteSequenceBranchDecisionEvidenceProofAsync(Guid.NewGuid(), revision, "input",
                evt => { events.Add(evt); return ValueTask.CompletedTask; }));

        Assert.Empty(events);
        Assert.Contains($"{label} 'each'", failure.Message, StringComparison.Ordinal);
        Assert.Contains("Sequence", failure.Message, StringComparison.Ordinal);
    }
}
