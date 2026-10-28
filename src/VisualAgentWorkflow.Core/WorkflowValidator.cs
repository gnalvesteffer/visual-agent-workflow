using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace VisualAgentWorkflow.Core;

public sealed record ValidationIssue(string Code, string Path, string Message);

public static class WorkflowValidator
{
    public static ImmutableArray<ValidationIssue> Validate(WorkflowDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var issues = ValidateDefinition(definition).ToBuilder();
        if (definition.Nodes is not null)
        {
            var ancestry = new HashSet<string>(StringComparer.Ordinal) { definition.WorkflowId };
            var expandedNodes = definition.Nodes.Count;
            ValidateNested(definition, 1, "", ancestry, ref expandedNodes, issues);
        }
        return issues.ToImmutable();
    }

    private static void ValidateNested(WorkflowDefinition definition, int depth, string path,
        HashSet<string> ancestry, ref int expandedNodes, ImmutableArray<ValidationIssue>.Builder issues)
    {
        foreach (var (nodeId, node) in definition.Nodes)
        {
            var call = node?.WorkflowCall;
            if (call is null) continue;
            if (call.Definition?.Nodes is not { } childNodes) continue;
            var callPath = $"{path}nodes.{nodeId}.workflowCall";
            if (depth >= WorkflowCallLimits.MaximumDepth)
            {
                issues.Add(new("workflowCall.depth", callPath, $"Nested workflow calls are limited to {WorkflowCallLimits.MaximumDepth - 1} levels."));
                continue;
            }
            if (ancestry.Contains(call.WorkflowId))
            {
                issues.Add(new("workflowCall.cycle", callPath + ".workflowId", "A workflow cannot call itself through a nested prefab."));
                continue;
            }
            expandedNodes += childNodes.Count;
            if (expandedNodes > WorkflowCallLimits.MaximumExpandedNodes)
            {
                issues.Add(new("workflowCall.size", callPath, $"Expanded workflows are limited to {WorkflowCallLimits.MaximumExpandedNodes} total nodes."));
                return;
            }
            ancestry.Add(call.WorkflowId);
            var childPath = callPath + ".definition.";
            issues.AddRange(ValidateDefinition(call.Definition).Select(issue => issue with { Path = childPath + issue.Path }));
            ValidateNested(call.Definition, depth + 1, childPath, ancestry, ref expandedNodes, issues);
            ancestry.Remove(call.WorkflowId);
        }
    }

    private static ImmutableArray<ValidationIssue> ValidateDefinition(WorkflowDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var issues = ImmutableArray.CreateBuilder<ValidationIssue>();
        void Error(string code, string path, string message) => issues.Add(new(code, path, message));

        if (definition.SchemaVersion is not ("1.0" or "1.1" or "1.2" or "1.3" or "1.4" or "1.5" or "1.6" or "1.7" or "1.8" or "1.9" or "1.10" or "1.11" or "1.12" or "1.13" or "1.14" or "1.15" or "1.16" or "1.17" or "1.18" or "1.19" or "1.20" or "1.21" or "1.22" or "1.23" or "1.24" or "1.25" or "1.26" or "1.27" or WorkflowSchemaVersions.AgentGraphFunctionTools or WorkflowSchemaVersions.AgentParameterBindings or WorkflowSchemaVersions.AgentExecutionRoutes or WorkflowSchemaVersions.IntegerGraph or WorkflowSchemaVersions.ConfigurableParallelGraph or WorkflowSchemaVersions.CollaborativeParallelGraph or WorkflowSchemaVersions.BranchLocalParallelGraph or WorkflowSchemaVersions.PhasedCollaborativeParallelGraph or WorkflowSchemaVersions.DirectMcpGraph or WorkflowSchemaVersions.DirectMcpPinsGraph))
            Error("schema.unsupported", "schemaVersion", "Supported schema versions are 1.0 through 1.37.");
        if (string.IsNullOrWhiteSpace(definition.WorkflowId))
            Error("workflow.id", "workflowId", "A workflow ID is required.");
        if (definition.DisplayName is { } displayName &&
            (string.IsNullOrWhiteSpace(displayName) || displayName != displayName.Trim() ||
                displayName.Length > WorkflowMetadataEditing.MaximumDisplayNameLength || displayName.Any(char.IsControl)))
            Error("workflow.display-name", "displayName", $"A workflow name must contain 1 to {WorkflowMetadataEditing.MaximumDisplayNameLength} printable characters.");
        if (definition.Nodes is null || definition.Connections is null)
        {
            Error("graph.missing", "nodes", "Nodes and connections must be present.");
            return issues.ToImmutable();
        }
        if (definition.Nodes.Count is < 2 or > 256 || definition.Connections.Count > (WorkflowSchemaVersions.UsesExplicitGraph(definition) || definition.SchemaVersion is "1.4" or "1.5" or "1.6" or "1.7" or "1.8" or "1.9" or "1.10" or "1.11" or "1.12" or "1.13" or "1.14" or "1.15" or "1.16" or "1.17" or "1.18" or "1.19" or "1.20" or "1.21" or "1.22" ? 510 : 255))
            Error("graph.limit", "nodes", "A graph requires 2–256 nodes and at most 255 connections (510 for routed and explicit-graph schemas).");

        var parameters = definition.Parameters ?? ImmutableSortedDictionary<string, WorkflowParameterDefinition>.Empty;
        if (parameters.Count > 64)
            Error("parameters.limit", "parameters", "A workflow can define at most 64 parameters.");
        if (definition.Parameters is { Count: > 0 } && definition.SchemaVersion is not ("1.6" or "1.7" or "1.8" or "1.9" or "1.10" or "1.11" or "1.12" or "1.13" or "1.14" or "1.15" or "1.19" or "1.22" or "1.21" or "1.20" or "1.18" or "1.17" or "1.16" or WorkflowSchemaVersions.AgentGraphFunctionTools or WorkflowSchemaVersions.AgentParameterBindings or WorkflowSchemaVersions.AgentExecutionRoutes or WorkflowSchemaVersions.IntegerGraph or WorkflowSchemaVersions.ConfigurableParallelGraph or WorkflowSchemaVersions.CollaborativeParallelGraph or WorkflowSchemaVersions.BranchLocalParallelGraph or WorkflowSchemaVersions.PhasedCollaborativeParallelGraph))
            Error("parameters.schema", "parameters", "Workflow parameters require schema 1.6 or later.");
        foreach (var (id, parameter) in parameters)
            if (!ValidParameterId(id) || parameter is null || string.IsNullOrWhiteSpace(parameter.DisplayName) ||
                parameter.DisplayName.Length > 128 || parameter.DefaultValue is null || parameter.DefaultValue.Length > 16_384 ||
                parameter.Description?.Length > 512 ||
                parameter.Type is not (null or "string" or "integer" or "number" or "boolean" or "choice") ||
                parameter.Type is not (null or "string") && definition.SchemaVersion is not ("1.8" or "1.9" or "1.10" or "1.11" or "1.12" or "1.13" or "1.14" or "1.15" or "1.19" or "1.22" or "1.21" or "1.20" or "1.18" or "1.17" or "1.16" or WorkflowSchemaVersions.AgentGraphFunctionTools or WorkflowSchemaVersions.AgentParameterBindings or WorkflowSchemaVersions.AgentExecutionRoutes or WorkflowSchemaVersions.IntegerGraph or WorkflowSchemaVersions.ConfigurableParallelGraph or WorkflowSchemaVersions.CollaborativeParallelGraph or WorkflowSchemaVersions.BranchLocalParallelGraph or WorkflowSchemaVersions.PhasedCollaborativeParallelGraph) ||
                parameter.Type == "choice" && (parameter.Choices is not { Length: > 0 and <= 32 } choices ||
                    choices.Any(choice => string.IsNullOrEmpty(choice) || choice.Length > 128) || choices.Distinct(StringComparer.Ordinal).Count() != choices.Length) ||
                parameter.Type != "choice" && parameter.Choices is not null)
                Error("parameter.definition", $"parameters.{id}", "Parameters require a safe ID, display name up to 128 characters, and a string default up to 16,384 characters.");
        foreach (var (id, parameter) in parameters)
            if (parameter is not null && !WorkflowParameters.IsValidValue(parameter, parameter.DefaultValue))
                Error("parameter.default", $"parameters.{id}.defaultValue", "The default must match its declared type and allowed choices.");
        if (parameters.Sum(parameter => (long)parameter.Key.Length + (parameter.Value?.DisplayName.Length ?? 0) + (parameter.Value?.DefaultValue.Length ?? 0) + (parameter.Value?.Description?.Length ?? 0)) > 128_000)
            Error("parameters.size", "parameters", "Combined parameter IDs, display names, descriptions, and defaults must not exceed 128,000 characters.");
        var skills = definition.Skills ?? ImmutableSortedDictionary<string, WorkflowSkill>.Empty;
        if (skills.Count > 32 || skills.Values.Sum(skill => (long)(skill?.Instructions?.Length ?? 0)) > 65_536)
            Error("skills.limit", "skills", "A workflow can include at most 32 skills and 65,536 instruction characters.");
        if (skills.Count > 0 && definition.SchemaVersion is not ("1.7" or "1.8" or "1.9" or "1.10" or "1.11" or "1.12" or "1.13" or "1.14" or "1.15" or "1.19" or "1.22" or "1.21" or "1.20" or "1.18" or "1.17" or "1.16" or WorkflowSchemaVersions.AgentGraphFunctionTools or WorkflowSchemaVersions.AgentParameterBindings or WorkflowSchemaVersions.AgentExecutionRoutes or WorkflowSchemaVersions.IntegerGraph or WorkflowSchemaVersions.ConfigurableParallelGraph or WorkflowSchemaVersions.CollaborativeParallelGraph or WorkflowSchemaVersions.BranchLocalParallelGraph or WorkflowSchemaVersions.PhasedCollaborativeParallelGraph))
            Error("skills.schema", "skills", "Portable skills require schema 1.7.");
        foreach (var (key, skill) in skills)
            if (skill is null || key != skill.Id + "@" + skill.Version || !ValidParameterId(skill.Id) ||
                string.IsNullOrWhiteSpace(skill.Version) || skill.Version.Length > 64 ||
                string.IsNullOrWhiteSpace(skill.Name) || skill.Name.Length > 128 ||
                string.IsNullOrWhiteSpace(skill.Instructions) || skill.Instructions.Length > 8192 ||
                !string.Equals(skill.ContentHash, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(skill.Instructions))), StringComparison.Ordinal))
                Error("skill.definition", $"skills.{key}", "A skill needs a valid identity, bounded text and matching content hash.");

        var profiles = definition.AgentProfiles ?? ImmutableSortedDictionary<string, AgentProfile>.Empty;
        if (profiles.Count > 32 || profiles.Values.Sum(profile => (long)(profile?.Instructions?.Length ?? 0) +
            (profile?.Skills ?? []).Sum(skill => (long)(skill?.Instructions?.Length ?? 0))) > 65_536)
            Error("agentProfiles.limit", "agentProfiles", "A workflow can include at most 32 agent profiles and 65,536 instruction characters.");
        if (profiles.Count > 0 && definition.SchemaVersion is not ("1.11" or "1.12" or "1.13" or "1.14" or "1.15" or "1.19" or "1.22" or "1.21" or "1.20" or "1.18" or "1.17" or "1.16" or WorkflowSchemaVersions.AgentGraphFunctionTools or WorkflowSchemaVersions.AgentParameterBindings or WorkflowSchemaVersions.AgentExecutionRoutes or WorkflowSchemaVersions.IntegerGraph or WorkflowSchemaVersions.ConfigurableParallelGraph or WorkflowSchemaVersions.CollaborativeParallelGraph or WorkflowSchemaVersions.BranchLocalParallelGraph or WorkflowSchemaVersions.PhasedCollaborativeParallelGraph))
            Error("agentProfiles.schema", "agentProfiles", "Embedded agent profiles require schema 1.11 or later.");
        foreach (var (key, profile) in profiles)
        {
            var expected = profile is null ? null : AgentProfile.Create(profile.Id, profile.Version, profile.Name, profile.Description,
                profile.ProviderId, profile.ProviderVersion, profile.Instructions, profile.Tools, profile.Skills,
                profile.ReasoningEffort, profile.MaxOutputTokens, profile.Channels);
            if (profile is null || key != profile.Id + "@" + profile.Version || !ValidParameterId(profile.Id) ||
                string.IsNullOrWhiteSpace(profile.Version) || profile.Version.Length > 64 ||
                string.IsNullOrWhiteSpace(profile.Name) || profile.Name.Length > 128 ||
                string.IsNullOrWhiteSpace(profile.Description) || profile.Description.Length > 512 ||
                string.IsNullOrWhiteSpace(profile.ProviderId) || profile.ProviderId.Length > 128 ||
                string.IsNullOrWhiteSpace(profile.Instructions) || profile.Instructions.Length > 8192 ||
                profile.Tools.IsDefault || profile.Tools.Any(tool => tool is null || !ValidBinding(tool)) ||
                profile.Tools.Select(tool => tool.BindingId).Distinct(StringComparer.Ordinal).Count() != profile.Tools.Length ||
                profile.Skills is { } profileSkills && (profileSkills.IsDefault || profileSkills.Length > 8 ||
                    profileSkills.Select(skill => skill is null ? "" : skill.Id + "@" + skill.Version).Distinct(StringComparer.Ordinal).Count() != profileSkills.Length ||
                    profileSkills.Any(skill => skill is null || skill.ContentHash != WorkflowSkill.Create(skill.Id, skill.Version, skill.Name, skill.Instructions).ContentHash)) ||
                profile.Channels is { } channels && (definition.SchemaVersion is not ("1.12" or "1.13" or "1.14" or "1.15" or "1.19" or "1.22" or "1.21" or "1.20" or "1.18" or "1.17" or "1.16" or WorkflowSchemaVersions.AgentGraphFunctionTools or WorkflowSchemaVersions.AgentParameterBindings or WorkflowSchemaVersions.AgentExecutionRoutes or WorkflowSchemaVersions.IntegerGraph or WorkflowSchemaVersions.ConfigurableParallelGraph or WorkflowSchemaVersions.CollaborativeParallelGraph or WorkflowSchemaVersions.BranchLocalParallelGraph or WorkflowSchemaVersions.PhasedCollaborativeParallelGraph) || channels.IsDefault || channels.Length > 8 ||
                    channels.Distinct(StringComparer.Ordinal).Count() != channels.Length || channels.Any(channel => !ValidChannelId(channel))) ||
                profile.ReasoningEffort is { } profileEffort && !Enum.IsDefined(profileEffort) ||
                profile.MaxOutputTokens is { } outputTokens && outputTokens is < AgentOutputTokenLimits.Minimum or > AgentOutputTokenLimits.Maximum ||
                profile.ContentHash != expected?.ContentHash)
                Error("agentProfile.definition", $"agentProfiles.{key}", "A profile needs a valid identity, bounded instructions, a model connection ID, pinned tool/skill requirements and a matching content hash.");
        }

        var collaborationRegions = definition.ParallelCollaborationRegions;
        if (collaborationRegions is not null && definition.SchemaVersion is not (WorkflowSchemaVersions.CollaborativeParallelGraph or WorkflowSchemaVersions.PhasedCollaborativeParallelGraph))
            Error("parallel.collaboration.unexpected", "parallelCollaborationRegions", "Parallel collaboration regions require schema 1.33 or 1.35.");
        if (definition.SchemaVersion is WorkflowSchemaVersions.CollaborativeParallelGraph or WorkflowSchemaVersions.PhasedCollaborativeParallelGraph && collaborationRegions is null)
            Error("parallel.collaboration.missing", "parallelCollaborationRegions", "Collaboration schemas require one explicitly configured collaboration region.");
        if (collaborationRegions is { Count: > 0 } &&
            definition.Nodes.Values.Any(node => node?.Type is NodeTypes.Loop or NodeTypes.LoopBody))
            Error("parallel.collaboration.loop", "nodes", "Collaborative parallel regions cannot be re-entered by bounded workflow loops; remove the loop or the collaborative region.");
        if (collaborationRegions is not null)
        {
            if (collaborationRegions.Count > 1)
                Error("parallel.collaboration.count", "parallelCollaborationRegions", "Collaboration schemas support exactly one collaboration region.");
            foreach (var (key, region) in collaborationRegions)
            {
                if (string.IsNullOrWhiteSpace(key) || region is null || key != region.RendezvousNodeId)
                    Error("parallel.collaboration.identity", $"parallelCollaborationRegions.{key}", "A region key must be the exact rendezvous node ID.");
                if (region is null) continue;
                if (definition.SchemaVersion == WorkflowSchemaVersions.PhasedCollaborativeParallelGraph && string.IsNullOrWhiteSpace(region.SecondRendezvousNodeId) ||
                    definition.SchemaVersion == WorkflowSchemaVersions.CollaborativeParallelGraph && region.SecondRendezvousNodeId is not null)
                    Error("parallel.collaboration.barriers", $"parallelCollaborationRegions.{key}.secondRendezvousNodeId", "Schema 1.35 requires a second rendezvous node; schema 1.33 does not allow one.");
                var channelIds = region.ChannelIds;
                var configuredChannelIds = channelIds.IsDefault ? ImmutableArray<string>.Empty : channelIds;
                if (channelIds.IsDefault || channelIds.Length is < 1 or > ParallelCollaborationLimits.MaximumChannels ||
                    channelIds.Distinct(StringComparer.Ordinal).Count() != channelIds.Length ||
                    channelIds.Any(channel => !ParallelCollaborationLimits.IsValidChannelId(channel)))
                    Error("parallel.collaboration.channels", $"parallelCollaborationRegions.{key}.channelIds", $"Declare 1–{ParallelCollaborationLimits.MaximumChannels} unique channel IDs using lowercase letters, digits, dots, dashes, and underscores.");
                if (region.TimeoutSeconds is <= 0)
                    Error("parallel.collaboration.timeout", $"parallelCollaborationRegions.{key}.timeoutSeconds", "A configured timeout must be a positive number of seconds; omit it to use the host default.");
                if (region.Branches.IsDefault || region.Branches.Length is < ParallelBranchLimits.MinimumBranches or > ParallelGraphLimits.MaximumExplicitBranches ||
                    region.Branches.Any(branch => branch is null || !ParallelBranchLimits.IsValidId(branch.BranchId)) ||
                    region.Branches.Select(branch => branch?.BranchId).Distinct(StringComparer.Ordinal).Count() != region.Branches.Length)
                    Error("parallel.collaboration.branches", $"parallelCollaborationRegions.{key}.branches", "A region requires 2–8 unique branches identified by stable fork branch IDs.");
                foreach (var branch in region.Branches.IsDefault ? [] : region.Branches)
                {
                    if (branch is null || branch.Agents.IsDefault)
                    {
                        Error("parallel.collaboration.grants", $"parallelCollaborationRegions.{key}.branches", "Each branch grant collection must be explicitly provided, even when empty.");
                        continue;
                    }
                    var grantedAgents = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var grant in branch.Agents)
                    {
                        var validGrant = grant is not null && !string.IsNullOrWhiteSpace(grant.AgentNodeId) && grantedAgents.Add(grant.AgentNodeId) &&
                            (definition.SchemaVersion == WorkflowSchemaVersions.PhasedCollaborativeParallelGraph
                                ? (grant.Phase is ParallelCollaborationPhases.Phase1 or ParallelCollaborationPhases.Phase2) && (grant.Role is ParallelCollaborationRoles.Publish or ParallelCollaborationRoles.Read)
                                : (grant.Phase is ParallelCollaborationPhases.Publish or ParallelCollaborationPhases.Read) && grant.Role is null) &&
                            !grant.Channels.IsDefault && grant.Channels.Length >= 1 && grant.Channels.Distinct(StringComparer.Ordinal).Count() == grant.Channels.Length &&
                            grant.Channels.All(channel => channel is not null && configuredChannelIds.Contains(channel, StringComparer.Ordinal));
                        if (!validGrant)
                            Error("parallel.collaboration.grant", $"parallelCollaborationRegions.{key}.branches.{branch.BranchId}.agents", "Each grant must uniquely name an Agent node, one valid role/phase assignment, and one or more configured channels.");
                    }
                }
            }
        }

        var incoming = definition.Nodes.Keys.ToDictionary(id => id, _ => 0);
        var outgoing = definition.Nodes.Keys.ToDictionary(id => id, _ => new List<string>());
        var hasTerminationExit = definition.Nodes.Values.Any(n => n?.Type == NodeTypes.Terminate);
        foreach (var (id, node) in definition.Nodes)
        {
            if (string.IsNullOrWhiteSpace(id)) Error("node.id", "nodes", "Node IDs must be nonempty.");
            if (node is null || !NodeTypes.IsSupported(node.Type) || node.TypeVersion != "1.0.0")
                Error("node.unsupported", $"nodes.{id}", "Unknown node type or implementation version.");
            if (node is null) continue;
            if (node.McpArgumentsJson is { } mcpArguments &&
                (node.Type != NodeTypes.McpTool ||
                 mcpArguments.Length > 65_536 || !IsJsonObject(mcpArguments)))
                Error("mcp.arguments", $"nodes.{id}.mcpArgumentsJson", "MCP arguments must be a JSON object of at most 65,536 characters.");
            if (node.McpInputPorts is not null &&
                (definition.SchemaVersion != WorkflowSchemaVersions.DirectMcpPinsGraph || node.Type != NodeTypes.McpTool ||
                 node.McpInputPorts.Value.Any(port => port is null || string.IsNullOrWhiteSpace(port.Id) || string.IsNullOrWhiteSpace(port.Name) ||
                     port.Id.Length > 256 || port.Name.Length > 256 || !WorkflowDataTypeCatalog.IsSupported(port.DataType) ||
                     !port.Id.StartsWith("arg.", StringComparison.Ordinal))))
                Error("mcp.input-ports", $"nodes.{id}.mcpInputPorts", "Schema 1.37 MCP input pins must be unique, bounded arg.* ports with supported graph data types.");
            if (node.McpInputPorts is { } inputPorts && inputPorts.Select(port => port.Id).Distinct(StringComparer.Ordinal).Count() != inputPorts.Length)
                Error("mcp.input-ports", $"nodes.{id}.mcpInputPorts", "MCP input pin IDs must be unique.");
            if (node.McpInputPorts is { } namedPorts && namedPorts.Select(port => port.Name).Distinct(StringComparer.Ordinal).Count() != namedPorts.Length)
                Error("mcp.input-ports", $"nodes.{id}.mcpInputPorts", "MCP input property names must be unique.");
            if (node.Type == NodeTypes.WorkflowCall)
            {
                var call = node.WorkflowCall;
                var hashMatches = false;
                if (call?.Definition is { } child)
                {
                    try { hashMatches = WorkflowJson.MatchesHash(child, call.ContentHash); }
                    catch (ArgumentException) { }
                }
                if (definition.SchemaVersion is not ("1.19" or "1.22" or "1.21" or "1.20" or WorkflowSchemaVersions.AgentGraphFunctionTools or WorkflowSchemaVersions.AgentParameterBindings or WorkflowSchemaVersions.AgentExecutionRoutes or WorkflowSchemaVersions.IntegerGraph or WorkflowSchemaVersions.ConfigurableParallelGraph or WorkflowSchemaVersions.CollaborativeParallelGraph or WorkflowSchemaVersions.BranchLocalParallelGraph or WorkflowSchemaVersions.PhasedCollaborativeParallelGraph) || call is null || string.IsNullOrWhiteSpace(call.WorkflowId) ||
                    call.WorkflowId.Length > 256 || call.WorkflowId != call.Definition?.WorkflowId || call.RevisionId == Guid.Empty ||
                    call.Version < 1 || !ValidHash(call.ContentHash) || !hashMatches || call.Definition is { } childDefinition && !HasSupportedWorkflowCallContract(childDefinition))
                    Error("workflowCall.configuration", $"nodes.{id}.workflowCall", "Workflow call nodes require schema 1.19 and an embedded, hash-matching workflow with string or JSON input and output contracts.");
                if (call?.ParameterMappings is { } mappings)
                {
                    var childParameters = call.Definition?.Parameters ?? ImmutableSortedDictionary<string, WorkflowParameterDefinition>.Empty;
                    if (mappings.IsDefault || mappings.Length > 64)
                        Error("workflowCall.parameters.limit", $"nodes.{id}.workflowCall.parameterMappings", "A workflow call can map at most 64 child parameters.");
                    else
                    {
                        if (mappings.Select(mapping => mapping?.ChildParameterId).Distinct(StringComparer.Ordinal).Count() != mappings.Length)
                            Error("workflowCall.parameters.duplicate", $"nodes.{id}.workflowCall.parameterMappings", "Each child parameter can be mapped only once.");
                        foreach (var mapping in mappings)
                        {
                            if (mapping is null || string.IsNullOrEmpty(mapping.ChildParameterId) || string.IsNullOrEmpty(mapping.ParentParameterId) ||
                                !childParameters.TryGetValue(mapping.ChildParameterId, out var childParameter) || childParameter is null ||
                                !parameters.TryGetValue(mapping.ParentParameterId, out var parentParameter) || parentParameter is null)
                            {
                                Error("workflowCall.parameters.reference", $"nodes.{id}.workflowCall.parameterMappings", "Mappings must reference a parameter declared by both the child and parent workflows.");
                                continue;
                            }

                            if (!WorkflowParameters.AreCompatible(parentParameter, childParameter))
                                Error("workflowCall.parameters.type", $"nodes.{id}.workflowCall.parameterMappings.{mapping.ChildParameterId}", "Mapped parameters must have compatible types; parent choice values must all be allowed by the child parameter.");
                        }
                    }
                }
            }
            else if (node.WorkflowCall is not null)
                Error("workflowCall.unexpected", $"nodes.{id}.workflowCall", "Only workflow call nodes may declare a nested workflow revision.");
            if (node.Type == NodeTypes.FunctionCall)
            {
                var function = node.GraphFunction;
                var hashMatches = false;
                if (function?.Definition is { } functionDefinition)
                {
                    try { hashMatches = GraphFunctionJson.ComputeHash(functionDefinition) == function.ContentHash; }
                    catch (ArgumentException) { }
                }
                if (!WorkflowSchemaVersions.UsesExplicitGraph(definition) || definition.SchemaVersion is not (WorkflowSchemaVersions.PureGraphFunctions or WorkflowSchemaVersions.AgentGraphFunctionTools or WorkflowSchemaVersions.AgentParameterBindings or WorkflowSchemaVersions.AgentExecutionRoutes or WorkflowSchemaVersions.IntegerGraph or WorkflowSchemaVersions.ConfigurableParallelGraph or WorkflowSchemaVersions.CollaborativeParallelGraph or WorkflowSchemaVersions.BranchLocalParallelGraph or WorkflowSchemaVersions.PhasedCollaborativeParallelGraph) || function is null ||
                    !ValidHash(function.ContentHash) || !hashMatches)
                    Error("graphFunction.configuration", $"nodes.{id}.graphFunction", "Graph function calls require schema 1.27 and an immutable, hash-matching function snapshot.");
                foreach (var issue in GraphFunctionJson.Validate(function?.Definition))
                    Error(issue.Code, $"nodes.{id}.{issue.Path}", issue.Message);
            }
            else if (node.GraphFunction is not null)
                Error("graphFunction.unexpected", $"nodes.{id}.graphFunction", "Only graph function call nodes may declare a function snapshot.");
            if (node.Type is NodeTypes.FunctionInput or NodeTypes.FunctionOutput || node.FunctionPortId is not null)
                Error("graphFunction.boundary", $"nodes.{id}", "Function boundary nodes are only valid inside an embedded pure function definition.");
            if (node.Type is NodeTypes.Condition or NodeTypes.Merge && definition.SchemaVersion is not ("1.4" or "1.5" or "1.6" or "1.7" or "1.8" or "1.9" or "1.10" or "1.11" or "1.12" or "1.13" or "1.14" or "1.15" or "1.16" or "1.19" or "1.22" or "1.21" or "1.20" or "1.18" or "1.17" or WorkflowSchemaVersions.AgentGraphFunctionTools or WorkflowSchemaVersions.AgentParameterBindings or WorkflowSchemaVersions.AgentExecutionRoutes or WorkflowSchemaVersions.IntegerGraph or WorkflowSchemaVersions.ConfigurableParallelGraph or WorkflowSchemaVersions.CollaborativeParallelGraph or WorkflowSchemaVersions.BranchLocalParallelGraph or WorkflowSchemaVersions.PhasedCollaborativeParallelGraph))
                Error("node.schema", $"nodes.{id}", "Conditional and merge nodes require schema 1.4.");
            if (node.Type == NodeTypes.WorkflowValue)
            {
                if (definition.SchemaVersion is not ("1.6" or "1.7" or "1.8" or "1.9" or "1.10" or "1.11" or "1.12" or "1.13" or "1.14" or "1.15" or "1.19" or "1.22" or "1.21" or "1.20" or "1.18" or "1.17" or "1.16" or WorkflowSchemaVersions.AgentGraphFunctionTools or WorkflowSchemaVersions.AgentParameterBindings or WorkflowSchemaVersions.AgentExecutionRoutes or WorkflowSchemaVersions.IntegerGraph or WorkflowSchemaVersions.ConfigurableParallelGraph or WorkflowSchemaVersions.CollaborativeParallelGraph or WorkflowSchemaVersions.BranchLocalParallelGraph or WorkflowSchemaVersions.PhasedCollaborativeParallelGraph)) Error("node.schema", $"nodes.{id}", "Workflow value nodes require schema 1.6 or later.");
                if (node.ParameterId is not { } parameterId || !parameters.ContainsKey(parameterId))
                    Error("parameter.reference", $"nodes.{id}.parameterId", "Select a parameter defined by this workflow.");
                if (definition.SchemaVersion is WorkflowSchemaVersions.IntegerGraph or WorkflowSchemaVersions.ConfigurableParallelGraph or WorkflowSchemaVersions.CollaborativeParallelGraph or WorkflowSchemaVersions.BranchLocalParallelGraph or WorkflowSchemaVersions.PhasedCollaborativeParallelGraph &&
                    (node.OutputDataType is not { } valueType || node.ParameterId is not { } valueParameter ||
                        !parameters.TryGetValue(valueParameter, out var valueDefinition) ||
                        valueType != WorkflowParameterGraphTypes.DataType(valueDefinition)))
                    Error("parameter.type", $"nodes.{id}.outputDataType", "Typed workflow value pins require schema 1.31 and must match a string, Boolean, or integer parameter.");
            }
            else if (node.ParameterId is not null)
                Error("parameter.unexpected", $"nodes.{id}.parameterId", "Only workflow value nodes may reference a parameter.");
            if (node.InputDataType is not null && (node.Type != NodeTypes.Input || node.InputDataType != "json" || definition.SchemaVersion is not ("1.9" or "1.10" or "1.11" or "1.12" or "1.13" or "1.14" or "1.15" or "1.19" or "1.22" or "1.21" or "1.20" or "1.18" or "1.17" or "1.16" or WorkflowSchemaVersions.AgentGraphFunctionTools or WorkflowSchemaVersions.AgentParameterBindings or WorkflowSchemaVersions.AgentExecutionRoutes or WorkflowSchemaVersions.IntegerGraph or WorkflowSchemaVersions.ConfigurableParallelGraph or WorkflowSchemaVersions.CollaborativeParallelGraph or WorkflowSchemaVersions.BranchLocalParallelGraph or WorkflowSchemaVersions.PhasedCollaborativeParallelGraph)))
                Error("input.dataType", $"nodes.{id}.inputDataType", "Only schema 1.9 or later input nodes can declare the JSON data type.");
            if (node.OutputDataType is not null && node.Type is not (NodeTypes.WorkflowValue or NodeTypes.Output) ||
                node.OutputDataType is not null && node.Type == NodeTypes.Output &&
                    (definition.SchemaVersion is WorkflowSchemaVersions.IntegerGraph or WorkflowSchemaVersions.ConfigurableParallelGraph
                        ? node.OutputDataType is not ("string" or "integer" or "boolean" or "json")
                        : node.OutputDataType != "json" || definition.SchemaVersion is not ("1.13" or "1.14" or "1.15" or "1.19" or "1.22" or "1.21" or "1.20" or "1.18" or "1.17" or "1.16" or WorkflowSchemaVersions.AgentGraphFunctionTools or WorkflowSchemaVersions.AgentParameterBindings or WorkflowSchemaVersions.AgentExecutionRoutes or WorkflowSchemaVersions.IntegerGraph or WorkflowSchemaVersions.ConfigurableParallelGraph or WorkflowSchemaVersions.CollaborativeParallelGraph or WorkflowSchemaVersions.BranchLocalParallelGraph or WorkflowSchemaVersions.PhasedCollaborativeParallelGraph)) ||
                node.OutputDataType is not null && node.Type == NodeTypes.WorkflowValue && definition.SchemaVersion is not (WorkflowSchemaVersions.IntegerGraph or WorkflowSchemaVersions.ConfigurableParallelGraph or WorkflowSchemaVersions.CollaborativeParallelGraph or WorkflowSchemaVersions.BranchLocalParallelGraph or WorkflowSchemaVersions.PhasedCollaborativeParallelGraph))
                Error("output.dataType", $"nodes.{id}.outputDataType", "Only schema 1.13 or later output nodes can declare the JSON data type.");
            if (node.Type == NodeTypes.JsonSelectString)
            {
                if (definition.SchemaVersion is not ("1.9" or "1.10" or "1.11" or "1.12" or "1.13" or "1.14" or "1.15" or "1.19" or "1.22" or "1.21" or "1.20" or "1.18" or "1.17" or "1.16" or WorkflowSchemaVersions.AgentGraphFunctionTools or WorkflowSchemaVersions.AgentParameterBindings or WorkflowSchemaVersions.AgentExecutionRoutes or WorkflowSchemaVersions.IntegerGraph or WorkflowSchemaVersions.ConfigurableParallelGraph or WorkflowSchemaVersions.CollaborativeParallelGraph or WorkflowSchemaVersions.BranchLocalParallelGraph or WorkflowSchemaVersions.PhasedCollaborativeParallelGraph)) Error("node.schema", $"nodes.{id}", "JSON selection nodes require schema 1.9 or later.");
                if (node.JsonPropertySelection is not { } selection || !ValidJsonPointer(selection.Pointer))
                    Error("json.selection", $"nodes.{id}.jsonPropertySelection", "Provide a valid RFC 6901 JSON pointer with at most 16 segments and 1,024 characters.");
            }
            else if (node.JsonPropertySelection is not null)
                Error("json.selection.unexpected", $"nodes.{id}.jsonPropertySelection", "Only JSON selection nodes can declare a JSON property pointer.");
            if (node.Type == NodeTypes.JsonProject)
            {
                if (definition.SchemaVersion is not ("1.13" or "1.14" or "1.15" or "1.19" or "1.22" or "1.21" or "1.20" or "1.18" or "1.17" or "1.16" or WorkflowSchemaVersions.AgentGraphFunctionTools or WorkflowSchemaVersions.AgentParameterBindings or WorkflowSchemaVersions.AgentExecutionRoutes or WorkflowSchemaVersions.IntegerGraph or WorkflowSchemaVersions.ConfigurableParallelGraph or WorkflowSchemaVersions.CollaborativeParallelGraph or WorkflowSchemaVersions.BranchLocalParallelGraph or WorkflowSchemaVersions.PhasedCollaborativeParallelGraph)) Error("node.schema", $"nodes.{id}", "JSON projection nodes require schema 1.13 or later.");
                if (!ValidJsonProjection(node.JsonProjection)) Error("json.projection", $"nodes.{id}.jsonProjection", "Map 1–16 unique JSON property names to valid bounded JSON Pointers.");
                if (node.JsonProjection?.Fields.Any(field => field?.ValueType is not null) == true && definition.SchemaVersion is not ("1.15" or "1.19" or "1.22" or "1.21" or "1.20" or "1.18" or "1.17" or "1.16" or WorkflowSchemaVersions.AgentGraphFunctionTools or WorkflowSchemaVersions.AgentParameterBindings or WorkflowSchemaVersions.AgentExecutionRoutes or WorkflowSchemaVersions.IntegerGraph or WorkflowSchemaVersions.ConfigurableParallelGraph or WorkflowSchemaVersions.CollaborativeParallelGraph or WorkflowSchemaVersions.BranchLocalParallelGraph or WorkflowSchemaVersions.PhasedCollaborativeParallelGraph))
                    Error("json.projection.schema", $"nodes.{id}.jsonProjection", "Typed JSON projection fields require schema 1.15.");
                if (node.JsonProjection?.Fields.Any(field => field?.ValueSchema is not null) == true && definition.SchemaVersion is not ("1.16" or "1.19" or "1.22" or "1.21" or "1.20" or "1.18" or "1.17" or WorkflowSchemaVersions.AgentGraphFunctionTools or WorkflowSchemaVersions.AgentParameterBindings or WorkflowSchemaVersions.AgentExecutionRoutes or WorkflowSchemaVersions.IntegerGraph or WorkflowSchemaVersions.ConfigurableParallelGraph or WorkflowSchemaVersions.CollaborativeParallelGraph or WorkflowSchemaVersions.BranchLocalParallelGraph or WorkflowSchemaVersions.PhasedCollaborativeParallelGraph))
                    Error("json.projection.schema", $"nodes.{id}.jsonProjection", "Nested JSON mapping schemas require schema 1.16 or later.");
            }
            else if (node.JsonProjection is not null)
                Error("json.projection.unexpected", $"nodes.{id}.jsonProjection", "Only JSON projection nodes may declare JSON field mappings.");
            if (node.Type is NodeTypes.ParallelFork or NodeTypes.ParallelBranch or NodeTypes.ParallelJoin && definition.SchemaVersion is not ("1.14" or "1.15" or "1.16" or "1.19" or "1.22" or "1.21" or "1.20" or "1.18" or "1.17" or WorkflowSchemaVersions.AgentGraphFunctionTools or WorkflowSchemaVersions.AgentParameterBindings or WorkflowSchemaVersions.AgentExecutionRoutes or WorkflowSchemaVersions.IntegerGraph or WorkflowSchemaVersions.ConfigurableParallelGraph or WorkflowSchemaVersions.CollaborativeParallelGraph or WorkflowSchemaVersions.BranchLocalParallelGraph or WorkflowSchemaVersions.PhasedCollaborativeParallelGraph))
                Error("node.schema", $"nodes.{id}", "Parallel graph nodes require schema 1.14.");
            if (node.Type == NodeTypes.ParallelRendezvous && definition.SchemaVersion is not (WorkflowSchemaVersions.CollaborativeParallelGraph or WorkflowSchemaVersions.PhasedCollaborativeParallelGraph))
                Error("node.schema", $"nodes.{id}", "Parallel rendezvous nodes require schema 1.33.");
            if (node.Type == NodeTypes.ParallelFork && definition.SchemaVersion is WorkflowSchemaVersions.ConfigurableParallelGraph or WorkflowSchemaVersions.CollaborativeParallelGraph or WorkflowSchemaVersions.BranchLocalParallelGraph or WorkflowSchemaVersions.PhasedCollaborativeParallelGraph)
            {
                var branches = node.ParallelFork?.Branches ?? [];
                if (node.ParallelFork is null || branches.IsDefault || branches.Length is < ParallelBranchLimits.MinimumBranches or > ParallelGraphLimits.MaximumExplicitBranches ||
                    branches.Select(branch => branch?.Id).Distinct(StringComparer.Ordinal).Count() != branches.Length ||
                    branches.Any(branch => branch is null || !ParallelBranchLimits.IsValidId(branch.Id) ||
                        string.IsNullOrWhiteSpace(branch.Label) || branch.Label.Length > ParallelBranchLimits.MaximumLabelLength ||
                        branch.AnchorNodeId is null || !definition.Nodes.TryGetValue(branch.AnchorNodeId, out var anchor) ||
                        anchor?.Type != NodeTypes.ParallelBranch))
                    Error("parallel.fork.configuration", $"nodes.{id}.parallelFork", $"A configurable parallel fork requires {ParallelBranchLimits.MinimumBranches}–{ParallelGraphLimits.MaximumExplicitBranches} uniquely identified branches with labels and existing branch anchors.");
            }
            else if (node.ParallelFork is not null)
                Error("parallel.fork.unexpected", $"nodes.{id}.parallelFork", "Only schema 1.32 parallel fork nodes may declare branch descriptors.");
            if (node.Type == NodeTypes.ParallelBranch && definition.SchemaVersion is WorkflowSchemaVersions.ConfigurableParallelGraph or WorkflowSchemaVersions.CollaborativeParallelGraph or WorkflowSchemaVersions.BranchLocalParallelGraph or WorkflowSchemaVersions.PhasedCollaborativeParallelGraph)
            {
                if (node.ParallelBranch is not { } branch || !ParallelBranchLimits.IsValidId(branch.BranchId) ||
                    string.IsNullOrWhiteSpace(branch.ForkNodeId) || !definition.Nodes.TryGetValue(branch.ForkNodeId, out var fork) ||
                    fork?.Type != NodeTypes.ParallelFork || fork.ParallelFork?.Branches.All(item => item?.Id != branch.BranchId || item.AnchorNodeId != id) != false)
                    Error("parallel.branch.configuration", $"nodes.{id}.parallelBranch", "A configurable parallel branch must match one branch descriptor on its referenced fork.");
            }
            else if (node.ParallelBranch is not null)
                Error("parallel.branch.unexpected", $"nodes.{id}.parallelBranch", "Only schema 1.32 parallel branch anchors may declare branch identity.");
            if (node.Type == NodeTypes.ParallelJoin && definition.SchemaVersion is WorkflowSchemaVersions.ConfigurableParallelGraph or WorkflowSchemaVersions.CollaborativeParallelGraph or WorkflowSchemaVersions.BranchLocalParallelGraph or WorkflowSchemaVersions.PhasedCollaborativeParallelGraph)
            {
                var branchIds = node.ParallelJoin?.BranchIds ?? [];
                if (node.ParallelJoin is not { } join || string.IsNullOrWhiteSpace(join.ForkNodeId) ||
                    !definition.Nodes.TryGetValue(join.ForkNodeId, out var fork) || fork?.Type != NodeTypes.ParallelFork ||
                    branchIds.IsDefault || branchIds.Length is < ParallelBranchLimits.MinimumBranches or > ParallelGraphLimits.MaximumExplicitBranches ||
                    branchIds.Distinct(StringComparer.Ordinal).Count() != branchIds.Length ||
                    branchIds.Any(branchId => !ParallelBranchLimits.IsValidId(branchId)) ||
                    fork.ParallelFork?.Branches.Select(branch => branch.Id).Order(StringComparer.Ordinal)
                        .SequenceEqual(branchIds.Order(StringComparer.Ordinal)) != true)
                    Error("parallel.join.configuration", $"nodes.{id}.parallelJoin", "A configurable parallel join must match every branch ID on exactly one referenced fork.");
            }
            else if (node.ParallelJoin is not null)
                Error("parallel.join.unexpected", $"nodes.{id}.parallelJoin", "Only schema 1.32 parallel joins may declare branch identities.");
            if (node.Type == NodeTypes.ParallelRendezvous && definition.SchemaVersion is WorkflowSchemaVersions.CollaborativeParallelGraph or WorkflowSchemaVersions.PhasedCollaborativeParallelGraph)
            {
                var branchIds = node.ParallelRendezvous?.BranchIds ?? [];
                if (node.ParallelRendezvous is null || branchIds.IsDefault || branchIds.Length is < ParallelBranchLimits.MinimumBranches or > ParallelGraphLimits.MaximumExplicitBranches ||
                    branchIds.Any(branchId => !ParallelBranchLimits.IsValidId(branchId)) || branchIds.Distinct(StringComparer.Ordinal).Count() != branchIds.Length)
                    Error("parallel.rendezvous.configuration", $"nodes.{id}.parallelRendezvous", "A rendezvous requires 2–8 unique stable fork branch IDs.");
            }
            else if (node.ParallelRendezvous is not null)
                Error("parallel.rendezvous.unexpected", $"nodes.{id}.parallelRendezvous", "Only schema 1.33 rendezvous nodes may declare branch identities.");
            if (node.Type == NodeTypes.Loop && (definition.SchemaVersion is not ("1.17" or "1.19" or "1.22" or "1.21" or "1.20" or "1.18" or WorkflowSchemaVersions.AgentGraphFunctionTools or WorkflowSchemaVersions.AgentParameterBindings or WorkflowSchemaVersions.AgentExecutionRoutes or WorkflowSchemaVersions.IntegerGraph or WorkflowSchemaVersions.ConfigurableParallelGraph or WorkflowSchemaVersions.CollaborativeParallelGraph or WorkflowSchemaVersions.BranchLocalParallelGraph or WorkflowSchemaVersions.PhasedCollaborativeParallelGraph) || node.Loop is not { } loop ||
                loop.MaximumIterations is < 1 or > WorkflowLoopLimits.MaximumIterations || !ValidStringCondition(loop.ContinueWhile)))
                Error("loop.configuration", $"nodes.{id}.loop", $"Loops require schema 1.17 or later, an approved continue-while predicate, and 1–{WorkflowLoopLimits.MaximumIterations} maximum iterations.");
            if (node.Type != NodeTypes.Loop && node.Loop is not null)
                Error("loop.unexpected", $"nodes.{id}.loop", "Only loop nodes may declare loop configuration.");
            if (node.Type == NodeTypes.TextTemplate)
            {
                if (definition.SchemaVersion is not ("1.7" or "1.8" or "1.9" or "1.10" or "1.11" or "1.12" or "1.13" or "1.14" or "1.15" or "1.19" or "1.22" or "1.21" or "1.20" or "1.18" or "1.17" or "1.16" or WorkflowSchemaVersions.AgentGraphFunctionTools or WorkflowSchemaVersions.AgentParameterBindings or WorkflowSchemaVersions.AgentExecutionRoutes or WorkflowSchemaVersions.IntegerGraph or WorkflowSchemaVersions.ConfigurableParallelGraph or WorkflowSchemaVersions.CollaborativeParallelGraph or WorkflowSchemaVersions.BranchLocalParallelGraph or WorkflowSchemaVersions.PhasedCollaborativeParallelGraph)) Error("node.schema", $"nodes.{id}", "Text template nodes require schema 1.7 or later.");
                if (!ValidTextTemplate(node.TextTemplate, parameters))
                    Error("text.template", $"nodes.{id}.textTemplate", "Use 1–64 bounded literal, input, or declared-parameter parts.");
            }
            else if (node.TextTemplate is not null)
                Error("text.template.unexpected", $"nodes.{id}.textTemplate", "Only text template nodes may declare a template.");
            if (node.Type == NodeTypes.Approval)
            {
                if (definition.SchemaVersion is not ("1.5" or "1.6" or "1.7" or "1.8" or "1.9" or "1.10" or "1.11" or "1.12" or "1.13" or "1.14" or "1.15" or "1.19" or "1.22" or "1.21" or "1.20" or "1.18" or "1.17" or "1.16" or WorkflowSchemaVersions.AgentGraphFunctionTools or WorkflowSchemaVersions.AgentParameterBindings or WorkflowSchemaVersions.AgentExecutionRoutes or WorkflowSchemaVersions.IntegerGraph or WorkflowSchemaVersions.ConfigurableParallelGraph or WorkflowSchemaVersions.CollaborativeParallelGraph or WorkflowSchemaVersions.BranchLocalParallelGraph or WorkflowSchemaVersions.PhasedCollaborativeParallelGraph)) Error("node.schema", $"nodes.{id}", "Approval nodes require schema 1.5 or later.");
                if (node.Approval is not { } approval || string.IsNullOrWhiteSpace(approval.Prompt) || approval.Prompt.Length > 2000)
                    Error("approval.configuration", $"nodes.{id}.approval", "An approval prompt of 1–2000 characters is required.");
                var targets = definition.Connections.Values.Where(e => e?.Source?.NodeId == id).ToArray();
                if (targets.Length != 1 || targets[0]?.Target?.NodeId is not { } targetId ||
                    !definition.Nodes.TryGetValue(targetId, out var targetNode) || targetNode?.Type != NodeTypes.McpTool)
                    Error("approval.target", $"nodes.{id}", "An approval must immediately precede exactly one MCP tool node.");
            }
            else if (node.Approval is not null)
                Error("approval.unexpected", $"nodes.{id}.approval", "Only approval nodes may declare an approval prompt.");
            if (node.Type == NodeTypes.Condition)
            {
                if (node.Condition is not { } condition || condition.Operator is not ("equals" or "contains" or "starts-with" or "is-empty") ||
                    condition.Operand is null || condition.Operand.Length > 1024 || condition.Operator == "is-empty" && condition.Operand.Length != 0)
                    Error("condition.configuration", $"nodes.{id}.condition", "Use an approved ordinal string predicate and an operand up to 1024 characters; is-empty requires an empty operand.");
            }
            else if (node.Condition is not null)
                Error("condition.unexpected", $"nodes.{id}.condition", "Only conditional nodes may declare a predicate.");
            if (node.Type == NodeTypes.StringConstant)
            {
                if (!WorkflowSchemaVersions.UsesExplicitGraph(definition) || node.StringConstant is not { Value.Length: <= 16_384 })
                    Error("constant.configuration", $"nodes.{id}.stringConstant", "String constants require schema 1.23 or later and a value up to 16,384 characters.");
            }
            else if (node.StringConstant is not null)
                Error("constant.unexpected", $"nodes.{id}.stringConstant", "Only string constant nodes may declare a constant value.");
            if (node.Type == NodeTypes.BooleanConstant)
            {
                if (definition.SchemaVersion is not (WorkflowSchemaVersions.TypedBooleanGraph or WorkflowSchemaVersions.BooleanTopologyGraph or WorkflowSchemaVersions.PureGraphFunctions or WorkflowSchemaVersions.AgentGraphFunctionTools or WorkflowSchemaVersions.AgentParameterBindings or WorkflowSchemaVersions.AgentExecutionRoutes or WorkflowSchemaVersions.IntegerGraph or WorkflowSchemaVersions.ConfigurableParallelGraph or WorkflowSchemaVersions.CollaborativeParallelGraph or WorkflowSchemaVersions.BranchLocalParallelGraph or WorkflowSchemaVersions.PhasedCollaborativeParallelGraph or WorkflowSchemaVersions.DirectMcpPinsGraph) || node.BooleanConstant is null)
                    Error("constant.configuration", $"nodes.{id}.booleanConstant", "Boolean constants require schema 1.25 or later and an explicit Boolean value.");
            }
            else if (node.BooleanConstant is not null)
                Error("constant.unexpected", $"nodes.{id}.booleanConstant", "Only Boolean constant nodes may declare a Boolean value.");
            if (node.Type == NodeTypes.IntegerConstant)
            {
                if (definition.SchemaVersion is not (WorkflowSchemaVersions.IntegerGraph or WorkflowSchemaVersions.ConfigurableParallelGraph or WorkflowSchemaVersions.CollaborativeParallelGraph or WorkflowSchemaVersions.BranchLocalParallelGraph or WorkflowSchemaVersions.PhasedCollaborativeParallelGraph or WorkflowSchemaVersions.DirectMcpPinsGraph) || node.IntegerConstant is null)
                    Error("constant.configuration", $"nodes.{id}.integerConstant", "Integer constants require schema 1.31 and an explicit signed 64-bit value.");
            }
            else if (node.IntegerConstant is not null)
                Error("constant.unexpected", $"nodes.{id}.integerConstant", "Only integer constant nodes may declare an integer value.");
            if (node.Type is NodeTypes.IntegerAdd or NodeTypes.IntegerEquals && definition.SchemaVersion is not (WorkflowSchemaVersions.IntegerGraph or WorkflowSchemaVersions.ConfigurableParallelGraph or WorkflowSchemaVersions.CollaborativeParallelGraph or WorkflowSchemaVersions.BranchLocalParallelGraph or WorkflowSchemaVersions.PhasedCollaborativeParallelGraph))
                Error("node.schema", $"nodes.{id}", "Integer arithmetic nodes require schema 1.31.");
            if (node.Type == NodeTypes.TextConcat && !WorkflowSchemaVersions.UsesExplicitGraph(definition))
                Error("node.schema", $"nodes.{id}", "Pure text concatenation requires an explicit graph schema.");
            if (node.Type is NodeTypes.StringEquals or NodeTypes.BooleanBranch && definition.SchemaVersion is not (WorkflowSchemaVersions.ExplicitBranchGraph or WorkflowSchemaVersions.TypedBooleanGraph or WorkflowSchemaVersions.BooleanTopologyGraph or WorkflowSchemaVersions.PureGraphFunctions or WorkflowSchemaVersions.AgentGraphFunctionTools or WorkflowSchemaVersions.AgentParameterBindings or WorkflowSchemaVersions.AgentExecutionRoutes or WorkflowSchemaVersions.IntegerGraph or WorkflowSchemaVersions.ConfigurableParallelGraph or WorkflowSchemaVersions.CollaborativeParallelGraph or WorkflowSchemaVersions.BranchLocalParallelGraph or WorkflowSchemaVersions.PhasedCollaborativeParallelGraph))
                Error("node.schema", $"nodes.{id}", "Pure string equality and Boolean Branch nodes require schema 1.24.");
            if (node.Type is NodeTypes.BooleanNot or NodeTypes.BooleanConstant && definition.SchemaVersion is not (WorkflowSchemaVersions.TypedBooleanGraph or WorkflowSchemaVersions.BooleanTopologyGraph or WorkflowSchemaVersions.PureGraphFunctions or WorkflowSchemaVersions.AgentGraphFunctionTools or WorkflowSchemaVersions.AgentParameterBindings or WorkflowSchemaVersions.AgentExecutionRoutes or WorkflowSchemaVersions.IntegerGraph or WorkflowSchemaVersions.ConfigurableParallelGraph or WorkflowSchemaVersions.CollaborativeParallelGraph or WorkflowSchemaVersions.BranchLocalParallelGraph or WorkflowSchemaVersions.PhasedCollaborativeParallelGraph))
                Error("node.schema", $"nodes.{id}", "Boolean constants and NOT require schema 1.25 or later.");
            if (node.Origin is { } origin && (definition.SchemaVersion is not ("1.3" or "1.4" or "1.5" or "1.6" or "1.7" or "1.8" or "1.9" or "1.10" or "1.11" or "1.12" or "1.13" or "1.14" or "1.15" or "1.19" or "1.22" or "1.21" or "1.20" or "1.18" or "1.17" or "1.16" or WorkflowSchemaVersions.AgentGraphFunctionTools or WorkflowSchemaVersions.AgentParameterBindings or WorkflowSchemaVersions.AgentExecutionRoutes or WorkflowSchemaVersions.IntegerGraph or WorkflowSchemaVersions.ConfigurableParallelGraph or WorkflowSchemaVersions.CollaborativeParallelGraph or WorkflowSchemaVersions.BranchLocalParallelGraph or WorkflowSchemaVersions.PhasedCollaborativeParallelGraph) ||
                string.IsNullOrWhiteSpace(origin.WorkflowId) || origin.WorkflowId.Length > 256 ||
                string.IsNullOrWhiteSpace(origin.NodeId) || origin.NodeId.Length > 256 || origin.RevisionId == Guid.Empty ||
                origin.Version < 1 || !ValidHash(origin.ContentHash)))
                Error("node.origin", $"nodes.{id}.origin", "Reusable steps require schema 1.3 and a bounded published source identity.");
            if (node.Type == NodeTypes.McpTool)
            {
                if (definition.SchemaVersion == "1.0")
                    Error("node.schema", $"nodes.{id}", "MCP nodes require schema 1.1.");
                if (node.Tool is not { } tool || !ValidBinding(tool))
                    Error("tool.binding", $"nodes.{id}.tool", "A logical binding and SHA-256 contract hash are required.");
            }
            else if (node.Tool is not null)
                Error("tool.unexpected", $"nodes.{id}.tool", "This node type cannot declare an MCP tool.");
            if (node.Type == NodeTypes.Agent)
            {
                if (definition.SchemaVersion is not ("1.2" or "1.3" or "1.4" or "1.5" or "1.6" or "1.7" or "1.8" or "1.9" or "1.10" or "1.11" or "1.12" or "1.13" or "1.14" or "1.15" or "1.19" or "1.22" or "1.21" or "1.20" or "1.18" or "1.17" or "1.16" or WorkflowSchemaVersions.AgentGraphFunctionTools or WorkflowSchemaVersions.AgentParameterBindings or WorkflowSchemaVersions.AgentExecutionRoutes or WorkflowSchemaVersions.IntegerGraph or WorkflowSchemaVersions.ConfigurableParallelGraph or WorkflowSchemaVersions.CollaborativeParallelGraph or WorkflowSchemaVersions.BranchLocalParallelGraph or WorkflowSchemaVersions.PhasedCollaborativeParallelGraph)) Error("node.schema", $"nodes.{id}", "Agent nodes require schema 1.2 or later.");
                if (node.Agent is not { } agent || string.IsNullOrWhiteSpace(agent.ProviderId) || agent.ProviderId.Length > 128 ||
                    agent.Instructions is null || agent.Instructions.Length > 8192 || agent.Tools.IsDefault ||
                    agent.Tools.Any(t => t is null || !ValidBinding(t)) || agent.Tools.Select(t => t.BindingId).Distinct(StringComparer.Ordinal).Count() != agent.Tools.Length)
                    Error("agent.configuration", $"nodes.{id}.agent", "An agent requires a model connection ID, bounded instructions, and unique tool requirements.");
                if (node.Agent is { } configured)
                {
                    if (configured.Routes is { } routes &&
                        (definition.SchemaVersion != WorkflowSchemaVersions.AgentExecutionRoutes || routes.IsDefault || routes.Length is < 1 or > AgentExecutionRouteLimits.MaximumRoutes ||
                         routes.Any(route => route is null || !ValidParameterId(route.Id) || route.Label is null || string.IsNullOrWhiteSpace(route.Label) || route.Label.Length > AgentExecutionRouteLimits.MaximumLabelLength || route.Label.Any(char.IsControl) || route.Description is null || route.Description.Length > AgentExecutionRouteLimits.MaximumDescriptionLength) ||
                         routes.Select(route => route.Id).Distinct(StringComparer.Ordinal).Count() != routes.Length))
                        Error("agent.routes", $"nodes.{id}.agent.routes", $"Agent routes require schema 1.30 and 1–{AgentExecutionRouteLimits.MaximumRoutes} unique routes with stable IDs, labels, and bounded descriptions.");
                    if (configured.UserMessage is { } template)
                    {
                        if (definition.SchemaVersion is not ("1.7" or "1.8" or "1.9" or "1.10" or "1.11" or "1.12" or "1.13" or "1.14" or "1.15" or "1.19" or "1.22" or "1.21" or "1.20" or "1.18" or "1.17" or "1.16" or WorkflowSchemaVersions.AgentGraphFunctionTools or WorkflowSchemaVersions.AgentParameterBindings or WorkflowSchemaVersions.AgentExecutionRoutes or WorkflowSchemaVersions.IntegerGraph or WorkflowSchemaVersions.ConfigurableParallelGraph or WorkflowSchemaVersions.CollaborativeParallelGraph or WorkflowSchemaVersions.BranchLocalParallelGraph or WorkflowSchemaVersions.PhasedCollaborativeParallelGraph) || template.Parts.IsDefault || template.Parts.Length is < 1 or > 64 ||
                            template.Parts.Any(part => part is null || part.Value is null || part.Value.Length > 8192 ||
                                part.Kind is not ("literal" or "input" or "parameter") ||
                                part.Kind == "parameter" && !parameters.ContainsKey(part.Value)))
                            Error("agent.prompt", $"nodes.{id}.agent.userMessage", "Use up to 64 literal, input or declared-parameter prompt parts in schema 1.7.");
                    }
                    if (configured.Skills is { } references)
                    {
                        if (definition.SchemaVersion is not ("1.7" or "1.8" or "1.9" or "1.10" or "1.11" or "1.12" or "1.13" or "1.14" or "1.15" or "1.19" or "1.22" or "1.21" or "1.20" or "1.18" or "1.17" or "1.16" or WorkflowSchemaVersions.AgentGraphFunctionTools or WorkflowSchemaVersions.AgentParameterBindings or WorkflowSchemaVersions.AgentExecutionRoutes or WorkflowSchemaVersions.IntegerGraph or WorkflowSchemaVersions.ConfigurableParallelGraph or WorkflowSchemaVersions.CollaborativeParallelGraph or WorkflowSchemaVersions.BranchLocalParallelGraph or WorkflowSchemaVersions.PhasedCollaborativeParallelGraph) || references.IsDefault || references.Length > 8 ||
                            references.Distinct().Count() != references.Length || references.Any(reference => reference is null ||
                                !skills.TryGetValue(reference.Id + "@" + reference.Version, out var skill) || skill.ContentHash != reference.ContentHash))
                            Error("agent.skills", $"nodes.{id}.agent.skills", "Each assigned skill must match an embedded immutable revision.");
                    }
                    if (configured.ReasoningEffort is not null &&
                        (definition.SchemaVersion is not ("1.7" or "1.8" or "1.9" or "1.10" or "1.11" or "1.12" or "1.13" or "1.14" or "1.15" or "1.19" or "1.22" or "1.21" or "1.20" or "1.18" or "1.17" or "1.16" or WorkflowSchemaVersions.AgentGraphFunctionTools or WorkflowSchemaVersions.AgentParameterBindings or WorkflowSchemaVersions.AgentExecutionRoutes or WorkflowSchemaVersions.IntegerGraph or WorkflowSchemaVersions.ConfigurableParallelGraph or WorkflowSchemaVersions.CollaborativeParallelGraph or WorkflowSchemaVersions.BranchLocalParallelGraph or WorkflowSchemaVersions.PhasedCollaborativeParallelGraph) || !Enum.IsDefined(configured.ReasoningEffort.Value) ||
                         configured.ReasoningEffort == AgentReasoningEffort.Off && definition.SchemaVersion is not ("1.10" or "1.11" or "1.12" or "1.13" or "1.14" or "1.15" or "1.19" or "1.22" or "1.21" or "1.20" or "1.18" or "1.17" or "1.16" or WorkflowSchemaVersions.AgentGraphFunctionTools or WorkflowSchemaVersions.AgentParameterBindings or WorkflowSchemaVersions.AgentExecutionRoutes or WorkflowSchemaVersions.IntegerGraph or WorkflowSchemaVersions.ConfigurableParallelGraph or WorkflowSchemaVersions.CollaborativeParallelGraph or WorkflowSchemaVersions.BranchLocalParallelGraph or WorkflowSchemaVersions.PhasedCollaborativeParallelGraph) ||
                         configured.ReasoningEffortParameterId is not null))
                        Error("agent.reasoning", $"nodes.{id}.agent.reasoningEffort", "Reasoning effort requires schema 1.7; explicit off requires schema 1.10.");
                    if (configured.MaxOutputTokens is { } maxOutputTokens &&
                        (definition.SchemaVersion is not ("1.10" or "1.11" or "1.12" or "1.13" or "1.14" or "1.15" or "1.19" or "1.22" or "1.21" or "1.20" or "1.18" or "1.17" or "1.16" or WorkflowSchemaVersions.AgentGraphFunctionTools or WorkflowSchemaVersions.AgentParameterBindings or WorkflowSchemaVersions.AgentExecutionRoutes or WorkflowSchemaVersions.IntegerGraph or WorkflowSchemaVersions.ConfigurableParallelGraph or WorkflowSchemaVersions.CollaborativeParallelGraph or WorkflowSchemaVersions.BranchLocalParallelGraph or WorkflowSchemaVersions.PhasedCollaborativeParallelGraph) || maxOutputTokens is < AgentOutputTokenLimits.Minimum or > AgentOutputTokenLimits.Maximum))
                        Error("agent.outputTokens", $"nodes.{id}.agent.maxOutputTokens", "Maximum output tokens require schema 1.10 and a value from 1 to 16,384.");
                    if (configured.MaxOutputTokensParameterId is { } outputParameter &&
                        (definition.SchemaVersion is not (WorkflowSchemaVersions.AgentParameterBindings or WorkflowSchemaVersions.AgentExecutionRoutes or WorkflowSchemaVersions.IntegerGraph or WorkflowSchemaVersions.ConfigurableParallelGraph or WorkflowSchemaVersions.CollaborativeParallelGraph or WorkflowSchemaVersions.BranchLocalParallelGraph or WorkflowSchemaVersions.PhasedCollaborativeParallelGraph) || configured.MaxOutputTokens is not null ||
                         !parameters.TryGetValue(outputParameter, out var outputDefinition) || outputDefinition.Type != "integer" ||
                         !int.TryParse(outputDefinition.DefaultValue, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var outputDefault) ||
                         outputDefault is < AgentOutputTokenLimits.Minimum or > AgentOutputTokenLimits.Maximum))
                        Error("agent.outputTokens.parameter", $"nodes.{id}.agent.maxOutputTokensParameterId", $"Maximum output token bindings require schema 1.29, an integer parameter with a default from {AgentOutputTokenLimits.Minimum} to {AgentOutputTokenLimits.Maximum}, and no static output limit.");
                    if (configured.MaxTotalTokens is { } maxTotalTokens &&
                        (definition.SchemaVersion is not ("1.22" or WorkflowSchemaVersions.AgentGraphFunctionTools or WorkflowSchemaVersions.AgentParameterBindings or WorkflowSchemaVersions.AgentExecutionRoutes or WorkflowSchemaVersions.IntegerGraph or WorkflowSchemaVersions.ConfigurableParallelGraph or WorkflowSchemaVersions.CollaborativeParallelGraph or WorkflowSchemaVersions.BranchLocalParallelGraph or WorkflowSchemaVersions.PhasedCollaborativeParallelGraph) || maxTotalTokens is < AgentTotalTokenLimits.Minimum or > AgentTotalTokenLimits.Maximum))
                        Error("agent.totalTokens", $"nodes.{id}.agent.maxTotalTokens", $"Maximum total tokens require schema 1.22 and a value from {AgentTotalTokenLimits.Minimum} to {AgentTotalTokenLimits.Maximum}.");
                    if (configured.MaxTotalTokensParameterId is { } totalParameter &&
                        (definition.SchemaVersion is not (WorkflowSchemaVersions.AgentParameterBindings or WorkflowSchemaVersions.AgentExecutionRoutes or WorkflowSchemaVersions.IntegerGraph or WorkflowSchemaVersions.ConfigurableParallelGraph or WorkflowSchemaVersions.CollaborativeParallelGraph or WorkflowSchemaVersions.BranchLocalParallelGraph or WorkflowSchemaVersions.PhasedCollaborativeParallelGraph) || configured.MaxTotalTokens is not null ||
                         !parameters.TryGetValue(totalParameter, out var totalDefinition) || totalDefinition.Type != "integer" ||
                         !int.TryParse(totalDefinition.DefaultValue, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var totalDefault) ||
                         totalDefault is < AgentTotalTokenLimits.Minimum or > AgentTotalTokenLimits.Maximum))
                        Error("agent.totalTokens.parameter", $"nodes.{id}.agent.maxTotalTokensParameterId", $"Maximum total-token bindings require schema 1.29, an integer parameter with a default from {AgentTotalTokenLimits.Minimum} to {AgentTotalTokenLimits.Maximum}, and no static total-token limit.");
                    if (configured.MaxTurns is { } maxTurns &&
                        (definition.SchemaVersion is not ("1.21" or "1.22" or WorkflowSchemaVersions.AgentGraphFunctionTools or WorkflowSchemaVersions.AgentParameterBindings or WorkflowSchemaVersions.AgentExecutionRoutes or WorkflowSchemaVersions.IntegerGraph or WorkflowSchemaVersions.ConfigurableParallelGraph or WorkflowSchemaVersions.CollaborativeParallelGraph or WorkflowSchemaVersions.BranchLocalParallelGraph or WorkflowSchemaVersions.PhasedCollaborativeParallelGraph) || !AgentExecutionLimits.IsValidMaximumTurns(maxTurns)))
                        Error("agent.maxTurns", $"nodes.{id}.agent.maxTurns", "Maximum turns require schema 1.21 or later and must be -1 (unlimited) or a positive integer.");
                    if (configured.TimeoutSeconds is { } timeoutSeconds &&
                        (definition.SchemaVersion is not ("1.21" or "1.22" or WorkflowSchemaVersions.AgentGraphFunctionTools or WorkflowSchemaVersions.AgentParameterBindings or WorkflowSchemaVersions.AgentExecutionRoutes or WorkflowSchemaVersions.IntegerGraph or WorkflowSchemaVersions.ConfigurableParallelGraph or WorkflowSchemaVersions.CollaborativeParallelGraph or WorkflowSchemaVersions.BranchLocalParallelGraph or WorkflowSchemaVersions.PhasedCollaborativeParallelGraph) || !AgentExecutionLimits.IsValidTimeoutSeconds(timeoutSeconds)))
                        Error("agent.timeout", $"nodes.{id}.agent.timeoutSeconds", "Agent timeout requires schema 1.21 or later and a positive whole number of seconds.");
                    if (configured.RequireStructuredResult is { } structuredResult &&
                        (definition.SchemaVersion is not ("1.21" or "1.22" or WorkflowSchemaVersions.AgentGraphFunctionTools or WorkflowSchemaVersions.AgentParameterBindings or WorkflowSchemaVersions.AgentExecutionRoutes or WorkflowSchemaVersions.IntegerGraph or WorkflowSchemaVersions.ConfigurableParallelGraph or WorkflowSchemaVersions.CollaborativeParallelGraph or WorkflowSchemaVersions.BranchLocalParallelGraph or WorkflowSchemaVersions.PhasedCollaborativeParallelGraph) || !structuredResult))
                        Error("agent.structuredResult", $"nodes.{id}.agent.requireStructuredResult", "Structured agent results require schema 1.21 or later and must be enabled explicitly.");
                    if (configured.ReasoningEffortParameterId is { } reasoningParameter &&
                        (definition.SchemaVersion is not ("1.8" or "1.9" or "1.10" or "1.11" or "1.12" or "1.13" or "1.14" or "1.15" or "1.19" or "1.22" or "1.21" or "1.20" or "1.18" or "1.17" or "1.16" or WorkflowSchemaVersions.AgentGraphFunctionTools or WorkflowSchemaVersions.AgentParameterBindings or WorkflowSchemaVersions.AgentExecutionRoutes or WorkflowSchemaVersions.IntegerGraph or WorkflowSchemaVersions.ConfigurableParallelGraph or WorkflowSchemaVersions.CollaborativeParallelGraph or WorkflowSchemaVersions.BranchLocalParallelGraph or WorkflowSchemaVersions.PhasedCollaborativeParallelGraph) || !parameters.TryGetValue(reasoningParameter, out var reasoningDefinition) ||
                         reasoningDefinition.Type != "choice" || reasoningDefinition.Choices is not { } effortChoices ||
                         !effortChoices.Contains("default") || !effortChoices.Contains("low") || !effortChoices.Contains("medium") || !effortChoices.Contains("high") ||
                         effortChoices.Contains("off") && definition.SchemaVersion is not ("1.10" or "1.11" or "1.12" or "1.13" or "1.14" or "1.15" or "1.19" or "1.22" or "1.21" or "1.20" or "1.18" or "1.17" or "1.16" or WorkflowSchemaVersions.AgentGraphFunctionTools or WorkflowSchemaVersions.AgentParameterBindings or WorkflowSchemaVersions.AgentExecutionRoutes or WorkflowSchemaVersions.IntegerGraph or WorkflowSchemaVersions.ConfigurableParallelGraph or WorkflowSchemaVersions.CollaborativeParallelGraph or WorkflowSchemaVersions.BranchLocalParallelGraph or WorkflowSchemaVersions.PhasedCollaborativeParallelGraph)))
                        Error("agent.reasoning.parameter", $"nodes.{id}.agent.reasoningEffortParameterId", "Use a schema 1.8 choice parameter containing default, low, medium, and high.");
                    if (configured.Delegates is { } delegates &&
                        (definition.SchemaVersion is not ("1.11" or "1.12" or "1.13" or "1.14" or "1.15" or "1.19" or "1.22" or "1.21" or "1.20" or "1.18" or "1.17" or "1.16" or WorkflowSchemaVersions.AgentGraphFunctionTools or WorkflowSchemaVersions.AgentParameterBindings or WorkflowSchemaVersions.AgentExecutionRoutes or WorkflowSchemaVersions.IntegerGraph or WorkflowSchemaVersions.ConfigurableParallelGraph or WorkflowSchemaVersions.CollaborativeParallelGraph or WorkflowSchemaVersions.BranchLocalParallelGraph or WorkflowSchemaVersions.PhasedCollaborativeParallelGraph) || delegates.IsDefault || delegates.Length > 4 || delegates.Distinct().Count() != delegates.Length ||
                         delegates.Any(reference => reference is null || !profiles.TryGetValue(reference.Id + "@" + reference.Version, out var profile) || profile is null ||
                             profile.ContentHash != reference.ContentHash ||
                             (profile.Skills ?? []).Any(profileSkill => !skills.TryGetValue(profileSkill.Id + "@" + profileSkill.Version, out var workflowSkill) || workflowSkill != profileSkill))))
                        Error("agent.delegates", $"nodes.{id}.agent.delegates", "Assign up to four embedded, immutable agent profile revisions in schema 1.11 or later.");
                    if (configured.GraphFunctionTools is { } graphFunctionTools)
                    {
                        var invalidFunctionTool = graphFunctionTools.IsDefault || graphFunctionTools.Any(function => function is null ||
                            !ValidHash(function.ContentHash) || function.Definition is null ||
                            !string.Equals(GraphFunctionJson.ComputeHash(function.Definition), function.ContentHash, StringComparison.Ordinal) ||
                            !GraphFunctionJson.Validate(function.Definition).IsEmpty);
                        if (!WorkflowSchemaVersions.UsesExplicitGraph(definition) || definition.SchemaVersion is not (WorkflowSchemaVersions.AgentGraphFunctionTools or WorkflowSchemaVersions.AgentParameterBindings or WorkflowSchemaVersions.AgentExecutionRoutes or WorkflowSchemaVersions.IntegerGraph or WorkflowSchemaVersions.ConfigurableParallelGraph or WorkflowSchemaVersions.CollaborativeParallelGraph or WorkflowSchemaVersions.BranchLocalParallelGraph or WorkflowSchemaVersions.PhasedCollaborativeParallelGraph) ||
                            graphFunctionTools.Length > AgentGraphFunctionToolLimits.MaximumAssignedFunctions || invalidFunctionTool ||
                            graphFunctionTools.Select(function => function.Definition.FunctionId).Distinct(StringComparer.Ordinal).Count() != graphFunctionTools.Length)
                            Error("agent.graphFunctionTools", $"nodes.{id}.agent.graphFunctionTools", $"Assign up to {AgentGraphFunctionToolLimits.MaximumAssignedFunctions} unique, valid, immutable pure graph-function revisions in schema 1.28.");
                    }
                }
            }
            else if (node.Agent is not null)
                Error("agent.unexpected", $"nodes.{id}.agent", "Only agent nodes may declare agent configuration.");
        }

        if (string.IsNullOrWhiteSpace(definition.EntryNodeId) ||
            !definition.Nodes.TryGetValue(definition.EntryNodeId, out var entry) || entry?.Type != NodeTypes.Input)
            Error("entry.invalid", "entryNodeId", "The entry must reference an input node.");

        if (WorkflowSchemaVersions.UsesExplicitGraph(definition))
        {
            if (definition.SchemaVersion is WorkflowSchemaVersions.DirectMcpGraph or WorkflowSchemaVersions.DirectMcpPinsGraph)
                ValidateDirectMcpGraph(definition, Error);
            else if (definition.SchemaVersion == WorkflowSchemaVersions.ExplicitGraph)
                ValidateExplicitGraph(definition, Error);
            else if (definition.SchemaVersion == WorkflowSchemaVersions.ConfigurableParallelGraph)
            {
                ValidateExplicitBranchGraph(definition, Error, allowBooleanTopology: true, allowGraphFunctions: true,
                    allowAgentNodes: true, allowConfigurableParallel: true);
                ValidateConfigurableParallelGraph(definition, Error);
            }
            else if (definition.SchemaVersion == WorkflowSchemaVersions.CollaborativeParallelGraph)
            {
                ValidateExplicitBranchGraph(definition, Error, allowBooleanTopology: true, allowGraphFunctions: true,
                    allowAgentNodes: true, allowConfigurableParallel: true, allowCollaborativeParallel: true);
                ValidateCollaborativeParallelGraph(definition, Error);
            }
            else if (definition.SchemaVersion == WorkflowSchemaVersions.PhasedCollaborativeParallelGraph)
            {
                ValidateExplicitBranchGraph(definition, Error, allowBooleanTopology: true, allowGraphFunctions: true,
                    allowAgentNodes: true, allowConfigurableParallel: true, allowCollaborativeParallel: true);
                ValidatePhasedCollaborativeParallelGraph(definition, Error);
            }
            else if (definition.SchemaVersion == WorkflowSchemaVersions.BranchLocalParallelGraph)
            {
                ValidateExplicitBranchGraph(definition, Error, allowBooleanTopology: true, allowGraphFunctions: true,
                    allowAgentNodes: true, allowConfigurableParallel: true);
                ValidateConfigurableParallelGraph(definition, Error, allowBranchLocalRouting: true);
            }
            else if (definition.SchemaVersion is WorkflowSchemaVersions.BooleanTopologyGraph or WorkflowSchemaVersions.PureGraphFunctions or WorkflowSchemaVersions.AgentGraphFunctionTools or WorkflowSchemaVersions.AgentParameterBindings or WorkflowSchemaVersions.AgentExecutionRoutes or WorkflowSchemaVersions.IntegerGraph or WorkflowSchemaVersions.ConfigurableParallelGraph or WorkflowSchemaVersions.CollaborativeParallelGraph or WorkflowSchemaVersions.BranchLocalParallelGraph or WorkflowSchemaVersions.PhasedCollaborativeParallelGraph)
                ValidateExplicitBranchGraph(definition, Error, allowBooleanTopology: true,
                    allowGraphFunctions: definition.SchemaVersion is WorkflowSchemaVersions.PureGraphFunctions or WorkflowSchemaVersions.AgentGraphFunctionTools or WorkflowSchemaVersions.AgentParameterBindings or WorkflowSchemaVersions.AgentExecutionRoutes or WorkflowSchemaVersions.IntegerGraph,
                    allowAgentNodes: definition.SchemaVersion is WorkflowSchemaVersions.AgentGraphFunctionTools or WorkflowSchemaVersions.AgentParameterBindings or WorkflowSchemaVersions.AgentExecutionRoutes or WorkflowSchemaVersions.IntegerGraph or WorkflowSchemaVersions.ConfigurableParallelGraph or WorkflowSchemaVersions.CollaborativeParallelGraph or WorkflowSchemaVersions.BranchLocalParallelGraph or WorkflowSchemaVersions.PhasedCollaborativeParallelGraph);
            else
                ValidateExplicitBranchGraph(definition, Error);
            return issues.ToImmutable();
        }
        if (definition.ExecutionConnections is not null)
            Error("execution.schema", "executionConnections", "Explicit execution edges require schema 1.23 or later.");

        var pairs = new HashSet<(string, string, string, string)>();
        foreach (var (id, edge) in definition.Connections)
        {
            var path = $"connections.{id}";
            if (string.IsNullOrWhiteSpace(id)) Error("connection.id", path, "Connection IDs must be nonempty.");
            if (edge?.Source?.NodeId is not { } source || edge.Target?.NodeId is not { } target ||
                !definition.Nodes.ContainsKey(source) || !definition.Nodes.ContainsKey(target))
            {
                Error("connection.reference", path, "Both endpoints must reference existing nodes.");
                continue;
            }
            var sourcePort = WorkflowPorts.Outputs(definition.Nodes[source]!).FirstOrDefault(port => port.Id == edge.Source.PortId);
            var targetPort = WorkflowPorts.Inputs(definition.Nodes[target]!).FirstOrDefault(port => port.Id == edge.Target.PortId);
            if (edge.Kind != "data" || sourcePort is null || targetPort is null || !WorkflowDataTypeCatalog.AreCompatible(sourcePort.DataType, targetPort.DataType))
                Error("connection.port", path, "Connect ports with matching declared data types using a data link.");
            if (!pairs.Add((source, edge.Source.PortId, target, edge.Target.PortId))) Error("connection.duplicate", path, "Duplicate connection.");
            incoming[target]++;
            outgoing[source].Add(target);
        }

        foreach (var (id, node) in definition.Nodes)
        {
            if (node is null) continue;
            if (node.Type == NodeTypes.Terminate && definition.SchemaVersion is not ("1.18" or "1.19" or "1.22" or "1.21" or "1.20" or WorkflowSchemaVersions.AgentGraphFunctionTools or WorkflowSchemaVersions.AgentParameterBindings or WorkflowSchemaVersions.AgentExecutionRoutes or WorkflowSchemaVersions.IntegerGraph or WorkflowSchemaVersions.ConfigurableParallelGraph or WorkflowSchemaVersions.CollaborativeParallelGraph or WorkflowSchemaVersions.BranchLocalParallelGraph or WorkflowSchemaVersions.PhasedCollaborativeParallelGraph))
                Error("termination.schema", $"nodes.{id}", "Explicit workflow termination requires schema 1.18 or later.");
            if (node.Termination is not null && node.Type != NodeTypes.Terminate)
                Error("termination.configuration", $"nodes.{id}.termination", "Termination outcome settings are only valid on a terminate node.");
            if (node.Termination is { } termination &&
                (definition.SchemaVersion is not ("1.20" or "1.22" or "1.21" or WorkflowSchemaVersions.AgentGraphFunctionTools or WorkflowSchemaVersions.AgentParameterBindings or WorkflowSchemaVersions.AgentExecutionRoutes or WorkflowSchemaVersions.IntegerGraph or WorkflowSchemaVersions.ConfigurableParallelGraph or WorkflowSchemaVersions.CollaborativeParallelGraph or WorkflowSchemaVersions.BranchLocalParallelGraph or WorkflowSchemaVersions.PhasedCollaborativeParallelGraph) || termination.Outcome is not (WorkflowTerminationOutcomes.Success or WorkflowTerminationOutcomes.Failure)))
                Error("termination.outcome", $"nodes.{id}.termination.outcome", "Configured termination outcomes require schema 1.20 or 1.21 and must be success or failure.");
            if (definition.SchemaVersion is "1.4" or "1.5" or "1.6" or "1.7" or "1.8" or "1.9" or "1.10" or "1.11" or "1.12" or "1.13" or "1.14" or "1.15" or "1.16" or "1.17" or "1.19" or "1.22" or "1.21" or "1.20" or "1.18")
            {
                var validIncoming = node.Type == NodeTypes.Input ? incoming[id] == 0 : node.Type == NodeTypes.Terminate ? incoming[id] == 1 : node.Type == NodeTypes.Output && hasTerminationExit && incoming[id] == 0 ? true : node.Type is NodeTypes.Merge or NodeTypes.Output
                    ? incoming[id] is >= 1 and <= 16 : node.Type == NodeTypes.ParallelJoin ? incoming[id] == 2 : node.Type == NodeTypes.Loop ? incoming[id] == 2 : incoming[id] == 1;
                var expected = WorkflowPorts.Outputs(node);
                var edges = definition.Connections.Values.Where(e => e?.Source?.NodeId == id).ToArray();
                if (!validIncoming || edges.Length != expected.Length || expected.Any(p => edges.Count(e => e.Source.PortId == p.Id) != 1))
                    Error("graph.degree", $"nodes.{id}", "Each output pin must connect once; loops have one initial and one body return input, normal nodes have one input, parallel joins have two branch inputs, exclusive merges and output accept 1–16 paths, and input has none.");
                continue;
            }
            var expectedIncoming = node.Type == NodeTypes.Input ? 0 : 1;
            var expectedOutgoing = node.Type == NodeTypes.Output ? 0 : 1;
            if (incoming[id] != expectedIncoming || outgoing[id].Count != expectedOutgoing)
                Error("workflow.degree", $"nodes.{id}", "Only a single input-to-output workflow is supported; branches and joins are not yet supported.");
        }
        var terminators = definition.Nodes.Where(n => n.Value?.Type == NodeTypes.Terminate).Select(n => n.Key).ToHashSet(StringComparer.Ordinal);
        if (definition.Nodes.Values.Count(n => n?.Type == NodeTypes.Terminate) > 16)
            Error("graph.termination.limit", "nodes", "A workflow may have at most 16 termination exits.");
        if (definition.Nodes.Values.Count(n => n?.Type == NodeTypes.Input) != 1 ||
            definition.Nodes.Values.Count(n => n?.Type == NodeTypes.Output) != 1)
            Error("workflow.terminals", "nodes", "Exactly one input and one output node are required.");

        ValidateParallelRegion(definition, Error);

        var loopBackConnection = ValidateLoopRegion(definition, incoming, Error);

        if (WorkflowGraph.TopologicalOrder(definition, loopBackConnection).Length != definition.Nodes.Count)
            Error("graph.cycle", "connections", "Only the single validated bounded loop-back edge is supported; other cycles are invalid.");
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Stack<string>();
        if (definition.EntryNodeId is { } entryId && outgoing.ContainsKey(entryId)) pending.Push(entryId);
        while (pending.TryPop(out var id))
        {
            if (!visited.Add(id)) continue;
            foreach (var next in outgoing[id]) pending.Push(next);
        }
        if (hasTerminationExit && definition.Connections.Values.All(edge => edge.Target.NodeId != definition.Nodes.Single(n => n.Value?.Type == NodeTypes.Output).Key))
            visited.Add(definition.Nodes.Single(n => n.Value?.Type == NodeTypes.Output).Key);
        if (visited.Count != definition.Nodes.Count)
            Error("graph.unreachable", "nodes", "Every node must be reachable from the entry.");
        return issues.ToImmutable();
    }

    private static bool IsJsonObject(string value)
    {
        try
        {
            using var document = JsonDocument.Parse(value, new JsonDocumentOptions { MaxDepth = 32 });
            return document.RootElement.ValueKind == JsonValueKind.Object;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static void ValidateDirectMcpGraph(WorkflowDefinition definition, Action<string, string, string> error)
    {
        var pinsGraph = definition.SchemaVersion == WorkflowSchemaVersions.DirectMcpPinsGraph;
        if (definition.ExecutionConnections is null)
        {
            error("execution.missing", "executionConnections", $"Schema {definition.SchemaVersion} requires explicit execution connections.");
            return;
        }

        var supportedTypes = new HashSet<string>(StringComparer.Ordinal)
        {
            NodeTypes.Input, NodeTypes.Output, NodeTypes.StringConstant, NodeTypes.TextConcat,
            NodeTypes.IntegerConstant, NodeTypes.BooleanConstant, NodeTypes.McpTool
        };
        foreach (var (id, node) in definition.Nodes)
            if (node is null || !supportedTypes.Contains(node.Type))
                error("execution.node.unsupported", $"nodes.{id}", $"Schema {definition.SchemaVersion} supports input, output, deterministic text nodes, and direct MCP tool nodes.");

        var input = definition.Nodes.SingleOrDefault(pair => pair.Value?.Type == NodeTypes.Input).Key;
        var output = definition.Nodes.SingleOrDefault(pair => pair.Value?.Type == NodeTypes.Output).Key;
        if (definition.Nodes.Count is < 2 or > 256 || definition.Connections.Count + definition.ExecutionConnections.Count > 510)
            error("graph.limit", "nodes", "An explicit graph requires 2–256 nodes and at most 510 total data and execution connections.");
        if (string.IsNullOrWhiteSpace(input) || string.IsNullOrWhiteSpace(output) || input != definition.EntryNodeId ||
            definition.Nodes.Count(node => node.Value?.Type == NodeTypes.Input) != 1 ||
            definition.Nodes.Count(node => node.Value?.Type == NodeTypes.Output) != 1)
            error("workflow.terminals", "nodes", "A direct MCP graph requires one configured input entry and one output node.");

        var seen = new HashSet<(string Kind, string Source, string SourcePort, string Target, string TargetPort)>();
        var dataIncoming = new Dictionary<(string Node, string Port), int>();
        var dataDependencies = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var (id, edge) in definition.Connections)
        {
            var path = $"connections.{id}";
            if (edge?.Source?.NodeId is not { } source || edge.Target?.NodeId is not { } target ||
                !definition.Nodes.TryGetValue(source, out var sourceNode) || !definition.Nodes.TryGetValue(target, out var targetNode) ||
                sourceNode is null || targetNode is null)
            {
                error("connection.reference", path, "Both data-edge endpoints must reference existing nodes and ports.");
                continue;
            }
            var sourcePort = WorkflowPorts.ExplicitDataOutputs(sourceNode).FirstOrDefault(port => port.Id == edge.Source.PortId);
            var targetPort = WorkflowPorts.ExplicitDataInputs(targetNode).FirstOrDefault(port => port.Id == edge.Target.PortId);
            if (edge.Kind != "data" || sourcePort is null || targetPort is null || !WorkflowDataTypeCatalog.AreCompatible(sourcePort.DataType, targetPort.DataType))
                error("connection.port", path, "Data edges must connect compatible typed data ports; they never schedule execution.");
            if (!seen.Add(("data", source, edge.Source.PortId, target, edge.Target.PortId)))
                error("connection.duplicate", path, "Duplicate data edge.");
            dataIncoming[(target, edge.Target.PortId)] = dataIncoming.GetValueOrDefault((target, edge.Target.PortId)) + 1;
            if (!dataDependencies.TryGetValue(target, out var upstream)) dataDependencies[target] = upstream = [];
            upstream.Add(source);
        }

        var executionIncoming = new Dictionary<(string Node, string Port), int>();
        var executionOutgoing = new Dictionary<(string Node, string Port), int>();
        var executionAdjacency = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var (id, edge) in definition.ExecutionConnections)
        {
            var path = $"executionConnections.{id}";
            if (edge?.Source?.NodeId is not { } source || edge.Target?.NodeId is not { } target ||
                !definition.Nodes.TryGetValue(source, out var sourceNode) || !definition.Nodes.TryGetValue(target, out var targetNode) ||
                sourceNode is null || targetNode is null)
            {
                error("execution.reference", path, "Both execution-edge endpoints must reference existing nodes and ports.");
                continue;
            }
            var validPorts = edge.Kind == "execution" && WorkflowPorts.ExplicitExecutionOutputs(sourceNode).Contains(edge.Source.PortId) &&
                WorkflowPorts.ExplicitExecutionInputs(targetNode).Contains(edge.Target.PortId);
            var validRoute = sourceNode.Type is NodeTypes.Input or NodeTypes.McpTool && targetNode.Type is NodeTypes.McpTool or NodeTypes.Output;
            if (!validPorts) error("execution.port", path, "Execution edges must connect declared execution ports and cannot carry data.");
            if (!validRoute) error("execution.target", path, "Direct MCP execution routes must be a single acyclic input/tool/output path.");
            if (!seen.Add(("execution", source, edge.Source.PortId, target, edge.Target.PortId)))
                error("execution.duplicate", path, "Duplicate execution edge.");
            executionIncoming[(target, edge.Target.PortId)] = executionIncoming.GetValueOrDefault((target, edge.Target.PortId)) + 1;
            executionOutgoing[(source, edge.Source.PortId)] = executionOutgoing.GetValueOrDefault((source, edge.Source.PortId)) + 1;
            if (!executionAdjacency.TryGetValue(source, out var downstream)) executionAdjacency[source] = downstream = [];
            downstream.Add(target);
        }

        foreach (var (id, node) in definition.Nodes)
        {
            if (node is null) continue;
            if (pinsGraph && node.Type == NodeTypes.McpTool && node.McpInputPorts is null)
                error("mcp.input-ports", $"nodes.{id}.mcpInputPorts", "Schema 1.37 MCP nodes must persist their discovered top-level input pins.");
            var hasConfiguredMcpArguments = node.Type == NodeTypes.McpTool && node.McpArgumentsJson is not null;
            foreach (var port in WorkflowPorts.Explicit(node).Where(port => port.Direction == WorkflowPortDirection.Input && port.Kind == WorkflowPortKind.Data))
            {
                var count = dataIncoming.GetValueOrDefault((id, port.Id));
                if (pinsGraph && node.Type == NodeTypes.McpTool && node.McpInputPorts is { } inputPorts && inputPorts.FirstOrDefault(candidate => candidate.Id == port.Id) is { } inputPort)
                {
                    if (port.Cardinality == WorkflowPortCardinality.AtMostOne && count > 1)
                        error("data.degree", $"nodes.{id}.inputs.{port.Id}", "Each MCP argument pin accepts at most one data edge.");
                    else if (count == 0 && inputPort.Required && !HasStaticArgument(node.McpArgumentsJson, inputPort.Name))
                        error("data.degree", $"nodes.{id}.inputs.{port.Id}", "Every required MCP argument must be connected or configured as a static argument.");
                }
                else if (node.Type == NodeTypes.McpTool && port.Id == "in" && hasConfiguredMcpArguments)
                {
                    if (port.Cardinality == WorkflowPortCardinality.AtMostOne && count > 1)
                        error("data.degree", $"nodes.{id}.inputs.{port.Id}", "A configured direct MCP tool accepts at most one optional connected input; configured arguments take precedence.");
                }
                else if (port.Required && port.Cardinality == WorkflowPortCardinality.ExactlyOne && count != 1)
                    error("data.degree", $"nodes.{id}.inputs.{port.Id}", "Every required data input must have exactly one compatible data edge.");
            }
            var executionInputs = WorkflowPorts.Explicit(node).Where(port => port.Direction == WorkflowPortDirection.Input && port.Kind == WorkflowPortKind.Execution).ToArray();
            var executionOutputs = WorkflowPorts.Explicit(node).Where(port => port.Direction == WorkflowPortDirection.Output && port.Kind == WorkflowPortKind.Execution).ToArray();
            var incoming = executionInputs.Sum(port => executionIncoming.GetValueOrDefault((id, port.Id)));
            if (node.Type == NodeTypes.Input && incoming != 0 || node.Type is NodeTypes.McpTool or NodeTypes.Output && incoming != 1)
                error("graph.degree", $"nodes.{id}", "Input has no execution input; MCP tools and output require one activation.");
            foreach (var port in executionOutputs)
                if (port.Required && port.Cardinality == WorkflowPortCardinality.ExactlyOne && executionOutgoing.GetValueOrDefault((id, port.Id)) != 1)
                    error("graph.degree", $"nodes.{id}.outputs.{port.Id}", "Every execution output pin must connect exactly once, except the output node which has none.");
            if ((node.Type is NodeTypes.StringConstant or NodeTypes.TextConcat or NodeTypes.IntegerConstant or NodeTypes.BooleanConstant) &&
                !definition.Connections.Values.Any(edge => edge.Source.NodeId == id))
                error("data.unused", $"nodes.{id}", "Every pure data node must contribute to a tool input or output value.");
        }

        if (definition.ExecutionConnections.Count < 1 || input is null || output is null ||
            definition.ExecutionConnections.Count != definition.Nodes.Count(node => node.Value?.Type is NodeTypes.Input or NodeTypes.McpTool) ||
            definition.ExecutionConnections.Values.Any(edge => edge.Source.NodeId == output || edge.Target.NodeId == input))
            error("execution.path", "executionConnections", "Direct MCP execution must be one input-to-tool-to-output path.");

        var reached = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Stack<string>();
        if (input is not null) pending.Push(input);
        while (pending.TryPop(out var current))
        {
            if (!reached.Add(current)) continue;
            foreach (var next in executionAdjacency.GetValueOrDefault(current) ?? []) pending.Push(next);
        }
        if (reached.Count != definition.Nodes.Count(node => node.Value?.Type is NodeTypes.Input or NodeTypes.McpTool or NodeTypes.Output))
            error("graph.unreachable", "executionConnections", "Every executable node must be reachable from the input entry.");
        if (HasCycle(executionAdjacency, reached))
            error("graph.cycle", "executionConnections", "Direct MCP execution routes must be acyclic.");
        if (WorkflowGraph.TopologicalOrder(definition).Length != definition.Nodes.Count)
            error("data.cycle", "connections", "Pure data dependencies must be acyclic.");

        var reachedData = new HashSet<string>(StringComparer.Ordinal);
        if (output is not null) pending = new Stack<string>([output]);
        while (pending.TryPop(out var current))
        {
            if (!reachedData.Add(current)) continue;
            foreach (var dependency in dataDependencies.GetValueOrDefault(current) ?? []) pending.Push(dependency);
        }
        foreach (var (id, node) in definition.Nodes)
            if (node?.Type is NodeTypes.StringConstant or NodeTypes.TextConcat && !reachedData.Contains(id))
                error("graph.unreachable", $"nodes.{id}", "Every pure data node must contribute to a tool input or output value.");
    }

    private static bool HasStaticArgument(string? arguments, string name)
    {
        if (arguments is null) return false;
        try
        {
            using var document = JsonDocument.Parse(arguments);
            return document.RootElement.TryGetProperty(name, out _);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static void ValidateExplicitGraph(WorkflowDefinition definition, Action<string, string, string> error)
    {
        if (definition.ExecutionConnections is null)
        {
            error("execution.missing", "executionConnections", "Schema 1.23 requires an explicit execution-connection collection.");
            return;
        }
        if (definition.Nodes.Count is < 2 or > 256 || definition.Connections.Count + definition.ExecutionConnections.Count > 510)
            error("graph.limit", "nodes", "An explicit graph requires 2–256 nodes and at most 510 total data and execution connections.");

        var supportedTypes = new HashSet<string>(StringComparer.Ordinal)
        {
            NodeTypes.Input, NodeTypes.Output, NodeTypes.StringConstant, NodeTypes.TextConcat
        };
        foreach (var (id, node) in definition.Nodes)
            if (node is null || !supportedTypes.Contains(node.Type))
                error("execution.node.unsupported", $"nodes.{id}", "Schema 1.23 currently supports input, output, string constant, and pure text concatenation nodes.");

        var inputs = definition.Nodes.Where(pair => pair.Value?.Type == NodeTypes.Input).Select(pair => pair.Key).ToArray();
        var outputs = definition.Nodes.Where(pair => pair.Value?.Type == NodeTypes.Output).Select(pair => pair.Key).ToArray();
        if (inputs.Length != 1 || outputs.Length != 1 || inputs[0] != definition.EntryNodeId)
            error("workflow.terminals", "nodes", "An explicit graph requires one configured input entry and one output node.");

        var seen = new HashSet<(string Kind, string Source, string SourcePort, string Target, string TargetPort)>();
        var dataIncoming = new Dictionary<(string Node, string Port), int>();
        var dataOutgoing = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var (id, edge) in definition.Connections)
        {
            var path = $"connections.{id}";
            if (string.IsNullOrWhiteSpace(id) || edge?.Source?.NodeId is not { } source || edge.Target?.NodeId is not { } target ||
                !definition.Nodes.TryGetValue(source, out var sourceNode) || !definition.Nodes.TryGetValue(target, out var targetNode) ||
                sourceNode is null || targetNode is null)
            {
                error("connection.reference", path, "Both data-edge endpoints must reference existing nodes and ports.");
                continue;
            }
            var sourcePort = WorkflowPorts.ExplicitDataOutputs(sourceNode).FirstOrDefault(port => port.Id == edge.Source.PortId);
            var targetPort = WorkflowPorts.ExplicitDataInputs(targetNode).FirstOrDefault(port => port.Id == edge.Target.PortId);
            if (edge.Kind != "data" || sourcePort is null || targetPort is null || !WorkflowDataTypeCatalog.AreCompatible(sourcePort.DataType, targetPort.DataType))
                error("connection.port", path, "Data edges must connect compatible typed data ports; they never schedule execution.");
            if (!seen.Add(("data", source, edge.Source.PortId, target, edge.Target.PortId)))
                error("connection.duplicate", path, "Duplicate data edge.");
            dataIncoming[(target, edge.Target.PortId)] = dataIncoming.GetValueOrDefault((target, edge.Target.PortId)) + 1;
            if (!dataOutgoing.TryGetValue(target, out var upstream)) dataOutgoing[target] = upstream = [];
            upstream.Add(source);
        }

        var executionIncoming = new Dictionary<(string Node, string Port), int>();
        var executionOutgoing = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var (id, edge) in definition.ExecutionConnections)
        {
            var path = $"executionConnections.{id}";
            if (string.IsNullOrWhiteSpace(id) || edge?.Source?.NodeId is not { } source || edge.Target?.NodeId is not { } target ||
                !definition.Nodes.TryGetValue(source, out var sourceNode) || !definition.Nodes.TryGetValue(target, out var targetNode) ||
                sourceNode is null || targetNode is null)
            {
                error("execution.reference", path, "Both execution-edge endpoints must reference existing nodes and ports.");
                continue;
            }
            var sourcePorts = WorkflowPorts.ExplicitExecutionOutputs(sourceNode);
            var targetPorts = WorkflowPorts.ExplicitExecutionInputs(targetNode);
            if (edge.Kind != "execution" || !sourcePorts.Contains(edge.Source.PortId) || !targetPorts.Contains(edge.Target.PortId))
                error("execution.port", path, "Execution edges must connect declared execution ports and cannot carry data.");
            if (!seen.Add(("execution", source, edge.Source.PortId, target, edge.Target.PortId)))
                error("execution.duplicate", path, "Duplicate execution edge.");
            executionIncoming[(target, edge.Target.PortId)] = executionIncoming.GetValueOrDefault((target, edge.Target.PortId)) + 1;
            if (!executionOutgoing.TryGetValue(source, out var downstream)) executionOutgoing[source] = downstream = [];
            downstream.Add(target);
        }

        foreach (var (id, node) in definition.Nodes)
        {
            if (node is null) continue;
            foreach (var port in WorkflowPorts.Explicit(node).Where(port => port.Direction == WorkflowPortDirection.Input && port.Kind == WorkflowPortKind.Data))
                if (port.Required && port.Cardinality == WorkflowPortCardinality.ExactlyOne && dataIncoming.GetValueOrDefault((id, port.Id)) != 1)
                    error("data.degree", $"nodes.{id}.inputs.{port.Id}", "Every required data input must have exactly one compatible data edge.");
            if (node.Type == NodeTypes.Input && executionIncoming.GetValueOrDefault((id, "execute")) != 0)
                error("execution.degree", $"nodes.{id}", "The input entry has no execution input.");
            if (node.Type == NodeTypes.Output && executionIncoming.GetValueOrDefault((id, "execute")) != 1)
                error("execution.degree", $"nodes.{id}", "The output node requires exactly one execution activation.");
            if (node.Type is NodeTypes.Input or NodeTypes.StringConstant or NodeTypes.TextConcat)
                if (WorkflowPorts.ExplicitDataOutputs(node).Length != 0 &&
                    definition.Connections.Values.Count(edge => edge?.Source?.NodeId == id) == 0 && node.Type != NodeTypes.Input)
                    error("data.unused", $"nodes.{id}", "Every pure data node must contribute to the output value.");
            if (node.Type == NodeTypes.Input && executionOutgoing.GetValueOrDefault(id)?.Count != 1)
                error("execution.degree", $"nodes.{id}", "The input entry requires exactly one execution output connection.");
            if (node.Type == NodeTypes.Output && executionOutgoing.GetValueOrDefault(id)?.Count > 0)
                error("execution.degree", $"nodes.{id}", "The output node cannot have execution outputs.");
            if (node.Type is NodeTypes.StringConstant or NodeTypes.TextConcat && executionOutgoing.GetValueOrDefault(id)?.Count > 0)
                error("execution.pure", $"nodes.{id}", "Pure data nodes do not expose execution pins.");
        }

        if (definition.ExecutionConnections.Values.Count(edge => edge?.Source is { NodeId: var sourceId, PortId: "then" } &&
                edge.Target is { NodeId: var targetId, PortId: "execute" } &&
                sourceId == inputs.FirstOrDefault() && targetId == outputs.FirstOrDefault()) != 1 || definition.ExecutionConnections.Count != 1)
            error("execution.path", "executionConnections", "The initial explicit execution subset is one input activation connected directly to the output node.");

        var dataOrder = WorkflowGraph.TopologicalOrder(definition);
        if (dataOrder.Length != definition.Nodes.Count)
            error("data.cycle", "connections", "Pure data dependencies must be acyclic.");
        if (outputs.Length == 1)
        {
            var visited = new HashSet<string>(StringComparer.Ordinal);
            var pending = new Stack<string>();
            pending.Push(outputs[0]);
            while (pending.TryPop(out var current))
            {
                if (!visited.Add(current)) continue;
                foreach (var dependency in dataOutgoing.GetValueOrDefault(current) ?? []) pending.Push(dependency);
            }
            foreach (var (id, node) in definition.Nodes)
                if (node?.Type is NodeTypes.StringConstant or NodeTypes.TextConcat && !visited.Contains(id))
                    error("data.unreachable", $"nodes.{id}", "Every pure data node must be reachable from an output input.");
        }
    }

    private static void ValidateExplicitBranchGraph(WorkflowDefinition definition, Action<string, string, string> error,
        bool allowBooleanTopology = false, bool allowGraphFunctions = false, bool allowAgentNodes = false,
        bool allowConfigurableParallel = false, bool allowCollaborativeParallel = false)
    {
        if (definition.ExecutionConnections is null)
        {
            error("execution.missing", "executionConnections", "Schema 1.24 requires an explicit execution-connection collection.");
            return;
        }
        if (definition.Nodes.Count is < 2 or > 256 || definition.Connections.Count + definition.ExecutionConnections.Count > 510)
            error("graph.limit", "nodes", "An explicit graph requires 2–256 nodes and at most 510 total data and execution connections.");

        var supportedTypes = new HashSet<string>(StringComparer.Ordinal)
        {
            NodeTypes.Input, NodeTypes.Output, NodeTypes.StringConstant, NodeTypes.TextConcat,
            NodeTypes.StringEquals, NodeTypes.BooleanBranch, NodeTypes.Terminate
        };
        if (definition.SchemaVersion is WorkflowSchemaVersions.TypedBooleanGraph or WorkflowSchemaVersions.BooleanTopologyGraph or WorkflowSchemaVersions.PureGraphFunctions or WorkflowSchemaVersions.AgentGraphFunctionTools or WorkflowSchemaVersions.AgentParameterBindings or WorkflowSchemaVersions.AgentExecutionRoutes or WorkflowSchemaVersions.IntegerGraph or WorkflowSchemaVersions.ConfigurableParallelGraph or WorkflowSchemaVersions.CollaborativeParallelGraph or WorkflowSchemaVersions.BranchLocalParallelGraph or WorkflowSchemaVersions.PhasedCollaborativeParallelGraph)
        {
            supportedTypes.Add(NodeTypes.BooleanConstant);
            supportedTypes.Add(NodeTypes.BooleanNot);
        }
        if (allowGraphFunctions) supportedTypes.Add(NodeTypes.FunctionCall);
        if (allowAgentNodes) supportedTypes.Add(NodeTypes.Agent);
        if (allowConfigurableParallel)
        {
            supportedTypes.Add(NodeTypes.ParallelFork);
            supportedTypes.Add(NodeTypes.ParallelBranch);
            supportedTypes.Add(NodeTypes.ParallelJoin);
        }
        if (allowCollaborativeParallel) supportedTypes.Add(NodeTypes.ParallelRendezvous);
        if (definition.SchemaVersion is WorkflowSchemaVersions.IntegerGraph or WorkflowSchemaVersions.ConfigurableParallelGraph or WorkflowSchemaVersions.CollaborativeParallelGraph or WorkflowSchemaVersions.BranchLocalParallelGraph or WorkflowSchemaVersions.PhasedCollaborativeParallelGraph)
        {
            supportedTypes.Add(NodeTypes.WorkflowValue);
            supportedTypes.Add(NodeTypes.IntegerConstant);
            supportedTypes.Add(NodeTypes.IntegerAdd);
            supportedTypes.Add(NodeTypes.IntegerEquals);
        }
        foreach (var (id, node) in definition.Nodes)
            if (node is null || !supportedTypes.Contains(node.Type))
                error("execution.node.unsupported", $"nodes.{id}", allowAgentNodes
                    ? "Schema 1.28 supports bounded Boolean branch graphs with Agent execution steps and pure graph functions."
                    : definition.SchemaVersion is WorkflowSchemaVersions.TypedBooleanGraph or WorkflowSchemaVersions.BooleanTopologyGraph or WorkflowSchemaVersions.PureGraphFunctions or WorkflowSchemaVersions.AgentGraphFunctionTools
                    ? "This schema supports the schema 1.24 branch nodes plus Boolean constants and Boolean NOT."
                    : "Schema 1.24 supports input, output, string constants, text concatenation, string equality, Boolean Branch, and termination nodes.");

        var inputs = definition.Nodes.Where(pair => pair.Value?.Type == NodeTypes.Input).Select(pair => pair.Key).ToArray();
        var outputs = definition.Nodes.Where(pair => pair.Value?.Type == NodeTypes.Output).Select(pair => pair.Key).ToArray();
        var branches = definition.Nodes.Where(pair => pair.Value?.Type == NodeTypes.BooleanBranch).Select(pair => pair.Key).ToArray();
        var terminators = definition.Nodes.Where(pair => pair.Value?.Type == NodeTypes.Terminate).Select(pair => pair.Key).ToArray();
        if (inputs.Length != 1 || outputs.Length != 1 || inputs.FirstOrDefault() != definition.EntryNodeId)
            error("workflow.terminals", "nodes", "An explicit graph requires one configured input entry and one output node.");
        if (allowBooleanTopology)
        {
            if ((!allowAgentNodes && branches.Length < 1) || branches.Length > 8)
                error("graph.limit", "nodes", "Schema 1.26 requires 1–8 Boolean Branch nodes.");
            if (terminators.Length > 16)
                error("graph.limit", "nodes", "Schema 1.26 supports up to 16 termination nodes.");
        }
        else if (branches.Length != 1 || terminators.Length != 1)
            error("graph.degree", "nodes", "The schema 1.24 proof graph requires one Boolean Branch and one termination node.");

        var seen = new HashSet<(string Kind, string Source, string SourcePort, string Target, string TargetPort)>();
        var dataIncoming = new Dictionary<(string Node, string Port), int>();
        var dataDependencies = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var (id, edge) in definition.Connections)
        {
            var path = $"connections.{id}";
            if (string.IsNullOrWhiteSpace(id) || edge?.Source?.NodeId is not { } source || edge.Target?.NodeId is not { } target ||
                !definition.Nodes.TryGetValue(source, out var sourceNode) || !definition.Nodes.TryGetValue(target, out var targetNode) ||
                sourceNode is null || targetNode is null)
            {
                error("connection.reference", path, "Both data-edge endpoints must reference existing nodes and ports.");
                continue;
            }
            var sourcePort = WorkflowPorts.ExplicitDataOutputs(sourceNode).FirstOrDefault(port => port.Id == edge.Source.PortId);
            var targetPort = WorkflowPorts.ExplicitDataInputs(targetNode).FirstOrDefault(port => port.Id == edge.Target.PortId);
            if (edge.Kind != "data" || sourcePort is null || targetPort is null || !WorkflowDataTypeCatalog.AreCompatible(sourcePort.DataType, targetPort.DataType))
                error("connection.port", path, "Data edges must connect compatible typed data ports; they never schedule execution.");
            if (!seen.Add(("data", source, edge.Source.PortId, target, edge.Target.PortId)))
                error("connection.duplicate", path, "Duplicate data edge.");
            dataIncoming[(target, edge.Target.PortId)] = dataIncoming.GetValueOrDefault((target, edge.Target.PortId)) + 1;
            if (!dataDependencies.TryGetValue(target, out var upstream)) dataDependencies[target] = upstream = [];
            upstream.Add(source);
        }

        var executionIncoming = new Dictionary<(string Node, string Port), int>();
        var executionOutgoing = new Dictionary<(string Node, string Port), int>();
        var executionAdjacency = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var (id, edge) in definition.ExecutionConnections)
        {
            var path = $"executionConnections.{id}";
            if (string.IsNullOrWhiteSpace(id) || edge?.Source?.NodeId is not { } source || edge.Target?.NodeId is not { } target ||
                !definition.Nodes.TryGetValue(source, out var sourceNode) || !definition.Nodes.TryGetValue(target, out var targetNode) ||
                sourceNode is null || targetNode is null)
            {
                error("execution.reference", path, "Both execution-edge endpoints must reference existing nodes and ports.");
                continue;
            }
            var sourcePorts = WorkflowPorts.ExplicitExecutionOutputs(sourceNode);
            var targetPorts = WorkflowPorts.ExplicitExecutionInputs(targetNode);
            if (edge.Kind != "execution" || !sourcePorts.Contains(edge.Source.PortId) || !targetPorts.Contains(edge.Target.PortId))
                error("execution.port", path, "Execution edges must connect declared execution ports and cannot carry data.");
            if (definition.SchemaVersion == WorkflowSchemaVersions.BranchLocalParallelGraph && allowConfigurableParallel &&
                    !IsAllowedBranchLocalParallelEdge(sourceNode, edge.Source.PortId, targetNode, edge.Target.PortId, allowCollaborativeParallel) ||
                definition.SchemaVersion != WorkflowSchemaVersions.BranchLocalParallelGraph && allowCollaborativeParallel &&
                    !IsAllowedCollaborativeParallelEdge(sourceNode, edge.Source.PortId, targetNode, edge.Target.PortId) ||
                definition.SchemaVersion != WorkflowSchemaVersions.BranchLocalParallelGraph && allowConfigurableParallel && !allowCollaborativeParallel &&
                    !IsAllowedConfigurableParallelEdge(sourceNode, edge.Source.PortId, targetNode, edge.Target.PortId) ||
                allowBooleanTopology && !allowConfigurableParallel && (sourceNode.Type == NodeTypes.Input && targetNode.Type is not (NodeTypes.BooleanBranch or NodeTypes.Agent) ||
                sourceNode.Type == NodeTypes.BooleanBranch && targetNode.Type is not (NodeTypes.BooleanBranch or NodeTypes.Output or NodeTypes.Terminate or NodeTypes.Agent) ||
                sourceNode.Type == NodeTypes.Agent && targetNode.Type is not (NodeTypes.BooleanBranch or NodeTypes.Output or NodeTypes.Terminate)))
                error("execution.target", path, allowAgentNodes
                    ? "Schema 1.28 routes Input to an Agent or Branch, an Agent to a Branch/Output/Terminate, and Branches to a Branch/Agent/Output/Terminate."
                    : "Schema 1.26 routes Input to a Boolean Branch and each Branch arm to a Branch, Output, or Terminate node.");
            if (!seen.Add(("execution", source, edge.Source.PortId, target, edge.Target.PortId)))
                error("execution.duplicate", path, "Duplicate execution edge.");
            executionIncoming[(target, edge.Target.PortId)] = executionIncoming.GetValueOrDefault((target, edge.Target.PortId)) + 1;
            executionOutgoing[(source, edge.Source.PortId)] = executionOutgoing.GetValueOrDefault((source, edge.Source.PortId)) + 1;
            if (!executionAdjacency.TryGetValue(source, out var downstream)) executionAdjacency[source] = downstream = [];
            downstream.Add(target);
        }

        foreach (var (id, node) in definition.Nodes)
        {
            if (node is null) continue;
            foreach (var port in WorkflowPorts.Explicit(node).Where(port => port.Direction == WorkflowPortDirection.Input && port.Kind == WorkflowPortKind.Data))
                if (port.Required && port.Cardinality == WorkflowPortCardinality.ExactlyOne && dataIncoming.GetValueOrDefault((id, port.Id)) != 1)
                    error("data.degree", $"nodes.{id}.inputs.{port.Id}", "Every required data input must have exactly one compatible data edge.");
            var actualExecutionInput = WorkflowPorts.Explicit(node)
                .Where(port => port.Direction == WorkflowPortDirection.Input && port.Kind == WorkflowPortKind.Execution)
                .Sum(port => executionIncoming.GetValueOrDefault((id, port.Id)));
            if (allowBooleanTopology)
            {
                if (node.Type == NodeTypes.Input && actualExecutionInput != 0 ||
                    node.Type is NodeTypes.Agent or NodeTypes.Output or NodeTypes.BooleanBranch or NodeTypes.Terminate or NodeTypes.ParallelRendezvous && actualExecutionInput is < 1 or > 16 ||
                    allowConfigurableParallel && node.Type is NodeTypes.ParallelFork or NodeTypes.ParallelBranch && actualExecutionInput != 1 ||
                    allowConfigurableParallel && node.Type == NodeTypes.ParallelJoin && actualExecutionInput != node.ParallelJoin?.BranchIds.Length ||
                    allowCollaborativeParallel && node.Type == NodeTypes.ParallelRendezvous && actualExecutionInput != node.ParallelRendezvous?.BranchIds.Length)
                    error("graph.degree", $"nodes.{id}", "Input has no execution input; Agent, Output, Branch, Rendezvous, and Terminate require 1–16 alternative activations.");
            }
            else
            {
                var expectedExecutionInput = node.Type == NodeTypes.Input ? 0 : node.Type is NodeTypes.Output or NodeTypes.BooleanBranch or NodeTypes.Terminate ? 1 : 0;
                if (actualExecutionInput != expectedExecutionInput)
                    error("graph.degree", $"nodes.{id}", "Input has no execution input; output, Branch, and termination require one activation.");
            }
            foreach (var port in WorkflowPorts.Explicit(node).Where(port => port.Direction == WorkflowPortDirection.Output && port.Kind == WorkflowPortKind.Execution))
                if (port.Required && port.Cardinality == WorkflowPortCardinality.ExactlyOne && executionOutgoing.GetValueOrDefault((id, port.Id)) != 1)
                    error("graph.degree", $"nodes.{id}.outputs.{port.Id}", "Every execution output pin must connect exactly once.");
            if (node.Type is NodeTypes.StringConstant or NodeTypes.TextConcat or NodeTypes.StringEquals or NodeTypes.BooleanConstant or NodeTypes.BooleanNot or NodeTypes.IntegerConstant or NodeTypes.IntegerAdd or NodeTypes.IntegerEquals or NodeTypes.WorkflowValue or NodeTypes.FunctionCall &&
                WorkflowPorts.ExplicitDataOutputs(node).Length > 0 && !definition.Connections.Values.Any(edge => edge.Source.NodeId == id))
                error("data.unused", $"nodes.{id}", "Every pure data node must contribute to a Branch condition or Output value.");
            if (node.Type == NodeTypes.Terminate && node.Termination is { Outcome: not (WorkflowTerminationOutcomes.Success or WorkflowTerminationOutcomes.Failure) })
                error("termination.outcome", $"nodes.{id}.termination.outcome", "Termination outcome must be success or failure.");
        }

        if (!allowBooleanTopology && branches.Length == 1 && inputs.Length == 1 && outputs.Length == 1 && terminators.Length == 1)
        {
            var inputId = inputs[0];
            var outputId = outputs[0];
            var branchId = branches[0];
            var terminateId = terminators[0];
            var expectedRoutes = new[]
            {
                (inputId, "then", branchId, "execute"),
                (branchId, "true", outputId, "execute"),
                (branchId, "false", terminateId, "execute")
            };
            if (definition.ExecutionConnections.Count != expectedRoutes.Length || expectedRoutes.Any(route =>
                !definition.ExecutionConnections.Values.Any(edge => edge.Source.NodeId == route.Item1 && edge.Source.PortId == route.Item2 &&
                    edge.Target.NodeId == route.Item3 && edge.Target.PortId == route.Item4)))
                error("graph.degree", "executionConnections", "Wire Input to Branch, Branch true to Output, and Branch false to Terminate.");
            if (definition.Nodes.TryGetValue(terminateId, out var terminator) && terminator.Termination?.Outcome != WorkflowTerminationOutcomes.Failure)
                error("graph.degree", $"nodes.{terminateId}.termination", "The Branch false route must end at a failure termination.");
        }

        var pendingExecution = new Stack<string>();
        if (inputs.FirstOrDefault() is { } entryId) pendingExecution.Push(entryId);
        var reachedExecution = new HashSet<string>(StringComparer.Ordinal);
        while (pendingExecution.TryPop(out var current))
        {
            if (!reachedExecution.Add(current)) continue;
            foreach (var next in executionAdjacency.GetValueOrDefault(current) ?? []) pendingExecution.Push(next);
        }
        var executableIds = definition.Nodes.Where(pair => WorkflowPorts.ExplicitExecutionInputs(pair.Value).Length > 0 ||
            WorkflowPorts.ExplicitExecutionOutputs(pair.Value).Length > 0).Select(pair => pair.Key);
        if (executableIds.Any(id => !reachedExecution.Contains(id)))
            error("graph.unreachable", "executionConnections", "Every executable node must be reachable from the input entry.");
        if (HasCycle(executionAdjacency, executableIds))
            error("graph.cycle", "executionConnections", "Explicit execution routes must be acyclic.");

        var dataOrder = WorkflowGraph.TopologicalOrder(definition);
        if (dataOrder.Length != definition.Nodes.Count)
            error("graph.cycle", "connections", "Pure data dependencies must be acyclic.");
        var reachedData = new HashSet<string>(StringComparer.Ordinal);
        var pendingData = new Stack<string>(outputs.Concat(branches));
        while (pendingData.TryPop(out var current))
        {
            if (!reachedData.Add(current)) continue;
            foreach (var dependency in dataDependencies.GetValueOrDefault(current) ?? []) pendingData.Push(dependency);
        }
        foreach (var (id, node) in definition.Nodes)
            if (node?.Type is NodeTypes.StringConstant or NodeTypes.TextConcat or NodeTypes.StringEquals or NodeTypes.BooleanConstant or NodeTypes.BooleanNot or NodeTypes.IntegerConstant or NodeTypes.IntegerAdd or NodeTypes.IntegerEquals or NodeTypes.WorkflowValue or NodeTypes.FunctionCall && !reachedData.Contains(id))
                error("graph.unreachable", $"nodes.{id}", "Every pure data node must contribute to a Branch condition or Output value.");
    }

    private static bool IsAllowedConfigurableParallelEdge(NodeDefinition source, string sourcePort,
        NodeDefinition target, string targetPort)
    {
        var allowedTarget = source.Type switch
        {
            NodeTypes.Input => target.Type is NodeTypes.Agent or NodeTypes.BooleanBranch or NodeTypes.ParallelFork,
            NodeTypes.BooleanBranch => target.Type is NodeTypes.BooleanBranch or NodeTypes.Agent or NodeTypes.Output or NodeTypes.Terminate or NodeTypes.ParallelFork,
            NodeTypes.Agent => target.Type is NodeTypes.Agent or NodeTypes.BooleanBranch or NodeTypes.Output or NodeTypes.Terminate or NodeTypes.ParallelFork or NodeTypes.ParallelJoin,
            NodeTypes.ParallelFork => target.Type == NodeTypes.ParallelBranch,
            NodeTypes.ParallelBranch => target.Type is NodeTypes.Agent or NodeTypes.ParallelJoin,
            NodeTypes.ParallelJoin => target.Type is NodeTypes.Agent or NodeTypes.BooleanBranch or NodeTypes.Output or NodeTypes.Terminate,
            _ => false
        };
        var validTargetPort = target.Type == NodeTypes.ParallelJoin
            ? targetPort.StartsWith("branch.", StringComparison.Ordinal)
            : targetPort == "execute";
        return allowedTarget && validTargetPort && sourcePort.Length > 0;
    }

    private static bool IsAllowedCollaborativeParallelEdge(NodeDefinition source, string sourcePort,
        NodeDefinition target, string targetPort)
    {
        var allowed = source.Type switch
        {
            NodeTypes.Input => target.Type is NodeTypes.Agent or NodeTypes.BooleanBranch or NodeTypes.ParallelFork,
            NodeTypes.BooleanBranch => target.Type is NodeTypes.BooleanBranch or NodeTypes.Agent or NodeTypes.Output or NodeTypes.Terminate or NodeTypes.ParallelFork,
            NodeTypes.Agent => target.Type is NodeTypes.Agent or NodeTypes.BooleanBranch or NodeTypes.Output or NodeTypes.Terminate or NodeTypes.ParallelFork or NodeTypes.ParallelJoin or NodeTypes.ParallelRendezvous,
            NodeTypes.ParallelFork => target.Type == NodeTypes.ParallelBranch,
            NodeTypes.ParallelBranch => target.Type is NodeTypes.Agent or NodeTypes.ParallelRendezvous,
            NodeTypes.ParallelRendezvous => target.Type is NodeTypes.Agent or NodeTypes.ParallelJoin,
            NodeTypes.ParallelJoin => target.Type is NodeTypes.Agent or NodeTypes.BooleanBranch or NodeTypes.Output or NodeTypes.Terminate,
            _ => false
        };
        if (!allowed || string.IsNullOrEmpty(sourcePort)) return false;
        return target.Type switch
        {
            NodeTypes.ParallelJoin => targetPort.StartsWith("branch.", StringComparison.Ordinal),
            NodeTypes.ParallelRendezvous => targetPort.StartsWith("branch.", StringComparison.Ordinal),
            _ => targetPort == "execute"
        };
    }

    private static bool IsAllowedBranchLocalParallelEdge(NodeDefinition source, string sourcePort,
        NodeDefinition target, string targetPort, bool collaborationEnabled)
    {
        if (IsAllowedCollaborativeParallelEdge(source, sourcePort, target, targetPort) ||
            IsAllowedConfigurableParallelEdge(source, sourcePort, target, targetPort)) return true;

        var extraTarget = source.Type switch
        {
            NodeTypes.ParallelBranch => target.Type == NodeTypes.BooleanBranch,
            NodeTypes.BooleanBranch => target.Type is NodeTypes.ParallelJoin or NodeTypes.ParallelRendezvous,
            _ => false
        };
        var targetPortIsValid = target.Type switch
        {
            NodeTypes.ParallelJoin => targetPort.StartsWith("branch.", StringComparison.Ordinal),
            NodeTypes.ParallelRendezvous => collaborationEnabled && targetPort.StartsWith("branch.", StringComparison.Ordinal),
            _ => targetPort == "execute"
        };
        return extraTarget && sourcePort is "then" or "true" or "false" && targetPortIsValid;
    }

    private static void ValidatePhasedCollaborativeParallelGraph(WorkflowDefinition definition,
        Action<string, string, string> error)
    {
        var forks = definition.Nodes.Where(entry => entry.Value?.Type == NodeTypes.ParallelFork).ToArray();
        var joins = definition.Nodes.Where(entry => entry.Value?.Type == NodeTypes.ParallelJoin).ToArray();
        var rendezvousNodes = definition.Nodes.Where(entry => entry.Value?.Type == NodeTypes.ParallelRendezvous).ToArray();
        var regions = definition.ParallelCollaborationRegions;
        if (forks.Length != 1 || joins.Length != 1 || rendezvousNodes.Length != 2 || regions is not { Count: 1 })
        {
            error("parallel.collaboration.topology.count", "nodes", "Schema 1.35 requires one matched fork, two ordered rendezvous nodes, one join, and one collaboration region.");
            return;
        }

        var (regionKey, region) = regions.Single();
        if (region is null || region.SecondRendezvousNodeId is not { Length: > 0 } secondRendezvousId ||
            !definition.Nodes.TryGetValue(region.ForkNodeId, out var fork) || fork?.Type != NodeTypes.ParallelFork ||
            !definition.Nodes.TryGetValue(region.RendezvousNodeId, out var firstRendezvous) || firstRendezvous?.Type != NodeTypes.ParallelRendezvous ||
            !definition.Nodes.TryGetValue(secondRendezvousId, out var secondRendezvous) || secondRendezvous?.Type != NodeTypes.ParallelRendezvous ||
            !definition.Nodes.TryGetValue(region.JoinNodeId, out var join) || join?.Type != NodeTypes.ParallelJoin ||
            forks[0].Key != region.ForkNodeId || joins[0].Key != region.JoinNodeId ||
            rendezvousNodes.Select(node => node.Key).ToHashSet(StringComparer.Ordinal).SetEquals([region.RendezvousNodeId, secondRendezvousId]) is false ||
            region.RendezvousNodeId == secondRendezvousId || regionKey != region.RendezvousNodeId)
        {
            error("parallel.collaboration.topology.reference", $"parallelCollaborationRegions.{regionKey}", "The region must identify distinct first and second rendezvous nodes between its exact fork and join.");
            return;
        }

        var descriptors = fork.ParallelFork?.Branches ?? [];
        var branchIds = descriptors.IsDefault ? [] : descriptors.Select(branch => branch?.Id ?? "").ToArray();
        if (descriptors.IsDefault || branchIds.Length is < ParallelBranchLimits.MinimumBranches or > ParallelGraphLimits.MaximumExplicitBranches ||
            branchIds.Distinct(StringComparer.Ordinal).Count() != branchIds.Length ||
            !string.Equals(join.ParallelJoin?.ForkNodeId, region.ForkNodeId, StringComparison.Ordinal) ||
            join.ParallelJoin?.BranchIds.IsDefault != false ||
            !branchIds.Order(StringComparer.Ordinal).SequenceEqual(join.ParallelJoin!.BranchIds.Order(StringComparer.Ordinal)) ||
            firstRendezvous.ParallelRendezvous?.BranchIds.IsDefault != false ||
            !branchIds.Order(StringComparer.Ordinal).SequenceEqual(firstRendezvous.ParallelRendezvous!.BranchIds.Order(StringComparer.Ordinal)) ||
            secondRendezvous.ParallelRendezvous?.BranchIds.IsDefault != false ||
            !branchIds.Order(StringComparer.Ordinal).SequenceEqual(secondRendezvous.ParallelRendezvous!.BranchIds.Order(StringComparer.Ordinal)) ||
            !branchIds.Order(StringComparer.Ordinal).SequenceEqual(region.Branches.IsDefault ? [] : region.Branches.Select(branch => branch?.BranchId ?? "").Order(StringComparer.Ordinal)))
        {
            error("parallel.collaboration.topology.branches", $"parallelCollaborationRegions.{regionKey}.branches", "Fork, both rendezvous nodes, join, and region must declare the same stable branch IDs.");
            return;
        }

        var edges = definition.ExecutionConnections?.Values.Where(edge => edge is not null).ToArray() ?? [];
        if (edges.Count(edge => edge.Target.NodeId == region.ForkNodeId) != 1)
            error("parallel.collaboration.topology.entry", $"nodes.{region.ForkNodeId}", "The fork must have one activation input.");
        var occupied = new HashSet<string>(StringComparer.Ordinal)
        {
            region.ForkNodeId, region.RendezvousNodeId, secondRendezvousId, region.JoinNodeId
        };
        var anchors = definition.Nodes.Where(entry => entry.Value?.Type == NodeTypes.ParallelBranch).ToArray();
        if (anchors.Length != descriptors.Length)
            error("parallel.collaboration.topology.anchors", "nodes", "Every declared fork branch requires exactly one matching anchor.");

        foreach (var descriptor in descriptors)
        {
            if (descriptor is null || !definition.Nodes.TryGetValue(descriptor.AnchorNodeId, out var anchor) ||
                anchor?.Type != NodeTypes.ParallelBranch || anchor.ParallelBranch is not { } identity ||
                identity.ForkNodeId != region.ForkNodeId || identity.BranchId != descriptor.Id)
            {
                error("parallel.collaboration.topology.anchor", $"nodes.{region.ForkNodeId}", "Each fork descriptor must point to an anchor with matching fork and branch IDs.");
                continue;
            }
            var branchPin = ParallelBranchLimits.PinId(descriptor.Id);
            var forkEdges = edges.Where(edge => edge.Source.NodeId == region.ForkNodeId && edge.Source.PortId == branchPin).ToArray();
            if (forkEdges.Length != 1 || forkEdges[0].Target.NodeId != descriptor.AnchorNodeId || forkEdges[0].Target.PortId != "execute")
                error("parallel.collaboration.topology.fork-route", $"nodes.{region.ForkNodeId}.outputs.{descriptor.Id}", "Each fork output must activate its matching branch anchor.");

            var grantBranch = region.Branches.FirstOrDefault(item => item?.BranchId == descriptor.Id);
            var beforeFirst = WalkSegment(descriptor.AnchorNodeId, region.RendezvousNodeId, descriptor.Id, "anchor", postRendezvous: false);
            var between = WalkSegment(region.RendezvousNodeId, secondRendezvousId, descriptor.Id, "rendezvous", postRendezvous: true);
            var afterSecond = WalkSegment(secondRendezvousId, region.JoinNodeId, descriptor.Id, "rendezvous", postRendezvous: true);
            ValidatePhaseGrants(grantBranch, descriptor.Id, beforeFirst, between, afterSecond);

            List<string> WalkSegment(string startNodeId, string targetNodeId, string branchId, string startKind, bool postRendezvous)
            {
                var agents = new List<string>();
                var current = startNodeId;
                var seen = new HashSet<string>(StringComparer.Ordinal);
                var arrived = false;
                for (var steps = 0; steps <= definition.Nodes.Count; steps++)
                {
                    if (!seen.Add(current)) break;
                    var outgoing = edges.Where(edge => edge.Source.NodeId == current).ToArray();
                    if (current == startNodeId && startKind == "rendezvous")
                    {
                        var route = outgoing.SingleOrDefault(edge => edge.Source.PortId == branchPin);
                        if (route is null || outgoing.Count(edge => edge.Source.PortId == branchPin) != 1)
                            error("parallel.collaboration.topology.rendezvous-route", $"nodes.{current}.outputs.{branchId}", "Each rendezvous output must continue only on its matching branch pin.");
                        else if (route.Target.NodeId == targetNodeId && route.Target.PortId == branchPin)
                            arrived = true;
                        else if (definition.Nodes.TryGetValue(route.Target.NodeId, out var next) && next?.Type == NodeTypes.Agent && route.Target.PortId == "execute")
                            current = route.Target.NodeId;
                        else
                            error("parallel.collaboration.topology.segment-route", "executionConnections", "Each rendezvous output may continue only through Agent nodes to the next ordered barrier or join.");
                    }
                    else
                    {
                        var node = definition.Nodes.GetValueOrDefault(current);
                        if (node?.Type == NodeTypes.Agent)
                        {
                            if (node.Agent?.Routes is { IsDefaultOrEmpty: false })
                                error("parallel.collaboration.topology.agent-route", $"nodes.{current}", "Agents in a collaboration branch cannot have conditional execution routes.");
                            agents.Add(current);
                        }
                        else if (!(current == startNodeId && startKind == "anchor" && node?.Type == NodeTypes.ParallelBranch))
                        {
                            error("parallel.collaboration.topology.segment-node", $"nodes.{current}", "A collaboration segment may contain only its branch anchor and Agent nodes.");
                            break;
                        }
                        if (outgoing.Length != 1) break;
                        var nextEdge = outgoing[0];
                        if (nextEdge.Target.NodeId == targetNodeId)
                        {
                            if (nextEdge.Target.PortId != branchPin)
                                error("parallel.collaboration.topology.branch-pin", "executionConnections", "A branch must enter the next ordered rendezvous or join input bearing its own stable ID.");
                            arrived = true;
                        }
                        else if (definition.Nodes.TryGetValue(nextEdge.Target.NodeId, out var nextNode) && nextNode?.Type == NodeTypes.Agent && nextEdge.Target.PortId == "execute")
                            current = nextEdge.Target.NodeId;
                        else
                        {
                            error("parallel.collaboration.topology.bypass", "executionConnections", "Every branch must pass through both matching rendezvous nodes in order before its matching join input.");
                            break;
                        }
                    }
                    if (arrived) break;
                }
                if (!arrived)
                    error("parallel.collaboration.topology.incomplete", $"nodes.{startNodeId}", "Each branch segment must form a straight-line Agent path to its next ordered barrier or join input.");
                foreach (var nodeId in seen.Where(nodeId => nodeId != region.RendezvousNodeId && nodeId != secondRendezvousId && nodeId != region.JoinNodeId && nodeId != region.ForkNodeId))
                    if (!occupied.Add(nodeId))
                        error("parallel.collaboration.topology.cross-branch", $"nodes.{nodeId}", "A collaboration path cannot share nodes with another branch or segment.");
                return agents;
            }

            void ValidatePhaseGrants(ParallelCollaborationBranch? branch, string branchId,
                List<string> beforeFirst, List<string> between, List<string> afterSecond)
            {
                if (branch is null || branch.Agents.IsDefault) return;
                var locations = beforeFirst.ToDictionary(agent => agent,
                    _ => new[] { (ParallelCollaborationPhases.Phase1, ParallelCollaborationRoles.Publish) }, StringComparer.Ordinal);
                foreach (var agent in between)
                    locations[agent] = [(ParallelCollaborationPhases.Phase1, ParallelCollaborationRoles.Read),
                        (ParallelCollaborationPhases.Phase2, ParallelCollaborationRoles.Publish)];
                foreach (var agent in afterSecond)
                    locations[agent] = [(ParallelCollaborationPhases.Phase2, ParallelCollaborationRoles.Read)];
                foreach (var grant in branch.Agents)
                {
                    if (grant is null || !locations.TryGetValue(grant.AgentNodeId, out var allowed))
                    {
                        error("parallel.collaboration.grant.node", $"parallelCollaborationRegions.{regionKey}.branches.{branchId}", "A grant must reference an Agent node on this branch.");
                        continue;
                    }
                    if (!allowed.Any(assignment => assignment.Item1 == grant.Phase && assignment.Item2 == grant.Role))
                        error("parallel.collaboration.grant.phase", $"parallelCollaborationRegions.{regionKey}.branches.{branchId}.agents.{grant.AgentNodeId}", "A grant must match the Agent's exact phase and role at its position between the ordered barriers.");
                }
            }
        }

        if (anchors.Any(anchor => !occupied.Contains(anchor.Key)))
            error("parallel.collaboration.topology.orphan", "nodes", "All branch anchors must belong to the collaboration region.");
    }

    private static void ValidateCollaborativeParallelGraph(WorkflowDefinition definition,
        Action<string, string, string> error)
    {
        var forks = definition.Nodes.Where(entry => entry.Value?.Type == NodeTypes.ParallelFork).ToArray();
        var joins = definition.Nodes.Where(entry => entry.Value?.Type == NodeTypes.ParallelJoin).ToArray();
        var rendezvousNodes = definition.Nodes.Where(entry => entry.Value?.Type == NodeTypes.ParallelRendezvous).ToArray();
        var regions = definition.ParallelCollaborationRegions;
        if (forks.Length != 1 || joins.Length != 1 || rendezvousNodes.Length != 1 || regions is not { Count: 1 })
        {
            error("parallel.collaboration.topology.count", "nodes", "Schema 1.33 requires exactly one matched fork, rendezvous, join, and collaboration region.");
            return;
        }

        var regionEntry = regions.Single();
        var region = regionEntry.Value;
        if (region is null || string.IsNullOrWhiteSpace(region.ForkNodeId) || string.IsNullOrWhiteSpace(region.RendezvousNodeId) || string.IsNullOrWhiteSpace(region.JoinNodeId) ||
            !definition.Nodes.TryGetValue(region.ForkNodeId, out var fork) || fork?.Type != NodeTypes.ParallelFork ||
            !definition.Nodes.TryGetValue(region.RendezvousNodeId, out var rendezvous) || rendezvous?.Type != NodeTypes.ParallelRendezvous ||
            !definition.Nodes.TryGetValue(region.JoinNodeId, out var join) || join?.Type != NodeTypes.ParallelJoin ||
            forks[0].Key != region.ForkNodeId || rendezvousNodes[0].Key != region.RendezvousNodeId || joins[0].Key != region.JoinNodeId)
        {
            error("parallel.collaboration.topology.reference", $"parallelCollaborationRegions.{regionEntry.Key}", "The region must reference the workflow's exact fork, rendezvous, and join nodes.");
            return;
        }

        var descriptors = fork.ParallelFork?.Branches ?? [];
        var joinConfiguration = join.ParallelJoin;
        var branchIds = descriptors.IsDefault ? [] : descriptors.Select(item => item?.Id ?? "").ToArray();
        if (descriptors.IsDefault || descriptors.Length is < ParallelBranchLimits.MinimumBranches or > ParallelGraphLimits.MaximumExplicitBranches ||
            branchIds.Distinct(StringComparer.Ordinal).Count() != branchIds.Length ||
            !string.Equals(joinConfiguration?.ForkNodeId, region.ForkNodeId, StringComparison.Ordinal) ||
            joinConfiguration?.BranchIds.IsDefault != false ||
            !branchIds.Order(StringComparer.Ordinal).SequenceEqual(joinConfiguration!.BranchIds.Order(StringComparer.Ordinal)) ||
            rendezvous.ParallelRendezvous?.BranchIds.IsDefault != false ||
            !branchIds.Order(StringComparer.Ordinal).SequenceEqual(rendezvous.ParallelRendezvous!.BranchIds.Order(StringComparer.Ordinal)) ||
            !branchIds.Order(StringComparer.Ordinal).SequenceEqual(region.Branches.IsDefault ? [] : region.Branches.Select(item => item?.BranchId ?? "").Order(StringComparer.Ordinal)))
        {
            error("parallel.collaboration.topology.branches", $"parallelCollaborationRegions.{regionEntry.Key}.branches", "Fork, rendezvous, join, and region must declare the same stable branch IDs.");
            return;
        }

        var edges = definition.ExecutionConnections?.Values.Where(edge => edge is not null).ToArray() ?? [];
        if (edges.Count(edge => edge.Target.NodeId == region.ForkNodeId) != 1)
            error("parallel.collaboration.topology.entry", $"nodes.{region.ForkNodeId}", "The fork must have one activation input.");
        var occupied = new HashSet<string>(StringComparer.Ordinal) { region.ForkNodeId, region.RendezvousNodeId, region.JoinNodeId };
        var anchors = definition.Nodes.Where(entry => entry.Value?.Type == NodeTypes.ParallelBranch).ToArray();
        if (anchors.Length != descriptors.Length)
            error("parallel.collaboration.topology.anchors", "nodes", "Every declared fork branch requires exactly one matching anchor.");

        foreach (var descriptor in descriptors)
        {
            if (descriptor is null || !definition.Nodes.TryGetValue(descriptor.AnchorNodeId, out var anchor) ||
                anchor?.Type != NodeTypes.ParallelBranch || anchor.ParallelBranch is not { } identity ||
                identity.ForkNodeId != region.ForkNodeId || identity.BranchId != descriptor.Id)
            {
                error("parallel.collaboration.topology.anchor", $"nodes.{region.ForkNodeId}", "Each fork descriptor must point to an anchor with matching fork and branch IDs.");
                continue;
            }
            var forkEdges = edges.Where(edge => edge.Source.NodeId == region.ForkNodeId && edge.Source.PortId == ParallelBranchLimits.PinId(descriptor.Id)).ToArray();
            if (forkEdges.Length != 1 || forkEdges[0].Target.NodeId != descriptor.AnchorNodeId || forkEdges[0].Target.PortId != "execute")
                error("parallel.collaboration.topology.fork-route", $"nodes.{region.ForkNodeId}.outputs.{descriptor.Id}", "Each fork output must activate its matching branch anchor.");
            var branchConfig = region.Branches.FirstOrDefault(item => item?.BranchId == descriptor.Id);
            var preAgents = WalkSegment(descriptor.AnchorNodeId, descriptor.Id, postRendezvous: false);
            var postAgents = WalkSegment(region.RendezvousNodeId, descriptor.Id, postRendezvous: true);
            ValidateGrants(branchConfig, descriptor.Id, preAgents, postAgents);

            List<string> WalkSegment(string startNodeId, string branchId, bool postRendezvous)
            {
                var agents = new List<string>();
                var current = startNodeId;
                var seen = new HashSet<string>(StringComparer.Ordinal);
                var arrived = false;
                var branchPin = ParallelBranchLimits.PinId(branchId);
                for (var steps = 0; steps <= definition.Nodes.Count; steps++)
                {
                    if (!seen.Add(current)) break;
                    var outgoing = edges.Where(edge => edge.Source.NodeId == current).ToArray();
                    if (postRendezvous && current == region.RendezvousNodeId)
                    {
                        var route = outgoing.SingleOrDefault(edge => edge.Source.PortId == branchPin);
                        if (route is null || outgoing.Count(edge => edge.Source.PortId == branchPin) != 1)
                            error("parallel.collaboration.topology.rendezvous-route", $"nodes.{region.RendezvousNodeId}.outputs.{branchId}", "Each rendezvous output must continue only on its matching branch pin.");
                        else if (route.Target.NodeId == region.JoinNodeId && route.Target.PortId == branchPin)
                            arrived = true;
                        else if (definition.Nodes.TryGetValue(route.Target.NodeId, out var next) && next?.Type == NodeTypes.Agent && route.Target.PortId == "execute")
                            current = route.Target.NodeId;
                        else
                            error("parallel.collaboration.topology.post-node", "executionConnections", "A rendezvous branch may continue only through Agent nodes to its matching join input.");
                    }
                    else
                    {
                        var node = definition.Nodes.GetValueOrDefault(current);
                        if (node?.Type == NodeTypes.Agent)
                        {
                            if (node.Agent?.Routes is { IsDefaultOrEmpty: false })
                                error("parallel.collaboration.topology.agent-route", $"nodes.{current}", "Agents in a collaboration branch cannot have conditional execution routes.");
                            agents.Add(current);
                        }
                        else if (!(current == startNodeId && node?.Type == NodeTypes.ParallelBranch && !postRendezvous))
                        {
                            error("parallel.collaboration.topology.segment-node", $"nodes.{current}", "A collaboration segment may contain only its anchor and Agent nodes.");
                            break;
                        }
                        if (outgoing.Length != 1) break;
                        var nextEdge = outgoing[0];
                        var segmentTargetId = postRendezvous ? region.JoinNodeId : region.RendezvousNodeId;
                        if (nextEdge.Target.NodeId == segmentTargetId)
                        {
                            if (nextEdge.Target.PortId != branchPin)
                                error("parallel.collaboration.topology.branch-pin", "executionConnections", postRendezvous
                                    ? "A branch must enter the matching join input bearing its own stable ID."
                                    : "A branch must arrive at the rendezvous input bearing its own stable ID.");
                            arrived = true;
                        }
                        else if (definition.Nodes.TryGetValue(nextEdge.Target.NodeId, out var nextNode) && nextNode?.Type == NodeTypes.Agent && nextEdge.Target.PortId == "execute")
                            current = nextEdge.Target.NodeId;
                        else
                        {
                            error("parallel.collaboration.topology.bypass", $"executionConnections", "Every fork branch must pass through its matching rendezvous and then reach its matching join input.");
                            break;
                        }
                    }
                    if (arrived) break;
                }
                if (!arrived)
                    error("parallel.collaboration.topology.incomplete", $"nodes.{startNodeId}", "Each branch must form an acyclic Agent path through its matching rendezvous to its matching join input.");
                foreach (var nodeId in seen.Where(nodeId => nodeId != region.RendezvousNodeId && nodeId != region.JoinNodeId && nodeId != region.ForkNodeId))
                    if (!occupied.Add(nodeId))
                        error("parallel.collaboration.topology.cross-branch", $"nodes.{nodeId}", "A collaboration path cannot share nodes with another branch.");
                return agents;
            }

            void ValidateGrants(ParallelCollaborationBranch? branch, string branchId,
                List<string> publishAgents, List<string> readAgents)
            {
                if (branch is null || branch.Agents.IsDefault) return;
                var publish = publishAgents.ToHashSet(StringComparer.Ordinal);
                var read = readAgents.ToHashSet(StringComparer.Ordinal);
                foreach (var grant in branch.Agents)
                {
                    if (grant is null || string.IsNullOrWhiteSpace(grant.AgentNodeId) ||
                        !definition.Nodes.TryGetValue(grant.AgentNodeId, out var grantedNode) || grantedNode?.Type != NodeTypes.Agent)
                    {
                        error("parallel.collaboration.grant.node", $"parallelCollaborationRegions.{regionEntry.Key}.branches.{branchId}", "A grant must reference an existing Agent node on this branch.");
                        continue;
                    }
                    var inPublishSegment = publish.Contains(grant.AgentNodeId);
                    var inReadSegment = read.Contains(grant.AgentNodeId);
                    if (grant.Phase == ParallelCollaborationPhases.Publish && !inPublishSegment ||
                        grant.Phase == ParallelCollaborationPhases.Read && !inReadSegment || !inPublishSegment && !inReadSegment)
                        error("parallel.collaboration.grant.phase", $"parallelCollaborationRegions.{regionEntry.Key}.branches.{branchId}.agents.{grant.AgentNodeId}", "Publish grants must target an Agent before the rendezvous; read grants must target an Agent after it on the same branch.");
                }
            }
        }

        var inRegion = occupied;
        var allAnchors = anchors.Select(entry => entry.Key).ToHashSet(StringComparer.Ordinal);
        if (allAnchors.Any(anchor => !inRegion.Contains(anchor)))
            error("parallel.collaboration.topology.orphan", "nodes", "All branch anchors must belong to the collaboration region.");
    }

    private static void ValidateConfigurableParallelGraph(WorkflowDefinition definition,
        Action<string, string, string> error, bool allowBranchLocalRouting = false)
    {
        var forks = definition.Nodes.Where(node => node.Value?.Type == NodeTypes.ParallelFork).ToArray();
        var joins = definition.Nodes.Where(node => node.Value?.Type == NodeTypes.ParallelJoin).ToArray();
        var anchors = definition.Nodes.Where(node => node.Value?.Type == NodeTypes.ParallelBranch).ToArray();
        if (forks.Length != 1 || joins.Length != 1)
        {
            error("parallel.block.count", "nodes", "Schema 1.32 currently requires one matched parallel fork and join block.");
            return;
        }

        var forkId = forks[0].Key;
        var joinId = joins[0].Key;
        var descriptors = forks[0].Value.ParallelFork?.Branches ?? [];
        var joinConfiguration = joins[0].Value.ParallelJoin;
        if (descriptors.IsDefault || descriptors.Length is < ParallelBranchLimits.MinimumBranches or > ParallelGraphLimits.MaximumExplicitBranches ||
            joinConfiguration is null || !string.Equals(joinConfiguration.ForkNodeId, forkId, StringComparison.Ordinal))
            return;

        if (anchors.Length != descriptors.Length)
            error("parallel.anchor.count", "nodes", "Each fork branch requires exactly one matching branch anchor.");
        var edges = definition.ExecutionConnections?.Values.ToArray() ?? [];
        var occupied = new HashSet<string>(StringComparer.Ordinal);
        if (edges.Count(edge => edge.Target.NodeId == forkId) != 1)
            error("parallel.fork.entry", $"nodes.{forkId}", "A parallel fork must have exactly one activation input.");

        foreach (var descriptor in descriptors)
        {
            if (descriptor is null || !definition.Nodes.TryGetValue(descriptor.AnchorNodeId, out var anchor) ||
                anchor?.Type != NodeTypes.ParallelBranch || anchor.ParallelBranch is not { } identity ||
                identity.ForkNodeId != forkId || identity.BranchId != descriptor.Id)
                continue;

            var forkEdges = edges.Where(edge => edge.Source.NodeId == forkId &&
                edge.Source.PortId == ParallelBranchLimits.PinId(descriptor.Id)).ToArray();
            if (forkEdges.Length != 1 || forkEdges[0].Target.NodeId != descriptor.AnchorNodeId || forkEdges[0].Target.PortId != "execute")
                error("parallel.fork.route", $"nodes.{forkId}.outputs.{descriptor.Id}", "Each named fork output must activate its matching branch anchor.");

            var branchNodes = new HashSet<string>(StringComparer.Ordinal) { descriptor.AnchorNodeId };
            var branchDecisionIds = new HashSet<string>(StringComparer.Ordinal);
            var active = new HashSet<string>(StringComparer.Ordinal);
            bool Walk(string current)
            {
                if (current == joinId) return false;
                if (!active.Add(current))
                {
                    error("parallel.branch.cycle", $"nodes.{current}", "Parallel branch paths cannot contain cycles.");
                    return false;
                }
                branchNodes.Add(current);
                if (!definition.Nodes.TryGetValue(current, out var currentNode) || currentNode is null)
                {
                    active.Remove(current);
                    return false;
                }
                var outgoing = edges.Where(edge => edge.Source.NodeId == current).ToArray();
                if (currentNode.Type == NodeTypes.BooleanBranch)
                    branchDecisionIds.Add(current);
                var expectedPins = currentNode.Type switch
                {
                    NodeTypes.ParallelBranch => new[] { "then" },
                    NodeTypes.BooleanBranch when allowBranchLocalRouting => new[] { "true", "false" },
                    NodeTypes.Agent when currentNode.Agent?.Routes is not { IsDefaultOrEmpty: false } => new[] { "out" },
                    _ => []
                };
                if (currentNode.Type != NodeTypes.ParallelBranch && currentNode.Type != NodeTypes.BooleanBranch &&
                    (currentNode.Type != NodeTypes.Agent || currentNode.Agent?.Routes is { IsDefaultOrEmpty: false }))
                {
                    error("parallel.branch.node", $"nodes.{current}", "A static parallel branch may contain only its anchor, Agent nodes, and the schema 1.34 Boolean Branch.");
                    active.Remove(current);
                    return false;
                }
                if (outgoing.Length != expectedPins.Length || expectedPins.Any(pin => outgoing.Count(edge => edge.Source.PortId == pin) != 1))
                {
                    error("parallel.branch.path", $"nodes.{current}", currentNode.Type == NodeTypes.BooleanBranch
                        ? "Both Boolean Branch outcomes must connect to one in-branch continuation each."
                        : "Every parallel branch step must connect to exactly one following step.");
                    active.Remove(current);
                    return false;
                }
                var allComplete = true;
                foreach (var edge in outgoing)
                {
                    if (edge.Target.NodeId == joinId)
                    {
                        if (edge.Target.PortId != ParallelBranchLimits.PinId(descriptor.Id))
                            error("parallel.join.route", $"executionConnections.{descriptor.Id}", "A branch must connect to the join pin with its own stable branch ID.");
                        continue;
                    }
                    if (!definition.Nodes.TryGetValue(edge.Target.NodeId, out var targetNode) || targetNode is null ||
                        targetNode.Type is not (NodeTypes.Agent or NodeTypes.BooleanBranch) ||
                        targetNode.Type == NodeTypes.BooleanBranch && !allowBranchLocalRouting ||
                        edge.Target.PortId != "execute")
                    {
                        error("parallel.branch.node", $"nodes.{edge.Target.NodeId}", "A branch outcome must stay within its owning branch and reach its matching join.");
                        allComplete = false;
                        continue;
                    }
                    if (!Walk(edge.Target.NodeId)) allComplete = false;
                }
                active.Remove(current);
                return allComplete;
            }
            var completed = Walk(descriptor.AnchorNodeId);
            if (branchDecisionIds.Count > (allowBranchLocalRouting ? 1 : 0))
                error("parallel.branch.decision.count", $"nodes.{descriptor.AnchorNodeId}", "A static parallel branch may contain at most one Boolean Branch node.");
            if (!completed)
                error("parallel.branch.join", $"nodes.{descriptor.AnchorNodeId}", "Every parallel branch must reach the matching join.");
            foreach (var branchNode in branchNodes)
            {
                if (!occupied.Add(branchNode))
                    error("parallel.branch.overlap", $"nodes.{branchNode}", "Parallel branches cannot share Agent, Branch, or anchor nodes.");
                var incoming = edges.Where(edge => edge.Target.NodeId == branchNode).ToArray();
                if (branchNode == descriptor.AnchorNodeId
                    ? incoming.Length != 1 || incoming[0].Source.NodeId != forkId
                    : incoming.Length < 1 || incoming.Any(edge => !branchNodes.Contains(edge.Source.NodeId)))
                    error("parallel.branch.entry", $"nodes.{branchNode}", "Every branch node must be activated only from its owning branch; local reconvergence is exclusive routing.");
            }
        }

        var joinInputs = edges.Where(edge => edge.Target.NodeId == joinId).ToArray();
        if (joinInputs.Length != descriptors.Length || joinInputs.Select(edge => edge.Target.PortId).Distinct(StringComparer.Ordinal).Count() != descriptors.Length)
            error("parallel.join.inputs", $"nodes.{joinId}", "The join requires exactly one completed result from every configured branch.");
        if (edges.Count(edge => edge.Source.NodeId == joinId) != 1)
            error("parallel.join.output", $"nodes.{joinId}", "The join must continue through exactly one execution output.");
    }

    private static bool HasCycle(IReadOnlyDictionary<string, List<string>> adjacency, IEnumerable<string> nodes)
    {
        var state = new Dictionary<string, byte>(StringComparer.Ordinal);
        bool Visit(string id)
        {
            if (state.GetValueOrDefault(id) == 1) return true;
            if (state.GetValueOrDefault(id) == 2) return false;
            state[id] = 1;
            foreach (var next in adjacency.GetValueOrDefault(id) ?? [])
                if (Visit(next)) return true;
            state[id] = 2;
            return false;
        }
        return nodes.Any(Visit);
    }

    public static void ThrowIfInvalid(WorkflowDefinition definition)
    {
        var issues = Validate(definition);
        if (issues.Length > 0)
            throw new ArgumentException(string.Join("; ", issues.Select(i => $"{i.Code} at {i.Path}: {i.Message}")), nameof(definition));
    }

    private static bool ValidBinding(ToolBindingRequest tool) =>
        !string.IsNullOrWhiteSpace(tool.BindingId) && tool.BindingId.Length <= 128 &&
        ValidHash(tool.ContractHash) && (tool.DescriptionHash is null || ValidHash(tool.DescriptionHash));

    private static bool HasSupportedWorkflowCallContract(WorkflowDefinition definition)
    {
        if (definition.Nodes is null ||
            !definition.Nodes.TryGetValue(definition.EntryNodeId, out var input) || input?.Type != NodeTypes.Input ||
            definition.Nodes.Values.Count(node => node?.Type == NodeTypes.Output) != 1)
            return false;

        var output = definition.Nodes.Values.Single(node => node?.Type == NodeTypes.Output)!;
        var inputType = WorkflowPorts.Outputs(input).SingleOrDefault()?.DataType;
        var outputType = WorkflowPorts.Inputs(output).SingleOrDefault()?.DataType;
        return (inputType is "string" or "json") && (outputType is "string" or "json");
    }

    private static bool ValidStringCondition(StringCondition? condition) => condition is not null &&
        (condition.Operator is "equals" or "contains" or "starts-with" or "is-empty") && condition.Operand is not null &&
        condition.Operand.Length <= 1024 && (condition.Operator != "is-empty" || condition.Operand.Length == 0);

    private static string? ValidateLoopRegion(WorkflowDefinition definition, IReadOnlyDictionary<string, int> incoming,
        Action<string, string, string> error)
    {
        var loops = definition.Nodes.Where(entry => entry.Value?.Type == NodeTypes.Loop).Select(entry => entry.Key).ToArray();
        var bodyAnchors = definition.Nodes.Where(entry => entry.Value?.Type == NodeTypes.LoopBody).Select(entry => entry.Key).ToArray();
        if (loops.Length == 0)
        {
            if (bodyAnchors.Length > 0) error("loop.body.unexpected", "nodes", "Loop body anchors require one bounded loop node.");
            return null;
        }

        if (loops.Length != 1 || bodyAnchors.Length != 1)
        {
            error("loop.count", "nodes", "A workflow may contain one bounded loop and its single loop-body anchor.");
            return null;
        }
        if (definition.SchemaVersion is not ("1.17" or "1.19" or "1.22" or "1.21" or "1.20" or "1.18" or WorkflowSchemaVersions.AgentGraphFunctionTools or WorkflowSchemaVersions.AgentParameterBindings or WorkflowSchemaVersions.AgentExecutionRoutes or WorkflowSchemaVersions.IntegerGraph or WorkflowSchemaVersions.ConfigurableParallelGraph or WorkflowSchemaVersions.CollaborativeParallelGraph or WorkflowSchemaVersions.BranchLocalParallelGraph or WorkflowSchemaVersions.PhasedCollaborativeParallelGraph)) error("loop.schema", "nodes", "Bounded loops require schema 1.17 or later.");

        var unsupportedControls = definition.Nodes.Where(entry => entry.Value?.Type is NodeTypes.Condition or NodeTypes.Merge or
            NodeTypes.Approval or NodeTypes.ParallelFork or NodeTypes.ParallelBranch or NodeTypes.ParallelJoin or NodeTypes.Terminate).Select(entry => entry.Key).ToArray();
        if (unsupportedControls.Length > 0)
            error("loop.control.nesting", "nodes", "This bounded-loop version supports one linear body; nested conditions, approvals, merges, and parallel blocks are not supported.");

        var loopId = loops[0];
        var anchorId = bodyAnchors[0];
        var bodyEdges = definition.Connections.Where(entry => entry.Value?.Source?.NodeId == loopId && entry.Value?.Source?.PortId == "body").ToArray();
        if (bodyEdges.Length != 1 || bodyEdges[0].Value?.Target?.NodeId != anchorId)
        {
            error("loop.body.start", $"nodes.{loopId}.body", "Connect the loop body output to its loop-body anchor.");
            return null;
        }

        var allowedBodyTypes = new HashSet<string>(StringComparer.Ordinal)
        {
            NodeTypes.LoopBody, NodeTypes.Uppercase, NodeTypes.Reverse, NodeTypes.TextTemplate,
            NodeTypes.WorkflowValue, NodeTypes.Agent, NodeTypes.McpTool
        };
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var current = anchorId;
        string? backConnection = null;
        while (visited.Add(current) && visited.Count <= definition.Nodes.Count)
        {
            if (!definition.Nodes.TryGetValue(current, out var bodyNode) || bodyNode is null || !allowedBodyTypes.Contains(bodyNode.Type))
            {
                error("loop.body.node", $"nodes.{current}", "A loop body may contain only its anchor, text operations, workflow values, agents, or MCP tools.");
                break;
            }
            if (current != anchorId && bodyNode.Type == NodeTypes.LoopBody)
            {
                error("loop.body.anchor", $"nodes.{current}", "The loop-body anchor can appear only at the beginning of the loop body.");
                break;
            }

            var outgoing = definition.Connections.Where(entry => entry.Value?.Source?.NodeId == current).ToArray();
            if (outgoing.Length != 1)
            {
                error("loop.body.path", $"nodes.{current}", "Each loop-body node must connect to exactly one next node or back to the loop input.");
                break;
            }
            if (outgoing[0].Value?.Target?.NodeId == loopId)
            {
                if (outgoing[0].Value?.Target?.PortId != "in")
                    error("loop.body.return", $"connections.{outgoing[0].Key}", "The loop body must return to the loop input pin.");
                backConnection = outgoing[0].Key;
                break;
            }
            if (outgoing[0].Value?.Target?.NodeId is not { } target) break;
            current = target;
        }
        if (backConnection is null)
            error("loop.body.return", $"nodes.{loopId}", "The loop body must form one linear path that returns to the loop input.");

        var loopInputs = definition.Connections.Where(entry => entry.Value?.Target?.NodeId == loopId && entry.Value?.Target?.PortId == "in").ToArray();
        if (loopInputs.Length != 2 || backConnection is null || !loopInputs.Any(entry => entry.Key == backConnection) ||
            loopInputs.Count(entry => entry.Key != backConnection) != 1 || incoming.GetValueOrDefault(loopId) != 2)
            error("loop.input", $"nodes.{loopId}.in", "The loop input requires exactly one initial value and one return connection from its body.");

        return backConnection;
    }

    private static bool ValidParameterId(string id) => id.Length is > 0 and <= 64 &&
        id.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-');

    private static bool ValidChannelId(string? id) => id is { Length: > 0 and <= 64 } && id[0] is >= 'a' and <= 'z' &&
        id.All(character => character is >= 'a' and <= 'z' or >= '0' and <= '9' or '_' or '-' or '.');

    private static bool ValidJsonPointer(string? pointer)
    {
        try { WorkflowJsonPointer.Parse(pointer!); return true; }
        catch (ArgumentException) { return false; }
    }

    private static bool ValidTextTemplate(PromptTemplate? template, IReadOnlyDictionary<string, WorkflowParameterDefinition> parameters) =>
        template is { Parts.IsDefault: false, Parts.Length: >= 1 and <= 64 } &&
        template.Parts.Sum(part => (long)(part?.Value?.Length ?? 0)) <= 8192 &&
        template.Parts.All(part => part is not null && part.Value is not null && part.Value.Length <= 8192 &&
            (part.Kind is "literal" or "input" or "parameter") && (part.Kind != "parameter" || parameters.ContainsKey(part.Value)));

    private static bool ValidJsonProjection(JsonProjectionConfiguration? projection)
    {
        if (projection is not { Fields.IsDefault: false, Fields.Length: >= 1 and <= JsonProjectionLimits.MaximumFields } ||
            projection.Fields.Select(field => field?.Name).Distinct(StringComparer.Ordinal).Count() != projection.Fields.Length)
            return false;

        var nodeCount = 0;
        long configurationCharacters = 0;
        foreach (var field in projection.Fields)
        {
            if (field is null || string.IsNullOrWhiteSpace(field.Name) || field.Name.Length > JsonProjectionLimits.MaximumNameCharacters ||
                !ValidJsonPointer(field.Pointer) || field.ValueType is not (null or "any" or "string" or "number" or "integer" or "boolean" or "object" or "array" or "null") ||
                field.ValueType is not null && field.ValueSchema is not null)
                return false;

            configurationCharacters += field.Name.Length + field.Pointer.Length;
            if (field.ValueSchema is not null && !ValidJsonValueSchema(field.ValueSchema, 1, ref nodeCount, ref configurationCharacters))
                return false;
        }
        return configurationCharacters <= JsonProjectionLimits.MaximumConfigurationCharacters;
    }

    private static bool ValidJsonValueSchema(JsonValueSchema? schema, int depth, ref int nodeCount, ref long configurationCharacters)
    {
        if (schema is null || depth > JsonProjectionLimits.MaximumSchemaDepth || ++nodeCount > JsonProjectionLimits.MaximumSchemaNodes ||
            schema.Type is not ("any" or "string" or "number" or "integer" or "boolean" or "object" or "array" or "null"))
            return false;
        configurationCharacters += schema.Type.Length;
        if (configurationCharacters > JsonProjectionLimits.MaximumConfigurationCharacters) return false;

        if (schema.Type == "object")
        {
            if (schema.Properties is not { } properties || properties.IsDefault || properties.Length > JsonProjectionLimits.MaximumFields || schema.Items is not null ||
                properties.Select(property => property?.Name).Distinct(StringComparer.Ordinal).Count() != properties.Length)
                return false;
            foreach (var property in properties)
            {
                if (property is null || string.IsNullOrWhiteSpace(property.Name) || property.Name.Length > JsonProjectionLimits.MaximumNameCharacters ||
                    !ValidJsonPointer(property.Pointer)) return false;
                configurationCharacters += property.Name.Length + property.Pointer.Length;
                if (configurationCharacters > JsonProjectionLimits.MaximumConfigurationCharacters ||
                    !ValidJsonValueSchema(property.Schema, depth + 1, ref nodeCount, ref configurationCharacters)) return false;
            }
            return true;
        }

        if (schema.Type == "array")
            return schema.Properties is null && schema.Items is not null && ValidJsonValueSchema(schema.Items, depth + 1, ref nodeCount, ref configurationCharacters);

        return schema.Properties is null && schema.Items is null;
    }

    private static void ValidateParallelRegion(WorkflowDefinition definition, Action<string, string, string> error)
    {
        var forks = definition.Nodes.Where(entry => entry.Value?.Type == NodeTypes.ParallelFork).Select(entry => entry.Key).ToArray();
        var joins = definition.Nodes.Where(entry => entry.Value?.Type == NodeTypes.ParallelJoin).Select(entry => entry.Key).ToArray();
        var branchNodes = definition.Nodes.Where(entry => entry.Value?.Type == NodeTypes.ParallelBranch).Select(entry => entry.Key).ToHashSet(StringComparer.Ordinal);
        if (forks.Length == 0 && joins.Length == 0 && branchNodes.Count == 0) return;
        if (definition.SchemaVersion is not ("1.14" or "1.15" or "1.16" or "1.19" or "1.22" or "1.21" or "1.20" or "1.18" or "1.17" or WorkflowSchemaVersions.AgentGraphFunctionTools or WorkflowSchemaVersions.AgentParameterBindings or WorkflowSchemaVersions.AgentExecutionRoutes or WorkflowSchemaVersions.IntegerGraph or WorkflowSchemaVersions.ConfigurableParallelGraph or WorkflowSchemaVersions.CollaborativeParallelGraph or WorkflowSchemaVersions.BranchLocalParallelGraph or WorkflowSchemaVersions.PhasedCollaborativeParallelGraph)) error("parallel.schema", "nodes", "Parallel blocks require schema 1.14.");
        var multiBlock = definition.SchemaVersion is "1.20" or "1.22" or "1.21";
        if (forks.Length == 0 || forks.Length != joins.Length || joins.Length != branchNodes.Count / 2 ||
            !multiBlock && forks.Length != 1 || forks.Length > ParallelGraphLimits.MaximumBlocks)
        {
            error("parallel.pair", "nodes", multiBlock
                ? $"Schemas 1.20 and 1.21 support up to {ParallelGraphLimits.MaximumBlocks} matched parallel fork and join blocks."
                : "This schema supports one matched parallel fork and join block.");
            return;
        }

        var usedBranches = new HashSet<string>(StringComparer.Ordinal);
        var usedJoins = new HashSet<string>(StringComparer.Ordinal);
        var connections = definition.Connections.Values.ToArray();
        foreach (var forkId in forks)
        {
            var branchJoins = new HashSet<string>(StringComparer.Ordinal);
            var approvalBearingBranches = 0;
            foreach (var branchPort in new[] { "branch_1", "branch_2" })
            {
                var firstEdges = connections.Where(edge => edge.Source.NodeId == forkId && edge.Source.PortId == branchPort).ToArray();
                if (firstEdges.Length != 1 || !branchNodes.Contains(firstEdges[0].Target.NodeId))
                {
                    error("parallel.branch.start", $"nodes.{forkId}.{branchPort}", "Each fork output must begin with its own parallel branch node.");
                    continue;
                }

                var current = firstEdges[0].Target.NodeId;
                var pathNodes = new HashSet<string>(StringComparer.Ordinal);
                var reachedJoin = false;
                var branchContainsNestedApproval = false;
                while (pathNodes.Add(current) && pathNodes.Count <= definition.Nodes.Count)
                {
                    usedBranches.Add(current);
                    if (current != firstEdges[0].Target.NodeId && branchNodes.Contains(current))
                    {
                        error("parallel.branch.nested", $"nodes.{current}", "A parallel branch path cannot contain another branch anchor.");
                        break;
                    }
                    var outgoing = connections.Where(edge => edge.Source.NodeId == current).ToArray();
                    if (outgoing.Length != 1)
                    {
                        error("parallel.branch.path", $"nodes.{current}", "Each parallel branch must form one path to its matching join input.");
                        break;
                    }
                    var edge = outgoing[0];
                    if (joins.Contains(edge.Target.NodeId, StringComparer.Ordinal))
                    {
                        if (edge.Target.PortId != branchPort)
                            error("parallel.branch.target", $"connections.{edge.Target.PortId}", "A branch must connect to the join input with the same branch number.");
                        branchJoins.Add(edge.Target.NodeId);
                        reachedJoin = true;
                        break;
                    }
                    if (!definition.Nodes.TryGetValue(edge.Target.NodeId, out var target) || target is null ||
                        target.Type is NodeTypes.Input or NodeTypes.Output or NodeTypes.Condition or NodeTypes.Merge or NodeTypes.Approval or NodeTypes.ParallelFork or NodeTypes.ParallelJoin ||
                        !definition.Nodes.TryGetValue(current, out var source) || source is null ||
                        WorkflowPorts.Outputs(source).Length != 1 || WorkflowPorts.Outputs(source)[0].DataType != "string")
                    {
                        error("parallel.branch.node", $"nodes.{edge.Target.NodeId}", "Parallel paths may contain only ordinary string-processing nodes.");
                        break;
                    }
                    branchContainsNestedApproval |= target.Type == NodeTypes.WorkflowCall && ContainsNestedApproval(target.WorkflowCall?.Definition, 0);
                    current = edge.Target.NodeId;
                }
                if (branchContainsNestedApproval) approvalBearingBranches++;
                if (!reachedJoin) error("parallel.branch.incomplete", $"nodes.{forkId}.{branchPort}", "Every parallel branch must reach a matching join input.");
            }

            if (branchJoins.Count != 1)
                error("parallel.pair", $"nodes.{forkId}", "Both branches of a fork must reach the same unique join.");
            else if (!usedJoins.Add(branchJoins.Single()))
                error("parallel.pair", $"nodes.{forkId}", "Each parallel join must be paired with exactly one fork.");
            if (approvalBearingBranches > 1)
                error("parallel.approval.concurrent", $"nodes.{forkId}", "Both parallel branches cannot contain nested approvals because this run can suspend at only one approval request at a time.");
        }
        if (branchNodes.Except(usedBranches, StringComparer.Ordinal).Any())
            error("parallel.branch.orphan", "nodes", "Every parallel branch node must belong to a fork-to-join block.");
        if (joins.Except(usedJoins, StringComparer.Ordinal).Any())
            error("parallel.join.orphan", "nodes", "Every parallel join must be paired with the same fork's two branches.");

        if (forks.Length > 1)
        {
            var reachable = forks.ToDictionary(id => id, id => ReachableNodes(id, connections), StringComparer.Ordinal);
            var sequential = forks.SelectMany((left, index) => forks.Skip(index + 1).Select(right => (left, right)))
                .All(pair => reachable[pair.left].Contains(pair.right) || reachable[pair.right].Contains(pair.left));
            if (!sequential)
                error("parallel.blocks.order", "nodes", "Multiple parallel blocks must be sequential; nested or independently concurrent blocks are not supported.");
        }
    }

    private static HashSet<string> ReachableNodes(string start, IReadOnlyList<ConnectionDefinition> connections)
    {
        var outgoing = connections.GroupBy(edge => edge.Source.NodeId)
            .ToDictionary(group => group.Key, group => group.Select(edge => edge.Target.NodeId).ToArray(), StringComparer.Ordinal);
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Stack<string>();
        pending.Push(start);
        while (pending.TryPop(out var current))
        {
            if (!visited.Add(current) || !outgoing.TryGetValue(current, out var next)) continue;
            foreach (var node in next) pending.Push(node);
        }
        return visited;
    }

    private static bool ContainsNestedApproval(WorkflowDefinition? definition, int depth)
    {
        if (definition is null || depth >= WorkflowCallLimits.MaximumDepth) return false;
        return definition.Nodes.Values.Any(node => node?.Type == NodeTypes.Approval ||
            node?.WorkflowCall?.Definition is { } child && ContainsNestedApproval(child, depth + 1));
    }

    private static bool ValidHash(string? hash) => hash is { Length: 64 } && hash.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
}
