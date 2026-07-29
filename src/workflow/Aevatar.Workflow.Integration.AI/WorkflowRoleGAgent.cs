using System.Text;
using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.Middleware;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.Core;
using Aevatar.AI.Core.AgentProfiles;
using Aevatar.AI.Core.Hooks;
using Aevatar.AI.ToolProviders.ToolSetRegistry;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Attributes;
using Aevatar.Foundation.Abstractions.TypeSystem;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Abstractions.Credentials;
using Aevatar.Workflow.Core.Primitives;
using Microsoft.Extensions.Logging;

namespace Aevatar.Workflow.Integration.AI;

[GAgent(WorkflowRoleConventions.DefaultAgentKind)]
public class WorkflowRoleGAgent(
    ILLMProviderFactory? llmProviderFactory = null,
    IEnumerable<IAIGAgentExecutionHook>? additionalHooks = null,
    IEnumerable<IAgentRunMiddleware>? agentMiddlewares = null,
    IEnumerable<IToolCallMiddleware>? toolMiddlewares = null,
    IEnumerable<ILLMCallMiddleware>? llmMiddlewares = null,
    IEnumerable<IAgentToolSource>? toolSources = null,
    IToolApprovalHandler? approvalHandler = null,
    IRemoteToolApprovalPort? remoteToolApprovalPort = null,
    IToolSetRegistry? toolSetRegistry = null,
    IWorkflowCallerAccessTokenProvider? callerAccessTokenProvider = null)
    : RoleGAgent(
        llmProviderFactory,
        additionalHooks,
        agentMiddlewares,
        toolMiddlewares,
        llmMiddlewares,
        toolSources,
        approvalHandler,
        remoteToolApprovalPort)
{
    public const string WorkflowAssistantRoleAgentKind = "workflow.assistant-role";
    private const string LegacyConnectorHttpAuthorizationBlockedKey = "connector.http.authorization";
    private readonly IToolSetRegistry? _toolSetRegistry = toolSetRegistry;
    private readonly IWorkflowCallerAccessTokenProvider? _callerAccessTokenProvider = callerAccessTokenProvider;

    [EventHandler(AllowSelfHandling = true)]
    public Task HandleWorkflowRoleInitialize(WorkflowRoleInitializeEvent evt)
    {
        ArgumentNullException.ThrowIfNull(evt);
        var initialize = new InitializeRoleAgentEvent
        {
            RoleId = evt.RoleId ?? string.Empty,
            RoleName = evt.RoleName ?? string.Empty,
            ProviderName = evt.ProviderName ?? string.Empty,
            Model = evt.Model ?? string.Empty,
            SystemPrompt = evt.SystemPrompt ?? string.Empty,
            MaxTokens = evt.MaxTokens,
            MaxToolRounds = evt.MaxToolRounds,
            MaxHistoryMessages = evt.MaxHistoryMessages,
            EventModules = evt.EventModules ?? string.Empty,
            EventRoutes = evt.EventRoutes ?? string.Empty,
        };
        if (evt.HasTemperature)
            initialize.Temperature = evt.Temperature;

        return HandleInitializeRoleAgent(initialize);
    }

    [EventHandler(AllowSelfHandling = true)]
    public async Task HandleWorkflowLlmExecutionIntent(WorkflowLlmExecutionIntent intent)
    {
        ArgumentNullException.ThrowIfNull(intent);
        await PublishAsync(new WorkflowLlmInvocationStartedEvent
        {
            RunId = intent.RunId ?? string.Empty,
            StepId = intent.StepId ?? string.Empty,
            SessionId = intent.SessionId ?? string.Empty,
            RoleActorId = Id,
        }, TopologyAudience.Parent);

        var chatRequest = BuildChatRequestFromWorkflowIntent(intent);
        using var timeoutCts = intent.TimeoutMs > 0 ? new CancellationTokenSource(intent.TimeoutMs) : null;
        var streamCt = timeoutCts?.Token ?? CancellationToken.None;
        try
        {
            var replayRecord = await ExecuteWorkflowIntentStreamingChatAsync(intent, chatRequest, streamCt);
            var pendingApproval = DetectPendingApproval(
                replayRecord.ToolReceipts,
                replayRecord.ToolCalls,
                chatRequest);
            if (pendingApproval != null)
            {
                pendingApproval.WorkflowLlmContinuation = BuildApprovalContinuation(intent);
                await SuspendForToolApprovalAsync(pendingApproval);
                return;
            }

            var completed = new WorkflowLlmInvocationCompletedEvent
            {
                RunId = intent.RunId ?? string.Empty,
                StepId = intent.StepId ?? string.Empty,
                SessionId = intent.SessionId ?? string.Empty,
                RoleActorId = Id,
                Success = true,
                Content = replayRecord.Content,
                ReasoningContent = replayRecord.ReasoningContent,
                Usage = ToWorkflowUsageMetrics(replayRecord.Usage, replayRecord.Model),
            };
            var managedHandoff = ToWorkflowManagedHandoffOutcome(replayRecord.ToolReceipts);
            if (managedHandoff != null)
                completed.ManagedHandoff = managedHandoff;
            await PublishAsync(completed, TopologyAudience.Parent);
            // O1 (06-19-workflow-run-observatory): the committed RoleChatSessionCompletedEvent is the only
            // committed fact carrying both tool_calls (arguments) and tool_receipts (result/success/error);
            // persist the receipts (previously dropped) so the run-artifact fact builder can enrich tool detail.
            await PersistRoleChatSessionCompletionAsync(
                chatRequest,
                replayRecord.Content,
                replayRecord.ReasoningContent,
                replayRecord.ToolCalls,
                replayRecord.ContentParts,
                replayRecord.ContentEmitted,
                replayRecord.Usage,
                replayRecord.Model,
                replayRecord.ToolReceipts);
        }
        catch (OperationCanceledException) when (timeoutCts is { IsCancellationRequested: true })
        {
            await PublishAsync(new WorkflowLlmInvocationCompletedEvent
            {
                RunId = intent.RunId ?? string.Empty,
                StepId = intent.StepId ?? string.Empty,
                SessionId = intent.SessionId ?? string.Empty,
                RoleActorId = Id,
                Success = false,
                Error = $"LLM request timed out after {intent.TimeoutMs}ms",
            }, TopologyAudience.Parent);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex,
                "[{Role}] Workflow LLM intent failed. run={RunId} step={StepId} session={SessionId}",
                RoleName,
                intent.RunId,
                intent.StepId,
                intent.SessionId);
            await PublishAsync(new WorkflowLlmInvocationCompletedEvent
            {
                RunId = intent.RunId ?? string.Empty,
                StepId = intent.StepId ?? string.Empty,
                SessionId = intent.SessionId ?? string.Empty,
                RoleActorId = Id,
                Success = false,
                Error = SanitizeWorkflowFailureMessage(ex.Message),
            }, TopologyAudience.Parent);
        }
    }

    public override async Task HandleChatRequest(ChatRequestEvent request)
    {
        if (request.WorkflowLlmToolApprovalContinuation is null)
        {
            await base.HandleChatRequest(request);
            return;
        }

        await HandleWorkflowApprovalContinuationAsync(request);
    }

    protected override async Task<ChatMessage> ExecuteApprovedToolAsync(
        PendingToolApprovalState pending,
        AgentToolExecutionContext toolContext,
        CancellationToken ct)
    {
        var continuation = pending.WorkflowLlmContinuation;
        if (continuation is null)
            return await base.ExecuteApprovedToolAsync(pending, toolContext, ct);

        var effectiveContext = await RefreshCallerTokenAsync(toolContext, ct);
        var catalog = await BuildRequestToolCatalogAsync(ToToolScope(continuation), effectiveContext, ct);
        var tool = catalog?.RouteOwnedTools.GetValueOrDefault(pending.ToolName)
                   ?? throw new InvalidOperationException(
                       $"Approved workflow tool '{pending.ToolName}' is no longer available.");
        using var _ = AgentToolContextScope.Push(effectiveContext);
        return ChatMessage.Tool(
            pending.ToolCallId,
            await tool.ExecuteAsync(pending.ArgumentsJson, ct));
    }

    protected override Task OnApprovalTerminalFailureAsync(
        PendingToolApprovalState pending,
        string reasonCode,
        string reasonMessage)
    {
        var continuation = pending.WorkflowLlmContinuation;
        return continuation is null
            ? Task.CompletedTask
            : PublishWorkflowCompletionAsync(
                continuation,
                success: false,
                content: string.Empty,
                reasoningContent: string.Empty,
                usage: null,
                error: $"{reasonCode}: {SanitizeWorkflowFailureMessage(reasonMessage)}");
    }

    private async Task HandleWorkflowApprovalContinuationAsync(ChatRequestEvent request)
    {
        var continuation = request.WorkflowLlmToolApprovalContinuation;
        try
        {
            var toolContext = await RefreshCallerTokenAsync(
                AgentToolExecutionContextMapper.FromPayload(request.ToolContext),
                CancellationToken.None);
            request.ToolContext = toolContext.ToPayload();
            request.LlmControl = BuildContinuationLlmControl(continuation, toolContext);
            var intent = BuildContinuationIntent(continuation);
            using var timeoutCts = continuation.TimeoutMs > 0
                ? new CancellationTokenSource(continuation.TimeoutMs)
                : null;
            var replay = await ExecuteWorkflowIntentStreamingChatAsync(
                intent,
                request,
                timeoutCts?.Token ?? CancellationToken.None);
            var pendingApproval = DetectPendingApproval(
                replay.ToolReceipts,
                replay.ToolCalls,
                request);
            if (pendingApproval != null)
            {
                pendingApproval.WorkflowLlmContinuation = continuation.Clone();
                await SuspendForToolApprovalAsync(pendingApproval);
                return;
            }

            await PublishWorkflowCompletionAsync(
                continuation,
                success: true,
                replay.Content,
                replay.ReasoningContent,
                ToWorkflowUsageMetrics(replay.Usage, replay.Model),
                error: string.Empty);
            await PersistRoleChatSessionCompletionAsync(
                request,
                replay.Content,
                replay.ReasoningContent,
                replay.ToolCalls,
                replay.ContentParts,
                replay.ContentEmitted,
                replay.Usage,
                replay.Model,
                replay.ToolReceipts);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(
                ex,
                "[{Role}] Workflow approval continuation failed. run={RunId} step={StepId} session={SessionId}",
                RoleName,
                continuation.RunId,
                continuation.StepId,
                continuation.SessionId);
            await PublishWorkflowCompletionAsync(
                continuation,
                success: false,
                content: string.Empty,
                reasoningContent: string.Empty,
                usage: null,
                error: SanitizeWorkflowFailureMessage(ex.Message));
        }
    }

    private async Task<AgentToolExecutionContext> RefreshCallerTokenAsync(
        AgentToolExecutionContext context,
        CancellationToken ct)
    {
        var authority = context.NyxIdAuthority;
        if (!authority.IsComplete || string.IsNullOrWhiteSpace(authority.Scope))
            return context;
        if (_callerAccessTokenProvider is null)
            throw new InvalidOperationException(
                "Workflow caller NyxID access token provider is unavailable.");

        var token = await _callerAccessTokenProvider.IssueAsync(new WorkflowCallerNyxIdAuthority
        {
            Platform = authority.Platform,
            Tenant = authority.Tenant ?? string.Empty,
            ExternalUserId = authority.ExternalUserId,
            Scope = authority.Scope,
            BindingId = context.SenderBinding.BindingId ?? string.Empty,
        }, ct);
        return context with
        {
            Credentials = new AgentToolCredentials(token, token, token),
        };
    }

    private Task PublishWorkflowCompletionAsync(
        WorkflowLlmToolApprovalContinuation continuation,
        bool success,
        string content,
        string reasoningContent,
        WorkflowUsageMetrics? usage,
        string error) =>
        PublishAsync(new WorkflowLlmInvocationCompletedEvent
        {
            RunId = continuation.RunId,
            StepId = continuation.StepId,
            SessionId = continuation.SessionId,
            RoleActorId = Id,
            Success = success,
            Content = content,
            ReasoningContent = reasoningContent,
            Usage = usage,
            Error = error,
        }, TopologyAudience.Parent);

    private static WorkflowLlmToolApprovalContinuation BuildApprovalContinuation(
        WorkflowLlmExecutionIntent intent)
    {
        var continuation = new WorkflowLlmToolApprovalContinuation
        {
            RunId = intent.RunId ?? string.Empty,
            StepId = intent.StepId ?? string.Empty,
            SessionId = intent.SessionId ?? string.Empty,
            Model = intent.Model ?? string.Empty,
            UserMemoryPrompt = intent.UserMemoryPrompt ?? string.Empty,
            RoutePreference = intent.RoutePreference ?? string.Empty,
            TimeoutMs = intent.TimeoutMs,
            RestrictToolSets = intent.AgentToolScope?.RestrictToolSets == true,
            RestrictAllowedToolNames = intent.AgentToolScope?.RestrictAllowedToolNames == true,
        };
        if (intent.HasMaxToolRounds)
            continuation.MaxToolRounds = intent.MaxToolRounds;
        if (intent.AgentToolScope != null)
        {
            continuation.ToolSetRefs.Add(intent.AgentToolScope.ToolSetRefs);
            continuation.AllowedToolNames.Add(intent.AgentToolScope.AllowedToolNames);
        }
        return continuation;
    }

    private static WorkflowAgentToolScope ToToolScope(
        WorkflowLlmToolApprovalContinuation continuation)
    {
        var scope = new WorkflowAgentToolScope
        {
            RestrictToolSets = continuation.RestrictToolSets,
            RestrictAllowedToolNames = continuation.RestrictAllowedToolNames,
        };
        scope.ToolSetRefs.Add(continuation.ToolSetRefs);
        scope.AllowedToolNames.Add(continuation.AllowedToolNames);
        return scope;
    }

    private static WorkflowLlmExecutionIntent BuildContinuationIntent(
        WorkflowLlmToolApprovalContinuation continuation) =>
        new()
        {
            RunId = continuation.RunId,
            StepId = continuation.StepId,
            SessionId = continuation.SessionId,
            AgentToolScope = ToToolScope(continuation),
        };

    private static LLMControlContextPayload BuildContinuationLlmControl(
        WorkflowLlmToolApprovalContinuation continuation,
        AgentToolExecutionContext toolContext)
    {
        var control = new LLMControlContextPayload
        {
            NyxIdAccessToken = toolContext.Credentials.NyxIdAccessToken ?? string.Empty,
            NyxIdOrgToken = toolContext.Credentials.NyxIdOrgToken ?? string.Empty,
            SenderNyxIdAccessToken = toolContext.Credentials.SenderNyxIdAccessToken ?? string.Empty,
            ModelOverride = continuation.Model,
            NyxIdRoutePreference = continuation.RoutePreference,
            UserMemoryPrompt = continuation.UserMemoryPrompt,
        };
        if (continuation.HasMaxToolRounds)
            control.MaxToolRoundsOverride = continuation.MaxToolRounds;
        return control;
    }

    private static ChatRequestEvent BuildChatRequestFromWorkflowIntent(WorkflowLlmExecutionIntent intent)
    {
        var workflowRuntimeContext = new AgentWorkflowRuntimeContext(
            Normalize(intent.WorkflowRuntimeContext?.ParentActorId),
            Normalize(intent.WorkflowRuntimeContext?.ParentRunId),
            Normalize(intent.WorkflowRuntimeContext?.ParentStepId),
            Normalize(intent.WorkflowRuntimeContext?.RootRunId),
            Math.Max(0, intent.WorkflowRuntimeContext?.Depth ?? 0));
        var toolContext = WorkflowCallerCredentialToolContextMapper.FromCredential(
            intent.CallerCredential,
            workflowRuntimeContext);
        if (!string.IsNullOrWhiteSpace(intent.RoutePreference))
        {
            toolContext = toolContext with
            {
                Routing = toolContext.Routing with
                {
                    NyxIdRoutePreference = intent.RoutePreference.Trim(),
                },
            };
        }
        toolContext = ApplyToolVisibility(intent.AgentToolScope, toolContext);
        toolContext = ApplyRunScopeToCaller(intent.ScopeId, toolContext);
        toolContext = ApplySchedule(intent.ScheduleId, toolContext);
        toolContext = toolContext with
        {
            InvocationSurface = AgentToolInvocationSurface.WorkflowLlmToolLoop,
        };

        var request = new ChatRequestEvent
        {
            Prompt = intent.Prompt ?? string.Empty,
            SessionId = intent.SessionId ?? string.Empty,
            TimeoutMs = intent.TimeoutMs,
            ToolContext = AgentToolExecutionContextMapper.ToPayload(toolContext),
            LlmControl = new LLMControlContextPayload
            {
                NyxIdAccessToken = toolContext.Credentials.NyxIdAccessToken ?? string.Empty,
                ModelOverride = intent.Model ?? string.Empty,
                NyxIdRoutePreference = toolContext.Routing.NyxIdRoutePreference ?? string.Empty,
                UserMemoryPrompt = intent.UserMemoryPrompt ?? string.Empty,
                SenderNyxIdAccessToken = intent.SenderNyxIdAccessToken ?? string.Empty,
            },
        };
        if (intent.HasMaxToolRounds)
            request.LlmControl.MaxToolRoundsOverride = intent.MaxToolRounds;
        request.InputParts.Add(intent.InputFileRefs.Select(ToChatContentPart));
        CopyWorkflowIntentMetadata(intent.Headers, request.Metadata);
        CopyWorkflowIntentMetadata(intent.Annotations, request.Metadata);
        return request;
    }

    private static ChatContentPart ToChatContentPart(WorkflowFileRef fileRef)
    {
        ArgumentNullException.ThrowIfNull(fileRef);
        return new ChatContentPart
        {
            Kind = ResolveChatContentPartKind(fileRef.MediaType),
            Uri = ResolveFileRefUri(fileRef),
            MediaType = Normalize(fileRef.MediaType) ?? string.Empty,
            Name = Normalize(fileRef.FileName) ?? string.Empty,
        };
    }

    private static ChatContentPartKind ResolveChatContentPartKind(string? mediaType)
    {
        var normalized = Normalize(mediaType);
        if (normalized is null)
            return ChatContentPartKind.Unspecified;
        if (normalized.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
            return ChatContentPartKind.Image;
        if (normalized.StartsWith("audio/", StringComparison.OrdinalIgnoreCase))
            return ChatContentPartKind.Audio;
        if (normalized.StartsWith("video/", StringComparison.OrdinalIgnoreCase))
            return ChatContentPartKind.Video;
        return ChatContentPartKind.Unspecified;
    }

    private static string ResolveFileRefUri(WorkflowFileRef fileRef) =>
        Normalize(fileRef.ArtifactId) ??
        (string.IsNullOrWhiteSpace(fileRef.FileId)
            ? string.Empty
            : $"workflow-file://{fileRef.FileId.Trim()}");

    private static AgentToolExecutionContext ApplyToolVisibility(
        WorkflowAgentToolScope? scope,
        AgentToolExecutionContext toolContext)
    {
        if (scope == null || (!scope.RestrictAllowedToolNames && scope.AllowedToolNames.Count == 0))
            return toolContext;

        return toolContext with
        {
            ToolVisibility = AgentToolVisibilityScope.FromAllowedToolNames(scope.AllowedToolNames),
        };
    }

    // Populate the tool caller scope from the owning run's scope on the channel-less
    // Direct/studio path: WorkflowCallerCredentialToolContextMapper only sets credentials,
    // leaving Caller empty, so scope-scoped tools (aevatar_*) would otherwise see no scope
    // and fail. Scope-scoped tools require both ScopeId and OwnerSubject (see
    // AevatarInvocationDispatcher.ResolveCallerScope), mirroring the channel inbound path
    // that sets both from the registration scope. This is an "empty -> fill" guard: it is a
    // no-op when the caller scope is already set (so any future inbound-stamped caller wins)
    // and a no-op when the run carries no scope.
    private static AgentToolExecutionContext ApplyRunScopeToCaller(
        string? runScopeId,
        AgentToolExecutionContext toolContext)
    {
        var scopeId = Normalize(runScopeId);
        if (scopeId is null)
            return toolContext;

        if (!string.IsNullOrWhiteSpace(toolContext.Caller.ScopeId))
            return toolContext;

        return toolContext with
        {
            Caller = toolContext.Caller with
            {
                ScopeId = scopeId,
                OwnerSubject = string.IsNullOrWhiteSpace(toolContext.Caller.OwnerSubject)
                    ? scopeId
                    : toolContext.Caller.OwnerSubject,
            },
        };
    }

    private static AgentToolExecutionContext ApplySchedule(
        string? scheduleId,
        AgentToolExecutionContext toolContext)
    {
        var normalizedScheduleId = Normalize(scheduleId);
        return normalizedScheduleId is null
            ? toolContext
            : toolContext with { Schedule = new AgentToolScheduleContext(normalizedScheduleId) };
    }

    private static void CopyWorkflowIntentMetadata(
        IEnumerable<KeyValuePair<string, string>> source,
        IDictionary<string, string> target)
    {
        foreach (var pair in source)
        {
            if (string.Equals(pair.Key, LegacyConnectorHttpAuthorizationBlockedKey, StringComparison.Ordinal))
                continue;

            target[pair.Key] = pair.Value;
        }
    }

    private async Task<WorkflowIntentReplayRecord> ExecuteWorkflowIntentStreamingChatAsync(
        WorkflowLlmExecutionIntent intent,
        ChatRequestEvent request,
        CancellationToken streamCt)
    {
        var inputParts = ResolveWorkflowRequestInputParts(request);
        var llmControl = LLMControlContextMapper.FromPayload(request.LlmControl);
        var toolContext = llmControl.ToToolContext(AgentToolExecutionContextMapper.FromPayload(request.ToolContext));
        var turnCatalog = await BuildRequestToolCatalogAsync(intent.AgentToolScope, toolContext, streamCt);
        if (turnCatalog is not null)
            toolContext = AddRequestToolsToVisibility(toolContext, turnCatalog.RouteOwnedTools.Keys);
        var metadata = request.Metadata.Count > 0
            ? AgentToolExecutionContextMapper.StripOwnedControlKeys(
                new Dictionary<string, string>(request.Metadata, StringComparer.Ordinal))
            : null;

        var fullContent = new StringBuilder();
        var fullReasoning = new StringBuilder();
        var toolCalls = new WorkflowToolCallAccumulator();
        var toolReceipts = new List<AgentToolReceipt>();
        var contentParts = new List<ContentPart>();
        TokenUsage? usage = null;

        await foreach (var chunk in ChatStreamAsync(
                           inputParts,
                           request.SessionId,
                           llmControl,
                           toolContext,
                           turnCatalog,
                           metadata,
                           streamCt))
        {
            if (chunk.Usage != null)
                usage = chunk.Usage;

            if (!string.IsNullOrEmpty(chunk.DeltaContent))
            {
                fullContent.Append(chunk.DeltaContent);
                await PublishAsync(new WorkflowLlmStreamChunkEvent
                {
                    RunId = intent.RunId ?? string.Empty,
                    StepId = intent.StepId ?? string.Empty,
                    SessionId = intent.SessionId ?? string.Empty,
                    RoleActorId = Id,
                    DeltaContent = chunk.DeltaContent,
                }, TopologyAudience.Parent);
            }

            if (chunk.DeltaContentPart != null)
                contentParts.Add(chunk.DeltaContentPart);

            if (!string.IsNullOrEmpty(chunk.DeltaReasoningContent))
            {
                fullReasoning.Append(chunk.DeltaReasoningContent);
                await PublishAsync(new WorkflowLlmStreamChunkEvent
                {
                    RunId = intent.RunId ?? string.Empty,
                    StepId = intent.StepId ?? string.Empty,
                    SessionId = intent.SessionId ?? string.Empty,
                    RoleActorId = Id,
                    DeltaReasoningContent = chunk.DeltaReasoningContent,
                }, TopologyAudience.Parent);
            }

            if (chunk.DeltaToolCall != null)
                toolCalls.TrackDelta(chunk.DeltaToolCall);

            if (chunk.ToolReceipt != null)
                toolReceipts.Add(chunk.ToolReceipt.Clone());
        }

        return new WorkflowIntentReplayRecord(
            fullContent.ToString(),
            fullReasoning.ToString(),
            toolCalls.BuildToolCalls(),
            toolReceipts,
            contentParts,
            Usage: usage,
            Model: EffectiveConfig.Model ?? string.Empty,
            ContentEmitted: fullContent.Length > 0);
    }

    private async Task<AgentProfileTurnCatalog?> BuildRequestToolCatalogAsync(
        WorkflowAgentToolScope? scope,
        AgentToolExecutionContext toolContext,
        CancellationToken ct)
    {
        if (_toolSetRegistry is null || scope?.ToolSetRefs.Count is not > 0)
            return null;

        var tools = new List<IAgentTool>();
        var resolutionFailures = 0;
        var discoveryFailures = 0;
        var collisions = 0;
        using var _ = AgentToolContextScope.Push(toolContext);
        foreach (var toolSetRef in scope.ToolSetRefs
                     .Where(static name => !string.IsNullOrWhiteSpace(name))
                     .Select(static name => name.Trim())
                     .Distinct(StringComparer.Ordinal))
        {
            ToolSetResolveResult resolved;
            try
            {
                resolved = _toolSetRegistry.Resolve(toolSetRef);
            }
            catch (Exception)
            {
                resolutionFailures++;
                continue;
            }

            if (!resolved.IsSuccess)
            {
                resolutionFailures++;
                continue;
            }

            foreach (var source in resolved.Sources)
            {
                try
                {
                    tools.AddRange(await source.DiscoverToolsAsync(ct));
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception)
                {
                    discoveryFailures++;
                }
            }
        }

        var exactTools = new List<IAgentTool>();
        foreach (var group in tools
                     .Where(static tool => !string.IsNullOrWhiteSpace(tool.Name))
                     .GroupBy(static tool => tool.Name.Trim(), StringComparer.OrdinalIgnoreCase))
        {
            var exact = group.First();
            if (group.Any(tool => !ReferenceEquals(tool, exact)))
            {
                collisions++;
                continue;
            }

            exactTools.Add(exact);
        }
        if (resolutionFailures + discoveryFailures + collisions > 0)
        {
            Logger.LogWarning(
                "Workflow request tools degraded. resolution_failures={ResolutionFailures} discovery_failures={DiscoveryFailures} collisions={Collisions}",
                resolutionFailures,
                discoveryFailures,
                collisions);
        }

        var allowedNames = (scope.RestrictAllowedToolNames || scope.AllowedToolNames.Count > 0
                ? scope.AllowedToolNames
                : Tools.GetAll().Select(static tool => tool.Name))
            .Concat(exactTools.Select(static tool => tool.Name));
        return new AgentProfileTurnCatalog(
            allowedNames,
            profilePromptLayer: null,
            selectedSkillPromptLayer: null,
            selectedIntentId: null,
            candidateIntentId: null,
            diagnostics: null,
            exactTools);
    }

    private static AgentToolExecutionContext AddRequestToolsToVisibility(
        AgentToolExecutionContext toolContext,
        IEnumerable<string> toolNames)
    {
        if (!toolContext.ToolVisibility.IsRestricted)
            return toolContext;

        return toolContext with
        {
            ToolVisibility = AgentToolVisibilityScope.FromAllowedToolNames(
                toolContext.ToolVisibility.AllowedToolNames!.Concat(toolNames)),
        };
    }

    private static IReadOnlyList<ContentPart> ResolveWorkflowRequestInputParts(ChatRequestEvent request)
    {
        if (request.InputParts.Count > 0)
        {
            var parts = new List<ContentPart>();
            if (!string.IsNullOrWhiteSpace(request.Prompt))
                parts.Add(ContentPart.TextPart(request.Prompt));
            parts.AddRange(ContentPartProtoMapper.FromProtoList(request.InputParts));
            return parts;
        }

        return [ContentPart.TextPart(request.Prompt ?? string.Empty)];
    }

    private static string SanitizeWorkflowFailureMessage(string? message) =>
        string.IsNullOrWhiteSpace(message) ? "LLM request failed." : message.Trim();

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private sealed record WorkflowIntentReplayRecord(
        string Content,
        string ReasoningContent,
        IReadOnlyList<ToolCall> ToolCalls,
        IReadOnlyList<AgentToolReceipt> ToolReceipts,
        IReadOnlyList<ContentPart> ContentParts,
        TokenUsage? Usage,
        string? Model,
        bool ContentEmitted);

    private static WorkflowManagedHandoffOutcome? ToWorkflowManagedHandoffOutcome(
        IReadOnlyList<AgentToolReceipt> toolReceipts)
    {
        var handoff = toolReceipts
            .Select(static receipt => receipt.ManagedWorkflowHandoff)
            .LastOrDefault(static receipt => receipt != null && !string.IsNullOrWhiteSpace(receipt.InvocationId));
        if (handoff == null)
            return null;

        return new WorkflowManagedHandoffOutcome
        {
            ParentActorId = handoff.ParentActorId ?? string.Empty,
            ParentRunId = handoff.ParentRunId ?? string.Empty,
            ParentStepId = handoff.ParentStepId ?? string.Empty,
            InvocationId = handoff.InvocationId ?? string.Empty,
            ChildRunId = handoff.ChildRunId ?? string.Empty,
            StreamTopic = handoff.StreamTopic ?? string.Empty,
        };
    }

    private static WorkflowUsageMetrics? ToWorkflowUsageMetrics(TokenUsage? usage, string? model) =>
        usage == null
            ? null
            : new WorkflowUsageMetrics
            {
                PromptTokens = usage.PromptTokens,
                CompletionTokens = usage.CompletionTokens,
                TotalTokens = usage.TotalTokens,
                Model = model ?? string.Empty,
            };

    private sealed class WorkflowToolCallAccumulator
    {
        private readonly Dictionary<string, ToolCallAggregate> _aggregates = new(StringComparer.Ordinal);
        private readonly List<string> _order = [];
        private int _anonymousCounter;
        private string? _activeAnonymousKey;
        private string? _lastKnownKey;

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
                if (!_aggregates.TryGetValue(key, out var aggregate))
                    continue;

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

            if (!string.IsNullOrWhiteSpace(_lastKnownKey) &&
                _aggregates.TryGetValue(_lastKnownKey, out var knownAggregate))
            {
                return knownAggregate;
            }

            return ResolveAnonymousAggregate();
        }

        private ToolCallAggregate ResolveKnownIdAggregate(string id)
        {
            var knownKey = $"id:{id}";
            if (TryPromoteActiveAnonymousAggregate(knownKey, id, out var promoted))
            {
                _activeAnonymousKey = null;
                _lastKnownKey = knownKey;
                return promoted;
            }

            _activeAnonymousKey = null;
            if (!_aggregates.TryGetValue(knownKey, out var aggregate))
            {
                aggregate = new ToolCallAggregate(id);
                _aggregates[knownKey] = aggregate;
                _order.Add(knownKey);
            }

            _lastKnownKey = knownKey;
            return aggregate;
        }

        private ToolCallAggregate ResolveAnonymousAggregate()
        {
            if (!string.IsNullOrWhiteSpace(_activeAnonymousKey) &&
                _aggregates.TryGetValue(_activeAnonymousKey, out var activeAggregate))
            {
                return activeAggregate;
            }

            _anonymousCounter++;
            var anonymousKey = $"anon:{_anonymousCounter}";
            var anonymousId = $"stream-tool-call-{_anonymousCounter}";
            var aggregate = new ToolCallAggregate(anonymousId);
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

            if (!_aggregates.TryGetValue(_activeAnonymousKey, out var anonymousAggregate))
                return false;

            if (_aggregates.ContainsKey(knownKey))
                return false;

            anonymousAggregate.Id = knownId;
            _aggregates.Remove(_activeAnonymousKey);
            _aggregates[knownKey] = anonymousAggregate;
            ReplaceOrderKey(_activeAnonymousKey, knownKey);
            aggregate = anonymousAggregate;
            return true;
        }

        private void ReplaceOrderKey(string sourceKey, string targetKey)
        {
            for (var index = 0; index < _order.Count; index++)
            {
                if (!string.Equals(_order[index], sourceKey, StringComparison.Ordinal))
                    continue;

                _order[index] = targetKey;
                return;
            }
        }

        private sealed class ToolCallAggregate(string id)
        {
            public string Id { get; set; } = id;
            public string? Name { get; set; }
            public StringBuilder Arguments { get; } = new();
        }
    }
}
