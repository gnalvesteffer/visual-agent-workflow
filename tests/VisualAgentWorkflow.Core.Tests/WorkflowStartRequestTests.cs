using System.Collections.Immutable;
using System.Text.Json;
using VisualAgentWorkflow.Core;
using Xunit;

namespace VisualAgentWorkflow.Core.Tests;

public sealed class WorkflowStartRequestTests
{
    private static WorkflowDefinition Workflow(string id, string inputType) =>
        new(WorkflowSchemaVersions.CurrentAuthoringSchema, id, "input",
            new Dictionary<string, NodeDefinition>
            {
                ["input"] = new(NodeTypes.Input, "1.0.0") { InputDataType = inputType == WorkflowDataTypes.Json ? inputType : null },
                ["output"] = new(NodeTypes.Output, "1.0.0") { OutputDataType = inputType == WorkflowDataTypes.Json ? inputType : null }
            }.ToImmutableSortedDictionary(StringComparer.Ordinal),
            new Dictionary<string, ConnectionDefinition>
            {
                ["input-output"] = new(new("input", "out"), new("output", "in"), "data")
            }.ToImmutableSortedDictionary(StringComparer.Ordinal))
        {
            ExecutionConnections = new Dictionary<string, ConnectionDefinition>
            {
                ["input-output"] = new(new("input", "then"), new("output", "execute"), "execution")
            }.ToImmutableSortedDictionary(StringComparer.Ordinal)
        };

    internal static WorkflowInterfaceImplementationRevision Implementation()
    {
        var contract = WorkflowInterfaceRevision.Create(new WorkflowInterfaceDefinition("sample.api", "Sample", "Proof.",
            ImmutableSortedDictionary<string, WorkflowInterfaceOperation>.Empty
                .Add("echo", new("Echo", WorkflowDataTypes.Json, WorkflowDataTypes.Json)
                {
                    Inputs = [new("text", "Text", WorkflowDataTypes.String)],
                    Outputs = [new("text", "Text", WorkflowDataTypes.String)]
                })
                .Add("order", new("Order", WorkflowDataTypes.Json, WorkflowDataTypes.Json)
                {
                    Inputs =
                    [
                        new("sku", "Sku", WorkflowDataTypes.String), new("count", "Count", WorkflowDataTypes.Integer),
                        new("price", "Price", WorkflowDataTypes.Number), new("gift", "Gift", WorkflowDataTypes.Boolean),
                        new("meta", "Meta", WorkflowDataTypes.Json)
                    ],
                    Outputs = [new("ok", "Ok", WorkflowDataTypes.Boolean)]
                }))
        { SchemaVersion = WorkflowInterfaceJson.FieldContractSchemaVersion });
        var echo = WorkflowInterfaceWorkflowPin.From(Workflow("echo-flow", WorkflowDataTypes.Json));
        var order = WorkflowInterfaceWorkflowPin.From(Workflow("order-flow", WorkflowDataTypes.Json));
        return WorkflowInterfaceImplementationRevision.Create(new WorkflowInterfaceImplementation("sample.impl", contract,
            ImmutableSortedDictionary<string, WorkflowInterfaceWorkflowPin>.Empty.Add("echo", echo).Add("order", order)));
    }

    private static WorkflowStartRequest Request(WorkflowInterfaceImplementationRevision impl, string op, string fields) =>
        new(op, impl.Definition.Interface.Definition.InterfaceId, impl.Definition.Interface.RevisionId, fields);

    private const string ValidOrder = """{"meta":{"b":[1,2]},"gift":true,"price":2.5,"count":3,"sku":"A-1"}""";

    private static string[] Codes(WorkflowStartRequestResult result) => result.Errors.Select(e => e.Code).ToArray();

    [Fact]
    public void ValidMultiFieldRequestMapsToDeclaredOrderJsonThatNormalizes()
    {
        var impl = Implementation();
        var result = WorkflowStartRequestValidator.Validate(Request(impl, "order", ValidOrder), impl);
        Assert.True(result.Succeeded);
        Assert.Equal("""{"sku":"A-1","count":3,"price":2.5,"gift":true,"meta":{"b":[1,2]}}""", result.EntryInput);
        Assert.Equal("order", result.Operation!.OperationId);
        Assert.Equal("order-flow", result.Operation.Workflow.WorkflowId);
        // Valid for a JSON Input node: parses within the runner's depth bound and round-trips unchanged.
        using var doc = JsonDocument.Parse(result.EntryInput!, new JsonDocumentOptions { MaxDepth = 32 });
        Assert.Equal(JsonValueKind.Object, doc.RootElement.ValueKind);
        Assert.True(result.EntryInput!.Length <= 16_384);
    }

    [Fact]
    public void MappingIsDeterministicAcrossFieldOrderAndNumberSpelling()
    {
        var impl = Implementation();
        var a = WorkflowStartRequestValidator.Validate(Request(impl, "order", ValidOrder), impl);
        var b = WorkflowStartRequestValidator.Validate(Request(impl, "order",
            """{"sku":"A-1","count":3,"price":2.50,"gift":true,"meta":{"b":[1,2]}}"""), impl);
        Assert.Equal(a.EntryInput, b.EntryInput);
    }

    [Theory]
    [InlineData("hello")]
    [InlineData("abc 🌍 \"quoted\" \n line")]
    [InlineData("{\"looks\":\"json\"}")]
    [InlineData("")]
    public void OneStringFieldOperationMapsToExactlyTheTextTodaysStartReceives(string text)
    {
        var impl = Implementation();
        var fields = JsonSerializer.Serialize(new Dictionary<string, string> { ["text"] = text });
        var result = WorkflowStartRequestValidator.Validate(Request(impl, "echo", fields), impl);
        Assert.True(result.Succeeded);
        // Golden: the text a caller passes to ExecuteAsync/StartRunAsync for this one-field JSON object today.
        Assert.Equal(fields, result.EntryInput);
        using var document = JsonDocument.Parse(result.EntryInput!, new JsonDocumentOptions { MaxDepth = 32 });
        Assert.Equal(text, document.RootElement.GetProperty("text").GetString());
    }

    [Fact]
    public void TwoOperationsOfOneImplementationResolveIndependently()
    {
        var impl = Implementation();
        var echo = WorkflowStartRequestValidator.Validate(Request(impl, "echo", """{"text":"x"}"""), impl);
        var order = WorkflowStartRequestValidator.Validate(Request(impl, "order", ValidOrder), impl);
        Assert.Equal("echo-flow", echo.Operation!.Workflow.WorkflowId);
        Assert.Equal("order-flow", order.Operation!.Workflow.WorkflowId);
    }

    [Theory]
    [InlineData("""{}""", "missing_field")]
    [InlineData("""{"text":"x","other":1}""", "unknown_field")]
    [InlineData("""{"text":1}""", "wrong_type")]
    [InlineData("""{"text":null}""", "wrong_type")]
    [InlineData("""{"text":"a","text":"b"}""", "duplicate_field")]
    [InlineData("""[]""", "invalid_request")]
    [InlineData("""not json""", "invalid_request")]
    [InlineData("", "invalid_request")]
    public void EchoRejections(string fields, string code)
    {
        var impl = Implementation();
        var result = WorkflowStartRequestValidator.Validate(Request(impl, "echo", fields), impl);
        Assert.False(result.Succeeded);
        Assert.Null(result.EntryInput);
        Assert.Contains(code, Codes(result));
    }

    [Theory]
    [InlineData("""{"sku":"a","count":1.5,"price":1,"gift":false,"meta":1}""", "wrong_type")]
    [InlineData("""{"sku":"a","count":"1","price":1,"gift":false,"meta":1}""", "wrong_type")]
    [InlineData("""{"sku":"a","count":1,"price":"1","gift":false,"meta":1}""", "wrong_type")]
    [InlineData("""{"sku":"a","count":1,"price":1e999,"gift":false,"meta":1}""", "wrong_type")]
    [InlineData("""{"sku":"a","count":9223372036854775808,"price":1,"gift":false,"meta":1}""", "wrong_type")]
    [InlineData("""{"sku":"a","count":1,"price":1,"gift":"true","meta":1}""", "wrong_type")]
    [InlineData("""{"sku":"a","count":1,"price":1,"gift":false,"meta":null}""", "wrong_type")]
    [InlineData("""{"sku":"a","count":1,"price":1,"gift":false}""", "missing_field")]
    public void OrderRejectsWrongTypesAndMissingFields(string fields, string code)
    {
        var impl = Implementation();
        var result = WorkflowStartRequestValidator.Validate(Request(impl, "order", fields), impl);
        Assert.False(result.Succeeded);
        Assert.Contains(code, Codes(result));
    }

    [Fact]
    public void ReportsEveryProblemWithFieldPaths()
    {
        var impl = Implementation();
        var result = WorkflowStartRequestValidator.Validate(Request(impl, "order", """{"sku":1,"zzz":1}"""), impl);
        Assert.Contains(result.Errors, e => e.Code == "wrong_type" && e.Path == "fields.sku");
        Assert.Contains(result.Errors, e => e.Code == "unknown_field" && e.Path == "fields.zzz");
        Assert.Contains(result.Errors, e => e.Code == "missing_field" && e.Path == "fields.count");
    }

    [Fact]
    public void OversizeFieldsAndOversizeMappedInputAreRejected()
    {
        var impl = Implementation();
        var big = new string('a', 16_385);
        var huge = WorkflowStartRequestValidator.Validate(Request(impl, "echo", $$"""{"text":"{{big}}"}"""), impl);
        Assert.Contains("too_large", Codes(huge));
        // Each part fits but JSON escaping of the mapped object exceeds the entry bound.
        var quotes = new string('"', 5_000);
        var escaped = JsonSerializer.Serialize(new Dictionary<string, object>
        { ["sku"] = quotes, ["count"] = 1, ["price"] = 1, ["gift"] = true, ["meta"] = 1 });
        var mapped = WorkflowStartRequestValidator.Validate(Request(impl, "order", escaped), impl);
        Assert.Contains("too_large", Codes(mapped));
        Assert.Null(mapped.EntryInput);
    }

    [Fact]
    public void TooDeepJsonIsRejectedWithoutThrowing()
    {
        var impl = Implementation();
        var deep = new string('[', 40) + new string(']', 40);
        var result = WorkflowStartRequestValidator.Validate(Request(impl, "order",
            $$"""{"sku":"a","count":1,"price":1,"gift":true,"meta":{{deep}}}"""), impl);
        Assert.False(result.Succeeded);
        Assert.Contains("invalid_request", Codes(result));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("../../etc/passwd")]
    [InlineData("order\u0000")]
    [InlineData("ördér")]
    public void HostileOrUnknownOperationIdsFailClosedWithoutEchoing(string operationId)
    {
        var impl = Implementation();
        var result = WorkflowStartRequestValidator.Validate(Request(impl, operationId, """{"text":"x"}"""), impl);
        Assert.False(result.Succeeded);
        Assert.Contains("unknown_operation", Codes(result));
        Assert.DoesNotContain(result.Errors, e => e.Message.Contains(operationId.Trim()) && operationId.Trim().Length > 0);
    }

    [Fact]
    public void OverlongOperationIdIsRejected()
    {
        var impl = Implementation();
        var result = WorkflowStartRequestValidator.Validate(Request(impl, new string('a', 5_000), "{}"), impl);
        Assert.Contains("unknown_operation", Codes(result));
        Assert.All(result.Errors, e => Assert.True(e.Message.Length < 300 && e.Path.Length < 100));
    }

    [Fact]
    public void StaleOrWrongPinsAreRejected()
    {
        var impl = Implementation();
        var wrongInterface = new WorkflowStartRequest("echo", "other.api", impl.Definition.Interface.RevisionId, """{"text":"x"}""");
        Assert.Contains("unknown_interface", Codes(WorkflowStartRequestValidator.Validate(wrongInterface, impl)));
        var staleRevision = new WorkflowStartRequest("echo", "sample.api", Guid.NewGuid(), """{"text":"x"}""");
        Assert.Contains("stale_interface_revision", Codes(WorkflowStartRequestValidator.Validate(staleRevision, impl)));
        var staleImpl = Request(impl, "echo", """{"text":"x"}""") with { ImplementationRevisionId = Guid.NewGuid() };
        Assert.Contains("stale_implementation_revision", Codes(WorkflowStartRequestValidator.Validate(staleImpl, impl)));
        var pinned = Request(impl, "echo", """{"text":"x"}""") with { ImplementationRevisionId = impl.RevisionId };
        Assert.True(WorkflowStartRequestValidator.Validate(pinned, impl).Succeeded);
    }

    [Fact]
    public void TamperedImplementationFailsClosedWithoutThrowing()
    {
        var impl = Implementation() with { ContentHash = new string('0', 64) };
        var result = WorkflowStartRequestValidator.Validate(
            new("echo", "sample.api", impl.Definition.Interface.RevisionId, """{"text":"x"}"""), impl);
        Assert.False(result.Succeeded);
        Assert.Contains("invalid_implementation", Codes(result));
    }

    [Fact]
    public void OperationWithoutAFieldContractIsRejected()
    {
        var contract = WorkflowInterfaceRevision.Create(new WorkflowInterfaceDefinition("legacy.api", "L", "d",
            ImmutableSortedDictionary<string, WorkflowInterfaceOperation>.Empty.Add("go",
                new("Go", WorkflowDataTypes.String, WorkflowDataTypes.String))));
        var impl = WorkflowInterfaceImplementationRevision.Create(new WorkflowInterfaceImplementation("legacy.impl", contract,
            ImmutableSortedDictionary<string, WorkflowInterfaceWorkflowPin>.Empty.Add("go",
                WorkflowInterfaceWorkflowPin.From(Workflow("legacy-flow", WorkflowDataTypes.String)))));
        var result = WorkflowStartRequestValidator.Validate(new("go", "legacy.api", contract.RevisionId, "{}"), impl);
        Assert.Contains("no_field_contract", Codes(result));
    }
}
