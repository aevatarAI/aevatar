// ─────────────────────────────────────────────────────────────
// ToolCallLoop — Tool Calling 循环逻辑
// LLM 返回 tool_call → 执行 → 将结果加入历史 → 继续调 LLM
// 在每次 LLM 调用和 Tool 执行前后调用 Hook Pipeline + Middleware
// ─────────────────────────────────────────────────────────────

using System.Text;
using System.Text.Json;
using Aevatar.AI.Core.Chat;
using Aevatar.AI.Core.Hooks;
using Aevatar.AI.Core.Middleware;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.Middleware;
using Aevatar.AI.Abstractions.ToolProviders;

namespace Aevatar.AI.Core.Tools;

/// <summary>Tool Calling 循环。含 Hook + Middleware 集成。</summary>
public sealed class ToolCallLoop
{
    private readonly ToolManager _tools;
    private readonly AgentHookPipeline? _hooks;
    private readonly IReadOnlyList<IToolCallMiddleware> _toolMiddlewares;
    private readonly IReadOnlyList<ILLMCallMiddleware> _llmMiddlewares;
    private readonly TokenBudgetTracker? _budgetTracker;

    public ToolCallLoop(
        ToolManager tools,
        AgentHookPipeline? hooks = null,
        IReadOnlyList<IToolCallMiddleware>? toolMiddlewares = null,
        IReadOnlyList<ILLMCallMiddleware>? llmMiddlewares = null,
        TokenBudgetTracker? budgetTracker = null)
    {
        _tools = tools;
        _hooks = hooks;
        _toolMiddlewares = toolMiddlewares ?? [];
        _llmMiddlewares = llmMiddlewares ?? [];
        _budgetTracker = budgetTracker;
    }

    /// <summary>Exposes the tool manager for streaming tool execution.</summary>
    internal ToolManager Tools => _tools;

    /// <summary>Exposes the tool middlewares for streaming tool execution.</summary>
    internal IReadOnlyList<IToolCallMiddleware> ToolMiddlewares => _toolMiddlewares;

    /// <summary>
    /// 执行 Tool Calling 循环。返回最终的 LLM 文本内容。
    /// 循环：LLM → tool_call → execute → result → LLM → ...
    /// 每次 LLM 调用和 Tool 执行前后触发 Hook + Middleware。
    /// </summary>
    public async Task<string?> ExecuteAsync(
        ILLMProvider provider, List<ChatMessage> messages,
        LLMRequest baseRequest, int maxRounds, CancellationToken ct)
    {
        AgentToolRequestContext.CurrentMetadata = baseRequest.Metadata;
        try
        {
            return await ExecuteCoreAsync(provider, messages, baseRequest, maxRounds, ct);
        }
        finally
        {
            AgentToolRequestContext.CurrentMetadata = null;
        }
    }

    /// <summary>Max recovery attempts when the LLM response is truncated by output token limit.</summary>
    internal const int MaxLengthRecoveries = 3;

    internal const string LengthRecoveryNudge =
        "[System: Your previous response was cut off due to length limits. " +
        "Continue exactly where you left off — do not repeat any text you already produced. " +
        "If you were in the middle of a tool call, please make the tool call again.]";

    private async Task<string?> ExecuteCoreAsync(
        ILLMProvider provider, List<ChatMessage> messages,
        LLMRequest baseRequest, int maxRounds, CancellationToken ct)
    {
        var lengthRecoveryCount = 0;
        StringBuilder? accumulatedContent = null;

        for (var round = 0; round < maxRounds; round++)
        {
            var callId = ComposeRoundCallId(baseRequest.RequestId, round);
            var request = new LLMRequest
            {
                Messages = [..messages],
                RequestId = baseRequest.RequestId,
                Metadata = BuildPerCallMetadata(baseRequest.Metadata, callId),
                Tools = baseRequest.Tools,
                Model = baseRequest.Model,
                Temperature = baseRequest.Temperature,
                MaxTokens = baseRequest.MaxTokens,
            };

            var (response, terminated) = await InvokeLlmAsync(provider, request, ct);

            // ─── Hook: Post-Sampling（LLM 输出后、Tool 执行前） ───
            if (_hooks != null && response.HasToolCalls && !terminated)
            {
                var postSamplingCtx = new AIGAgentExecutionHookContext
                {
                    LLMResponse = response,
                };
                postSamplingCtx.Items["tool_call_count"] = response.ToolCalls?.Count ?? 0;
                await _hooks.RunPostSamplingAsync(postSamplingCtx, ct);

                // Hook 可通过 Items["block_tool_calls"] = true 阻止 tool call 执行
                if (postSamplingCtx.Items.TryGetValue("block_tool_calls", out var block)
                    && block is true)
                {
                    if (response.Content != null)
                        messages.Add(ChatMessage.Assistant(response.Content));
                    return response.Content;
                }
            }

            if (terminated || !response.HasToolCalls)
            {
                // Recovery: if the response was truncated by max_tokens, inject a continuation
                // nudge and retry instead of exiting — mirrors Claude Code's recovery logic.
                if (!terminated
                    && IsLengthTruncated(response.FinishReason)
                    && lengthRecoveryCount < MaxLengthRecoveries)
                {
                    if (response.Content != null)
                    {
                        accumulatedContent ??= new StringBuilder();
                        accumulatedContent.Append(response.Content);
                        messages.Add(ChatMessage.Assistant(response.Content));
                    }
                    messages.Add(ChatMessage.User(LengthRecoveryNudge));
                    lengthRecoveryCount++;
                    continue;
                }

                // Build result: concatenate any previously accumulated partial content
                // with this final segment so the caller gets the full reconstructed answer.
                var resultContent = response.Content;
                if (accumulatedContent != null)
                {
                    if (resultContent != null)
                        accumulatedContent.Append(resultContent);
                    resultContent = accumulatedContent.ToString();
                }

                if (resultContent != null)
                    messages.Add(ChatMessage.Assistant(resultContent));
                return resultContent;
            }

            // Tool call round resets accumulation — tool results break the text continuation.
            accumulatedContent = null;

            // 记录 assistant tool_call 消息
            messages.Add(new ChatMessage { Role = "assistant", ToolCalls = response.ToolCalls });
            await ExecuteToolCallsCoreAsync(response.ToolCalls!, messages, ct);
        }

        // maxRounds exhausted — tool results from the last round are already in messages.
        // Make one final LLM call WITHOUT tools so the model must produce a text response.
        var finalCallId = ComposeFinalCallId(baseRequest.RequestId);
        var finalRequest = new LLMRequest
        {
            Messages = [..messages],
            RequestId = baseRequest.RequestId,
            Metadata = BuildPerCallMetadata(baseRequest.Metadata, finalCallId),
            Tools = null,
            Model = baseRequest.Model,
            Temperature = baseRequest.Temperature,
            MaxTokens = baseRequest.MaxTokens,
        };
        var (finalResponse, _) = await InvokeLlmAsync(provider, finalRequest, ct);
        var finalContent = finalResponse?.Content;
        if (finalContent != null)
            messages.Add(ChatMessage.Assistant(finalContent));
        return finalContent;
    }

    internal async Task ExecuteToolCallsAsync(
        IReadOnlyList<ToolCall> toolCalls,
        List<ChatMessage> messages,
        IReadOnlyDictionary<string, string>? metadata,
        CancellationToken ct)
    {
        AgentToolRequestContext.CurrentMetadata = metadata;
        try
        {
            await ExecuteToolCallsCoreAsync(toolCalls, messages, ct);
        }
        finally
        {
            AgentToolRequestContext.CurrentMetadata = null;
        }
    }

    private async Task<(LLMResponse Response, bool Terminated)> InvokeLlmAsync(
        ILLMProvider provider,
        LLMRequest request,
        CancellationToken ct)
    {
        // ─── Hook: LLM Request Start ───
        var llmCtx = new AIGAgentExecutionHookContext { LLMRequest = request };
        if (_hooks != null) await _hooks.RunLLMRequestStartAsync(llmCtx, ct);

        var llmCallContext = new LLMCallContext
        {
            Request = request,
            Provider = provider,
            CancellationToken = ct,
            IsStreaming = false,
        };
        AnnotateRequestIdentity(llmCallContext);

        await MiddlewarePipeline.RunLLMCallAsync(_llmMiddlewares, llmCallContext, async () =>
        {
            if (llmCallContext.Terminate) return;
            llmCallContext.Response = await provider.ChatAsync(llmCallContext.Request, ct);
        });

        var response = llmCallContext.Response
            ?? new LLMResponse { Content = null, ToolCalls = null };
        _budgetTracker?.RecordUsage(response.Usage);
        llmCtx.LLMResponse = response;

        // ─── Hook: LLM Request End ───
        if (_hooks != null) await _hooks.RunLLMRequestEndAsync(llmCtx, ct);

        return (response, llmCallContext.Terminate);
    }

    internal static IReadOnlyDictionary<string, string>? BuildPerCallMetadata(
        IReadOnlyDictionary<string, string>? baseMetadata,
        string? callId)
    {
        if (baseMetadata == null || baseMetadata.Count == 0)
        {
            if (string.IsNullOrWhiteSpace(callId))
                return null;

            return new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [LLMRequestMetadataKeys.CallId] = callId,
            };
        }

        var metadata = new Dictionary<string, string>(baseMetadata, StringComparer.Ordinal);
        if (!string.IsNullOrWhiteSpace(callId))
            metadata[LLMRequestMetadataKeys.CallId] = callId;
        return metadata;
    }

    internal static string? ComposeRoundCallId(string? baseRequestId, int round)
    {
        if (string.IsNullOrWhiteSpace(baseRequestId))
            return null;

        return round <= 0
            ? baseRequestId
            : $"{baseRequestId}:tool-round:{round + 1}";
    }

    internal static string? ComposeFinalCallId(string? baseRequestId)
    {
        if (string.IsNullOrWhiteSpace(baseRequestId))
            return null;

        return $"{baseRequestId}:final";
    }

    private static void AnnotateRequestIdentity(LLMCallContext context)
    {
        if (!string.IsNullOrWhiteSpace(context.Request.RequestId))
            context.Items[LLMRequestMetadataKeys.RequestId] = context.Request.RequestId;

        if (context.Request.Metadata != null &&
            context.Request.Metadata.TryGetValue(LLMRequestMetadataKeys.CallId, out var callId) &&
            !string.IsNullOrWhiteSpace(callId))
        {
            context.Items[LLMRequestMetadataKeys.CallId] = callId;
        }
    }

    internal static ChatMessage BuildToolResultMessage(string callId, string toolResult)
    {
        if (!TryExtractToolContentParts(toolResult, out var text, out var parts))
            return ChatMessage.Tool(callId, toolResult);

        return new ChatMessage
        {
            Role = "tool",
            ToolCallId = callId,
            Content = text,
            ContentParts = parts,
        };
    }

    private static bool TryExtractToolContentParts(
        string toolResult,
        out string text,
        out IReadOnlyList<ContentPart>? contentParts)
    {
        text = toolResult;
        contentParts = null;

        if (string.IsNullOrWhiteSpace(toolResult))
            return false;

        try
        {
            using var doc = JsonDocument.Parse(toolResult);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return false;

            var root = doc.RootElement;
            var imageBase64 =
                TryGetStringByKeys(root, "image_base64", "imageBase64", "base64", "data") ??
                TryGetNestedImageBase64(root);
            if (string.IsNullOrWhiteSpace(imageBase64))
                return false;

            var mediaType =
                TryGetStringByKeys(root, "image_media_type", "imageMediaType", "mime_type", "mimeType", "media_type", "mediaType", "content_type") ??
                TryGetNestedImageMediaType(root) ??
                "image/png";

            // Accept data-uri output and normalize into raw base64 + media type.
            var normalizedBase64 = imageBase64!.Trim();
            if (normalizedBase64.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                var commaIndex = normalizedBase64.IndexOf(',');
                if (commaIndex > 5)
                {
                    var meta = normalizedBase64[5..commaIndex];
                    if (meta.EndsWith(";base64", StringComparison.OrdinalIgnoreCase))
                        meta = meta[..^7];
                    if (!string.IsNullOrWhiteSpace(meta))
                        mediaType = meta;
                    normalizedBase64 = normalizedBase64[(commaIndex + 1)..];
                }
            }

            text =
                TryGetStringByKeys(root, "text", "description", "summary", "observation", "message") ??
                "[tool image output]";
            contentParts =
            [
                ContentPart.TextPart(text),
                ContentPart.ImagePart(normalizedBase64, mediaType),
            ];
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string? TryGetNestedImageBase64(JsonElement root)
    {
        if (!root.TryGetProperty("image", out var image) || image.ValueKind != JsonValueKind.Object)
            return null;
        return TryGetStringByKeys(image, "base64", "image_base64", "data");
    }

    private static string? TryGetNestedImageMediaType(JsonElement root)
    {
        if (!root.TryGetProperty("image", out var image) || image.ValueKind != JsonValueKind.Object)
            return null;
        return TryGetStringByKeys(image, "media_type", "mime_type", "mediaType", "mimeType", "content_type");
    }

    private static string? TryGetStringByKeys(JsonElement element, params string[] keys)
    {
        if (element.ValueKind != JsonValueKind.Object)
            return null;

        foreach (var key in keys)
        {
            if (!element.TryGetProperty(key, out var value))
                continue;

            if (value.ValueKind == JsonValueKind.String)
                return value.GetString();

            if (value.ValueKind != JsonValueKind.Null && value.ValueKind != JsonValueKind.Undefined)
                return value.ToString();
        }

        return null;
    }

    private async Task ExecuteToolCallsCoreAsync(
        IReadOnlyList<ToolCall> toolCalls,
        List<ChatMessage> messages,
        CancellationToken ct)
    {
        using var executor = new StreamingToolExecutor(_tools, _hooks, _toolMiddlewares);

        foreach (var call in toolCalls)
            executor.AddTool(call);

        await foreach (var result in executor.GetRemainingResultsAsync(ct))
            messages.Add(BuildToolResultMessage(result.CallId, result.Result));
    }

    /// <summary>
    /// Detects whether the LLM response was truncated by the output token limit.
    /// Different providers use different finish_reason strings for this condition.
    /// </summary>
    public static bool IsLengthTruncated(string? finishReason) =>
        string.Equals(finishReason, "length", StringComparison.OrdinalIgnoreCase)
        || string.Equals(finishReason, "max_tokens", StringComparison.OrdinalIgnoreCase);

}
