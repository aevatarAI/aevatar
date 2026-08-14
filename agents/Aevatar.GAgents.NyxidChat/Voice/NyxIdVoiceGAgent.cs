using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.Middleware;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.Core;
using Aevatar.AI.Core.Hooks;
using Aevatar.Foundation.Abstractions.Attributes;
using Aevatar.Foundation.Abstractions.TypeSystem;

namespace Aevatar.GAgents.NyxidChat.Voice;

[GAgent(NyxIdVoiceServiceDefaults.GAgentKind)]
public sealed class NyxIdVoiceGAgent : RoleGAgent
{
    public NyxIdVoiceGAgent(
        IAgentToolExecutionPort toolExecutionPort,
        ILLMProviderFactory? llmProviderFactory = null,
        IEnumerable<IAIGAgentExecutionHook>? additionalHooks = null,
        IEnumerable<IAgentRunMiddleware>? agentMiddlewares = null,
        IEnumerable<ILLMCallMiddleware>? llmMiddlewares = null,
        IEnumerable<IAgentToolSource>? toolSources = null,
        TimeProvider? timeProvider = null,
        RoleChatExecutionOptions? chatExecutionOptions = null)
        : base(
            toolExecutionPort,
            llmProviderFactory,
            additionalHooks,
            agentMiddlewares,
            llmMiddlewares,
            toolSources,
            timeProvider: timeProvider,
            chatExecutionOptions: chatExecutionOptions)
    {
    }

    protected override async Task OnActivateAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(State.RoleName))
        {
            await PersistDomainEventAsync(new InitializeRoleAgentEvent
            {
                RoleName = NyxIdVoiceServiceDefaults.DisplayName,
                SystemPrompt = "You are Aevatar's voice assistant. Respond naturally and concisely in the user's language.",
            });
        }

        await base.OnActivateAsync(ct);
    }
}
