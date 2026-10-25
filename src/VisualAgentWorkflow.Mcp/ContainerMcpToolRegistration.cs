using System.Text.RegularExpressions;
using ModelContextProtocol.Client;
using VisualAgentWorkflow.Core;

namespace VisualAgentWorkflow.Mcp;

/// <summary>Host-owned settings for one digest-pinned, no-network OCI MCP worker.</summary>
public sealed record ContainerMcpServerOptions(string ImageReference)
{
    public string DockerExecutable { get; init; } = "docker";
    public IReadOnlyList<string> Arguments { get; init; } = [];
    public bool AllowInvocation { get; init; }
}

/// <summary>Creates a host registration for an MCP tool running in a constrained disposable container.</summary>
public static partial class ContainerMcpToolRegistration
{
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
        var arguments = BuildDockerArguments(options).ToArray();

        async Task<McpClient> Connect(CancellationToken cancellationToken)
        {
            var transport = new StdioClientTransport(new()
            {
                Name = $"VAW isolated MCP worker · {serverId}",
                Command = options.DockerExecutable,
                Arguments = arguments,
                InheritEnvironmentVariables = false
            });
            return await McpClient.CreateAsync(transport, cancellationToken: cancellationToken);
        }

        return new(bindingId, displayName, serverId, toolName, Connect)
        {
            AllowInvocation = options.AllowInvocation
        };
    }

    internal static IReadOnlyList<string> BuildDockerArguments(ContainerMcpServerOptions options)
    {
        var arguments = new List<string>
        {
            "run", "--rm", "--interactive", "--pull=never", "--network=none", "--read-only",
            "--cap-drop=ALL", "--security-opt=no-new-privileges=true", "--pids-limit=64",
            "--memory=256m", "--memory-swap=256m", "--cpus=1", "--user=65532:65532",
            "--ulimit=nofile=128:128", "--log-driver=none",
            "--tmpfs", "/tmp:rw,noexec,nosuid,nodev,size=16m"
        };
        arguments.Add(options.ImageReference);
        arguments.AddRange(options.Arguments);
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
    }

    private static bool IsDigestPinned(string? imageReference) => imageReference is not null &&
        (ImageIdRegex().IsMatch(imageReference) || RegistryDigestRegex().IsMatch(imageReference));

    [GeneratedRegex("^sha256:[0-9a-f]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex ImageIdRegex();

    [GeneratedRegex("^[A-Za-z0-9._:/-]+@sha256:[0-9a-f]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex RegistryDigestRegex();
}
