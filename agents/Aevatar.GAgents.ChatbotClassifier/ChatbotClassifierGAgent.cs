using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.Middleware;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.Core;
using Aevatar.AI.Core.Hooks;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Attributes;
using Microsoft.Extensions.Logging;

namespace Aevatar.GAgents.ChatbotClassifier;

/// <summary>
/// NyxID Chatbot Classifier GAgent.
/// Stateless intent classification service: receives a user message (with context),
/// classifies intent (FAQ / action / chitchat / unknown), generates a natural language
/// reply, and extracts structured parameters for action intents.
///
/// Uses non-streaming ChatAsync for reliable JSON output parsing.
/// No tools — pure LLM classification with MaxToolRounds=0.
/// </summary>
public sealed class ChatbotClassifierGAgent : RoleGAgent
{
    public ChatbotClassifierGAgent(
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
            await PersistDomainEventAsync(new InitializeRoleAgentEvent
            {
                RoleName = "NyxID Chatbot Classifier",
                SystemPrompt = ChatbotClassifierSystemPrompt.Value,
                MaxToolRounds = 0,
            });
        }

        await base.OnActivateAsync(ct);
    }

    [EventHandler]
    public override async Task HandleChatRequest(ChatRequestEvent request)
    {
        IReadOnlyDictionary<string, string>? metadata = request.Metadata.Count == 0
            ? null
            : new Dictionary<string, string>(request.Metadata, StringComparer.Ordinal);

        await PublishAsync(new TextMessageStartEvent
        {
            SessionId = request.SessionId,
            AgentId = Id,
        }, TopologyAudience.Parent);

        string? result;
        try
        {
            result = await ChatAsync(request.Prompt, request.SessionId, metadata, CancellationToken.None);
        }
        catch (Exception ex)
        {
            var inner = ex;
            while (inner.InnerException != null) inner = inner.InnerException;
            var errorDetail = !string.IsNullOrWhiteSpace(ex.Message) ? ex.Message
                : !string.IsNullOrWhiteSpace(inner.Message) ? inner.Message
                : ex.GetType().Name;
            Logger.LogWarning(ex, "[ChatbotClassifier] LLM request failed: {Error}", errorDetail);
            result = """{"intent":"unknown","intent_type":"unknown","reply":"Sorry, I'm having trouble right now. Please try again.","context_summary":null,"params":{}}""";
        }

        if (!string.IsNullOrEmpty(result))
        {
            await PublishAsync(new TextMessageContentEvent
            {
                Delta = result,
                SessionId = request.SessionId,
            }, TopologyAudience.Parent);
        }

        await PublishAsync(new TextMessageEndEvent
        {
            Content = result ?? string.Empty,
            SessionId = request.SessionId,
        }, TopologyAudience.Parent);
    }
}
