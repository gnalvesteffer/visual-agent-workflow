using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;

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

        if (definition.SchemaVersion is not ("1.0" or "1.1" or "1.2" or "1.3" or "1.4" or "1.5" or "1.6" or "1.7" or "1.8" or "1.9" or "1.10" or "1.11" or "1.12" or "1.13" or "1.14" or "1.15" or "1.16" or "1.17" or "1.18" or "1.19"))
            Error("schema.unsupported", "schemaVersion", "Supported schema versions are 1.0 through 1.19.");
        if (string.IsNullOrWhiteSpace(definition.WorkflowId))
            Error("workflow.id", "workflowId", "A workflow ID is required.");
        if (definition.Nodes is null || definition.Connections is null)
        {
            Error("graph.missing", "nodes", "Nodes and connections must be present.");
            return issues.ToImmutable();
        }
        if (definition.Nodes.Count is < 2 or > 256 || definition.Connections.Count > (definition.SchemaVersion is "1.4" or "1.5" or "1.6" or "1.7" or "1.8" or "1.9" or "1.10" or "1.11" or "1.12" or "1.13" or "1.14" or "1.15" or "1.16" or "1.17" or "1.19" or "1.18" ? 510 : 255))
            Error("graph.limit", "nodes", "A graph requires 2–256 nodes and at most 255 connections (510 for schemas 1.4–1.19).");

        var parameters = definition.Parameters ?? ImmutableSortedDictionary<string, WorkflowParameterDefinition>.Empty;
        if (parameters.Count > 64)
            Error("parameters.limit", "parameters", "A workflow can define at most 64 parameters.");
        if (definition.Parameters is { Count: > 0 } && definition.SchemaVersion is not ("1.6" or "1.7" or "1.8" or "1.9" or "1.10" or "1.11" or "1.12" or "1.13" or "1.14" or "1.15" or "1.19" or "1.18" or "1.17" or "1.16"))
            Error("parameters.schema", "parameters", "Workflow parameters require schema 1.6 or later.");
        foreach (var (id, parameter) in parameters)
            if (!ValidParameterId(id) || parameter is null || string.IsNullOrWhiteSpace(parameter.DisplayName) ||
                parameter.DisplayName.Length > 128 || parameter.DefaultValue is null || parameter.DefaultValue.Length > 16_384 ||
                parameter.Description?.Length > 512 ||
                parameter.Type is not (null or "string" or "integer" or "number" or "boolean" or "choice") ||
                parameter.Type is not (null or "string") && definition.SchemaVersion is not ("1.8" or "1.9" or "1.10" or "1.11" or "1.12" or "1.13" or "1.14" or "1.15" or "1.19" or "1.18" or "1.17" or "1.16") ||
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
        if (skills.Count > 0 && definition.SchemaVersion is not ("1.7" or "1.8" or "1.9" or "1.10" or "1.11" or "1.12" or "1.13" or "1.14" or "1.15" or "1.19" or "1.18" or "1.17" or "1.16"))
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
        if (profiles.Count > 0 && definition.SchemaVersion is not ("1.11" or "1.12" or "1.13" or "1.14" or "1.15" or "1.19" or "1.18" or "1.17" or "1.16"))
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
                string.IsNullOrWhiteSpace(profile.ProviderVersion) || profile.ProviderVersion.Length > 64 ||
                string.IsNullOrWhiteSpace(profile.Instructions) || profile.Instructions.Length > 8192 ||
                profile.Tools.IsDefault || profile.Tools.Length > 8 || profile.Tools.Any(tool => tool is null || !ValidBinding(tool)) ||
                profile.Tools.Select(tool => tool.BindingId).Distinct(StringComparer.Ordinal).Count() != profile.Tools.Length ||
                profile.Skills is { } profileSkills && (profileSkills.IsDefault || profileSkills.Length > 8 ||
                    profileSkills.Select(skill => skill is null ? "" : skill.Id + "@" + skill.Version).Distinct(StringComparer.Ordinal).Count() != profileSkills.Length ||
                    profileSkills.Any(skill => skill is null || skill.ContentHash != WorkflowSkill.Create(skill.Id, skill.Version, skill.Name, skill.Instructions).ContentHash)) ||
                profile.Channels is { } channels && (definition.SchemaVersion is not ("1.12" or "1.13" or "1.14" or "1.15" or "1.19" or "1.18" or "1.17" or "1.16") || channels.IsDefault || channels.Length > 8 ||
                    channels.Distinct(StringComparer.Ordinal).Count() != channels.Length || channels.Any(channel => !ValidChannelId(channel))) ||
                profile.ReasoningEffort is { } profileEffort && !Enum.IsDefined(profileEffort) ||
                profile.MaxOutputTokens is { } outputTokens && outputTokens is < AgentOutputTokenLimits.Minimum or > AgentOutputTokenLimits.Maximum ||
                profile.ContentHash != expected?.ContentHash)
                Error("agentProfile.definition", $"agentProfiles.{key}", "A profile needs a valid identity, bounded instructions, pinned provider/tool/skill requirements and a matching content hash.");
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
            if (node.Type == NodeTypes.WorkflowCall)
            {
                var call = node.WorkflowCall;
                var hashMatches = false;
                if (call?.Definition is { } child)
                {
                    try { hashMatches = WorkflowJson.MatchesHash(child, call.ContentHash); }
                    catch (ArgumentException) { }
                }
                if (definition.SchemaVersion != "1.19" || call is null || string.IsNullOrWhiteSpace(call.WorkflowId) ||
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
            if (node.Type is NodeTypes.Condition or NodeTypes.Merge && definition.SchemaVersion is not ("1.4" or "1.5" or "1.6" or "1.7" or "1.8" or "1.9" or "1.10" or "1.11" or "1.12" or "1.13" or "1.14" or "1.15" or "1.16" or "1.19" or "1.18" or "1.17"))
                Error("node.schema", $"nodes.{id}", "Conditional and merge nodes require schema 1.4.");
            if (node.Type == NodeTypes.WorkflowValue)
            {
                if (definition.SchemaVersion is not ("1.6" or "1.7" or "1.8" or "1.9" or "1.10" or "1.11" or "1.12" or "1.13" or "1.14" or "1.15" or "1.19" or "1.18" or "1.17" or "1.16")) Error("node.schema", $"nodes.{id}", "Workflow value nodes require schema 1.6 or later.");
                if (node.ParameterId is not { } parameterId || !parameters.ContainsKey(parameterId))
                    Error("parameter.reference", $"nodes.{id}.parameterId", "Select a parameter defined by this workflow.");
            }
            else if (node.ParameterId is not null)
                Error("parameter.unexpected", $"nodes.{id}.parameterId", "Only workflow value nodes may reference a parameter.");
            if (node.InputDataType is not null && (node.Type != NodeTypes.Input || node.InputDataType != "json" || definition.SchemaVersion is not ("1.9" or "1.10" or "1.11" or "1.12" or "1.13" or "1.14" or "1.15" or "1.19" or "1.18" or "1.17" or "1.16")))
                Error("input.dataType", $"nodes.{id}.inputDataType", "Only schema 1.9 or later input nodes can declare the JSON data type.");
            if (node.OutputDataType is not null && (node.Type != NodeTypes.Output || node.OutputDataType != "json" || definition.SchemaVersion is not ("1.13" or "1.14" or "1.15" or "1.19" or "1.18" or "1.17" or "1.16")))
                Error("output.dataType", $"nodes.{id}.outputDataType", "Only schema 1.13 or later output nodes can declare the JSON data type.");
            if (node.Type == NodeTypes.JsonSelectString)
            {
                if (definition.SchemaVersion is not ("1.9" or "1.10" or "1.11" or "1.12" or "1.13" or "1.14" or "1.15" or "1.19" or "1.18" or "1.17" or "1.16")) Error("node.schema", $"nodes.{id}", "JSON selection nodes require schema 1.9 or later.");
                if (node.JsonPropertySelection is not { } selection || !ValidJsonPointer(selection.Pointer))
                    Error("json.selection", $"nodes.{id}.jsonPropertySelection", "Provide a valid RFC 6901 JSON pointer with at most 16 segments and 1,024 characters.");
            }
            else if (node.JsonPropertySelection is not null)
                Error("json.selection.unexpected", $"nodes.{id}.jsonPropertySelection", "Only JSON selection nodes can declare a JSON property pointer.");
            if (node.Type == NodeTypes.JsonProject)
            {
                if (definition.SchemaVersion is not ("1.13" or "1.14" or "1.15" or "1.19" or "1.18" or "1.17" or "1.16")) Error("node.schema", $"nodes.{id}", "JSON projection nodes require schema 1.13 or later.");
                if (!ValidJsonProjection(node.JsonProjection)) Error("json.projection", $"nodes.{id}.jsonProjection", "Map 1–16 unique JSON property names to valid bounded JSON Pointers.");
                if (node.JsonProjection?.Fields.Any(field => field?.ValueType is not null) == true && definition.SchemaVersion is not ("1.15" or "1.19" or "1.18" or "1.17" or "1.16"))
                    Error("json.projection.schema", $"nodes.{id}.jsonProjection", "Typed JSON projection fields require schema 1.15.");
                if (node.JsonProjection?.Fields.Any(field => field?.ValueSchema is not null) == true && definition.SchemaVersion is not ("1.16" or "1.19" or "1.18" or "1.17"))
                    Error("json.projection.schema", $"nodes.{id}.jsonProjection", "Nested JSON mapping schemas require schema 1.16 or later.");
            }
            else if (node.JsonProjection is not null)
                Error("json.projection.unexpected", $"nodes.{id}.jsonProjection", "Only JSON projection nodes may declare JSON field mappings.");
            if (node.Type is NodeTypes.ParallelFork or NodeTypes.ParallelBranch or NodeTypes.ParallelJoin && definition.SchemaVersion is not ("1.14" or "1.15" or "1.16" or "1.19" or "1.18" or "1.17"))
                Error("node.schema", $"nodes.{id}", "Parallel graph nodes require schema 1.14.");
            if (node.Type == NodeTypes.Loop && (definition.SchemaVersion is not ("1.17" or "1.19" or "1.18") || node.Loop is not { } loop ||
                loop.MaximumIterations is < 1 or > WorkflowLoopLimits.MaximumIterations || !ValidStringCondition(loop.ContinueWhile)))
                Error("loop.configuration", $"nodes.{id}.loop", $"Loops require schema 1.17 or later, an approved continue-while predicate, and 1–{WorkflowLoopLimits.MaximumIterations} maximum iterations.");
            if (node.Type != NodeTypes.Loop && node.Loop is not null)
                Error("loop.unexpected", $"nodes.{id}.loop", "Only loop nodes may declare loop configuration.");
            if (node.Type == NodeTypes.TextTemplate)
            {
                if (definition.SchemaVersion is not ("1.7" or "1.8" or "1.9" or "1.10" or "1.11" or "1.12" or "1.13" or "1.14" or "1.15" or "1.19" or "1.18" or "1.17" or "1.16")) Error("node.schema", $"nodes.{id}", "Text template nodes require schema 1.7 or later.");
                if (!ValidTextTemplate(node.TextTemplate, parameters))
                    Error("text.template", $"nodes.{id}.textTemplate", "Use 1–64 bounded literal, input, or declared-parameter parts.");
            }
            else if (node.TextTemplate is not null)
                Error("text.template.unexpected", $"nodes.{id}.textTemplate", "Only text template nodes may declare a template.");
            if (node.Type == NodeTypes.Approval)
            {
                if (definition.SchemaVersion is not ("1.5" or "1.6" or "1.7" or "1.8" or "1.9" or "1.10" or "1.11" or "1.12" or "1.13" or "1.14" or "1.15" or "1.19" or "1.18" or "1.17" or "1.16")) Error("node.schema", $"nodes.{id}", "Approval nodes require schema 1.5 or later.");
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
            if (node.Origin is { } origin && (definition.SchemaVersion is not ("1.3" or "1.4" or "1.5" or "1.6" or "1.7" or "1.8" or "1.9" or "1.10" or "1.11" or "1.12" or "1.13" or "1.14" or "1.15" or "1.19" or "1.18" or "1.17" or "1.16") ||
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
                if (definition.SchemaVersion is not ("1.2" or "1.3" or "1.4" or "1.5" or "1.6" or "1.7" or "1.8" or "1.9" or "1.10" or "1.11" or "1.12" or "1.13" or "1.14" or "1.15" or "1.19" or "1.18" or "1.17" or "1.16")) Error("node.schema", $"nodes.{id}", "Agent nodes require schema 1.2 or later.");
                if (node.Agent is not { } agent || string.IsNullOrWhiteSpace(agent.ProviderId) || agent.ProviderId.Length > 128 ||
                    string.IsNullOrWhiteSpace(agent.ProviderVersion) || agent.ProviderVersion.Length > 64 ||
                    agent.Instructions is null || agent.Instructions.Length > 8192 || agent.Tools.IsDefault || agent.Tools.Length > 8 ||
                    agent.Tools.Any(t => t is null || !ValidBinding(t)) || agent.Tools.Select(t => t.BindingId).Distinct(StringComparer.Ordinal).Count() != agent.Tools.Length)
                    Error("agent.configuration", $"nodes.{id}.agent", "An agent requires a provider/version, bounded instructions, and up to eight unique tool requirements.");
                if (node.Agent is { } configured)
                {
                    if (configured.UserMessage is { } template)
                    {
                        if (definition.SchemaVersion is not ("1.7" or "1.8" or "1.9" or "1.10" or "1.11" or "1.12" or "1.13" or "1.14" or "1.15" or "1.19" or "1.18" or "1.17" or "1.16") || template.Parts.IsDefault || template.Parts.Length is < 1 or > 64 ||
                            template.Parts.Any(part => part is null || part.Value is null || part.Value.Length > 8192 ||
                                part.Kind is not ("literal" or "input" or "parameter") ||
                                part.Kind == "parameter" && !parameters.ContainsKey(part.Value)))
                            Error("agent.prompt", $"nodes.{id}.agent.userMessage", "Use up to 64 literal, input or declared-parameter prompt parts in schema 1.7.");
                    }
                    if (configured.Skills is { } references)
                    {
                        if (definition.SchemaVersion is not ("1.7" or "1.8" or "1.9" or "1.10" or "1.11" or "1.12" or "1.13" or "1.14" or "1.15" or "1.19" or "1.18" or "1.17" or "1.16") || references.IsDefault || references.Length > 8 ||
                            references.Distinct().Count() != references.Length || references.Any(reference => reference is null ||
                                !skills.TryGetValue(reference.Id + "@" + reference.Version, out var skill) || skill.ContentHash != reference.ContentHash))
                            Error("agent.skills", $"nodes.{id}.agent.skills", "Each assigned skill must match an embedded immutable revision.");
                    }
                    if (configured.ReasoningEffort is not null &&
                        (definition.SchemaVersion is not ("1.7" or "1.8" or "1.9" or "1.10" or "1.11" or "1.12" or "1.13" or "1.14" or "1.15" or "1.19" or "1.18" or "1.17" or "1.16") || !Enum.IsDefined(configured.ReasoningEffort.Value) ||
                         configured.ReasoningEffort == AgentReasoningEffort.Off && definition.SchemaVersion is not ("1.10" or "1.11" or "1.12" or "1.13" or "1.14" or "1.15" or "1.19" or "1.18" or "1.17" or "1.16") ||
                         configured.ReasoningEffortParameterId is not null))
                        Error("agent.reasoning", $"nodes.{id}.agent.reasoningEffort", "Reasoning effort requires schema 1.7; explicit off requires schema 1.10.");
                    if (configured.MaxOutputTokens is { } maxOutputTokens &&
                        (definition.SchemaVersion is not ("1.10" or "1.11" or "1.12" or "1.13" or "1.14" or "1.15" or "1.19" or "1.18" or "1.17" or "1.16") || maxOutputTokens is < AgentOutputTokenLimits.Minimum or > AgentOutputTokenLimits.Maximum))
                        Error("agent.outputTokens", $"nodes.{id}.agent.maxOutputTokens", "Maximum output tokens require schema 1.10 and a value from 1 to 16,384.");
                    if (configured.ReasoningEffortParameterId is { } reasoningParameter &&
                        (definition.SchemaVersion is not ("1.8" or "1.9" or "1.10" or "1.11" or "1.12" or "1.13" or "1.14" or "1.15" or "1.19" or "1.18" or "1.17" or "1.16") || !parameters.TryGetValue(reasoningParameter, out var reasoningDefinition) ||
                         reasoningDefinition.Type != "choice" || reasoningDefinition.Choices is not { } effortChoices ||
                         !effortChoices.Contains("default") || !effortChoices.Contains("low") || !effortChoices.Contains("medium") || !effortChoices.Contains("high") ||
                         effortChoices.Contains("off") && definition.SchemaVersion is not ("1.10" or "1.11" or "1.12" or "1.13" or "1.14" or "1.15" or "1.19" or "1.18" or "1.17" or "1.16")))
                        Error("agent.reasoning.parameter", $"nodes.{id}.agent.reasoningEffortParameterId", "Use a schema 1.8 choice parameter containing default, low, medium, and high.");
                    if (configured.Delegates is { } delegates &&
                        (definition.SchemaVersion is not ("1.11" or "1.12" or "1.13" or "1.14" or "1.15" or "1.19" or "1.18" or "1.17" or "1.16") || delegates.IsDefault || delegates.Length > 4 || delegates.Distinct().Count() != delegates.Length ||
                         delegates.Any(reference => reference is null || !profiles.TryGetValue(reference.Id + "@" + reference.Version, out var profile) || profile is null ||
                             profile.ContentHash != reference.ContentHash ||
                             (profile.Skills ?? []).Any(profileSkill => !skills.TryGetValue(profileSkill.Id + "@" + profileSkill.Version, out var workflowSkill) || workflowSkill != profileSkill))))
                        Error("agent.delegates", $"nodes.{id}.agent.delegates", "Assign up to four embedded, immutable agent profile revisions in schema 1.11 or later.");
                }
            }
            else if (node.Agent is not null)
                Error("agent.unexpected", $"nodes.{id}.agent", "Only agent nodes may declare agent configuration.");
        }

        if (string.IsNullOrWhiteSpace(definition.EntryNodeId) ||
            !definition.Nodes.TryGetValue(definition.EntryNodeId, out var entry) || entry?.Type != NodeTypes.Input)
            Error("entry.invalid", "entryNodeId", "The entry must reference an input node.");

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
            if (edge.Kind != "data" || sourcePort is null || targetPort is null || sourcePort.DataType != targetPort.DataType)
                Error("connection.port", path, "Connect ports with matching declared data types using a data link.");
            if (!pairs.Add((source, edge.Source.PortId, target, edge.Target.PortId))) Error("connection.duplicate", path, "Duplicate connection.");
            incoming[target]++;
            outgoing[source].Add(target);
        }

        foreach (var (id, node) in definition.Nodes)
        {
            if (node is null) continue;
            if (node.Type == NodeTypes.Terminate && definition.SchemaVersion is not ("1.18" or "1.19"))
                Error("termination.schema", $"nodes.{id}", "Explicit workflow termination requires schema 1.18 or later.");
            if (definition.SchemaVersion is "1.4" or "1.5" or "1.6" or "1.7" or "1.8" or "1.9" or "1.10" or "1.11" or "1.12" or "1.13" or "1.14" or "1.15" or "1.16" or "1.17" or "1.19" or "1.18")
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
                Error("pipeline.degree", $"nodes.{id}", "Only a single input-to-output pipeline is supported; branches and joins are not yet supported.");
        }
        var terminators = definition.Nodes.Where(n => n.Value?.Type == NodeTypes.Terminate).Select(n => n.Key).ToHashSet(StringComparer.Ordinal);
        if (definition.Nodes.Values.Count(n => n?.Type == NodeTypes.Terminate) > 16)
            Error("graph.termination.limit", "nodes", "A workflow may have at most 16 termination exits.");
        if (definition.Nodes.Values.Count(n => n?.Type == NodeTypes.Input) != 1 ||
            definition.Nodes.Values.Count(n => n?.Type == NodeTypes.Output) != 1)
            Error("pipeline.terminals", "nodes", "Exactly one input and one output node are required.");

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

    public static void ThrowIfInvalid(WorkflowDefinition definition)
    {
        var issues = Validate(definition);
        if (issues.Length > 0)
            throw new ArgumentException(string.Join("; ", issues.Select(i => $"{i.Code}: {i.Message}")), nameof(definition));
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
        if (definition.SchemaVersion is not ("1.17" or "1.19" or "1.18")) error("loop.schema", "nodes", "Bounded loops require schema 1.17 or later.");

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
        if (definition.SchemaVersion is not ("1.14" or "1.15" or "1.16" or "1.19" or "1.18" or "1.17")) error("parallel.schema", "nodes", "Parallel blocks require schema 1.14.");
        if (forks.Length != 1 || joins.Length != 1)
        {
            error("parallel.pair", "nodes", "A workflow may contain one matched parallel fork and join block.");
            return;
        }

        var forkId = forks[0];
        var joinId = joins[0];
        var usedBranches = new HashSet<string>(StringComparer.Ordinal);
        var connections = definition.Connections.Values.ToArray();
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
                if (edge.Target.NodeId == joinId)
                {
                    if (edge.Target.PortId != branchPort)
                        error("parallel.branch.target", $"connections.{edge.Target.PortId}", "A branch must connect to the join input with the same branch number.");
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
            if (!reachedJoin) error("parallel.branch.incomplete", $"nodes.{forkId}.{branchPort}", "Every parallel branch must reach the matching join input.");
        }
        if (approvalBearingBranches > 1)
            error("parallel.approval.concurrent", $"nodes.{forkId}", "Both parallel branches cannot contain nested approvals because this run can suspend at only one approval request at a time.");
        if (branchNodes.Except(usedBranches, StringComparer.Ordinal).Any())
            error("parallel.branch.orphan", "nodes", "Every parallel branch node must belong to the workflow's fork-to-join block.");
    }

    private static bool ContainsNestedApproval(WorkflowDefinition? definition, int depth)
    {
        if (definition is null || depth >= WorkflowCallLimits.MaximumDepth) return false;
        return definition.Nodes.Values.Any(node => node?.Type == NodeTypes.Approval ||
            node?.WorkflowCall?.Definition is { } child && ContainsNestedApproval(child, depth + 1));
    }

    private static bool ValidHash(string? hash) => hash is { Length: 64 } && hash.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
}
