using System.Runtime.InteropServices;
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
        var definition = PipelineTemplates.Create("uppercase");
        definition = PipelineEditing.ReplaceNode(definition, "step-1", new(NodeTypes.McpTool, "1.0.0", new(binding.BindingId, binding.ContractHash)));
        return WorkflowRevision.Create(definition, "test");
    }

    private static NodeInvocationContext Invocation(WorkflowRevision revision, List<string> kinds) =>
        new(Guid.NewGuid(), revision, "step-1", Guid.NewGuid(), (kind, _) => { kinds.Add(kind); return ValueTask.CompletedTask; });

    [Fact]
    public async Task ActualStdioDiscoveryInvocationAndRecordingWork()
    {
        var broker = CreateBroker(policy: new DemoToolPolicy());
        var bindings = await broker.ListBindingsAsync();
        Assert.Equal(3, bindings.Count);
        var reverse = bindings.Single(b => b.BindingId == "demo.reverse");
        var events = new List<string>();
        await using var scope = broker.OpenScope(Invocation(Revision(reverse), events));
        Assert.Single(await scope.ListToolsAsync());
        Assert.Equal("🌍 olleh", await scope.InvokeAsync("demo.reverse", "hello 🌍"));
        Assert.Equal(new[] { ExecutionEventKinds.ToolRequested, ExecutionEventKinds.ToolAuthorized, ExecutionEventKinds.ToolCompleted }, events);

        var submitAnswer = bindings.Single(b => b.BindingId == "demo.submit-answer");
        await using var answerScope = broker.OpenScope(Invocation(Revision(submitAnswer), []));
        Assert.Equal("2", await answerScope.InvokeAsync(submitAnswer.BindingId, "2"));
        await Assert.ThrowsAsync<ToolInvocationException>(() => answerScope.InvokeAsync(submitAnswer.BindingId, "1 + 1 = 2"));
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
    public void ExistingPureWorkflowHashesRemainCompatible()
    {
        var definition = PipelineTemplates.Create("uppercase-reverse");
        var serialized = WorkflowJson.Serialize(definition);
        Assert.DoesNotContain("\r", serialized);
        Assert.Equal("24a0d04c870b902c333b83ef8f1eb2806115e550418a9e631edcb1b7bd842cec",
            WorkflowJson.ComputeHash(definition));
        Assert.DoesNotContain("\"tool\"", WorkflowJson.Serialize(PipelineTemplates.Create("uppercase")));
    }

    private sealed class SwitchPolicy : IToolAuthorizationPolicy
    {
        public bool Allowed { get; set; } = true;
        public bool IsAllowed(WorkflowRevision revision, string nodeId, string bindingId) => Allowed;
    }
}
