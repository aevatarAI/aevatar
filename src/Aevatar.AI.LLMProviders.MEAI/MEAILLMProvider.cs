// ─────────────────────────────────────────────────────────────
// MEAILLMProvider — 基于 Microsoft.Extensions.AI 的 LLM 提供者
//
// 将 MEAI 的 IChatClient 桥接到 Aevatar 的 ILLMProvider。
// 支持 OpenAI / Azure OpenAI / 任何兼容 OpenAI API 的提供者
// （DeepSeek、Moonshot、通义千问等通过 baseUrl 配置）。
// ─────────────────────────────────────────────────────────────

using System.Runtime.CompilerServices;
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

        await foreach (var update in _client.GetStreamingResponseAsync(messages, options, ct))
        {
            yield return new LLMStreamChunk
            {
                DeltaContent = update.Text,
            };
        }

        // 最后一个 chunk 标记结束
        yield return new LLMStreamChunk { IsLast = true };
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

            // 处理 Tool Call 结果
            if (msg.Role == "tool" && msg.ToolCallId != null)
            {
                meaiMsg.Contents.Clear();
                meaiMsg.Contents.Add(new FunctionResultContent(msg.ToolCallId, msg.Content ?? ""));
            }

            // 处理 Assistant 的 Tool Calls
            if (msg.ToolCalls is { Count: > 0 })
            {
                meaiMsg.Contents.Clear();
                if (msg.Content != null)
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

    private static ChatOptions? BuildOptions(LLMRequest request)
    {
        var options = new ChatOptions();
        var hasOptions = false;

        if (request.Model != null) { options.ModelId = request.Model; hasOptions = true; }
        if (request.Temperature.HasValue) { options.Temperature = (float)request.Temperature.Value; hasOptions = true; }
        if (request.MaxTokens.HasValue) { options.MaxOutputTokens = request.MaxTokens.Value; hasOptions = true; }

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
                    var argsJson = fcc.Arguments != null
                        ? System.Text.Json.JsonSerializer.Serialize(fcc.Arguments)
                        : "{}";
                    toolCalls.Add(new ToolCall
                    {
                        Id = fcc.CallId ?? Guid.NewGuid().ToString("N"),
                        Name = fcc.Name,
                        ArgumentsJson = argsJson,
                    });
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
}
