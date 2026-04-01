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
        IEnumerable<IAgentToolSource>? toolSources = null)
        : base(
            llmProviderFactory,
            additionalHooks,
            agentMiddlewares,
            toolMiddlewares,
            llmMiddlewares,
            toolSources)
    {
    }

    /// <summary>Role name.</summary>
    public string RoleName { get; private set; } = "";

    [EventHandler]
    public async Task HandleInitializeRoleAgent(InitializeRoleAgentEvent evt)
    {
        await PersistDomainEventAsync(evt);
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
    [EventHandler]
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
            });
        }
        else if (trackedSession != null)
        {
            Logger.LogInformation(
                "[{Role}] Resuming incomplete LLM session={SessionId}",
                RoleName,
                request.SessionId);
        }

        var promptPreview = request.Prompt.Length > 200
            ? request.Prompt[..200] + "..."
            : request.Prompt;
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
            replayRecord = SessionReplayRecord.FromFailure(
                useWorkflowFailureMarker
                    ? BuildLlmFailureContent(ex.Message)
                    : $"LLM request failed: {SanitizeFailureMessage(ex.Message)}");
        }

        await PersistSessionCompletionAsync(request, replayRecord);
        await PublishCompletionAsync(request.SessionId, replayRecord.Content);
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
        IReadOnlyDictionary<string, string>? metadata = request.Metadata.Count == 0
            ? null
            : new Dictionary<string, string>(request.Metadata, StringComparer.Ordinal);

        await foreach (var chunk in ChatStreamAsync(request.Prompt, request.SessionId, metadata, streamCt))
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

    private sealed record SessionReplayRecord(
        string Content,
        string ReasoningContent,
        IReadOnlyList<ToolCall> ToolCalls,
        bool ContentEmitted)
    {
        public static SessionReplayRecord FromFailure(string content) =>
            new(content, string.Empty, [], ContentEmitted: false);
    }

}
