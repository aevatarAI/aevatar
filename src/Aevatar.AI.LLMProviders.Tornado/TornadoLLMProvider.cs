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
    private readonly TornadoApi _api;
    private readonly string _modelName;
    private readonly ILogger _logger;

    public string Name { get; }

    public TornadoLLMProvider(string name, TornadoApi api, string modelName, ILogger? logger = null)
    {
        Name = name;
        _api = api;
        _modelName = modelName;
        _logger = logger ?? NullLogger.Instance;
    }

    // ─── ILLMProvider.ChatAsync ───

    /// <summary>单轮 LLM 调用。</summary>
    public async Task<LLMResponse> ChatAsync(LLMRequest request, CancellationToken ct = default)
    {
        var chatRequest = MapRequest(request);
        _logger.LogDebug("Tornado ChatAsync: {Model}, {MsgCount} 条消息", _modelName, request.Messages.Count);
        var result = await _api.Chat.CreateChatCompletion(chatRequest).WaitAsync(ct);
        return MapResponse(result);
    }

    // ─── ILLMProvider.ChatStreamAsync ───

    /// <summary>流式 LLM 调用。</summary>
    public async IAsyncEnumerable<LLMStreamChunk> ChatStreamAsync(
        LLMRequest request, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var chatRequest = MapRequest(request);
        _logger.LogDebug("Tornado ChatStreamAsync: {Model}", _modelName);

        await foreach (var chunk in _api.Chat.StreamChatEnumerable(chatRequest).WithCancellation(ct))
        {
            var delta = chunk.Choices?.FirstOrDefault()?.Delta;
            if (delta?.Content != null)
                yield return new LLMStreamChunk { DeltaContent = delta.Content };
        }
        yield return new LLMStreamChunk { IsLast = true };
    }

    // ─── 转换：Aevatar → LlmTornado ───

    private LlmTornado.Chat.ChatRequest MapRequest(LLMRequest request)
    {
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

        if (request.Temperature.HasValue)
            chatRequest.Temperature = request.Temperature.Value;

        if (request.MaxTokens.HasValue)
            chatRequest.MaxTokens = request.MaxTokens.Value;

        // Tool Calling：推荐使用 MEAI Provider
        // LlmTornado Provider 主要用于纯 Chat 场景

        return chatRequest;
    }

    // ─── 转换：LlmTornado → Aevatar ───

    private static LLMResponse MapResponse(ChatResult? result)
    {
        if (result == null)
            return new LLMResponse { Content = null, FinishReason = "error" };

        var choice = result.Choices?.FirstOrDefault();
        var content = choice?.Message?.Content;
        List<AevatarToolCall>? toolCalls = null;

        if (choice?.Message?.ToolCalls is { Count: > 0 })
        {
            toolCalls = choice.Message.ToolCalls.Select(tc => new AevatarToolCall
            {
                Id = tc.Id ?? Guid.NewGuid().ToString("N"),
                Name = tc.FunctionCall?.Name ?? "",
                ArgumentsJson = tc.FunctionCall?.Arguments ?? "{}",
            }).ToList();
        }

        TokenUsage? usage = null;
        if (result.Usage != null)
        {
            usage = new TokenUsage(
                result.Usage.PromptTokens,
                result.Usage.CompletionTokens,
                result.Usage.TotalTokens);
        }

        return new LLMResponse
        {
            Content = content,
            ToolCalls = toolCalls,
            Usage = usage,
            FinishReason = choice?.FinishReason?.ToString(),
        };
    }
}
