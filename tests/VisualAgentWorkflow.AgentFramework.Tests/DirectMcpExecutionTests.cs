using System.Collections.Immutable;
using System.IO;
using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using VisualAgentWorkflow.AgentFramework;
using VisualAgentWorkflow.Blazor.Components;
using VisualAgentWorkflow.Core;
using VisualAgentWorkflow.Hosting;
using Xunit;

namespace VisualAgentWorkflow.AgentFramework.Tests;

public sealed class DirectMcpExecutionTests
{
    [Fact]
    public async Task Direct_mcp_result_flows_to_the_output_data_pin()
    {
        var broker = new RecordingBroker();
        var events = new List<ExecutionEvent>();

        var result = await new WorkflowRunner(broker).ExecuteWithOutcomeAsync(Guid.NewGuid(),
            WorkflowRevision.Create(Definition(), "test"), "{\"text\":\"hello\"}", null,
            evt => { events.Add(evt); return ValueTask.CompletedTask; });

        Assert.False(result.Failed);
        Assert.Equal("tool-result", result.Output);
        Assert.Equal("{\"text\":\"hello\"}", broker.Input);
        AssertRedacted(events.Single(evt => evt.Kind == ExecutionEventKinds.NodeStarted && evt.NodeId == "tool"));
        AssertRedacted(events.Single(evt => evt.Kind == ExecutionEventKinds.NodeCompleted && evt.NodeId == "tool"));
        AssertRedacted(events.Single(evt => evt.Kind == ExecutionEventKinds.NodeStarted && evt.NodeId == "output"));
        AssertRedacted(events.Single(evt => evt.Kind == ExecutionEventKinds.NodeCompleted && evt.NodeId == "output"));
        Assert.All(events.Where(evt => evt.Kind == ExecutionEventKinds.DataEvaluated), AssertRedacted);
        Assert.Contains(events, evt => evt.Kind == ExecutionEventKinds.NodeStarted && evt.NodeId == "input" && evt.Data == "{\"text\":\"hello\"}");
    }

    [Fact]
    public async Task Direct_mcp_requires_a_json_object_input()
    {
        var broker = new RecordingBroker();

        await Assert.ThrowsAsync<InvalidOperationException>(() => new WorkflowRunner(broker).ExecuteWithOutcomeAsync(Guid.NewGuid(),
            WorkflowRevision.Create(Definition(), "test"), "hello", null, _ => ValueTask.CompletedTask));
        Assert.Null(broker.Input);
    }

    [Fact]
    public async Task Configured_direct_mcp_arguments_override_the_connected_input()
    {
        var broker = new RecordingBroker();
        var baseline = Definition();
        var definition = baseline with
        {
            Nodes = baseline.Nodes.SetItem("tool", baseline.Nodes["tool"] with { McpArgumentsJson = "{\"query\":\"configured\"}" })
        };

        var result = await new WorkflowRunner(broker).ExecuteWithOutcomeAsync(Guid.NewGuid(),
            WorkflowRevision.Create(definition, "test"), "{\"query\":\"connected\"}", null,
            _ => ValueTask.CompletedTask);

        Assert.False(result.Failed);
        Assert.Equal("{\"query\":\"configured\"}", broker.Input);
    }

    [Fact]
    public async Task Unified_direct_mcp_nested_pin_updates_its_path_and_preserves_static_siblings()
    {
        var broker = new RecordingBroker();
        var nested = new McpInputPortDefinition(WorkflowMcpInputPath.NestedPinId("/request/name"),
            "name", WorkflowDataTypes.String, true) { JsonPointer = "/request/name" };
        var definition = new WorkflowDefinition(WorkflowSchemaVersions.UnifiedWorkflowGraph,
            "nested-mcp-runtime", "input", new Dictionary<string, NodeDefinition>
            {
                ["input"] = new(NodeTypes.Input, "1.0.0"),
                ["name"] = new(NodeTypes.StringConstant, "1.0.0") { StringConstant = new("connected") },
                ["tool"] = new(NodeTypes.McpTool, "1.0.0", new ToolBindingRequest("demo.nested", new string('a', 64)))
                {
                    McpArgumentsJson = "{\"request\":{\"name\":\"static\",\"kind\":\"fixed\",\"labels\":[\"a\"]}}",
                    McpInputPorts = [nested]
                },
                ["output"] = new(NodeTypes.Output, "1.0.0")
            }.ToImmutableSortedDictionary(StringComparer.Ordinal),
            new Dictionary<string, ConnectionDefinition>
            {
                ["name"] = new(new("name", "out"), new("tool", nested.Id), "data"),
                ["result"] = new(new("tool", "out"), new("output", "in"), "data")
            }.ToImmutableSortedDictionary(StringComparer.Ordinal))
        {
            ExecutionConnections = new Dictionary<string, ConnectionDefinition>
            {
                ["activate"] = new(new("input", "then"), new("tool", "execute"), "execution"),
                ["complete"] = new(new("tool", "then"), new("output", "execute"), "execution")
            }.ToImmutableSortedDictionary(StringComparer.Ordinal)
        };

        var events = new List<ExecutionEvent>();
        var result = await new WorkflowRunner(broker).ExecuteWithOutcomeAsync(Guid.NewGuid(),
            WorkflowRevision.Create(definition, "test"), "ignored", null,
            evt => { events.Add(evt); return ValueTask.CompletedTask; });

        Assert.False(result.Failed);
        var output = Assert.IsType<string>(result.Output);
        using var actual = JsonDocument.Parse(broker.Input!);
        Assert.Equal("connected", actual.RootElement.GetProperty("request").GetProperty("name").GetString());
        Assert.Equal("fixed", actual.RootElement.GetProperty("request").GetProperty("kind").GetString());
        Assert.Equal("a", actual.RootElement.GetProperty("request").GetProperty("labels")[0].GetString());
        Assert.DoesNotContain(events, evt => evt.Data?.Contains("connected", StringComparison.Ordinal) == true);
        using var completed = JsonDocument.Parse(Assert.Single(events, evt => evt.Kind == ExecutionEventKinds.Completed).Data!);
        Assert.Equal("redacted", completed.RootElement.GetProperty("payloadState").GetString());
        Assert.Equal(output.Length, completed.RootElement.GetProperty("outputCharacters").GetInt32());
    }

    [Fact]
    public async Task Configured_nested_direct_mcp_values_are_redacted_from_runtime_evidence()
    {
        const string privateName = "private-nested-name";
        const string privateLabel = "private-nested-label";
        var configuredArguments = JsonSerializer.Serialize(new
        {
            payload = new
            {
                request = new { name = privateName, count = 3 },
                labels = new[] { privateLabel }
            }
        });
        var baseline = Definition();
        var definition = baseline with
        {
            Nodes = baseline.Nodes.SetItem("tool", baseline.Nodes["tool"] with
            {
                McpArgumentsJson = configuredArguments
            })
        };
        var broker = new RecordingBroker();
        var events = new List<ExecutionEvent>();

        var result = await new WorkflowRunner(broker).ExecuteWithOutcomeAsync(Guid.NewGuid(),
            WorkflowRevision.Create(definition, "test"), "{}", null,
            evt => { events.Add(evt); return ValueTask.CompletedTask; });

        Assert.False(result.Failed);
        Assert.Equal(configuredArguments, broker.Input);
        Assert.DoesNotContain(events, evt => evt.Data?.Contains(privateName, StringComparison.Ordinal) == true);
        Assert.DoesNotContain(events, evt => evt.Data?.Contains(privateLabel, StringComparison.Ordinal) == true);
        AssertRedacted(events.Single(evt => evt.Kind == ExecutionEventKinds.NodeStarted && evt.NodeId == "tool"));
        AssertRedacted(events.Single(evt => evt.Kind == ExecutionEventKinds.NodeCompleted && evt.NodeId == "tool"));
        var evaluations = events.Where(evt => evt.Kind == ExecutionEventKinds.DataEvaluated).ToArray();
        Assert.NotEmpty(evaluations);
        Assert.All(evaluations, AssertRedacted);
    }

    [Theory]
    [InlineData("file")]
    [InlineData("sqlite")]
    public async Task Configured_nested_direct_mcp_values_remain_redacted_after_store_reopen(string storeKind)
    {
        const string privateName = "persisted-private-nested-name";
        const string privateLabel = "persisted-private-nested-label";
        var configuredArguments = JsonSerializer.Serialize(new
        {
            payload = new { request = new { name = privateName, count = 3 }, labels = new[] { privateLabel } }
        });
        var baseline = Definition();
        var definition = baseline with
        {
            Nodes = baseline.Nodes.SetItem("tool", baseline.Nodes["tool"] with
            {
                McpArgumentsJson = configuredArguments
            })
        };
        var directory = Path.Combine(Path.GetTempPath(), "vaw-nested-mcp-redaction-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        try
        {
            var revision = WorkflowRevision.Create(definition, "nested-mcp-redaction");
            var storePath = storeKind == "sqlite" ? Path.Combine(directory, "runs.db") : Path.Combine(directory, "runs");
            IWorkflowRunStore store = CreateStore(storeKind, storePath);
            var created = store.Create(revision, "{}");
            var broker = new RecordingBroker();

            var result = await new WorkflowRunner(broker).ExecuteWithOutcomeAsync(created.Header.RunId,
                revision, "{}", null, evt =>
                {
                    store.Append(evt);
                    return ValueTask.CompletedTask;
                });

            Assert.False(result.Failed);
            Assert.Equal(configuredArguments, broker.Input);

            IWorkflowRunStore reopened = CreateStore(storeKind, storePath);
            var snapshot = Assert.IsType<RunSnapshot>(reopened.Get(created.Header.RunId));
            Assert.Equal("completed", snapshot.Status);
            Assert.NotEmpty(snapshot.Events);
            Assert.DoesNotContain(snapshot.Events, evt => evt.Data?.Contains(privateName, StringComparison.Ordinal) == true);
            Assert.DoesNotContain(snapshot.Events, evt => evt.Data?.Contains(privateLabel, StringComparison.Ordinal) == true);
            Assert.All(snapshot.Events.Where(evt =>
                evt.NodeId == "tool" && evt.Kind is ExecutionEventKinds.NodeStarted or ExecutionEventKinds.NodeCompleted), AssertRedacted);
            var dataEvaluations = snapshot.Events.Where(evt => evt.Kind == ExecutionEventKinds.DataEvaluated).ToArray();
            Assert.NotEmpty(dataEvaluations);
            Assert.All(dataEvaluations, AssertRedacted);

            var playbackHtml = await RenderPlayback(snapshot);
            Assert.Contains("Execution evidence", playbackHtml, StringComparison.Ordinal);
            Assert.Contains("payloadState", playbackHtml, StringComparison.Ordinal);
            Assert.DoesNotContain(privateName, playbackHtml, StringComparison.Ordinal);
            Assert.DoesNotContain(privateLabel, playbackHtml, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    [Theory]
    [InlineData("file")]
    [InlineData("sqlite")]
    public async Task Connected_nested_mcp_pin_values_remain_redacted_after_store_reopen(string storeKind)
    {
        const string privateName = "connected-private-nested-name";
        const string privateLabel = "connected-private-static-sibling";
        var baseline = PinDefinition() with { SchemaVersion = WorkflowSchemaVersions.UnifiedWorkflowGraph };
        var nested = new McpInputPortDefinition(WorkflowMcpInputPath.NestedPinId("/payload/request/name"),
            "name", WorkflowDataTypes.String, true) { JsonPointer = "/payload/request/name" };
        var tool = baseline.Nodes["tool"] with
        {
            McpArgumentsJson = JsonSerializer.Serialize(new
            {
                payload = new
                {
                    request = new { name = "static-fallback", count = 3 },
                    labels = new[] { privateLabel }
                }
            }),
            McpInputPorts = [new("arg.maxBytes", "maxBytes", WorkflowDataTypes.Integer, false), nested]
        };
        var definition = baseline with
        {
            Nodes = baseline.Nodes
                .SetItem("path", baseline.Nodes["path"] with { StringConstant = new(privateName) })
                .SetItem("tool", tool),
            Connections = baseline.Connections.SetItem("path",
                new(new("path", "out"), new("tool", nested.Id), "data"))
        };
        var directory = Path.Combine(Path.GetTempPath(), "vaw-nested-mcp-connected-redaction-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        try
        {
            var revision = WorkflowRevision.Create(definition, "nested-mcp-connected-redaction");
            var storePath = storeKind == "sqlite" ? Path.Combine(directory, "runs.db") : Path.Combine(directory, "runs");
            IWorkflowRunStore store = CreateStore(storeKind, storePath);
            var created = store.Create(revision, "{}");
            var broker = new RecordingBroker();

            var result = await new WorkflowRunner(broker).ExecuteWithOutcomeAsync(created.Header.RunId,
                revision, "{}", null, evt =>
                {
                    store.Append(evt);
                    return ValueTask.CompletedTask;
                });

            Assert.False(result.Failed);
            using (var arguments = JsonDocument.Parse(broker.Input!))
            {
                Assert.Equal(privateName, arguments.RootElement.GetProperty("payload").GetProperty("request").GetProperty("name").GetString());
                Assert.Equal(privateLabel, arguments.RootElement.GetProperty("payload").GetProperty("labels")[0].GetString());
                Assert.Equal(3, arguments.RootElement.GetProperty("payload").GetProperty("request").GetProperty("count").GetInt32());
            }

            IWorkflowRunStore reopened = CreateStore(storeKind, storePath);
            var snapshot = Assert.IsType<RunSnapshot>(reopened.Get(created.Header.RunId));
            Assert.Equal("completed", snapshot.Status);
            Assert.DoesNotContain(snapshot.Events, evt => evt.Data?.Contains(privateName, StringComparison.Ordinal) == true);
            Assert.DoesNotContain(snapshot.Events, evt => evt.Data?.Contains(privateLabel, StringComparison.Ordinal) == true);
            Assert.All(snapshot.Events.Where(evt =>
                evt.NodeId == "tool" && evt.Kind is ExecutionEventKinds.NodeStarted or ExecutionEventKinds.NodeCompleted), AssertRedacted);
            Assert.All(snapshot.Events.Where(evt => evt.Kind == ExecutionEventKinds.DataEvaluated), AssertRedacted);

            var playbackHtml = await RenderPlayback(snapshot);
            Assert.Contains("Execution evidence", playbackHtml, StringComparison.Ordinal);
            Assert.DoesNotContain(privateName, playbackHtml, StringComparison.Ordinal);
            Assert.DoesNotContain(privateLabel, playbackHtml, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    private static IWorkflowRunStore CreateStore(string storeKind, string path) => storeKind == "sqlite"
        ? new SqliteWorkflowRunStore(path)
        : new FileRunJournal(path);

    private static async Task<string> RenderPlayback(RunSnapshot snapshot)
    {
        using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        return await renderer.Dispatcher.InvokeAsync(async () =>
            (await renderer.RenderComponentAsync<WorkflowRunViewer>(ParameterView.FromDictionary(
                new Dictionary<string, object?> { [nameof(WorkflowRunViewer.Snapshot)] = snapshot }))).ToHtmlString());
    }

    [Theory]
    [InlineData("1.37")]
    [InlineData("2.0")]
    public async Task Schema_1_37_builds_tool_arguments_from_typed_input_pins(string schemaVersion)
    {
        var broker = new RecordingBroker();
        var result = await new WorkflowRunner(broker).ExecuteWithOutcomeAsync(Guid.NewGuid(),
            WorkflowRevision.Create(PinDefinition() with { SchemaVersion = schemaVersion }, "test"), "ignored", null,
            _ => ValueTask.CompletedTask);

        Assert.False(result.Failed);
        using var arguments = JsonDocument.Parse(broker.Input!);
        Assert.Equal("README.md", arguments.RootElement.GetProperty("path").GetString());
        Assert.Equal(50000, arguments.RootElement.GetProperty("maxBytes").GetInt64());
    }

    [Fact]
    public async Task Schema_1_40_emits_number_mcp_arguments_as_json_numbers()
    {
        var broker = new RecordingBroker();
        var result = await new WorkflowRunner(broker).ExecuteWithOutcomeAsync(Guid.NewGuid(),
            WorkflowRevision.Create(NumberPinDefinition(), "test"), "ignored", null,
            _ => ValueTask.CompletedTask);

        Assert.False(result.Failed);
        using var arguments = JsonDocument.Parse(broker.Input!);
        Assert.Equal(JsonValueKind.Number, arguments.RootElement.GetProperty("amount").ValueKind);
        Assert.Equal(12.5d, arguments.RootElement.GetProperty("amount").GetDouble());
    }

    [Fact]
    public async Task Schema_1_41_resolves_a_number_parameter_into_a_persisted_mcp_pin()
    {
        var broker = new RecordingBroker();
        var events = new List<ExecutionEvent>();
        var result = await new WorkflowRunner(broker).ExecuteWithOutcomeAsync(Guid.NewGuid(),
            WorkflowRevision.Create(NumberParameterPinDefinition(), "test"), "ignored",
            new Dictionary<string, string> { ["amount"] = "-0" },
            evt => { events.Add(evt); return ValueTask.CompletedTask; });

        Assert.False(result.Failed);
        using var arguments = JsonDocument.Parse(broker.Input!);
        Assert.Equal(JsonValueKind.Number, arguments.RootElement.GetProperty("amount").ValueKind);
        Assert.True(double.IsNegative(arguments.RootElement.GetProperty("amount").GetDouble()));
        var evidence = Assert.Single(events, evt => evt.Kind == ExecutionEventKinds.DataEvaluated && evt.NodeId == "amount");
        Assert.Contains("\"dataType\":\"number\"", evidence.Data, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Schema_1_42_evaluates_number_addition_before_building_mcp_arguments()
    {
        var broker = new RecordingBroker();
        var events = new List<ExecutionEvent>();
        var result = await new WorkflowRunner(broker).ExecuteWithOutcomeAsync(Guid.NewGuid(),
            WorkflowRevision.Create(NumberAdditionPinDefinition(), "test"), "ignored", null,
            evt => { events.Add(evt); return ValueTask.CompletedTask; });

        Assert.False(result.Failed);
        using var arguments = JsonDocument.Parse(broker.Input!);
        Assert.Equal(5.75d, arguments.RootElement.GetProperty("amount").GetDouble());
        var sumEvidence = Assert.Single(events, evt => evt.Kind == ExecutionEventKinds.DataEvaluated && evt.NodeId == "sum");
        using var recordedSum = JsonDocument.Parse(sumEvidence.Data!);
        Assert.Equal("redacted", recordedSum.RootElement.GetProperty("payloadState").GetString());
        Assert.Equal("number", recordedSum.RootElement.GetProperty("dataType").GetString());
    }

    private static WorkflowDefinition Definition() => new(
        WorkflowSchemaVersions.DirectMcpGraph, "direct-mcp-runtime", "input",
        new Dictionary<string, NodeDefinition>
        {
            ["input"] = new(NodeTypes.Input, "1.0.0"),
            ["tool"] = new(NodeTypes.McpTool, "1.0.0", new ToolBindingRequest("demo.echo", new string('a', 64))),
            ["output"] = new(NodeTypes.Output, "1.0.0")
        }.ToImmutableSortedDictionary(StringComparer.Ordinal),
        new Dictionary<string, ConnectionDefinition>
        {
            ["tool-input"] = new(new("input", "out"), new("tool", "in"), "data"),
            ["output-value"] = new(new("tool", "out"), new("output", "in"), "data")
        }.ToImmutableSortedDictionary(StringComparer.Ordinal))
    {
        ExecutionConnections = new Dictionary<string, ConnectionDefinition>
        {
            ["activate-tool"] = new(new("input", "then"), new("tool", "execute"), "execution"),
            ["complete"] = new(new("tool", "then"), new("output", "execute"), "execution")
        }.ToImmutableSortedDictionary(StringComparer.Ordinal)
    };

    private static void AssertRedacted(ExecutionEvent evt)
    {
        using var evidence = JsonDocument.Parse(evt.Data!);
        Assert.Equal("redacted", evidence.RootElement.GetProperty("payloadState").GetString());
        var size = evidence.RootElement.TryGetProperty("valueCharacters", out var valueCharacters)
            ? valueCharacters.GetInt32()
            : evidence.RootElement.TryGetProperty("outputCharacters", out var outputCharacters)
                ? outputCharacters.GetInt32()
                : evidence.RootElement.GetProperty("inputCharacters").GetInt32();
        Assert.True(size >= 0);
        Assert.DoesNotContain("hello", evt.Data, StringComparison.Ordinal);
        Assert.DoesNotContain("tool-result", evt.Data, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Unified_approval_captures_typed_arguments_and_dispatches_the_approved_snapshot()
    {
        var baseline = PinDefinition();
        var definition = baseline with
        {
            SchemaVersion = WorkflowSchemaVersions.UnifiedWorkflowGraph,
            Nodes = baseline.Nodes.Add("approval", new(NodeTypes.Approval, "1.0.0") { Approval = new("Approve read") }),
            Connections = baseline.Connections.Add("approval-input", new(new("input", "out"), new("approval", "in"), "data")),
            ExecutionConnections = baseline.ExecutionConnections!
                .SetItem("activate-tool", new(new("input", "then"), new("approval", "execute"), "execution"))
                .Add("approved-tool", new(new("approval", "then"), new("tool", "execute"), "execution"))
        };
        var broker = new RecordingBroker();
        var gate = new CapturingApprovalGate();
        var result = await new WorkflowRunner(broker).ExecuteControlledWithParametersAndOutcomeAsync(Guid.NewGuid(),
            WorkflowRevision.Create(definition, "test"), "caller data", null, _ => ValueTask.CompletedTask, gate);

        Assert.False(result.Failed);
        Assert.Equal("tool-result", result.Output);
        Assert.NotNull(gate.Action);
        Assert.Equal(gate.Action.Input, broker.Input);
        using var arguments = JsonDocument.Parse(gate.Action.Input);
        Assert.Equal("README.md", arguments.RootElement.GetProperty("path").GetString());
        Assert.Equal(50000, arguments.RootElement.GetProperty("maxBytes").GetInt64());
    }

    private sealed class CapturingApprovalGate : IWorkflowExecutionGate, IWorkflowApprovalGate
    {
        public WorkflowApprovalAction? Action { get; private set; }
        public ValueTask WaitForBoundaryAsync(string? nextNodeId, CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public ValueTask<WorkflowApprovalPermit> WaitForApprovalAsync(WorkflowApprovalAction action, string prompt,
            CancellationToken cancellationToken)
        {
            Action = action;
            return ValueTask.FromResult(new WorkflowApprovalPermit(Guid.NewGuid(), action.ComputeHash(), action.TargetInvocationId));
        }
    }

    private static WorkflowDefinition PinDefinition() => new(
        WorkflowSchemaVersions.DirectMcpPinsGraph, "direct-mcp-pins-runtime", "input",
        new Dictionary<string, NodeDefinition>
        {
            ["input"] = new(NodeTypes.Input, "1.0.0"),
            ["path"] = new(NodeTypes.StringConstant, "1.0.0") { StringConstant = new("README.md") },
            ["max"] = new(NodeTypes.IntegerConstant, "1.0.0") { IntegerConstant = new(50000) },
            ["tool"] = new(NodeTypes.McpTool, "1.0.0", new ToolBindingRequest("demo.read", new string('a', 64)))
            {
                McpInputPorts = [new("arg.path", "path", "string", true), new("arg.maxBytes", "maxBytes", "integer", false)]
            },
            ["output"] = new(NodeTypes.Output, "1.0.0")
        }.ToImmutableSortedDictionary(StringComparer.Ordinal),
        new Dictionary<string, ConnectionDefinition>
        {
            ["path"] = new(new("path", "out"), new("tool", "arg.path"), "data"),
            ["max"] = new(new("max", "out"), new("tool", "arg.maxBytes"), "data"),
            ["output-value"] = new(new("tool", "out"), new("output", "in"), "data")
        }.ToImmutableSortedDictionary(StringComparer.Ordinal))
    {
        ExecutionConnections = new Dictionary<string, ConnectionDefinition>
        {
            ["activate-tool"] = new(new("input", "then"), new("tool", "execute"), "execution"),
            ["complete"] = new(new("tool", "then"), new("output", "execute"), "execution")
        }.ToImmutableSortedDictionary(StringComparer.Ordinal)
    };

    private static WorkflowDefinition NumberPinDefinition() => new(
        WorkflowSchemaVersions.NumberGraph, "direct-mcp-number-runtime", "input",
        new Dictionary<string, NodeDefinition>
        {
            ["input"] = new(NodeTypes.Input, "1.0.0"),
            ["amount"] = new(NodeTypes.NumberConstant, "1.0.0") { NumberConstant = new(12.5d) },
            ["tool"] = new(NodeTypes.McpTool, "1.0.0", new ToolBindingRequest("demo.number", new string('a', 64)))
            {
                McpInputPorts = [new("arg.amount", "amount", WorkflowDataTypes.Number, true)]
            },
            ["output"] = new(NodeTypes.Output, "1.0.0")
        }.ToImmutableSortedDictionary(StringComparer.Ordinal),
        new Dictionary<string, ConnectionDefinition>
        {
            ["amount"] = new(new("amount", "out"), new("tool", "arg.amount"), "data"),
            ["output-value"] = new(new("tool", "out"), new("output", "in"), "data")
        }.ToImmutableSortedDictionary(StringComparer.Ordinal))
    {
        ExecutionConnections = new Dictionary<string, ConnectionDefinition>
        {
            ["activate-tool"] = new(new("input", "then"), new("tool", "execute"), "execution"),
            ["complete"] = new(new("tool", "then"), new("output", "execute"), "execution")
        }.ToImmutableSortedDictionary(StringComparer.Ordinal)
    };

    private static WorkflowDefinition NumberParameterPinDefinition() => NumberPinDefinition() with
    {
        SchemaVersion = WorkflowSchemaVersions.NumberPropagationGraph,
        Nodes = NumberPinDefinition().Nodes.SetItem("amount", new(NodeTypes.WorkflowValue, "1.0.0")
        {
            ParameterId = "amount", OutputDataType = WorkflowDataTypes.Number
        }),
        Parameters = ImmutableSortedDictionary<string, WorkflowParameterDefinition>.Empty
            .Add("amount", new WorkflowParameterDefinition("Amount", "12.5") { Type = "number" })
    };

    private static WorkflowDefinition NumberAdditionPinDefinition()
    {
        var baseline = NumberPinDefinition() with { SchemaVersion = WorkflowSchemaVersions.NumberArithmeticGraph };
        return baseline with
        {
            Nodes = baseline.Nodes
                .Remove("amount")
                .Add("left", new(NodeTypes.NumberConstant, "1.0.0") { NumberConstant = new(2.5d) })
                .Add("right", new(NodeTypes.NumberConstant, "1.0.0") { NumberConstant = new(3.25d) })
                .Add("sum", new(NodeTypes.NumberAdd, "1.0.0")),
            Connections = baseline.Connections
                .Remove("amount")
                .Add("left", new(new("left", "out"), new("sum", "left"), "data"))
                .Add("right", new(new("right", "out"), new("sum", "right"), "data"))
                .Add("amount", new(new("sum", "out"), new("tool", "arg.amount"), "data"))
        };
    }

    private sealed class RecordingBroker : INodeToolBroker
    {
        public string? Input { get; private set; }

        public Task<IReadOnlyList<ToolBindingDescriptor>> ListBindingsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ToolBindingDescriptor>>([]);

        public INodeToolScope OpenScope(NodeInvocationContext invocation) => new RecordingScope(this);

        private sealed class RecordingScope(RecordingBroker owner) : INodeToolScope
        {
            public Task<IReadOnlyList<ToolBindingDescriptor>> ListToolsAsync(CancellationToken cancellationToken = default) =>
                Task.FromResult<IReadOnlyList<ToolBindingDescriptor>>([]);

            public Task<string> InvokeAsync(string bindingId, string input, CancellationToken cancellationToken = default)
            {
                owner.Input = input;
                return Task.FromResult("tool-result");
            }

            public Task<string> InvokeJsonAsync(string bindingId, JsonElement arguments, CancellationToken cancellationToken = default)
            {
                owner.Input = arguments.GetRawText();
                return Task.FromResult("tool-result");
            }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
