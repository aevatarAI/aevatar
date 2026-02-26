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

namespace Aevatar.AI.Core;

/// <summary>
/// Role-based AI GAgent. Receives ChatRequestEvent and streams LLM response.
/// </summary>
public class RoleGAgent : AIGAgentBase<RoleGAgentState>, IRoleAgent
{
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

    Task IRoleAgent.ConfigureAsync(RoleAgentConfig config, CancellationToken ct) =>
        ConfigureAsync(new AIAgentConfig
        {
            ProviderName = config.ProviderName,
            Model = config.Model,
            SystemPrompt = config.SystemPrompt,
            Temperature = config.Temperature,
            MaxTokens = config.MaxTokens,
            MaxToolRounds = config.MaxToolRounds,
            MaxHistoryMessages = config.MaxHistoryMessages,
            StreamBufferCapacity = config.StreamBufferCapacity,
        }, ct);

    [EventHandler]
    public async Task HandleConfigureRoleAgent(ConfigureRoleAgentEvent evt)
    {
        var appConfigCodec = NormalizeAppConfigCodecOrThrow(evt.AppConfigCodec);
        await PersistDomainEventAsync(evt);
        await ConfigureAsync(new AIAgentConfig
        {
            ProviderName = string.IsNullOrWhiteSpace(evt.ProviderName) ? string.Empty : evt.ProviderName,
            Model = string.IsNullOrWhiteSpace(evt.Model) ? null : evt.Model,
            SystemPrompt = evt.SystemPrompt ?? string.Empty,
            Temperature = evt.HasTemperature ? evt.Temperature : null,
            MaxTokens = evt.MaxTokens == 0 ? null : evt.MaxTokens,
            MaxToolRounds = evt.MaxToolRounds <= 0 ? 10 : evt.MaxToolRounds,
            MaxHistoryMessages = evt.MaxHistoryMessages <= 0 ? 100 : evt.MaxHistoryMessages,
            StreamBufferCapacity = evt.StreamBufferCapacity <= 0 ? 256 : evt.StreamBufferCapacity,
            AppConfigJson = evt.AppConfigJson ?? string.Empty,
            AppConfigCodec = appConfigCodec,
            AppConfigSchemaVersion = evt.AppConfigSchemaVersion,
        });

        RoleGAgentFactory.ApplyModuleExtensions(this, evt.EventModules, evt.EventRoutes, Services);
    }

    [EventHandler]
    public async Task HandleSetRoleAppConfig(SetRoleAppConfigEvent evt)
    {
        var appConfigCodec = NormalizeAppConfigCodecOrThrow(evt.AppConfigCodec);
        await PersistDomainEventAsync(evt);
        await ConfigureAsync(new AIAgentConfig
        {
            ProviderName = Config.ProviderName,
            Model = Config.Model,
            SystemPrompt = Config.SystemPrompt,
            Temperature = Config.Temperature,
            MaxTokens = Config.MaxTokens,
            MaxToolRounds = Config.MaxToolRounds,
            MaxHistoryMessages = Config.MaxHistoryMessages,
            StreamBufferCapacity = Config.StreamBufferCapacity,
            AppConfigJson = evt.AppConfigJson ?? string.Empty,
            AppConfigCodec = appConfigCodec,
            AppConfigSchemaVersion = evt.AppConfigSchemaVersion,
        });
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

    protected override Task OnStateChangedAsync(RoleGAgentState state, CancellationToken ct)
    {
        _ = ct;
        RoleName = state.RoleName ?? string.Empty;
        if (HasAppConfigSnapshot(state))
        {
            Config.AppConfigJson = state.AppConfigJson ?? string.Empty;
            Config.AppConfigCodec = NormalizeAppConfigCodecOrThrow(state.AppConfigCodec);
            Config.AppConfigSchemaVersion = state.AppConfigSchemaVersion;
        }
        return Task.CompletedTask;
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

        // ─── AG-UI: TEXT_MESSAGE_START ───
        await PublishAsync(new TextMessageStartEvent
        {
            SessionId = request.SessionId,
            AgentId = Id,
        }, EventDirection.Up);

        // ─── AG-UI: TEXT_MESSAGE_CONTENT — streaming chunks ───
        var fullContent = new StringBuilder();
        var toolCalls = new StreamingToolCallAccumulator();

        await foreach (var chunk in ChatStreamAsync(request.Prompt))
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

        // ─── AG-UI: TEXT_MESSAGE_END ───
        await PublishAsync(new TextMessageEndEvent
        {
            Content = response,
            SessionId = request.SessionId,
        }, EventDirection.Up);
    }

    private static RoleGAgentState ApplyConfigureRoleAgent(
        RoleGAgentState current,
        ConfigureRoleAgentEvent evt)
    {
        var next = current.Clone();
        next.RoleName = evt.RoleName ?? string.Empty;
        next.AppConfigJson = evt.AppConfigJson ?? string.Empty;
        next.AppConfigCodec = NormalizeAppConfigCodecOrThrow(evt.AppConfigCodec);
        next.AppConfigSchemaVersion = evt.AppConfigSchemaVersion;
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
        next.AppConfigJson = evt.AppConfigJson ?? string.Empty;
        next.AppConfigCodec = NormalizeAppConfigCodecOrThrow(evt.AppConfigCodec);
        next.AppConfigSchemaVersion = evt.AppConfigSchemaVersion;
        return next;
    }

    private static bool HasAppConfigSnapshot(RoleGAgentState state) =>
        !string.IsNullOrWhiteSpace(state.AppConfigCodec) ||
        !string.IsNullOrEmpty(state.AppConfigJson) ||
        state.AppConfigSchemaVersion != 0;

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
}
