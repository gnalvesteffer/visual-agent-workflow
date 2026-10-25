using System.Text.Json;
using VisualAgentWorkflow.Blazor;
using VisualAgentWorkflow.Core;
using VisualAgentWorkflow.Mcp;
using Xunit;

namespace VisualAgentWorkflow.Mcp.Tests;

[Collection(McpStdioCollection.Name)]
public sealed class ContainerMcpSandboxTests
{
    [ContainerSandboxFact]
    [Trait("Category", "ContainerSandbox")]
    public async Task ContainerizedMcpRunsThroughNodeScopedBrokerWithNetworkAndHostAccessBlocked()
    {
        var image = Environment.GetEnvironmentVariable("VAW_CONTAINER_MCP_IMAGE")!;
        var extensionDigest = Environment.GetEnvironmentVariable("VAW_CONTAINER_MCP_EXTENSION_SHA256")!;
        var options = new ContainerMcpServerOptions(image) { AllowInvocation = true };
        var delegatedOptions = options with { CapabilityBindingIds = ["sandbox.add"] };
        var extension = new WorkflowExtensionArtifact("sample.math", "1.0.0", extensionDigest);
        var extensionOptions = options with { ExtensionArtifact = extension };
        var delegatedExtensionOptions = extensionOptions with { CapabilityBindingIds = ["sandbox.add"] };
        var registrations = new[]
        {
            ContainerMcpToolRegistration.Create("sandbox.add", "Sandbox · add", "isolated-mcp", "add_integers", options),
            ContainerMcpToolRegistration.Create("sandbox.delegate", "Sandbox · delegate add", "isolated-mcp", "delegate_add", delegatedOptions),
            ContainerMcpToolRegistration.Create("sandbox.extension.add", "Extension · add", "isolated-extension", "sample.math.add", extensionOptions),
            ContainerMcpToolRegistration.Create("sandbox.extension.host-add", "Extension · host add", "isolated-extension", "sample.math.add_via_host", delegatedExtensionOptions),
            ContainerMcpToolRegistration.Create("sandbox.inspect", "Sandbox · inspect", "isolated-mcp", "inspect_isolation", options)
        };
        var allowed = new ExplicitToolAuthorizationPolicy([
            new("container-mcp-test", "step-1", "sandbox.add"),
            new("container-mcp-test", "step-1", "sandbox.delegate"),
            new("container-mcp-test", "step-1", "sandbox.extension.add"),
            new("container-mcp-test", "step-1", "sandbox.extension.host-add"),
            new("container-mcp-test", "step-1", "sandbox.inspect")
        ]);
        var broker = new McpToolBroker(registrations, allowed, TimeSpan.FromSeconds(20));
        var tools = await broker.ListBindingsAsync();
        var delegated = tools.Single(tool => tool.BindingId == "sandbox.extension.host-add");
        var definition = PipelineEditing.ReplaceNode(PipelineTemplates.Create("uppercase") with { WorkflowId = "container-mcp-test" },
            "step-1", new(NodeTypes.McpTool, "1.0.0", new(delegated.BindingId, delegated.ContractHash)));
        var revision = WorkflowRevision.Create(definition, "container-sandbox-test");
        var audit = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var requirements = tools.Select(tool => new ToolBindingRequest(tool.BindingId, tool.ContractHash)
        {
            DescriptionHash = tool.DescriptionHash
        }).ToArray();

        await using var scope = broker.OpenScope(new(Guid.NewGuid(), revision, "step-1", Guid.NewGuid(),
            (_, data) => { if (data is not null) audit.Enqueue(data); return ValueTask.CompletedTask; })
        {
            ToolRequirements = requirements
        });
        using var arguments = JsonDocument.Parse("""{"left":20,"right":22}""");
        Assert.Equal("42", await scope.InvokeJsonAsync("sandbox.extension.add", arguments.RootElement));
        Assert.Equal("42", await scope.InvokeJsonAsync(delegated.BindingId, arguments.RootElement));
        Assert.Contains(audit, entry => entry.Contains("sandbox.add", StringComparison.Ordinal));

        var inspection = tools.Single(tool => tool.BindingId == "sandbox.inspect");
        definition = PipelineEditing.ReplaceNode(definition, "step-1", new(NodeTypes.McpTool, "1.0.0",
            new(inspection.BindingId, inspection.ContractHash)));
        revision = WorkflowRevision.Create(definition, "container-sandbox-test");
        await using var inspectionScope = broker.OpenScope(new(Guid.NewGuid(), revision, "step-1", Guid.NewGuid(), (_, _) => ValueTask.CompletedTask));
        using var emptyArguments = JsonDocument.Parse("{}");
        using var report = JsonDocument.Parse(await inspectionScope.InvokeJsonAsync(inspection.BindingId, emptyArguments.RootElement));
        Assert.False(report.RootElement.GetProperty("hostSecretPresent").GetBoolean());
        Assert.False(report.RootElement.GetProperty("hostPathVisible").GetBoolean());
        Assert.True(report.RootElement.GetProperty("rootFileWriteDenied").GetBoolean());
        Assert.True(report.RootElement.GetProperty("temporaryWriteAllowed").GetBoolean());
        Assert.False(report.RootElement.GetProperty("networkReachable").GetBoolean());
    }

    [Fact]
    [Trait("Category", "ContainerSandbox")]
    public void ContainerRegistrationRequiresImmutableImageIdentityAndBoundsArguments()
    {
        Assert.Throws<ArgumentException>(() => ContainerMcpToolRegistration.Create("tool", "Tool", "server", "tool",
            new("registry.example/server:latest")));
        Assert.Throws<ArgumentException>(() => ContainerMcpToolRegistration.Create("tool", "Tool", "server", "tool",
            new("registry.example/server@sha256:" + new string('a', 64))
            {
                Arguments = Enumerable.Repeat("argument", 33).ToArray()
            }));
        Assert.Throws<ArgumentException>(() => ContainerMcpToolRegistration.Create("tool", "Tool", "server", "tool",
            new("registry.example/server@sha256:" + new string('a', 64))
            {
                CapabilityBindingIds = Enumerable.Repeat("capability", 9).ToArray()
            }));
        Assert.Throws<ArgumentException>(() => ContainerMcpToolRegistration.Create("self", "Tool", "server", "tool",
            new("registry.example/server@sha256:" + new string('a', 64))
            {
                CapabilityBindingIds = ["self"]
            }));
        Assert.Throws<ArgumentException>(() => ContainerMcpToolRegistration.Create("extension", "Extension", "server", "tool",
            new("registry.example/server@sha256:" + new string('a', 64))
            {
                ExtensionArtifact = new("sample.math", "1.0.0", new string('a', 64)),
                Arguments = ["--unreviewed-option"]
            }));
    }
}

public sealed class ContainerSandboxFactAttribute : FactAttribute
{
    public ContainerSandboxFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("VAW_CONTAINER_MCP_IMAGE")))
            Skip = "Run scripts/container_mcp_smoke.py to build a disposable sandbox image and exercise container isolation.";
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("VAW_CONTAINER_MCP_EXTENSION_SHA256")))
            Skip = "Run scripts/container_mcp_smoke.py to build and hash the isolated extension assembly fixture.";
    }
}
