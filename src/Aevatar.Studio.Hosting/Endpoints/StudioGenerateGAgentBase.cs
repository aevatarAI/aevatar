using System.Text;
using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.Middleware;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.Core;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Attributes;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Studio.Hosting.Endpoints;

internal abstract class StudioGenerateGAgentBase : AIGAgentBase<Empty>
{
    protected StudioGenerateGAgentBase(
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

    [EventHandler(AllowSelfHandling = true)]
    public async Task HandleChatRequest(ChatRequestEvent request)
    {
        ArgumentNullException.ThrowIfNull(request);

        await PublishAsync(new TextMessageStartEvent
        {
            SessionId = request.SessionId,
            AgentId = Id,
        }, TopologyAudience.Parent);

        var timeoutMs = request.TimeoutMs > 0 ? request.TimeoutMs : 0;
        using var timeoutCts = timeoutMs > 0 ? new CancellationTokenSource(timeoutMs) : null;
        var streamCt = timeoutCts?.Token ?? CancellationToken.None;

        StudioGenerateCompletion completion;
        try
        {
            completion = await ExecuteStreamingChatAsync(request, streamCt);
        }
        catch (OperationCanceledException) when (timeoutCts is { IsCancellationRequested: true })
        {
            completion = StudioGenerateCompletion.Failure($"LLM request timed out after {timeoutMs}ms");
        }
        catch (Exception ex)
        {
            completion = StudioGenerateCompletion.Failure(ex.Message);
        }

        await PublishAsync(new TextMessageEndEvent
        {
            SessionId = request.SessionId,
            Content = completion.Content,
        }, TopologyAudience.Parent);

        if (!string.IsNullOrWhiteSpace(request.SessionId))
        {
            await PersistDomainEventAsync(new RoleChatSessionCompletedEvent
            {
                SessionId = request.SessionId,
                Prompt = request.Prompt,
                Content = completion.Content,
                ReasoningContent = completion.ReasoningContent,
                ContentEmitted = false,
                OutputParts = { ContentPartProtoMapper.ToProtoList(completion.ContentParts) },
            });
        }
    }

    public void ResetConversation() => ClearHistory();

    private static IReadOnlyDictionary<string, string>? MergeHeadersAndMetadata(ChatRequestEvent request)
    {
        if (request.Headers.Count == 0 && request.Metadata.Count == 0)
            return null;

        var merged = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var kv in request.Headers)
            merged[kv.Key] = kv.Value;
        foreach (var kv in request.Metadata)
            merged[kv.Key] = kv.Value;
        return merged;
    }

    private async Task<StudioGenerateCompletion> ExecuteStreamingChatAsync(
        ChatRequestEvent request,
        CancellationToken ct)
    {
        var fullContent = new StringBuilder();
        var fullReasoning = new StringBuilder();
        var contentParts = new List<ContentPart>();
        IReadOnlyDictionary<string, string>? metadata = MergeHeadersAndMetadata(request);
        var inputParts = ResolveInputParts(request);

        await foreach (var chunk in ChatStreamAsync(inputParts, request.SessionId, metadata, ct))
        {
            if (!string.IsNullOrEmpty(chunk.DeltaContent))
            {
                fullContent.Append(chunk.DeltaContent);
                await PublishAsync(new TextMessageContentEvent
                {
                    SessionId = request.SessionId,
                    Delta = chunk.DeltaContent,
                }, TopologyAudience.Parent);
            }

            if (!string.IsNullOrEmpty(chunk.DeltaReasoningContent))
            {
                fullReasoning.Append(chunk.DeltaReasoningContent);
                await PublishAsync(new TextMessageReasoningEvent
                {
                    SessionId = request.SessionId,
                    Delta = chunk.DeltaReasoningContent,
                }, TopologyAudience.Parent);
            }

            if (chunk.DeltaContentPart != null)
            {
                contentParts.Add(chunk.DeltaContentPart);
                await PublishAsync(new MediaContentEvent
                {
                    SessionId = request.SessionId,
                    AgentId = Id,
                    Part = ContentPartProtoMapper.ToProto(chunk.DeltaContentPart),
                }, TopologyAudience.Parent);
            }
        }

        return new StudioGenerateCompletion(
            fullContent.ToString(),
            fullReasoning.ToString(),
            contentParts);
    }

    private static IReadOnlyList<ContentPart> ResolveInputParts(ChatRequestEvent request)
    {
        var parts = ContentPartProtoMapper.FromProtoList(request.InputParts);
        if (parts.Count > 0)
            return parts;

        return [ContentPart.TextPart(request.Prompt ?? string.Empty)];
    }

    private sealed record StudioGenerateCompletion(
        string Content,
        string ReasoningContent,
        IReadOnlyList<ContentPart> ContentParts)
    {
        public static StudioGenerateCompletion Failure(string? message) =>
            new(
                string.IsNullOrWhiteSpace(message) ? "LLM request failed." : $"LLM request failed: {message.Trim()}",
                string.Empty,
                []);
    }
}
