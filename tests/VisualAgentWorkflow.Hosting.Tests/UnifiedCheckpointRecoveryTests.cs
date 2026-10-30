using System.Collections.Immutable;
using System.Text.Json;
using VisualAgentWorkflow.AgentFramework;
using VisualAgentWorkflow.Core;
using Xunit;

namespace VisualAgentWorkflow.Hosting.Tests;

public sealed class UnifiedCheckpointRecoveryTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "vaw-unified-checkpoint-" + Guid.NewGuid());
    private SqliteWorkflowRunStore Store() => new(Path.Combine(directory, "runs.sqlite3"));

    [Fact]
    public async Task Unified_loop_requires_opt_in_before_admission()
    {
        var store = Store();
        await using var host = new CheckpointWorkflowHost(store);
        await Assert.ThrowsAsync<InvalidOperationException>(() => host.StartAsync(
            WorkflowRevision.Create(LoopDefinition(), "test"), "continue", 4));
        Assert.Empty(store.List());
    }

    [Fact]
    public async Task Unified_loop_recovers_exact_revision_frame_and_variables_after_host_replacement()
    {
        var store = Store();
        var revision = WorkflowRevision.Create(LoopDefinition(), "test");
        RunSnapshot saved;
        await using (var host = new CheckpointWorkflowHost(store, new(true)))
            saved = await host.StartAsync(revision, "continue", 4);
        var checkpoint = store.GetCheckpoint(saved.Header.RunId)!;
        Assert.Equal("suspended", saved.Status);
        Assert.Equal(revision.RevisionId, checkpoint.RevisionId);
        Assert.Equal(revision.ContentHash, checkpoint.ContentHash);
        Assert.Equal(WorkflowSchemaVersions.UnifiedWorkflowGraph, checkpoint.Frame!.WorkflowSchemaVersion);
        Assert.Equal(WorkflowCheckpointFrame.CurrentFrameVersion, checkpoint.Frame.FrameVersion);
        Assert.True(WorkflowCheckpointRecovery.Assess(saved, checkpoint).CanResume);
        Assert.False(WorkflowCheckpointRecovery.Assess(saved, checkpoint with { Frame = null }).CanResume);

        var wrongSchema = checkpoint.Frame with { WorkflowSchemaVersion = WorkflowSchemaVersions.LoopVariablesGraph };
        wrongSchema = wrongSchema with { FrameHash = wrongSchema.ComputeFrameHash() };
        Assert.False(WorkflowCheckpointRecovery.Assess(saved, checkpoint with { Frame = wrongSchema }).CanResume);
        Assert.False(WorkflowCheckpointRecovery.Assess(saved, checkpoint with { Native = checkpoint.Native with { Payload = "{}" }, PayloadHash = WorkflowRunCheckpoint.Hash("{}") }).CanResume);

        await using (var defaultHost = new CheckpointWorkflowHost(Store()))
            await Assert.ThrowsAsync<InvalidOperationException>(() => defaultHost.ResumeAsync(saved.Header.RunId, checkpoint.Native.CheckpointId));
        Assert.Equal(saved.Events.ToArray(), store.Get(saved.Header.RunId)!.Events.ToArray());

        await using var replacement = new CheckpointWorkflowHost(Store(), new(true));
        var completed = await replacement.ResumeAsync(saved.Header.RunId, checkpoint.Native.CheckpointId);
        Assert.Equal("completed", completed.Status);
        Assert.Equal("continue", completed.Events[^1].Data);
        Assert.Equal(saved.Events.ToArray(), completed.Events.Take(saved.Events.Length).ToArray());
        Assert.Equal(new long[] { 2, 3, 4 }, completed.Events.Where(evt => evt.Kind == ExecutionEventKinds.VariableWritten)
            .Select(evt => JsonDocument.Parse(evt.Data!).RootElement.GetProperty("version").GetInt64()));
        Assert.Equal(revision.ContentHash, completed.Header.ContentHash);
        Assert.Equal(WorkflowSchemaVersions.UnifiedWorkflowGraph, completed.Header.Definition.SchemaVersion);
    }

    [Theory]
    [InlineData(NodeTypes.Agent)]
    [InlineData(NodeTypes.McpTool)]
    [InlineData(NodeTypes.SequenceUppercase)]
    public void Unified_checkpoint_admission_rejects_valid_loop_bodies_outside_the_proof(string nodeType)
    {
        var definition = LoopDefinition();
        var node = nodeType switch
        {
            NodeTypes.Agent => new NodeDefinition(nodeType, "1.0.0") { Agent = new("fixture", null, "Answer", []) },
            NodeTypes.McpTool => new NodeDefinition(nodeType, "1.0.0", new("fixture.tool", new string('a', 64)))
                { McpInputPorts = [new("arg.input", "input", "string", true)] },
            _ => new NodeDefinition(nodeType, "1.0.0")
        };
        var inputPort = nodeType == NodeTypes.McpTool ? "arg.input" : "in";
        definition = definition with
        {
            Nodes = definition.Nodes.SetItem("get", node),
            ExecutionConnections = definition.ExecutionConnections!.SetItem("get-loop",
                new(new("get", nodeType == NodeTypes.Agent ? "out" : "then"), new("loop", "execute"), "execution")),
            Connections = definition.Connections
                .Add("body-input", new(new("input", "out"), new("get", inputPort), "data"))
        };
        WorkflowValidator.ThrowIfInvalid(definition);
        Assert.Throws<InvalidOperationException>(() => WorkflowRunner.ValidateCheckpointGraph(definition, true));
    }

    public void Dispose()
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, true);
    }

    private static WorkflowDefinition LoopDefinition()
    {
        var nodes = new Dictionary<string, NodeDefinition>
        {
            ["input"] = new(NodeTypes.Input, "1.0.0"),
            ["loop"] = new(NodeTypes.Loop, "1.0.0") { Loop = new(new("contains", "continue"), 3) },
            ["body"] = new(NodeTypes.LoopBody, "1.0.0"),
            ["set"] = new(NodeTypes.VariableSet, "1.0.0") { VariableId = "message", InputDataType = WorkflowDataTypes.String },
            ["get"] = new(NodeTypes.VariableGet, "1.0.0") { VariableId = "message", OutputDataType = WorkflowDataTypes.String },
            ["output"] = new(NodeTypes.Output, "1.0.0")
        };
        var data = new Dictionary<string, ConnectionDefinition>
        {
            ["input-value"] = new(new("input", "out"), new("set", "value"), "data"),
            ["get-value"] = new(new("get", "out"), new("output", "in"), "data")
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
        return new WorkflowDefinition(WorkflowSchemaVersions.UnifiedWorkflowGraph, "loop-variable-runtime", "input",
            nodes.ToImmutableSortedDictionary(), data.ToImmutableSortedDictionary())
        {
            Variables = ImmutableSortedDictionary<string, WorkflowVariableDefinition>.Empty
                .Add("message", new("Message", "default")),
            ExecutionConnections = execution.ToImmutableSortedDictionary()
        };
    }

}
