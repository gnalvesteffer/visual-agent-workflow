using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using VisualAgentWorkflow.Core;

namespace VisualAgentWorkflow.AgentFramework;

/// <summary>Host-registered provider factory; workflow documents cannot construct clients or supply endpoints/secrets.</summary>
public sealed record AgentProviderRegistration(AgentProviderDescriptor Descriptor, Func<IChatClient> Create)
{
    public TimeSpan InvocationTimeout { get; init; } = TimeSpan.FromSeconds(30);
    /// <summary>Optional trusted adapter mapping for provider-specific cache-write usage.</summary>
    public Func<ChatResponse, long?>? CacheWriteTokenCount { get; init; }
}

public sealed class AgentNodeExecutor(IEnumerable<AgentProviderRegistration> providers, INodeToolBroker tools) : IAgentNodeExecutor
{
    private IReadOnlyDictionary<string, AgentProviderRegistration> registrations = Index(providers);

    public void ReplaceProviders(IEnumerable<AgentProviderRegistration> providers) =>
        Volatile.Write(ref registrations, Index(providers));

    private static IReadOnlyDictionary<string, AgentProviderRegistration> Index(IEnumerable<AgentProviderRegistration> providers) =>
        providers.ToDictionary(provider => provider.Descriptor.ProviderId, StringComparer.Ordinal);

    private static JsonDocument ChannelSchema(ImmutableArray<string> channels, bool includeMessage)
    {
        var properties = new Dictionary<string, object>
        {
            ["channel"] = new { type = "string", @enum = channels }
        };
        var required = new List<string> { "channel" };
        if (includeMessage)
        {
            properties["message"] = new { type = "string", minLength = 1, maxLength = AgentCoordinationChannelBoard.MaximumMessageCharacters };
            required.Add("message");
        }
        return JsonDocument.Parse(JsonSerializer.Serialize(new { type = "object", properties, required, additionalProperties = false }));
    }

    private static JsonDocument GraphFunctionSchema(GraphFunctionDefinition function)
    {
        var properties = function.Inputs.ToDictionary(pair => pair.Key, pair => pair.Value.DataType switch
        {
            "string" => (object)new { type = "string", maxLength = GraphFunctionLimits.MaximumStringLength, description = pair.Value.DisplayName },
            "boolean" => new { type = "boolean", description = pair.Value.DisplayName },
            _ => throw new InvalidOperationException("The graph function declares an unsupported tool input type.")
        }, StringComparer.Ordinal);
        return JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            type = "object",
            properties,
            required = function.Inputs.Keys,
            additionalProperties = false
        }));
    }

    private static object GraphFunctionArgument(AIFunctionArguments arguments, string portId, string dataType)
    {
        if (!arguments.TryGetValue(portId, out var value))
            throw new InvalidOperationException($"Graph function input '{portId}' is required.");
        if (value is JsonElement jsonValue)
            value = dataType == "string" && jsonValue.ValueKind == JsonValueKind.String ? jsonValue.GetString() :
                dataType == "boolean" && jsonValue.ValueKind is JsonValueKind.True or JsonValueKind.False ? jsonValue.GetBoolean() : value;
        return dataType switch
        {
            "string" when value is string text && text.Length <= GraphFunctionLimits.MaximumStringLength => text,
            "boolean" when value is bool boolean => boolean,
            "string" => throw new InvalidOperationException($"Graph function input '{portId}' must be a string of at most {GraphFunctionLimits.MaximumStringLength} characters."),
            "boolean" => throw new InvalidOperationException($"Graph function input '{portId}' must be a Boolean."),
            _ => throw new InvalidOperationException("The graph function declares an unsupported tool input type.")
        };
    }

    private static JsonDocument StructuredResultSchema() => JsonDocument.Parse("""
        {"type":"object","properties":{"status":{"type":"string","enum":["success","failure"]},"payload":{"type":"string","minLength":2,"maxLength":16384,"description":"A JSON object encoded as text; it must fit the node's bounded result payload limit."}},"required":["status","payload"],"additionalProperties":false}
        """);

    private static JsonDocument AgentRouteSchema(ImmutableArray<AgentExecutionRoute> routes) => JsonDocument.Parse(JsonSerializer.Serialize(new
    {
        type = "object",
        properties = new { route_id = new { type = "string", @enum = routes.Select(route => route.Id).ToArray() } },
        required = new[] { "route_id" },
        additionalProperties = false
    }));

    private static string RequiredString(AIFunctionArguments arguments, string name) =>
        arguments.TryGetValue(name, out var value) && value is string text
            ? text
            : throw new InvalidOperationException($"A string value for {name} is required.");

    private static int? ResolveAgentLimit(string? parameterId, int? staticValue, IReadOnlyDictionary<string, string>? parameters)
    {
        if (parameterId is null) return staticValue;
        if (parameters is null || !parameters.TryGetValue(parameterId, out var value) ||
            !int.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var resolved))
            throw new InvalidOperationException($"Agent limit parameter '{parameterId}' is unavailable or invalid.");
        return resolved;
    }

    public Task<IReadOnlyList<AgentProviderDescriptor>> ListProvidersAsync() =>
        Task.FromResult<IReadOnlyList<AgentProviderDescriptor>>(Volatile.Read(ref registrations).Values.Select(r => r.Descriptor).ToArray());

    public async Task<string> ExecuteAsync(NodeInvocationContext invocation, string input, CancellationToken cancellationToken = default)
    {
        WorkflowValidator.ThrowIfInvalid(invocation.Revision.Definition);
        var configuration = invocation.Revision.Definition.Nodes[invocation.NodeId].Agent
            ?? throw new ArgumentException("The invocation is not an agent node.");
        return await ExecuteConfigurationAsync(invocation, configuration, input, true, cancellationToken);
    }

    private async Task<string> ExecuteConfigurationAsync(NodeInvocationContext invocation, AgentConfiguration configuration,
        string input, bool allowDelegation, CancellationToken cancellationToken, AgentCoordinationChannelAccess? channelAccess = null,
        AgentTokenBudget? sharedTokenBudget = null)
    {
        var maximumOutputTokens = ResolveAgentLimit(configuration.MaxOutputTokensParameterId,
            configuration.MaxOutputTokens ?? AgentOutputTokenLimits.LegacyDefault, invocation.Parameters);
        var maximumTotalTokens = ResolveAgentLimit(configuration.MaxTotalTokensParameterId,
            configuration.MaxTotalTokens, invocation.Parameters);
        var tokenBudget = sharedTokenBudget ?? (maximumTotalTokens is { } maximumTokens ? new AgentTokenBudget(maximumTokens) : null);
        if (!Volatile.Read(ref registrations).TryGetValue(configuration.ProviderId, out var provider))
            throw new InvalidOperationException($"Agent connection '{configuration.ProviderId}' is unavailable.");
        if (configuration.ProviderContractHash is { } expectedContract &&
            !string.Equals(expectedContract, provider.Descriptor.ContractHash, StringComparison.Ordinal))
            throw new InvalidOperationException($"Agent connection '{configuration.ProviderId}' no longer matches the workflow's provider contract.");
        var reasoningEffort = configuration.ReasoningEffortParameterId is { } parameterId && invocation.Parameters is { } values
            ? values.GetValueOrDefault(parameterId) switch
            {
                "off" => AgentReasoningEffort.Off,
                "low" => AgentReasoningEffort.Low,
                "medium" => AgentReasoningEffort.Medium,
                "high" => AgentReasoningEffort.High,
                _ => (AgentReasoningEffort?)null
            }
            : configuration.ReasoningEffort;
        if (reasoningEffort is not null && !provider.Descriptor.SupportsReasoningEffort)
            throw new InvalidOperationException("The selected provider does not support reasoning effort.");
        var assignedSkills = (configuration.Skills ?? []).Select(reference =>
            invocation.Revision.Definition.Skills![reference.Id + "@" + reference.Version]).ToArray();
        var instructions = string.Join("\n\n", new[] { configuration.Instructions }
            .Concat(assignedSkills.Select(skill => $"Skill: {skill.Name}\n{skill.Instructions}")));
        if (instructions.Length > 65_536) throw new InvalidOperationException("Combined agent instructions exceed the prompt budget.");
        var userMessage = configuration.UserMessage is null ? input :
            PromptRendering.Render(configuration.UserMessage, input, invocation.Parameters);
        AgentStructuredSubmission? structuredSubmission = null;
        var toolInvocationFailed = false;
        var lastFinishReason = "";
        using var deadline = new InvocationDeadline(cancellationToken);
        if (provider.InvocationTimeout <= TimeSpan.Zero)
            throw new InvalidOperationException("The registered model connection timeout is invalid.");
        var configuredTimeout = configuration.TimeoutSeconds is { } timeoutSeconds
            ? TimeSpan.FromSeconds(timeoutSeconds)
            : provider.InvocationTimeout;
        var effectiveTimeout = configuredTimeout;
        deadline.Start(effectiveTimeout);
        await Record(ExecutionEventKinds.AgentStarted, new
        {
            provider = provider.Descriptor,
            timeoutSeconds = effectiveTimeout.TotalSeconds,
            configuredTimeoutSeconds = configuration.TimeoutSeconds,
            maximumTurns = configuration.MaxTurns ?? AgentExecutionLimits.DefaultMaximumTurns,
            maxOutputTokens = maximumOutputTokens,
            maxOutputTokensParameterId = configuration.MaxOutputTokensParameterId,
            maxTotalTokensParameterId = configuration.MaxTotalTokensParameterId,
            maxTotalTokens = tokenBudget?.Maximum,
            usageNormalization = new
            {
                cacheRead = "provider-reported cached input tokens; included in input token count",
                cacheWrite = provider.CacheWriteTokenCount is null ? "unavailable" : "trusted provider adapter mapping"
            },
            structuredResultRequired = configuration.RequireStructuredResult == true,
            reasoningEffort,
            requestedTools = configuration.Tools.Select((binding, index) => new { functionName = $"tool_{index}", binding.BindingId, binding.ContractHash }),
            requestedGraphFunctionTools = (configuration.GraphFunctionTools ?? []).Select(function => new
            {
                functionId = function.Definition.FunctionId,
                version = function.Definition.Version,
                function.ContentHash
            }),
            skills = assignedSkills.Select(skill => new { skill.Id, skill.Version, skill.ContentHash })
        });
        try
        {
            await using var scope = tools.OpenScope(invocation);
            var allowed = await scope.ListToolsAsync(deadline.Token);
            foreach (var requirement in configuration.Tools)
            {
                if (allowed.Any(t => t.BindingId == requirement.BindingId)) continue;
                await Record(ExecutionEventKinds.ToolDenied, new { requirement.BindingId, reason = "Agent tool requirement has no effective host grant." });
                toolInvocationFailed = true;
                throw new ToolAccessDeniedException();
            }
            string? lastSuccessfulToolResult = null;
            var recoverableToolFeedbacks = 0;
            var graphFunctionCalls = 0;
            var graphFunctionNodeVisits = 0;
            var functions = new List<AITool>();
            foreach (var binding in allowed)
            {
                var name = $"tool_{functions.Count}";
                using var schema = JsonDocument.Parse(binding.InputSchemaJson);
                var description = string.IsNullOrWhiteSpace(binding.ToolDescription)
                    ? $"Approved MCP tool. Binding: {binding.BindingId}."
                    : $"{binding.ToolDescription}\nMCP binding: {binding.BindingId}.";
                functions.Add(new BoundMcpFunction(name,
                    description,
                    schema.RootElement.Clone(), async (arguments, token) =>
                    {
                        var json = JsonSerializer.SerializeToElement(arguments.ToDictionary(pair => pair.Key, pair => pair.Value));
                        try
                        {
                            var result = await scope.InvokeJsonAsync(binding.BindingId, json, token);
                            lastSuccessfulToolResult = result;
                            return result;
                        }
                        catch (ToolInvocationException failure) when (failure.Disposition == ToolInvocationFailureDisposition.RecoverableFeedback)
                        {
                            toolInvocationFailed = true;
                            if (Interlocked.Increment(ref recoverableToolFeedbacks) > AgentExecutionLimits.MaximumRecoverableToolFeedbacks)
                                throw new ToolInvocationException($"The agent received too many recoverable tool failures (limit {AgentExecutionLimits.MaximumRecoverableToolFeedbacks}).");
                            return $"The assigned tool reported a recoverable failure: {failure.Message} Do not repeat the same call unchanged; correct the arguments or choose another assigned tool.";
                        }
                        catch
                        {
                            toolInvocationFailed = true;
                            throw;
                        }
                    }));
            }
            foreach (var functionSnapshot in configuration.GraphFunctionTools ?? [])
            {
                var graphFunction = functionSnapshot.Definition;
                using var schema = GraphFunctionSchema(graphFunction);
                functions.Add(new BoundMcpFunction($"graph_function_{functions.Count}",
                    $"{graphFunction.Description}\nPure deterministic graph function: {graphFunction.DisplayName} (ID {graphFunction.FunctionId}, revision {graphFunction.Version}). Returns a JSON object with its declared outputs.",
                    schema.RootElement.Clone(), async (arguments, token) =>
                    {
                        var functionInvocationId = Guid.NewGuid();
                        var inputValues = new Dictionary<string, object>(StringComparer.Ordinal);
                        await invocation.RecordEventAsync(ExecutionEventKinds.AgentGraphFunctionStarted, JsonSerializer.Serialize(new
                        {
                            functionInvocationId,
                            functionId = graphFunction.FunctionId,
                            version = graphFunction.Version,
                            contentHash = functionSnapshot.ContentHash
                        }));
                        try
                        {
                            if (Interlocked.Increment(ref graphFunctionCalls) > AgentGraphFunctionToolLimits.MaximumCallsPerInvocation)
                                throw new InvalidOperationException($"The agent reached its {AgentGraphFunctionToolLimits.MaximumCallsPerInvocation}-call graph-function limit.");
                            foreach (var (inputId, inputPort) in graphFunction.Inputs)
                                inputValues.Add(inputId, GraphFunctionArgument(arguments, inputId, inputPort.DataType));
                            if (arguments.Keys.Any(key => !graphFunction.Inputs.ContainsKey(key)))
                                throw new InvalidOperationException("The graph function received an undeclared input.");
                            var inputCharacters = JsonSerializer.Serialize(inputValues).Length;
                            if (inputCharacters > AgentGraphFunctionToolLimits.MaximumInputCharacters)
                                throw new InvalidOperationException("Graph function inputs exceed the 65,536-character aggregate limit.");
                            var functionInputs = inputValues.ToDictionary(pair => pair.Key,
                                pair => new GraphFunctionEvaluation(graphFunction.Inputs[pair.Key].DataType, pair.Value, []), StringComparer.Ordinal);
                            var outputs = new SortedDictionary<string, object>(StringComparer.Ordinal);
                            var evaluated = new List<GraphFunctionValueEvidence>();
                            foreach (var (outputId, _) in graphFunction.Outputs)
                            {
                                var result = GraphFunctionJson.Evaluate(graphFunction, functionInputs, outputId, () =>
                                {
                                    var visit = Interlocked.Increment(ref graphFunctionNodeVisits);
                                    if (visit > AgentGraphFunctionToolLimits.MaximumEvaluatedNodesPerInvocation)
                                        throw new InvalidOperationException($"The agent reached its {AgentGraphFunctionToolLimits.MaximumEvaluatedNodesPerInvocation}-node graph-function evaluation limit.");
                                    return visit;
                                });
                                outputs.Add(outputId, result.Value);
                                evaluated.AddRange(result.Values);
                            }
                            var resultJson = JsonSerializer.Serialize(new { outputs });
                            if (resultJson.Length > AgentGraphFunctionToolLimits.MaximumOutputCharacters)
                                throw new InvalidOperationException("Graph function outputs exceed the 65,536-character aggregate limit.");
                            await invocation.RecordEventAsync(ExecutionEventKinds.AgentGraphFunctionCompleted, JsonSerializer.Serialize(new
                            {
                                functionInvocationId,
                                functionId = graphFunction.FunctionId,
                                version = graphFunction.Version,
                                contentHash = functionSnapshot.ContentHash,
                                inputs = inputValues,
                                outputs,
                                evaluatedValues = evaluated
                            }));
                            lastSuccessfulToolResult = resultJson;
                            return resultJson;
                        }
                        catch (Exception failure)
                        {
                            toolInvocationFailed = true;
                            await invocation.RecordEventAsync(ExecutionEventKinds.AgentGraphFunctionFailed, JsonSerializer.Serialize(new
                            {
                                functionInvocationId,
                                functionId = graphFunction.FunctionId,
                                version = graphFunction.Version,
                                contentHash = functionSnapshot.ContentHash,
                                inputs = inputValues,
                                reason = failure.Message
                            }));
                            throw;
                        }
                    }));
            }
            if (configuration.RequireStructuredResult == true)
            {
                using var resultSchema = StructuredResultSchema();
                functions.Add(new BoundMcpFunction("submit_result",
                    "Submit the required bounded JSON object result exactly once. status must be success or failure; payload must be a JSON object encoded as text.",
                    resultSchema.RootElement.Clone(), (arguments, _) =>
                    {
                        var status = RequiredString(arguments, "status");
                        if (status is not ("success" or "failure"))
                            throw new InvalidOperationException("The structured result status must be success or failure.");
                        var payloadText = RequiredString(arguments, "payload");
                        if (payloadText.Length > AgentResultLimits.MaximumPayloadCharacters)
                            throw new InvalidOperationException("The structured result payload exceeds its size limit.");
                        using var payload = JsonDocument.Parse(payloadText, new JsonDocumentOptions { MaxDepth = 16 });
                        if (payload.RootElement.ValueKind != JsonValueKind.Object || HasDuplicateProperties(payload.RootElement))
                            throw new InvalidOperationException("The structured result payload must be a JSON object without duplicate properties.");
                        var submission = new AgentStructuredSubmission(status, payload.RootElement.GetRawText());
                        if (Interlocked.CompareExchange(ref structuredSubmission, submission, null) is not null)
                            throw new InvalidOperationException("A structured result can be submitted only once.");
                        return ValueTask.FromResult<string>("Structured result accepted.");
                    }));
            }
            if (configuration.Routes is { IsDefaultOrEmpty: false } routes)
            {
                if (invocation.ProposeAgentRoute is null)
                    throw new InvalidOperationException("The workflow runtime does not support Agent execution routes.");
                using var routeSchema = AgentRouteSchema(routes);
                functions.Add(new BoundMcpFunction("select_route",
                    "Select exactly one authored workflow execution route after completing the task. This does not invoke the route; the workflow runtime activates it only after the agent finishes successfully.",
                    routeSchema.RootElement.Clone(), async (arguments, _) =>
                    {
                        await invocation.ProposeAgentRoute(RequiredString(arguments, "route_id"));
                        return "Route selection recorded. Continue only if more work is required, then finish the task.";
                    }));
            }
            if (channelAccess is { } channels)
            {
                var channelIds = channels.Board.ChannelsFor(channels.Profile);
                if (!channelIds.IsEmpty)
                {
                    using var sendSchema = ChannelSchema(channelIds, includeMessage: true);
                    functions.Add(new BoundMcpFunction("channel_send",
                        "Append a message to one of your assigned coordination channels. Other assigned profiles on that channel can read it.",
                        sendSchema.RootElement.Clone(), async (arguments, token) =>
                        {
                            var channel = RequiredString(arguments, "channel");
                            var message = RequiredString(arguments, "message");
                            return await channels.Board.SendAsync(invocation, channels.Profile, channel, message, token);
                        }));
                    using var readSchema = ChannelSchema(channelIds, includeMessage: false);
                    functions.Add(new BoundMcpFunction("channel_read",
                        "Read the append-only messages currently recorded in one of your assigned coordination channels.",
                        readSchema.RootElement.Clone(), async (arguments, token) =>
                        {
                            var channel = RequiredString(arguments, "channel");
                            return await channels.Board.ReadAsync(invocation, channels.Profile, channel, token);
                        }));
                }
            }
            if (invocation.CollaborationSendChannels is { Count: > 0 } sendChannels)
            {
                if (invocation.CollaborationSend is null)
                    throw new InvalidOperationException("The runtime did not provide the assigned collaboration publish capability.");
                using var schema = ChannelSchema([.. sendChannels], includeMessage: true);
                functions.Add(new BoundMcpFunction("collaboration_publish",
                    $"Publish a bounded message to a channel assigned to this parallel collaboration branch for {invocation.CollaborationPhase ?? "the collaboration"}.",
                    schema.RootElement.Clone(), async (arguments, token) =>
                        await invocation.CollaborationSend(RequiredString(arguments, "channel"), RequiredString(arguments, "message"), token)));
            }
            if (invocation.CollaborationReadChannels is { Count: > 0 } readChannels)
            {
                if (invocation.CollaborationRead is null)
                    throw new InvalidOperationException("The runtime did not provide the assigned collaboration snapshot capability.");
                using var schema = ChannelSchema([.. readChannels], includeMessage: false);
                functions.Add(new BoundMcpFunction("collaboration_read",
                    $"Read the immutable {invocation.CollaborationPhase ?? "collaboration"} snapshot captured when every branch reached its rendezvous.",
                    schema.RootElement.Clone(), async (arguments, token) =>
                        await invocation.CollaborationRead(RequiredString(arguments, "channel"), token)));
            }
            if (allowDelegation && configuration.Delegates is { } delegates)
            {
                var profiles = invocation.Revision.Definition.AgentProfiles
                    ?? throw new InvalidOperationException("The workflow has no embedded agent profiles.");
                var assignedProfiles = delegates.Select(reference =>
                {
                    if (!profiles.TryGetValue(reference.Id + "@" + reference.Version, out var profile) || profile.ContentHash != reference.ContentHash)
                        throw new InvalidOperationException("An assigned agent profile is unavailable or has changed.");
                    return profile;
                }).ToArray();
                var parentChannels = new AgentCoordinationChannelBoard(invocation.InvocationId, "agent-invocation",
                    invocation.InvocationId, assignedProfiles);
                var delegatedCalls = 0;
                async Task<string> InvokeProfileAsync(AgentProfile profile, string task, CancellationToken token,
                    Guid? teamInvocationId = null, bool callReserved = false, AgentCoordinationChannelBoard? channels = null)
                {
                    if (!callReserved && Interlocked.Increment(ref delegatedCalls) > 4)
                    {
                        Interlocked.Decrement(ref delegatedCalls);
                        throw new InvalidOperationException("The agent delegation call limit was reached.");
                    }
                    var childInvocationId = Guid.NewGuid();
                    var childContext = invocation with
                    {
                        InvocationId = childInvocationId,
                        ToolRequirements = profile.Tools,
                        CollaborationPhase = null,
                        CollaborationRole = null,
                        CollaborationSendChannels = null,
                        CollaborationReadChannels = null,
                        CollaborationSend = null,
                        CollaborationRead = null,
                        RecordWithInvocation = (eventInvocationId, kind, data) => invocation.RecordEventForAsync(
                            eventInvocationId, kind, JsonSerializer.Serialize(new
                            {
                                delegatedProfileId = profile.Id,
                                delegatedProfileVersion = profile.Version,
                                childInvocationId,
                                parentInvocationId = invocation.InvocationId,
                                teamInvocationId,
                                eventData = data
                            }))
                    };
                    var childConfiguration = new AgentConfiguration(profile.ProviderId, profile.ProviderVersion, profile.Instructions, profile.Tools)
                    {
                        Skills = profile.Skills?.Select(skill => new SkillReference(skill.Id, skill.Version, skill.ContentHash)).ToImmutableArray(),
                        ReasoningEffort = profile.ReasoningEffort,
                        MaxOutputTokens = profile.MaxOutputTokens,
                        MaxTotalTokens = tokenBudget?.Maximum
                    };
                    await childContext.RecordEventAsync(ExecutionEventKinds.AgentStarted, JsonSerializer.Serialize(new
                    {
                        delegatedProfileId = profile.Id,
                        delegatedProfileVersion = profile.Version,
                        profile.ContentHash,
                        childInvocationId,
                        parentInvocationId = invocation.InvocationId,
                        teamInvocationId
                    }));
                    try
                    {
                        var result = await ExecuteConfigurationAsync(childContext, childConfiguration, task, false, token,
                            channels is null ? null : new(channels, profile), tokenBudget);
                        await childContext.RecordEventAsync(ExecutionEventKinds.AgentCompleted, JsonSerializer.Serialize(new
                        {
                            delegatedProfileId = profile.Id,
                            delegatedProfileVersion = profile.Version,
                            childInvocationId,
                            parentInvocationId = invocation.InvocationId,
                            teamInvocationId,
                            output = result
                        }));
                        return result;
                    }
                    catch
                    {
                        await childContext.RecordEventAsync(ExecutionEventKinds.AgentFailed, JsonSerializer.Serialize(new
                        {
                            delegatedProfileId = profile.Id,
                            delegatedProfileVersion = profile.Version,
                            childInvocationId,
                            parentInvocationId = invocation.InvocationId,
                            teamInvocationId,
                            reason = "Delegated agent failed."
                        }));
                        throw;
                    }
                }

                foreach (var profile in assignedProfiles)
                {
                    var name = $"delegate_{functions.Count}";
                    using var schema = JsonDocument.Parse("""{"type":"object","properties":{"task":{"type":"string","description":"The bounded task for this specialist agent."}},"required":["task"],"additionalProperties":false}""");
                    functions.Add(new BoundMcpFunction(name,
                        $"Delegate a bounded task to {profile.Name}. {profile.Description}", schema.RootElement.Clone(), async (arguments, token) =>
                        {
                            if (!arguments.TryGetValue("task", out var taskValue) || taskValue is not string task || string.IsNullOrWhiteSpace(task) || task.Length > 16_384)
                                throw new InvalidOperationException("A delegate task must contain 1–16,384 characters.");
                            return await InvokeProfileAsync(profile, task, token, channels: parentChannels);
                        }));
                }

                if (assignedProfiles.Length >= 2)
                {
                    using var schema = JsonDocument.Parse("""{"type":"object","properties":{"task":{"type":"string","description":"Shared task given to every assigned team member."}},"required":["task"],"additionalProperties":false}""");
                    functions.Add(new BoundMcpFunction("team_0", "Run every assigned specialist in parallel on the same task. The result contains one named output per specialist.",
                        schema.RootElement.Clone(), async (arguments, token) =>
                        {
                            if (!arguments.TryGetValue("task", out var taskValue) || taskValue is not string task || string.IsNullOrWhiteSpace(task) || task.Length > 16_384)
                                throw new InvalidOperationException("A team task must contain 1–16,384 characters.");
                            if (Interlocked.Add(ref delegatedCalls, assignedProfiles.Length) > 4)
                            {
                                Interlocked.Add(ref delegatedCalls, -assignedProfiles.Length);
                                throw new InvalidOperationException("The team would exceed the four-call delegation limit.");
                            }
                            var teamInvocationId = Guid.NewGuid();
                            var teamChannels = new AgentCoordinationChannelBoard(teamInvocationId, "team", invocation.InvocationId, assignedProfiles);
                            await invocation.RecordEventAsync(ExecutionEventKinds.AgentTeamStarted, JsonSerializer.Serialize(new
                            {
                                teamInvocationId,
                                parentInvocationId = invocation.InvocationId,
                                profiles = assignedProfiles.Select(profile => new { profile.Id, profile.Version, profile.ContentHash }),
                                task
                            }));
                            using var teamCancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
                            try
                            {
                                async Task<object> RunMemberAsync(AgentProfile profile)
                                {
                                    try
                                    {
                                        return new
                                        {
                                            profile.Id,
                                            profile.Version,
                                            profile.Name,
                                            output = await InvokeProfileAsync(profile, task, teamCancellation.Token, teamInvocationId,
                                                callReserved: true, channels: teamChannels)
                                        };
                                    }
                                    catch
                                    {
                                        teamCancellation.Cancel();
                                        throw;
                                    }
                                }
                                var members = assignedProfiles.Select(RunMemberAsync);
                                var results = await Task.WhenAll(members);
                                var aggregated = JsonSerializer.Serialize(results);
                                if (aggregated.Length > 80_000)
                                    throw new InvalidOperationException("Named team results exceed the 80,000-character aggregate limit.");
                                await invocation.RecordEventAsync(ExecutionEventKinds.AgentTeamCompleted, JsonSerializer.Serialize(new
                                {
                                    teamInvocationId,
                                    parentInvocationId = invocation.InvocationId,
                                    members = results.Length,
                                    output = aggregated
                                }));
                                return aggregated;
                            }
                            catch
                            {
                                teamCancellation.Cancel();
                                await invocation.RecordEventAsync(ExecutionEventKinds.AgentTeamFailed, JsonSerializer.Serialize(new
                                {
                                    teamInvocationId,
                                    parentInvocationId = invocation.InvocationId,
                                    reason = "At least one team member failed; the aggregate was not produced."
                                }));
                                throw;
                            }
                        }));
                }
            }
            var configuredMaximumTurns = configuration.MaxTurns ?? AgentExecutionLimits.DefaultMaximumTurns;
            var executionMaximumTurns = configuredMaximumTurns == AgentExecutionLimits.UnlimitedTurns
                ? int.MaxValue
                : configuredMaximumTurns;
            using var client = new FunctionInvokingChatClient(new RecordingClient(provider.Create(), invocation, executionMaximumTurns,
                tokenBudget, provider.CacheWriteTokenCount, response => lastFinishReason = response.FinishReason?.ToString() ?? ""))
            {
                MaximumIterationsPerRequest = executionMaximumTurns,
                MaximumConsecutiveErrorsPerRequest = 1,
                AllowConcurrentInvocation = false,
                IncludeDetailedErrors = false
            };
            var agent = new ChatClientAgent(client, new ChatClientAgentOptions
            {
                Name = invocation.NodeId,
                UseProvidedChatClientAsIs = true,
            ChatOptions = new ChatOptions
            {
                Instructions = string.Join("\n\n", new[] { instructions,
                    configuration.RequireStructuredResult == true ? "This node requires a structured result. Call submit_result exactly once with status success or failure and a JSON object payload. Free-form text alone does not complete the task." : null,
                    configuration.Routes is { IsDefaultOrEmpty: false } ? "This node requires exactly one call to select_route using the route that best fits the completed task. Route names are authored workflow choices; never invent a route." : null
                }.Where(part => !string.IsNullOrWhiteSpace(part))),
                Tools = functions,
                MaxOutputTokens = maximumOutputTokens,
                Reasoning = reasoningEffort is { } effort
                    ? new ReasoningOptions { Effort = effort switch
                    {
                        AgentReasoningEffort.Off => Microsoft.Extensions.AI.ReasoningEffort.None,
                        AgentReasoningEffort.Low => Microsoft.Extensions.AI.ReasoningEffort.Low,
                        AgentReasoningEffort.Medium => Microsoft.Extensions.AI.ReasoningEffort.Medium,
                        AgentReasoningEffort.High => Microsoft.Extensions.AI.ReasoningEffort.High,
                        _ => throw new InvalidOperationException("Unsupported reasoning effort.")
                    } }
                    : null
            }
            });
            var response = await agent.RunAsync(userMessage, cancellationToken: deadline.Token);
            if (configuration.RequireStructuredResult == true)
            {
                var submission = Volatile.Read(ref structuredSubmission);
                if (submission is null)
                    return await StructuredOutcomeAsync(lastFinishReason == "Length" ? AgentResultStatuses.TokensExhausted : AgentResultStatuses.InvalidResult,
                        lastFinishReason == "Length" ? "The model response reached its output-token cap before submitting a result." : "The agent did not submit a valid structured result.");
                var status = submission.Status == "success" ? AgentResultStatuses.Success : AgentResultStatuses.ReportedFailure;
                await Record(ExecutionEventKinds.AgentCompleted, new { status, payload = JsonDocument.Parse(submission.PayloadJson).RootElement.Clone() });
                return StructuredResultJson(status, submission.PayloadJson);
            }
            var output = response.Text;
            var completionMode = "model-response";
            if (string.IsNullOrWhiteSpace(output) && lastSuccessfulToolResult is not null)
            {
                output = lastSuccessfulToolResult.Length <= 16_384
                    ? lastSuccessfulToolResult
                    : "The assigned tool completed successfully; its result exceeded the text output limit.";
                if (string.IsNullOrWhiteSpace(output)) output = "The assigned tool completed successfully.";
                completionMode = "successful-tool-result-fallback";
            }
            if (string.IsNullOrWhiteSpace(output) || output.Length > 16_384)
                throw new InvalidOperationException("The agent did not produce bounded final text or a successful tool result.");
            await Record(ExecutionEventKinds.AgentCompleted, new { output, completionMode });
            return output;
        }
        catch (AgentTurnLimitException)
        {
            await Record(ExecutionEventKinds.AgentLimitExceeded, new
            {
                outcome = "turns_exhausted",
                maximumTurns = configuration.MaxTurns ?? AgentExecutionLimits.DefaultMaximumTurns
            });
            if (configuration.RequireStructuredResult == true)
                return await StructuredOutcomeAsync(AgentResultStatuses.TurnsExhausted, "The agent reached its configured model-turn limit.");
            await Record(ExecutionEventKinds.AgentFailed, new { outcome = "turns_exhausted", reason = "The agent reached its configured model-turn limit." });
            throw new InvalidOperationException("The agent reached its configured model-turn limit.");
        }
        catch (AgentTokenLimitException failure)
        {
            await Record(ExecutionEventKinds.AgentLimitExceeded, new
            {
                outcome = AgentResultStatuses.TokensExhausted,
                maximumTokens = failure.MaximumTokens,
                observedTokens = failure.ObservedTokens
            });
            if (configuration.RequireStructuredResult == true)
                return await StructuredOutcomeAsync(AgentResultStatuses.TokensExhausted, "The agent reached its configured reported-token limit.");
            await Record(ExecutionEventKinds.AgentFailed, new { outcome = AgentResultStatuses.TokensExhausted, reason = "The agent reached its configured reported-token limit." });
            throw new InvalidOperationException("The agent reached its configured reported-token limit.");
        }
        catch (AgentTokenUsageUnavailableException)
        {
            if (configuration.RequireStructuredResult == true)
                return await StructuredOutcomeAsync(AgentResultStatuses.TokenUsageUnavailable, "The provider did not report usage required by the configured token budget.");
            await Record(ExecutionEventKinds.AgentFailed, new
            {
                outcome = AgentResultStatuses.TokenUsageUnavailable,
                reason = "The configured aggregate token budget requires provider-reported usage."
            });
            throw new InvalidOperationException("The provider did not report token usage required by the configured aggregate budget.");
        }
        catch (OperationCanceledException)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                await Record(ExecutionEventKinds.AgentFailed, new { outcome = "cancelled", reason = "The host cancelled the agent invocation." });
                throw;
            }
            await Record(ExecutionEventKinds.AgentLimitExceeded, new { outcome = "timeout", timeoutSeconds = effectiveTimeout.TotalSeconds });
            if (configuration.RequireStructuredResult == true)
                return await StructuredOutcomeAsync(AgentResultStatuses.Timeout, "The agent reached its configured time limit.");
            await Record(ExecutionEventKinds.AgentFailed, new { outcome = "timeout", reason = "The agent reached its configured time limit. Provider outcome may be unknown; no automatic retry." });
            throw new InvalidOperationException("The agent operation timed out.");
        }
        catch (AgentProviderRequestException failure)
        {
            var reason = failure.UserMessage;
            if (configuration.RequireStructuredResult == true)
                return await StructuredOutcomeAsync(AgentResultStatuses.ProviderError, reason);
            await Record(ExecutionEventKinds.AgentFailed, new { reason,
                noAutomaticRetry = true,
                failureKind = failure.FailureKind.ToString(), statusCode = failure.StatusCode });
            throw;
        }
        catch
        {
            if (configuration.RequireStructuredResult == true)
                return await StructuredOutcomeAsync(toolInvocationFailed ? AgentResultStatuses.ToolError : AgentResultStatuses.ExecutionError,
                    toolInvocationFailed ? "An assigned tool invocation failed." : "Agent execution failed.");
            await Record(ExecutionEventKinds.AgentFailed, new { reason = "Agent execution failed." });
            throw;
        }

        ValueTask Record(string kind, object payload) => invocation.RecordEventAsync(kind, JsonSerializer.Serialize(payload));

        async Task<string> StructuredOutcomeAsync(string status, string message)
        {
            await Record(ExecutionEventKinds.AgentOutcome, new { status, message });
            return StructuredResultJson(status, null, message);
        }
    }

    private static string StructuredResultJson(string status, string? payloadJson, string? message = null)
    {
        JsonElement? payload = null;
        if (payloadJson is not null)
        {
            using var document = JsonDocument.Parse(payloadJson, new JsonDocumentOptions { MaxDepth = 16 });
            payload = document.RootElement.Clone();
        }
        return JsonSerializer.Serialize(new { status, payload, message });
    }

    private static bool HasDuplicateProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
                if (!names.Add(property.Name) || HasDuplicateProperties(property.Value)) return true;
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
                if (HasDuplicateProperties(item)) return true;
        }
        return false;
    }

    private sealed record AgentStructuredSubmission(string Status, string PayloadJson);

    private sealed class InvocationDeadline : IDisposable
    {
        private static readonly TimeSpan MaximumTimerWindow = TimeSpan.FromDays(20);
        private readonly CancellationTokenSource source;
        private readonly Stopwatch elapsed = Stopwatch.StartNew();
        private readonly Timer timer;
        private readonly object gate = new();
        private TimeSpan timeout;
        private bool started;
        private bool disposed;

        public InvocationDeadline(CancellationToken cancellationToken)
        {
            source = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timer = new Timer(OnTimer, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        }

        public CancellationToken Token => source.Token;

        public void Start(TimeSpan timeout)
        {
            if (timeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout));
            lock (gate)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                if (started) throw new InvalidOperationException("The invocation deadline has already started.");
                this.timeout = timeout;
                started = true;
                timer.Change(NextWindow(timeout), Timeout.InfiniteTimeSpan);
            }
        }

        private void OnTimer(object? state)
        {
            var cancel = false;
            lock (gate)
            {
                if (disposed || !started) return;
                var remaining = timeout - elapsed.Elapsed;
                if (remaining <= TimeSpan.Zero)
                {
                    timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
                    cancel = true;
                }
                else
                {
                    timer.Change(NextWindow(remaining), Timeout.InfiniteTimeSpan);
                }
            }
            if (cancel)
            {
                try { source.Cancel(); }
                catch (ObjectDisposedException) { }
            }
        }

        private static TimeSpan NextWindow(TimeSpan remaining) => remaining < MaximumTimerWindow ? remaining : MaximumTimerWindow;

        public void Dispose()
        {
            lock (gate)
            {
                if (disposed) return;
                disposed = true;
                timer.Dispose();
            }
            source.Dispose();
        }
    }

    private sealed class AgentTokenBudget(int maximum)
    {
        private long reportedTokens;
        private int usageUnavailable;

        public int Maximum { get; } = maximum;
        public long ReportedTokens => Volatile.Read(ref reportedTokens);
        public long Remaining => Volatile.Read(ref usageUnavailable) == 0 ? Math.Max(0, Maximum - ReportedTokens) : 0;
        public bool UsageUnavailable => Volatile.Read(ref usageUnavailable) != 0;

        public void MarkUsageUnavailable() => Interlocked.Exchange(ref usageUnavailable, 1);

        public void Record(long usage)
        {
            var total = Interlocked.Add(ref reportedTokens, Math.Max(0, usage));
            if (total > Maximum) throw new AgentTokenLimitException(Maximum, total);
        }
    }

    private sealed class RecordingClient(IChatClient inner, NodeInvocationContext invocation, int maximumTurns, AgentTokenBudget? tokenBudget,
        Func<ChatResponse, long?>? cacheWriteTokenCount,
        Action<ChatResponse>? responseObserved = null) : DelegatingChatClient(inner)
    {
        private int turns;

        public override async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            var turn = Interlocked.Increment(ref turns);
            if (turn > maximumTurns) throw new AgentTurnLimitException();
            if (tokenBudget?.UsageUnavailable == true) throw new AgentTokenUsageUnavailableException();
            var effectiveOptions = options?.Clone();
            if (tokenBudget is { } requestBudget && effectiveOptions?.MaxOutputTokens is { } requestedOutputTokens)
            {
                var remainingTokens = requestBudget.Remaining;
                if (remainingTokens <= 0) throw new AgentTokenLimitException(requestBudget.Maximum, requestBudget.ReportedTokens);
                effectiveOptions.MaxOutputTokens = Math.Min(requestedOutputTokens, (int)remainingTokens);
            }
            var snapshot = messages.ToArray();
            var modelCallId = Guid.NewGuid();
            await Record(ExecutionEventKinds.ModelRequested, new
            {
                modelCallId,
                instructions = effectiveOptions?.Instructions,
                reasoningEffort = effectiveOptions?.Reasoning?.Effort?.ToString(),
                maxOutputTokens = effectiveOptions?.MaxOutputTokens,
                messages = Project(snapshot),
                tools = effectiveOptions?.Tools?.Select(t => t.Name).ToArray(),
                toolDeclarations = effectiveOptions?.Tools?.OfType<AIFunctionDeclaration>()
                    .Select(tool => new { tool.Name, tool.Description, inputSchema = tool.JsonSchema }).ToArray()
            });
            using var usageCapture = ProviderUsageCapture.Begin(modelCallId);
            var response = await InnerClient.GetResponseAsync(snapshot, effectiveOptions, cancellationToken).WaitAsync(cancellationToken);
            var cacheReadTokens = response.Usage?.CachedInputTokenCount;
            var cacheWriteTokens = cacheWriteTokenCount?.Invoke(response) is { } reportedCacheWrite && reportedCacheWrite >= 0
                ? reportedCacheWrite
                : response.Usage?.AdditionalCounts?.GetValueOrDefault("cache_write_input_tokens");
            await Record(ExecutionEventKinds.ModelResponded, new
            {
                modelCallId,
                response.ModelId,
                response.ResponseId,
                finishReason = response.FinishReason?.ToString(),
                usage = response.Usage is { } recordedUsage ? (object)new
                {
                    recordedUsage.InputTokenCount,
                    recordedUsage.OutputTokenCount,
                    recordedUsage.TotalTokenCount,
                    recordedUsage.CachedInputTokenCount,
                    recordedUsage.ReasoningTokenCount,
                    cacheWriteInputTokenCount = cacheWriteTokens
                } : cacheWriteTokens is { } explicitCacheWrite ? new { cacheWriteInputTokenCount = (long?)explicitCacheWrite } : null,
                messages = Project(response.Messages)
            });
            responseObserved?.Invoke(response);
            if (tokenBudget is { } responseBudget)
            {
                var usageDetails = response.Usage;
                if (usageDetails is null)
                {
                    responseBudget.MarkUsageUnavailable();
                    throw new AgentTokenUsageUnavailableException();
                }
                long reported = usageDetails.TotalTokenCount ?? 0;
                if (reported <= 0) reported = Math.Max(0L, usageDetails.InputTokenCount ?? 0) + Math.Max(0L, usageDetails.OutputTokenCount ?? 0);
                responseBudget.Record(reported);
            }
            return response;
        }

        private ValueTask Record(string kind, object payload)
        {
            var json = JsonSerializer.Serialize(payload);
            if (json.Length > 262_144) throw new InvalidOperationException("Model trace exceeds the local capture budget.");
            return invocation.RecordEventAsync(kind, json);
        }

        private static object[] Project(IEnumerable<ChatMessage> messages) => messages.Select(message => (object)new
        {
            role = message.Role.ToString(),
            content = message.Contents.Select(content => content switch
            {
                TextContent text => (object)new { kind = "text", text = text.Text },
                FunctionCallContent call => new { kind = "function-call", call.CallId, call.Name, call.Arguments },
                FunctionResultContent result => new { kind = "function-result", result.CallId, result = result.Result?.ToString() },
                _ => new { kind = "unsupported-content" }
            }).ToArray()
        }).ToArray();
    }

    private sealed class AgentTurnLimitException() : InvalidOperationException("The configured agent turn limit was reached.");
    private sealed class AgentTokenLimitException(int maximumTokens, long observedTokens)
        : InvalidOperationException("The configured aggregate token limit was reached.")
    {
        public int MaximumTokens { get; } = maximumTokens;
        public long ObservedTokens { get; } = observedTokens;
    }
    private sealed class AgentTokenUsageUnavailableException()
        : InvalidOperationException("The provider did not report token usage required by the configured aggregate budget.");

    private sealed class BoundMcpFunction(string name, string description, JsonElement schema,
        Func<AIFunctionArguments, CancellationToken, ValueTask<string>> invoke) : AIFunction
    {
        public override string Name => name;
        public override string Description => description;
        public override JsonElement JsonSchema => schema;

        protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken) =>
            await invoke(arguments, cancellationToken);
    }
}
