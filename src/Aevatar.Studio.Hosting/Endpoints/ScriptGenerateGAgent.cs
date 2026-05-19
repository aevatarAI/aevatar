using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.Middleware;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.Core;
using Aevatar.AI.Core.Chat;
using Google.Protobuf.WellKnownTypes;
using System.Text;

using Aevatar.Studio.Application.Scripts.Contracts;
namespace Aevatar.Studio.Hosting.Endpoints;

internal sealed class ScriptGenerateGAgent : AIGAgentBase<Empty>
{
    public ScriptGenerateGAgent(
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

    public async Task<string?> GenerateAsync(
        string prompt,
        string requestId,
        IReadOnlyDictionary<string, string>? metadata,
        CancellationToken ct = default)
    {
        // Refactor (iter15/cluster-024):
        //   Old pattern: non-streaming ChatAsync directly called provider.ChatAsync.
        //   New principle: ChatStreamAsync is the only authoritative AI executor; offline text aggregation consumes the stream as an explicit adapter.
        return await ChatStreamContentAggregator.AggregateContentAsync(
            ChatStreamAsync(prompt, requestId, metadata, ct),
            emptyAsNull: false,
            ct: ct);
    }

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
