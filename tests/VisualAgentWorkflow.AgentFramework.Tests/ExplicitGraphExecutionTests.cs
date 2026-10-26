using System.Collections.Immutable;
using System.Text.Json;
using VisualAgentWorkflow.AgentFramework;
using VisualAgentWorkflow.Core;
using Xunit;

namespace VisualAgentWorkflow.AgentFramework.Tests;

public sealed class ExplicitGraphExecutionTests
{
    [Theory]
    [InlineData("yes", "true", "yes", false)]
    [InlineData("no", "false", null, true)]
    public async Task Boolean_branch_records_the_selected_route_and_only_evaluates_reached_output(
        string input, string expectedRoute, string? expectedOutput, bool failed)
    {
        var definition = BranchDefinition();
        WorkflowValidator.ThrowIfInvalid(definition);
        var events = new List<ExecutionEvent>();

        var result = await new WorkflowRunner().ExecuteWithOutcomeAsync(Guid.NewGuid(),
            WorkflowRevision.Create(definition, "test"), input, null,
            evt => { events.Add(evt); return ValueTask.CompletedTask; });

        var branchDecision = Assert.Single(events, evt => evt.Kind == ExecutionEventKinds.BranchSelected && evt.NodeId == "branch");
        Assert.Contains($"\"port\":\"{expectedRoute}\"", branchDecision.Data, StringComparison.Ordinal);
        var activatedRoute = Assert.Single(events, evt => evt.Kind == ExecutionEventKinds.ExecutionActivated &&
            evt.ConnectionId == $"execution:{expectedRoute}-route");
        Assert.Equal(expectedRoute == "true" ? "output" : "terminate", activatedRoute.NodeId);
        var comparison = Assert.Single(events, evt => evt.Kind == ExecutionEventKinds.DataEvaluated && evt.NodeId == "equals");
        using (var comparisonData = JsonDocument.Parse(comparison.Data!))
        {
            Assert.Equal("boolean", comparisonData.RootElement.GetProperty("dataType").GetString());
            Assert.Equal(expectedRoute == "true", comparisonData.RootElement.GetProperty("value").GetBoolean());
        }
        Assert.Equal(failed, result.Failed);
        Assert.True(result.Terminated == failed);
        Assert.Equal(expectedOutput, result.Output);
        Assert.Equal(expectedRoute == "true", events.Any(evt => evt.Kind == ExecutionEventKinds.NodeStarted && evt.NodeId == "output"));
        var outputEvaluations = events.Where(evt => evt.Kind == ExecutionEventKinds.DataEvaluated).Where(evt =>
        {
            using var data = JsonDocument.Parse(evt.Data!);
            return data.RootElement.GetProperty("activatedByNodeId").GetString() == "output";
        });
        Assert.Equal(expectedRoute == "true", outputEvaluations.Any());
    }

    [Theory]
    [InlineData("yes", "false", null, true)]
    [InlineData("no", "true", "no", false)]
    public async Task Boolean_not_inverts_a_typed_boolean_before_branching(
        string input, string expectedRoute, string? expectedOutput, bool failed)
    {
        var definition = BooleanNotDefinition();
        WorkflowValidator.ThrowIfInvalid(definition);
        var events = new List<ExecutionEvent>();

        var result = await new WorkflowRunner().ExecuteWithOutcomeAsync(Guid.NewGuid(),
            WorkflowRevision.Create(definition, "test"), input, null,
            evt => { events.Add(evt); return ValueTask.CompletedTask; });

        Assert.Equal(expectedOutput, result.Output);
        Assert.Equal(failed, result.Failed);
        Assert.Contains(events, evt => evt.Kind == ExecutionEventKinds.BranchSelected &&
            evt.NodeId == "branch" && evt.Data!.Contains($"\"port\":\"{expectedRoute}\"", StringComparison.Ordinal));
        var inverted = Assert.Single(events, evt => evt.Kind == ExecutionEventKinds.DataEvaluated && evt.NodeId == "invert");
        using var data = JsonDocument.Parse(inverted.Data!);
        Assert.Equal(expectedRoute == "true", data.RootElement.GetProperty("value").GetBoolean());
        Assert.Equal("boolean", data.RootElement.GetProperty("dataType").GetString());
    }

    [Theory]
    [InlineData(true, "true", "Hello", false)]
    [InlineData(false, "false", null, true)]
    public async Task Boolean_constant_can_select_either_branch_route(bool value, string expectedRoute, string? output, bool failed)
    {
        var definition = BooleanConstantDefinition(value);
        WorkflowValidator.ThrowIfInvalid(definition);
        var events = new List<ExecutionEvent>();

        var result = await new WorkflowRunner().ExecuteWithOutcomeAsync(Guid.NewGuid(),
            WorkflowRevision.Create(definition, "test"), "Hello", null,
            evt => { events.Add(evt); return ValueTask.CompletedTask; });

        Assert.Equal(output, result.Output);
        Assert.Equal(failed, result.Failed);
        var decision = Assert.Single(events, evt => evt.Kind == ExecutionEventKinds.BranchSelected && evt.NodeId == "branch");
        Assert.Contains($"\"port\":\"{expectedRoute}\"", decision.Data!, StringComparison.Ordinal);
        var constant = Assert.Single(events, evt => evt.Kind == ExecutionEventKinds.DataEvaluated && evt.NodeId == "constant");
        using var data = JsonDocument.Parse(constant.Data!);
        Assert.Equal(value, data.RootElement.GetProperty("value").GetBoolean());
    }

    [Fact]
    public async Task Integer_parameter_addition_and_equality_choose_branch_and_record_typed_values()
    {
        var definition = IntegerParameterBranchDefinition();
        Assert.Empty(WorkflowValidator.Validate(definition));
        var events = new List<ExecutionEvent>();

        var result = await new WorkflowRunner().ExecuteWithOutcomeAsync(Guid.NewGuid(),
            WorkflowRevision.Create(definition, "test"), "ignored",
            new Dictionary<string, string> { ["base"] = "41" },
            evt => { events.Add(evt); return ValueTask.CompletedTask; });

        Assert.False(result.Failed);
        Assert.Equal("42", result.Output);
        Assert.Contains(events, evt => evt.Kind == ExecutionEventKinds.BranchSelected && evt.NodeId == "branch" &&
            evt.Data!.Contains("\"port\":\"true\"", StringComparison.Ordinal));
        var sum = Assert.Single(events, evt =>
        {
            if (evt.Kind != ExecutionEventKinds.DataEvaluated || evt.NodeId != "sum") return false;
            using var evidence = JsonDocument.Parse(evt.Data!);
            return evidence.RootElement.GetProperty("activatedByNodeId").GetString() == "branch";
        });
        using var sumEvidence = JsonDocument.Parse(sum.Data!);
        Assert.Equal("integer", sumEvidence.RootElement.GetProperty("dataType").GetString());
        Assert.Equal(42, sumEvidence.RootElement.GetProperty("value").GetInt64());
    }

    [Fact]
    public async Task Integer_addition_overflow_fails_at_the_deterministic_data_node()
    {
        var definition = IntegerParameterBranchDefinition();
        var events = new List<ExecutionEvent>();

        await Assert.ThrowsAsync<InvalidOperationException>(() => new WorkflowRunner().ExecuteWithOutcomeAsync(Guid.NewGuid(),
            WorkflowRevision.Create(definition, "test"), "ignored",
            new Dictionary<string, string> { ["base"] = long.MaxValue.ToString(System.Globalization.CultureInfo.InvariantCulture) },
            evt => { events.Add(evt); return ValueTask.CompletedTask; }));

        Assert.Contains(events, evt => evt.Kind == ExecutionEventKinds.NodeFailed && evt.NodeId == "sum");
        Assert.Contains(events, evt => evt.Kind == ExecutionEventKinds.NodeFailed && evt.NodeId == "branch");
        Assert.DoesNotContain(events, evt => evt.Kind == ExecutionEventKinds.BranchSelected);
    }

    [Theory]
    [InlineData(false, false, "failure", true, null, 1)]
    [InlineData(true, false, "success", true, null, 2)]
    [InlineData(true, true, "output", false, "Hello", 2)]
    public async Task Nested_boolean_routes_execute_only_the_selected_path(
        bool rootChoice, bool nestedChoice, string terminalNode, bool terminated, string? output, int reachedBranches)
    {
        var definition = BooleanTopologyDefinition(rootChoice, nestedChoice);
        WorkflowValidator.ThrowIfInvalid(definition);
        var events = new List<ExecutionEvent>();

        var result = await new WorkflowRunner().ExecuteWithOutcomeAsync(Guid.NewGuid(),
            WorkflowRevision.Create(definition, "test"), "Hello", null,
            evt => { events.Add(evt); return ValueTask.CompletedTask; });

        Assert.Equal(output, result.Output);
        Assert.Equal(terminated, result.Terminated);
        Assert.Equal(terminalNode == "failure", result.Failed);
        Assert.Equal(reachedBranches, events.Count(evt => evt.Kind == ExecutionEventKinds.BranchSelected));
        Assert.Equal(reachedBranches, events.Count(evt => evt.Kind == ExecutionEventKinds.NodeStarted && evt.NodeId is "root" or "nested"));
        Assert.Contains(events, evt => evt.Kind == ExecutionEventKinds.NodeStarted && evt.NodeId == terminalNode ||
            terminalNode == "output" && evt.Kind == ExecutionEventKinds.NodeStarted && evt.NodeId == "output");
        Assert.Equal(output is not null, events.Any(evt => evt.Kind == ExecutionEventKinds.DataEvaluated &&
            evt.Data!.Contains("\"activatedByNodeId\":\"output\"", StringComparison.Ordinal)));
        var activatedEdges = events.Where(evt => evt.Kind == ExecutionEventKinds.ExecutionActivated)
            .Select(evt => evt.ConnectionId).ToArray();
        Assert.Contains("execution:activate-root", activatedEdges);
        Assert.Contains(rootChoice ? "execution:root-true" : "execution:root-false", activatedEdges);
        if (rootChoice) Assert.Contains(nestedChoice ? "execution:nested-true" : "execution:nested-false", activatedEdges);
        else Assert.DoesNotContain(activatedEdges, edge => edge is "execution:nested-true" or "execution:nested-false");
        Assert.DoesNotContain(events, evt => evt.Kind == ExecutionEventKinds.NodeStarted && evt.NodeId == (terminalNode == "output" ? "success" : "output"));
    }

    [Fact]
    public async Task Mutually_exclusive_routes_can_reconverge_without_activating_a_shared_branch_twice()
    {
        var baseDefinition = BooleanTopologyDefinition(rootChoice: false, nestedChoice: true);
        var definition = baseDefinition with
        {
            Nodes = baseDefinition.Nodes.Remove("failure"),
            ExecutionConnections = baseDefinition.ExecutionConnections!
                .Remove("root-false")
                .Add("root-false-to-nested", new(new("root", "false"), new("nested", "execute"), "execution"))
        };
        WorkflowValidator.ThrowIfInvalid(definition);
        var events = new List<ExecutionEvent>();

        var result = await new WorkflowRunner().ExecuteWithOutcomeAsync(Guid.NewGuid(),
            WorkflowRevision.Create(definition, "test"), "Hello", null,
            evt => { events.Add(evt); return ValueTask.CompletedTask; });

        Assert.Equal("Hello", result.Output);
        Assert.Equal(1, events.Count(evt => evt.Kind == ExecutionEventKinds.NodeStarted && evt.NodeId == "nested"));
        Assert.Equal(1, events.Count(evt => evt.Kind == ExecutionEventKinds.BranchSelected && evt.NodeId == "nested"));
        Assert.Contains(events, evt => evt.Kind == ExecutionEventKinds.ExecutionActivated && evt.ConnectionId == "execution:root-false-to-nested");
        Assert.Contains(events, evt => evt.Kind == ExecutionEventKinds.ExecutionActivated && evt.ConnectionId == "execution:nested-true");
    }

    [Fact]
    public async Task Pure_graph_function_runs_lazily_and_records_its_pinned_snapshot_and_values()
    {
        var function = AppendSuffixFunction();
        var definition = GraphFunctionCaller(function);
        WorkflowValidator.ThrowIfInvalid(definition);
        var events = new List<ExecutionEvent>();

        var result = await new WorkflowRunner().ExecuteWithOutcomeAsync(Guid.NewGuid(),
            WorkflowRevision.Create(definition, "test"), "hello", null,
            evt => { events.Add(evt); return ValueTask.CompletedTask; });

        Assert.Equal("hello!", result.Output);
        var callEvidence = Assert.Single(events, evt => evt.Kind == ExecutionEventKinds.DataEvaluated && evt.NodeId == "call");
        using var evidence = JsonDocument.Parse(callEvidence.Data!);
        Assert.Equal(function.FunctionId, evidence.RootElement.GetProperty("function").GetProperty("functionId").GetString());
        Assert.Equal(function.Version, evidence.RootElement.GetProperty("function").GetProperty("version").GetInt32());
        Assert.Equal(GraphFunctionJson.ComputeHash(function), evidence.RootElement.GetProperty("function").GetProperty("contentHash").GetString());
        Assert.Equal("hello", evidence.RootElement.GetProperty("function").GetProperty("inputs").GetProperty("text").GetString());
        Assert.Contains(evidence.RootElement.GetProperty("function").GetProperty("evaluatedValues").EnumerateArray(),
            value => value.GetProperty("nodeId").GetString() == "concat" && value.GetProperty("value").GetString() == "hello!");
    }

    [Fact]
    public async Task Data_wires_evaluate_pure_values_without_activating_them()
    {
        var definition = new WorkflowDefinition(
            "1.23", "explicit-data", "input",
            new Dictionary<string, NodeDefinition>
            {
                ["input"] = new(NodeTypes.Input, "1.0.0"),
                ["constant"] = new(NodeTypes.StringConstant, "1.0.0") { StringConstant = new("!") },
                ["concat"] = new(NodeTypes.TextConcat, "1.0.0"),
                ["output"] = new(NodeTypes.Output, "1.0.0")
            }.ToImmutableSortedDictionary(StringComparer.Ordinal),
            new Dictionary<string, ConnectionDefinition>
            {
                ["input-value"] = new(new("input", "out"), new("concat", "left"), "data"),
                ["suffix"] = new(new("constant", "out"), new("concat", "right"), "data"),
                ["result"] = new(new("concat", "out"), new("output", "in"), "data")
            }.ToImmutableSortedDictionary(StringComparer.Ordinal))
        {
            ExecutionConnections = new Dictionary<string, ConnectionDefinition>
            {
                ["activate-output"] = new(new("input", "then"), new("output", "execute"), "execution")
            }.ToImmutableSortedDictionary(StringComparer.Ordinal)
        };
        var events = new List<ExecutionEvent>();

        var result = await new WorkflowRunner().ExecuteWithOutcomeAsync(Guid.NewGuid(),
            WorkflowRevision.Create(definition, "test"), "Hello",
            parameters: null,
            evt => { events.Add(evt); return ValueTask.CompletedTask; });

        Assert.Equal("Hello!", result.Output);
        Assert.False(result.Terminated);
        Assert.Single(events, evt => evt.Kind == ExecutionEventKinds.ExecutionActivated && evt.ConnectionId == "execution:activate-output");
        Assert.DoesNotContain(events, evt => evt.Kind == ExecutionEventKinds.Transfer);
        Assert.DoesNotContain(events, evt => evt.Kind == ExecutionEventKinds.NodeStarted && evt.NodeId is "constant" or "concat");
        Assert.Equal(3, events.Count(evt => evt.Kind == ExecutionEventKinds.DataEvaluated));
        var output = Assert.Single(events, evt => evt.Kind == ExecutionEventKinds.Completed);
        Assert.Equal("Hello!", output.Data);

        var values = events.Where(evt => evt.Kind == ExecutionEventKinds.DataEvaluated)
            .Select(evt => JsonDocument.Parse(evt.Data!).RootElement.GetProperty("value").GetString())
            .ToArray();
        Assert.Equal(new[] { "Hello", "!", "Hello!" }, values.Select(value => value!));
        Assert.Equal(new[] { "data:input-value", "data:result", "data:suffix" }, events
            .Where(evt => evt.Kind == ExecutionEventKinds.DataEvaluated)
            .Select(evt => evt.ConnectionId)
            .Order(StringComparer.Ordinal));
    }

    private static WorkflowDefinition BranchDefinition() => new(
        "1.24", "boolean-branch", "input",
        new Dictionary<string, NodeDefinition>
        {
            ["input"] = new(NodeTypes.Input, "1.0.0"),
            ["expected"] = new(NodeTypes.StringConstant, "1.0.0") { StringConstant = new("yes") },
            ["equals"] = new(NodeTypes.StringEquals, "1.0.0"),
            ["branch"] = new(NodeTypes.BooleanBranch, "1.0.0"),
            ["output"] = new(NodeTypes.Output, "1.0.0"),
            ["terminate"] = new(NodeTypes.Terminate, "1.0.0") { Termination = new(WorkflowTerminationOutcomes.Failure) }
        }.ToImmutableSortedDictionary(StringComparer.Ordinal),
        new Dictionary<string, ConnectionDefinition>
        {
            ["input-left"] = new(new("input", "out"), new("equals", "left"), "data"),
            ["expected-right"] = new(new("expected", "out"), new("equals", "right"), "data"),
            ["comparison"] = new(new("equals", "out"), new("branch", "condition"), "data"),
            ["output-value"] = new(new("input", "out"), new("output", "in"), "data")
        }.ToImmutableSortedDictionary(StringComparer.Ordinal))
    {
        ExecutionConnections = new Dictionary<string, ConnectionDefinition>
        {
            ["activate-branch"] = new(new("input", "then"), new("branch", "execute"), "execution"),
            ["true-route"] = new(new("branch", "true"), new("output", "execute"), "execution"),
            ["false-route"] = new(new("branch", "false"), new("terminate", "execute"), "execution")
        }.ToImmutableSortedDictionary(StringComparer.Ordinal)
    };

    private static WorkflowDefinition BooleanNotDefinition() => new WorkflowDefinition(
        "1.25", "boolean-not", "input",
        new Dictionary<string, NodeDefinition>
        {
            ["input"] = new(NodeTypes.Input, "1.0.0"),
            ["expected"] = new(NodeTypes.StringConstant, "1.0.0") { StringConstant = new("yes") },
            ["equals"] = new(NodeTypes.StringEquals, "1.0.0"),
            ["invert"] = new(NodeTypes.BooleanNot, "1.0.0"),
            ["branch"] = new(NodeTypes.BooleanBranch, "1.0.0"),
            ["output"] = new(NodeTypes.Output, "1.0.0"),
            ["terminate"] = new(NodeTypes.Terminate, "1.0.0") { Termination = new(WorkflowTerminationOutcomes.Failure) }
        }.ToImmutableSortedDictionary(StringComparer.Ordinal),
        new Dictionary<string, ConnectionDefinition>
        {
            ["input-left"] = new(new("input", "out"), new("equals", "left"), "data"),
            ["expected-right"] = new(new("expected", "out"), new("equals", "right"), "data"),
            ["equals-invert"] = new(new("equals", "out"), new("invert", "value"), "data"),
            ["invert-branch"] = new(new("invert", "out"), new("branch", "condition"), "data"),
            ["output-value"] = new(new("input", "out"), new("output", "in"), "data")
        }.ToImmutableSortedDictionary(StringComparer.Ordinal))
    {
        ExecutionConnections = new Dictionary<string, ConnectionDefinition>
        {
            ["activate-branch"] = new(new("input", "then"), new("branch", "execute"), "execution"),
            ["true-route"] = new(new("branch", "true"), new("output", "execute"), "execution"),
            ["false-route"] = new(new("branch", "false"), new("terminate", "execute"), "execution")
        }.ToImmutableSortedDictionary(StringComparer.Ordinal)
    };

    private static WorkflowDefinition BooleanConstantDefinition(bool value) => new WorkflowDefinition(
        "1.25", "boolean-constant", "input",
        new Dictionary<string, NodeDefinition>
        {
            ["input"] = new(NodeTypes.Input, "1.0.0"),
            ["constant"] = new(NodeTypes.BooleanConstant, "1.0.0") { BooleanConstant = new(value) },
            ["branch"] = new(NodeTypes.BooleanBranch, "1.0.0"),
            ["output"] = new(NodeTypes.Output, "1.0.0"),
            ["terminate"] = new(NodeTypes.Terminate, "1.0.0") { Termination = new(WorkflowTerminationOutcomes.Failure) }
        }.ToImmutableSortedDictionary(StringComparer.Ordinal),
        new Dictionary<string, ConnectionDefinition>
        {
            ["constant-branch"] = new(new("constant", "out"), new("branch", "condition"), "data"),
            ["output-value"] = new(new("input", "out"), new("output", "in"), "data")
        }.ToImmutableSortedDictionary(StringComparer.Ordinal))
    {
        ExecutionConnections = new Dictionary<string, ConnectionDefinition>
        {
            ["activate-branch"] = new(new("input", "then"), new("branch", "execute"), "execution"),
            ["true-route"] = new(new("branch", "true"), new("output", "execute"), "execution"),
            ["false-route"] = new(new("branch", "false"), new("terminate", "execute"), "execution")
        }.ToImmutableSortedDictionary(StringComparer.Ordinal)
    };

    private static WorkflowDefinition IntegerParameterBranchDefinition() => new WorkflowDefinition(
        WorkflowSchemaVersions.IntegerGraph, "integer-parameter-branch", "input",
        new Dictionary<string, NodeDefinition>
        {
            ["input"] = new(NodeTypes.Input, "1.0.0"),
            ["base"] = new(NodeTypes.WorkflowValue, "1.0.0") { ParameterId = "base", OutputDataType = "integer" },
            ["one"] = new(NodeTypes.IntegerConstant, "1.0.0") { IntegerConstant = new(1) },
            ["expected"] = new(NodeTypes.IntegerConstant, "1.0.0") { IntegerConstant = new(42) },
            ["sum"] = new(NodeTypes.IntegerAdd, "1.0.0"),
            ["equals"] = new(NodeTypes.IntegerEquals, "1.0.0"),
            ["branch"] = new(NodeTypes.BooleanBranch, "1.0.0"),
            ["output"] = new(NodeTypes.Output, "1.0.0") { OutputDataType = "integer" },
            ["failure"] = new(NodeTypes.Terminate, "1.0.0") { Termination = new(WorkflowTerminationOutcomes.Failure) }
        }.ToImmutableSortedDictionary(StringComparer.Ordinal),
        new Dictionary<string, ConnectionDefinition>
        {
            ["base-left"] = new(new("base", "out"), new("sum", "left"), "data"),
            ["one-right"] = new(new("one", "out"), new("sum", "right"), "data"),
            ["sum-left"] = new(new("sum", "out"), new("equals", "left"), "data"),
            ["expected-right"] = new(new("expected", "out"), new("equals", "right"), "data"),
            ["equals-condition"] = new(new("equals", "out"), new("branch", "condition"), "data"),
            ["sum-output"] = new(new("sum", "out"), new("output", "in"), "data")
        }.ToImmutableSortedDictionary(StringComparer.Ordinal))
    {
        Parameters = ImmutableSortedDictionary<string, WorkflowParameterDefinition>.Empty
            .Add("base", new WorkflowParameterDefinition("Base", "41") { Type = "integer" }),
        ExecutionConnections = new Dictionary<string, ConnectionDefinition>
        {
            ["activate"] = new(new("input", "then"), new("branch", "execute"), "execution"),
            ["success"] = new(new("branch", "true"), new("output", "execute"), "execution"),
            ["failure"] = new(new("branch", "false"), new("failure", "execute"), "execution")
        }.ToImmutableSortedDictionary(StringComparer.Ordinal)
    };

    private static WorkflowDefinition BooleanTopologyDefinition(bool rootChoice, bool nestedChoice) => new WorkflowDefinition(
        "1.26", "boolean-topology", "input",
        new Dictionary<string, NodeDefinition>
        {
            ["input"] = new(NodeTypes.Input, "1.0.0"),
            ["root-value"] = new(NodeTypes.BooleanConstant, "1.0.0") { BooleanConstant = new(rootChoice) },
            ["nested-value"] = new(NodeTypes.BooleanConstant, "1.0.0") { BooleanConstant = new(nestedChoice) },
            ["root"] = new(NodeTypes.BooleanBranch, "1.0.0"),
            ["nested"] = new(NodeTypes.BooleanBranch, "1.0.0"),
            ["output"] = new(NodeTypes.Output, "1.0.0"),
            ["failure"] = new(NodeTypes.Terminate, "1.0.0") { Termination = new(WorkflowTerminationOutcomes.Failure) },
            ["success"] = new(NodeTypes.Terminate, "1.0.0") { Termination = new(WorkflowTerminationOutcomes.Success) }
        }.ToImmutableSortedDictionary(StringComparer.Ordinal),
        new Dictionary<string, ConnectionDefinition>
        {
            ["root-condition"] = new(new("root-value", "out"), new("root", "condition"), "data"),
            ["nested-condition"] = new(new("nested-value", "out"), new("nested", "condition"), "data"),
            ["output-value"] = new(new("input", "out"), new("output", "in"), "data")
        }.ToImmutableSortedDictionary(StringComparer.Ordinal))
    {
        ExecutionConnections = new Dictionary<string, ConnectionDefinition>
        {
            ["activate-root"] = new(new("input", "then"), new("root", "execute"), "execution"),
            ["root-true"] = new(new("root", "true"), new("nested", "execute"), "execution"),
            ["root-false"] = new(new("root", "false"), new("failure", "execute"), "execution"),
            ["nested-true"] = new(new("nested", "true"), new("output", "execute"), "execution"),
            ["nested-false"] = new(new("nested", "false"), new("success", "execute"), "execution")
        }.ToImmutableSortedDictionary(StringComparer.Ordinal)
    };

    private static GraphFunctionDefinition AppendSuffixFunction() => new(
        "text.append-suffix", 1, "Append suffix", "Appends a fixed suffix to text.",
        new Dictionary<string, GraphFunctionPort>
        {
            ["text"] = new("Text", "string")
        }.ToImmutableSortedDictionary(StringComparer.Ordinal),
        new Dictionary<string, GraphFunctionPort>
        {
            ["result"] = new("Result", "string")
        }.ToImmutableSortedDictionary(StringComparer.Ordinal),
        new Dictionary<string, NodeDefinition>
        {
            ["input"] = new(NodeTypes.FunctionInput, "1.0.0") { FunctionPortId = "text" },
            ["suffix"] = new(NodeTypes.StringConstant, "1.0.0") { StringConstant = new("!") },
            ["concat"] = new(NodeTypes.TextConcat, "1.0.0"),
            ["output"] = new(NodeTypes.FunctionOutput, "1.0.0") { FunctionPortId = "result" }
        }.ToImmutableSortedDictionary(StringComparer.Ordinal),
        new Dictionary<string, ConnectionDefinition>
        {
            ["argument"] = new(new("input", "out"), new("concat", "left"), "data"),
            ["suffix"] = new(new("suffix", "out"), new("concat", "right"), "data"),
            ["result"] = new(new("concat", "out"), new("output", "in"), "data")
        }.ToImmutableSortedDictionary(StringComparer.Ordinal));

    private static WorkflowDefinition GraphFunctionCaller(GraphFunctionDefinition function)
    {
        var call = new NodeDefinition(NodeTypes.FunctionCall, "1.0.0")
        {
            GraphFunction = new(function, GraphFunctionJson.ComputeHash(function))
        };
        return new WorkflowDefinition("1.27", "graph-function", "input",
            new Dictionary<string, NodeDefinition>
            {
                ["input"] = new(NodeTypes.Input, "1.0.0"),
                ["constant"] = new(NodeTypes.BooleanConstant, "1.0.0") { BooleanConstant = new(true) },
                ["branch"] = new(NodeTypes.BooleanBranch, "1.0.0"),
                ["call"] = call,
                ["output"] = new(NodeTypes.Output, "1.0.0"),
                ["terminate"] = new(NodeTypes.Terminate, "1.0.0") { Termination = new(WorkflowTerminationOutcomes.Failure) }
            }.ToImmutableSortedDictionary(StringComparer.Ordinal),
            new Dictionary<string, ConnectionDefinition>
            {
                ["condition"] = new(new("constant", "out"), new("branch", "condition"), "data"),
                ["argument"] = new(new("input", "out"), new("call", "text"), "data"),
                ["result"] = new(new("call", "result"), new("output", "in"), "data")
            }.ToImmutableSortedDictionary(StringComparer.Ordinal))
        {
            ExecutionConnections = new Dictionary<string, ConnectionDefinition>
            {
                ["activate"] = new(new("input", "then"), new("branch", "execute"), "execution"),
                ["success"] = new(new("branch", "true"), new("output", "execute"), "execution"),
                ["failure"] = new(new("branch", "false"), new("terminate", "execute"), "execution")
            }.ToImmutableSortedDictionary(StringComparer.Ordinal)
        };
    }
}
