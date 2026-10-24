# Runtime providers and capability boundaries

The Studio remains a local development host. These registration points are for trusted host code; workflow authors cannot supply executable commands, endpoint URLs, credentials, or factories through definitions.

## MCP bindings

`McpToolRegistration` maps a logical alias to a server/tool and a cancellation-aware `McpClient` connection factory. Registrations default to non-read-only and are denied by the initial broker; an administrator must explicitly designate supported read-only registrations and provide an `IToolAuthorizationPolicy`. Remote provisioning, credentials, side-effect approvals, and arbitrary argument schemas are not supplied by this prototype.

`ExplicitToolAuthorizationPolicy` accepts grants by workflow ID, node ID, and binding ID. A definition's tool requirements are checked independently and never grant authority. A production host still needs authenticated workspace/principal policy and trusted invocation identity; these local grant records are not an authentication system.

The built-in Studio launches its own known fixture entry point over stdio and registers three pure text tools. `--mcp-demo-server` is an internal stdio entry point, not a public HTTP endpoint or a mechanism for workflow-selected executables. Child environment inheritance is disabled. This protects against accidental credential propagation, but does not sandbox the trusted child process or an arbitrary replacement server.

Tool contracts currently have one string `text` input and text-only output. Definitions pin the actual tool name/input/output schema fingerprint returned during discovery. Schema fingerprints detect contract drift; they cannot freeze a remote implementation's behavior.

## Agent providers

`AgentProviderRegistration` pairs an `AgentProviderDescriptor` with a factory returning an owned `Microsoft.Extensions.AI.IChatClient`. The executor disposes that client pipeline after the invocation, so return a fresh client or an ownership-aware wrapper rather than an application-wide singleton that must remain alive.

Construct `AgentNodeExecutor` with the registrations and the same node tool broker used by direct MCP nodes. Register it as `IAgentNodeExecutor` for execution and `IAgentProviderCatalog` for authoring choices. `LocalWorkflowRuntime` accepts both interfaces through its constructor. The Studio's DI composition in `Program.cs` demonstrates this with the deterministic fixture.

A workflow stores provider ID/version, instructions, and explicit tool requirements. Host code owns model client construction, identity/authentication, model settings, network destinations, and credentials. Tool functions exposed to the agent are broker delegates; do not replace them with raw `McpClientTool` instances, which would bypass platform authorization and recording.

The deterministic provider calls the first assigned function once or returns `Demo agent: <input>`. Instructions are passed through the real agent API and captured but are intentionally not interpreted by this fixture. Provider descriptors and recorded lifecycle events label the fixture as deterministic. No external model provider is enabled automatically. The optional [OpenAI adapter](model-providers.md) now supplies host-configured factories with protocol-fixture verification; credentialed live-service qualification has not been run.

Production integrations must validate the chosen provider/package versions, cancellation behavior, persistence requirements, output constraints, sensitive-data handling, and authentication. Provider ID/version pins a host contract, not reproducible external model behavior. Historical playback continues to use saved evidence without provider or tool calls.
