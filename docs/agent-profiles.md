# Agent profiles and bounded delegation

Studio provides an **Agent Designer** at `/agents` for creating, organizing, versioning, and deleting local agent-profile revisions. A profile pins a host-configured provider and version, system instructions, optional reasoning/output-token settings, selected MCP tool contracts, and exact text-skill snapshots. Saving changed behavior creates another immutable version; moving a profile between folders changes only local organization.

In the workflow designer, select an agent node and assign the exact profiles it may call under **Subagents**. The profile revision and its skill snapshots are embedded in the workflow. Profiles do not contain credentials, server endpoints, or host grants. Exported packages list provider, MCP tool, profile, and skill requirements, but a consuming application must register those dependencies and explicitly authorize MCP tools itself.

At runtime, assigned profiles appear to the parent model as bounded `delegate` tools. The parent supplies a task string and receives a result string. The child uses its profile's instructions, provider, skills, reasoning/output settings, and tool list. A child does not inherit the parent's tools. Each requested child MCP tool must also be allowed by the host policy for the parent workflow node; failure to resolve or authorize a required tool fails that delegation closed.

The initial delegation implementation is deliberately bounded:

- A parent may expose at most four profiles and make at most four delegation calls during its node invocation.
- Delegation depth is one level; profiles cannot delegate further.
- Each task is limited to 16,384 characters. Agent calls retain the existing provider timeout and model-round limits.
- A profile's optional output-token limit applies to that child agent. There is no aggregate token budget across the parent and children yet.
- Child model, tool, start, completion, and failure events retain the same run/revision and use a distinct child invocation ID. Event data identifies the exact profile revision and links child activity to the parent invocation. Playback reads this evidence and makes no live calls.
- Individual delegation remains sequential. Shared channels, persistent agent memory, workflow-as-tool invocation, automatic retries, and model-selected tool-call approval are outside the current scope.

When an agent node has two or more assigned profiles, its parent also receives a **parallel team** tool. One task is sent to every assigned profile concurrently, up to four members. The team returns a JSON array in assignment order, with each member's profile ID, version, name, and output. Individual delegation and team calls share the four-child-execution limit for that parent invocation. If one member fails, the team cancels remaining members where possible, records a failed team event, and returns no partial aggregate; the parent model may decide how to proceed from the tool error. Completed MCP side effects are not undone. The combined result is limited to 80,000 characters. Team members do not communicate with each other during this fan-out; run-scoped channels and manager-directed rounds are the next coordination increment.

The local Agent Designer library is part of the unauthenticated development Studio and is stored in its data directory. It is not a multi-user asset service. Consumer hosts do not need this local library to execute a package because each used profile revision is embedded in the workflow definition.

See the [user guide](user-guide.md#design-reusable-subagents) for the Studio workflow and [architecture](architecture.md#versioned-agent-profiles) for runtime boundaries.

The design research compared Microsoft's [sub-workflow](https://github.com/microsoft/agent-framework/blob/67ba500ec1fd312606a5d585a42335cd8631e851/dotnet/samples/03-workflows/_StartHere/05_SubWorkflows/Program.cs), [workflow-as-agent](https://github.com/microsoft/agent-framework/blob/67ba500ec1fd312606a5d585a42335cd8631e851/dotnet/samples/03-workflows/Agents/WorkflowAsAnAgent/Program.cs), and [registered-agent handoff](https://github.com/microsoft/agent-framework/blob/67ba500ec1fd312606a5d585a42335cd8631e851/dotnet/samples/03-workflows/Orchestration/Handoff/Program.cs) samples with the OpenAI Agents SDK's [agents-as-tools example](https://github.com/openai/openai-agents-python/blob/0dc4cf152bff1f50bdc0e41f28808563406545b2/examples/agent_patterns/agents_as_tools.py). VAW uses the explicit specialist-as-tool shape for this first increment; it does not take a dependency on the OpenAI SDK.
