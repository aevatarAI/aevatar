// ─────────────────────────────────────────────────────────────
// MEAILLMProvider — 基于 Microsoft.Extensions.AI 的 LLM 提供者
//
// 将 MEAI 的 IChatClient 桥接到 Aevatar 的 ILLMProvider。
// 支持 OpenAI / Azure OpenAI / 任何兼容 OpenAI API 的提供者
// （DeepSeek、Moonshot、通义千问等通过 baseUrl 配置）。
// ─────────────────────────────────────────────────────────────

using System.Runtime.CompilerServices;
using System.Text.Json;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.AI.LLMProviders.MEAI;

/// <summary>
/// 基于 MEAI IChatClient 的 ILLMProvider 实现。
/// 支持 OpenAI、Azure OpenAI、以及任何兼容 OpenAI API 的提供者。
/// </summary>
public sealed class MEAILLMProvider : ILLMProvider
{
    private readonly IChatClient _client;
    private readonly ILogger _logger;

    /// <summary>提供者名称。</summary>
    public string Name { get; }

    /// <summary>
    /// 创建 MEAI LLM Provider。
    /// </summary>
    /// <param name="name">提供者名称（如 "openai", "deepseek"）。</param>
    /// <param name="client">MEAI 的 IChatClient 实例。</param>
    /// <param name="logger">日志记录器。</param>
    public MEAILLMProvider(string name, IChatClient client, ILogger? logger = null)
    {
        Name = name;
        _client = client;
        _logger = logger ?? NullLogger.Instance;
    }

    // ─── ILLMProvider.ChatAsync ───

    /// <summary>单轮 LLM 调用。将 Aevatar 的 LLMRequest 转为 MEAI 的 ChatMessage 列表。</summary>
    public async Task<LLMResponse> ChatAsync(LLMRequest request, CancellationToken ct = default)
    {
        var messages = ConvertMessages(request.Messages);
        var options = BuildOptions(request);

        _logger.LogDebug("MEAI ChatAsync: {MessageCount} 条消息, model={Model}",
            messages.Count, options?.ModelId);

        var response = await _client.GetResponseAsync(messages, options, ct);

        return ConvertResponse(response);
    }

    // ─── ILLMProvider.ChatStreamAsync ───

    /// <summary>流式 LLM 调用。返回异步枚举的流式 chunk。</summary>
    public async IAsyncEnumerable<LLMStreamChunk> ChatStreamAsync(
        LLMRequest request,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var messages = ConvertMessages(request.Messages);
        var options = BuildOptions(request);

        _logger.LogDebug("MEAI ChatStreamAsync: {MessageCount} 条消息", messages.Count);
        var emittedStreamChunk = false;
        string? lastFinishReason = null;

        await foreach (var update in _client.GetStreamingResponseAsync(messages, options, ct))
        {
            if (update.FinishReason != null)
                lastFinishReason = update.FinishReason.Value.ToString();

            var emittedTextFromContents = false;
            if (update.Contents is { Count: > 0 })
            {
                foreach (var part in update.Contents)
                {
                    switch (part)
                    {
                        case TextContent textContent when !string.IsNullOrEmpty(textContent.Text):
                            emittedTextFromContents = true;
                            emittedStreamChunk = true;
                            yield return new LLMStreamChunk
                            {
                                DeltaContent = textContent.Text,
                            };
                            break;
                        case TextReasoningContent reasoningContent when !string.IsNullOrEmpty(reasoningContent.Text):
                            emittedStreamChunk = true;
                            yield return new LLMStreamChunk
                            {
                                DeltaReasoningContent = reasoningContent.Text,
                            };
                            break;
                        case FunctionCallContent functionCall:
                            emittedStreamChunk = true;
                            yield return new LLMStreamChunk
                            {
                                DeltaToolCall = ConvertFunctionCallDelta(functionCall),
                            };
                            break;
                    }
                }
            }

            if (!emittedTextFromContents && !string.IsNullOrEmpty(update.Text))
            {
                emittedStreamChunk = true;
                yield return new LLMStreamChunk
                {
                    DeltaContent = update.Text,
                };
            }
        }

        // Some providers may terminate a streaming call without emitting any chunk at all.
        // Only in that case do a single non-streaming fallback call.
        if (!emittedStreamChunk)
        {
            _logger.LogWarning(
                "MEAI ChatStreamAsync emitted no chunks for provider={Provider}; fallback to non-streaming response.",
                Name);

            var fallback = ConvertResponse(await _client.GetResponseAsync(messages, options, ct));
            if (!string.IsNullOrEmpty(fallback.Content))
            {
                yield return new LLMStreamChunk
                {
                    DeltaContent = fallback.Content,
                };
            }

            if (fallback.ToolCalls is { Count: > 0 })
            {
                foreach (var toolCall in fallback.ToolCalls)
                {
                    yield return new LLMStreamChunk
                    {
                        DeltaToolCall = toolCall,
                    };
                }
            }

            yield return new LLMStreamChunk
            {
                IsLast = true,
                Usage = fallback.Usage,
                FinishReason = fallback.FinishReason,
            };
            yield break;
        }

        // 最后一个 chunk 标记结束
        yield return new LLMStreamChunk { IsLast = true, FinishReason = lastFinishReason };
    }

    // ─── 转换：Aevatar → MEAI ───

    private static List<Microsoft.Extensions.AI.ChatMessage> ConvertMessages(
        IEnumerable<Aevatar.AI.Abstractions.LLMProviders.ChatMessage> messages)
    {
        var result = new List<Microsoft.Extensions.AI.ChatMessage>();

        foreach (var msg in messages)
        {
            var role = msg.Role switch
            {
                "system" => ChatRole.System,
                "user" => ChatRole.User,
                "assistant" => ChatRole.Assistant,
                "tool" => ChatRole.Tool,
                _ => ChatRole.User,
            };

            var meaiMsg = new Microsoft.Extensions.AI.ChatMessage(role, msg.Content ?? "");
            if (msg.ContentParts is { Count: > 0 })
            {
                meaiMsg.Contents.Clear();
                AppendContentParts(meaiMsg, msg.ContentParts);
                if (meaiMsg.Contents.Count == 0 && !string.IsNullOrEmpty(msg.Content))
                    meaiMsg.Contents.Add(new TextContent(msg.Content));
            }

            // 处理 Tool Call 结果
            if (msg.Role == "tool" && msg.ToolCallId != null)
            {
                meaiMsg.Contents.Clear();
                meaiMsg.Contents.Add(new FunctionResultContent(msg.ToolCallId, BuildToolResultPayload(msg)));
            }

            // 处理 Assistant 的 Tool Calls
            if (msg.ToolCalls is { Count: > 0 })
            {
                meaiMsg.Contents.Clear();
                if (msg.ContentParts is { Count: > 0 })
                    AppendContentParts(meaiMsg, msg.ContentParts);
                else if (msg.Content != null)
                    meaiMsg.Contents.Add(new TextContent(msg.Content));

                foreach (var tc in msg.ToolCalls)
                {
                    // 解析 JSON 参数为字典
                    Dictionary<string, object?>? args = null;
                    if (!string.IsNullOrEmpty(tc.ArgumentsJson))
                    {
                        try
                        {
                            args = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object?>>(tc.ArgumentsJson);
                        }
                        catch { /* 解析失败则不传参数 */ }
                    }

                    meaiMsg.Contents.Add(new FunctionCallContent(tc.Id, tc.Name, args));
                }
            }

            result.Add(meaiMsg);
        }

        return result;
    }

    private static void AppendContentParts(
        Microsoft.Extensions.AI.ChatMessage message,
        IReadOnlyList<ContentPart> parts)
    {
        foreach (var part in parts)
        {
            if (part == null || string.IsNullOrWhiteSpace(part.Type))
                continue;

            if (string.Equals(part.Type, "text", StringComparison.OrdinalIgnoreCase))
            {
                if (!string.IsNullOrWhiteSpace(part.Text))
                    message.Contents.Add(new TextContent(part.Text));
                continue;
            }

            if (!string.Equals(part.Type, "image", StringComparison.OrdinalIgnoreCase))
                continue;

            if (TryCreateImageDataContent(part, out var dataContent) && dataContent != null)
                message.Contents.Add(dataContent);
        }
    }

    private static bool TryCreateImageDataContent(ContentPart part, out DataContent? content)
    {
        content = null;
        if (string.IsNullOrWhiteSpace(part.ImageBase64))
            return false;

        var mediaType = string.IsNullOrWhiteSpace(part.ImageMediaType) ? "image/png" : part.ImageMediaType!;
        var base64 = part.ImageBase64!.Trim();

        // Accept both plain base64 and data-uri input.
        if (base64.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            var commaIndex = base64.IndexOf(',');
            if (commaIndex > 5)
            {
                var meta = base64[5..commaIndex];
                if (meta.EndsWith(";base64", StringComparison.OrdinalIgnoreCase))
                    meta = meta[..^7];
                if (!string.IsNullOrWhiteSpace(meta))
                    mediaType = meta;
                base64 = base64[(commaIndex + 1)..];
            }
        }

        try
        {
            var bytes = Convert.FromBase64String(base64);
            content = new DataContent(bytes, mediaType);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static object BuildToolResultPayload(Aevatar.AI.Abstractions.LLMProviders.ChatMessage message)
    {
        if (message.ContentParts is not { Count: > 0 })
            return message.Content ?? string.Empty;

        return new
        {
            text = message.Content,
            content_parts = message.ContentParts.Select(p => new
            {
                type = p.Type,
                text = p.Text,
                image_base64 = p.ImageBase64,
                image_media_type = p.ImageMediaType,
            }).ToArray(),
        };
    }

    private static ChatOptions? BuildOptions(LLMRequest request)
    {
        var options = new ChatOptions();
        var hasOptions = false;

        if (!string.IsNullOrWhiteSpace(request.RequestId))
        {
            options.ConversationId = request.RequestId.Trim();
            hasOptions = true;
        }

        if (request.Model != null) { options.ModelId = request.Model; hasOptions = true; }
        if (request.Temperature.HasValue) { options.Temperature = (float)request.Temperature.Value; hasOptions = true; }
        if (request.MaxTokens.HasValue) { options.MaxOutputTokens = request.MaxTokens.Value; hasOptions = true; }

        if (!string.IsNullOrWhiteSpace(request.RequestId) || request.Metadata is { Count: > 0 })
        {
            options.AdditionalProperties = BuildAdditionalProperties(request);
            hasOptions = true;
        }

        // 注册 Tools
        if (request.Tools is { Count: > 0 })
        {
            options.Tools = [];
            foreach (var tool in request.Tools)
            {
                options.Tools.Add(AIFunctionFactory.Create(
                    (string input) => tool.ExecuteAsync(input),
                    tool.Name,
                    tool.Description));
            }
            hasOptions = true;
        }

        return hasOptions ? options : null;
    }

    private static AdditionalPropertiesDictionary BuildAdditionalProperties(LLMRequest request)
    {
        var properties = new Dictionary<string, object?>(StringComparer.Ordinal);

        if (request.Metadata != null)
        {
            foreach (var pair in request.Metadata)
                properties[pair.Key] = pair.Value;
        }

        if (!string.IsNullOrWhiteSpace(request.RequestId))
            properties[LLMRequestMetadataKeys.RequestId] = request.RequestId.Trim();

        return new AdditionalPropertiesDictionary(properties);
    }

    // ─── 转换：MEAI → Aevatar ───

    private static LLMResponse ConvertResponse(Microsoft.Extensions.AI.ChatResponse response)
    {
        // ChatResponse.Messages 包含所有回复消息
        var lastMessage = response.Messages.LastOrDefault();
        var content = lastMessage?.Text;
        List<ToolCall>? toolCalls = null;

        // 检查是否有 Tool Calls
        if (lastMessage != null)
        {
            foreach (var part in lastMessage.Contents)
            {
                if (part is FunctionCallContent fcc)
                {
                    toolCalls ??= [];
                    toolCalls.Add(ConvertFunctionCall(fcc));
                }
            }
        }

        TokenUsage? usage = null;
        if (response.Usage != null)
        {
            usage = new TokenUsage(
                (int)(response.Usage.InputTokenCount ?? 0),
                (int)(response.Usage.OutputTokenCount ?? 0),
                (int)(response.Usage.TotalTokenCount ?? 0));
        }

        return new LLMResponse
        {
            Content = content,
            ToolCalls = toolCalls,
            Usage = usage,
            FinishReason = response.FinishReason?.ToString(),
        };
    }

    private static ToolCall ConvertFunctionCall(FunctionCallContent functionCall)
    {
        var argsJson = functionCall.Arguments != null
            ? System.Text.Json.JsonSerializer.Serialize(functionCall.Arguments)
            : "{}";
        return new ToolCall
        {
            Id = functionCall.CallId ?? Guid.NewGuid().ToString("N"),
            Name = functionCall.Name ?? string.Empty,
            ArgumentsJson = argsJson,
        };
    }

    // Keep delta semantics: missing callId should stay empty and be resolved by downstream accumulator.
    private static ToolCall ConvertFunctionCallDelta(FunctionCallContent functionCall)
    {
        var argsJson = functionCall.Arguments != null
            ? System.Text.Json.JsonSerializer.Serialize(functionCall.Arguments)
            : string.Empty;
        return new ToolCall
        {
            Id = functionCall.CallId ?? string.Empty,
            Name = functionCall.Name ?? string.Empty,
            ArgumentsJson = argsJson,
        };
    }
}
