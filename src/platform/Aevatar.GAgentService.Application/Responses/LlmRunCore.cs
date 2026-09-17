using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.Core.AgentProfiles;
using Aevatar.AI.ToolProviders.ToolSetRegistry;
using Aevatar.ChatRouting.Abstractions;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Responses;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging;

namespace Aevatar.GAgentService.Application.Responses;

public sealed class LlmRunCore(
    ILLMProviderFactory providerFactory,
    IEnumerable<IResponsesToolProvider> toolProviders,
    IToolSetRegistry toolSetRegistry,
    ILogger<LlmRunCore> logger,
    IAgentToolExecutionPort? toolExecutionPort = null) : ILlmRunCore
{
    private static readonly Duration DefaultTtl = Duration.FromTimeSpan(TimeSpan.FromHours(24));
    private const int MaxToolRounds = 8;

    public async Task RunAsync(
        LlmRunCoreRequest request,
        ILlmRunSink sink,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(sink);
        ArgumentNullException.ThrowIfNull(request.Command);

        try
        {
            await RunLlmLoopAsync(request, sink, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _ = await sink.RecordRunCancelledAsync(new LlmRunCancelled
            {
                ResponseId = request.Command.ResponseId,
                RunId = request.RunId,
                CancelledAt = Timestamp.FromDateTime(DateTime.UtcNow),
            }, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _ = await sink.RecordRunFailedAsync(new LlmRunFailed
            {
                ResponseId = request.Command.ResponseId,
                RunId = request.RunId,
                FailureCode = MapFailureCode(ex),
                FailureMessage = string.IsNullOrWhiteSpace(ex.Message) ? "LLM run failed." : ex.Message,
                FailedAt = Timestamp.FromDateTime(DateTime.UtcNow),
            }, ct).ConfigureAwait(false);
        }
    }

    private async Task RunLlmLoopAsync(
        LlmRunCoreRequest request,
        ILlmRunSink sink,
        CancellationToken ct)
    {
        var command = request.Command;
        var provider = providerFactory.GetDefault();
        var toolContext = BuildToolContext(command) with
        {
            ExecutionOwner = AgentToolExecutionOwners.WorkflowRun(request.RunId),
        };
        var runtimeCatalog = await BuildRuntimeToolCatalogAsync(
            command,
            toolContext,
            request.OriginPlatform,
            ct).ConfigureAwait(false);
        var tools = runtimeCatalog.EffectiveTools;
        var ownershipPlan = LlmToolOwnershipPlan.From(command.ToolSelection, tools);
        var messages = command.Messages.Select(ToChatMessage).ToList();
        var outputText = new System.Text.StringBuilder();
        TokenUsage? usage = null;

        for (var round = 0; round < MaxToolRounds; round++)
        {
            var roundRequest = new LLMRequest
            {
                Messages = [.. messages],
                RequestId = command.ResponseId,
                Metadata = toolContext.ExternalMetadata,
                CallerContext = new LLMRequestCallerContext(
                    toolContext.Caller.ScopeId ?? string.Empty,
                    toolContext.Caller.OwnerSubject ?? string.Empty,
                    toolContext.Caller.ResponseId,
                    new LLMRequestCallerCredentials(toolContext.Credentials.NyxIdAccessToken)),
                Tools = tools,
                ToolCatalogProof = runtimeCatalog.OwnedProof,
                ToolContext = toolContext,
                LlmControl = new LLMControlContext(
                    NyxIdAccessToken: toolContext.Credentials.NyxIdAccessToken,
                    NyxIdOrgToken: toolContext.Credentials.NyxIdOrgToken,
                    SenderNyxIdAccessToken: toolContext.Credentials.SenderNyxIdAccessToken,
                    ModelOverride: toolContext.Routing.ModelOverride,
                    NyxIdRoutePreference: toolContext.Routing.NyxIdRoutePreference,
                    MaxToolRoundsOverride: null,
                    UserMemoryPrompt: toolContext.Routing.UserMemoryPrompt),
                RouteTarget = command.RouteTarget?.Clone(),
                Model = NormalizeOptional(command.Model),
                Temperature = command.HasTemperature ? command.Temperature : null,
                MaxTokens = command.HasMaxTokens ? command.MaxTokens : null,
            };

            var toolCalls = new RuntimeToolCallAccumulator();
            using (AgentToolContextScope.Push(toolContext))
            {
                await foreach (var chunk in provider.ChatStreamAsync(roundRequest, ct).ConfigureAwait(false))
                {
                    var delta = ExtractChunkText(chunk);
                    if (!string.IsNullOrEmpty(delta))
                        outputText.Append(delta);

                    if (chunk.Usage is not null)
                        usage = chunk.Usage;

                    var observed = new LlmStreamChunkObserved
                    {
                        ResponseId = command.ResponseId,
                        RunId = request.RunId,
                        Round = round,
                        DeltaText = delta ?? string.Empty,
                        ToolCallDelta = chunk.DeltaToolCall is null ? null : ToRuntimeToolCall(chunk.DeltaToolCall),
                        Usage = chunk.Usage is null ? null : ToSessionUsage(chunk.Usage),
                        ObservedAt = Timestamp.FromDateTime(DateTime.UtcNow),
                    };
                    if (ShouldStop(await sink.RecordStreamChunkObservedAsync(observed, ct).ConfigureAwait(false)))
                        return;

                    if (chunk.DeltaToolCall != null)
                        toolCalls.TrackDelta(chunk.DeltaToolCall);

                    if (chunk.IsLast)
                        break;
                }
            }

            var builtToolCalls = ApplyToolChoiceHint(toolCalls.BuildToolCalls(), command.ToolSelection);
            var routedToolCalls = ownershipPlan.Route(builtToolCalls);
            var forwardedToolCalls = routedToolCalls.Forwarded;
            if (forwardedToolCalls.Count > 0)
            {
                var completed = new LlmRunCompleted
                {
                    ResponseId = command.ResponseId,
                    RunId = request.RunId,
                    OutputText = outputText.ToString(),
                    ForwardedToolCalls = { forwardedToolCalls.Select(ToRuntimeToolCall) },
                    Usage = usage is null ? null : ToSessionUsage(usage),
                    CompletedAt = Timestamp.FromDateTime(DateTime.UtcNow),
                };
                completed.ForwardedToolCallRecords.AddRange(
                    forwardedToolCalls.Select(call => BuildForwardedToolCall(call, command.ToolSelection)));
                _ = await sink.RecordRunCompletedAsync(completed, ct).ConfigureAwait(false);
                return;
            }

            var localToolCalls = routedToolCalls.Local;
            if (localToolCalls.Count == 0)
            {
                _ = await sink.RecordRunCompletedAsync(new LlmRunCompleted
                {
                    ResponseId = command.ResponseId,
                    RunId = request.RunId,
                    OutputText = outputText.ToString(),
                    Usage = usage is null ? null : ToSessionUsage(usage),
                    CompletedAt = Timestamp.FromDateTime(DateTime.UtcNow),
                }, ct).ConfigureAwait(false);
                return;
            }

            messages.Add(new ChatMessage
            {
                Role = "assistant",
                ToolCalls = localToolCalls,
            });
            if (ShouldStop(await ExecuteLocalToolCallsAsync(
                    command,
                    request.RunId,
                    round,
                    localToolCalls,
                    ownershipPlan,
                    messages,
                    toolContext,
                    sink,
                    ct).ConfigureAwait(false)))
            {
                return;
            }
        }

        _ = await sink.RecordRunFailedAsync(new LlmRunFailed
        {
            ResponseId = command.ResponseId,
            RunId = request.RunId,
            FailureCode = "max_tool_rounds_exhausted",
            FailureMessage = $"LLM run exceeded the maximum of {MaxToolRounds} tool rounds.",
            FailedAt = Timestamp.FromDateTime(DateTime.UtcNow),
        }, ct).ConfigureAwait(false);
    }

    private async Task<RuntimeToolCatalog> BuildRuntimeToolCatalogAsync(
        LlmRunRequested command,
        AgentToolExecutionContext toolContext,
        string? originPlatform,
        CancellationToken ct)
    {
        var selection = command.ToolSelection;
        if (!string.Equals(
                selection?.ToolCatalogPolicyVersion,
                ResponsesOwnedToolCatalogPlanner.PolicyVersion,
                StringComparison.Ordinal))
        {
            var legacy = await BuildLegacyEffectiveToolsAsync(
                command,
                toolContext,
                originPlatform,
                ct).ConfigureAwait(false);
            return new RuntimeToolCatalog(legacy, null);
        }
        if (selection?.OwnedCatalogProof is null)
            throw CatalogProofMismatch("The persisted owned tool catalog proof is missing.");

        var proof = AgentTurnToolCatalogProofPayloadMapper.FromPayload(selection.OwnedCatalogProof);
        ValidatePinnedProfile(selection, proof);
        var context = new ResponsesToolProviderContext(
            toolContext with
            {
                Channel = toolContext.Channel with { Platform = originPlatform },
            });

        var substituteTools = await DiscoverSubstituteToolsStrictAsync(context, ct).ConfigureAwait(false);
        var routeTools = await DiscoverRouteToolsStrictAsync(
            selection.ToolSetName,
            toolContext,
            ct).ConfigureAwait(false);
        var substitutedNames = selection.SubstitutedToolNames.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var ownedNames = selection.OwnedToolNames.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var proofNames = proof.ToolDescriptors
            .Select(static descriptor => descriptor.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!proofNames.SetEquals(ownedNames))
            throw CatalogProofMismatch("Persisted owned tool names do not match the catalog proof.");

        var ownedTools = new List<IAgentTool>(ownedNames.Count);
        foreach (var ownedName in ownedNames.OrderBy(static name => name, StringComparer.OrdinalIgnoreCase))
        {
            var source = substitutedNames.Contains(ownedName) ? substituteTools : routeTools;
            if (!source.TryGetValue(ownedName, out var exact))
            {
                throw CatalogProofMismatch(
                    $"Owned tool '{ownedName}' could not be re-materialized from its pinned source.");
            }

            ownedTools.Add(exact);
        }
        proof.AssertMatchesExactTools(ownedTools);

        var forwardedTools = new List<IAgentTool>();
        var effectiveNames = new HashSet<string>(ownedNames, StringComparer.OrdinalIgnoreCase);
        foreach (var declaration in selection.ForwardedTools)
        {
            if (string.IsNullOrWhiteSpace(declaration.ToolName) ||
                !effectiveNames.Add(declaration.ToolName.Trim()))
            {
                throw CatalogProofMismatch(
                    "Forwarded and owned tool declarations contain a duplicate name.");
            }

            forwardedTools.Add(new ForwardedRuntimeTool(declaration));
        }

        var effective = ownedTools.Concat(forwardedTools).ToArray();
        var forwardedSchemaBytes = selection.ForwardedTools.Sum(static declaration =>
            System.Text.Encoding.UTF8.GetByteCount(ToolDeclarationParametersJson(declaration)));
        AgentTurnToolCatalogTelemetry.RecordForwarded(
            forwardedTools.Count,
            forwardedSchemaBytes,
            "runtime");
        logger.LogInformation(
            "Responses runtime tool catalog verified. policy={PolicyVersion} profile={ProfileId} owned={OwnedCount} ownedSchemaBytes={OwnedSchemaBytes} forwarded={ForwardedCount} forwardedSchemaBytes={ForwardedSchemaBytes} combined={CombinedCount} combinedSchemaBytes={CombinedSchemaBytes} digest={CatalogDigest}",
            selection.ToolCatalogPolicyVersion,
            selection.ProfileSnapshot?.ProfileId ?? string.Empty,
            proof.ToolCount,
            proof.SchemaBytes,
            forwardedTools.Count,
            forwardedSchemaBytes,
            effective.Length,
            proof.SchemaBytes + forwardedSchemaBytes,
            proof.CatalogDigest);
        return new RuntimeToolCatalog(effective, proof);
    }

    private static void ValidatePinnedProfile(
        LlmSessionRuntimeToolSelection selection,
        AgentTurnToolCatalogProof proof)
    {
        var profile = selection.ProfileSnapshot;
        if (profile is null)
        {
            if (proof.ToolCount != 0 || !string.IsNullOrWhiteSpace(selection.ToolSetName))
            {
                throw CatalogProofMismatch(
                    "A non-empty owned catalog requires an immutable pinned profile snapshot.");
            }

            return;
        }
        if (!AgentProfileSnapshotCodec.Verify(profile) ||
            !string.Equals(profile.RouteToolSetRef, selection.ToolSetName, StringComparison.Ordinal))
        {
            throw CatalogProofMismatch(
                "The pinned profile snapshot does not match the persisted route tool set.");
        }

        // The persisted proof carries its own budget, so re-derive the authoritative one from the
        // sealed profile instead of trusting what the payload asserts about itself.
        if (proof.Budget != AgentTurnToolCatalogFactory.ResolveProfileBudget(profile))
        {
            throw CatalogProofMismatch(
                "The persisted catalog budget does not match the sealed profile budget.");
        }
    }

    private async Task<IReadOnlyDictionary<string, IAgentTool>> DiscoverSubstituteToolsStrictAsync(
        ResponsesToolProviderContext context,
        CancellationToken ct)
    {
        var exact = new Dictionary<string, IAgentTool>(StringComparer.OrdinalIgnoreCase);
        foreach (var provider in toolProviders)
        {
            ct.ThrowIfCancellationRequested();
            var tools = await provider.GetSubstituteToolsAsync(context, ct).ConfigureAwait(false);
            AddExactToolsOrThrow(exact, tools, provider.GetType().FullName ?? provider.GetType().Name);
        }

        return exact;
    }

    private async Task<IReadOnlyDictionary<string, IAgentTool>> DiscoverRouteToolsStrictAsync(
        string? toolSetName,
        AgentToolExecutionContext toolContext,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(toolSetName))
            return new Dictionary<string, IAgentTool>(StringComparer.OrdinalIgnoreCase);

        ToolSetResolveResult resolved;
        try
        {
            resolved = toolSetRegistry.Resolve(toolSetName);
        }
        catch (Exception exception)
        {
            throw new ToolSetResolutionException(new ToolSetResolveError(
                ToolSetResolveError.ResolutionFailedCode,
                toolSetName.Trim(),
                $"Run tool set '{toolSetName.Trim()}' could not be resolved.",
                toolSetRegistry.GetRegisteredNames()), exception);
        }
        if (!resolved.IsSuccess)
            throw new ToolSetResolutionException(resolved.Error!);

        var discovery = await AgentToolDiscoveryService.Instance
            .DiscoverAsync(resolved.Sources, toolContext, ct)
            .ConfigureAwait(false);
        if (!discovery.IsSuccess)
            throw new AgentToolDiscoveryException(discovery.Failure!);
        return discovery.Tools.ToDictionary(
            static tool => tool.Name,
            StringComparer.OrdinalIgnoreCase);
    }

    private static void AddExactToolsOrThrow(
        Dictionary<string, IAgentTool> exact,
        IReadOnlyList<IAgentTool> tools,
        string providerType)
    {
        foreach (var tool in tools)
        {
            if (tool is null || string.IsNullOrWhiteSpace(tool.Name))
            {
                throw new AgentToolDiscoveryException(new AgentToolDiscoveryFailure(
                    AgentToolDiscoveryFailureCode.InvalidToolName,
                    string.Empty,
                    providerType,
                    string.Empty,
                    $"Responses provider '{providerType}' returned an empty tool name."));
            }

            var name = tool.Name.Trim();
            if (!exact.TryGetValue(name, out var existing))
            {
                exact.Add(name, tool);
                continue;
            }
            if (ReferenceEquals(existing, tool))
                continue;

            throw new AgentToolDiscoveryException(new AgentToolDiscoveryFailure(
                AgentToolDiscoveryFailureCode.ToolNameCollision,
                name,
                existing.GetType().FullName ?? existing.GetType().Name,
                providerType,
                $"Responses substitute tool '{name}' resolved to different exact objects."));
        }
    }

    private static AgentTurnToolCatalogException CatalogProofMismatch(string detail) =>
        new(new AgentTurnToolCatalogFailure(
            AgentTurnToolCatalogFailureCode.CatalogProofMismatch,
            detail));

    private IEnumerable<IResponsesToolProvider> ResolveRunToolProviders(string? toolSetName)
    {
        if (string.IsNullOrWhiteSpace(toolSetName))
            return toolProviders;

        ToolSetResolveResult resolved;
        try
        {
            resolved = toolSetRegistry.Resolve(toolSetName);
        }
        catch (Exception ex)
        {
            throw new ToolSetResolutionException(new ToolSetResolveError(
                ToolSetResolveError.ResolutionFailedCode,
                toolSetName.Trim(),
                $"Run tool set '{toolSetName.Trim()}' could not be resolved.",
                toolSetRegistry.GetRegisteredNames()), ex);
        }

        if (!resolved.IsSuccess)
            throw new ToolSetResolutionException(resolved.Error!);

        return toolProviders.Append(new ToolSetResponsesToolProvider(resolved.Sources, logger));
    }

    private async Task<IReadOnlyList<IAgentTool>> BuildLegacyEffectiveToolsAsync(
        LlmRunRequested command,
        AgentToolExecutionContext toolContext,
        string? originPlatform,
        CancellationToken ct)
    {
        var context = new ResponsesToolProviderContext(
            toolContext with
            {
                Channel = toolContext.Channel with
                {
                    Platform = originPlatform,
                },
            });

        // The DI-injected providers are only the always-on ones (Aevatar substitute + user skills).
        // The route-selected tool set (e.g. workspace.default, carrying nyxid_*/invoke_*/lark_* ...)
        // was a per-request transient provider on the facade; its live tool objects can't cross the
        // command boundary, so the command persists only the tool-set *name* (tool_set_name) plus the
        // selected tool names. Re-resolve that tool set here from the same registry so the run
        // materializes the same sources the facade classified against; otherwise every route-tool-set
        // tool is silently dropped before the model call.
        var providers = ResolveRunToolProviders(command.ToolSelection?.ToolSetName);

        var substituteTools = new List<IAgentTool>();
        var additiveTools = new List<IAgentTool>();
        foreach (var provider in providers)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                substituteTools.AddRange(await provider.GetSubstituteToolsAsync(context, ct).ConfigureAwait(false));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogWarning(
                    ex,
                    "Responses substitute tool discovery failed for provider {ProviderType}; continuing without that provider.",
                    provider.GetType().Name);
            }

            try
            {
                additiveTools.AddRange(await provider.GetAdditiveToolsAsync(context, ct).ConfigureAwait(false));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogWarning(
                    ex,
                    "Responses additive tool discovery failed for provider {ProviderType}; continuing without that provider.",
                    provider.GetType().Name);
            }
        }

        var substitutedNames = (command.ToolSelection?.SubstitutedToolNames ?? [])
            .ToHashSet(StringComparer.Ordinal);
        var additiveNames = (command.ToolSelection?.AdditiveToolNames ?? [])
            .ToHashSet(StringComparer.Ordinal);
        var ownedToolNames = BuildOwnedToolNameSet(command.ToolSelection);
        var substitutesByName = substituteTools
            .GroupBy(static tool => tool.Name, StringComparer.Ordinal)
            .ToDictionary(static group => group.Key, static group => group.First(), StringComparer.Ordinal);
        var effective = new List<IAgentTool>();
        foreach (var declaration in command.ToolSelection?.ForwardedTools ?? [])
        {
            if (ownedToolNames.Contains(declaration.ToolName))
            {
                if (substitutedNames.Contains(declaration.ToolName) &&
                    substitutesByName.TryGetValue(declaration.ToolName, out var substitute))
                {
                    effective.Add(substitute);
                }
            }
            else
            {
                effective.Add(new ForwardedRuntimeTool(declaration));
            }
        }

        var effectiveNames = new HashSet<string>(effective.Select(static tool => tool.Name), StringComparer.Ordinal);
        foreach (var substitutedName in substitutedNames)
        {
            if (effectiveNames.Contains(substitutedName))
                continue;
            if (substitutesByName.TryGetValue(substitutedName, out var substitute) &&
                effectiveNames.Add(substitute.Name))
            {
                effective.Add(substitute);
            }
        }

        foreach (var additive in additiveTools)
        {
            if (additiveNames.Contains(additive.Name) && effectiveNames.Add(additive.Name))
                effective.Add(additive);
        }

        return effective;
    }

    private async Task<LlmRunRecordDecision> ExecuteLocalToolCallsAsync(
        LlmRunRequested command,
        string runId,
        int round,
        IReadOnlyList<ToolCall> localToolCalls,
        LlmToolOwnershipPlan ownershipPlan,
        List<ChatMessage> messages,
        AgentToolExecutionContext toolContext,
        ILlmRunSink sink,
        CancellationToken ct)
    {
        using (AgentToolContextScope.Push(toolContext))
        {
            foreach (var toolCall in localToolCalls)
            {
                using var _ = AgentToolContextScope.Push(toolContext.WithCallId(toolCall.Id));
                var argumentsJson = string.IsNullOrWhiteSpace(toolCall.ArgumentsJson)
                    ? "{}"
                    : toolCall.ArgumentsJson;
                string result;
                if (ownershipPlan.TryGetExecutableTool(toolCall.Name, out var tool))
                {
                    var executionPort = toolExecutionPort
                        ?? throw new InvalidOperationException(
                            "IAgentToolExecutionPort is required for server-owned Responses tools.");
                    var outcome = await executionPort.ExecuteAsync(
                        new AgentToolExecutionRequest(
                            tool,
                            argumentsJson,
                            toolContext.WithCallId(toolCall.Id),
                            AgentToolApprovalContinuationMode.None,
                            null),
                        ct).ConfigureAwait(false);
                    result = outcome.ResultJson;
                }
                else
                {
                    result = BuildLocalToolUnavailableResult(
                        ownershipPlan.ResolveUnavailableToolCode(toolCall.Name),
                        toolCall.Name);
                }

                var decision = await sink.RecordToolCallObservedAsync(new LlmToolCallObserved
                {
                    ResponseId = command.ResponseId,
                    RunId = runId,
                    Round = round,
                    ToolCall = ToRuntimeToolCall(toolCall),
                    Forwarded = false,
                    LocalResultJson = result,
                    LocalResult = ResponsesJsonValues.ParseBoundaryPayload(result),
                    ObservedAt = Timestamp.FromDateTime(DateTime.UtcNow),
                }, ct).ConfigureAwait(false);
                if (ShouldStop(decision))
                    return decision;
                messages.Add(ChatMessage.Tool(toolCall.Id, result));
            }
        }

        return LlmRunRecordDecision.Continue;
    }

    private static bool ShouldStop(LlmRunRecordDecision decision) =>
        !decision.Accepted || decision.StopDispatching;

    private static AgentToolExecutionContext BuildToolContext(LlmRunRequested command)
    {
        if (command.ToolContext is not null)
            return AgentToolExecutionContextMapper.FromPayload(command.ToolContext);

        return new(
            new AgentToolRequestIdentity(command.ResponseId, null),
            new AgentToolCredentials(command.BearerToken, null, null),
            new AgentToolCallerContext(command.ScopeId, command.OwnerSubject, command.ResponseId),
            AgentToolChannelContext.Empty,
            AgentToolSenderBindingContext.Empty,
            new LLMRequestRoutingContext(null, NormalizeOptional(command.RoutePreference), null, null),
            AgentToolConnectedServicesContext.Empty,
            AgentSkillRecoveryContext.Empty,
            new Dictionary<string, string>(StringComparer.Ordinal));
    }

    private static ChatMessage ToChatMessage(LlmSessionRuntimeChatMessage message) =>
        new()
        {
            Role = string.IsNullOrWhiteSpace(message.Role) ? "user" : message.Role,
            Content = message.Content,
            ReasoningContent = NormalizeOptional(message.ReasoningContent),
            ToolCallId = NormalizeOptional(message.ToolCallId),
            ToolCalls = message.ToolCalls.Count == 0
                ? null
                : message.ToolCalls.Select(ToToolCall).ToArray(),
        };

    private static ToolCall ToToolCall(LlmSessionRuntimeToolCall call) =>
        new()
        {
            Id = call.CallId,
            Name = call.ToolName,
            ArgumentsJson = RuntimeToolArgumentsJson(call),
        };

    private static LlmSessionRuntimeToolCall ToRuntimeToolCall(ToolCall call) =>
        new()
        {
            CallId = call.Id,
            ToolName = call.Name,
            ArgumentsJson = call.ArgumentsJson,
            Arguments = ParseStruct(call.ArgumentsJson),
        };

    private static LlmSessionTokenUsage ToSessionUsage(TokenUsage usage) =>
        new()
        {
            PromptTokens = usage.PromptTokens,
            CompletionTokens = usage.CompletionTokens,
            TotalTokens = usage.TotalTokens,
        };

    private static string? ExtractChunkText(LLMStreamChunk chunk)
    {
        // Use IsNullOrEmpty (not IsNullOrWhiteSpace): whitespace-only deltas carry
        // newlines and indentation that must survive for code/YAML output to stay intact.
        if (!string.IsNullOrEmpty(chunk.DeltaContent))
            return chunk.DeltaContent;
        if (chunk.DeltaContentPart is { Kind: ContentPartKind.Text } part && !string.IsNullOrEmpty(part.Text))
            return part.Text;
        return null;
    }

    private static IReadOnlyList<ToolCall> ApplyToolChoiceHint(
        IReadOnlyList<ToolCall> toolCalls,
        LlmSessionRuntimeToolSelection? selection)
    {
        if (toolCalls.Count == 0 ||
            selection is null ||
            string.IsNullOrWhiteSpace(selection.ToolChoiceHintName))
        {
            return toolCalls;
        }

        return toolCalls
            .Select(call => ApplyToolChoiceHint(call, selection))
            .ToArray();
    }

    private static ToolCall ApplyToolChoiceHint(
        ToolCall call,
        LlmSessionRuntimeToolSelection selection)
    {
        var toolName = selection.ToolChoiceHintName.Trim();
        if (!string.Equals(call.Name, toolName, StringComparison.Ordinal))
        {
            return CloneWithArguments(
                call,
                BuildStructuredToolChoiceError(
                    "tool_choice_hint_mismatch",
                    $"Tool choice hint requires '{toolName}', but the model called '{call.Name}'.",
                    "tool_name"));
        }

        JsonObject prefilled;
        try
        {
            prefilled = ParseJsonObject(ToolChoiceHintArgumentsJson(selection));
        }
        catch (JsonException ex)
        {
            return CloneWithArguments(
                call,
                BuildStructuredToolChoiceError(
                    "invalid_tool_choice_prefill",
                    $"Tool choice prefilled arguments must be a JSON object: {ex.Message}",
                    "tool_choice_hint_arguments_json"));
        }

        JsonObject modelArguments;
        try
        {
            modelArguments = ParseJsonObject(call.ArgumentsJson);
        }
        catch (JsonException ex)
        {
            return CloneWithArguments(
                call,
                BuildStructuredToolChoiceError(
                    "invalid_tool_arguments",
                    $"Arguments must be a JSON object: {ex.Message}",
                    "arguments"));
        }

        var merged = new JsonObject();
        foreach (var (key, value) in prefilled)
            merged[key] = value?.DeepClone();

        foreach (var (key, value) in modelArguments)
        {
            if (prefilled.TryGetPropertyValue(key, out var prefilledValue) &&
                !JsonNode.DeepEquals(prefilledValue, value))
            {
                return CloneWithArguments(
                    call,
                    BuildStructuredToolChoiceError(
                        "tool_choice_prefill_conflict",
                        $"Tool argument '{key}' conflicts with server-trusted prefilled_arguments.",
                        key));
            }

            if (!prefilled.ContainsKey(key))
                merged[key] = value?.DeepClone();
        }

        return CloneWithArguments(
            call,
            merged.ToJsonString(new JsonSerializerOptions { WriteIndented = false }));
    }

    private static JsonObject ParseJsonObject(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return new JsonObject();

        return JsonNode.Parse(json) as JsonObject
               ?? throw new JsonException("Root value must be an object.");
    }

    private static Struct ParseStruct(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return new Struct();

        try
        {
            return JsonParser.Default.Parse<Struct>(json);
        }
        catch
        {
            return new Struct();
        }
    }

    private static string RuntimeToolArgumentsJson(LlmSessionRuntimeToolCall call)
    {
        if (call.Arguments is { Fields.Count: > 0 })
            return JsonFormatter.Default.Format(call.Arguments);
        return string.IsNullOrWhiteSpace(call.ArgumentsJson) ? "{}" : call.ArgumentsJson;
    }

    private static Value ToolCallArgumentsValue(ToolCall call)
    {
        var arguments = ParseStruct(call.ArgumentsJson);
        if (arguments.Fields.Count > 0)
            return Value.ForStruct(arguments);
        return ResponsesJsonValues.ParseBoundaryPayload(
            string.IsNullOrWhiteSpace(call.ArgumentsJson) ? "{}" : call.ArgumentsJson);
    }

    private static string ToolChoiceHintArgumentsJson(LlmSessionRuntimeToolSelection selection)
    {
        if (selection.ToolChoiceHintArguments is { Fields.Count: > 0 })
            return JsonFormatter.Default.Format(selection.ToolChoiceHintArguments);
        return selection.ToolChoiceHintArgumentsJson;
    }

    private static string ToolDeclarationParametersJson(LlmSessionRuntimeToolDeclaration declaration)
    {
        if (declaration.Parameters is { Fields.Count: > 0 })
            return JsonFormatter.Default.Format(declaration.Parameters);
        return declaration.ParametersJson;
    }

    private static ToolCall CloneWithArguments(ToolCall call, string argumentsJson) =>
        new()
        {
            Id = call.Id,
            Name = call.Name,
            ArgumentsJson = argumentsJson,
        };

    private static string BuildStructuredToolChoiceError(
        string code,
        string message,
        string field) =>
        JsonSerializer.Serialize(new
        {
            error = new
            {
                code,
                message,
                field,
            },
        });

    private static HashSet<string> BuildOwnedToolNameSet(LlmSessionRuntimeToolSelection? selection)
    {
        if (selection is null)
            return new HashSet<string>(StringComparer.Ordinal);

        var ownedNames = selection.OwnedToolNames.Count > 0
            ? selection.OwnedToolNames
            : selection.SubstitutedToolNames.Concat(selection.AdditiveToolNames);
        return ownedNames.ToHashSet(StringComparer.Ordinal);
    }

    private static LlmSessionForwardedToolCall BuildForwardedToolCall(
        ToolCall toolCall,
        LlmSessionRuntimeToolSelection? selection)
    {
        var declaration = selection?.ForwardedTools
            .FirstOrDefault(tool => string.Equals(tool.ToolName, toolCall.Name, StringComparison.Ordinal));
        return new LlmSessionForwardedToolCall
        {
            CallId = toolCall.Id,
            ToolName = toolCall.Name,
            SchemaHash = declaration?.SchemaHash ?? string.Empty,
            Arguments = ToolCallArgumentsValue(toolCall),
            Status = LlmSessionForwardedToolCallStatus.Pending,
            EmittedAt = Timestamp.FromDateTime(DateTime.UtcNow),
            Expiry = Timestamp.FromDateTime(DateTime.UtcNow.Add(DefaultTtl.ToTimeSpan())),
        };
    }

    private static string BuildLocalToolUnavailableResult(
        string code,
        string toolName) =>
        JsonSerializer.Serialize(new
        {
            error = code,
            tool_name = toolName,
        });

    private static string MapFailureCode(Exception ex) =>
        ex switch
        {
            NyxIdAuthenticationRequiredException => "authentication_required",
            NyxIdUpstreamException upstream => upstream.Kind.ToString().ToLowerInvariant(),
            ToolSetResolutionException resolution => resolution.Error.Code,
            AgentToolDiscoveryException discovery => discovery.Failure.Code switch
            {
                AgentToolDiscoveryFailureCode.ToolNameCollision => "tool_name_collision",
                AgentToolDiscoveryFailureCode.InvalidToolName => "invalid_tool_name",
                _ => "tool_discovery_failed",
            },
            AgentTurnToolCatalogException catalog => catalog.Failure.Code switch
            {
                AgentTurnToolCatalogFailureCode.CatalogProofMismatch => "tool_catalog_proof_mismatch",
                AgentTurnToolCatalogFailureCode.CatalogOverBudget => "tool_catalog_over_budget",
                AgentTurnToolCatalogFailureCode.ToolNameCollision => "tool_name_collision",
                AgentTurnToolCatalogFailureCode.SchemaInvalid => "tool_schema_invalid",
                _ => "tool_catalog_invalid",
            },
            _ => "execution_failed",
        };

    private static string? NormalizeOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private sealed record RuntimeToolCatalog(
        IReadOnlyList<IAgentTool> EffectiveTools,
        AgentTurnToolCatalogProof? OwnedProof);

    private sealed class ForwardedRuntimeTool : IAgentTool
    {
        public ForwardedRuntimeTool(LlmSessionRuntimeToolDeclaration declaration)
        {
            ArgumentNullException.ThrowIfNull(declaration);
            Name = declaration.ToolName;
            Description = declaration.Description;
            ParametersSchema = ToolDeclarationParametersJson(declaration);
        }

        public string Name { get; }

        public string Description { get; }

        public string ParametersSchema { get; }

        public bool IsReadOnly => true;

        public Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default) =>
            throw new InvalidOperationException(
                $"Forwarded Responses tool '{Name}' must be executed by the client, not by Aevatar.");
    }

    private sealed class LlmToolOwnershipPlan
    {
        private readonly IReadOnlyDictionary<string, IAgentTool> _executableTools;
        private readonly IReadOnlySet<string> _ownedToolNames;
        private readonly IReadOnlySet<string> _forwardedToolNames;

        private LlmToolOwnershipPlan(
            IReadOnlyDictionary<string, IAgentTool> executableTools,
            IReadOnlySet<string> ownedToolNames,
            IReadOnlySet<string> forwardedToolNames)
        {
            _executableTools = executableTools;
            _ownedToolNames = ownedToolNames;
            _forwardedToolNames = forwardedToolNames;
        }

        public static LlmToolOwnershipPlan From(
            LlmSessionRuntimeToolSelection? selection,
            IReadOnlyList<IAgentTool> effectiveTools)
        {
            var ownedToolNames = BuildOwnedToolNameSet(selection);
            var forwardedToolNames = (selection?.ForwardedTools ?? [])
                .Select(static tool => tool.ToolName)
                .Where(name => !ownedToolNames.Contains(name))
                .ToHashSet(StringComparer.Ordinal);
            var executableTools = effectiveTools
                .Where(static tool => tool is not ForwardedRuntimeTool)
                .GroupBy(static tool => tool.Name, StringComparer.Ordinal)
                .ToDictionary(static group => group.Key, static group => group.First(), StringComparer.Ordinal);

            return new LlmToolOwnershipPlan(executableTools, ownedToolNames, forwardedToolNames);
        }

        public RoutedToolCalls Route(IReadOnlyList<ToolCall> toolCalls)
        {
            if (toolCalls.Count == 0)
                return RoutedToolCalls.Empty;

            var forwarded = new List<ToolCall>();
            var local = new List<ToolCall>();
            foreach (var toolCall in toolCalls)
            {
                if (_forwardedToolNames.Contains(toolCall.Name))
                {
                    forwarded.Add(toolCall);
                    continue;
                }

                local.Add(toolCall);
            }

            return new RoutedToolCalls(local, forwarded);
        }

        public bool TryGetExecutableTool(
            string toolName,
            out IAgentTool tool) =>
            _executableTools.TryGetValue(toolName, out tool!);

        public string ResolveUnavailableToolCode(string toolName) =>
            _ownedToolNames.Contains(toolName)
                ? "tool_not_available"
                : "tool_not_declared";
    }

    private sealed record RoutedToolCalls(
        IReadOnlyList<ToolCall> Local,
        IReadOnlyList<ToolCall> Forwarded)
    {
        public static RoutedToolCalls Empty { get; } = new([], []);
    }

    private sealed class RuntimeToolCallAccumulator
    {
        private readonly Dictionary<string, ToolCallAggregate> _aggregates = new(StringComparer.Ordinal);
        private readonly List<string> _order = [];
        private int _anonymousCounter;
        private string? _activeAnonymousKey;

        public void TrackDelta(ToolCall delta)
        {
            ArgumentNullException.ThrowIfNull(delta);
            var aggregate = ResolveAggregate(delta);
            if (!string.IsNullOrWhiteSpace(delta.Name))
                aggregate.Name = delta.Name;
            if (!string.IsNullOrEmpty(delta.ArgumentsJson))
                aggregate.Arguments.Append(delta.ArgumentsJson);
        }

        public IReadOnlyList<ToolCall> BuildToolCalls()
        {
            var result = new List<ToolCall>(_order.Count);
            foreach (var key in _order)
            {
                var aggregate = _aggregates[key];
                result.Add(new ToolCall
                {
                    Id = aggregate.Id,
                    Name = aggregate.Name ?? string.Empty,
                    ArgumentsJson = aggregate.Arguments.ToString(),
                });
            }

            return result;
        }

        private ToolCallAggregate ResolveAggregate(ToolCall delta)
        {
            if (!string.IsNullOrWhiteSpace(delta.Id))
                return ResolveKnownIdAggregate(delta.Id);

            return ResolveAnonymousAggregate();
        }

        private ToolCallAggregate ResolveKnownIdAggregate(string id)
        {
            var knownKey = $"id:{id}";
            if (TryPromoteActiveAnonymousAggregate(knownKey, id, out var promoted))
            {
                _activeAnonymousKey = null;
                return promoted;
            }

            _activeAnonymousKey = null;
            if (!_aggregates.TryGetValue(knownKey, out var aggregate))
            {
                aggregate = new ToolCallAggregate(id);
                _aggregates[knownKey] = aggregate;
                _order.Add(knownKey);
            }

            return aggregate;
        }

        private ToolCallAggregate ResolveAnonymousAggregate()
        {
            if (!string.IsNullOrWhiteSpace(_activeAnonymousKey))
                return _aggregates[_activeAnonymousKey];

            _anonymousCounter++;
            var anonymousKey = $"anon:{_anonymousCounter}";
            var aggregate = new ToolCallAggregate($"stream-tool-call-{_anonymousCounter}");
            _aggregates[anonymousKey] = aggregate;
            _order.Add(anonymousKey);
            _activeAnonymousKey = anonymousKey;
            return aggregate;
        }

        private bool TryPromoteActiveAnonymousAggregate(
            string knownKey,
            string knownId,
            out ToolCallAggregate aggregate)
        {
            aggregate = default!;
            if (string.IsNullOrWhiteSpace(_activeAnonymousKey))
                return false;
            var anonymousAggregate = _aggregates[_activeAnonymousKey];
            if (_aggregates.ContainsKey(knownKey))
                return false;
            anonymousAggregate.Id = knownId;
            _aggregates.Remove(_activeAnonymousKey);
            _aggregates[knownKey] = anonymousAggregate;
            _order[_order.IndexOf(_activeAnonymousKey)] = knownKey;
            aggregate = anonymousAggregate;
            return true;
        }

        private sealed class ToolCallAggregate(string id)
        {
            public string Id { get; set; } = id;

            public string? Name { get; set; }

            public System.Text.StringBuilder Arguments { get; } = new();
        }
    }
}
