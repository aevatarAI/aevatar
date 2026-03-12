// ─────────────────────────────────────────────────────────────
// ToolCallLoop — Tool Calling 循环逻辑
// LLM 返回 tool_call → 执行 → 将结果加入历史 → 继续调 LLM
// 在每次 LLM 调用和 Tool 执行前后调用 Hook Pipeline + Middleware
// ─────────────────────────────────────────────────────────────

using System.Text.Json;
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

    public ToolCallLoop(
        ToolManager tools,
        AgentHookPipeline? hooks = null,
        IReadOnlyList<IToolCallMiddleware>? toolMiddlewares = null,
        IReadOnlyList<ILLMCallMiddleware>? llmMiddlewares = null)
    {
        _tools = tools;
        _hooks = hooks;
        _toolMiddlewares = toolMiddlewares ?? [];
        _llmMiddlewares = llmMiddlewares ?? [];
    }

    /// <summary>
    /// 执行 Tool Calling 循环。返回最终的 LLM 文本内容。
    /// 循环：LLM → tool_call → execute → result → LLM → ...
    /// 每次 LLM 调用和 Tool 执行前后触发 Hook + Middleware。
    /// </summary>
    public async Task<string?> ExecuteAsync(
        ILLMProvider provider, List<ChatMessage> messages,
        LLMRequest baseRequest, int maxRounds, CancellationToken ct)
    {
        for (var round = 0; round < maxRounds; round++)
        {
            var request = new LLMRequest
            {
                Messages = [..messages], Tools = baseRequest.Tools,
                Model = baseRequest.Model, Temperature = baseRequest.Temperature,
                MaxTokens = baseRequest.MaxTokens,
            };

            var (response, terminated) = await InvokeLlmAsync(provider, request, ct);

            if (terminated || !response.HasToolCalls)
            {
                if (response.Content != null)
                    messages.Add(ChatMessage.Assistant(response.Content));
                return response.Content;
            }

            // 记录 assistant tool_call 消息
            messages.Add(new ChatMessage { Role = "assistant", ToolCalls = response.ToolCalls });

            // 执行每个 tool call
            foreach (var call in response.ToolCalls!)
            {
                // ─── Hook: Tool Execute Start ───
                var toolCtx = new AIGAgentExecutionHookContext
                {
                    ToolName = call.Name, ToolArguments = call.ArgumentsJson, ToolCallId = call.Id,
                };
                if (_hooks != null) await _hooks.RunToolExecuteStartAsync(toolCtx, ct);

                var tool = _tools.Get(call.Name);
                var toolCallContext = new ToolCallContext
                {
                    Tool = tool ?? new NullAgentTool(call.Name),
                    ToolName = string.IsNullOrWhiteSpace(toolCtx.ToolName) ? call.Name : toolCtx.ToolName!,
                    ToolCallId = call.Id,
                    ArgumentsJson = toolCtx.ToolArguments ?? call.ArgumentsJson,
                    CancellationToken = ct,
                };

                await MiddlewarePipeline.RunToolCallAsync(_toolMiddlewares, toolCallContext, async () =>
                {
                    if (toolCallContext.Terminate) return;

                    var resolvedCall = new ToolCall
                    {
                        Id = toolCallContext.ToolCallId,
                        Name = toolCallContext.ToolName,
                        ArgumentsJson = toolCallContext.ArgumentsJson,
                    };

                    var result = await _tools.ExecuteToolCallAsync(resolvedCall, ct);
                    toolCallContext.Result = result.Content;
                });

                var toolResult = toolCallContext.Result ?? $"Tool '{toolCallContext.ToolName}' returned no result";
                if (toolCallContext.Terminate)
                    messages.Add(ChatMessage.Tool(call.Id, toolCallContext.Result ?? "Tool call terminated by middleware"));
                else
                    messages.Add(BuildToolResultMessage(call.Id, toolResult));

                // ─── Hook: Tool Execute End ───
                toolCtx.ToolResult = toolResult;
                if (_hooks != null) await _hooks.RunToolExecuteEndAsync(toolCtx, ct);
            }
        }

        // maxRounds exhausted — tool results from the last round are already in messages.
        // Make one final LLM call WITHOUT tools so the model must produce a text response.
        var finalRequest = new LLMRequest
        {
            Messages = [..messages], Tools = null,
            Model = baseRequest.Model, Temperature = baseRequest.Temperature,
            MaxTokens = baseRequest.MaxTokens,
        };
        var (finalResponse, _) = await InvokeLlmAsync(provider, finalRequest, ct);
        var finalContent = finalResponse?.Content;
        if (finalContent != null)
            messages.Add(ChatMessage.Assistant(finalContent));
        return finalContent;
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

        await MiddlewarePipeline.RunLLMCallAsync(_llmMiddlewares, llmCallContext, async () =>
        {
            if (llmCallContext.Terminate) return;
            llmCallContext.Response = await provider.ChatAsync(llmCallContext.Request, ct);
        });

        var response = llmCallContext.Response
            ?? new LLMResponse { Content = null, ToolCalls = null };
        llmCtx.LLMResponse = response;

        // ─── Hook: LLM Request End ───
        if (_hooks != null) await _hooks.RunLLMRequestEndAsync(llmCtx, ct);

        return (response, llmCallContext.Terminate);
    }

    private static ChatMessage BuildToolResultMessage(string callId, string toolResult)
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

    private sealed class NullAgentTool(string name) : IAgentTool
    {
        public string Name => name;
        public string Description => "";
        public string ParametersSchema => "{}";
        public ToolApprovalMode ApprovalMode => ToolApprovalMode.NeverRequire;

        public Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default) =>
            Task.FromResult($"Tool '{name}' not found");
    }
}
