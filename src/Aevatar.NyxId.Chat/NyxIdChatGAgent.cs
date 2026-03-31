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

namespace Aevatar.NyxId.Chat;

/// <summary>
/// NyxID chat GAgent. Extends RoleGAgent with a chat system prompt.
/// On first activation (empty state), self-initializes with the system prompt
/// so callers never need to dispatch InitializeRoleAgentEvent manually.
/// Uses the default LLM provider (same as other scope services).
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
        // Self-initialize on first activation so the system prompt is persisted
        // into actor state without requiring an external init command.
        // ProviderName is intentionally left unset so the GAgent uses the default
        // LLM provider, consistent with other scope services (e.g. workflow-bound).
        if (string.IsNullOrWhiteSpace(State.RoleName))
        {
            await PersistDomainEventAsync(new InitializeRoleAgentEvent
            {
                RoleName = NyxIdChatServiceDefaults.DisplayName,
                SystemPrompt = NyxIdChatSystemPrompt.Value,
                MaxToolRounds = 5,
            });
        }

        await base.OnActivateAsync(ct);
    }

    /// <summary>
    /// Handles chat requests using non-streaming ChatAsync which runs the full
    /// ToolCallLoop. The base class uses ChatStreamAsync which only makes one
    /// LLM call and does not execute tools.
    /// </summary>
    [EventHandler]
    public new async Task HandleChatRequest(ChatRequestEvent request)
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
            Logger.LogWarning(ex, "[NyxIdChat] LLM request failed");
            result = $"LLM request failed: {ex.Message}";
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
