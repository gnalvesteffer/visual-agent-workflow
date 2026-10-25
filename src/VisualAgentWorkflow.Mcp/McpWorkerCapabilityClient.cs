using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text.Json;
using VisualAgentWorkflow.Core;

namespace VisualAgentWorkflow.Mcp;

/// <summary>Worker-side client for the host-brokered MCP capabilities mounted into a Linux container worker.</summary>
public sealed class McpWorkerCapabilityClient(string socketPath = "/run/vaw-capabilities/capabilities.sock") : IWorkflowExtensionContext
{
    public const string DefaultSocketPath = "/run/vaw-capabilities/capabilities.sock";
    private const int ProtocolVersion = 1;
    private const int MaximumFrameBytes = 65_536;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<IReadOnlyList<ToolBindingDescriptor>> ListToolsAsync(CancellationToken cancellationToken = default)
    {
        var response = await SendAsync(new(ProtocolVersion, Guid.NewGuid(), "list", null, null), cancellationToken);
        return response.Tools ?? throw new ToolInvocationException("The host returned no capability list.");
    }

    public async Task<string> InvokeJsonAsync(string bindingId, JsonElement arguments, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bindingId);
        if (arguments.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("Capability arguments must be a JSON object.", nameof(arguments));
        var response = await SendAsync(new(ProtocolVersion, Guid.NewGuid(), "invoke", bindingId, arguments.Clone()), cancellationToken);
        return response.Result ?? throw new ToolInvocationException("The host returned no capability result.");
    }

    public Task<string> InvokeMcpToolAsync(string bindingId, JsonElement arguments, CancellationToken cancellationToken = default) =>
        InvokeJsonAsync(bindingId, arguments, cancellationToken);

    private async Task<CapabilityResponse> SendAsync(CapabilityRequest request, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsLinux())
            throw new PlatformNotSupportedException("Brokered container capabilities currently require a Linux worker.");

        using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        await socket.ConnectAsync(new UnixDomainSocketEndPoint(socketPath), cancellationToken);
        await using var stream = new NetworkStream(socket, ownsSocket: false);
        await WriteFrameAsync(stream, JsonSerializer.SerializeToUtf8Bytes(request, JsonOptions), cancellationToken);
        var payload = await ReadFrameAsync(stream, cancellationToken);
        var response = JsonSerializer.Deserialize<CapabilityResponse>(payload, JsonOptions)
            ?? throw new ToolInvocationException("The host returned an invalid capability response.");
        if (response.ProtocolVersion != ProtocolVersion || response.RequestId != request.RequestId)
            throw new ToolInvocationException("The host capability response did not match the request.");
        if (response.Error is { } error) throw new ToolInvocationException(error);
        return response;
    }

    internal static async Task WriteFrameAsync(Stream stream, byte[] payload, CancellationToken cancellationToken)
    {
        if (payload.Length is <= 0 or > MaximumFrameBytes)
            throw new ToolInvocationException("Capability message exceeds the protocol limit.");
        var header = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32BigEndian(header, payload.Length);
        await stream.WriteAsync(header, cancellationToken);
        await stream.WriteAsync(payload, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    internal static async Task<byte[]> ReadFrameAsync(Stream stream, CancellationToken cancellationToken)
    {
        var header = new byte[sizeof(int)];
        await stream.ReadExactlyAsync(header, cancellationToken);
        var length = BinaryPrimitives.ReadInt32BigEndian(header);
        if (length is <= 0 or > MaximumFrameBytes)
            throw new ToolInvocationException("Capability message exceeds the protocol limit.");
        var payload = new byte[length];
        await stream.ReadExactlyAsync(payload, cancellationToken);
        return payload;
    }

    internal sealed record CapabilityRequest(int ProtocolVersion, Guid RequestId, string Operation,
        string? BindingId, JsonElement? Arguments);

    internal sealed record CapabilityResponse(int ProtocolVersion, Guid RequestId, string? Result,
        IReadOnlyList<ToolBindingDescriptor>? Tools, string? Error);
}

internal sealed class McpCapabilitySocketSession : IAsyncDisposable
{
    private readonly Socket listener;
    private readonly string directory;
    private readonly INodeToolScope scope;
    private readonly CancellationTokenSource lifetime;
    private readonly Task acceptLoop;
    private int requests;

    private McpCapabilitySocketSession(Socket listener, string directory, INodeToolScope scope,
        CancellationToken cancellationToken)
    {
        this.listener = listener;
        this.directory = directory;
        this.scope = scope;
        lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        acceptLoop = AcceptLoopAsync(lifetime.Token);
    }

    public string MountSource => directory;

    public static McpCapabilitySocketSession Create(NodeInvocationContext invocation,
        IReadOnlyList<ToolBindingRequest> allowedRequirements, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsLinux())
            throw new PlatformNotSupportedException("Brokered container capabilities currently require a Linux worker.");
        if (invocation.BrokeredCapabilityDepth != 0)
            throw new ToolAccessDeniedException();
        if (invocation.OpenToolScope is null)
            throw new ToolInvocationException("The host broker does not expose a worker capability channel.");

        var narrowedInvocation = invocation with
        {
            ToolRequirements = allowedRequirements,
            BrokeredCapabilityDepth = invocation.BrokeredCapabilityDepth + 1
        };
        var scope = invocation.OpenToolScope(narrowedInvocation);
        var directory = Path.Combine(Path.GetTempPath(), $"vaw-cap-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(directory);
            File.SetUnixFileMode(directory, UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
            var path = Path.Combine(directory, "capabilities.sock");
            var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            listener.Bind(new UnixDomainSocketEndPoint(path));
            listener.Listen(backlog: 8);
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite |
                                      UnixFileMode.GroupRead | UnixFileMode.GroupWrite |
                                      UnixFileMode.OtherRead | UnixFileMode.OtherWrite);
            return new(listener, directory, scope, cancellationToken);
        }
        catch
        {
            scope.DisposeAsync().AsTask().GetAwaiter().GetResult();
            try { File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); }
            catch (IOException) { }
            try { Directory.Delete(directory, recursive: true); } catch (IOException) { }
            throw;
        }
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                using var client = await listener.AcceptAsync(cancellationToken);
                await using var stream = new NetworkStream(client, ownsSocket: false);
                var requestId = Guid.Empty;
                try
                {
                    var request = JsonSerializer.Deserialize<McpWorkerCapabilityClient.CapabilityRequest>(
                        await McpWorkerCapabilityClient.ReadFrameAsync(stream, cancellationToken),
                        new JsonSerializerOptions(JsonSerializerDefaults.Web));
                    if (request is null || request.ProtocolVersion != 1 || request.RequestId == Guid.Empty)
                        throw new ToolInvocationException("Invalid worker capability request.");
                    requestId = request.RequestId;
                    var response = await DispatchAsync(request, cancellationToken);
                    await WriteResponseAsync(stream, request.RequestId, response.Result, response.Tools, response.Error, cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
                catch (Exception error)
                {
                    try
                    {
                        await WriteResponseAsync(stream, requestId, null, null,
                            error is ToolAccessDeniedException ? "Capability denied by host policy." : "Capability request failed.", cancellationToken);
                    }
                    catch (Exception writeError) when (writeError is IOException or SocketException or OperationCanceledException) { }
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested) { }
        catch (SocketException) when (cancellationToken.IsCancellationRequested) { }
    }

    private async Task<McpWorkerCapabilityClient.CapabilityResponse> DispatchAsync(
        McpWorkerCapabilityClient.CapabilityRequest request, CancellationToken cancellationToken)
    {
        if (Interlocked.Increment(ref requests) > 16)
            return new(1, request.RequestId, null, null, "Worker capability request budget exceeded.");
        return request.Operation switch
        {
            "list" => new(1, request.RequestId, null, await scope.ListToolsAsync(cancellationToken), null),
            "invoke" when request.BindingId is { Length: > 0 } bindingId && request.Arguments is { } arguments &&
                           arguments.ValueKind == JsonValueKind.Object => new(1, request.RequestId,
                await scope.InvokeJsonAsync(bindingId, arguments, cancellationToken), null, null),
            _ => new(1, request.RequestId, null, null, "Invalid capability request.")
        };
    }

    private static Task WriteResponseAsync(Stream stream, Guid requestId, string? result,
        IReadOnlyList<ToolBindingDescriptor>? tools, string? error, CancellationToken cancellationToken)
    {
        var response = new McpWorkerCapabilityClient.CapabilityResponse(1, requestId, result, tools, error);
        return McpWorkerCapabilityClient.WriteFrameAsync(stream,
            JsonSerializer.SerializeToUtf8Bytes(response, new JsonSerializerOptions(JsonSerializerDefaults.Web)), cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        await lifetime.CancelAsync();
        listener.Dispose();
        try { await acceptLoop; }
        catch (Exception error) when (error is OperationCanceledException or IOException or SocketException) { }
        await scope.DisposeAsync();
        lifetime.Dispose();
        if (OperatingSystem.IsLinux())
        {
            try { File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
        try { Directory.Delete(directory, recursive: true); } catch (IOException) { }
    }
}
