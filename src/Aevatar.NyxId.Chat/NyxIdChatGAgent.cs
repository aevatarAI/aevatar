using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.Middleware;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.Core;
using Aevatar.AI.Core.Hooks;

namespace Aevatar.NyxId.Chat;

/// <summary>
/// NyxID-backed chat GAgent. Extends RoleGAgent with NyxID provider defaults.
/// On first activation (empty state), self-initializes with the NyxID provider name
/// and the NyxID chat system prompt so callers never need to dispatch
/// InitializeRoleAgentEvent manually.
///
/// The NyxID access token must be supplied per-request via
/// ChatRequestEvent.Metadata["nyxid.access_token"]; the NyxID LLM provider
/// reads it from there and forwards it to the NyxID gateway.
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
        // Self-initialize on first activation so the NyxID provider and system prompt
        // are persisted into actor state without requiring an external init command.
        if (string.IsNullOrWhiteSpace(State.RoleName))
        {
            await PersistDomainEventAsync(new InitializeRoleAgentEvent
            {
                RoleName = NyxIdChatServiceDefaults.DisplayName,
                ProviderName = NyxIdChatServiceDefaults.ProviderName,
                SystemPrompt = NyxIdChatSystemPrompt.Value,
            });
        }

        await base.OnActivateAsync(ct);
    }
}
