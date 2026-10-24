using System.ComponentModel;
using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;
using ModelContextProtocol.Server;
using VisualAgentWorkflow.Core;

namespace VisualAgentWorkflow.Mcp;

/// <summary>Known pure-text development fixture, not a user-provided executable or sandbox.</summary>
public static class DemoMcpServer
{
    public static async Task RunAsync()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddMcpServer().WithStdioServerTransport().WithTools<TextTools>();
        await builder.Build().RunAsync();
    }

    public static IReadOnlyList<McpToolRegistration> Registrations(string command, IReadOnlyList<string> arguments)
    {
        async Task<McpClient> Connect(CancellationToken cancellationToken)
        {
            var runtimeRoot = Directory.GetParent(RuntimeEnvironment.GetRuntimeDirectory().TrimEnd(Path.DirectorySeparatorChar))!.Parent!.Parent!.FullName;
            var transport = new StdioClientTransport(new()
            {
                Name = "Local text MCP fixture", Command = command, Arguments = [.. arguments],
                InheritEnvironmentVariables = false,
                EnvironmentVariables = new Dictionary<string, string?> { ["DOTNET_ROOT"] = runtimeRoot }
            });
            return await McpClient.CreateAsync(transport, cancellationToken: cancellationToken);
        }
        return
        [
            new("demo.uppercase", "MCP · uppercase", "demo-text", "uppercase", Connect, ReadOnly: true),
            new("demo.reverse", "MCP · reverse", "demo-text", "reverse", Connect, ReadOnly: true)
        ];
    }

    public static IReadOnlyList<McpToolRegistration> ForCurrentExecutable()
    {
        var command = Environment.ProcessPath ?? throw new InvalidOperationException("Cannot identify the host executable.");
        string[] arguments = Path.GetFileNameWithoutExtension(command).Equals("dotnet", StringComparison.OrdinalIgnoreCase)
            ? [Assembly.GetEntryAssembly()!.Location, "--mcp-demo-server"] : ["--mcp-demo-server"];
        return Registrations(command, arguments);
    }

    private sealed class TextTools
    {
        [McpServerTool(Name = "uppercase", ReadOnly = true, Destructive = false), Description("Converts text to uppercase without external I/O.")]
        public static string Uppercase(string text) => text.ToUpperInvariant();

        [McpServerTool(Name = "reverse", ReadOnly = true, Destructive = false), Description("Reverses Unicode scalar values without external I/O.")]
        public static string Reverse(string text) => string.Concat(text.EnumerateRunes().Reverse());
    }
}

/// <summary>Development-only host grants for the two built-in pure tools. All other bindings are denied.</summary>
public sealed class DemoToolPolicy : IToolAuthorizationPolicy
{
    public bool IsAllowed(WorkflowRevision revision, string nodeId, string bindingId) =>
        bindingId is "demo.uppercase" or "demo.reverse";
}
