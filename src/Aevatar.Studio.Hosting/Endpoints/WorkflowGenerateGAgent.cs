using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.Middleware;
using Aevatar.AI.Abstractions.ToolProviders;

namespace Aevatar.Studio.Hosting.Endpoints;

internal sealed class WorkflowGenerateGAgent : StudioGenerateGAgentBase
{
    public WorkflowGenerateGAgent(
        ILLMProviderFactory? llmProviderFactory = null,
        IEnumerable<IAgentRunMiddleware>? agentMiddlewares = null,
        IEnumerable<IToolCallMiddleware>? toolMiddlewares = null,
        IEnumerable<ILLMCallMiddleware>? llmMiddlewares = null,
        IEnumerable<IAgentToolSource>? toolSources = null)
        : base(
            llmProviderFactory: llmProviderFactory,
            agentMiddlewares: agentMiddlewares,
            toolMiddlewares: toolMiddlewares,
            llmMiddlewares: llmMiddlewares,
            toolSources: toolSources)
    {
    }
}
