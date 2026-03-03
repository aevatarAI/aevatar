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

        // Manual iteration so we can catch premature stream end on MoveNextAsync
        // without yielding inside a try-catch (which C# disallows).
        var enumerator = _client.GetStreamingResponseAsync(messages, options, ct).GetAsyncEnumerator(ct);
        try
        {
            while (true)
            {
                ChatResponseUpdate update;
                bool hasNext;
                try
                {
                    hasNext = await enumerator.MoveNextAsync();
                }
                catch (System.Net.Http.HttpIOException ex) when (
                    ex.HttpRequestError == System.Net.Http.HttpRequestError.ResponseEnded)
                {
                    // Upstream dropped the SSE connection mid-stream.  All chunks yielded
                    // so far are valid — gracefully end with whatever we received.
                    _logger.LogWarning(
                        "LLM streaming response ended prematurely; using partial content received so far");
                    break;
                }

                if (!hasNext) break;
                update = enumerator.Current;

                var emittedTextFromContents = false;
                if (update.Contents is { Count: > 0 })
                {
                    foreach (var part in update.Contents)
                    {
                        switch (part)
                        {
                            case TextContent textContent when !string.IsNullOrEmpty(textContent.Text):
                                emittedTextFromContents = true;
                                yield return new LLMStreamChunk
                                {
                                    DeltaContent = textContent.Text,
                                };
                                break;
                            case FunctionCallContent functionCall:
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
                    yield return new LLMStreamChunk
                    {
                        DeltaContent = update.Text,
                    };
                }
            }
        }
        finally
        {
            await enumerator.DisposeAsync();
        }

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
                options.Tools.Add(new AgentToolAIFunction(tool));
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

/// <summary>
/// Wraps an <see cref="IAgentTool"/> as an MEAI <see cref="AIFunction"/>,
/// preserving the original JSON Schema so the LLM sees the real parameter definitions.
/// </summary>
internal sealed class AgentToolAIFunction : AIFunction
{
    private readonly IAgentTool _tool;
    private readonly System.Text.Json.JsonElement _schema;

    public AgentToolAIFunction(IAgentTool tool)
    {
        _tool = tool;

        // Build a JSON schema document that wraps the tool's parameter schema
        // in the standard function-calling envelope: { "description": "...", ... }
        _schema = BuildFunctionSchema(tool);
    }

    public override string Name => _tool.Name;
    public override string Description => _tool.Description;
    public override System.Text.Json.JsonElement JsonSchema => _schema;

    protected override async ValueTask<object?> InvokeCoreAsync(
        AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        // Serialize the arguments dictionary back to JSON for the tool
        var json = arguments.Count > 0
            ? System.Text.Json.JsonSerializer.Serialize(
                arguments.ToDictionary(kv => kv.Key, kv => kv.Value))
            : "{}";

        return await _tool.ExecuteAsync(json, cancellationToken);
    }

    private static System.Text.Json.JsonElement BuildFunctionSchema(IAgentTool tool)
    {
        // If the tool provides a parameters schema, merge it into the function schema.
        // The MEAI OpenAI adapter expects: { "type": "object", "properties": { ... } }
        if (!string.IsNullOrWhiteSpace(tool.ParametersSchema))
        {
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(tool.ParametersSchema);
                // If it's already a valid JSON schema object, use it directly.
                if (doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object)
                    return doc.RootElement.Clone();
            }
            catch
            {
                // Fall through to default schema
            }
        }

        // Fallback: single "input" string parameter
        using var fallback = System.Text.Json.JsonDocument.Parse(
            """{"type":"object","properties":{"input":{"type":"string","description":"Tool input as JSON string"}},"required":["input"]}""");
        return fallback.RootElement.Clone();
    }
}
