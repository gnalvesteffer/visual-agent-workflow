using System.Collections.Immutable;
using VisualAgentWorkflow.Core;
using Xunit;

namespace VisualAgentWorkflow.Core.Tests;

public sealed class LoopVariableTopologyTests
{
    [Fact]
    public void Analyzer_accepts_the_bounded_explicit_loop_variable_shape_without_schema_admission()
    {
        var candidate = Candidate();

        var analysis = LoopVariableTopologyAnalyzer.Analyze(candidate);

        Assert.True(analysis.IsValid, string.Join(Environment.NewLine, analysis.Issues.Select(issue => issue.Message)));
        Assert.Equal(["body", "set", "get"], analysis.Topology!.BodyNodeIds.ToArray());
        Assert.Equal(["set", "get"], analysis.Topology.VariableNodeIds.ToArray());
        Assert.Empty(WorkflowValidator.Validate(candidate));
    }

    [Fact]
    public void Analyzer_rejects_a_shared_or_non_linear_body()
    {
        var candidate = Candidate() with
        {
            ExecutionConnections = Candidate().ExecutionConnections!.SetItem("extra", new(
                new("set", "then"), new("output", "execute"), "execution"))
        };

        var analysis = LoopVariableTopologyAnalyzer.Analyze(candidate);

        Assert.Contains(analysis.Issues, issue => issue.Code == "loop.body.path");
        Assert.False(analysis.IsValid);
    }

    internal static WorkflowDefinition Candidate()
    {
        var nodes = new Dictionary<string, NodeDefinition>
        {
            ["input"] = new(NodeTypes.Input, "1.0.0"),
            ["loop"] = new(NodeTypes.Loop, "1.0.0") { Loop = new(new("contains", "continue"), 3) },
            ["body"] = new(NodeTypes.LoopBody, "1.0.0"),
            ["set"] = new(NodeTypes.VariableSet, "1.0.0") { VariableId = "message", InputDataType = WorkflowDataTypes.String },
            ["get"] = new(NodeTypes.VariableGet, "1.0.0") { VariableId = "message", OutputDataType = WorkflowDataTypes.String },
            ["output"] = new(NodeTypes.Output, "1.0.0") { OutputDataType = WorkflowDataTypes.String }
        };
        var data = new Dictionary<string, ConnectionDefinition>
        {
            ["set-value"] = new(new("input", "out"), new("set", "value"), "data"),
            ["output-value"] = new(new("get", "out"), new("output", "in"), "data")
        };
        var execution = new Dictionary<string, ConnectionDefinition>
        {
            ["input-loop"] = new(new("input", "then"), new("loop", "execute"), "execution"),
            ["loop-body"] = new(new("loop", "body"), new("body", "execute"), "execution"),
            ["body-set"] = new(new("body", "then"), new("set", "execute"), "execution"),
            ["set-get"] = new(new("set", "then"), new("get", "execute"), "execution"),
            ["get-loop"] = new(new("get", "then"), new("loop", "execute"), "execution"),
            ["loop-output"] = new(new("loop", "done"), new("output", "execute"), "execution")
        };
        return new WorkflowDefinition(WorkflowSchemaVersions.LoopVariablesGraph, "loop-variable-candidate", "input",
            nodes.ToImmutableSortedDictionary(), data.ToImmutableSortedDictionary())
        {
            Variables = ImmutableSortedDictionary<string, WorkflowVariableDefinition>.Empty
                .Add("message", new("Message", "default")),
            ExecutionConnections = execution.ToImmutableSortedDictionary()
        };
    }
}
