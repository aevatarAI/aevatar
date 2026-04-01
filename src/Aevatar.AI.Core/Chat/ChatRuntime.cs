// ─── ChatRuntime — Chat/ChatStream 执行逻辑 ───
// 组合 LLMProvider + History + ToolCallLoop + Hooks + Middleware。

using Aevatar.AI.Core.Hooks;
using Aevatar.AI.Core.Middleware;
using Aevatar.AI.Core.Tools;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.Middleware;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Channels;

namespace Aevatar.AI.Core.Chat;

/// <summary>Chat 执行运行时。调 LLM，管理历史，集成 Middleware。</summary>
public sealed class ChatRuntime
{
    private const int DefaultMaxToolRounds = 40;
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
        int streamBufferCapacity = 256)
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
    }

    /// <summary>单轮 Chat（含 Tool Calling 循环），包裹 Agent Run Middleware。</summary>
    public Task<string?> ChatAsync(string userMessage, int maxToolRounds = 10, CancellationToken ct = default) =>
        ChatAsync(userMessage, maxToolRounds, requestId: null, metadata: null, ct);

    /// <summary>单轮 Chat（含 Tool Calling 循环），显式传入稳定 request id 和 metadata。</summary>
    public async Task<string?> ChatAsync(
        string userMessage,
        int maxToolRounds,
        string? requestId,
        IReadOnlyDictionary<string, string>? metadata,
        CancellationToken ct = default)
    {
        var runContext = new AgentRunContext
        {
            UserMessage = userMessage,
            AgentId = _agentId,
            AgentName = _agentName,
            CancellationToken = ct,
        };

        await MiddlewarePipeline.RunAgentAsync(_agentMiddlewares, runContext, async () =>
        {
            if (runContext.Terminate) return;

            _history.Add(ChatMessage.User(runContext.UserMessage));
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

        return runContext.Result;
    }

    /// <summary>流式 Chat，包裹 LLM Call Middleware。</summary>
    public IAsyncEnumerable<LLMStreamChunk> ChatStreamAsync(
        string userMessage,
        CancellationToken ct = default) =>
        ChatStreamAsync(userMessage, DefaultMaxToolRounds, requestId: null, metadata: null, ct);

    /// <summary>流式 Chat，允许显式控制 tool calling 轮数。</summary>
    public IAsyncEnumerable<LLMStreamChunk> ChatStreamAsync(
        string userMessage,
        int maxToolRounds,
        CancellationToken ct = default) =>
        ChatStreamAsync(userMessage, maxToolRounds, requestId: null, metadata: null, ct);

    /// <summary>流式 Chat，显式传入稳定 request id 和 metadata。</summary>
    public async IAsyncEnumerable<LLMStreamChunk> ChatStreamAsync(
        string userMessage,
        string? requestId,
        IReadOnlyDictionary<string, string>? metadata = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var chunk in ChatStreamAsync(
                           userMessage,
                           DefaultMaxToolRounds,
                           requestId,
                           metadata,
                           ct))
        {
            yield return chunk;
        }
    }

    /// <summary>流式 Chat，显式传入稳定 request id / metadata / tool 调用轮数。</summary>
    public async IAsyncEnumerable<LLMStreamChunk> ChatStreamAsync(
        string userMessage,
        int maxToolRounds,
        string? requestId,
        IReadOnlyDictionary<string, string>? metadata = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
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
            UserMessage = userMessage,
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

                    var userMsg = ChatMessage.User(runContext.UserMessage);
                    pendingHistoryMessages.Add(userMsg);
                    var baseRequest = ApplyRequestIdentity(_requestBuilder(), requestId, metadata);
                    var provider = _providerFactory();
                    runContext.Items["gen_ai.provider.name"] = provider.Name;
                    // Build messages from a local snapshot + pending user message instead of mutating _history.
                    var messages = BuildMessagesWithPending(baseRequest, userMsg);
                    string? finalContent = null;
                    var lengthRecoveryCount = 0;

                    for (var round = 0; round < effectiveMaxToolRounds; round++)
                    {
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
                            () => wroteOutput = true);

                        if (roundResult.Terminated)
                        {
                            AppendAssistantMessage(messages, pendingHistoryMessages, roundResult.Content, roundResult.ToolCalls);
                            finalContent = roundResult.Content;
                            break;
                        }

                        if (roundResult.ToolCalls is not { Count: > 0 })
                        {
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

                        var assistantToolCallMessage = new ChatMessage
                        {
                            Role = "assistant",
                            ToolCalls = roundResult.ToolCalls,
                        };
                        messages.Add(assistantToolCallMessage);
                        pendingHistoryMessages.Add(assistantToolCallMessage);

                        var toolMessageStartIndex = messages.Count;
                        await _toolLoop.ExecuteToolCallsAsync(roundResult.ToolCalls, messages, baseRequest.Metadata, runToken);
                        for (var index = toolMessageStartIndex; index < messages.Count; index++)
                            pendingHistoryMessages.Add(messages[index]);
                    }

                    if (finalContent == null)
                    {
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
                        AppendAssistantMessage(messages, pendingHistoryMessages, finalRound.Content, toolCalls: null);
                        finalContent = finalRound.Content;
                    }

                    runContext.Result = finalContent;
                });

                if (runContext.Terminate && runContext.Result != null && !wroteOutput)
                {
                    await channel.Writer.WriteAsync(
                        new LLMStreamChunk { DeltaContent = runContext.Result },
                        runToken);
                }

                channel.Writer.TryComplete();
                return pendingHistoryMessages;
            }
            catch (Exception ex)
            {
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

    private async Task<StreamingRoundResult> StreamLlmRoundAsync(
        ILLMProvider provider,
        LLMRequest request,
        ChannelWriter<LLMStreamChunk> writer,
        CancellationToken ct,
        Action markOutputWritten)
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
            var toolCalls = new StreamingToolCallAccumulator();

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
            normalizedToolCall == null &&
            !chunk.IsLast &&
            chunk.Usage == null)
        {
            return null;
        }

        return new LLMStreamChunk
        {
            DeltaContent = chunk.DeltaContent,
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

    private sealed record StreamingRoundResult(
        string? Content,
        IReadOnlyList<ToolCall>? ToolCalls,
        bool Terminated,
        string? FinishReason);
}
