// ─────────────────────────────────────────────────────────────
// TornadoLLMProvider — 基于 LlmTornado 的 LLM 提供者
//
// 桥接 LlmTornado 的 TornadoApi 到 Aevatar 的 ILLMProvider。
// 支持 OpenAI / Anthropic / Google / Cohere / DeepSeek 等。
// ─────────────────────────────────────────────────────────────

using System.Runtime.CompilerServices;
using Aevatar.AI.Abstractions.LLMProviders;
using LlmTornado;
using LlmTornado.Chat;
using LlmTornado.Chat.Models;
// LlmTornado Tool API 在 v3.8+ 有较大变动
using LlmTornado.Code;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using TornadoChatMessage = LlmTornado.Chat.ChatMessage;
using AevatarChatMessage = Aevatar.AI.Abstractions.LLMProviders.ChatMessage;
using AevatarToolCall = Aevatar.AI.Abstractions.LLMProviders.ToolCall;

namespace Aevatar.AI.LLMProviders.Tornado;

/// <summary>
/// 基于 LlmTornado 的 ILLMProvider 实现。
/// </summary>
public sealed class TornadoLLMProvider : ILLMProvider
{
    private static readonly LLMProviderCapabilities ProviderCapabilities = new()
    {
        SupportedInputModalities = new HashSet<ContentPartKind> { ContentPartKind.Text },
        SupportedOutputModalities = new HashSet<ContentPartKind> { ContentPartKind.Text },
        SupportsStreaming = true,
        SupportsToolCalls = true,
        SupportsReasoningDeltas = false,
    };

    private readonly TornadoApi _api;
    private readonly string _modelName;
    private readonly ILogger _logger;

    public string Name { get; }
    public LLMProviderCapabilities Capabilities => ProviderCapabilities;

    public TornadoLLMProvider(string name, TornadoApi api, string modelName, ILogger? logger = null)
    {
        Name = name;
        _api = api;
        _modelName = modelName;
        _logger = logger ?? NullLogger.Instance;
    }

    // ─── ILLMProvider.ChatStreamAsync ───

    /// <summary>流式 LLM 调用。</summary>
    // Refactor (iter18/cluster-001):
    //   Old pattern: ILLMProvider 仍暴露 ChatAsync 非流式入口,provider/failover 可绕过流式链路
    //   New principle: Provider contract 只暴露 ChatStreamAsync;非流式聚合用现有 ChatStreamContentAggregator;无新 offline adapter
    public async IAsyncEnumerable<LLMStreamChunk> ChatStreamAsync(
        LLMRequest request, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var chatRequest = MapRequest(request);
        _logger.LogDebug("Tornado ChatStreamAsync: {Model}", _modelName);

        string? lastFinishReason = null;

        await foreach (var chunk in _api.Chat.StreamChatEnumerable(chatRequest).WithCancellation(ct))
        {
            var choice = chunk.Choices?.FirstOrDefault();
            var delta = choice?.Delta;
            var usage = MapUsage(chunk.Usage);
            var emitted = false;

            if (choice?.FinishReason != null)
                lastFinishReason = choice.FinishReason.ToString();

            if (!string.IsNullOrEmpty(delta?.Content))
            {
                yield return new LLMStreamChunk
                {
                    DeltaContent = delta.Content,
                    Usage = usage,
                };
                usage = null;
                emitted = true;
            }

            if (delta?.ToolCalls is { Count: > 0 })
            {
                foreach (var toolCall in delta.ToolCalls)
                {
                    yield return new LLMStreamChunk
                    {
                        DeltaToolCall = ConvertToolCallDelta(toolCall),
                        Usage = usage,
                    };
                    usage = null;
                    emitted = true;
                }
            }

            if (!emitted && usage != null)
                yield return new LLMStreamChunk { Usage = usage };
        }

        yield return new LLMStreamChunk { IsLast = true, FinishReason = lastFinishReason };
    }

    // ─── 转换：Aevatar → LlmTornado ───

    private LlmTornado.Chat.ChatRequest MapRequest(LLMRequest request)
    {
        request = SanitizeMessages(request);

        // Graceful multimodal fallback: strip non-text content parts instead of throwing.
        var hasNonText = request.Messages.Any(m =>
            m.ContentParts?.Any(p => p.Kind is not (ContentPartKind.Unspecified or ContentPartKind.Text)) == true);
        if (hasNonText)
        {
            request = new LLMRequest
            {
                Messages = request.Messages.Select(StripNonTextContentParts).ToList(),
                RequestId = request.RequestId,
                Metadata = request.Metadata,
                Tools = request.Tools,
                Model = request.Model,
                Temperature = request.Temperature,
                MaxTokens = request.MaxTokens,
                AllowMultipleToolCalls = request.AllowMultipleToolCalls,
                ResponseFormat = request.ResponseFormat,
            };
        }

        var messages = request.Messages.Select(m => new TornadoChatMessage(
            m.Role switch
            {
                "system" => ChatMessageRoles.System,
                "user" => ChatMessageRoles.User,
                "assistant" => ChatMessageRoles.Assistant,
                "tool" => ChatMessageRoles.Tool,
                _ => ChatMessageRoles.User,
            },
            m.Content ?? "")).ToList();

        var chatRequest = new LlmTornado.Chat.ChatRequest
        {
            Model = _modelName,
            Messages = messages,
        };

        var metadata = BuildMetadata(request);
        if (metadata != null)
            chatRequest.Metadata = metadata;

        if (request.Temperature.HasValue)
            chatRequest.Temperature = request.Temperature.Value;

        if (request.MaxTokens.HasValue)
            chatRequest.MaxTokens = request.MaxTokens.Value;

        // Tool Calling：推荐使用 MEAI Provider
        // LlmTornado Provider 主要用于纯 Chat 场景

        return chatRequest;
    }

    private static LLMRequest SanitizeMessages(LLMRequest request)
    {
        var sanitizedMessages = ChatMessageToolCallTranscript.WithoutInvalidToolCallPairs(request.Messages);
        return sanitizedMessages.Count == request.Messages.Count
            ? request
            : new LLMRequest
            {
                Messages = sanitizedMessages,
                RequestId = request.RequestId,
                Metadata = request.Metadata,
                CallerContext = request.CallerContext,
                ToolContext = request.ToolContext,
                RoutingContext = request.RoutingContext,
                LlmControl = request.LlmControl,
                Tools = request.Tools,
                Model = request.Model,
                Temperature = request.Temperature,
                MaxTokens = request.MaxTokens,
                AllowMultipleToolCalls = request.AllowMultipleToolCalls,
                ResponseFormat = request.ResponseFormat,
            };
    }

    private static Dictionary<string, string>? BuildMetadata(LLMRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.RequestId) &&
            (request.Metadata == null || request.Metadata.Count == 0))
        {
            return null;
        }

        var metadata = new Dictionary<string, string>(StringComparer.Ordinal);
        if (request.Metadata != null)
        {
            foreach (var pair in request.Metadata)
                metadata[pair.Key] = pair.Value;
        }

        if (!string.IsNullOrWhiteSpace(request.RequestId))
            metadata[LLMRequestMetadataKeys.RequestId] = request.RequestId.Trim();

        return metadata;
    }

    // ─── 转换：LlmTornado → Aevatar ───

    private static TokenUsage? MapUsage(ChatUsage? usage)
    {
        if (usage == null)
            return null;

        return new TokenUsage(
            usage.PromptTokens,
            usage.CompletionTokens,
            usage.TotalTokens);
    }

    // Keep delta semantics: missing stream IDs should remain empty for downstream merge.
    private static AevatarToolCall ConvertToolCallDelta(LlmTornado.ChatFunctions.ToolCall toolCall)
    {
        return new AevatarToolCall
        {
            Id = toolCall.Id ?? string.Empty,
            Name = toolCall.FunctionCall?.Name ?? string.Empty,
            ArgumentsJson = toolCall.FunctionCall?.Arguments ?? string.Empty,
        };
    }

    private static Aevatar.AI.Abstractions.LLMProviders.ChatMessage StripNonTextContentParts(
        Aevatar.AI.Abstractions.LLMProviders.ChatMessage m)
    {
        if (m.ContentParts is not { Count: > 0 })
            return m;

        var textParts = m.ContentParts
            .Where(p => p.Kind is ContentPartKind.Text or ContentPartKind.Unspecified)
            .ToList();
        var strippedKinds = m.ContentParts
            .Where(p => p.Kind is not (ContentPartKind.Text or ContentPartKind.Unspecified))
            .Select(p => p.Kind.ToString().ToLowerInvariant())
            .Distinct()
            .ToList();

        if (strippedKinds.Count > 0)
            textParts.Add(ContentPart.TextPart(
                $"[Note: {string.Join(", ", strippedKinds)} content was attached but this model only supports text]"));

        // Tornado's MapRequest reads m.Content, so materialize all text parts into Content.
        var mergedText = string.Join("\n", textParts
            .Where(p => !string.IsNullOrWhiteSpace(p.Text))
            .Select(p => p.Text));
        var fallbackContent = !string.IsNullOrWhiteSpace(mergedText) ? mergedText
            : !string.IsNullOrWhiteSpace(m.Content) ? m.Content
            : "";

        return new Aevatar.AI.Abstractions.LLMProviders.ChatMessage
        {
            Role = m.Role,
            Content = fallbackContent,
            ReasoningContent = m.ReasoningContent,
            ContentParts = null, // Tornado doesn't use ContentParts
            ToolCallId = m.ToolCallId,
            ToolCalls = m.ToolCalls,
        };
    }
}
