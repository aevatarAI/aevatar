using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.Middleware;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.Core;
using Aevatar.AI.Core.Hooks;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Attributes;
using Aevatar.Foundation.Core;
using Microsoft.Extensions.Logging;

namespace Aevatar.GAgents.NyxidChat;

/// <summary>
/// NyxID chat GAgent. Extends RoleGAgent with a chat system prompt.
/// On first activation (empty state), self-initializes with the system prompt
/// so callers never need to dispatch InitializeRoleAgentEvent manually.
/// Always pins the NyxID-backed provider so requests are routed using the
/// authenticated NyxID account instead of drifting with the app default.
/// The NyxID provider itself decides whether to use a user-configured
/// chrono-llm service or fall back to the NyxID LLM gateway.
///
/// Overrides HandleChatRequest to use non-streaming ChatAsync which runs
/// the full ToolCallLoop (LLM -> tool_call -> execute -> result -> LLM -> ...),
/// instead of the base class's ChatStreamAsync which only makes one LLM call.
/// </summary>
public sealed class NyxIdChatGAgent : RoleGAgent
{
    public NyxIdChatGAgent(
        ILLMProviderFactory? llmProviderFactory = null,
        IEnumerable<IAIGAgentExecutionHook>? additionalHooks = null,
        IEnumerable<IAgentRunMiddleware>? agentMiddlewares = null,
        IEnumerable<IToolCallMiddleware>? toolMiddlewares = null,
        IEnumerable<ILLMCallMiddleware>? llmMiddlewares = null,
        IEnumerable<IAgentToolSource>? toolSources = null)
        : base(llmProviderFactory, additionalHooks, agentMiddlewares, toolMiddlewares, llmMiddlewares, toolSources)
    {
    }

    protected override async Task OnActivateAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(State.RoleName))
        {
            await PersistDomainEventAsync(BuildInitializeRoleAgentEvent(NyxIdChatServiceDefaults.DisplayName));
        }
        else if (RequiresNyxIdProviderMigration())
        {
            await PersistDomainEventAsync(BuildInitializeRoleAgentEvent(State.RoleName));
        }

        await base.OnActivateAsync(ct);
    }

    private bool RequiresNyxIdProviderMigration()
    {
        var overrides = State.ConfigOverrides;
        return overrides == null ||
               !overrides.HasProviderName ||
               string.IsNullOrWhiteSpace(overrides.ProviderName);
    }

    private InitializeRoleAgentEvent BuildInitializeRoleAgentEvent(string roleName)
    {
        var initializeEvent = new InitializeRoleAgentEvent
        {
            RoleName = string.IsNullOrWhiteSpace(roleName)
                ? NyxIdChatServiceDefaults.DisplayName
                : roleName.Trim(),
            ProviderName = NyxIdChatServiceDefaults.ProviderName,
            SystemPrompt = NyxIdChatSystemPrompt.Value,
            MaxToolRounds = State.ConfigOverrides?.HasMaxToolRounds == true &&
                            State.ConfigOverrides.MaxToolRounds > 0
                ? State.ConfigOverrides.MaxToolRounds
                : 5,
            EventModules = State.EventModules ?? string.Empty,
            EventRoutes = State.EventRoutes ?? string.Empty,
        };

        var overrides = State.ConfigOverrides;
        if (overrides?.HasModel == true)
            initializeEvent.Model = overrides.Model;

        if (overrides?.HasTemperature == true)
            initializeEvent.Temperature = overrides.Temperature;

        if (overrides?.HasMaxTokens == true && overrides.MaxTokens > 0)
            initializeEvent.MaxTokens = overrides.MaxTokens;

        if (overrides?.HasMaxHistoryMessages == true && overrides.MaxHistoryMessages > 0)
            initializeEvent.MaxHistoryMessages = overrides.MaxHistoryMessages;

        if (overrides?.HasStreamBufferCapacity == true && overrides.StreamBufferCapacity > 0)
            initializeEvent.StreamBufferCapacity = overrides.StreamBufferCapacity;

        return initializeEvent;
    }

    /// <summary>
    /// Handles chat requests using non-streaming ChatAsync which runs the full
    /// ToolCallLoop. The base class uses ChatStreamAsync which only makes one
    /// LLM call and does not execute tools.
    /// </summary>
    [EventHandler]
    public override async Task HandleChatRequest(ChatRequestEvent request)
    {
        var promptPreview = request.Prompt.Length > 200
            ? request.Prompt[..200] + "..."
            : request.Prompt;
        Logger.LogInformation("[NyxIdChat] LLM request: {Preview}", promptPreview);

        IReadOnlyDictionary<string, string>? metadata = request.Metadata.Count == 0
            ? null
            : new Dictionary<string, string>(request.Metadata, StringComparer.Ordinal);

        // Publish TEXT_MESSAGE_START
        await PublishAsync(new TextMessageStartEvent
        {
            SessionId = request.SessionId,
            AgentId = Id,
        }, TopologyAudience.Parent);

        string? result;
        try
        {
            // Use non-streaming ChatAsync which runs the full ToolCallLoop:
            // LLM -> tool_calls -> execute tools -> add results -> LLM -> ...
            result = await ChatAsync(request.Prompt, request.SessionId, metadata, CancellationToken.None);
        }
        catch (Exception ex)
        {
            // Dig out the real error — MEAI/OpenAI SDK often wraps the cause
            var inner = ex;
            while (inner.InnerException != null) inner = inner.InnerException;
            var errorDetail = !string.IsNullOrWhiteSpace(ex.Message) ? ex.Message
                : !string.IsNullOrWhiteSpace(inner.Message) ? inner.Message
                : ex.GetType().Name;
            Logger.LogWarning(ex, "[NyxIdChat] LLM request failed: {Error} (type={ExType})", errorDetail, ex.GetType().FullName);
            result = $"LLM request failed: {errorDetail}";
        }

        // Publish the full result as a single content event
        if (!string.IsNullOrEmpty(result))
        {
            await PublishAsync(new TextMessageContentEvent
            {
                Delta = result,
                SessionId = request.SessionId,
            }, TopologyAudience.Parent);
        }

        // Publish TEXT_MESSAGE_END
        await PublishAsync(new TextMessageEndEvent
        {
            Content = result ?? string.Empty,
            SessionId = request.SessionId,
        }, TopologyAudience.Parent);
    }
}
