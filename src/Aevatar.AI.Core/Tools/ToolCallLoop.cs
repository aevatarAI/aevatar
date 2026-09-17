// ─────────────────────────────────────────────────────────────
// ToolCallLoop — Tool Calling 循环逻辑
// LLM 返回 tool_call → 执行 → 将结果加入历史 → 继续调 LLM
// 在每次 LLM 调用和 Tool 执行前后调用 Hook Pipeline + Middleware
// ─────────────────────────────────────────────────────────────

using Aevatar.AI.Core.Chat;
using Aevatar.AI.Core.Hooks;
using Aevatar.AI.Core.Middleware;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.Middleware;
using Aevatar.AI.Abstractions.ToolProviders;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Aevatar.AI.Core.Tools;

/// <summary>Tool Calling 循环。含 Hook + Middleware 集成。</summary>
public sealed class ToolCallLoop
{
    private readonly ToolManager _tools;
    private readonly AgentHookPipeline? _hooks;
    private readonly IReadOnlyList<ILLMCallMiddleware> _llmMiddlewares;
    private readonly TokenBudgetTracker? _budgetTracker;
    private readonly IAgentToolExecutionPort? _toolExecutionPort;
    private readonly AgentToolApprovalContinuationMode _approvalContinuationMode;

    public ToolCallLoop(
        ToolManager tools,
        AgentHookPipeline? hooks = null,
        IReadOnlyList<ILLMCallMiddleware>? llmMiddlewares = null,
        TokenBudgetTracker? budgetTracker = null,
        IAgentToolExecutionPort? toolExecutionPort = null,
        AgentToolApprovalContinuationMode approvalContinuationMode = AgentToolApprovalContinuationMode.None)
    {
        _tools = tools;
        _hooks = hooks;
        _llmMiddlewares = llmMiddlewares ?? [];
        _budgetTracker = budgetTracker;
        _toolExecutionPort = toolExecutionPort;
        _approvalContinuationMode = approvalContinuationMode;
    }

    internal IAgentToolExecutionPort? ToolExecutionPort => _toolExecutionPort;

    internal AgentToolApprovalContinuationMode ApprovalContinuationMode => _approvalContinuationMode;

    /// <summary>
    /// 执行 Tool Calling 循环。返回最终的 LLM 文本内容。
    /// 循环：LLM → tool_call → execute → result → LLM → ...
    /// 每次 LLM 调用和 Tool 执行前后触发 Hook + Middleware。
    /// </summary>
    public async Task<string?> ExecuteAsync(
        ILLMProvider provider, List<ChatMessage> messages,
        LLMRequest baseRequest, int maxRounds, CancellationToken ct)
    {
        // Refactor (iter24/cluster-002-agent-tool-context-generic-metadata-bag):
        //   Old pattern: ToolCallLoop pushed raw request Metadata into AsyncLocal.
        //   New principle: tool control semantics are typed context fields; Metadata is not the internal control plane.
        var toolContext = AgentToolExecutionContextMapper.FromRequest(baseRequest);
        using var _ = AgentToolContextScope.Push(toolContext);
        return await ExecuteCoreAsync(
            provider,
            messages,
            baseRequest,
            maxRounds,
            ct);
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
                Metadata = AgentToolExecutionContextMapper.StripOwnedControlKeys(baseRequest.Metadata),
                CallerContext = baseRequest.CallerContext,
                ToolContext = AgentToolExecutionContextMapper.FromRequestWithCallId(baseRequest, callId),
                RoutingContext = baseRequest.RoutingContext,
                LlmControl = baseRequest.LlmControl,
                RouteTarget = baseRequest.RouteTarget?.Clone(),
                Tools = baseRequest.Tools,
                ToolCatalogProof = baseRequest.ToolCatalogProof,
                Model = baseRequest.Model,
                Temperature = baseRequest.Temperature,
                MaxTokens = baseRequest.MaxTokens,
                AllowMultipleToolCalls = baseRequest.AllowMultipleToolCalls,
                ResponseFormat = baseRequest.ResponseFormat,
            };

            var (response, terminated, authorizedTools) = await InvokeLlmAsync(provider, request, ct);

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
                        messages.Add(ChatMessage.Assistant(response.Content, response.ReasoningContent));
                    return response.Content;
                }
            }

            if (terminated || !response.HasToolCalls)
            {
                // ─── Fallback: parse text-based function calls (DSML/XML) ───
                // Some LLMs emit tool invocations as DSML-formatted text instead of
                // structured FunctionCallContent. Detect and execute them.
                if (!terminated && response.Content != null)
                {
                    var parsed = TextToolCallParser.Parse(response.Content);
                    if (parsed.ToolCalls.Count > 0)
                    {
                        // Run PostSampling hook — same gate as structured calls
                        if (_hooks != null)
                        {
                            var postCtx = new AIGAgentExecutionHookContext
                            {
                                LLMResponse = new LLMResponse
                                {
                                    Content = parsed.CleanedContent,
                                    ReasoningContent = response.ReasoningContent,
                                    ToolCalls = parsed.ToolCalls,
                                },
                            };
                            postCtx.Items["tool_call_count"] = parsed.ToolCalls.Count;
                            await _hooks.RunPostSamplingAsync(postCtx, ct);

                            if (postCtx.Items.TryGetValue("block_tool_calls", out var block)
                                && block is true)
                            {
                                if (parsed.CleanedContent != null)
                                    messages.Add(ChatMessage.Assistant(parsed.CleanedContent, response.ReasoningContent));
                                return parsed.CleanedContent;
                            }
                        }

                        messages.Add(BuildAssistantToolCallMessage(
                            parsed.CleanedContent,
                            response.ReasoningContent,
                            parsed.ToolCalls));
                        await ExecuteToolCallsCoreAsync(
                            authorizedTools,
                            parsed.ToolCalls,
                            messages,
                            baseRequest.RequestId ?? "standalone-tool-loop",
                            ct);
                        accumulatedContent = null;
                        continue;
                    }
                }

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
                        messages.Add(ChatMessage.Assistant(response.Content, response.ReasoningContent));
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
                    messages.Add(ChatMessage.Assistant(resultContent, response.ReasoningContent));
                return resultContent;
            }

            // Tool call round resets accumulation — tool results break the text continuation.
            accumulatedContent = null;

            // 记录 assistant tool_call 消息
            messages.Add(new ChatMessage
            {
                Role = "assistant",
                Content = response.Content,
                ReasoningContent = response.ReasoningContent,
                ToolCalls = response.ToolCalls,
            });
            await ExecuteToolCallsCoreAsync(
                authorizedTools,
                response.ToolCalls!,
                messages,
                baseRequest.RequestId ?? "standalone-tool-loop",
                ct);
        }

        // maxRounds exhausted — tool results from the last round are already in messages.
        // Make one final LLM call WITHOUT tools so the model must produce a text response.
        var finalCallId = ComposeFinalCallId(baseRequest.RequestId);
        var finalRequest = new LLMRequest
        {
            Messages = [..messages],
            RequestId = baseRequest.RequestId,
            Metadata = AgentToolExecutionContextMapper.StripOwnedControlKeys(baseRequest.Metadata),
            CallerContext = baseRequest.CallerContext,
            ToolContext = AgentToolExecutionContextMapper.FromRequestWithCallId(baseRequest, finalCallId),
            RoutingContext = baseRequest.RoutingContext,
            LlmControl = baseRequest.LlmControl,
            RouteTarget = baseRequest.RouteTarget?.Clone(),
            Tools = null,
            ToolCatalogProof = AgentTurnToolCatalogProof.RestrictedEmpty(
                baseRequest.ToolCatalogProof?.Budget),
            Model = baseRequest.Model,
            Temperature = baseRequest.Temperature,
            MaxTokens = baseRequest.MaxTokens,
            AllowMultipleToolCalls = baseRequest.AllowMultipleToolCalls,
            ResponseFormat = baseRequest.ResponseFormat,
        };
        var (finalResponse, _, authorizedFinalTools) = await InvokeLlmAsync(provider, finalRequest, ct);
        var finalContent = finalResponse?.Content;

        // ─── Fallback: the final no-tools call may still contain DSML text calls ───
        if (finalContent != null)
        {
            var finalParsed = TextToolCallParser.Parse(finalContent);
            if (finalParsed.ToolCalls.Count > 0)
            {
                messages.Add(BuildAssistantToolCallMessage(
                    finalParsed.CleanedContent,
                    finalResponse?.ReasoningContent,
                    finalParsed.ToolCalls));
                await ExecuteToolCallsCoreAsync(
                    authorizedFinalTools,
                    finalParsed.ToolCalls,
                    messages,
                    baseRequest.RequestId ?? "standalone-tool-loop",
                    ct);

                // One more LLM call to summarize
                var summaryRequest = new LLMRequest
                {
                    Messages = [..messages],
                    RequestId = finalRequest.RequestId,
                    Metadata = finalRequest.Metadata,
                    CallerContext = finalRequest.CallerContext,
                    ToolContext = finalRequest.ToolContext,
                    RoutingContext = finalRequest.RoutingContext,
                    LlmControl = finalRequest.LlmControl,
                    RouteTarget = finalRequest.RouteTarget?.Clone(),
                    Tools = null,
                    ToolCatalogProof = finalRequest.ToolCatalogProof,
                    Model = finalRequest.Model,
                    Temperature = finalRequest.Temperature,
                    MaxTokens = finalRequest.MaxTokens,
                    AllowMultipleToolCalls = finalRequest.AllowMultipleToolCalls,
                    ResponseFormat = finalRequest.ResponseFormat,
                };
                var (summaryResponse, _, _) = await InvokeLlmAsync(provider, summaryRequest, ct);
                var summaryContent = summaryResponse?.Content;
                if (summaryContent != null)
                    messages.Add(ChatMessage.Assistant(summaryContent, summaryResponse?.ReasoningContent));
                return summaryContent;
            }

            messages.Add(ChatMessage.Assistant(finalContent, finalResponse?.ReasoningContent));
        }

        return finalContent;
    }

    internal async Task ExecuteToolCallsAsync(
        IReadOnlyList<ToolCall> toolCalls,
        List<ChatMessage> messages,
        IReadOnlyDictionary<string, string>? metadata,
        CancellationToken ct)
    {
        // Refactor (issue1574): Old pattern: standalone tool execution promoted Metadata into tool control.
        // New principle: core tool execution receives typed control; Metadata only supplies scrubbed annotations.
        using var _ = AgentToolContextScope.Push(AgentToolExecutionContext.Empty with
        {
            ExternalMetadata = AgentToolExecutionContextMapper.StripOwnedControlKeys(metadata),
        });
        await ExecuteToolCallsCoreAsync(
            _tools,
            toolCalls,
            messages,
            "standalone-tool-loop",
            ct);
    }

    private async Task<(LLMResponse Response, bool Terminated, ToolManager AuthorizedTools)> InvokeLlmAsync(
        ILLMProvider provider,
        LLMRequest request,
        CancellationToken ct)
    {
        // Refactor (iter15/cluster-024):
        //   Old pattern: non-streaming ChatAsync directly called provider.ChatAsync.
        //   New principle: ChatStreamAsync is the only authoritative AI executor; offline text aggregation consumes the stream as an explicit adapter.
        // ─── Hook: LLM Request Start ───
        var authorizationFence = ChatRuntimeRequestBuilder.CaptureAuthorizationFence(request);
        var hasRequestExtensionPoint = _hooks is not null || _llmMiddlewares.Count > 0;
        var catalogBoundRequest = authorizationFence.Apply(request, forceCopy: hasRequestExtensionPoint);
        var llmCtx = new AIGAgentExecutionHookContext { LLMRequest = catalogBoundRequest };
        if (_hooks != null) await _hooks.RunLLMRequestStartAsync(llmCtx, ct);
        var llmStartedAt = Stopwatch.GetTimestamp();

        var llmCallContext = new LLMCallContext
        {
            Request = authorizationFence.Apply(catalogBoundRequest),
            Provider = provider,
            CancellationToken = ct,
            IsStreaming = true,
        };
        AnnotateRequestIdentity(llmCallContext);

        ToolManager? authorizedTools = null;
        await MiddlewarePipeline.RunLLMCallAsync(_llmMiddlewares, llmCallContext, async () =>
        {
            if (llmCallContext.Terminate) return;
            var authorizedRequest = authorizationFence.Apply(llmCallContext.Request);
            llmCallContext.Request = authorizedRequest;
            authorizedTools = CreateRequestToolManager(authorizedRequest.Tools);
            llmCallContext.Response = await ChatStreamContentAggregator.AggregateResponseAsync(
                provider,
                authorizedRequest,
                ct);
        });
        authorizedTools ??= CreateRequestToolManager(
            authorizationFence.Apply(llmCallContext.Request).Tools);

        var response = llmCallContext.Response
            ?? new LLMResponse { Content = null, ToolCalls = null };
        _budgetTracker?.RecordUsage(response.Usage);
        llmCtx.LLMResponse = response;
        llmCtx.Duration = Stopwatch.GetElapsedTime(llmStartedAt);

        // ─── Hook: LLM Request End ───
        if (_hooks != null) await _hooks.RunLLMRequestEndAsync(llmCtx, ct);

        return (response, llmCallContext.Terminate, authorizedTools);
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

        var callId = context.Request.ToolContext?.Request.CallId;
        if (!string.IsNullOrWhiteSpace(callId))
        {
            context.Items[LLMRequestMetadataKeys.CallId] = callId;
        }
    }

    public static ChatMessage BuildToolResultMessage(
        string callId,
        string toolName,
        string toolResult,
        AgentToolReceipt? receipt = null)
    {
        if (!TryExtractToolContentParts(toolResult, out var text, out var parts))
        {
            return SkillRecoveryToolResultViews.Attach(
                ChatMessage.Tool(callId, toolResult),
                toolName,
                toolResult,
                receipt);
        }

        return SkillRecoveryToolResultViews.Attach(
            new ChatMessage
            {
                Role = "tool",
                ToolCallId = callId,
                Content = text,
                ContentParts = parts,
            },
            toolName,
            toolResult,
            receipt);
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
                TryGetNestedMediaBase64(root, "image", "image_base64", "imageBase64");
            var audioBase64 =
                TryGetStringByKeys(root, "audio_base64", "audioBase64") ??
                TryGetNestedMediaBase64(root, "audio", "audio_base64", "audioBase64");
            var videoBase64 =
                TryGetStringByKeys(root, "video_base64", "videoBase64") ??
                TryGetNestedMediaBase64(root, "video", "video_base64", "videoBase64");

            if (string.IsNullOrWhiteSpace(imageBase64) &&
                string.IsNullOrWhiteSpace(audioBase64) &&
                string.IsNullOrWhiteSpace(videoBase64))
            {
                imageBase64 = TryGetStringByKeys(root, "base64", "data");
            }

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

    private static string? TryGetNestedMediaBase64(JsonElement root, string propertyName, params string[] legacyAliasKeys)
    {
        if (!root.TryGetProperty(propertyName, out var media) || media.ValueKind != JsonValueKind.Object)
            return null;

        var keys = new List<string>(legacyAliasKeys.Length + 2);
        keys.AddRange(legacyAliasKeys);
        keys.Add("base64");
        keys.Add("data");
        return TryGetStringByKeys(media, [.. keys]);
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
            Task.FromException<string>(new InvalidOperationException($"Tool '{name}' was not found."));
    }

    internal static ToolManager CreateRequestToolManager(IReadOnlyList<IAgentTool>? tools)
    {
        var manager = new ToolManager();
        if (tools is { Count: > 0 })
            manager.Register(tools);
        return manager;
    }

    private async Task ExecuteToolCallsCoreAsync(
        ToolManager tools,
        IReadOnlyList<ToolCall> toolCalls,
        List<ChatMessage> messages,
        string sessionId,
        CancellationToken ct)
    {
        // Refactor (iter35/cluster-040-streaming-tool-executor):
        //   Old pattern: StreamingToolExecutor owns process-local channel coordinator + TaskCompletionSource waiters + List<TrackedTool>/List<TaskCompletionSource> as object fields for tool execution ordering.
        //   New principle: Tool execution state kept in owning chat/actor turn,或 narrow runtime-neutral tool scheduling abstraction(no process-local progress storage)。Streaming tool progress advanced by owning execution flow;process-local channels 仅作 transport mechanics,不作 business progress 来源。
        var executor = new StreamingToolExecutor(
            tools,
            _hooks,
            toolExecutionPort: _toolExecutionPort,
            approvalContinuationMode: _approvalContinuationMode);
        using var executionState = executor.CreateExecutionState();

        var prepared = await executor.PrepareBatchAsync(
            sessionId,
            round: 0,
            toolCalls,
            ct).ConfigureAwait(false);
        foreach (var operation in prepared)
            executor.AddTool(executionState, operation);

        await foreach (var result in executor.GetRemainingResultsAsync(executionState, ct))
        {
            messages.Add(BuildToolResultMessage(
                result.CallId,
                result.ToolName,
                ToolExecutionResultHistory.ResolveSafeContent(result),
                result.Receipt));
        }
    }

    private static ChatMessage BuildAssistantToolCallMessage(
        string? content,
        string? reasoningContent,
        IReadOnlyList<ToolCall> toolCalls) =>
        new()
        {
            Role = "assistant",
            Content = string.IsNullOrWhiteSpace(content) ? null : content,
            ReasoningContent = reasoningContent,
            ToolCalls = toolCalls,
        };

    /// <summary>
    /// Detects whether the LLM response was truncated by the output token limit.
    /// Different providers use different finish_reason strings for this condition.
    /// </summary>
    public static bool IsLengthTruncated(string? finishReason) =>
        string.Equals(finishReason, "length", StringComparison.OrdinalIgnoreCase)
        || string.Equals(finishReason, "max_tokens", StringComparison.OrdinalIgnoreCase);

}
