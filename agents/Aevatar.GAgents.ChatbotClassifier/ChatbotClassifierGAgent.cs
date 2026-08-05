using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.Middleware;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.Core;
using Aevatar.AI.Core.Hooks;
using Aevatar.Foundation.Abstractions.Attributes;
using Aevatar.Foundation.Abstractions.TypeSystem;

namespace Aevatar.GAgents.ChatbotClassifier;

/// <summary>
/// NyxID Chatbot Classifier GAgent.
/// Intent classification actor: receives a user message (with context),
/// classifies intent (FAQ / action / chitchat / unknown), generates a natural language
/// reply, and extracts structured parameters for action intents.
///
/// Uses the RoleGAgent authoritative streaming, deadline, and terminal commit pipeline.
/// No tools — pure LLM classification with MaxToolRounds=0.
/// </summary>
[GAgent("chatbot.classifier")]
public sealed class ChatbotClassifierGAgent : RoleGAgent
{
    public ChatbotClassifierGAgent(
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
                RoleName = "NyxID Chatbot Classifier",
                SystemPrompt = ChatbotClassifierSystemPrompt.Value,
                MaxToolRounds = 0,
            });
        }

        await base.OnActivateAsync(ct);
    }

    protected override string BuildNonTimeoutLlmFailureContent(
        string safeError,
        string toolNames,
        bool useWorkflowFailureMarker)
    {
        _ = safeError;
        _ = toolNames;
        _ = useWorkflowFailureMarker;
        return """{"intent":"unknown","intent_type":"unknown","reply":"Sorry, I'm having trouble right now. Please try again.","context_summary":null,"params":{}}""";
    }
}
