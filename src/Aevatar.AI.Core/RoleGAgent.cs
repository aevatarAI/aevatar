// ─────────────────────────────────────────────────────────────
// RoleGAgent - role-based AI GAgent.
//
// Handles ChatRequestEvent:
// 1. Calls LLM via ChatAsync with onContent callback (streaming + tool calling)
// 2. Publishes AG-UI events: TextMessageStart → Content* → ToolCall* → End
// 3. Logs prompt and full LLM response for observability
// ─────────────────────────────────────────────────────────────

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
        SetRoleName(evt.RoleName);
        await ((IRoleAgent)this).ConfigureAsync(new RoleAgentConfig
        {
            ProviderName = string.IsNullOrWhiteSpace(evt.ProviderName) ? string.Empty : evt.ProviderName,
            Model = string.IsNullOrWhiteSpace(evt.Model) ? null : evt.Model,
            SystemPrompt = evt.SystemPrompt ?? string.Empty,
            Temperature = evt.HasTemperature ? evt.Temperature : null,
            MaxTokens = evt.MaxTokens == 0 ? null : evt.MaxTokens,
            MaxToolRounds = evt.MaxToolRounds <= 0 ? 10 : evt.MaxToolRounds,
            MaxHistoryMessages = evt.MaxHistoryMessages <= 0 ? 100 : evt.MaxHistoryMessages,
            StreamBufferCapacity = evt.StreamBufferCapacity <= 0 ? 256 : evt.StreamBufferCapacity,
        });

        RoleGAgentFactory.ApplyModuleExtensions(this, evt.EventModules, evt.EventRoutes, Services);
    }

    /// <summary>Returns agent description.</summary>
    public override Task<string> GetDescriptionAsync() =>
        Task.FromResult($"RoleGAgent[{RoleName}]:{Id}");

    /// <summary>
    /// Handles ChatRequestEvent via non-streaming LLM call with tool-calling loop.
    /// Publishes AG-UI three-phase events with real-time intermediate text streaming.
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

        // ChatAsync with onContent callback: streams LLM tokens in real-time.
        // Each callback invocation delivers a small text delta (token-level granularity).
        var response = await ChatAsync(request.Prompt, async (content, _) =>
        {
            await PublishAsync(new TextMessageContentEvent
            {
                Delta = content,
                SessionId = request.SessionId,
            }, EventDirection.Up);
        }) ?? "";

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
}
