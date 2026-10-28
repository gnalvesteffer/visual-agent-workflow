using System.Runtime.InteropServices;
using System.Text.Json;
using VisualAgentWorkflow.Blazor;
using VisualAgentWorkflow.Core;
using Xunit;

namespace VisualAgentWorkflow.Mcp.Tests;

[Collection(McpStdioCollection.Name)]
public class BrokerTests
{
    internal static McpToolBroker CreateBroker(IEnumerable<McpToolRegistration>? registrations = null,
        IToolAuthorizationPolicy? policy = null, TimeSpan? timeout = null) =>
        new(registrations ?? Registrations(), policy ?? new DemoToolPolicy(), timeout ?? TimeSpan.FromSeconds(30));

    internal static IReadOnlyList<McpToolRegistration> Registrations()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "VisualAgentWorkflow.slnx"))) root = root.Parent;
        if (root is null) throw new InvalidOperationException("Cannot locate fixture host.");
        var configuration = Directory.GetParent(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar))!.Name;
        var studio = Path.Combine(root.FullName, "src", "VisualAgentWorkflow.Studio", "bin", configuration, "net10.0", "VisualAgentWorkflow.Studio.dll");
        var runtimeRoot = Directory.GetParent(RuntimeEnvironment.GetRuntimeDirectory().TrimEnd(Path.DirectorySeparatorChar))!.Parent!.Parent!.FullName;
        return DemoMcpServer.Registrations(Path.Combine(runtimeRoot, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet"), [studio, "--mcp-demo-server"]);
    }

    private static WorkflowRevision Revision(ToolBindingDescriptor binding)
    {
        var definition = WorkflowTemplates.Create("uppercase");
        definition = WorkflowEditing.ReplaceNode(definition, "step-1", new(NodeTypes.McpTool, "1.0.0",
            new ToolBindingRequest(binding.BindingId, binding.ContractHash) { DescriptionHash = binding.DescriptionHash }));
        return WorkflowRevision.Create(definition, "test");
    }

    private static NodeInvocationContext Invocation(WorkflowRevision revision, List<string> kinds) =>
        new(Guid.NewGuid(), revision, "step-1", Guid.NewGuid(), (kind, _) => { kinds.Add(kind); return ValueTask.CompletedTask; });

    [Fact]
    public async Task ActualStdioDiscoveryInvocationAndRecordingWork()
    {
        var broker = CreateBroker(policy: new DemoToolPolicy());
        var bindings = await broker.ListBindingsAsync();
        Assert.Equal(6, bindings.Count);
        var reverse = bindings.Single(b => b.BindingId == "demo.reverse");
        Assert.Contains("Reverses Unicode", reverse.ToolDescription);
        Assert.Equal(McpToolDescription.ComputeHash(reverse.ToolDescription), reverse.DescriptionHash);
        var events = new List<string>();
        await using var scope = broker.OpenScope(Invocation(Revision(reverse), events));
        Assert.Single(await scope.ListToolsAsync());
        Assert.Equal("🌍 olleh", await scope.InvokeAsync("demo.reverse", "hello 🌍"));
        Assert.Equal(new[] { ExecutionEventKinds.ToolRequested, ExecutionEventKinds.ToolAuthorized, ExecutionEventKinds.ToolCompleted }, events);

        var submitAnswer = bindings.Single(b => b.BindingId == "demo.submit-answer");
        await using var answerScope = broker.OpenScope(Invocation(Revision(submitAnswer), []));
        Assert.Equal("2", await answerScope.InvokeAsync(submitAnswer.BindingId, "2"));
        await Assert.ThrowsAsync<ToolInvocationException>(() => answerScope.InvokeAsync(submitAnswer.BindingId, "1 + 1 = 2"));

        var structured = bindings.Single(binding => binding.BindingId == "demo.inspect-payload");
        Assert.Contains("payload.request.name: string required", structured.InputSummary);
        Assert.Contains("payload.labels: array<string> required", structured.InputSummary);
        await using var structuredScope = broker.OpenScope(Invocation(Revision(structured), []));
        using var valid = JsonDocument.Parse("""{"payload":{"request":{"name":"Ada","count":3},"labels":["blue","green"]}}""");
        Assert.Equal("Ada:3:blue,green", await structuredScope.InvokeJsonAsync(structured.BindingId, valid.RootElement));

        using var invalid = JsonDocument.Parse("""{"payload":{"request":{"name":"Ada","count":"three"},"labels":["blue"]}}""");
        await Assert.ThrowsAsync<ToolInvocationException>(() => structuredScope.InvokeJsonAsync(structured.BindingId, invalid.RootElement));
    }

    [Fact]
    public async Task ListingManyToolsUsesOneConnectionAndOneToolDiscoveryPerServer()
    {
        var connectionCount = 0;
        var original = Registrations();
        var originalConnect = original[0].Connect;
        Assert.All(original, registration => Assert.Equal(originalConnect, registration.Connect));
        Func<CancellationToken, Task<ModelContextProtocol.Client.McpClient>> connect = async cancellationToken =>
        {
            Interlocked.Increment(ref connectionCount);
            return await originalConnect(cancellationToken);
        };
        var registrations = original.Select(registration => registration with { Connect = connect });

        var bindings = await CreateBroker(registrations).ListBindingsAsync();

        Assert.Equal(6, bindings.Count);
        Assert.Equal(1, connectionCount);
    }

    [Fact]
    public void NestedArraySchemasNormalizeAndRejectUnsupportedOrUnboundedShapes()
    {
        using var schema = JsonDocument.Parse("""
            {"type":"object","properties":{"request":{"type":"object","properties":{"name":{"type":"string","minLength":1,"maxLength":48},"count":{"type":"integer","minimum":0,"maximum":10}},"required":["name","count"]},"labels":{"type":"array","items":{"type":"string","enum":["blue","green"]},"minItems":1,"maxItems":4}},"required":["request","labels"],"additionalProperties":false}
            """);

        Assert.True(McpToolInputSchemas.IsSupported(schema.RootElement));
        var summary = McpToolInputSchemas.Summarize(schema.RootElement);
        Assert.Contains("request.name: string required", summary);
        Assert.Contains("labels: array<string> required", summary);

        var normalized = McpToolInputSchemas.Normalize(schema.RootElement);
        Assert.Equal(10, normalized.GetProperty("properties").GetProperty("request").GetProperty("properties")
            .GetProperty("count").GetProperty("maximum").GetInt32());
        using var validArguments = JsonDocument.Parse("""{"request":{"name":"Ada","count":3},"labels":["blue","green"]}""");
        var converted = McpToolInputSchemas.ToCallArguments(schema.RootElement, validArguments.RootElement);
        var request = Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(converted["request"]);
        Assert.Equal(3L, request["count"]);
        Assert.Equal(new object?[] { "blue", "green" }, Assert.IsAssignableFrom<object?[]>(converted["labels"]));

        var invalidInputs = new[]
        {
            """{"request":{"name":"","count":3},"labels":["blue"]}""",
            """{"request":{"name":"Ada","count":11},"labels":["blue"]}""",
            """{"request":{"name":"Ada","count":3},"labels":["red"]}""",
            """{"request":{"name":"Ada","count":3},"labels":["blue","blue","blue","blue","blue"]}""",
            """{"request":{"name":"Ada","count":3,"extra":true},"labels":["blue"]}"""
        };
        foreach (var invalidInput in invalidInputs)
        {
            using var arguments = JsonDocument.Parse(invalidInput);
            Assert.Throws<ToolInvocationException>(() => McpToolInputSchemas.ToCallArguments(schema.RootElement, arguments.RootElement));
        }
        Assert.False(McpToolInputSchemas.IsSupported(JsonDocument.Parse("""{"type":"object","properties":{"value":{"type":"string","format":"date-time"}}}""").RootElement));
        Assert.False(McpToolInputSchemas.IsSupported(JsonDocument.Parse("""{"type":"object","properties":{"values":{"type":"array","items":{"type":"string"},"maxItems":65}}}""").RootElement));
    }

    [Fact]
    public void DynamicMapSchemasValidateAndConvertBoundedArbitraryJson()
    {
        using var schema = JsonDocument.Parse("""{"type":"object","properties":{"tool":{"type":"string"},"arguments":{"type":"object","propertyNames":{"type":"string"},"additionalProperties":{}}},"required":["tool","arguments"]}""");
        using var arguments = JsonDocument.Parse("""{"tool":"ntfy.publish","arguments":{"topic":"alerts","priority":5,"tags":["red",true,null],"meta":{"source":"agent"}}}""");

        Assert.True(McpToolInputSchemas.IsSupported(schema.RootElement));
        var summary = McpToolInputSchemas.Summarize(schema.RootElement);
        Assert.Contains("arguments: dynamic JSON object required", summary);
        var normalized = McpToolInputSchemas.Normalize(schema.RootElement);
        Assert.Equal(JsonValueKind.Object, normalized.GetProperty("properties").GetProperty("arguments").GetProperty("additionalProperties").ValueKind);
        var converted = McpToolInputSchemas.ToCallArguments(schema.RootElement, arguments.RootElement);
        var dynamicArguments = Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(converted["arguments"]);
        Assert.Equal(5L, dynamicArguments["priority"]);
        Assert.Equal(new object?[] { "red", true, null }, Assert.IsAssignableFrom<object?[]>(dynamicArguments["tags"]));

        using var tooDeep = JsonDocument.Parse("""{"tool":"x","arguments":{"a":{"b":{"c":{"d":{"e":{"f":{"g":{"h":{"i":1}}}}}}}}}}""");
        Assert.Throws<ToolInvocationException>(() => McpToolInputSchemas.ToCallArguments(schema.RootElement, tooDeep.RootElement));
    }

    [Fact]
    public void OptionalNullDefaultFieldsAndImplicitArrayBoundsAreSupportedSafely()
    {
        using var schema = JsonDocument.Parse("""
            {"type":"object","properties":{
              "command":{"default":null,"description":"Shell command string (mutually exclusive with args).","type":"string"},
              "args":{"default":null,"description":"List of arguments for direct execution.","items":{"type":"string"},"type":"array"},
              "stdin":{"default":null,"type":"string"},
              "working_directory":{"default":null,"type":"string"},
              "timeout":{"default":null,"type":"integer"}
            },"title":"sandbox_execArguments"}
            """);

        Assert.True(McpToolInputSchemas.IsSupported(schema.RootElement));
        Assert.Null(McpToolInputSchemas.GetUnsupportedReason(schema.RootElement));
        Assert.Contains("array<string> optional", McpToolInputSchemas.Summarize(schema.RootElement));
        var normalized = McpToolInputSchemas.Normalize(schema.RootElement);
        Assert.Equal(64, normalized.GetProperty("properties").GetProperty("args").GetProperty("maxItems").GetInt32());
        Assert.False(normalized.GetProperty("additionalProperties").GetBoolean());

        using var valid = JsonDocument.Parse("""{"command":"python3 -c 'print(1 + 1)'","args":["one","two"],"timeout":30}""");
        var converted = McpToolInputSchemas.ToCallArguments(schema.RootElement, valid.RootElement);
        Assert.Equal(30L, converted["timeout"]);
        using var nullableInput = JsonDocument.Parse("""{"command":null}""");
        Assert.Throws<ToolInvocationException>(() => McpToolInputSchemas.ToCallArguments(schema.RootElement, nullableInput.RootElement));
        using var tooMany = JsonDocument.Parse("""{"args":["a","b","c","d","e","f","g","h","i","j","k","l","m","n","o","p","q","r","s","t","u","v","w","x","y","z","a","b","c","d","e","f","g","h","i","j","k","l","m","n","o","p","q","r","s","t","u","v","w","x","y","z","a","b","c","d","e","f","g","h","i","j","k","l","m","n","o"]}""");
        Assert.Throws<ToolInvocationException>(() => McpToolInputSchemas.ToCallArguments(schema.RootElement, tooMany.RootElement));
    }

    [Fact]
    public void UnsupportedSchemaKeywordsProduceActionableDiscoveryReason()
    {
        using var schema = JsonDocument.Parse("""{"type":"object","properties":{"value":{"type":"string","format":"date-time"}}}""");
        Assert.Equal("Unsupported input schema: Unsupported JSON Schema keyword 'format'.",
            McpToolInputSchemas.GetUnsupportedReason(schema.RootElement));
    }

    [Fact]
    public void OverlongDescriptionsProduceActionableDiscoveryReason()
    {
        Assert.Equal("Tool description exceeds the 2048-character limit.",
            McpToolDescription.GetUnsupportedReason(new string('x', McpToolDescription.MaximumLength + 1)));
        Assert.Null(McpToolDescription.GetUnsupportedReason("A supported tool description."));
    }

    [Fact]
    public void StringPatternsAreNormalizedAndEnforcedWithBoundedRegexEvaluation()
    {
        using var schema = JsonDocument.Parse("""{"type":"object","properties":{"topic":{"type":"string","pattern":"^[a-zA-Z0-9_-]+$"}},"required":["topic"]}""");
        Assert.True(McpToolInputSchemas.IsSupported(schema.RootElement));
        Assert.Equal("^[a-zA-Z0-9_-]+$", McpToolInputSchemas.Normalize(schema.RootElement)
            .GetProperty("properties").GetProperty("topic").GetProperty("pattern").GetString());

        using var valid = JsonDocument.Parse("""{"topic":"news_updates-01"}""");
        Assert.Equal("news_updates-01", McpToolInputSchemas.ToCallArguments(schema.RootElement, valid.RootElement)["topic"]);
        using var invalid = JsonDocument.Parse("""{"topic":"bad/topic"}""");
        Assert.Throws<ToolInvocationException>(() => McpToolInputSchemas.ToCallArguments(schema.RootElement, invalid.RootElement));

        using var malformed = JsonDocument.Parse("""{"type":"object","properties":{"topic":{"type":"string","pattern":"["}}}""");
        Assert.Contains("not a valid", McpToolInputSchemas.GetUnsupportedReason(malformed.RootElement));
        using var unsupportedFeature = JsonDocument.Parse("""{"type":"object","properties":{"topic":{"type":"string","pattern":"(?=a)a"}}}""");
        Assert.Contains("not supported by the safe non-backtracking engine", McpToolInputSchemas.GetUnsupportedReason(unsupportedFeature.RootElement));
    }

    [Fact]
    public async Task TypedScalarArgumentsAreValidatedAndPassedToMcp()
    {
        var broker = CreateBroker(policy: new DemoToolPolicy());
        var add = (await broker.ListBindingsAsync()).Single(binding => binding.BindingId == "demo.add-integers");
        using var schema = JsonDocument.Parse(add.InputSchemaJson);
        Assert.Equal("integer", schema.RootElement.GetProperty("properties").GetProperty("left").GetProperty("type").GetString());
        var events = new List<string>();
        await using var scope = broker.OpenScope(Invocation(Revision(add), events));

        using var arguments = JsonDocument.Parse("{\"left\": 4, \"right\": 5}");
        Assert.Equal("9", await scope.InvokeJsonAsync(add.BindingId, arguments.RootElement));

        using var missing = JsonDocument.Parse("{\"left\": 4}");
        await Assert.ThrowsAsync<ToolInvocationException>(() => scope.InvokeJsonAsync(add.BindingId, missing.RootElement));
        using var unknown = JsonDocument.Parse("{\"left\": 4, \"right\": 5, \"extra\": true}");
        await Assert.ThrowsAsync<ToolInvocationException>(() => scope.InvokeJsonAsync(add.BindingId, unknown.RootElement));
        Assert.Equal(1, events.Count(kind => kind == ExecutionEventKinds.ToolAuthorized));
        Assert.Equal(2, events.Count(kind => kind == ExecutionEventKinds.ToolFailed));
    }

    [Fact]
    public async Task NodeDeclarationAndHostGrantAreBothRequiredEvenForGuessedNames()
    {
        var broker = CreateBroker(policy: new DemoToolPolicy());
        var reverse = (await broker.ListBindingsAsync()).Single(b => b.BindingId == "demo.reverse");
        var revision = Revision(reverse);
        var connections = 0;
        var registrations = Registrations().Select(r => r with
        {
            Connect = ct => { Interlocked.Increment(ref connections); return r.Connect(ct); }
        }).ToArray();
        var restricted = CreateBroker(registrations, new ExplicitToolAuthorizationPolicy([
            new(revision.Definition.WorkflowId, "step-1", "demo.uppercase")
        ]));
        var events = new List<string>();
        await using var scope = restricted.OpenScope(Invocation(revision, events));
        Assert.Empty(await scope.ListToolsAsync());
        await Assert.ThrowsAsync<ToolAccessDeniedException>(() => scope.InvokeAsync("demo.reverse", "test"));
        await Assert.ThrowsAsync<ToolAccessDeniedException>(() => scope.InvokeAsync("demo.uppercase", "test"));
        Assert.Equal(0, connections);
        Assert.Equal(2, events.Count(e => e == ExecutionEventKinds.ToolDenied));
    }

    [Fact]
    public async Task AnotherNodeCannotBorrowTheGrantedNodesTool()
    {
        var broker = CreateBroker(policy: new DemoToolPolicy());
        var reverse = (await broker.ListBindingsAsync()).Single(b => b.BindingId == "demo.reverse");
        var revision = Revision(reverse);
        var restricted = CreateBroker(Registrations(), new ExplicitToolAuthorizationPolicy([
            new(revision.Definition.WorkflowId, "other-node", reverse.BindingId)
        ]));
        await using var scope = restricted.OpenScope(Invocation(revision, []));
        await Assert.ThrowsAsync<ToolAccessDeniedException>(() => scope.InvokeAsync(reverse.BindingId, "test"));
    }

    [Fact]
    public async Task ContractDriftFailsBeforeDispatch()
    {
        var broker = CreateBroker(policy: new DemoToolPolicy());
        var reverse = (await broker.ListBindingsAsync()).Single(b => b.BindingId == "demo.reverse");
        var events = new List<string>();
        await using var scope = broker.OpenScope(Invocation(Revision(reverse with { ContractHash = new string('0', 64) }), events));
        await Assert.ThrowsAsync<ToolContractMismatchException>(() => scope.InvokeAsync(reverse.BindingId, "test"));
        Assert.DoesNotContain(ExecutionEventKinds.ToolAuthorized, events);
        Assert.Contains(ExecutionEventKinds.ToolFailed, events);
    }

    [Fact]
    public async Task DescriptionDriftFailsBeforeDispatchWhenDescriptionWasPinned()
    {
        var broker = CreateBroker(policy: new DemoToolPolicy());
        var reverse = (await broker.ListBindingsAsync()).Single(binding => binding.BindingId == "demo.reverse");
        var pinned = reverse with { DescriptionHash = McpToolDescription.ComputeHash("A different operation.") };
        var events = new List<string>();
        await using var scope = broker.OpenScope(Invocation(Revision(pinned), events));

        await Assert.ThrowsAsync<ToolDescriptionMismatchException>(() => scope.InvokeAsync(reverse.BindingId, "test"));
        Assert.DoesNotContain(ExecutionEventKinds.ToolAuthorized, events);
        Assert.Contains(ExecutionEventKinds.ToolFailed, events);
    }

    [Fact]
    public async Task RevocationAndDisposedScopePreventCalls()
    {
        var policy = new SwitchPolicy();
        var broker = CreateBroker(Registrations(), policy);
        var reverse = (await broker.ListBindingsAsync()).Single(b => b.BindingId == "demo.reverse");
        var scope = broker.OpenScope(Invocation(Revision(reverse), []));
        policy.Allowed = false;
        await Assert.ThrowsAsync<ToolAccessDeniedException>(() => scope.InvokeAsync(reverse.BindingId, "test"));
        await scope.DisposeAsync();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => scope.InvokeAsync(reverse.BindingId, "test"));
    }

    [Fact]
    public async Task OversizedInputIsDeniedWithoutConnecting()
    {
        var descriptor = new ToolBindingDescriptor("demo.reverse", "reverse", "demo-text", "reverse", new string('0', 64));
        var connections = 0;
        var registration = Registrations()[1] with { Connect = _ => { connections++; throw new InvalidOperationException("Must not connect"); } };
        var broker = CreateBroker([registration], new DemoToolPolicy());
        await using var scope = broker.OpenScope(Invocation(Revision(descriptor), []));
        await Assert.ThrowsAsync<ToolInvocationException>(() => scope.InvokeAsync(descriptor.BindingId, new string('x', 16_385)));
        Assert.Equal(0, connections);
    }

    [Fact]
    public async Task InvocationScopeAllowsSixteenCallsAndDeniesTheSeventeenth()
    {
        var broker = CreateBroker(policy: new DemoToolPolicy());
        var reverse = (await broker.ListBindingsAsync()).Single(binding => binding.BindingId == "demo.reverse");
        var events = new List<string>();
        await using var scope = broker.OpenScope(Invocation(Revision(reverse), events));

        for (var index = 0; index < 16; index++)
            Assert.Equal("x", await scope.InvokeAsync(reverse.BindingId, "x"));

        await Assert.ThrowsAsync<ToolInvocationException>(() => scope.InvokeAsync(reverse.BindingId, "call 16"));
        Assert.Equal(16, events.Count(kind => kind == ExecutionEventKinds.ToolCompleted));
        Assert.Equal(1, events.Count(kind => kind == ExecutionEventKinds.ToolDenied));
    }

    [Fact]
    public async Task NonReadOnlyRegistrationsFailClosedEvenWithAHostGrant()
    {
        var descriptor = new ToolBindingDescriptor("demo.reverse", "reverse", "demo-text", "reverse", new string('0', 64));
        var registration = Registrations()[1] with { ReadOnly = false, Connect = _ => throw new InvalidOperationException("Must not connect") };
        var broker = CreateBroker([registration], new DemoToolPolicy());
        Assert.Empty(await broker.ListBindingsAsync());
        await using var scope = broker.OpenScope(Invocation(Revision(descriptor), []));
        await Assert.ThrowsAsync<ToolAccessDeniedException>(() => scope.InvokeAsync(descriptor.BindingId, "test"));
    }

    [Fact]
    public async Task ConnectionTimeoutIsRecordedWithoutRetry()
    {
        var descriptor = new ToolBindingDescriptor("demo.reverse", "reverse", "demo-text", "reverse", new string('0', 64));
        var attempts = 0;
        var registration = Registrations()[1] with { Connect = async ct =>
        {
            Interlocked.Increment(ref attempts);
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            throw new InvalidOperationException("Unreachable");
        } };
        var broker = CreateBroker([registration], new DemoToolPolicy(), TimeSpan.FromMilliseconds(100));
        var events = new List<string>();
        await using var scope = broker.OpenScope(Invocation(Revision(descriptor), events));
        await Assert.ThrowsAsync<ToolInvocationException>(() => scope.InvokeAsync(descriptor.BindingId, "test"));
        Assert.Equal(1, attempts);
        Assert.DoesNotContain(ExecutionEventKinds.ToolAuthorized, events);
        Assert.Contains(ExecutionEventKinds.ToolFailed, events);
    }

    [Fact]
    public async Task ExplicitlyClassifiedPostDispatchFailureIsRecoverableFeedback()
    {
        var registration = (await CreateBroker(policy: new DemoToolPolicy()).ListBindingsAsync())
            .Single(binding => binding.BindingId == "demo.protocol-failure");
        var hostRegistration = Registrations().Single(item => item.BindingId == registration.BindingId) with
        {
            AllowModelRecoveryAfterFailure = true,
            IsDefinitiveToolFailure = exception => exception is ModelContextProtocol.McpProtocolException
        };
        var events = new List<string>();
        var broker = CreateBroker([hostRegistration], new DemoToolPolicy());
        await using var scope = broker.OpenScope(Invocation(Revision(registration), events));

        using var arguments = JsonDocument.Parse("{}");
        var failure = await Assert.ThrowsAsync<ToolInvocationException>(() => scope.InvokeJsonAsync(registration.BindingId, arguments.RootElement));

        Assert.True(failure.Disposition == ToolInvocationFailureDisposition.RecoverableFeedback,
            $"{failure.GetType().FullName}: {failure.Message}");
        Assert.Contains(ExecutionEventKinds.ToolFailed, events);
        Assert.DoesNotContain(ExecutionEventKinds.ToolUncertain, events);
    }

    [Fact]
    public async Task UnclassifiedPostDispatchFailureRemainsOutcomeUnknown()
    {
        var registration = (await CreateBroker(policy: new DemoToolPolicy()).ListBindingsAsync())
            .Single(binding => binding.BindingId == "demo.protocol-failure");
        var hostRegistration = Registrations().Single(item => item.BindingId == registration.BindingId);
        var events = new List<(string Kind, string? Data)>();
        var broker = CreateBroker([hostRegistration], new DemoToolPolicy());
        var revision = Revision(registration);
        var invocation = new NodeInvocationContext(Guid.NewGuid(), revision, "step-1", Guid.NewGuid(),
            (kind, data) => { events.Add((kind, data)); return ValueTask.CompletedTask; });
        await using var scope = broker.OpenScope(invocation);

        using var arguments = JsonDocument.Parse("{}");
        await Assert.ThrowsAnyAsync<Exception>(() => scope.InvokeJsonAsync(registration.BindingId, arguments.RootElement));

        var failure = Assert.Single(events, item => item.Kind == ExecutionEventKinds.ToolUncertain);
        Assert.Contains("errorType", failure.Data);
        Assert.DoesNotContain("Tool operation failed.", failure.Data);
        Assert.DoesNotContain(events, item => item.Kind == ExecutionEventKinds.ToolFailed);
    }

    [Fact]
    public void ExistingPureWorkflowHashesRemainCompatible()
    {
        var definition = WorkflowTemplates.Create("uppercase-reverse");
        var serialized = WorkflowJson.Serialize(definition);
        Assert.DoesNotContain("\r", serialized);
        Assert.Equal("24a0d04c870b902c333b83ef8f1eb2806115e550418a9e631edcb1b7bd842cec",
            WorkflowJson.ComputeHash(definition));
        Assert.DoesNotContain("\"tool\"", WorkflowJson.Serialize(WorkflowTemplates.Create("uppercase")));
    }

    private sealed class SwitchPolicy : IToolAuthorizationPolicy
    {
        public bool Allowed { get; set; } = true;
        public bool IsAllowed(WorkflowRevision revision, string nodeId, string bindingId) => Allowed;
    }
}
