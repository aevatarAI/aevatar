// ─────────────────────────────────────────────────────────────
// RoleGAgent - role-based AI GAgent.
//
// Handles ChatRequestEvent:
// 1. Calls LLM via ChatStreamAsync (streaming)
// 2. Publishes AG-UI events: TextMessageStart → Content* → ToolCall* → End
// 3. Logs prompt and full LLM response for observability
// ─────────────────────────────────────────────────────────────

using System.Text;
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
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging;
using System.Globalization;

namespace Aevatar.AI.Core;

/// <summary>
/// Role-based AI GAgent. Receives ChatRequestEvent and streams LLM response.
/// </summary>
public class RoleGAgent : AIGAgentBase<RoleGAgentState>, IRoleAgent
{
    private const string LlmTimeoutMetadataKey = "aevatar.llm_timeout_ms";
    private const string LlmFailureContentPrefix = "[[AEVATAR_LLM_ERROR]]";

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

    /// <summary>Sets role name.</summary>
    public void SetRoleName(string name) => RoleName = name;

    Task IRoleAgent.ConfigureAsync(RoleAgentConfig config, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(config);
        ct.ThrowIfCancellationRequested();

        var evt = new ConfigureRoleAgentEvent
        {
            RoleName = RoleName,
            ProviderName = config.ProviderName,
            Model = config.Model ?? string.Empty,
            SystemPrompt = config.SystemPrompt,
            MaxTokens = config.MaxTokens ?? 0,
            MaxToolRounds = config.MaxToolRounds,
            MaxHistoryMessages = config.MaxHistoryMessages,
            StreamBufferCapacity = config.StreamBufferCapacity,
            AppConfigJson = CurrentAppConfigJson(),
            AppConfigCodec = CurrentAppConfigCodec(),
            AppConfigSchemaVersion = CurrentAppConfigSchemaVersion(),
        };
        if (config.Temperature.HasValue)
            evt.Temperature = config.Temperature.Value;
        return HandleConfigureRoleAgent(evt);
    }

    [EventHandler]
    public async Task HandleConfigureRoleAgent(ConfigureRoleAgentEvent evt)
    {
        _ = NormalizeAppConfigCodecOrThrow(evt.AppConfigCodec);
        await PersistDomainEventAsync(evt);
        RoleGAgentFactory.ApplyModuleExtensions(this, evt.EventModules, evt.EventRoutes, Services);
    }

    [EventHandler]
    public async Task HandleSetRoleAppConfig(SetRoleAppConfigEvent evt)
    {
        _ = NormalizeAppConfigCodecOrThrow(evt.AppConfigCodec);
        await PersistDomainEventAsync(evt);
    }

    [EventHandler]
    public Task HandleSetRoleAppState(SetRoleAppStateEvent evt)
    {
        _ = NormalizeAppStateCodecOrThrow(evt.AppStateCodec);
        return PersistDomainEventAsync(evt);
    }

    /// <summary>Returns agent description.</summary>
    public override Task<string> GetDescriptionAsync() =>
        Task.FromResult($"RoleGAgent[{RoleName}]:{Id}");

    protected override RoleGAgentState TransitionState(RoleGAgentState current, IMessage evt) =>
        StateTransitionMatcher
            .Match(current, evt)
            .On<ConfigureRoleAgentEvent>(ApplyConfigureRoleAgent)
            .On<SetRoleAppConfigEvent>(ApplySetRoleAppConfig)
            .On<SetRoleAppStateEvent>(ApplySetRoleAppState)
            .OrCurrent();

    protected override Task OnStateChangedAfterConfigAppliedAsync(RoleGAgentState state, CancellationToken ct)
    {
        _ = ct;
        RoleName = state.RoleName ?? string.Empty;
        return Task.CompletedTask;
    }

    protected override AIAgentConfigStateOverrides ExtractStateConfigOverrides(RoleGAgentState state)
    {
        var overrides = state.ConfigOverrides;
        if (overrides == null)
            return new AIAgentConfigStateOverrides();

        var hasAppConfig = HasAppConfigSnapshot(overrides);
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
            HasAppConfigJson = hasAppConfig,
            AppConfigJson = hasAppConfig ? overrides.AppConfigJson : null,
            HasAppConfigCodec = hasAppConfig,
            AppConfigCodec = hasAppConfig ? NormalizeAppConfigCodecOrThrow(overrides.AppConfigCodec) : null,
            HasAppConfigSchemaVersion = hasAppConfig,
            AppConfigSchemaVersion = hasAppConfig ? overrides.AppConfigSchemaVersion : null,
        };
    }

    /// <summary>
    /// Handles ChatRequestEvent via streaming LLM call.
    /// Publishes text stream events and tool call events.
    /// </summary>
    [EventHandler]
    public async Task HandleChatRequest(ChatRequestEvent request)
    {
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
        }, EventDirection.Up);

        try
        {
            // ─── AG-UI: TEXT_MESSAGE_CONTENT — streaming chunks ───
            var fullContent = new StringBuilder();
            var fullReasoning = new StringBuilder();
            var toolCalls = new StreamingToolCallAccumulator();

            await foreach (var chunk in ChatStreamAsync(request.Prompt, streamCt))
            {
                if (!string.IsNullOrEmpty(chunk.DeltaContent))
                {
                    fullContent.Append(chunk.DeltaContent);
                    await PublishAsync(new TextMessageContentEvent
                    {
                        Delta = chunk.DeltaContent,
                        SessionId = request.SessionId,
                    }, EventDirection.Up);
                }

                if (!string.IsNullOrEmpty(chunk.DeltaReasoningContent))
                {
                    fullReasoning.Append(chunk.DeltaReasoningContent);
                    await PublishAsync(new TextMessageReasoningEvent
                    {
                        Delta = chunk.DeltaReasoningContent,
                        SessionId = request.SessionId,
                    }, EventDirection.Up);
                }

                if (chunk.DeltaToolCall != null)
                    toolCalls.TrackDelta(chunk.DeltaToolCall);
            }

            foreach (var toolCall in toolCalls.BuildToolCalls())
            {
                await PublishAsync(new ToolCallEvent
                {
                    CallId = toolCall.Id,
                    ToolName = toolCall.Name,
                    ArgumentsJson = toolCall.ArgumentsJson,
                }, EventDirection.Up);
            }

            var response = fullContent.ToString();
            var responsePreview = response.Length > 300
                ? response[..300] + "..."
                : response;
            Logger.LogInformation("[{Role}] LLM response ({Len} chars): {Preview}",
                RoleName, response.Length, responsePreview);

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

            // ─── AG-UI: TEXT_MESSAGE_END ───
            await PublishAsync(new TextMessageEndEvent
            {
                Content = response,
                SessionId = request.SessionId,
            }, EventDirection.Up);
        }
        catch (OperationCanceledException) when (timeoutCts is { IsCancellationRequested: true })
        {
            Logger.LogWarning(
                "[{Role}] LLM request timeout after {TimeoutMs}ms. session={SessionId}",
                RoleName,
                timeoutMs,
                request.SessionId);
            await PublishAsync(new TextMessageEndEvent
            {
                Content = BuildLlmFailureContent($"LLM request timed out after {timeoutMs}ms"),
                SessionId = request.SessionId,
            }, EventDirection.Up);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "[{Role}] LLM request failed. session={SessionId}", RoleName, request.SessionId);
            await PublishAsync(new TextMessageEndEvent
            {
                Content = useWorkflowFailureMarker
                    ? BuildLlmFailureContent(ex.Message)
                    : $"LLM request failed: {SanitizeFailureMessage(ex.Message)}",
                SessionId = request.SessionId,
            }, EventDirection.Up);
        }
    }

    private static int ResolveLlmTimeoutMs(ChatRequestEvent request)
    {
        if (request.Metadata.TryGetValue(LlmTimeoutMetadataKey, out var raw) &&
            int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var timeoutMs) &&
            timeoutMs > 0)
        {
            return timeoutMs;
        }

        return 0;
    }

    private static string BuildLlmFailureContent(string? message)
    {
        var safeMessage = SanitizeFailureMessage(message);
        return $"{LlmFailureContentPrefix} {safeMessage}";
    }

    private static string SanitizeFailureMessage(string? message) =>
        string.IsNullOrWhiteSpace(message) ? "LLM request failed." : message.Trim();

    private static RoleGAgentState ApplyConfigureRoleAgent(
        RoleGAgentState current,
        ConfigureRoleAgentEvent evt)
    {
        var next = current.Clone();
        var overrides = EnsureConfigOverrides(next);
        next.RoleName = evt.RoleName ?? string.Empty;
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
        overrides.AppConfigJson = evt.AppConfigJson ?? string.Empty;
        overrides.AppConfigCodec = NormalizeAppConfigCodecOrThrow(evt.AppConfigCodec);
        overrides.AppConfigSchemaVersion = evt.AppConfigSchemaVersion;
        return next;
    }

    private static RoleGAgentState ApplySetRoleAppState(
        RoleGAgentState current,
        SetRoleAppStateEvent evt)
    {
        var next = current.Clone();
        next.AppState = evt.AppState?.Clone() ?? new Any();
        next.AppStateCodec = NormalizeAppStateCodecOrThrow(evt.AppStateCodec);
        next.AppStateSchemaVersion = evt.AppStateSchemaVersion;
        return next;
    }

    private static RoleGAgentState ApplySetRoleAppConfig(
        RoleGAgentState current,
        SetRoleAppConfigEvent evt)
    {
        var next = current.Clone();
        var overrides = EnsureConfigOverrides(next);
        overrides.AppConfigJson = evt.AppConfigJson ?? string.Empty;
        overrides.AppConfigCodec = NormalizeAppConfigCodecOrThrow(evt.AppConfigCodec);
        overrides.AppConfigSchemaVersion = evt.AppConfigSchemaVersion;
        return next;
    }

    private static bool HasAppConfigSnapshot(AIAgentConfigOverrides overrides) =>
        overrides.HasAppConfigCodec ||
        overrides.HasAppConfigJson ||
        overrides.HasAppConfigSchemaVersion;

    private static string NormalizeAppStateCodecOrThrow(string? codec)
    {
        if (string.IsNullOrWhiteSpace(codec))
            return RoleGAgentExtensionContract.AppStateCodecProtobufAny;

        if (string.Equals(codec, RoleGAgentExtensionContract.AppStateCodecProtobufAny, StringComparison.OrdinalIgnoreCase))
            return RoleGAgentExtensionContract.AppStateCodecProtobufAny;

        throw new InvalidOperationException(
            $"Unsupported app state codec '{codec}'. Supported codec: '{RoleGAgentExtensionContract.AppStateCodecProtobufAny}'.");
    }

    private static string NormalizeAppConfigCodecOrThrow(string? codec)
    {
        if (string.IsNullOrWhiteSpace(codec))
            return RoleGAgentExtensionContract.AppConfigCodecJsonPlain;

        if (string.Equals(codec, RoleGAgentExtensionContract.AppConfigCodecJsonPlain, StringComparison.OrdinalIgnoreCase))
            return RoleGAgentExtensionContract.AppConfigCodecJsonPlain;

        throw new InvalidOperationException(
            $"Unsupported app config codec '{codec}'. Supported codec: '{RoleGAgentExtensionContract.AppConfigCodecJsonPlain}'.");
    }

    private string CurrentAppConfigJson()
    {
        var overrides = State.ConfigOverrides;
        if (overrides == null || !overrides.HasAppConfigJson)
            return string.Empty;
        return overrides.AppConfigJson ?? string.Empty;
    }

    private string CurrentAppConfigCodec()
    {
        var overrides = State.ConfigOverrides;
        if (overrides == null || !overrides.HasAppConfigCodec)
            return string.Empty;
        return overrides.AppConfigCodec ?? string.Empty;
    }

    private int CurrentAppConfigSchemaVersion()
    {
        var overrides = State.ConfigOverrides;
        if (overrides == null || !overrides.HasAppConfigSchemaVersion)
            return 0;
        return overrides.AppConfigSchemaVersion;
    }

    private static AIAgentConfigOverrides EnsureConfigOverrides(RoleGAgentState state)
    {
        if (state.ConfigOverrides == null)
            state.ConfigOverrides = new AIAgentConfigOverrides();
        return state.ConfigOverrides;
    }

}
