using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.DataProtection;
using ModelContextProtocol.Client;
using VisualAgentWorkflow.Core;
using VisualAgentWorkflow.Mcp;

namespace VisualAgentWorkflow.Studio;

public enum StudioMcpTransport { Http, Stdio }

public sealed record StudioMcpConnectionDraft(string Id, string Name, string Folder, StudioMcpTransport Transport,
    string Endpoint, string BearerToken, string Command, IReadOnlyList<string> Arguments,
    string WorkingDirectory, IReadOnlyDictionary<string, string> EnvironmentVariables);

public sealed record StudioMcpServer(string Id, string Name, string Folder, string Endpoint,
    string ProtectedBearerToken, IReadOnlyList<string> EnabledTools)
{
    public StudioMcpTransport Transport { get; init; }
    public string Command { get; init; } = "";
    public IReadOnlyList<string> Arguments { get; init; } = [];
    public string WorkingDirectory { get; init; } = "";
    public IReadOnlyList<string> EnvironmentVariableNames { get; init; } = [];
    public string ProtectedEnvironmentVariables { get; init; } = "";
}

public sealed record StudioMcpServerView(string Id, string Name, string Folder, string Endpoint, bool HasToken,
    IReadOnlyList<string> EnabledTools)
{
    public StudioMcpTransport Transport { get; init; }
    public string Command { get; init; } = "";
    public IReadOnlyList<string> Arguments { get; init; } = [];
    public string WorkingDirectory { get; init; } = "";
    public IReadOnlyList<string> EnvironmentVariableNames { get; init; } = [];
}

public sealed record StudioMcpToolView(string Name, string BindingId, string ContractHash, string InputSummary,
    string Description, string DescriptionHash, bool IsSupported = true, string? RejectionReason = null);
public sealed record StudioMcpConnectionTestResult(int DiscoveredTools, int SupportedTools, int ExcludedTools,
    string? Error = null)
{
    public bool Succeeded => Error is null;
}

/// <summary>Operator-owned MCP connections. Only logical tool identities enter workflow definitions.</summary>
public sealed class StudioMcpLibrary
{
    private const int MaximumArgumentCount = 64;
    private const int MaximumArgumentLength = 4096;
    private const int MaximumEnvironmentVariables = 64;
    private const int MaximumEnvironmentValueLength = 8192;
    private const int ConnectionTimeoutSeconds = 30;
    private const int ProcessShutdownTimeoutSeconds = 3;
    private static readonly Regex EnvironmentName = new("^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.CultureInvariant);

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
        foreach (var server in servers)
            Validate(server.Id, server.Name, server.Folder, Draft(server, ""));
        Refresh();
    }

    public IReadOnlyList<StudioMcpServerView> List()
    {
        lock (gate) return servers.Select(server => new StudioMcpServerView(server.Id, server.Name, server.Folder,
            server.Endpoint, server.ProtectedBearerToken.Length > 0, server.EnabledTools.ToArray())
        {
            Transport = server.Transport,
            Command = server.Command,
            Arguments = server.Arguments.ToArray(),
            WorkingDirectory = server.WorkingDirectory,
            EnvironmentVariableNames = server.EnvironmentVariableNames.ToArray()
        }).ToArray();
    }

    public Task<IReadOnlyList<StudioMcpToolView>> DiscoverAsync(string endpoint, string bearerToken,
        string serverId, CancellationToken cancellationToken = default) =>
        DiscoverAsync(new(serverId, serverId, "", StudioMcpTransport.Http, endpoint, bearerToken, "", [], "",
            new Dictionary<string, string>()), cancellationToken);

    public async Task<IReadOnlyList<StudioMcpToolView>> DiscoverAsync(StudioMcpConnectionDraft draft,
        CancellationToken cancellationToken = default)
    {
        Validate(draft.Id, draft.Name, draft.Folder, draft);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(ConnectionTimeoutSeconds));
        await using var client = await Connect(draft, deadline.Token);
        var tools = await client.ListToolsAsync(cancellationToken: deadline.Token);
        return tools.Select(tool =>
        {
            var schemaReason = McpToolInputSchemas.GetUnsupportedReason(tool.JsonSchema);
            var descriptionReason = McpToolDescription.GetUnsupportedReason(tool.Description);
            var reason = string.Join(" ", new[]
            {
                schemaReason is null ? null : $"Schema: {schemaReason}",
                descriptionReason is null ? null : $"Description: {descriptionReason}"
            }.Where(value => value is not null));
            return new StudioMcpToolView(tool.Name, draft.Id + "." + tool.Name, ToolContractHash.Compute(tool),
                schemaReason is null ? McpToolInputSchemas.Summarize(tool.JsonSchema) : "Input schema not supported",
                tool.Description?.Trim() ?? "", descriptionReason is null ? McpToolDescription.ComputeHash(tool.Description) : "",
                reason.Length == 0, reason.Length == 0 ? null : reason);
        }).ToArray();
    }

    public async Task<StudioMcpConnectionTestResult> TestSavedConnectionAsync(string serverId,
        CancellationToken cancellationToken = default)
    {
        StudioMcpServer server;
        lock (gate)
            server = servers.SingleOrDefault(item => item.Id == serverId)
                ?? throw new ArgumentException("This MCP server is no longer configured.", nameof(serverId));

        var token = server.ProtectedBearerToken.Length == 0 ? "" : protector.Unprotect(server.ProtectedBearerToken);
        var draft = Draft(server, token) with { EnvironmentVariables = ReadEnvironmentVariables(server) };
        try
        {
            var tools = await DiscoverAsync(draft, cancellationToken);
            return new(tools.Count, tools.Count(tool => tool.IsSupported), tools.Count(tool => !tool.IsSupported));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new(0, 0, 0, "The MCP connection test timed out after 30 seconds. Check that the server starts promptly and responds to MCP initialization.");
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not OperationCanceledException)
        {
            return new(0, 0, 0, ConnectionFailureMessage(exception, draft.Transport));
        }
    }

    private static string ConnectionFailureMessage(Exception exception, StudioMcpTransport transport)
    {
        var current = exception;
        while (current.InnerException is not null) current = current.InnerException;
        return current switch
        {
            FileNotFoundException => "The MCP executable could not be found. Check the executable path and ensure it is installed.",
            DirectoryNotFoundException => "The MCP working directory could not be found. Check the configured directory.",
            System.ComponentModel.Win32Exception => "Studio could not start the MCP executable. Check that it exists and is runnable by the current user.",
            HttpRequestException { StatusCode: System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden } => "The MCP server rejected authentication. Check the bearer token and its permissions.",
            HttpRequestException => "Could not reach the MCP endpoint. Check its URL, network access, and server availability.",
            IOException when transport == StudioMcpTransport.Stdio => "Studio could not start the MCP executable or its process exited before tool discovery. Check the executable path, arguments, working directory, and server logs.",
            IOException => "The MCP connection ended unexpectedly. Check the endpoint and server logs.",
            TimeoutException => "The MCP server did not finish initialization or tool discovery before the timeout.",
            _ when current.GetType().Name.Contains("Protocol", StringComparison.OrdinalIgnoreCase) => "The server responded, but MCP initialization or tool discovery failed. Check protocol compatibility and server logs.",
            _ => "Could not connect, initialize MCP, or list tools. Check the executable or endpoint, credentials, and server logs."
        };
    }

    public void Save(string id, string name, string folder, string endpoint, string bearerToken,
        IReadOnlyList<string> enabledTools) =>
        Save(new(id, name, folder, StudioMcpTransport.Http, endpoint, bearerToken, "", [], "",
            new Dictionary<string, string>()), enabledTools);

    public void Save(StudioMcpConnectionDraft draft, IReadOnlyList<string> enabledTools)
    {
        Validate(draft.Id, draft.Name, draft.Folder, draft);
        if (enabledTools.Count > 64 || enabledTools.Any(tool => !ValidId(tool)) ||
            enabledTools.Distinct(StringComparer.Ordinal).Count() != enabledTools.Count)
            throw new ArgumentException("Choose up to 64 distinct discovered tools.");
        lock (gate)
        {
            var prior = servers.SingleOrDefault(server => server.Id == draft.Id);
            var environment = draft.Transport == StudioMcpTransport.Stdio
                ? ResolveEnvironmentVariables(draft, prior)
                : new Dictionary<string, string>(StringComparer.Ordinal);
            var protectedToken = draft.Transport == StudioMcpTransport.Http
                ? draft.BearerToken.Length > 0 ? protector.Protect(draft.BearerToken) : prior?.ProtectedBearerToken ?? ""
                : "";
            var protectedEnvironment = environment.Count == 0 ? "" :
                protector.Protect(JsonSerializer.Serialize(environment.OrderBy(item => item.Key, StringComparer.Ordinal)
                    .ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal)));
            var updatedServer = new StudioMcpServer(draft.Id, draft.Name, draft.Folder,
                draft.Transport == StudioMcpTransport.Http ? draft.Endpoint : "", protectedToken,
                enabledTools.ToArray())
            {
                Transport = draft.Transport,
                Command = draft.Transport == StudioMcpTransport.Stdio ? draft.Command : "",
                Arguments = draft.Transport == StudioMcpTransport.Stdio ? draft.Arguments.ToArray() : [],
                WorkingDirectory = draft.Transport == StudioMcpTransport.Stdio ? draft.WorkingDirectory : "",
                EnvironmentVariableNames = environment.Keys.Order(StringComparer.Ordinal).ToArray(),
                ProtectedEnvironmentVariables = protectedEnvironment
            };
            var updated = servers.Where(server => server.Id != draft.Id).Append(updatedServer).ToList();
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
        var configured = servers.SelectMany(server =>
        {
            Func<CancellationToken, Task<McpClient>> connect = token => Connect(server, token);
            return server.EnabledTools.Select(tool =>
                new McpToolRegistration(server.Id + "." + tool, server.Name + " · " + tool, server.Id, tool,
                    connect) { AllowInvocation = true });
        });
        var registrations = builtIn.Concat(configured).ToArray();
        broker.ReplaceRegistrations(registrations);
        policy.Replace(registrations.Select(registration => registration.BindingId));
    }

    private async Task<McpClient> Connect(StudioMcpServer server, CancellationToken cancellationToken)
    {
        var draft = Draft(server, server.ProtectedBearerToken.Length == 0
            ? "" : protector.Unprotect(server.ProtectedBearerToken)) with
        { EnvironmentVariables = ReadEnvironmentVariables(server) };
        return await Connect(draft, cancellationToken);
    }

    private async Task<McpClient> Connect(StudioMcpConnectionDraft draft, CancellationToken cancellationToken)
    {
        if (draft.Transport == StudioMcpTransport.Stdio)
        {
            var prior = FindServer(draft.Id);
            var environment = StdioClientTransportOptions.GetDefaultEnvironmentVariables();
            foreach (var pair in ResolveEnvironmentVariables(draft, prior)) environment[pair.Key] = pair.Value;
            return await CreateStdioClient(draft, environment, cancellationToken);
        }

        var saved = FindServer(draft.Id);
        var bearerToken = string.IsNullOrEmpty(draft.BearerToken) && saved is not null &&
                          !string.IsNullOrEmpty(saved.ProtectedBearerToken)
            ? protector.Unprotect(saved.ProtectedBearerToken)
            : draft.BearerToken;
        return await ConnectHttp(draft with { BearerToken = bearerToken }, cancellationToken);
    }

    private static Task<McpClient> CreateStdioClient(StudioMcpConnectionDraft draft,
        IReadOnlyDictionary<string, string?> environment, CancellationToken cancellationToken)
    {
        var transport = new StdioClientTransport(new StdioClientTransportOptions
        {
            Name = $"VAW local MCP · {draft.Id}",
            Command = draft.Command,
            Arguments = draft.Arguments.ToList(),
            WorkingDirectory = string.IsNullOrWhiteSpace(draft.WorkingDirectory) ? null : draft.WorkingDirectory,
            InheritEnvironmentVariables = false,
            EnvironmentVariables = environment.ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal),
            ShutdownTimeout = TimeSpan.FromSeconds(ProcessShutdownTimeoutSeconds)
        });
        // Prefer the current MCP protocol while retaining SDK fallback to initialize-capable
        // servers. Pinning 2025-11-25 here made newer servers fail with "initialize is not
        // available" after they moved to the 2026-07-28 discover/per-request-metadata protocol.
        return McpClient.CreateAsync(transport, new McpClientOptions
        {
            InitializationTimeout = TimeSpan.FromSeconds(ConnectionTimeoutSeconds)
        }, cancellationToken: cancellationToken);
    }

    private static async Task<McpClient> ConnectHttp(StudioMcpConnectionDraft draft, CancellationToken cancellationToken)
    {
        var options = new HttpClientTransportOptions
        {
            Endpoint = new Uri(draft.Endpoint), Name = draft.Name, TransportMode = HttpTransportMode.StreamableHttp,
            ConnectionTimeout = TimeSpan.FromSeconds(ConnectionTimeoutSeconds),
            AdditionalHeaders = string.IsNullOrEmpty(draft.BearerToken) ? null :
                new Dictionary<string, string> { ["Authorization"] = "Bearer " + draft.BearerToken }
        };
        return await McpClient.CreateAsync(new HttpClientTransport(options),
            new McpClientOptions { ProtocolVersion = "2025-11-25" }, cancellationToken: cancellationToken);
    }

    private IReadOnlyDictionary<string, string> ResolveEnvironmentVariables(StudioMcpConnectionDraft draft,
        StudioMcpServer? prior)
    {
        var existing = prior is null ? new Dictionary<string, string>(StringComparer.Ordinal) : ReadEnvironmentVariables(prior);
        return draft.EnvironmentVariables.ToDictionary(pair => pair.Key,
            pair => string.IsNullOrEmpty(pair.Value) && existing.TryGetValue(pair.Key, out var saved) ? saved : pair.Value,
            StringComparer.Ordinal);
    }

    private IReadOnlyDictionary<string, string> ReadEnvironmentVariables(StudioMcpServer server)
    {
        if (string.IsNullOrEmpty(server.ProtectedEnvironmentVariables)) return new Dictionary<string, string>(StringComparer.Ordinal);
        var json = protector.Unprotect(server.ProtectedEnvironmentVariables);
        return JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? new Dictionary<string, string>(StringComparer.Ordinal);
    }

    private StudioMcpServer? FindServer(string id)
    {
        lock (gate) return servers.SingleOrDefault(server => server.Id == id);
    }

    private static StudioMcpConnectionDraft Draft(StudioMcpServer server, string bearerToken) =>
        new(server.Id, server.Name, server.Folder, server.Transport, server.Endpoint, bearerToken,
            server.Command, server.Arguments, server.WorkingDirectory, new Dictionary<string, string>());

    private static void Validate(string id, string name, string folder, StudioMcpConnectionDraft connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        var commonInvalid = !ValidId(id) || string.IsNullOrWhiteSpace(name) || name.Length > 128 ||
            folder.Length > 256;
        if (commonInvalid) throw new ArgumentException("Use a safe server ID, name, and folder.");

        switch (connection.Transport)
        {
            case StudioMcpTransport.Http:
                if (!Uri.TryCreate(connection.Endpoint, UriKind.Absolute, out var uri) ||
                    uri.Scheme != Uri.UriSchemeHttps && (uri.Scheme != Uri.UriSchemeHttp || !uri.IsLoopback) ||
                    !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Fragment) ||
                    connection.BearerToken.Contains('\0'))
                    throw new ArgumentException("Use an HTTPS or loopback HTTP endpoint without user information or a fragment.");
                break;
            case StudioMcpTransport.Stdio:
                ValidateStdio(connection);
                break;
            default:
                throw new ArgumentException("Choose a supported MCP transport.");
        }
    }

    private static void ValidateStdio(StudioMcpConnectionDraft connection)
    {
        if (string.IsNullOrWhiteSpace(connection.Command) || connection.Command.Length > 1024 ||
            connection.Command.Contains('\0') || connection.Arguments is null ||
            connection.Arguments.Count > MaximumArgumentCount || connection.Arguments.Any(argument =>
                argument is null || argument.Length > MaximumArgumentLength || argument.Contains('\0')))
            throw new ArgumentException("Enter an executable and up to 64 arguments, each no longer than 4,096 characters.");
        if (!string.IsNullOrWhiteSpace(connection.WorkingDirectory) &&
            (!Path.IsPathFullyQualified(connection.WorkingDirectory) || !Directory.Exists(connection.WorkingDirectory)))
            throw new ArgumentException("The working directory must be an existing absolute path.");
        if (connection.EnvironmentVariables is null || connection.EnvironmentVariables.Count > MaximumEnvironmentVariables ||
            connection.EnvironmentVariables.Any(pair => !EnvironmentName.IsMatch(pair.Key) ||
                pair.Value is null || pair.Value.Length > MaximumEnvironmentValueLength || pair.Value.Contains('\0')) ||
            connection.EnvironmentVariables.Keys.Distinct(StringComparer.OrdinalIgnoreCase).Count() != connection.EnvironmentVariables.Count)
            throw new ArgumentException("Use up to 64 unique environment names with values no longer than 8,192 characters.");
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
