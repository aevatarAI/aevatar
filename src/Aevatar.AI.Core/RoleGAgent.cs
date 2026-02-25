// ─────────────────────────────────────────────────────────────
// RoleGAgent - role-based AI GAgent.
//
// Handles ChatRequestEvent:
// 1. Calls LLM via ChatStreamAsync (streaming)
// 2. Publishes AG-UI events: TextMessageStart → Content* → End
// 3. Logs prompt and full LLM response for observability
// ─────────────────────────────────────────────────────────────

using System.Text;
using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.Agents;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.Middleware;
using Aevatar.AI.Abstractions.ToolProviders;
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
        await PersistDomainEventAsync(evt);
        await ((IRoleAgent)this).ConfigureAsync(new RoleAgentConfig
        {
            ProviderName = string.IsNullOrWhiteSpace(evt.ProviderName) ? "deepseek" : evt.ProviderName,
            Model = string.IsNullOrWhiteSpace(evt.Model) ? null : evt.Model,
            SystemPrompt = evt.SystemPrompt ?? string.Empty,
            Temperature = evt.HasTemperature ? evt.Temperature : null,
            MaxTokens = evt.MaxTokens == 0 ? null : evt.MaxTokens,
            MaxToolRounds = evt.MaxToolRounds <= 0 ? 10 : evt.MaxToolRounds,
            MaxHistoryMessages = evt.MaxHistoryMessages <= 0 ? 100 : evt.MaxHistoryMessages,
            StreamBufferCapacity = evt.StreamBufferCapacity <= 0 ? 256 : evt.StreamBufferCapacity,
        });
    }

    /// <summary>Returns agent description.</summary>
    public override Task<string> GetDescriptionAsync() =>
        Task.FromResult($"RoleGAgent[{RoleName}]:{Id}");

    protected override RoleGAgentState TransitionState(RoleGAgentState current, IMessage evt) =>
        StateTransitionMatcher
            .Match(current, evt)
            .On<ConfigureRoleAgentEvent>(ApplyConfigureRoleAgent)
            .OrCurrent();

    protected override Task OnStateChangedAsync(RoleGAgentState state, CancellationToken ct)
    {
        _ = ct;
        RoleName = state.RoleName ?? string.Empty;
        return Task.CompletedTask;
    }

    /// <summary>
    /// Handles ChatRequestEvent via streaming LLM call.
    /// Publishes AG-UI three-phase events and logs the interaction.
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
        await foreach (var chunk in ChatStreamAsync(request.Prompt))
        {
            fullContent.Append(chunk);
            await PublishAsync(new TextMessageContentEvent
            {
                Delta = chunk,
                SessionId = request.SessionId,
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
        return next;
    }
}
