using System.Runtime.InteropServices;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using VisualAgentWorkflow.Blazor;
using VisualAgentWorkflow.Core;
using VisualAgentWorkflow.Mcp;
using VisualAgentWorkflow.Studio;
using Xunit;

namespace VisualAgentWorkflow.Mcp.Tests;

[Collection(McpStdioCollection.Name)]
public sealed class StudioStdioMcpTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "vaw-stdio-mcp-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task StdioConnectionDiscoversAndInvokesAssignedToolAndProtectsEnvironmentValues()
    {
        var services = new ServiceCollection();
        services.AddDataProtection();
        using var serviceProvider = services.BuildServiceProvider();
        var protection = serviceProvider.GetRequiredService<IDataProtectionProvider>();
        var policy = new StudioToolPolicy();
        var broker = new McpToolBroker([], policy, TimeSpan.FromSeconds(20));
        var library = new StudioMcpLibrary(directory, protection, broker, policy, []);
        var draft = new StudioMcpConnectionDraft("stdio-demo", "Stdio demo", "Tests", StudioMcpTransport.Stdio,
            "", "", DotnetExecutable(), [StudioExecutable(), "--mcp-demo-server"], "",
            new Dictionary<string, string> { ["VAW_TEST_SECRET"] = "never-write-this-to-disk" });

        var discovered = await library.DiscoverAsync(draft);
        var reverse = Assert.Single(discovered, tool => tool.Name == "reverse");
        Assert.True(reverse.IsSupported);

        library.Save(draft, ["reverse"]);
        var storedSettings = File.ReadAllText(Path.Combine(directory, "mcp-servers.json"));
        Assert.DoesNotContain("never-write-this-to-disk", storedSettings, StringComparison.Ordinal);
        Assert.Contains("VAW_TEST_SECRET", Assert.Single(library.List()).EnvironmentVariableNames);
        Assert.Single(await broker.ListBindingsAsync());

        var definition = WorkflowTemplates.Create("uppercase");
        definition = WorkflowEditing.ReplaceNode(definition, "step-1", new(NodeTypes.McpTool, "1.0.0",
            new ToolBindingRequest("stdio-demo.reverse", reverse.ContractHash)
            { DescriptionHash = reverse.DescriptionHash }));
        var revision = WorkflowRevision.Create(definition, "stdio integration");
        var invocation = new NodeInvocationContext(Guid.NewGuid(), revision, "step-1", Guid.NewGuid(),
            (_, _) => ValueTask.CompletedTask);
        await using var scope = broker.OpenScope(invocation);

        Assert.Equal("owt olleh", await scope.InvokeAsync("stdio-demo.reverse", "hello two"));
    }

    public void Dispose()
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }

    private static string DotnetExecutable()
    {
        var runtime = new DirectoryInfo(RuntimeEnvironment.GetRuntimeDirectory());
        var dotnetRoot = runtime.Parent!.Parent!.Parent!;
        return Path.Combine(dotnetRoot.FullName, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
    }

    private static string StudioExecutable()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "VisualAgentWorkflow.slnx")))
            root = root.Parent;
        if (root is null) throw new InvalidOperationException("Cannot locate the Studio fixture executable.");
        var configuration = Directory.GetParent(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar))!.Name;
        return Path.Combine(root.FullName, "src", "VisualAgentWorkflow.Studio", "bin", configuration,
            "net10.0", "VisualAgentWorkflow.Studio.dll");
    }
}
