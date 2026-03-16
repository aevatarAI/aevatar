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
        llmCtx.LLMResponse = response;

        // ─── Hook: LLM Request End ───
        if (_hooks != null) await _hooks.RunLLMRequestEndAsync(llmCtx, ct);

        return (response, llmCallContext.Terminate);
    }

    private static IReadOnlyDictionary<string, string>? BuildPerCallMetadata(
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

    private static string? ComposeRoundCallId(string? baseRequestId, int round)
    {
        if (string.IsNullOrWhiteSpace(baseRequestId))
            return null;

        return round <= 0
            ? baseRequestId
            : $"{baseRequestId}:tool-round:{round + 1}";
    }

    private static string? ComposeFinalCallId(string? baseRequestId)
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
                TryGetStringByKeys(root, "image_base64", "imageBase64") ??
                TryGetNestedMediaBase64(root, "image");
            var audioBase64 =
                TryGetStringByKeys(root, "audio_base64", "audioBase64") ??
                TryGetNestedMediaBase64(root, "audio");
            var videoBase64 =
                TryGetStringByKeys(root, "video_base64", "videoBase64") ??
                TryGetNestedMediaBase64(root, "video");

            var kind = ResolveMediaKind(imageBase64, audioBase64, videoBase64);
            var dataBase64 = kind switch
            {
                ContentPartKind.Image => imageBase64,
                ContentPartKind.Audio => audioBase64,
                ContentPartKind.Video => videoBase64,
                _ => null,
            };

            if (string.IsNullOrWhiteSpace(dataBase64))
                return false;

            var mediaType =
                TryGetStringByKeys(
                    root,
                    kind switch
                    {
                        ContentPartKind.Image => "image_media_type",
                        ContentPartKind.Audio => "audio_media_type",
                        ContentPartKind.Video => "video_media_type",
                        _ => "media_type",
                    },
                    "mime_type",
                    "mimeType",
                    "media_type",
                    "mediaType",
                    "content_type") ??
                TryGetNestedMediaType(root, kind) ??
                DefaultMediaType(kind);

            // Accept data-uri output and normalize into raw base64 + media type.
            var normalizedBase64 = dataBase64!.Trim();
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
                kind switch
                {
                    ContentPartKind.Audio => "[tool audio output]",
                    ContentPartKind.Video => "[tool video output]",
                    _ => "[tool image output]",
                };
            contentParts =
            [
                ContentPart.TextPart(text),
                CreateMediaPart(kind, normalizedBase64, mediaType),
            ];
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string? TryGetNestedMediaBase64(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var media) || media.ValueKind != JsonValueKind.Object)
            return null;
        return TryGetStringByKeys(media, "base64", "data");
    }

    private static string? TryGetNestedMediaType(JsonElement root, ContentPartKind kind)
    {
        var propertyName = kind switch
        {
            ContentPartKind.Audio => "audio",
            ContentPartKind.Video => "video",
            _ => "image",
        };

        if (!root.TryGetProperty(propertyName, out var media) || media.ValueKind != JsonValueKind.Object)
            return null;
        return TryGetStringByKeys(media, "media_type", "mime_type", "mediaType", "mimeType", "content_type");
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

    private static ContentPartKind ResolveMediaKind(string? imageBase64, string? audioBase64, string? videoBase64)
    {
        if (!string.IsNullOrWhiteSpace(imageBase64))
            return ContentPartKind.Image;
        if (!string.IsNullOrWhiteSpace(audioBase64))
            return ContentPartKind.Audio;
        if (!string.IsNullOrWhiteSpace(videoBase64))
            return ContentPartKind.Video;
        return ContentPartKind.Unspecified;
    }

    private static string DefaultMediaType(ContentPartKind kind) =>
        kind switch
        {
            ContentPartKind.Audio => "audio/wav",
            ContentPartKind.Video => "video/mp4",
            _ => "image/png",
        };

    private static ContentPart CreateMediaPart(ContentPartKind kind, string dataBase64, string mediaType) =>
        kind switch
        {
            ContentPartKind.Audio => ContentPart.AudioPart(dataBase64, mediaType),
            ContentPartKind.Video => ContentPart.VideoPart(dataBase64, mediaType),
            _ => ContentPart.ImagePart(dataBase64, mediaType),
        };

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
