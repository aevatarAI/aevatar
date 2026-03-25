using Aevatar.AI.Core;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.Middleware;
using Aevatar.AI.Abstractions.ToolProviders;
using Google.Protobuf.WellKnownTypes;
using System.Text;

using Aevatar.Studio.Application.Scripts.Contracts;
namespace Aevatar.Studio.Hosting.Endpoints;

internal sealed class WorkflowGenerateGAgent : AIGAgentBase<Empty>
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

    protected override AIAgentConfigStateOverrides ExtractStateConfigOverrides(Empty state)
    {
        _ = state;
        return new AIAgentConfigStateOverrides();
    }

    public Task<string?> GenerateAsync(
        string prompt,
        string requestId,
        IReadOnlyDictionary<string, string>? metadata,
        CancellationToken ct = default) =>
        ChatAsync(prompt, requestId, metadata, ct);

    public async Task<string?> GenerateWithReasoningAsync(
        string prompt,
        string requestId,
        IReadOnlyDictionary<string, string>? metadata,
        Func<string, CancellationToken, Task>? onReasoning,
        CancellationToken ct = default)
    {
        var content = new StringBuilder();
        await foreach (var chunk in ChatStreamAsync(prompt, requestId, metadata, ct))
        {
            if (!string.IsNullOrEmpty(chunk.DeltaContent))
                content.Append(chunk.DeltaContent);

            if (!string.IsNullOrEmpty(chunk.DeltaReasoningContent) && onReasoning != null)
                await onReasoning(chunk.DeltaReasoningContent, ct);
        }

        return content.ToString();
    }

    public void ResetConversation() => ClearHistory();
}
