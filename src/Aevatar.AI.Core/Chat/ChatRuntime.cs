// ─── ChatRuntime — Chat/ChatStream 执行逻辑 ───
// 组合 LLMProvider + History + ToolCallLoop + Hooks + Middleware。

using Aevatar.AI.Core.Hooks;
using Aevatar.AI.Core.Middleware;
using Aevatar.AI.Core.Tools;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.Middleware;
using Aevatar.AI.Abstractions.ToolProviders;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Channels;

namespace Aevatar.AI.Core.Chat;

/// <summary>上下文压缩配置。</summary>
/// <param name="MaxPromptTokenBudget">Prompt token 预算上限。0 = 禁用。</param>
/// <param name="CompressionThreshold">触发压缩的阈值比例（0.5~0.99）。</param>
/// <param name="EnableSummarization">是否启用 LLM 摘要压缩（Level 3）。</param>
public sealed record ContextCompressionConfig(
    int MaxPromptTokenBudget = 0,
    double CompressionThreshold = 0.85,
    bool EnableSummarization = false);

/// <summary>Chat 执行运行时。调 LLM，管理历史，集成 Middleware。</summary>
public sealed class ChatRuntime
{
    /// <summary>
    /// Default max tool rounds. int.MaxValue = no artificial limit;
    /// the loop runs until the LLM stops calling tools (matching Claude Code behaviour).
    /// </summary>
    private const int DefaultMaxToolRounds = int.MaxValue;
    private readonly Func<ILLMProvider> _providerFactory;
    private readonly ChatHistory _history;
    private readonly ToolCallLoop _toolLoop;
    private readonly AgentHookPipeline? _hooks;
    private readonly Func<LLMRequest> _requestBuilder;
    private readonly IReadOnlyList<IAgentRunMiddleware> _agentMiddlewares;
    private readonly IReadOnlyList<ILLMCallMiddleware> _llmMiddlewares;
    private readonly string? _agentId;
    private readonly string? _agentName;
    private readonly int _streamBufferCapacity;
    private readonly ContextCompressionConfig _compressionConfig;

    public ChatRuntime(
        Func<ILLMProvider> providerFactory,
        ChatHistory history,
        ToolCallLoop toolLoop,
        AgentHookPipeline? hooks,
        Func<LLMRequest> requestBuilder,
        IReadOnlyList<IAgentRunMiddleware>? agentMiddlewares = null,
        IReadOnlyList<ILLMCallMiddleware>? llmMiddlewares = null,
        string? agentId = null,
        string? agentName = null,
        int streamBufferCapacity = 256,
        ContextCompressionConfig? compressionConfig = null)
    {
        _providerFactory = providerFactory;
        _history = history;
        _toolLoop = toolLoop;
        _hooks = hooks;
        _requestBuilder = requestBuilder;
        _agentMiddlewares = agentMiddlewares ?? [];
        _llmMiddlewares = llmMiddlewares ?? [];
        _agentId = string.IsNullOrWhiteSpace(agentId) ? null : agentId;
        _agentName = string.IsNullOrWhiteSpace(agentName) ? null : agentName;
        _streamBufferCapacity = streamBufferCapacity > 0
            ? streamBufferCapacity
            : throw new ArgumentOutOfRangeException(nameof(streamBufferCapacity), "Stream buffer capacity must be greater than zero.");
        _compressionConfig = compressionConfig ?? new ContextCompressionConfig();
    }

    /// <summary>单轮 Chat（含 Tool Calling 循环），包裹 Agent Run Middleware。</summary>
    public Task<string?> ChatAsync(string userMessage, int maxToolRounds = DefaultMaxToolRounds, CancellationToken ct = default) =>
        ChatAsync([ContentPart.TextPart(userMessage)], maxToolRounds, requestId: null, metadata: null, ct);

    /// <summary>单轮 Chat（含 Tool Calling 循环），显式传入稳定 request id 和 metadata（文本快捷方式）。</summary>
    public Task<string?> ChatAsync(
        string userMessage,
        int maxToolRounds,
        string? requestId,
        IReadOnlyDictionary<string, string>? metadata,
        CancellationToken ct = default) =>
        ChatAsync([ContentPart.TextPart(userMessage)], maxToolRounds, requestId, metadata, ct);

    /// <summary>单轮 Chat（多模态内容），显式传入稳定 request id 和 metadata。</summary>
    public async Task<string?> ChatAsync(
        IReadOnlyList<ContentPart> userContent,
        int maxToolRounds,
        string? requestId,
        IReadOnlyDictionary<string, string>? metadata,
        CancellationToken ct = default)
    {
        var normalizedUserContent = NormalizeUserContent(userContent);
        var runContext = new AgentRunContext
        {
            UserMessage = DescribeUserContent(normalizedUserContent),
            AgentId = _agentId,
            AgentName = _agentName,
            CancellationToken = ct,
        };

        try
        {
            await MiddlewarePipeline.RunAgentAsync(_agentMiddlewares, runContext, async () =>
            {
                if (runContext.Terminate) return;

                _history.Add(ChatMessage.User(normalizedUserContent, runContext.UserMessage));
                await RunCompressionIfNeededAsync(ct);
                var baseRequest = ApplyRequestIdentity(_requestBuilder(), requestId, metadata);
                var provider = _providerFactory();
                runContext.Items["gen_ai.provider.name"] = provider.Name;
                var messages = _history.BuildMessages(baseRequest.Messages.FirstOrDefault(m => m.Role == "system")?.Content);

                var result = await _toolLoop.ExecuteAsync(provider, messages, baseRequest, maxToolRounds, ct);

                _history.Clear();
                foreach (var m in messages.Where(m => m.Role != "system"))
                    _history.Add(m);

                runContext.Result = result;
            });

            // ─── Hook: Stop（轮次正常完成） ───
            if (_hooks != null)
            {
                var stopCtx = new AIGAgentExecutionHookContext { AgentId = _agentId };
                stopCtx.Items["final_content"] = runContext.Result ?? "";
                // Count tool-calling rounds by counting assistant messages that have tool calls
                stopCtx.Items["total_rounds"] = _history.Messages
                    .Count(m => m.Role == "assistant" && m.ToolCalls is { Count: > 0 });
                await _hooks.RunStopAsync(stopCtx, ct);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // ─── Hook: StopFailure（轮次因错误终止） ───
            if (_hooks != null)
            {
                var failCtx = new AIGAgentExecutionHookContext { AgentId = _agentId };
                failCtx.Items["error"] = ex;
                failCtx.Items["error_message"] = ex.Message;
                failCtx.Items["error_phase"] = "llm_or_tool_execution";
                try { await _hooks.RunStopFailureAsync(failCtx, ct); }
                catch { /* best-effort */ }
            }
            throw;
        }

        return runContext.Result;
    }

    /// <summary>流式 Chat，包裹 LLM Call Middleware。</summary>
    public IAsyncEnumerable<LLMStreamChunk> ChatStreamAsync(
        string userMessage,
        CancellationToken ct = default) =>
        ChatStreamAsync([ContentPart.TextPart(userMessage)], DefaultMaxToolRounds, requestId: null, metadata: null, ct);

    /// <summary>流式 Chat（多模态内容）。</summary>
    public IAsyncEnumerable<LLMStreamChunk> ChatStreamAsync(
        IReadOnlyList<ContentPart> userContent,
        CancellationToken ct = default) =>
        ChatStreamAsync(userContent, DefaultMaxToolRounds, requestId: null, metadata: null, ct);

    /// <summary>流式 Chat，允许显式控制 tool calling 轮数。</summary>
    public IAsyncEnumerable<LLMStreamChunk> ChatStreamAsync(
        string userMessage,
        int maxToolRounds,
        CancellationToken ct = default) =>
        ChatStreamAsync([ContentPart.TextPart(userMessage)], maxToolRounds, requestId: null, metadata: null, ct);

    /// <summary>流式 Chat（多模态内容），允许显式控制 tool calling 轮数。</summary>
    public IAsyncEnumerable<LLMStreamChunk> ChatStreamAsync(
        IReadOnlyList<ContentPart> userContent,
        int maxToolRounds,
        CancellationToken ct = default) =>
        ChatStreamAsync(userContent, maxToolRounds, requestId: null, metadata: null, ct);

    /// <summary>流式 Chat，显式传入稳定 request id 和 metadata（默认 tool 轮数）。</summary>
    public IAsyncEnumerable<LLMStreamChunk> ChatStreamAsync(
        string userMessage,
        string? requestId,
        IReadOnlyDictionary<string, string>? metadata = null,
        CancellationToken ct = default) =>
        ChatStreamAsync([ContentPart.TextPart(userMessage)], DefaultMaxToolRounds, requestId, metadata, ct);

    /// <summary>流式 Chat，显式传入稳定 request id 和 metadata + tool 轮数。</summary>
    public async IAsyncEnumerable<LLMStreamChunk> ChatStreamAsync(
        string userMessage,
        int maxToolRounds,
        string? requestId,
        IReadOnlyDictionary<string, string>? metadata = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var chunk in ChatStreamAsync(
                           [ContentPart.TextPart(userMessage)],
                           maxToolRounds,
                           requestId,
                           metadata,
                           ct))
        {
            yield return chunk;
        }
    }

    /// <summary>流式 Chat（多模态内容），显式传入稳定 request id / metadata / tool 调用轮数。</summary>
    public async IAsyncEnumerable<LLMStreamChunk> ChatStreamAsync(
        IReadOnlyList<ContentPart> userContent,
        int maxToolRounds,
        string? requestId,
        IReadOnlyDictionary<string, string>? metadata = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var normalizedUserContent = NormalizeUserContent(userContent);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var runToken = linkedCts.Token;
        var effectiveMaxToolRounds = maxToolRounds > 0 ? maxToolRounds : DefaultMaxToolRounds;

        var channel = Channel.CreateBounded<LLMStreamChunk>(new BoundedChannelOptions(_streamBufferCapacity)
        {
            SingleReader = true,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.Wait,
        });

        var runContext = new AgentRunContext
        {
            UserMessage = DescribeUserContent(normalizedUserContent),
            AgentId = _agentId,
            AgentName = _agentName,
            CancellationToken = runToken,
        };

        // The background task collects history mutations and returns them as its result.
        // The caller applies them to _history after awaiting, making the producer-consumer
        // contract explicit through the Task<List<ChatMessage>> return type.
        var runTask = Task.Run(async () =>
        {
            var pendingHistoryMessages = new List<ChatMessage>();
            var wroteOutput = false;
            try
            {
                await MiddlewarePipeline.RunAgentAsync(_agentMiddlewares, runContext, async () =>
                {
                    if (runContext.Terminate) return;

                    await RunCompressionIfNeededAsync(runToken);
                    var userMsg = ChatMessage.User(normalizedUserContent, runContext.UserMessage);
                    pendingHistoryMessages.Add(userMsg);
                    var baseRequest = ApplyRequestIdentity(_requestBuilder(), requestId, metadata);
                    var provider = _providerFactory();
                    runContext.Items["gen_ai.provider.name"] = provider.Name;
                    // Build messages from a local snapshot + pending user message instead of mutating _history.
                    var messages = BuildMessagesWithPending(baseRequest, userMsg);
                    string? finalContent = null;
                    var lengthRecoveryCount = 0;
                    var hasStreamedTextContent = false;

                    for (var round = 0; round < effectiveMaxToolRounds; round++)
                    {
                        // Emit a paragraph separator between agent loop rounds so the
                        // frontend can visually separate each "thinking pass".
                        // Only if a prior round actually streamed text content (not just tool calls).
                        if (hasStreamedTextContent)
                        {
                            await channel.Writer.WriteAsync(
                                new LLMStreamChunk { DeltaContent = "\n\n" }, runToken);
                        }

                        // Create a streaming tool executor for mid-stream dispatch.
                        // Tools start executing as soon as their tool_use block completes
                        // in the stream, before the full LLM response finishes.
                        //
                        // HOWEVER: when PostSampling hooks are configured, we must defer
                        // tool dispatch until after the hook runs — the hook may block all
                        // tool calls. In that case we collect tool calls into a list first,
                        // then dispatch after PostSampling approves.
                        using var streamingExecutor = new StreamingToolExecutor(
                            _toolLoop.Tools, _hooks, _toolLoop.ToolMiddlewares,
                            requestMetadata: baseRequest.Metadata);

                        List<ToolCall>? deferredToolCalls = _hooks != null ? [] : null;

                        var roundRequest = new LLMRequest
                        {
                            Messages = [..messages],
                            RequestId = baseRequest.RequestId,
                            Metadata = ToolCallLoop.BuildPerCallMetadata(
                                baseRequest.Metadata,
                                ToolCallLoop.ComposeRoundCallId(baseRequest.RequestId, round)),
                            Tools = baseRequest.Tools,
                            Model = baseRequest.Model,
                            Temperature = baseRequest.Temperature,
                            MaxTokens = baseRequest.MaxTokens,
                        };
                        var roundResult = await StreamLlmRoundAsync(
                            provider,
                            roundRequest,
                            channel.Writer,
                            runToken,
                            () => wroteOutput = true,
                            onToolCallCompleted: toolCall =>
                            {
                                if (deferredToolCalls != null)
                                    deferredToolCalls.Add(toolCall);
                                else
                                    streamingExecutor.AddTool(toolCall);
                            });

                        if (!string.IsNullOrEmpty(roundResult.Content))
                            hasStreamedTextContent = true;

                        if (roundResult.Terminated)
                        {
                            streamingExecutor.Discard();
                            AppendAssistantMessage(messages, pendingHistoryMessages, roundResult.Content, roundResult.ToolCalls);
                            finalContent = roundResult.Content;
                            break;
                        }

                        if (roundResult.ToolCalls is not { Count: > 0 })
                        {
                            // ─── Fallback: parse text-based function calls (DSML/XML) ───
                            if (roundResult.Content != null)
                            {
                                var parsed = TextToolCallParser.Parse(roundResult.Content);
                                if (parsed.ToolCalls.Count > 0)
                                {
                                    // Run PostSampling hook — same gate as structured calls
                                    var fallbackBlocked = false;
                                    if (_hooks != null)
                                    {
                                        var postCtx = new AIGAgentExecutionHookContext
                                        {
                                            LLMResponse = new LLMResponse
                                            {
                                                Content = parsed.CleanedContent,
                                                ToolCalls = parsed.ToolCalls,
                                            },
                                        };
                                        postCtx.Items["tool_call_count"] = parsed.ToolCalls.Count;
                                        await _hooks.RunPostSamplingAsync(postCtx, runToken);

                                        if (postCtx.Items.TryGetValue("block_tool_calls", out var block)
                                            && block is true)
                                        {
                                            fallbackBlocked = true;
                                        }
                                    }

                                    if (fallbackBlocked)
                                    {
                                        AppendAssistantMessage(messages, pendingHistoryMessages, parsed.CleanedContent, toolCalls: null);
                                        finalContent = parsed.CleanedContent;
                                        break;
                                    }

                                    AppendAssistantMessage(messages, pendingHistoryMessages, parsed.CleanedContent, toolCalls: null);

                                    var textToolCallMsg = new ChatMessage
                                    {
                                        Role = "assistant",
                                        ToolCalls = parsed.ToolCalls,
                                    };
                                    messages.Add(textToolCallMsg);
                                    pendingHistoryMessages.Add(textToolCallMsg);

                                    // Execute parsed tool calls via a fresh executor
                                    using var textToolExecutor = new StreamingToolExecutor(
                                        _toolLoop.Tools, _hooks, _toolLoop.ToolMiddlewares,
                                        requestMetadata: baseRequest.Metadata);
                                    foreach (var tc in parsed.ToolCalls)
                                        textToolExecutor.AddTool(tc);
                                    await foreach (var result in textToolExecutor.GetRemainingResultsAsync(runToken))
                                    {
                                        var toolMsg = ToolCallLoop.BuildToolResultMessage(result.CallId, result.Result);
                                        messages.Add(toolMsg);
                                        pendingHistoryMessages.Add(toolMsg);
                                    }

                                    continue; // next round
                                }
                            }

                            // Recovery: if truncated by max_tokens, inject continuation nudge and retry.
                            if (ToolCallLoop.IsLengthTruncated(roundResult.FinishReason)
                                && lengthRecoveryCount < ToolCallLoop.MaxLengthRecoveries)
                            {
                                AppendAssistantMessage(messages, pendingHistoryMessages, roundResult.Content, toolCalls: null);
                                var nudge = ChatMessage.User(ToolCallLoop.LengthRecoveryNudge);
                                messages.Add(nudge);
                                pendingHistoryMessages.Add(nudge);
                                lengthRecoveryCount++;
                                continue;
                            }

                            AppendAssistantMessage(messages, pendingHistoryMessages, roundResult.Content, toolCalls: null);
                            finalContent = roundResult.Content;
                            break;
                        }

                        // ─── Hook: Post-Sampling（流式路径：LLM 输出完成后、tool 调度前） ───
                        // When hooks are configured, tool calls were deferred (not dispatched
                        // mid-stream). Run PostSampling first; if it blocks, discard everything.
                        // Otherwise, dispatch the deferred tool calls now.
                        if (_hooks != null)
                        {
                            var postSamplingCtx = new AIGAgentExecutionHookContext
                            {
                                LLMResponse = new LLMResponse
                                {
                                    Content = roundResult.Content,
                                    ToolCalls = roundResult.ToolCalls,
                                },
                            };
                            postSamplingCtx.Items["tool_call_count"] = roundResult.ToolCalls?.Count ?? 0;
                            await _hooks.RunPostSamplingAsync(postSamplingCtx, runToken);

                            if (postSamplingCtx.Items.TryGetValue("block_tool_calls", out var block)
                                && block is true)
                            {
                                AppendAssistantMessage(messages, pendingHistoryMessages, roundResult.Content, toolCalls: null);
                                finalContent = roundResult.Content;
                                break;
                            }

                            // PostSampling approved — dispatch deferred tool calls
                            if (deferredToolCalls != null)
                            {
                                foreach (var tc in deferredToolCalls)
                                    streamingExecutor.AddTool(tc);
                            }
                        }

                        var assistantToolCallMessage = new ChatMessage
                        {
                            Role = "assistant",
                            ToolCalls = roundResult.ToolCalls,
                        };
                        messages.Add(assistantToolCallMessage);
                        pendingHistoryMessages.Add(assistantToolCallMessage);

                        // Collect results from the streaming executor (tools already started mid-stream).
                        // Metadata is propagated inside the executor via its constructor parameter.
                        await foreach (var result in streamingExecutor.GetRemainingResultsAsync(runToken))
                        {
                            var toolMsg = ToolCallLoop.BuildToolResultMessage(result.CallId, result.Result);
                            messages.Add(toolMsg);
                            pendingHistoryMessages.Add(toolMsg);
                        }
                    }

                    if (finalContent == null)
                    {
                        if (hasStreamedTextContent)
                        {
                            await channel.Writer.WriteAsync(
                                new LLMStreamChunk { DeltaContent = "\n\n" }, runToken);
                        }

                        var finalRequest = new LLMRequest
                        {
                            Messages = [..messages],
                            RequestId = baseRequest.RequestId,
                            Metadata = ToolCallLoop.BuildPerCallMetadata(
                                baseRequest.Metadata,
                                ToolCallLoop.ComposeFinalCallId(baseRequest.RequestId)),
                            Tools = null,
                            Model = baseRequest.Model,
                            Temperature = baseRequest.Temperature,
                            MaxTokens = baseRequest.MaxTokens,
                        };
                        var finalRound = await StreamLlmRoundAsync(
                            provider,
                            finalRequest,
                            channel.Writer,
                            runToken,
                            () => wroteOutput = true);

                        // ─── Fallback: the final no-tools call may still contain DSML text calls ───
                        // When maxRounds is exhausted, LLM is called without tools. If it outputs
                        // DSML/XML function call blocks as text, parse and execute them.
                        var finalParsed = finalRound.Content != null
                            ? TextToolCallParser.Parse(finalRound.Content)
                            : null;
                        if (finalParsed?.ToolCalls.Count > 0)
                        {
                            AppendAssistantMessage(messages, pendingHistoryMessages, finalParsed.CleanedContent, toolCalls: null);

                            var finalToolCallMsg = new ChatMessage
                            {
                                Role = "assistant",
                                ToolCalls = finalParsed.ToolCalls,
                            };
                            messages.Add(finalToolCallMsg);
                            pendingHistoryMessages.Add(finalToolCallMsg);

                            using var finalToolExecutor = new StreamingToolExecutor(
                                _toolLoop.Tools, _hooks, _toolLoop.ToolMiddlewares,
                                requestMetadata: baseRequest.Metadata);
                            foreach (var tc in finalParsed.ToolCalls)
                                finalToolExecutor.AddTool(tc);
                            await foreach (var result in finalToolExecutor.GetRemainingResultsAsync(runToken))
                            {
                                var toolMsg = ToolCallLoop.BuildToolResultMessage(result.CallId, result.Result);
                                messages.Add(toolMsg);
                                pendingHistoryMessages.Add(toolMsg);
                            }

                            // One more LLM call to summarize (still without tools).
                            // Use the updated message list so the model can see the
                            // tool results produced by the parsed final-round call.
                            var summaryRequest = new LLMRequest
                            {
                                Messages = [..messages],
                                RequestId = finalRequest.RequestId,
                                Metadata = finalRequest.Metadata,
                                Tools = null,
                                Model = finalRequest.Model,
                                Temperature = finalRequest.Temperature,
                                MaxTokens = finalRequest.MaxTokens,
                            };
                            var summaryRound = await StreamLlmRoundAsync(
                                provider, summaryRequest, channel.Writer, runToken,
                                () => wroteOutput = true);
                            AppendAssistantMessage(messages, pendingHistoryMessages, summaryRound.Content, toolCalls: null);
                            finalContent = summaryRound.Content;
                        }
                        else
                        {
                            AppendAssistantMessage(messages, pendingHistoryMessages, finalRound.Content, toolCalls: null);
                            finalContent = finalRound.Content;
                        }
                    }

                    runContext.Result = finalContent;
                });

                if (runContext.Terminate && runContext.Result != null && !wroteOutput)
                {
                    await channel.Writer.WriteAsync(
                        new LLMStreamChunk { DeltaContent = runContext.Result },
                        runToken);
                }

                // ─── Hook: Stop（流式轮次正常完成） ───
                if (_hooks != null)
                {
                    var stopCtx = new AIGAgentExecutionHookContext { AgentId = _agentId };
                    stopCtx.Items["final_content"] = runContext.Result ?? "";
                    stopCtx.Items["total_rounds"] = pendingHistoryMessages
                        .Count(m => m.Role == "assistant" && m.ToolCalls is { Count: > 0 });
                    try { await _hooks.RunStopAsync(stopCtx, runToken); }
                    catch { /* best-effort */ }
                }

                channel.Writer.TryComplete();
                return pendingHistoryMessages;
            }
            catch (Exception ex)
            {
                // ─── Hook: StopFailure（流式轮次因错误终止） ───
                if (_hooks != null && ex is not OperationCanceledException)
                {
                    var failCtx = new AIGAgentExecutionHookContext { AgentId = _agentId };
                    failCtx.Items["error"] = ex;
                    failCtx.Items["error_message"] = ex.Message;
                    failCtx.Items["error_phase"] = "streaming_llm_or_tool_execution";
                    try { await _hooks.RunStopFailureAsync(failCtx, CancellationToken.None); }
                    catch { /* best-effort */ }
                }

                channel.Writer.TryComplete(ex);
                return pendingHistoryMessages;
            }
        });

        try
        {
            await foreach (var chunk in channel.Reader.ReadAllAsync(runToken))
                yield return chunk;
        }
        finally
        {
            linkedCts.Cancel();
            List<ChatMessage>? collectedHistory = null;
            try
            {
                collectedHistory = await runTask.ConfigureAwait(false);
            }
            catch { /* best-effort — errors already surfaced via channel */ }

            // Apply collected history mutations on the caller context after the background task completes.
            if (collectedHistory != null)
            {
                foreach (var msg in collectedHistory)
                    _history.Add(msg);
            }
        }
    }

    private Task<StreamingRoundResult> StreamLlmRoundAsync(
        ILLMProvider provider,
        LLMRequest request,
        ChannelWriter<LLMStreamChunk> writer,
        CancellationToken ct,
        Action markOutputWritten,
        Action<ToolCall>? onToolCallCompleted = null)
    {
        return StreamLlmRoundCoreAsync(provider, request, writer, ct, markOutputWritten, onToolCallCompleted);
    }

    private async Task<StreamingRoundResult> StreamLlmRoundCoreAsync(
        ILLMProvider provider,
        LLMRequest request,
        ChannelWriter<LLMStreamChunk> writer,
        CancellationToken ct,
        Action markOutputWritten,
        Action<ToolCall>? onToolCallCompleted)
    {
        var llmHookContext = new AIGAgentExecutionHookContext { LLMRequest = request };
        if (_hooks != null) await _hooks.RunLLMRequestStartAsync(llmHookContext, ct);

        var llmCallContext = new LLMCallContext
        {
            Request = request,
            Provider = provider,
            CancellationToken = ct,
            IsStreaming = true,
        };
        AnnotateRequestIdentity(llmCallContext);

        string? streamedContent = null;
        TokenUsage? streamedUsage = null;
        IReadOnlyList<ToolCall>? streamedToolCalls = null;
        string? streamedFinishReason = null;

        await MiddlewarePipeline.RunLLMCallAsync(_llmMiddlewares, llmCallContext, async () =>
        {
            if (llmCallContext.Terminate) return;

            var full = new StringBuilder();
            TokenUsage? usage = null;
            string? finishReason = null;
            var toolCalls = onToolCallCompleted != null
                ? new StreamingToolCallAccumulator(onToolCallCompleted)
                : new StreamingToolCallAccumulator();

            await foreach (var chunk in provider.ChatStreamAsync(llmCallContext.Request, ct))
            {
                var normalizedChunk = NormalizeStreamChunk(chunk, toolCalls, full, ref usage, ref finishReason);
                if (normalizedChunk == null)
                    continue;

                await writer.WriteAsync(normalizedChunk, ct);
                markOutputWritten();
            }

            streamedContent = full.Length > 0 ? full.ToString() : null;
            streamedUsage = usage;
            streamedFinishReason = finishReason;
            var finalizedToolCalls = toolCalls.BuildToolCalls();
            streamedToolCalls = finalizedToolCalls.Count > 0 ? finalizedToolCalls : null;
            llmCallContext.Response = new LLMResponse
            {
                Content = streamedContent,
                Usage = streamedUsage,
                ToolCalls = streamedToolCalls,
                FinishReason = finishReason,
            };
        });

        if (llmCallContext.Terminate)
        {
            streamedContent = llmCallContext.Response?.Content;
            streamedUsage = llmCallContext.Response?.Usage;
            streamedToolCalls = llmCallContext.Response?.ToolCalls;

            if (llmCallContext.Response != null)
            {
                foreach (var chunk in BuildSyntheticChunks(llmCallContext.Response))
                {
                    await writer.WriteAsync(chunk, ct);
                    markOutputWritten();
                }
            }
        }

        var response = llmCallContext.Response ?? new LLMResponse
        {
            Content = streamedContent,
            Usage = streamedUsage,
            ToolCalls = streamedToolCalls,
        };
        _history.Budget.RecordUsage(response.Usage);
        llmHookContext.LLMResponse = response;
        if (_hooks != null) await _hooks.RunLLMRequestEndAsync(llmHookContext, ct);

        return new StreamingRoundResult(response.Content, response.ToolCalls, llmCallContext.Terminate, response.FinishReason ?? streamedFinishReason);
    }

    private static void AppendAssistantMessage(
        List<ChatMessage> messages,
        List<ChatMessage> pendingHistoryMessages,
        string? content,
        IReadOnlyList<ToolCall>? toolCalls)
    {
        if (string.IsNullOrEmpty(content) && toolCalls is not { Count: > 0 })
            return;

        var assistantMessage = new ChatMessage
        {
            Role = "assistant",
            Content = content,
            ToolCalls = toolCalls,
        };
        messages.Add(assistantMessage);
        pendingHistoryMessages.Add(assistantMessage);
    }

    /// <summary>
    /// Build the LLM messages list from the current history snapshot plus a pending user message,
    /// without mutating <see cref="_history"/>. Used by the streaming path to avoid cross-thread mutation.
    /// </summary>
    private List<ChatMessage> BuildMessagesWithPending(LLMRequest baseRequest, ChatMessage pendingUserMessage)
    {
        var systemPrompt = baseRequest.Messages.FirstOrDefault(m => m.Role == "system")?.Content;
        var messages = new List<ChatMessage>();
        if (!string.IsNullOrEmpty(systemPrompt))
            messages.Add(ChatMessage.System(systemPrompt));
        messages.AddRange(_history.Messages);
        messages.Add(pendingUserMessage);
        return messages;
    }

    private static LLMRequest ApplyRequestIdentity(
        LLMRequest baseRequest,
        string? requestId,
        IReadOnlyDictionary<string, string>? metadata)
    {
        return new LLMRequest
        {
            Messages = baseRequest.Messages,
            RequestId = string.IsNullOrWhiteSpace(requestId) ? baseRequest.RequestId : requestId.Trim(),
            Metadata = MergeMetadata(baseRequest.Metadata, metadata),
            Tools = baseRequest.Tools,
            Model = baseRequest.Model,
            Temperature = baseRequest.Temperature,
            MaxTokens = baseRequest.MaxTokens,
        };
    }

    private static IReadOnlyDictionary<string, string>? MergeMetadata(
        IReadOnlyDictionary<string, string>? baseMetadata,
        IReadOnlyDictionary<string, string>? overrideMetadata)
    {
        if ((baseMetadata == null || baseMetadata.Count == 0) &&
            (overrideMetadata == null || overrideMetadata.Count == 0))
        {
            return null;
        }

        var merged = new Dictionary<string, string>(StringComparer.Ordinal);
        if (baseMetadata != null)
        {
            foreach (var pair in baseMetadata)
                merged[pair.Key] = pair.Value;
        }

        if (overrideMetadata != null)
        {
            foreach (var pair in overrideMetadata)
                merged[pair.Key] = pair.Value;
        }

        return merged;
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

    private static LLMStreamChunk? NormalizeStreamChunk(
        LLMStreamChunk chunk,
        StreamingToolCallAccumulator toolCalls,
        StringBuilder fullContent,
        ref TokenUsage? usage,
        ref string? finishReason)
    {
        ToolCall? normalizedToolCall = null;
        if (chunk.DeltaToolCall != null)
            normalizedToolCall = toolCalls.TrackDelta(chunk.DeltaToolCall);

        if (!string.IsNullOrEmpty(chunk.DeltaContent))
            fullContent.Append(chunk.DeltaContent);

        if (chunk.Usage != null)
            usage = chunk.Usage;

        if (chunk.FinishReason != null)
            finishReason = chunk.FinishReason;

        if (string.IsNullOrEmpty(chunk.DeltaContent) &&
            string.IsNullOrEmpty(chunk.DeltaReasoningContent) &&
            chunk.DeltaContentPart == null &&
            normalizedToolCall == null &&
            !chunk.IsLast &&
            chunk.Usage == null)
        {
            return null;
        }

        return new LLMStreamChunk
        {
            DeltaContent = chunk.DeltaContent,
            DeltaContentPart = chunk.DeltaContentPart,
            DeltaReasoningContent = chunk.DeltaReasoningContent,
            DeltaToolCall = normalizedToolCall,
            Usage = chunk.Usage,
            IsLast = chunk.IsLast,
        };
    }

    private static IReadOnlyList<LLMStreamChunk> BuildSyntheticChunks(LLMResponse response)
    {
        var chunks = new List<LLMStreamChunk>();

        if (!string.IsNullOrEmpty(response.Content))
            chunks.Add(new LLMStreamChunk { DeltaContent = response.Content });

        if (response.ToolCalls is { Count: > 0 })
        {
            chunks.AddRange(response.ToolCalls.Select(toolCall => new LLMStreamChunk
            {
                DeltaToolCall = toolCall,
            }));
        }

        chunks.Add(new LLMStreamChunk
        {
            IsLast = true,
            Usage = response.Usage,
        });

        return chunks;
    }

    private async Task RunCompressionIfNeededAsync(CancellationToken ct)
    {
        if (_compressionConfig.MaxPromptTokenBudget <= 0
            || !_history.Budget.IsOverBudget(_compressionConfig.MaxPromptTokenBudget, _compressionConfig.CompressionThreshold))
        {
            return;
        }

        var hookCtx = new AIGAgentExecutionHookContext();
        hookCtx.Items["compression_reason"] = "token_budget_exceeded";
        hookCtx.Items["last_prompt_tokens"] = _history.Budget.LastPromptTokens;
        hookCtx.Items["budget_limit"] = _compressionConfig.MaxPromptTokenBudget;
        if (_hooks != null) await _hooks.RunCompactStartAsync(hookCtx, ct);

        // Level 1: Tool result compaction
        var compacted = ContextCompressor.CompactToolResults(_history.WritableMessages);

        // Level 2: Importance-aware truncation (target 70% of max)
        var targetCount = (int)(_history.MaxMessages * 0.7);
        var truncated = ContextCompressor.TruncateByImportance(_history.WritableMessages, targetCount);

        // Level 3: Summarization (opt-in)
        var summarized = false;
        if (_compressionConfig.EnableSummarization && _history.Count > 12)
        {
            var provider = _providerFactory();
            summarized = await ContextCompressor.SummarizeOldestBlockAsync(
                _history.WritableMessages, provider, null, blockSize: 8, ct);
        }

        hookCtx.Items["compacted_tool_results"] = compacted;
        hookCtx.Items["truncated_messages"] = truncated;
        hookCtx.Items["summarized"] = summarized;
        if (_hooks != null) await _hooks.RunCompactEndAsync(hookCtx, ct);

        // ─── Hook: Notification（token 预算超限，已触发压缩） ───
        if (_hooks != null)
        {
            var notifyCtx = new AIGAgentExecutionHookContext { AgentId = _agentId };
            notifyCtx.Items["notification_type"] = "budget_compression_triggered";
            notifyCtx.Items["notification_payload"] = new Dictionary<string, object?>
            {
                ["last_prompt_tokens"] = _history.Budget.LastPromptTokens,
                ["budget_limit"] = _compressionConfig.MaxPromptTokenBudget,
                ["compacted_tool_results"] = compacted,
                ["truncated_messages"] = truncated,
                ["summarized"] = summarized,
            };
            await _hooks.RunNotificationAsync(notifyCtx, ct);
        }
    }

    private sealed record StreamingRoundResult(
        string? Content,
        IReadOnlyList<ToolCall>? ToolCalls,
        bool Terminated,
        string? FinishReason);

    // ─── Multimodal helpers ───

    private static IReadOnlyList<ContentPart> NormalizeUserContent(IReadOnlyList<ContentPart> userContent)
    {
        if (userContent == null || userContent.Count == 0)
            return [ContentPart.TextPart(string.Empty)];

        return userContent;
    }

    private static string DescribeUserContent(IReadOnlyList<ContentPart> userContent)
    {
        var textParts = userContent
            .Where(part => part.Kind == ContentPartKind.Text && !string.IsNullOrWhiteSpace(part.Text))
            .Select(part => part.Text!.Trim())
            .ToArray();

        if (textParts.Length > 0)
            return string.Join("\n", textParts);

        return string.Join(
            ", ",
            userContent.Select(part => part.Kind switch
            {
                ContentPartKind.Image => "[image]",
                ContentPartKind.Audio => "[audio]",
                ContentPartKind.Video => "[video]",
                _ => "[content]",
            }));
    }
}
