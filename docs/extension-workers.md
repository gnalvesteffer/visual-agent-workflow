# Containerized MCP extension workers

This increment lets a consumer host register an MCP tool implemented by a digest-pinned container image. Workflow execution continues through the normal node-scoped `INodeToolBroker`; definitions and packages contain only logical tool IDs and contract hashes. Host registration remains outside the workflow artifact, and selecting a tool in a workflow never installs or launches an image by itself.

```mermaid
flowchart LR
    Workflow["Pinned workflow node"] --> Broker["Host MCP broker\nnode request ∩ host grant"]
    Broker -->|"MCP stdio"| Docker["Host Docker Engine"]
    Docker --> Worker["Pinned image\nnon-root, no network, read-only root"]
    Worker -->|"MCP result"| Broker
    Worker -. "optional Unix socket capability request" .-> Capability["Node-scoped MCP broker"]
    Broker --> Audit["Sequenced tool audit events"]
    Worker -. "No filesystem, secrets, or network capability" .-> Denied["Denied by container boundary"]
```

## Host configuration

The current container MCP adapter negotiates protocol `2025-11-25`, which the worker image must support. Pinning both ends avoids relying on the MCP SDK's moving default protocol version.

Register each tool with its logical binding ID and an immutable container image identity:

```csharp
var options = new ContainerMcpServerOptions(
    "ghcr.io/example/number-tools@sha256:<64-lowercase-hex-digest>")
{
    AllowInvocation = true,
    CapabilityBindingIds = ["math.add"]
};

var add = ContainerMcpToolRegistration.Create(
    "custom.add", "Number tools · add", "number-tools", "add_integers", options);
var broker = new McpToolBroker([add], hostToolPolicy);
services.AddSingleton<INodeToolBroker>(broker);
```

The worker can request one of its explicitly configured host capabilities through the worker-side client:

```csharp
using var arguments = JsonDocument.Parse("{\"left\":20,\"right\":22}");
var result = await new McpWorkerCapabilityClient()
    .InvokeJsonAsync("math.add", arguments.RootElement, cancellationToken);
```

Capability IDs are the intersection of the host registration's `CapabilityBindingIds` and the active node's pinned tool requirements. `ListToolsAsync` returns only bindings currently allowed by the host policy. Every request goes through the regular broker again, which rechecks the current registration, pinned schema/description, node grant, input/output bounds, timeout and audit writer. Calls are bound to the active run/revision/node/invocation by a host-created scope; the worker cannot supply or change those identities. At most eight capability IDs can be configured, and capability delegation is limited to one level to prevent cycles and unbounded recursive calls. A worker does not receive endpoint credentials.

`AllowInvocation` opts the registered service into discovery/dispatch. It does not grant a workflow node access. The workflow must still declare the binding and the host's `IToolAuthorizationPolicy` must grant that exact workflow/node/binding tuple. MCP schema and contract checks remain in `McpToolBroker`. Studio's library supports Streamable HTTP and trusted local stdio connections; container registrations remain host-code configuration in this increment.

Hosts may also set `AllowModelRecoveryAfterFailure` when a server-declared error is known to be safe for one model correction attempt. For adapters that surface a definitive post-dispatch exception instead of an MCP error result, the registration may additionally set `IsDefinitiveToolFailure` to a narrow, trusted classifier. The classifier must reject cancellation, timeout, transport, authorization, contract, and budget failures. Recovery is opt-in per registration; it never authorizes an automatic retry of an uncertain external effect.

The Docker CLI launches a fresh disposable container per connection with `--network=none`, a read-only root filesystem, no inherited environment, all Linux capabilities dropped, `no-new-privileges`, non-root UID/GID 65532, 256 MiB memory/no swap, one CPU, 64 processes, 256 open files, and a 16 MiB no-exec temporary filesystem. The file-descriptor cap is 256 because 128 caused the .NET MCP server to fail while generating tool schemas; the Linux integration smoke confirms discovery and direct invocation at 256. By default there are no host mounts. If brokered capabilities are configured, the host mounts one read-only directory containing only a private Unix socket; that socket accepts bounded requests and exposes no filesystem or network access. Docker image pulls are disabled for invocation. The host should pull/review the image through its deployment process before enabling the binding. The MCP broker's invocation timeout cancels the channel and disposes the worker process.

The worker may perform deterministic computation, use its private temporary space, and request only separately assigned MCP capabilities from the host broker. It cannot call arbitrary HTTP APIs, read host files, or access host credentials through this registration. A worker can only use a tool if it is both listed in `CapabilityBindingIds`, pinned as a requirement on the active node, and currently permitted by the host's node grant. Chained worker capabilities are denied; a worker cannot call another capability-enabled worker. Audit evidence records each nested MCP call under the active workflow invocation and playback remains read-only.

## Trust boundary and limitations

This is process/container isolation, not a promise that arbitrary hostile native code is safe. The Docker Engine, container runtime, Linux kernel, pinned image contents, host administrator, and all users of the host must remain trusted. The temporary capability socket is opened to the worker's UID through a world-connectable Unix socket so the fixed worker UID can use its assigned tools; a host process that learns the per-invocation socket path can also connect and use the same narrowed live broker scope. The randomized directory name is not authentication, and execute-only directory mode is not access control. Images must be reviewed; use a dedicated worker host/identity where possible, keep the runtime patched, and do not treat shared-kernel containers as a mutually untrusted multi-tenant boundary. Multi-user production hosts require an authenticated peer-identity design with a verified UID mapping or another private capability transport. A consumer that cannot meet this host trust model should keep extension code in an independently operated MCP service or disable the feature.

The worker is an MCP server executable baked into the image. An operator may also pin one .NET extension assembly in that image; the sample worker verifies its SHA-256 hash and version before loading it inside the container. The engine and Studio never load the assembly. Direct assembly upload, dynamic discovery from untrusted registries, a user-facing container catalog, Windows-native isolation, per-workspace identity, health pools, and durable retries remain unsupported. Brokered Unix-socket capabilities and extension loading currently require a local Linux Docker Engine; arbitrary remote daemon mounts and Windows workers are not supported. See [isolated .NET extensions](extension-assemblies.md) for the contract and sample.

## Verification

`samples/IsolatedMcpServer` exposes deterministic addition, an isolated extension loader, and a boundary probe. `samples/ExtensionMathPlugin` builds an extension with typed MCP operations; `python3 scripts/container_mcp_smoke.py` publishes both fixtures, pins the extension SHA-256, builds the Docker image from a pinned ASP.NET runtime digest, and invokes the operations through the real MCP broker. The probe asserts that host secrets and paths are absent, direct network access and writes to the image root are denied, the bounded temporary filesystem remains writable, and an extension can call its one assigned nested MCP capability. The Linux integration workflow runs this check. Ordinary Windows/Linux unit suites still compile and exercise the socket protocol and artifact validation; Docker runtime containment is Linux-integration-only today. The host creates the capability socket atomically in a private 0700 directory, binds it before narrowing the directory to traversal-only, sets the socket mode for the fixed worker UID, and mounts the directory read-only. Both the host container client and sample worker pin MCP protocol `2025-11-25` to keep their initialize handshakes aligned. The managed Linux development environment passed the complete image smoke, including nested capability invocation/audit and the host-secret/path, root-write, temporary-write, and network-isolation probes. This qualifies the tested local Linux Docker configuration; it does not change the documented trusted-host-user requirement or establish a hostile multi-tenant boundary.
