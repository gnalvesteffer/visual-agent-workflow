using System.Collections.Immutable;
using VisualAgentWorkflow.Core;
using Xunit;

namespace VisualAgentWorkflow.Core.Tests;

public sealed class GraphFunctionTests
{
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

    private static WorkflowDefinition Caller(GraphFunctionDefinition function, string? contentHash = null)
    {
        var call = new NodeDefinition(NodeTypes.FunctionCall, "1.0.0")
        {
            GraphFunction = new(function, contentHash ?? GraphFunctionJson.ComputeHash(function))
        };
        return new WorkflowDefinition("1.27", "function-caller", "input",
            new Dictionary<string, NodeDefinition>
            {
                ["input"] = new(NodeTypes.Input, "1.0.0"),
                ["condition"] = new(NodeTypes.BooleanConstant, "1.0.0") { BooleanConstant = new(true) },
                ["branch"] = new(NodeTypes.BooleanBranch, "1.0.0"),
                ["call"] = call,
                ["output"] = new(NodeTypes.Output, "1.0.0"),
                ["failure"] = new(NodeTypes.Terminate, "1.0.0") { Termination = new(WorkflowTerminationOutcomes.Failure) }
            }.ToImmutableSortedDictionary(StringComparer.Ordinal),
            new Dictionary<string, ConnectionDefinition>
            {
                ["condition"] = new(new("condition", "out"), new("branch", "condition"), "data"),
                ["argument"] = new(new("input", "out"), new("call", "text"), "data"),
                ["result"] = new(new("call", "result"), new("output", "in"), "data")
            }.ToImmutableSortedDictionary(StringComparer.Ordinal))
        {
            ExecutionConnections = new Dictionary<string, ConnectionDefinition>
            {
                ["activate"] = new(new("input", "then"), new("branch", "execute"), "execution"),
                ["success"] = new(new("branch", "true"), new("output", "execute"), "execution"),
                ["failure"] = new(new("branch", "false"), new("failure", "execute"), "execution")
            }.ToImmutableSortedDictionary(StringComparer.Ordinal)
        };
    }

    [Fact]
    public void Pure_function_contract_is_canonical_and_round_trips_in_a_workflow_package()
    {
        var function = AppendSuffixFunction();
        var definition = Caller(function);

        Assert.Empty(GraphFunctionJson.Validate(function));
        Assert.Empty(WorkflowValidator.Validate(definition));
        var roundTrippedDefinition = WorkflowJson.Deserialize(WorkflowJson.Serialize(definition));
        Assert.Equal(WorkflowJson.ComputeHash(definition), WorkflowJson.ComputeHash(roundTrippedDefinition));
        Assert.Equal(GraphFunctionJson.ComputeHash(function), GraphFunctionJson.ComputeHash(
            roundTrippedDefinition.Nodes["call"].GraphFunction!.Definition));

        var package = WorkflowPackageJson.Create(definition, WorkflowLayout.Create(definition));
        Assert.Contains(package.Requirements, requirement => requirement.Kind == "graph-function" &&
            requirement.Id == function.FunctionId && requirement.Version == "1" &&
            requirement.ContractHash == GraphFunctionJson.ComputeHash(function));
        var importedPackage = WorkflowPackageJson.Deserialize(WorkflowPackageJson.Serialize(definition, package.Layout));
        Assert.Contains(importedPackage.Requirements, requirement => requirement.Kind == "graph-function" &&
            requirement.Id == function.FunctionId && requirement.ContractHash == GraphFunctionJson.ComputeHash(function));
    }

    [Fact]
    public void Function_hash_mismatch_and_unsupported_effect_nodes_are_rejected()
    {
        var function = AppendSuffixFunction();
        var mismatch = Caller(function, new string('0', 64));
        var effectfulFunction = function with
        {
            Nodes = function.Nodes.SetItem("agent", new(NodeTypes.Agent, "1.0.0"))
        };

        Assert.Contains(WorkflowValidator.Validate(mismatch), issue => issue.Code == "graphFunction.configuration");
        Assert.Contains(GraphFunctionJson.Validate(effectfulFunction), issue => issue.Code == "function.node.unsupported");
    }

    [Fact]
    public void Function_bodies_reject_cycles_and_nonportable_value_types()
    {
        var function = AppendSuffixFunction();
        var cyclic = function with
        {
            Connections = function.Connections.Add("cycle", new(new("concat", "out"), new("concat", "left"), "data"))
        };
        var unsupportedType = function with
        {
            Inputs = function.Inputs.SetItem("value", new("Value", "json"))
                .Remove("text"),
            Nodes = function.Nodes.SetItem("input", function.Nodes["input"] with { FunctionPortId = "value" })
        };

        Assert.Contains(GraphFunctionJson.Validate(cyclic), issue => issue.Code == "function.cycle");
        Assert.Contains(GraphFunctionJson.Validate(unsupportedType), issue => issue.Code == "function.port.type");
    }

    [Fact]
    public void Function_boundary_sockets_keep_the_declared_data_type()
    {
        var function = AppendSuffixFunction();
        var mismatched = function with
        {
            Nodes = function.Nodes.SetItem("input", function.Nodes["input"] with { OutputDataType = "boolean" })
        };
        var inputNode = function.Nodes["input"] with { OutputDataType = "boolean" };

        Assert.Contains(GraphFunctionJson.Validate(mismatched), issue => issue.Code == "function.port.type");
        Assert.Contains(WorkflowPorts.Explicit(inputNode), port => port.Id == "out" && port.DataType == "boolean");
    }
}
