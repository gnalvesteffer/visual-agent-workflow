using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using ModelContextProtocol.Client;
using VisualAgentWorkflow.Core;
using VisualAgentWorkflow.Mcp;

namespace VisualAgentWorkflow.Studio;

public sealed record StudioMcpServer(string Id, string Name, string Folder, string Endpoint,
    string ProtectedBearerToken, IReadOnlyList<string> EnabledTools);
public sealed record StudioMcpServerView(string Id, string Name, string Folder, string Endpoint, bool HasToken,
    IReadOnlyList<string> EnabledTools);
public sealed record StudioMcpToolView(string Name, string BindingId, string ContractHash);

/// <summary>Operator-owned Studio connections. Only logical tool identities enter workflow definitions.</summary>
public sealed class StudioMcpLibrary
{
    private readonly object gate = new();
    private readonly string path;
    private readonly IDataProtector protector;
    private readonly McpToolBroker broker;
    private readonly StudioToolPolicy policy;
    private readonly IReadOnlyList<McpToolRegistration> builtIn;
    private List<StudioMcpServer> servers;

    public StudioMcpLibrary(string directory, IDataProtectionProvider protection, McpToolBroker broker,
        StudioToolPolicy policy, IReadOnlyList<McpToolRegistration> builtIn)
    {
        Directory.CreateDirectory(directory);
        path = Path.Combine(directory, "mcp-servers.json");
        protector = protection.CreateProtector("VisualAgentWorkflow.Studio.McpLibrary.v1");
        this.broker = broker;
        this.policy = policy;
        this.builtIn = builtIn;
        servers = File.Exists(path) ? JsonSerializer.Deserialize<List<StudioMcpServer>>(File.ReadAllText(path)) ?? [] : [];
        foreach (var server in servers) Validate(server.Id, server.Name, server.Folder, server.Endpoint);
        Refresh();
    }

    public IReadOnlyList<StudioMcpServerView> List()
    {
        lock (gate) return servers.Select(server => new StudioMcpServerView(server.Id, server.Name, server.Folder,
            server.Endpoint, server.ProtectedBearerToken.Length > 0, server.EnabledTools.ToArray())).ToArray();
    }

    public async Task<IReadOnlyList<StudioMcpToolView>> DiscoverAsync(string endpoint, string bearerToken,
        string serverId, CancellationToken cancellationToken = default)
    {
        Validate(serverId, serverId, "", endpoint);
        if (bearerToken.Length == 0)
        {
            lock (gate)
            {
                var stored = servers.SingleOrDefault(server => server.Id == serverId);
                if (stored is not null && stored.ProtectedBearerToken.Length > 0)
                    bearerToken = protector.Unprotect(stored.ProtectedBearerToken);
            }
        }
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        await using var client = await Connect(endpoint, bearerToken, serverId, deadline.Token);
        var tools = await client.ListToolsAsync(cancellationToken: deadline.Token);
        return tools.Where(tool => IsSupported(tool.JsonSchema)).Select(tool =>
            new StudioMcpToolView(tool.Name, serverId + "." + tool.Name, ToolContractHash.Compute(tool))).ToArray();
    }

    public void Save(string id, string name, string folder, string endpoint, string bearerToken, IReadOnlyList<string> enabledTools)
    {
        Validate(id, name, folder, endpoint);
        if (enabledTools.Count > 64 || enabledTools.Any(tool => !ValidId(tool)) || enabledTools.Distinct(StringComparer.Ordinal).Count() != enabledTools.Count)
            throw new ArgumentException("Choose up to 64 distinct discovered tools.");
        lock (gate)
        {
            var prior = servers.SingleOrDefault(server => server.Id == id);
            var protectedToken = bearerToken.Length > 0 ? protector.Protect(bearerToken) : prior?.ProtectedBearerToken ?? "";
            var updated = servers.Where(server => server.Id != id).Append(new(id, name, folder, endpoint, protectedToken, enabledTools.ToArray())).ToList();
            Persist(updated);
        }
    }

    public bool Delete(string id)
    {
        lock (gate)
        {
            var updated = servers.Where(server => server.Id != id).ToList();
            if (updated.Count == servers.Count) return false;
            Persist(updated);
            return true;
        }
    }

    private void Persist(List<StudioMcpServer> updated)
    {
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(updated));
        File.Move(temporary, path, true);
        servers = updated;
        Refresh();
    }

    private void Refresh()
    {
        var configured = servers.SelectMany(server => server.EnabledTools.Select(tool =>
            new McpToolRegistration(server.Id + "." + tool, server.Name + " · " + tool, server.Id, tool,
                token => Connect(server.Endpoint, server.ProtectedBearerToken.Length == 0 ? "" : protector.Unprotect(server.ProtectedBearerToken), server.Id, token))
            { AllowInvocation = true }));
        var registrations = builtIn.Concat(configured).ToArray();
        broker.ReplaceRegistrations(registrations);
        policy.Replace(registrations.Select(registration => registration.BindingId));
    }

    private static async Task<McpClient> Connect(string endpoint, string token, string name, CancellationToken cancellationToken)
    {
        var options = new HttpClientTransportOptions
        {
            Endpoint = new Uri(endpoint), Name = name, TransportMode = HttpTransportMode.StreamableHttp,
            ConnectionTimeout = TimeSpan.FromSeconds(10),
            AdditionalHeaders = string.IsNullOrEmpty(token) ? null : new Dictionary<string, string> { ["Authorization"] = "Bearer " + token }
        };
        return await McpClient.CreateAsync(new HttpClientTransport(options),
            new McpClientOptions { ProtocolVersion = "2025-11-25" }, cancellationToken: cancellationToken);
    }

    private static bool IsSupported(JsonElement schema) =>
        schema.TryGetProperty("properties", out var properties) && properties.EnumerateObject().Count() == 1 &&
        properties.TryGetProperty("text", out var text) && text.TryGetProperty("type", out var type) && type.GetString() == "string";

    private static void Validate(string id, string name, string folder, string endpoint)
    {
        if (!ValidId(id) || string.IsNullOrWhiteSpace(name) || name.Length > 128 || folder.Length > 256 ||
            !Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps && (uri.Scheme != Uri.UriSchemeHttp || !uri.IsLoopback) ||
            !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Fragment))
            throw new ArgumentException("Use a safe server ID, name, and an HTTPS or loopback HTTP endpoint.");
    }

    private static bool ValidId(string value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 64 &&
        value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.');
}

public sealed class StudioToolPolicy : IToolAuthorizationPolicy
{
    private IReadOnlySet<string> bindings = new HashSet<string>(StringComparer.Ordinal);
    public void Replace(IEnumerable<string> allowed) => Volatile.Write(ref bindings, allowed.ToHashSet(StringComparer.Ordinal));
    public bool IsAllowed(WorkflowRevision revision, string nodeId, string bindingId) =>
        Volatile.Read(ref bindings).Contains(bindingId);
}
