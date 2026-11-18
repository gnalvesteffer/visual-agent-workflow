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
    public void Array_contains_composes_inside_a_pure_graph_function()
    {
        var inputs = ImmutableSortedDictionary<string, GraphFunctionPort>.Empty.WithComparers(StringComparer.Ordinal)
            .Add("items", new("Items", "array<number>"))
            .Add("item", new("Item", "number"));
        var outputs = ImmutableSortedDictionary<string, GraphFunctionPort>.Empty.WithComparers(StringComparer.Ordinal)
            .Add("contains", new("Contains", WorkflowDataTypes.Boolean));
        var nodes = ImmutableSortedDictionary<string, NodeDefinition>.Empty.WithComparers(StringComparer.Ordinal)
            .Add("items", new(NodeTypes.FunctionInput, "1.0.0") { FunctionPortId = "items", OutputDataType = "array<number>" })
            .Add("item", new(NodeTypes.FunctionInput, "1.0.0") { FunctionPortId = "item", OutputDataType = WorkflowDataTypes.Number })
            .Add("contains", new(NodeTypes.ArrayContains, "1.0.0") { ArrayElementType = WorkflowDataTypes.Number })
            .Add("output", new(NodeTypes.FunctionOutput, "1.0.0") { FunctionPortId = "contains", InputDataType = WorkflowDataTypes.Boolean });
        var connections = ImmutableSortedDictionary<string, ConnectionDefinition>.Empty.WithComparers(StringComparer.Ordinal)
            .Add("array", new(new("items", "out"), new("contains", "array"), "data"))
            .Add("item", new(new("item", "out"), new("contains", "item"), "data"))
            .Add("result", new(new("contains", "out"), new("output", "in"), "data"));
        var function = new GraphFunctionDefinition("array.contains", 1, "Array contains", "Checks membership in a primitive array.",
            inputs, outputs, nodes, connections);

        Assert.Empty(GraphFunctionJson.Validate(function, allowNumber: true, allowNumberAdd: false, allowPrimitiveArrays: true));
        var result = GraphFunctionJson.Evaluate(function,
            new Dictionary<string, GraphFunctionEvaluation>
            {
                ["items"] = new("array<number>", ParseArray("[-0,2.5]"), []),
                ["item"] = new(WorkflowDataTypes.Number, 0d, [])
            }, "contains", () => 1);

        Assert.Equal(WorkflowDataTypes.Boolean, result.DataType);
        Assert.Equal(true, result.Value);
        Assert.Contains(result.Values, value => value.NodeId == "contains" && value.DataType == WorkflowDataTypes.Boolean);

        var jsonArrayFunction = function with
        {
            Inputs = inputs.SetItem("items", new("Items", WorkflowDataTypes.JsonArray)),
            Nodes = nodes
                .SetItem("items", nodes["items"] with { OutputDataType = WorkflowDataTypes.JsonArray })
                .SetItem("contains", nodes["contains"] with { ArrayElementType = WorkflowDataTypes.Json })
        };
        Assert.Contains(GraphFunctionJson.Validate(jsonArrayFunction,
                allowNumber: true, allowNumberAdd: false, allowPrimitiveArrays: true, allowJsonArrays: true),
            issue => issue.Code == "function.array.contains.type");
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

    [Fact]
    public void Integer_function_adds_signed_64_bit_values_and_fails_deterministically_on_overflow()
    {
        var function = IncrementIntegerFunction();
        Assert.Empty(GraphFunctionJson.Validate(function));
        var inputs = new Dictionary<string, GraphFunctionEvaluation>
        {
            ["value"] = new("integer", long.MaxValue - 1, [])
        };

        var result = GraphFunctionJson.Evaluate(function, inputs, "result", () => 1);

        Assert.Equal("integer", result.DataType);
        Assert.Equal(long.MaxValue, result.Value);
        Assert.Throws<OverflowException>(() => GraphFunctionJson.Evaluate(function,
            new Dictionary<string, GraphFunctionEvaluation> { ["value"] = new("integer", long.MaxValue, []) },
            "result", () => 1));
    }

    [Fact]
    public void Number_function_preserves_a_finite_double_through_typed_boundaries()
    {
        var function = new GraphFunctionDefinition(
            "number.identity", 1, "Number identity", "Passes a finite number through.",
            ImmutableSortedDictionary<string, GraphFunctionPort>.Empty.WithComparers(StringComparer.Ordinal)
                .Add("value", new("Value", "number")),
            ImmutableSortedDictionary<string, GraphFunctionPort>.Empty.WithComparers(StringComparer.Ordinal)
                .Add("result", new("Result", "number")),
            ImmutableSortedDictionary<string, NodeDefinition>.Empty.WithComparers(StringComparer.Ordinal)
                .Add("input", new(NodeTypes.FunctionInput, "1.0.0") { FunctionPortId = "value", OutputDataType = "number" })
                .Add("output", new(NodeTypes.FunctionOutput, "1.0.0") { FunctionPortId = "result", InputDataType = "number" }),
            ImmutableSortedDictionary<string, ConnectionDefinition>.Empty.WithComparers(StringComparer.Ordinal)
                .Add("result", new(new("input", "out"), new("output", "in"), "data")));

        Assert.Empty(GraphFunctionJson.Validate(function));
        var result = GraphFunctionJson.Evaluate(function,
            new Dictionary<string, GraphFunctionEvaluation> { ["value"] = new("number", 12.5d, []) },
            "result", () => 1);

        Assert.Equal("number", result.DataType);
        Assert.Equal(12.5d, result.Value);
        Assert.Contains(GraphFunctionJson.Validate(function, allowNumber: false), issue => issue.Code is "function.node.unsupported" or "function.port.type");
        Assert.Throws<InvalidOperationException>(() => GraphFunctionJson.Evaluate(function,
            new Dictionary<string, GraphFunctionEvaluation> { ["value"] = new("number", double.NaN, []) },
            "result", () => 1));
        Assert.Contains(GraphFunctionJson.Validate(function with
        {
            Inputs = function.Inputs.SetItem("value", new("Value", "json"))
        }), issue => issue.Code == "function.port.type");
    }

    [Fact]
    public void Number_addition_requires_the_arithmetic_schema_and_preserves_finite_double_results()
    {
        var function = NumberAdditionFunction();

        Assert.Contains(GraphFunctionJson.Validate(function), issue => issue.Code == "function.node.unsupported");
        Assert.Empty(GraphFunctionJson.Validate(function, allowNumber: true, allowNumberAdd: true));
        var result = GraphFunctionJson.Evaluate(function,
            new Dictionary<string, GraphFunctionEvaluation>
            {
                ["left"] = new("number", 2.5d, []),
                ["right"] = new("number", 3.25d, [])
            }, "result", () => 1);

        Assert.Equal(5.75d, result.Value);
        Assert.Throws<InvalidOperationException>(() => GraphFunctionJson.Evaluate(function,
            new Dictionary<string, GraphFunctionEvaluation>
            {
                ["left"] = new("number", double.MaxValue, []),
                ["right"] = new("number", double.MaxValue, [])
            }, "result", () => 1));
    }

    [Fact]
    public void Primitive_array_function_ports_literals_length_and_get_are_typed_and_bounded()
    {
        var function = ArrayGetFunction();
        Assert.Empty(GraphFunctionJson.Validate(function, allowNumber: true, allowNumberAdd: true, allowPrimitiveArrays: true));
        Assert.Contains(GraphFunctionJson.Validate(function), issue => issue.Code == "function.node.unsupported");

        var result = GraphFunctionJson.Evaluate(function,
            new Dictionary<string, GraphFunctionEvaluation> { ["index"] = new("integer", 1L, []) },
            "item", () => 1);

        Assert.Equal("string", result.DataType);
        Assert.Equal("second", result.Value);
        Assert.Contains(result.Values, value => value.NodeId == "items" && value.DataType == "array<string>");
        var length = GraphFunctionJson.Evaluate(function, new Dictionary<string, GraphFunctionEvaluation>
            { ["index"] = new("integer", 0L, []) }, "count", () => 1);
        Assert.Equal("integer", length.DataType);
        Assert.Equal(2L, length.Value);
        Assert.Contains(GraphFunctionJson.Validate(function with
        {
            Outputs = function.Outputs.SetItem("item", new("Item", "array<json>"))
        }, allowNumber: true, allowNumberAdd: true, allowPrimitiveArrays: true), issue => issue.Code == "function.port.type");
        var rangeFailure = Assert.Throws<GraphFunctionEvaluationException>(() => GraphFunctionJson.Evaluate(function,
            new Dictionary<string, GraphFunctionEvaluation> { ["index"] = new("integer", 2L, []) },
            "item", () => 1));
        Assert.Equal("get", rangeFailure.NodeId);
    }

    [Fact]
    public void Primitive_array_function_values_are_validated_and_aggregate_evidence_is_bounded()
    {
        var function = ArrayIdentityFunction();
        var invalidInput = new Dictionary<string, GraphFunctionEvaluation>
        {
            ["items"] = new("array<integer>", ParseArray("[1,\"2\"]"), [])
        };
        Assert.Throws<InvalidOperationException>(() => GraphFunctionJson.Evaluate(function, invalidInput, "result", () => 1));
        var oversizedInput = new Dictionary<string, GraphFunctionEvaluation>
        {
            ["items"] = new("array<integer>", ParseArray("[" + string.Join(',', Enumerable.Repeat("1", WorkflowJsonArrayLimits.MaximumItems + 1)) + "]"), [])
        };
        Assert.Throws<InvalidOperationException>(() => GraphFunctionJson.Evaluate(function, oversizedInput, "result", () => 1));

        var budget = new GraphFunctionEvaluationBudget();
        var largeValue = ParseArray("[\"" + new string('x', 8_000) + "\"]");
        for (var index = 0; index < 8; index++) budget.RecordOutput("array<string>", largeValue);
        Assert.True(budget.RetainedArrayBytes > 0);
        Assert.ThrowsAny<InvalidOperationException>(() => budget.RecordOutput("array<string>", largeValue));
    }

    [Fact]
    public void Pure_function_ports_accept_each_supported_primitive_array_type()
    {
        foreach (var type in new[] { "array<string>", "array<boolean>", "array<integer>", "array<number>" })
        {
            var function = ArrayIdentityFunction(type);
            Assert.Empty(GraphFunctionJson.Validate(function, allowNumber: true, allowNumberAdd: true, allowPrimitiveArrays: true));
            var value = type switch
            {
                "array<string>" => "[\"value\"]",
                "array<boolean>" => "[true]",
                "array<integer>" => "[1]",
                _ => "[1.25]"
            };
            var result = GraphFunctionJson.Evaluate(function,
                new Dictionary<string, GraphFunctionEvaluation> { ["items"] = new(type, ParseArray(value), []) },
                "result", () => 1);
            Assert.Equal(type, result.DataType);
        }

        var emptyArray = GraphFunctionJson.Evaluate(ArrayIdentityFunction("array<number>"),
            new Dictionary<string, GraphFunctionEvaluation> { ["items"] = new("array<number>", ParseArray("[]"), []) },
            "result", () => 1);
        Assert.Equal("[]", ((System.Text.Json.JsonElement)emptyArray.Value).GetRawText());
    }

    [Fact]
    public void Unified_function_ports_and_array_nodes_support_json_values_end_to_end()
    {
        var identity = ArrayIdentityFunction(WorkflowDataTypes.JsonArray);
        Assert.Contains(GraphFunctionJson.Validate(identity, allowNumber: true, allowNumberAdd: true, allowPrimitiveArrays: true),
            issue => issue.Code == "function.port.type");
        Assert.Empty(GraphFunctionJson.Validate(identity, allowNumber: true, allowNumberAdd: true,
            allowPrimitiveArrays: true, allowJsonArrays: true));

        var function = JsonArrayOperationsFunction();
        Assert.Empty(GraphFunctionJson.Validate(function, allowNumber: true, allowNumberAdd: true,
            allowPrimitiveArrays: true, allowJsonArrays: true));

        var inputs = new Dictionary<string, GraphFunctionEvaluation>
        {
            ["items"] = new(WorkflowDataTypes.JsonArray, ParseArray("[null,{\"order\":\"b\",\"n\":1.00},[true]]"), [])
        };
        var result = GraphFunctionJson.Evaluate(function, inputs, "result", () => 1);
        var count = GraphFunctionJson.Evaluate(function, inputs, "count", () => 1);

        Assert.Equal(WorkflowDataTypes.JsonArray, result.DataType);
        Assert.Equal("[{\"order\":\"b\",\"n\":1.00},{\"order\":\"b\",\"n\":1.00},[true],{\"order\":\"b\",\"n\":1.00},false,null]",
            ((System.Text.Json.JsonElement)result.Value).GetRawText());
        Assert.Equal(3L, count.Value);
        Assert.Contains(result.Values, value => value.NodeId == "get" && value.DataType == WorkflowDataTypes.Json);
    }

    [Fact]
    public void Json_array_function_input_is_normalized_validated_and_charged_to_the_invocation_budget()
    {
        var function = ArrayIdentityFunction(WorkflowDataTypes.JsonArray);
        var result = GraphFunctionJson.Evaluate(function,
            new Dictionary<string, GraphFunctionEvaluation>
            {
                ["items"] = new(WorkflowDataTypes.JsonArray, ParseArray(" [ { \"b\" : 1.00, \"a\" : null } ] "), [])
            }, "result", () => 1, new GraphFunctionEvaluationBudget());

        Assert.Equal("[{\"b\":1.00,\"a\":null}]", ((System.Text.Json.JsonElement)result.Value).GetRawText());
        Assert.Throws<InvalidOperationException>(() => GraphFunctionJson.Evaluate(function,
            new Dictionary<string, GraphFunctionEvaluation>
            {
                ["items"] = new(WorkflowDataTypes.JsonArray, ParseArray("[{\"a\":1,\"a\":2}]"), [])
            }, "result", () => 1));
    }

    [Fact]
    public void Unified_workflow_pins_and_packages_json_array_function_snapshots()
    {
        var function = ArrayIdentityFunction(WorkflowDataTypes.JsonArray);
        var definition = UnifiedCaller(function);

        Assert.Empty(WorkflowValidator.Validate(definition));
        var package = WorkflowPackageJson.Create(definition, WorkflowLayout.Create(definition));
        var imported = WorkflowPackageJson.Deserialize(WorkflowPackageJson.Serialize(definition, package.Layout));
        Assert.Equal(GraphFunctionJson.ComputeHash(function), GraphFunctionJson.ComputeHash(
            imported.Definition.Nodes["call"].GraphFunction!.Definition));
        Assert.Empty(WorkflowValidator.Validate(imported.Definition));
    }

    private static GraphFunctionDefinition JsonArrayOperationsFunction()
    {
        var type = WorkflowDataTypes.JsonArray;
        var inputs = ImmutableSortedDictionary<string, GraphFunctionPort>.Empty.WithComparers(StringComparer.Ordinal)
            .Add("items", new("Items", type));
        var outputs = ImmutableSortedDictionary<string, GraphFunctionPort>.Empty.WithComparers(StringComparer.Ordinal)
            .Add("result", new("Result", type)).Add("count", new("Count", "integer"));
        var nodes = ImmutableSortedDictionary<string, NodeDefinition>.Empty.WithComparers(StringComparer.Ordinal)
            .Add("items", new(NodeTypes.FunctionInput, "1.0.0") { FunctionPortId = "items", OutputDataType = type })
            .Add("right", new(NodeTypes.ArrayLiteral, "1.0.0") { ArrayLiteral = new(WorkflowDataTypes.Json, "[false,null]") })
            .Add("length", new(NodeTypes.ArrayLength, "1.0.0") { ArrayElementType = WorkflowDataTypes.Json })
            .Add("get-index", new(NodeTypes.IntegerConstant, "1.0.0") { IntegerConstant = new(1) })
            .Add("insert-index", new(NodeTypes.IntegerConstant, "1.0.0") { IntegerConstant = new(1) })
            .Add("replace-index", new(NodeTypes.IntegerConstant, "1.0.0") { IntegerConstant = new(0) })
            .Add("remove-index", new(NodeTypes.IntegerConstant, "1.0.0") { IntegerConstant = new(1) })
            .Add("get", new(NodeTypes.ArrayGet, "1.0.0") { ArrayElementType = WorkflowDataTypes.Json })
            .Add("append", new(NodeTypes.ArrayAppend, "1.0.0") { ArrayElementType = WorkflowDataTypes.Json })
            .Add("insert", new(NodeTypes.ArrayInsert, "1.0.0") { ArrayElementType = WorkflowDataTypes.Json })
            .Add("replace", new(NodeTypes.ArrayReplace, "1.0.0") { ArrayElementType = WorkflowDataTypes.Json })
            .Add("remove", new(NodeTypes.ArrayRemove, "1.0.0") { ArrayElementType = WorkflowDataTypes.Json })
            .Add("concatenate", new(NodeTypes.ArrayConcatenate, "1.0.0") { ArrayElementType = WorkflowDataTypes.Json })
            .Add("output", new(NodeTypes.FunctionOutput, "1.0.0") { FunctionPortId = "result", InputDataType = type })
            .Add("count-output", new(NodeTypes.FunctionOutput, "1.0.0") { FunctionPortId = "count", InputDataType = "integer" });
        var edges = ImmutableSortedDictionary<string, ConnectionDefinition>.Empty.WithComparers(StringComparer.Ordinal)
            .Add("length-array", new(new("items", "out"), new("length", "in"), "data"))
            .Add("length-result", new(new("length", "out"), new("count-output", "in"), "data"))
            .Add("get-array", new(new("items", "out"), new("get", "array"), "data"))
            .Add("get-index", new(new("get-index", "out"), new("get", "index"), "data"))
            .Add("append-array", new(new("items", "out"), new("append", "array"), "data"))
            .Add("append-item", new(new("get", "item"), new("append", "item"), "data"))
            .Add("insert-array", new(new("append", "out"), new("insert", "array"), "data"))
            .Add("insert-index", new(new("insert-index", "out"), new("insert", "index"), "data"))
            .Add("insert-item", new(new("get", "item"), new("insert", "item"), "data"))
            .Add("replace-array", new(new("insert", "out"), new("replace", "array"), "data"))
            .Add("replace-index", new(new("replace-index", "out"), new("replace", "index"), "data"))
            .Add("replace-item", new(new("get", "item"), new("replace", "item"), "data"))
            .Add("remove-array", new(new("replace", "out"), new("remove", "array"), "data"))
            .Add("remove-index", new(new("remove-index", "out"), new("remove", "index"), "data"))
            .Add("concat-left", new(new("remove", "out"), new("concatenate", "left"), "data"))
            .Add("concat-right", new(new("right", "out"), new("concatenate", "right"), "data"))
            .Add("result", new(new("concatenate", "out"), new("output", "in"), "data"));
        return new("array.json-operations", 1, "JSON array operations", "Exercises bounded JSON array operations.",
            inputs, outputs, nodes, edges);
    }

    private static WorkflowDefinition UnifiedCaller(GraphFunctionDefinition function)
    {
        var call = new NodeDefinition(NodeTypes.FunctionCall, "1.0.0")
        {
            GraphFunction = new(function, GraphFunctionJson.ComputeHash(function))
        };
        var definition = new WorkflowDefinition(WorkflowSchemaVersions.UnifiedWorkflowGraph, "array-json-function", "input",
            ImmutableSortedDictionary<string, NodeDefinition>.Empty.WithComparers(StringComparer.Ordinal)
                .Add("input", new(NodeTypes.Input, "1.0.0"))
                .Add("value", new(NodeTypes.WorkflowValue, "1.0.0") { ParameterId = "items", OutputDataType = WorkflowDataTypes.JsonArray })
                .Add("condition", new(NodeTypes.BooleanConstant, "1.0.0") { BooleanConstant = new(true) })
                .Add("branch", new(NodeTypes.BooleanBranch, "1.0.0"))
                .Add("call", call)
                .Add("output", new(NodeTypes.Output, "1.0.0") { OutputDataType = WorkflowDataTypes.JsonArray })
                .Add("failure", new(NodeTypes.Terminate, "1.0.0") { Termination = new(WorkflowTerminationOutcomes.Failure) }),
            ImmutableSortedDictionary<string, ConnectionDefinition>.Empty.WithComparers(StringComparer.Ordinal)
                .Add("condition", new(new("condition", "out"), new("branch", "condition"), "data"))
                .Add("value", new(new("value", "out"), new("call", "items"), "data"))
                .Add("result", new(new("call", "result"), new("output", "in"), "data")))
        {
            Parameters = ImmutableSortedDictionary<string, WorkflowParameterDefinition>.Empty.WithComparers(StringComparer.Ordinal)
                .Add("items", new("Items", "[null,{\"id\":1.00}]") { Type = WorkflowDataTypes.JsonArray }),
            ExecutionConnections = ImmutableSortedDictionary<string, ConnectionDefinition>.Empty.WithComparers(StringComparer.Ordinal)
                .Add("activate", new(new("input", "then"), new("branch", "execute"), "execution"))
                .Add("success", new(new("branch", "true"), new("output", "execute"), "execution"))
                .Add("failure", new(new("branch", "false"), new("failure", "execute"), "execution"))
        };
        return definition;
    }

    private static GraphFunctionDefinition ArrayGetFunction() => new(
        "array.get-second", 1, "Array get", "Returns a selected array item.",
        ImmutableSortedDictionary<string, GraphFunctionPort>.Empty.WithComparers(StringComparer.Ordinal)
            .Add("index", new("Index", "integer")),
        ImmutableSortedDictionary<string, GraphFunctionPort>.Empty.WithComparers(StringComparer.Ordinal)
            .Add("item", new("Item", "string"))
            .Add("count", new("Count", "integer")),
        ImmutableSortedDictionary<string, NodeDefinition>.Empty.WithComparers(StringComparer.Ordinal)
            .Add("index", new(NodeTypes.FunctionInput, "1.0.0") { FunctionPortId = "index", OutputDataType = "integer" })
            .Add("items", new(NodeTypes.ArrayLiteral, "1.0.0") { ArrayLiteral = new("string", "[\"first\",\"second\"]") })
            .Add("length", new(NodeTypes.ArrayLength, "1.0.0") { ArrayElementType = "string" })
            .Add("get", new(NodeTypes.ArrayGet, "1.0.0") { ArrayElementType = "string" })
            .Add("output", new(NodeTypes.FunctionOutput, "1.0.0") { FunctionPortId = "item", InputDataType = "string" })
            .Add("count-output", new(NodeTypes.FunctionOutput, "1.0.0") { FunctionPortId = "count", InputDataType = "integer" }),
        ImmutableSortedDictionary<string, ConnectionDefinition>.Empty.WithComparers(StringComparer.Ordinal)
            .Add("array", new(new("items", "out"), new("get", "array"), "data"))
            .Add("length-array", new(new("items", "out"), new("length", "in"), "data"))
            .Add("index", new(new("index", "out"), new("get", "index"), "data"))
            .Add("result", new(new("get", "item"), new("output", "in"), "data"))
            .Add("count", new(new("length", "out"), new("count-output", "in"), "data")));

    private static GraphFunctionDefinition ArrayIdentityFunction(string type = "array<integer>") => new(
        "array.identity", 1, "Array identity", "Returns the supplied array.",
        ImmutableSortedDictionary<string, GraphFunctionPort>.Empty.WithComparers(StringComparer.Ordinal)
            .Add("items", new("Items", type)),
        ImmutableSortedDictionary<string, GraphFunctionPort>.Empty.WithComparers(StringComparer.Ordinal)
            .Add("result", new("Result", type)),
        ImmutableSortedDictionary<string, NodeDefinition>.Empty.WithComparers(StringComparer.Ordinal)
            .Add("input", new(NodeTypes.FunctionInput, "1.0.0") { FunctionPortId = "items", OutputDataType = type })
            .Add("output", new(NodeTypes.FunctionOutput, "1.0.0") { FunctionPortId = "result", InputDataType = type }),
        ImmutableSortedDictionary<string, ConnectionDefinition>.Empty.WithComparers(StringComparer.Ordinal)
            .Add("result", new(new("input", "out"), new("output", "in"), "data")));

    private static System.Text.Json.JsonElement ParseArray(string json)
    {
        using var document = System.Text.Json.JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static GraphFunctionDefinition IncrementIntegerFunction() => new(
        "integer.increment", 1, "Increment integer", "Adds one to a signed 64-bit integer.",
        ImmutableSortedDictionary<string, GraphFunctionPort>.Empty.WithComparers(StringComparer.Ordinal)
            .Add("value", new("Value", "integer")),
        ImmutableSortedDictionary<string, GraphFunctionPort>.Empty.WithComparers(StringComparer.Ordinal)
            .Add("result", new("Result", "integer")),
        ImmutableSortedDictionary<string, NodeDefinition>.Empty.WithComparers(StringComparer.Ordinal)
            .Add("input", new(NodeTypes.FunctionInput, "1.0.0") { FunctionPortId = "value", OutputDataType = "integer" })
            .Add("one", new(NodeTypes.IntegerConstant, "1.0.0") { IntegerConstant = new(1) })
            .Add("sum", new(NodeTypes.IntegerAdd, "1.0.0"))
            .Add("output", new(NodeTypes.FunctionOutput, "1.0.0") { FunctionPortId = "result", InputDataType = "integer" }),
        ImmutableSortedDictionary<string, ConnectionDefinition>.Empty.WithComparers(StringComparer.Ordinal)
            .Add("left", new(new("input", "out"), new("sum", "left"), "data"))
            .Add("right", new(new("one", "out"), new("sum", "right"), "data"))
            .Add("result", new(new("sum", "out"), new("output", "in"), "data")));

    private static GraphFunctionDefinition NumberAdditionFunction() => new(
        "number.add", 1, "Add numbers", "Adds two finite numbers.",
        ImmutableSortedDictionary<string, GraphFunctionPort>.Empty.WithComparers(StringComparer.Ordinal)
            .Add("left", new("Left", "number"))
            .Add("right", new("Right", "number")),
        ImmutableSortedDictionary<string, GraphFunctionPort>.Empty.WithComparers(StringComparer.Ordinal)
            .Add("result", new("Result", "number")),
        ImmutableSortedDictionary<string, NodeDefinition>.Empty.WithComparers(StringComparer.Ordinal)
            .Add("left", new(NodeTypes.FunctionInput, "1.0.0") { FunctionPortId = "left", OutputDataType = "number" })
            .Add("right", new(NodeTypes.FunctionInput, "1.0.0") { FunctionPortId = "right", OutputDataType = "number" })
            .Add("sum", new(NodeTypes.NumberAdd, "1.0.0"))
            .Add("output", new(NodeTypes.FunctionOutput, "1.0.0") { FunctionPortId = "result", InputDataType = "number" }),
        ImmutableSortedDictionary<string, ConnectionDefinition>.Empty.WithComparers(StringComparer.Ordinal)
            .Add("left", new(new("left", "out"), new("sum", "left"), "data"))
            .Add("right", new(new("right", "out"), new("sum", "right"), "data"))
            .Add("result", new(new("sum", "out"), new("output", "in"), "data")));
}
