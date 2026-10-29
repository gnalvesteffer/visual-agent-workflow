using System.Collections.Immutable;
using Microsoft.Extensions.Logging.Abstractions;
using VisualAgentWorkflow.Blazor;
using VisualAgentWorkflow.Core;
using Xunit;

namespace VisualAgentWorkflow.Hosting.Tests;

public sealed class WorkflowAdmissionRuntimeTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "vaw-admission-" + Guid.NewGuid());

    [Fact]
    public async Task MissingMcpBindingIsRejectedBeforeRunCreation()
    {
        var journal = new FileRunJournal(directory);
        var provider = new SnapshotProvider(new WorkflowAdmissionHostSnapshot([], []));
        using var runtime = new LocalWorkflowRuntime(journal, NullLogger<LocalWorkflowRuntime>.Instance,
            admissionProvider: provider);
        await runtime.StartAsync(CancellationToken.None);
        try
        {
            var error = await Assert.ThrowsAsync<WorkflowAdmissionException>(() =>
                runtime.StartPausedRunAsync(Definition("missing", new string('a', 64)), "input"));

            Assert.Contains(error.Report.Issues, issue => issue.Kind == WorkflowAdmissionIssueKind.Missing);
            Assert.Empty(journal.List());
            Assert.Equal(1, provider.Calls);
        }
        finally { await runtime.StopAsync(CancellationToken.None); }
    }

    [Fact]
    public async Task ExactSnapshotAllowsPausedRunAndCapturesOneSnapshot()
    {
        const string binding = "fixture.tool";
        const string contract = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        var journal = new FileRunJournal(directory);
        var provider = new SnapshotProvider(new WorkflowAdmissionHostSnapshot([],
            [new WorkflowAdmissionToolFact(binding, contract)]));
        using var runtime = new LocalWorkflowRuntime(journal, NullLogger<LocalWorkflowRuntime>.Instance,
            admissionProvider: provider);
        await runtime.StartAsync(CancellationToken.None);
        try
        {
            var run = await runtime.StartPausedRunAsync(Definition(binding, contract), "input");

            Assert.Equal("paused", run.Status);
            Assert.Single(journal.List());
            Assert.Equal(1, provider.Calls);
        }
        finally { await runtime.StopAsync(CancellationToken.None); }
    }

    [Fact]
    public async Task SnapshotProviderFailureFailsClosedWithoutCreatingRun()
    {
        var journal = new FileRunJournal(directory);
        var provider = new SnapshotProvider(exception: new InvalidOperationException("provider details"));
        using var runtime = new LocalWorkflowRuntime(journal, NullLogger<LocalWorkflowRuntime>.Instance,
            admissionProvider: provider);
        await runtime.StartAsync(CancellationToken.None);
        try
        {
            await Assert.ThrowsAsync<WorkflowAdmissionUnavailableException>(() =>
                runtime.StartPausedRunAsync(Definition("fixture.tool", new string('a', 64)), "input"));

            Assert.Empty(journal.List());
        }
        finally { await runtime.StopAsync(CancellationToken.None); }
    }

    [Fact]
    public async Task MissingSequenceAgentProviderIsRejectedBeforeRunCreation()
    {
        var journal = new FileRunJournal(directory);
        var provider = new SnapshotProvider(new WorkflowAdmissionHostSnapshot([], []));
        using var runtime = new LocalWorkflowRuntime(journal, NullLogger<LocalWorkflowRuntime>.Instance,
            admissionProvider: provider);
        await runtime.StartAsync(CancellationToken.None);
        try
        {
            var error = await Assert.ThrowsAsync<WorkflowAdmissionException>(() =>
                runtime.StartPausedRunAsync(SequenceAgentPathDefinition(), "input"));

            Assert.Contains(error.Report.Issues, issue => issue.Kind == WorkflowAdmissionIssueKind.Missing &&
                issue.Requirement is { Kind: "agent-provider-contract", Id: "sequence-proof" });
            Assert.Empty(journal.List());
            Assert.Equal(1, provider.Calls);
        }
        finally { await runtime.StopAsync(CancellationToken.None); }
    }

    private static WorkflowDefinition Definition(string binding, string contract) =>
        WorkflowEditing.ReplaceNode(WorkflowTemplates.Create("uppercase"), "step-1",
            new(NodeTypes.McpTool, "1.0.0", new(binding, contract)));

    private static WorkflowDefinition SequenceAgentPathDefinition()
    {
        var nodes = new Dictionary<string, NodeDefinition>
        {
            ["input"] = new(NodeTypes.Input, "1.0.0"),
            ["sequence"] = new(NodeTypes.Sequence, "1.0.0")
            {
                Sequence = new SequenceConfiguration([new("inventory", "Inventory"), new("research", "Research")])
            },
            ["inventory-tool"] = new(NodeTypes.McpTool, "1.0.0",
                new ToolBindingRequest("demo.inventory", new string('a', 64))) { McpArgumentsJson = "{}" },
            ["research-agent"] = new(NodeTypes.Agent, "1.0.0")
            {
                Agent = new AgentConfiguration("sequence-proof", null, "Return the supplied text.", [])
                { ProviderContractHash = new string('a', 64), MaxTurns = 1, MaxTotalTokens = 4000 }
            },
            ["output"] = new(NodeTypes.Output, "1.0.0")
        };
        var execution = new Dictionary<string, ConnectionDefinition>
        {
            ["input-sequence"] = new(new("input", "then"), new("sequence", "execute"), "execution"),
            ["sequence-inventory"] = new(new("sequence", "then.inventory"), new("inventory-tool", "execute"), "execution"),
            ["inventory-output"] = new(new("inventory-tool", "then"), new("output", "execute"), "execution"),
            ["sequence-research"] = new(new("sequence", "then.research"), new("research-agent", "execute"), "execution"),
            ["research-output"] = new(new("research-agent", "out"), new("output", "execute"), "execution")
        };
        return new WorkflowDefinition(WorkflowSchemaVersions.SequenceAgentPathGraph, "sequence-admission", "input",
            nodes.ToImmutableSortedDictionary(),
            ImmutableSortedDictionary<string, ConnectionDefinition>.Empty.WithComparers(StringComparer.Ordinal))
        {
            ExecutionConnections = execution.ToImmutableSortedDictionary()
        };
    }

    public void Dispose()
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }

    private sealed class SnapshotProvider(WorkflowAdmissionHostSnapshot? snapshot = null, Exception? exception = null)
        : IWorkflowAdmissionSnapshotProvider
    {
        public int Calls { get; private set; }

        public Task<WorkflowAdmissionHostSnapshot> CaptureAsync(CancellationToken cancellationToken = default)
        {
            Calls++;
            if (exception is not null) throw exception;
            return Task.FromResult(snapshot!);
        }
    }
}
