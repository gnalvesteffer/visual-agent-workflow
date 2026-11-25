using System.Collections.Immutable;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using VisualAgentWorkflow.Core;
using Xunit;

namespace VisualAgentWorkflow.Hosting.Tests;

public sealed class WorkflowOperationStartTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "vaw-opstart-" + Guid.NewGuid());
    public void Dispose() { try { Directory.Delete(directory, true); } catch (IOException) { } }

    private static WorkflowDefinition Passthrough(string id) =>
        new(WorkflowSchemaVersions.CurrentAuthoringSchema, id, "input",
            new Dictionary<string, NodeDefinition>
            {
                ["input"] = new(NodeTypes.Input, "1.0.0") { InputDataType = WorkflowDataTypes.Json },
                ["output"] = new(NodeTypes.Output, "1.0.0") { OutputDataType = WorkflowDataTypes.Json }
            }.ToImmutableSortedDictionary(StringComparer.Ordinal),
            new Dictionary<string, ConnectionDefinition>
            { ["input-output"] = new(new("input", "out"), new("output", "in"), "data") }.ToImmutableSortedDictionary(StringComparer.Ordinal))
        {
            ExecutionConnections = new Dictionary<string, ConnectionDefinition>
            { ["input-output"] = new(new("input", "then"), new("output", "execute"), "execution") }.ToImmutableSortedDictionary(StringComparer.Ordinal)
        };

    private static WorkflowInterfaceImplementationRevision Implementation()
    {
        var contract = WorkflowInterfaceRevision.Create(new WorkflowInterfaceDefinition("sample.api", "Sample", "Proof.",
            ImmutableSortedDictionary<string, WorkflowInterfaceOperation>.Empty
                .Add("echo", new("Echo", WorkflowDataTypes.Json, WorkflowDataTypes.Json)
                { Inputs = [new("text", "Text", WorkflowDataTypes.String)], Outputs = [new("text", "Text", WorkflowDataTypes.String)] })
                .Add("pair", new("Pair", WorkflowDataTypes.Json, WorkflowDataTypes.Json)
                {
                    Inputs = [new("a", "A", WorkflowDataTypes.String), new("n", "N", WorkflowDataTypes.Integer)],
                    Outputs = [new("ok", "Ok", WorkflowDataTypes.Boolean)]
                }))
        { SchemaVersion = WorkflowInterfaceJson.FieldContractSchemaVersion });
        return WorkflowInterfaceImplementationRevision.Create(new WorkflowInterfaceImplementation("sample.impl", contract,
            ImmutableSortedDictionary<string, WorkflowInterfaceWorkflowPin>.Empty
                .Add("echo", WorkflowInterfaceWorkflowPin.From(Passthrough("echo-flow")))
                .Add("pair", WorkflowInterfaceWorkflowPin.From(Passthrough("pair-flow")))));
    }

    private static async Task<RunSnapshot> Terminal(LocalWorkflowRuntime runtime, Guid id)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (true)
        {
            var run = (await runtime.GetRunAsync(id))!;
            if (run.IsTerminal) return run;
            await Task.Delay(10, deadline.Token);
        }
    }

    private static WorkflowStartRequest Request(WorkflowInterfaceImplementationRevision impl, string op, string fields) =>
        new(op, "sample.api", impl.Definition.Interface.RevisionId, fields);

    [Fact]
    public async Task ValidOperationStartsTheSameRunAsALegacyStartWithTheMappedInput()
    {
        var impl = Implementation();
        using var runtime = new LocalWorkflowRuntime(new FileRunJournal(directory), NullLogger<LocalWorkflowRuntime>.Instance);
        await runtime.StartAsync(CancellationToken.None);
        try
        {
            var shim = await runtime.StartOperationAsync(Request(impl, "echo", """{"text":"héllo"}"""), impl, "alice");
            Assert.True(shim.Started);
            Assert.Equal("echo", shim.OperationId);
            Assert.Equal("alice", shim.Actor);
            var legacy = await runtime.StartRevisionAsync(
                WorkflowRevision.Create(impl.Definition.Operations["echo"].Definition, "local-developer"),
                shim.Validation.EntryInput!);
            var a = await Terminal(runtime, shim.Run!.Header.RunId);
            var b = await Terminal(runtime, legacy.Header.RunId);
            Assert.Equal("completed", a.Status);
            Assert.Equal(b.Header.Input, a.Header.Input);
            Assert.Equal(b.Header.ContentHash, a.Header.ContentHash);
            Assert.Equal(pinRevision(impl, "echo"), a.Header.RevisionId);
            Assert.Equal(b.Events[^1].Data, a.Events[^1].Data);
            Assert.Equal(b.Events.Select(e => e.Kind), a.Events.Select(e => e.Kind));
            Assert.DoesNotContain("alice", JsonSerializer.Serialize(a));
        }
        finally { await runtime.StopAsync(CancellationToken.None); }
    }

    private static Guid pinRevision(WorkflowInterfaceImplementationRevision impl, string op) =>
        impl.Definition.Operations[op].RevisionId;

    [Fact]
    public async Task TwoOperationsOfOneImplementationStartTheirOwnWorkflows()
    {
        var impl = Implementation();
        using var runtime = new LocalWorkflowRuntime(new FileRunJournal(directory), NullLogger<LocalWorkflowRuntime>.Instance);
        await runtime.StartAsync(CancellationToken.None);
        try
        {
            var echo = await runtime.StartOperationAsync(Request(impl, "echo", """{"text":"x"}"""), impl);
            var pair = await runtime.StartOperationAsync(Request(impl, "pair", """{"n":2,"a":"y"}"""), impl);
            var echoRun = await Terminal(runtime, echo.Run!.Header.RunId);
            var pairRun = await Terminal(runtime, pair.Run!.Header.RunId);
            Assert.Equal("echo-flow", echoRun.Header.Definition.WorkflowId);
            Assert.Equal("pair-flow", pairRun.Header.Definition.WorkflowId);
            Assert.Equal("""{"a":"y","n":2}""", pairRun.Header.Input);
            Assert.Equal("completed", pairRun.Status);
            Assert.Equal("""{"a":"y","n":2}""", pairRun.Events[^1].Data);
        }
        finally { await runtime.StopAsync(CancellationToken.None); }
    }

    [Theory]
    [InlineData("echo", """{}""", null)]
    [InlineData("echo", """{"text":1}""", null)]
    [InlineData("echo", """{"text":"x","extra":1}""", null)]
    [InlineData("nope", """{"text":"x"}""", null)]
    [InlineData("echo", """{"text":"x"}""", "bad\nactor")]
    public async Task RejectedRequestsStartNoRun(string op, string fields, string? actor)
    {
        var impl = Implementation();
        using var runtime = new LocalWorkflowRuntime(new FileRunJournal(directory), NullLogger<LocalWorkflowRuntime>.Instance);
        await runtime.StartAsync(CancellationToken.None);
        try
        {
            var result = await runtime.StartOperationAsync(Request(impl, op, fields), impl, actor);
            Assert.False(result.Started);
            Assert.Null(result.Run);
            Assert.NotEmpty(result.Errors);
            Assert.Empty(await runtime.ListRunsAsync());
        }
        finally { await runtime.StopAsync(CancellationToken.None); }
    }

    [Fact]
    public async Task StalePinsStartNoRun()
    {
        var impl = Implementation();
        using var runtime = new LocalWorkflowRuntime(new FileRunJournal(directory), NullLogger<LocalWorkflowRuntime>.Instance);
        var stale = new WorkflowStartRequest("echo", "sample.api", Guid.NewGuid(), """{"text":"x"}""");
        var result = await runtime.StartOperationAsync(stale, impl);
        Assert.Contains(result.Errors, e => e.Code == "stale_interface_revision");
        Assert.Empty(await runtime.ListRunsAsync());
    }
}
