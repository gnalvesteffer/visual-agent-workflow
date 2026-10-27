using System.Text.RegularExpressions;
using ModelContextProtocol.Client;
using VisualAgentWorkflow.Core;

namespace VisualAgentWorkflow.Mcp;

/// <summary>Host-owned settings for one digest-pinned, no-network OCI MCP worker.</summary>
public sealed record ContainerMcpServerOptions(string ImageReference)
{
    public string DockerExecutable { get; init; } = "docker";
    public IReadOnlyList<string> Arguments { get; init; } = [];
    /// <summary>Additional node-assigned host tool bindings this worker may request through the broker.</summary>
    public IReadOnlyList<string> CapabilityBindingIds { get; init; } = [];
    /// <summary>Allows a known server-declared failure to be fed back to the model once for correction.</summary>
    public bool AllowModelRecoveryAfterFailure { get; init; }
    /// <summary>Identity and SHA-256 pin for an extension assembly baked into the worker image.</summary>
    public WorkflowExtensionArtifact? ExtensionArtifact { get; init; }
    public bool AllowInvocation { get; init; }
}

/// <summary>Operator-approved extension identity. The worker verifies the assembly bytes against this digest.</summary>
public sealed record WorkflowExtensionArtifact(string Id, string Version, string Sha256);

/// <summary>Creates a host registration for an MCP tool running in a constrained disposable container.</summary>
public static partial class ContainerMcpToolRegistration
{
    private const string WorkerProtocolVersion = "2025-11-25";
    private const int MaximumArguments = 32;
    private const int MaximumArgumentLength = 4096;

    /// <summary>
    /// Creates a registration whose worker has no network, host mounts, inherited environment, or writable
    /// root filesystem. The host must separately grant this binding to each workflow node.
    /// </summary>
    public static McpToolRegistration Create(string bindingId, string displayName, string serverId, string toolName,
        ContainerMcpServerOptions options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bindingId);
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        ArgumentException.ThrowIfNullOrWhiteSpace(serverId);
        ArgumentException.ThrowIfNullOrWhiteSpace(toolName);
        ArgumentNullException.ThrowIfNull(options);

        ValidateOptions(options);
        var capabilityBindingIds = options.CapabilityBindingIds.ToArray();
        if (capabilityBindingIds.Contains(bindingId, StringComparer.Ordinal))
            throw new ArgumentException("A worker cannot grant itself as a capability.", nameof(options));
        var workerArguments = BuildWorkerArguments(options);
        var arguments = BuildDockerArguments(options, workerArguments).ToArray();

        async Task<McpClient> Connect(CancellationToken cancellationToken)
        {
            var transport = new StdioClientTransport(new()
            {
                Name = $"VAW isolated MCP worker · {serverId}",
                Command = options.DockerExecutable,
                Arguments = arguments,
                InheritEnvironmentVariables = false
            });
            var clientOptions = new McpClientOptions { ProtocolVersion = WorkerProtocolVersion };
            return await McpClient.CreateAsync(transport, clientOptions, cancellationToken: cancellationToken);
        }

        async Task<McpToolConnection> ConnectInScope(NodeInvocationContext invocation, CancellationToken cancellationToken)
        {
            var selected = (invocation.ToolRequirements ?? [])
                .Where(requirement => capabilityBindingIds.Contains(requirement.BindingId, StringComparer.Ordinal))
                .ToArray();
            if (selected.Length == 0) return new(await Connect(cancellationToken));
            if (invocation.BrokeredCapabilityDepth != 0)
                throw new ToolAccessDeniedException();

            var session = McpCapabilitySocketSession.Create(invocation, selected, cancellationToken);
            try
            {
                var workerArguments = arguments.ToList();
                var imageIndex = workerArguments.IndexOf(options.ImageReference);
                workerArguments.Insert(imageIndex, "--mount");
                workerArguments.Insert(imageIndex + 1,
                    $"type=bind,source={session.MountSource},target=/run/vaw-capabilities,readonly");
                var transport = new StdioClientTransport(new()
                {
                    Name = $"VAW isolated MCP worker · {serverId}",
                    Command = options.DockerExecutable,
                    Arguments = workerArguments,
                    InheritEnvironmentVariables = false
                });
                var clientOptions = new McpClientOptions { ProtocolVersion = WorkerProtocolVersion };
                var client = await McpClient.CreateAsync(transport, clientOptions, cancellationToken: cancellationToken);
                return new(client, session);
            }
            catch
            {
                await session.DisposeAsync();
                throw;
            }
        }

        return new(bindingId, displayName, serverId, toolName, Connect)
        {
            AllowInvocation = options.AllowInvocation,
            AllowModelRecoveryAfterFailure = options.AllowModelRecoveryAfterFailure,
            ConnectInScope = ConnectInScope
        };
    }

    internal static IReadOnlyList<string> BuildDockerArguments(ContainerMcpServerOptions options) =>
        BuildDockerArguments(options, options.Arguments);

    private static IReadOnlyList<string> BuildDockerArguments(ContainerMcpServerOptions options,
        IReadOnlyList<string> workerArguments)
    {
        var arguments = new List<string>
        {
            "run", "--rm", "--interactive", "--pull=never", "--network=none", "--read-only",
            "--cap-drop=ALL", "--security-opt=no-new-privileges=true", "--pids-limit=64",
            "--memory=256m", "--memory-swap=256m", "--cpus=1", "--user=65532:65532",
            "--ulimit=nofile=256:256", "--log-driver=none",
            "--env", "HOME=/tmp", "--env", "DOTNET_CLI_HOME=/tmp/.dotnet",
            "--env", "DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1", "--env", "DOTNET_NOLOGO=1",
            "--tmpfs", "/tmp:rw,noexec,nosuid,nodev,size=16m",
        };
        arguments.Add(options.ImageReference);
        arguments.AddRange(workerArguments);
        return arguments;
    }

    private static IReadOnlyList<string> BuildWorkerArguments(ContainerMcpServerOptions options)
    {
        var arguments = options.Arguments.ToList();
        if (options.ExtensionArtifact is not { } extension) return arguments;
        arguments.Add("--extension-id");
        arguments.Add(extension.Id);
        arguments.Add("--extension-version");
        arguments.Add(extension.Version);
        arguments.Add("--extension-sha256");
        arguments.Add(extension.Sha256);
        return arguments;
    }

    private static void ValidateOptions(ContainerMcpServerOptions options)
    {
        if (!IsDigestPinned(options.ImageReference))
            throw new ArgumentException("Container MCP images must use an immutable sha256 digest.", nameof(options));
        if (string.IsNullOrWhiteSpace(options.DockerExecutable) || options.DockerExecutable.Contains('\0'))
            throw new ArgumentException("A Docker executable is required.", nameof(options));
        if (options.Arguments is null || options.Arguments.Count > MaximumArguments || options.Arguments.Any(argument =>
                argument is null || argument.Length > MaximumArgumentLength || argument.Contains('\0')))
            throw new ArgumentException("Container MCP arguments exceed the allowed count or length.", nameof(options));
        if (options.CapabilityBindingIds is null || options.CapabilityBindingIds.Count > 8 ||
            options.CapabilityBindingIds.Any(string.IsNullOrWhiteSpace) ||
            options.CapabilityBindingIds.Distinct(StringComparer.Ordinal).Count() != options.CapabilityBindingIds.Count)
            throw new ArgumentException("Worker capability bindings must be unique and limited to eight entries.", nameof(options));
        if (options.ExtensionArtifact is { } extension &&
            (!ExtensionIdRegex().IsMatch(extension.Id) || !ExtensionVersionRegex().IsMatch(extension.Version) ||
             !ImageIdRegex().IsMatch($"sha256:{extension.Sha256}")))
            throw new ArgumentException("Extension ID, semantic version, or SHA-256 digest is invalid.", nameof(options));
        if (options.ExtensionArtifact is not null && options.Arguments.Count > 0)
            throw new ArgumentException("The extension worker image does not accept additional process arguments.", nameof(options));
    }

    private static bool IsDigestPinned(string? imageReference) => imageReference is not null &&
        (ImageIdRegex().IsMatch(imageReference) || RegistryDigestRegex().IsMatch(imageReference));

    [GeneratedRegex("^sha256:[0-9a-f]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex ImageIdRegex();

    [GeneratedRegex("^[A-Za-z0-9._:/-]+@sha256:[0-9a-f]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex RegistryDigestRegex();

    [GeneratedRegex("^[a-z0-9][a-z0-9._-]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex ExtensionIdRegex();

    [GeneratedRegex("^[0-9]+\\.[0-9]+\\.[0-9]+(?:-[A-Za-z0-9.-]+)?$", RegexOptions.CultureInvariant)]
    private static partial Regex ExtensionVersionRegex();
}
