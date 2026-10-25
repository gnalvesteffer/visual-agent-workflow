using System.ComponentModel;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using VisualAgentWorkflow.Mcp;

var builder = Host.CreateApplicationBuilder();
builder.Logging.ClearProviders();
builder.Services.AddMcpServer().WithStdioServerTransport().WithTools<IsolationProbeTools>();
await builder.Build().RunAsync();

internal sealed class IsolationProbeTools
{
    [McpServerTool(Name = "add_integers", ReadOnly = true, Destructive = false),
     Description("Adds two integers deterministically without external I/O.")]
    public static int AddIntegers(int left, int right) => checked(left + right);

    [McpServerTool(Name = "delegate_add", ReadOnly = true, Destructive = false),
     Description("Adds two integers by requesting the separately assigned sandbox.add host capability.")]
    public static async Task<string> DelegateAdd(int left, int right, CancellationToken cancellationToken)
    {
        var capabilities = await new McpWorkerCapabilityClient().ListToolsAsync(cancellationToken);
        if (capabilities.Count != 1 || capabilities[0].BindingId != "sandbox.add")
            throw new InvalidOperationException("The host did not assign the sandbox.add capability.");
        using var arguments = JsonDocument.Parse(JsonSerializer.Serialize(new { left, right }));
        return await new McpWorkerCapabilityClient().InvokeJsonAsync("sandbox.add", arguments.RootElement, cancellationToken);
    }

    [McpServerTool(Name = "inspect_isolation", ReadOnly = true, Destructive = false),
     Description("Reports whether the worker can access host state, direct network, and its ephemeral temp directory.")]
    public static async Task<string> InspectIsolation(CancellationToken cancellationToken)
    {
        var rootFileWriteDenied = await IsDenied(() => File.WriteAllTextAsync("/app/probe.txt", "probe", cancellationToken));
        var hostPathVisible = Directory.Exists("/workspace") || Directory.Exists("/host");
        var temporaryWriteAllowed = false;
        var temporaryPath = Path.Combine(Path.GetTempPath(), $"vaw-{Guid.NewGuid():N}.tmp");
        try
        {
            await File.WriteAllTextAsync(temporaryPath, "ephemeral", cancellationToken);
            temporaryWriteAllowed = true;
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }

        var networkReachable = await CanReachExternalNetwork(cancellationToken);
        var report = new IsolationReport(
            HostSecretPresent: Environment.GetEnvironmentVariable("VAW_CONTAINER_MCP_HOST_SECRET") is not null,
            HostPathVisible: hostPathVisible,
            RootFileWriteDenied: rootFileWriteDenied,
            TemporaryWriteAllowed: temporaryWriteAllowed,
            NetworkReachable: networkReachable);
        return JsonSerializer.Serialize(report);
    }

    private static async Task<bool> IsDenied(Func<Task> write)
    {
        try
        {
            await write();
            return false;
        }
        catch (IOException) { return true; }
        catch (UnauthorizedAccessException) { return true; }
    }

    private static async Task<bool> CanReachExternalNetwork(CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(1));
        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync("1.1.1.1", 53, deadline.Token);
            return true;
        }
        catch (SocketException) { return false; }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return false; }
    }

    private sealed record IsolationReport(bool HostSecretPresent, bool HostPathVisible, bool RootFileWriteDenied,
        bool TemporaryWriteAllowed, bool NetworkReachable);
}
