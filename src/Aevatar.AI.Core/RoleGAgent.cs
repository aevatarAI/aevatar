// ─────────────────────────────────────────────────────────────
// RoleGAgent - role-based AI GAgent.
//
// Handles ChatRequestEvent:
// 1. Calls LLM via ChatStreamAsync (streaming)
// 2. Publishes AG-UI events: TextMessageStart → Content* → ToolCall* → End
// 3. Logs prompt and full LLM response for observability
// ─────────────────────────────────────────────────────────────

using System.Text;
using System.Text.Json;
using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.Agents;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.Middleware;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.Core.Chat;
using Aevatar.AI.Core.Hooks;
using Aevatar.AI.Core.Middleware;
using Aevatar.Foundation.Abstractions.Attributes;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Core.EventSourcing;
using Google.Protobuf;
using Microsoft.Extensions.Logging;
namespace Aevatar.AI.Core;

/// <summary>
/// Role-based AI GAgent. Receives ChatRequestEvent and streams LLM response.
/// </summary>
public class RoleGAgent : AIGAgentBase<RoleGAgentState>, IRoleAgent
{
    private const string LlmFailureContentPrefix = "[[AEVATAR_LLM_ERROR]]";
    private const int MaxTrackedSessions = 128;
    private string _appliedEventModules = string.Empty;
    private string _appliedEventRoutes = string.Empty;
    private IServiceProvider? _appliedModuleServices;

    public RoleGAgent(
        ILLMProviderFactory? llmProviderFactory = null,
        IEnumerable<IAIGAgentExecutionHook>? additionalHooks = null,
        IEnumerable<IAgentRunMiddleware>? agentMiddlewares = null,
        IEnumerable<IToolCallMiddleware>? toolMiddlewares = null,
        IEnumerable<ILLMCallMiddleware>? llmMiddlewares = null,
        IEnumerable<IAgentToolSource>? toolSources = null,
        IToolApprovalHandler? approvalHandler = null)
        : base(
            llmProviderFactory,
            additionalHooks,
            agentMiddlewares,
            toolMiddlewares,
            llmMiddlewares,
            toolSources,
            approvalHandler)
    {
    }

    /// <summary>Role name.</summary>
    public string RoleName { get; private set; } = "";

    [EventHandler]
    public async Task HandleInitializeRoleAgent(InitializeRoleAgentEvent evt)
    {
        await PersistDomainEventAsync(evt);
    }

    /// <summary>Handles tool approval decisions from the frontend or NyxID remote.</summary>
    [EventHandler(AllowSelfHandling = true)]
    public async Task HandleToolApprovalDecision(ToolApprovalDecisionEvent evt)
    {
        // ─── Multi-turn continuation ───
        var pending = State.PendingApproval;
        if (pending == null || pending.RequestId != evt.RequestId)
            return;

        // Cancel the escalation timeout
        await CancelApprovalTimeoutAsync(pending);

        if (evt.Approved)
        {
            Logger.LogInformation(
                "[{Role}] Tool approval APPROVED. Executing tool={Tool} request={RequestId}",
                RoleName, pending.ToolName, pending.RequestId);

            // Restore metadata context (NyxID access token etc.) so tool execution can
            // read AgentToolRequestContext.TryGet(NyxIdAccessToken).
            var prevMetadata = AgentToolRequestContext.CurrentMetadata;
            try
            {
                AgentToolRequestContext.CurrentMetadata = pending.Metadata.Count > 0
                    ? new Dictionary<string, string>(pending.Metadata, StringComparer.Ordinal)
                    : null;

                // Execute the yielded tool call
                var toolResult = await Tools.ExecuteToolCallAsync(
                    new ToolCall
                    {
                        Id = pending.ToolCallId,
                        Name = pending.ToolName,
                        ArgumentsJson = pending.ArgumentsJson,
                    },
                    CancellationToken.None);

                Logger.LogInformation(
                    "[{Role}] Tool executed. result length={Len} request={RequestId}",
                    RoleName, toolResult.Content?.Length ?? 0, pending.RequestId);

                // Clear pending state
                await PersistDomainEventAsync(new ClearPendingApprovalEvent { RequestId = pending.RequestId });

                // Build continuation prompt with the actual tool result
                var continuation = BuildContinuationPrompt(pending, toolResult.Content);

                Logger.LogInformation(
                    "[{Role}] Dispatching continuation chat. request={RequestId}",
                    RoleName, pending.RequestId);

                // Self-continuation: dispatch ChatRequestEvent to own inbox (new turn).
                var continuationRequest = new ChatRequestEvent
                {
                    Prompt = continuation,
                    SessionId = Guid.NewGuid().ToString("N"),
                    ScopeId = pending.SessionId,
                };
                foreach (var kv in pending.Metadata)
                    continuationRequest.Metadata[kv.Key] = kv.Value;

                await SendToAsync(Id, continuationRequest);

                Logger.LogInformation(
                    "[{Role}] Continuation dispatched. request={RequestId}",
                    RoleName, pending.RequestId);
            }
            catch (Exception ex)
            {
                Logger.LogError(ex,
                    "[{Role}] Approval continuation FAILED. request={RequestId}",
                    RoleName, pending.RequestId);

                // Still clear pending so we don't get stuck
                try { await PersistDomainEventAsync(new ClearPendingApprovalEvent { RequestId = pending.RequestId }); }
                catch { /* best effort */ }

                throw; // Re-throw so the SSE endpoint sees the error
            }
            finally
            {
                AgentToolRequestContext.CurrentMetadata = prevMetadata;
            }
        }
        else
        {
            await PersistDomainEventAsync(new ClearPendingApprovalEvent { RequestId = pending.RequestId });
        }
    }

    /// <summary>
    /// Handles local approval timeout: escalate to NyxID remote.
    /// Calls the remote handler directly within the actor turn. This blocks the actor
    /// for up to 45s while NyxID polls for a decision, but guarantees the call works
    /// (background Task.Run cannot reliably call SendToAsync outside actor context).
    /// </summary>
    [EventHandler(AllowSelfHandling = true, OnlySelfHandling = true)]
    public async Task HandleToolApprovalTimeout(ToolApprovalTimeoutFiredEvent evt)
    {
        var pending = State.PendingApproval;
        if (pending == null || pending.RequestId != evt.RequestId)
            return;

        Logger.LogInformation(
            "[{Role}] Tool approval local timeout. Escalating to NyxID remote. request={RequestId}",
            RoleName, evt.RequestId);

        var remoteHandler = ResolveRemoteApprovalHandler();
        if (remoteHandler == null)
        {
            Logger.LogWarning("[{Role}] No remote approval handler configured. Clearing pending. request={RequestId}",
                RoleName, evt.RequestId);
            await PersistDomainEventAsync(new ClearPendingApprovalEvent { RequestId = pending.RequestId });
            return;
        }

        // Restore metadata (NyxID access token) for the remote handler
        var prevMetadata = AgentToolRequestContext.CurrentMetadata;
        try
        {
            AgentToolRequestContext.CurrentMetadata = pending.Metadata.Count > 0
                ? new Dictionary<string, string>(pending.Metadata, StringComparer.Ordinal)
                : null;

            var result = await remoteHandler.RequestApprovalAsync(
                new ToolApprovalRequest
                {
                    RequestId = pending.RequestId,
                    ToolName = pending.ToolName,
                    ToolCallId = pending.ToolCallId,
                    ArgumentsJson = pending.ArgumentsJson,
                    ApprovalMode = ToolApprovalMode.Auto,
                    IsDestructive = pending.IsDestructive,
                },
                CancellationToken.None);

            if (result.Decision == ToolApprovalDecision.Approved)
            {
                Logger.LogInformation("[{Role}] NyxID remote approved. request={RequestId}", RoleName, evt.RequestId);
                await HandleToolApprovalDecision(new ToolApprovalDecisionEvent
                {
                    RequestId = pending.RequestId,
                    Approved = true,
                    Reason = result.Reason ?? "Approved via NyxID remote.",
                });
                return;
            }

            Logger.LogWarning("[{Role}] NyxID remote denied/timed out: {Reason}. request={RequestId}",
                RoleName, result.Reason, evt.RequestId);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "[{Role}] NyxID remote approval failed. request={RequestId}",
                RoleName, evt.RequestId);
        }
        finally
        {
            AgentToolRequestContext.CurrentMetadata = prevMetadata;
        }

        // Remote failed/denied/timed out → clear pending
        await PersistDomainEventAsync(new ClearPendingApprovalEvent { RequestId = pending.RequestId });
    }

    /// <summary>Override in subclasses to provide the NyxID remote approval handler for timeout escalation.</summary>
    protected virtual IToolApprovalHandler? ResolveRemoteApprovalHandler() => null;

    // ─── Approval continuation constants ───

    private const int ApprovalLocalTimeoutSeconds = 15;

    // ─── Approval helpers ───

    private PendingToolApprovalState? DetectPendingApproval(
        SessionReplayRecord replayRecord,
        ChatRequestEvent request)
    {
        return DetectPendingApprovalFromHistory(request);
    }

    private PendingToolApprovalState? DetectPendingApprovalFromHistory(ChatRequestEvent request)
    {
        // Scan recent history messages for approval_required marker
        for (var i = History.Messages.Count - 1; i >= 0; i--)
        {
            var msg = History.Messages[i];
            if (msg.Role != "tool" || string.IsNullOrWhiteSpace(msg.Content))
                continue;

            if (!msg.Content.Contains(Middleware.ToolApprovalMiddleware.ApprovalRequiredKey))
                continue;

            try
            {
                using var doc = JsonDocument.Parse(msg.Content);
                if (doc.RootElement.ValueKind != JsonValueKind.Object)
                    continue;
                if (!doc.RootElement.TryGetProperty("approval_required", out var ar) || !ar.GetBoolean())
                    continue;

                var requestId = doc.RootElement.TryGetProperty("request_id", out var rid)
                    ? rid.GetString() ?? Guid.NewGuid().ToString("N")
                    : Guid.NewGuid().ToString("N");
                var toolName = doc.RootElement.TryGetProperty("tool_name", out var tn)
                    ? tn.GetString() ?? "" : "";
                var toolCallId = doc.RootElement.TryGetProperty("tool_call_id", out var tcid)
                    ? tcid.GetString() ?? "" : "";
                var args = doc.RootElement.TryGetProperty("arguments", out var a)
                    ? a.GetString() ?? "{}" : "{}";

                var pending = new PendingToolApprovalState
                {
                    RequestId = requestId,
                    SessionId = request.SessionId ?? "",
                    ToolName = toolName,
                    ToolCallId = toolCallId,
                    ArgumentsJson = args,
                    IsDestructive = true,
                };
                // Preserve metadata (NyxID access token etc.) for continuation
                foreach (var kv in request.Metadata)
                    pending.Metadata[kv.Key] = kv.Value;

                return pending;
            }
            catch (JsonException)
            {
                // Not valid JSON, skip
            }
        }

        return null;
    }

    // Stored lease from the last scheduled timeout, kept in-memory for cancellation.
    // Not persisted — if the actor deactivates, the durable callback runtime handles
    // re-delivery; the actor's HandleToolApprovalTimeout idempotently checks pending state.
    private Foundation.Abstractions.Runtime.Callbacks.RuntimeCallbackLease? _approvalTimeoutLease;

    private async Task ScheduleApprovalTimeoutAsync(PendingToolApprovalState pending)
    {
        var callbackId = $"tool-approval-timeout-{pending.RequestId}";
        pending.TimeoutCallbackId = callbackId;
        try
        {
            _approvalTimeoutLease = await ScheduleSelfDurableTimeoutAsync(
                callbackId,
                TimeSpan.FromSeconds(ApprovalLocalTimeoutSeconds),
                new ToolApprovalTimeoutFiredEvent
                {
                    RequestId = pending.RequestId,
                    SessionId = pending.SessionId,
                });
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "[{Role}] Failed to schedule approval timeout", RoleName);
        }
    }

    private async Task CancelApprovalTimeoutAsync(PendingToolApprovalState pending)
    {
        if (_approvalTimeoutLease == null)
            return;

        try
        {
            await CancelDurableCallbackAsync(_approvalTimeoutLease);
            _approvalTimeoutLease = null;
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "[{Role}] Failed to cancel approval timeout", RoleName);
        }
    }

    private static string BuildContinuationPrompt(PendingToolApprovalState pending, string? toolResult)
    {
        return $"[System continuation] The user approved the tool call '{pending.ToolName}'. " +
               $"The tool was executed and returned the following result:\n\n" +
               $"{toolResult ?? "(no output)"}\n\n" +
               "Please continue with the original task based on this result.";
    }

    // ─── Pending approval state transitions ───

    private static RoleGAgentState ApplyPendingApproval(
        RoleGAgentState current,
        PendingToolApprovalPersistedEvent evt)
    {
        var next = current.Clone();
        next.PendingApproval = evt.Pending;
        return next;
    }

    private static RoleGAgentState ApplyClearPendingApproval(
        RoleGAgentState current,
        ClearPendingApprovalEvent evt)
    {
        if (current.PendingApproval == null)
            return current;
        if (!string.IsNullOrWhiteSpace(evt.RequestId) &&
            current.PendingApproval.RequestId != evt.RequestId)
            return current;

        var next = current.Clone();
        next.PendingApproval = null;
        return next;
    }

    /// <summary>Returns agent description.</summary>
    public override Task<string> GetDescriptionAsync() =>
        Task.FromResult($"RoleGAgent[{RoleName}]:{Id}");

    protected override RoleGAgentState TransitionState(RoleGAgentState current, IMessage evt) =>
        StateTransitionMatcher
            .Match(current, evt)
            .On<InitializeRoleAgentEvent>(ApplyInitializeRoleAgent)
            .On<RoleChatSessionStartedEvent>(ApplyChatSessionStarted)
            .On<RoleChatSessionCompletedEvent>(ApplyChatSessionCompleted)
            .On<PendingToolApprovalPersistedEvent>(ApplyPendingApproval)
            .On<ClearPendingApprovalEvent>(ApplyClearPendingApproval)
            .OrCurrent();

    protected override Task OnStateChangedAfterConfigAppliedAsync(RoleGAgentState state, CancellationToken ct)
    {
        _ = ct;
        RoleName = state.RoleName ?? string.Empty;
        ApplyModuleExtensionsFromStateIfNeeded(state);
        return Task.CompletedTask;
    }

    protected override AIAgentConfigStateOverrides ExtractStateConfigOverrides(RoleGAgentState state)
    {
        var overrides = state.ConfigOverrides;
        if (overrides == null)
            return new AIAgentConfigStateOverrides();

        return new AIAgentConfigStateOverrides
        {
            HasProviderName = overrides.HasProviderName,
            ProviderName = overrides.HasProviderName ? overrides.ProviderName : null,
            HasModel = overrides.HasModel,
            Model = overrides.HasModel ? overrides.Model : null,
            HasSystemPrompt = overrides.HasSystemPrompt,
            SystemPrompt = overrides.HasSystemPrompt ? overrides.SystemPrompt : null,
            HasTemperature = overrides.HasTemperature,
            Temperature = overrides.HasTemperature ? overrides.Temperature : null,
            HasMaxTokens = overrides.HasMaxTokens,
            MaxTokens = overrides.HasMaxTokens ? overrides.MaxTokens : null,
            HasMaxToolRounds = overrides.HasMaxToolRounds,
            MaxToolRounds = overrides.HasMaxToolRounds ? overrides.MaxToolRounds : null,
            HasMaxHistoryMessages = overrides.HasMaxHistoryMessages,
            MaxHistoryMessages = overrides.HasMaxHistoryMessages ? overrides.MaxHistoryMessages : null,
            HasStreamBufferCapacity = overrides.HasStreamBufferCapacity,
            StreamBufferCapacity = overrides.HasStreamBufferCapacity ? overrides.StreamBufferCapacity : null,
            HasMaxPromptTokenBudget = overrides.HasMaxPromptTokenBudget,
            MaxPromptTokenBudget = overrides.HasMaxPromptTokenBudget ? overrides.MaxPromptTokenBudget : null,
            HasCompressionThreshold = overrides.HasCompressionThreshold,
            CompressionThreshold = overrides.HasCompressionThreshold ? overrides.CompressionThreshold : null,
            HasEnableSummarization = overrides.HasEnableSummarization,
            EnableSummarization = overrides.HasEnableSummarization ? overrides.EnableSummarization : null,
        };
    }

    /// <summary>
    /// Handles ChatRequestEvent via streaming LLM call.
    /// Publishes text stream events and tool call events.
    /// </summary>
    [EventHandler(AllowSelfHandling = true)]
    public virtual async Task HandleChatRequest(ChatRequestEvent request)
    {
        var trackedSession = ResolveTrackedSession(request);
        if (trackedSession is { Completed: true })
        {
            Logger.LogInformation(
                "[{Role}] Replaying cached LLM completion for session={SessionId}",
                RoleName,
                request.SessionId);
            await ReplayCompletedSessionAsync(request.SessionId, trackedSession);
            return;
        }

        if (!string.IsNullOrWhiteSpace(request.SessionId) && trackedSession == null)
        {
            await PersistDomainEventAsync(new RoleChatSessionStartedEvent
            {
                SessionId = request.SessionId,
                Prompt = request.Prompt,
                InputParts = { request.InputParts },
            });
        }
        else if (trackedSession != null)
        {
            Logger.LogInformation(
                "[{Role}] Resuming incomplete LLM session={SessionId}",
                RoleName,
                request.SessionId);
        }

        var promptPreview = BuildRequestPreview(request);
        Logger.LogInformation("[{Role}] LLM request: {Preview}", RoleName, promptPreview);
        var timeoutMs = ResolveLlmTimeoutMs(request);
        var useWorkflowFailureMarker = timeoutMs > 0;
        using var timeoutCts = timeoutMs > 0 ? new CancellationTokenSource(timeoutMs) : null;
        var streamCt = timeoutCts?.Token ?? CancellationToken.None;

        // ─── AG-UI: TEXT_MESSAGE_START ───
        await PublishAsync(new TextMessageStartEvent
        {
            SessionId = request.SessionId,
            AgentId = Id,
        }, TopologyAudience.Parent);

        SessionReplayRecord replayRecord;
        try
        {
            replayRecord = await ExecuteStreamingChatAsync(request, streamCt);
        }
        catch (OperationCanceledException) when (timeoutCts is { IsCancellationRequested: true })
        {
            Logger.LogWarning(
                "[{Role}] LLM request timeout after {TimeoutMs}ms. session={SessionId}",
                RoleName,
                timeoutMs,
                request.SessionId);
            replayRecord = SessionReplayRecord.FromFailure(
                BuildLlmFailureContent($"LLM request timed out after {timeoutMs}ms"));
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex,
                "[{Role}] LLM request failed. session={SessionId}, provider={Provider}, model={Model}, metadataKeys=[{MetadataKeys}]",
                RoleName,
                request.SessionId,
                EffectiveConfig.ProviderName,
                EffectiveConfig.Model ?? "<default>",
                request.Metadata.Count > 0 ? string.Join(",", request.Metadata.Keys) : "<none>");
            var toolNames = Tools.HasTools
                ? string.Join(",", Tools.GetAll().Select(t => t.Name ?? "<null>"))
                : "none";
            replayRecord = SessionReplayRecord.FromFailure(
                useWorkflowFailureMarker
                    ? BuildLlmFailureContent(ex.Message)
                    : $"LLM request failed [tools={toolNames}]: {SanitizeFailureMessage(ex.Message)}");
        }

        // ─── Detect approval-pending tool result and set up continuation ───
        var pendingApproval = DetectPendingApproval(replayRecord, request);
        if (pendingApproval != null)
        {
            await PersistDomainEventAsync(new PendingToolApprovalPersistedEvent
            {
                Pending = pendingApproval,
            });

            await PublishAsync(new ToolApprovalRequestEvent
            {
                RequestId = pendingApproval.RequestId,
                SessionId = pendingApproval.SessionId,
                ToolName = pendingApproval.ToolName,
                ToolCallId = pendingApproval.ToolCallId,
                ArgumentsJson = pendingApproval.ArgumentsJson,
                IsDestructive = pendingApproval.IsDestructive,
                ApprovalMode = "yield",
                TimeoutSeconds = ApprovalLocalTimeoutSeconds,
            }, TopologyAudience.Parent);

            await ScheduleApprovalTimeoutAsync(pendingApproval);
        }

        // Publish first so consumers (relay, SSE) get the response immediately.
        // Persist is best-effort: concurrency conflicts must not block the reply.
        await PublishCompletionAsync(request.SessionId, replayRecord.Content);

        try
        {
            await PersistSessionCompletionAsync(request, replayRecord);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Logger.LogWarning(ex,
                "[{Role}] Failed to persist session completion. session={SessionId}. " +
                "Response was already published — session replay may be unavailable.",
                RoleName, request.SessionId);
        }
    }

    private static int ResolveLlmTimeoutMs(ChatRequestEvent request)
    {
        return request.TimeoutMs > 0 ? request.TimeoutMs : 0;
    }

    private static string BuildLlmFailureContent(string? message)
    {
        var safeMessage = SanitizeFailureMessage(message);
        return $"{LlmFailureContentPrefix} {safeMessage}";
    }

    private static string SanitizeFailureMessage(string? message) =>
        string.IsNullOrWhiteSpace(message) ? "LLM request failed." : message.Trim();

    private async Task<SessionReplayRecord> ExecuteStreamingChatAsync(ChatRequestEvent request, CancellationToken streamCt)
    {
        // ─── AG-UI: TEXT_MESSAGE_CONTENT — streaming chunks ───
        var initialHistoryCount = History.Count;
        var fullContent = new StringBuilder();
        var fullReasoning = new StringBuilder();
        var toolCalls = new StreamingToolCallAccumulator();
        var contentParts = new List<ContentPart>();
        IReadOnlyDictionary<string, string>? metadata = null;
        if (request.Headers.Count > 0 || request.Metadata.Count > 0)
        {
            var merged = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var kv in request.Headers) merged[kv.Key] = kv.Value;
            // Metadata takes precedence (contains NyxID token, model override, etc.)
            foreach (var kv in request.Metadata) merged[kv.Key] = kv.Value;
            metadata = merged;
        }
        var inputParts = ResolveRequestInputParts(request);

        await foreach (var chunk in ChatStreamAsync(inputParts, request.SessionId, metadata, streamCt))
        {
            if (!string.IsNullOrEmpty(chunk.DeltaContent))
            {
                fullContent.Append(chunk.DeltaContent);
                await PublishAsync(new TextMessageContentEvent
                {
                    Delta = chunk.DeltaContent,
                    SessionId = request.SessionId,
                }, TopologyAudience.Parent);
            }

            if (chunk.DeltaContentPart != null)
            {
                contentParts.Add(chunk.DeltaContentPart);
                await PublishAsync(new MediaContentEvent
                {
                    SessionId = request.SessionId,
                    AgentId = Id,
                    Part = ContentPartProtoMapper.ToProto(chunk.DeltaContentPart),
                }, TopologyAudience.Parent);
            }

            if (!string.IsNullOrEmpty(chunk.DeltaReasoningContent))
            {
                fullReasoning.Append(chunk.DeltaReasoningContent);
                await PublishAsync(new TextMessageReasoningEvent
                {
                    Delta = chunk.DeltaReasoningContent,
                    SessionId = request.SessionId,
                }, TopologyAudience.Parent);
            }

            if (chunk.DeltaToolCall != null)
                toolCalls.TrackDelta(chunk.DeltaToolCall);
        }

        var appendedHistoryMessages = History.Messages
            .Skip(Math.Min(initialHistoryCount, History.Count))
            .ToArray();

        foreach (var toolCall in toolCalls.BuildToolCalls())
        {
            await PublishAsync(new ToolCallEvent
            {
                CallId = toolCall.Id,
                ToolName = toolCall.Name,
                ArgumentsJson = toolCall.ArgumentsJson,
            }, TopologyAudience.Parent);
        }

        foreach (var toolResult in appendedHistoryMessages)
        {
            if (!string.Equals(toolResult.Role, "tool", StringComparison.Ordinal) ||
                string.IsNullOrWhiteSpace(toolResult.ToolCallId))
            {
                continue;
            }

            var (success, error) = ClassifyToolResult(toolResult.Content);
            await PublishAsync(new ToolResultEvent
            {
                CallId = toolResult.ToolCallId,
                ResultJson = toolResult.Content ?? string.Empty,
                Success = success,
                Error = error ?? string.Empty,
            }, TopologyAudience.Parent);
        }

        var response = fullContent.ToString();
        var responsePreview = response.Length > 300
            ? response[..300] + "..."
            : response;
        Logger.LogInformation(
            "[{Role}] LLM response ({Len} chars): {Preview}",
            RoleName,
            response.Length,
            responsePreview);

        if (fullReasoning.Length > 0)
        {
            var reasoning = fullReasoning.ToString();
            var reasoningPreview = reasoning.Length > 300
                ? reasoning[..300] + "..."
                : reasoning;
            Logger.LogInformation(
                "[{Role}] LLM reasoning ({Len} chars): {Preview}",
                RoleName,
                reasoning.Length,
                reasoningPreview);
        }

        return new SessionReplayRecord(
            response,
            fullReasoning.ToString(),
            toolCalls.BuildToolCalls(),
            contentParts,
            ContentEmitted: fullContent.Length > 0);
    }

    private Task PersistSessionCompletionAsync(ChatRequestEvent request, SessionReplayRecord replayRecord)
    {
        if (string.IsNullOrWhiteSpace(request.SessionId))
            return Task.CompletedTask;

        return PersistDomainEventAsync(new RoleChatSessionCompletedEvent
        {
            SessionId = request.SessionId,
            Content = replayRecord.Content,
            ReasoningContent = replayRecord.ReasoningContent,
            Prompt = request.Prompt,
            ContentEmitted = replayRecord.ContentEmitted,
            ToolCalls = { ToToolCallEvents(replayRecord.ToolCalls) },
            OutputParts = { ContentPartProtoMapper.ToProtoList(replayRecord.ContentParts) },
        });
    }

    private async Task ReplayCompletedSessionAsync(string sessionId, RoleChatSessionState trackedSession)
    {
        await PublishAsync(new TextMessageStartEvent
        {
            SessionId = sessionId,
            AgentId = Id,
        }, TopologyAudience.Parent);

        if (trackedSession.ContentEmitted && !string.IsNullOrEmpty(trackedSession.FinalContent))
        {
            await PublishAsync(new TextMessageContentEvent
            {
                Delta = trackedSession.FinalContent,
                SessionId = sessionId,
            }, TopologyAudience.Parent);
        }

        if (!string.IsNullOrEmpty(trackedSession.FinalReasoningContent))
        {
            await PublishAsync(new TextMessageReasoningEvent
            {
                Delta = trackedSession.FinalReasoningContent,
                SessionId = sessionId,
            }, TopologyAudience.Parent);
        }

        foreach (var toolCall in trackedSession.ToolCalls)
        {
            await PublishAsync(new ToolCallEvent
            {
                CallId = toolCall.CallId,
                ToolName = toolCall.ToolName,
                ArgumentsJson = toolCall.ArgumentsJson,
            }, TopologyAudience.Parent);
        }

        foreach (var contentPart in trackedSession.OutputParts)
        {
            await PublishAsync(new MediaContentEvent
            {
                SessionId = sessionId,
                AgentId = Id,
                Part = contentPart.Clone(),
            }, TopologyAudience.Parent);
        }

        await PublishCompletionAsync(sessionId, trackedSession.FinalContent);
    }

    private Task PublishCompletionAsync(string sessionId, string completionContent) =>
        PublishAsync(
            new TextMessageEndEvent
            {
                Content = completionContent,
                SessionId = sessionId,
            },
            TopologyAudience.Parent);

    private RoleChatSessionState? ResolveTrackedSession(ChatRequestEvent request)
    {
        if (string.IsNullOrWhiteSpace(request.SessionId))
            return null;

        if (!State.Sessions.TryGetValue(request.SessionId, out var trackedSession))
            return null;

        if (!string.Equals(trackedSession.Prompt, request.Prompt, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Session '{request.SessionId}' already exists with a different prompt.");
        }

        if (!HaveMatchingInputParts(trackedSession.InputParts, request.InputParts))
        {
            throw new InvalidOperationException(
                $"Session '{request.SessionId}' already exists with different multimodal input.");
        }

        return trackedSession;
    }

    private static RoleGAgentState ApplyInitializeRoleAgent(
        RoleGAgentState current,
        InitializeRoleAgentEvent evt)
    {
        var next = current.Clone();
        var overrides = EnsureConfigOverrides(next);
        next.RoleName = evt.RoleName ?? string.Empty;
        next.EventModules = NormalizeModuleExtensionText(evt.EventModules);
        next.EventRoutes = NormalizeModuleExtensionText(evt.EventRoutes);
        overrides.ProviderName = string.IsNullOrWhiteSpace(evt.ProviderName) ? string.Empty : evt.ProviderName.Trim();
        overrides.Model = string.IsNullOrWhiteSpace(evt.Model) ? string.Empty : evt.Model.Trim();
        overrides.SystemPrompt = evt.SystemPrompt ?? string.Empty;
        if (evt.HasTemperature)
            overrides.Temperature = evt.Temperature;
        else
            overrides.ClearTemperature();
        if (evt.MaxTokens > 0)
            overrides.MaxTokens = evt.MaxTokens;
        else
            overrides.ClearMaxTokens();
        if (evt.MaxToolRounds > 0)
            overrides.MaxToolRounds = evt.MaxToolRounds;
        else
            overrides.ClearMaxToolRounds();
        if (evt.MaxHistoryMessages > 0)
            overrides.MaxHistoryMessages = evt.MaxHistoryMessages;
        else
            overrides.ClearMaxHistoryMessages();
        if (evt.StreamBufferCapacity > 0)
            overrides.StreamBufferCapacity = evt.StreamBufferCapacity;
        else
            overrides.ClearStreamBufferCapacity();
        if (evt.MaxPromptTokenBudget > 0)
            overrides.MaxPromptTokenBudget = evt.MaxPromptTokenBudget;
        else
            overrides.ClearMaxPromptTokenBudget();
        if (evt.CompressionThreshold > 0)
            overrides.CompressionThreshold = evt.CompressionThreshold;
        else
            overrides.ClearCompressionThreshold();
        if (evt.EnableSummarization)
            overrides.EnableSummarization = true;
        else
            overrides.ClearEnableSummarization();
        return next;
    }

    private static RoleGAgentState ApplyChatSessionStarted(
        RoleGAgentState current,
        RoleChatSessionStartedEvent evt)
    {
        if (string.IsNullOrWhiteSpace(evt.SessionId))
            return current;

        var next = current.Clone();
        var sessions = next.Sessions;
        if (!sessions.TryGetValue(evt.SessionId, out var session))
        {
            session = new RoleChatSessionState();
            next.MessageCount++;
            session.Sequence = next.MessageCount;
        }
        else if (session.Sequence <= 0)
        {
            next.MessageCount++;
            session.Sequence = next.MessageCount;
        }

        session.Prompt = evt.Prompt ?? string.Empty;
        session.InputParts.Clear();
        session.InputParts.Add(evt.InputParts);
        sessions[evt.SessionId] = session;
        TrimTrackedSessions(next);
        return next;
    }

    private static RoleGAgentState ApplyChatSessionCompleted(
        RoleGAgentState current,
        RoleChatSessionCompletedEvent evt)
    {
        if (string.IsNullOrWhiteSpace(evt.SessionId))
            return current;

        var next = current.Clone();
        if (!next.Sessions.TryGetValue(evt.SessionId, out var session))
        {
            session = new RoleChatSessionState();
            next.MessageCount++;
            session.Sequence = next.MessageCount;
        }
        else if (session.Sequence <= 0)
        {
            next.MessageCount++;
            session.Sequence = next.MessageCount;
        }

        session.Completed = true;
        session.Prompt = evt.Prompt ?? session.Prompt ?? string.Empty;
        session.FinalContent = evt.Content ?? string.Empty;
        session.FinalReasoningContent = evt.ReasoningContent ?? string.Empty;
        session.ContentEmitted = evt.ContentEmitted;
        session.ToolCalls.Clear();
        session.ToolCalls.Add(evt.ToolCalls);
        session.OutputParts.Clear();
        session.OutputParts.Add(evt.OutputParts);
        next.Sessions[evt.SessionId] = session;
        TrimTrackedSessions(next);
        return next;
    }

    private static IEnumerable<ToolCallEvent> ToToolCallEvents(IEnumerable<ToolCall> toolCalls)
    {
        foreach (var toolCall in toolCalls)
        {
            yield return new ToolCallEvent
            {
                CallId = toolCall.Id,
                ToolName = toolCall.Name,
                ArgumentsJson = toolCall.ArgumentsJson,
            };
        }
    }

    private static (bool Success, string? Error) ClassifyToolResult(string? resultJson)
    {
        if (string.IsNullOrWhiteSpace(resultJson))
            return (true, null);

        try
        {
            using var doc = JsonDocument.Parse(resultJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object ||
                !doc.RootElement.TryGetProperty("error", out var errorElement))
            {
                return (true, null);
            }

            var error = errorElement.ValueKind switch
            {
                JsonValueKind.String => errorElement.GetString(),
                JsonValueKind.Null or JsonValueKind.Undefined => null,
                _ => errorElement.ToString(),
            };
            return string.IsNullOrWhiteSpace(error)
                ? (true, null)
                : (false, error.Trim());
        }
        catch (JsonException)
        {
            return (true, null);
        }
    }

    private static void TrimTrackedSessions(RoleGAgentState state)
    {
        if (state.Sessions.Count <= MaxTrackedSessions)
            return;

        while (state.Sessions.Count > MaxTrackedSessions)
        {
            string? oldestSessionId = null;
            long oldestSequence = long.MaxValue;

            foreach (var session in state.Sessions)
            {
                var sequence = session.Value.Sequence <= 0 ? long.MinValue : session.Value.Sequence;
                if (sequence < oldestSequence)
                {
                    oldestSequence = sequence;
                    oldestSessionId = session.Key;
                }
            }

            if (string.IsNullOrWhiteSpace(oldestSessionId))
                break;

            state.Sessions.Remove(oldestSessionId);
        }
    }

    private static AIAgentConfigOverrides EnsureConfigOverrides(RoleGAgentState state)
    {
        if (state.ConfigOverrides == null)
            state.ConfigOverrides = new AIAgentConfigOverrides();
        return state.ConfigOverrides;
    }

    private void ApplyModuleExtensionsFromStateIfNeeded(RoleGAgentState state)
    {
        var eventModules = NormalizeModuleExtensionText(state.EventModules);
        var eventRoutes = NormalizeModuleExtensionText(state.EventRoutes);
        if (string.Equals(_appliedEventModules, eventModules, StringComparison.Ordinal) &&
            string.Equals(_appliedEventRoutes, eventRoutes, StringComparison.Ordinal) &&
            ReferenceEquals(_appliedModuleServices, Services))
        {
            return;
        }

        if (string.IsNullOrEmpty(eventModules))
        {
            SetModules([]);
        }
        else
        {
            RoleGAgentFactory.ApplyModuleExtensions(this, eventModules, eventRoutes, Services);
        }

        _appliedEventModules = eventModules;
        _appliedEventRoutes = eventRoutes;
        _appliedModuleServices = Services;
    }

    private static string NormalizeModuleExtensionText(string? value) =>
        string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();

    private static IReadOnlyList<ContentPart> ResolveRequestInputParts(ChatRequestEvent request)
    {
        if (request.InputParts.Count > 0)
        {
            var parts = new List<ContentPart>();
            // Include the text prompt as a TextPart alongside media parts
            if (!string.IsNullOrWhiteSpace(request.Prompt))
                parts.Add(ContentPart.TextPart(request.Prompt));
            parts.AddRange(ContentPartProtoMapper.FromProtoList(request.InputParts));
            return parts;
        }

        return [ContentPart.TextPart(request.Prompt ?? string.Empty)];
    }

    private static string BuildRequestPreview(ChatRequestEvent request)
    {
        var previewSource = string.IsNullOrWhiteSpace(request.Prompt)
            ? string.Join(", ", ResolveRequestInputParts(request).Select(part => part.Kind.ToString().ToLowerInvariant()))
            : request.Prompt;

        return previewSource.Length > 200
            ? previewSource[..200] + "..."
            : previewSource;
    }

    private static bool HaveMatchingInputParts(
        Google.Protobuf.Collections.RepeatedField<ChatContentPart> existing,
        Google.Protobuf.Collections.RepeatedField<ChatContentPart> incoming)
    {
        if (existing.Count != incoming.Count)
            return false;

        for (var i = 0; i < existing.Count; i++)
        {
            if (!existing[i].Equals(incoming[i]))
                return false;
        }

        return true;
    }

    private sealed record SessionReplayRecord(
        string Content,
        string ReasoningContent,
        IReadOnlyList<ToolCall> ToolCalls,
        IReadOnlyList<ContentPart> ContentParts,
        bool ContentEmitted)
    {
        public static SessionReplayRecord FromFailure(string content) =>
            new(content, string.Empty, [], [], ContentEmitted: false);
    }

}
