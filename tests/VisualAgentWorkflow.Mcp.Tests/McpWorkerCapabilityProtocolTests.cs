using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text.Json;
using VisualAgentWorkflow.Core;
using VisualAgentWorkflow.Mcp;
using Xunit;

namespace VisualAgentWorkflow.Mcp.Tests;

public sealed class McpWorkerCapabilityProtocolTests
{
    [UnixSocketFact]
    public async Task WorkerClientListsHostAuthorizedBindingsOverUnixSocket()
    {
        using var directory = new TemporaryDirectory();
        var socketPath = Path.Combine(directory.Path, "capabilities.sock");
        using var listener = Listen(socketPath);
        var host = RespondOnceAsync(listener, request =>
        {
            Assert.Equal("list", request.GetProperty("operation").GetString());
            return new
            {
                protocolVersion = 1,
                requestId = request.GetProperty("requestId").GetGuid(),
                tools = new[]
                {
                    new ToolBindingDescriptor("math.add", "Math · add", "math", "add", "contract")
                    {
                        InputSchemaJson = "{\"type\":\"object\"}",
                        ToolDescription = "Add two numbers",
                        InputSummary = "left, right"
                    }
                },
                result = (string?)null,
                error = (string?)null
            };
        });

        var tools = await new McpWorkerCapabilityClient(socketPath).ListToolsAsync();
        await host;

        var tool = Assert.Single(tools);
        Assert.Equal("math.add", tool.BindingId);
        Assert.Equal("contract", tool.ContractHash);
    }

    [UnixSocketFact]
    public async Task WorkerClientSendsTypedArgumentsAndCorrelatesResponse()
    {
        using var directory = new TemporaryDirectory();
        var socketPath = Path.Combine(directory.Path, "capabilities.sock");
        using var listener = Listen(socketPath);
        var host = RespondOnceAsync(listener, request =>
        {
            Assert.Equal("invoke", request.GetProperty("operation").GetString());
            Assert.Equal("math.add", request.GetProperty("bindingId").GetString());
            Assert.Equal(20, request.GetProperty("arguments").GetProperty("left").GetInt32());
            Assert.Equal(22, request.GetProperty("arguments").GetProperty("right").GetInt32());
            return new
            {
                protocolVersion = 1,
                requestId = request.GetProperty("requestId").GetGuid(),
                tools = (object?)null,
                result = "42",
                error = (string?)null
            };
        });

        using var arguments = JsonDocument.Parse("""{"left":20,"right":22}""");
        var result = await new McpWorkerCapabilityClient(socketPath)
            .InvokeJsonAsync("math.add", arguments.RootElement);
        await host;

        Assert.Equal("42", result);
    }

    private static Socket Listen(string socketPath)
    {
        var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        listener.Bind(new UnixDomainSocketEndPoint(socketPath));
        listener.Listen(1);
        return listener;
    }

    private static async Task RespondOnceAsync(Socket listener, Func<JsonElement, object> createResponse)
    {
        using var client = await listener.AcceptAsync();
        await using var stream = new NetworkStream(client, ownsSocket: false);
        var header = new byte[sizeof(int)];
        await stream.ReadExactlyAsync(header);
        var length = BinaryPrimitives.ReadInt32BigEndian(header);
        Assert.InRange(length, 1, 65_536);
        var payload = new byte[length];
        await stream.ReadExactlyAsync(payload);
        using var request = JsonDocument.Parse(payload);
        var response = JsonSerializer.SerializeToUtf8Bytes(createResponse(request.RootElement), new JsonSerializerOptions(JsonSerializerDefaults.Web));
        BinaryPrimitives.WriteInt32BigEndian(header, response.Length);
        await stream.WriteAsync(header);
        await stream.WriteAsync(response);
        await stream.FlushAsync();
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"vaw-protocol-{Guid.NewGuid():N}");

        public TemporaryDirectory() => Directory.CreateDirectory(Path);

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }

    private sealed class UnixSocketFactAttribute : FactAttribute
    {
        public UnixSocketFactAttribute()
        {
            if (!OperatingSystem.IsLinux()) Skip = "The brokered worker protocol uses Linux Unix-domain sockets.";
        }
    }
}
