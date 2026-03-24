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
        ChatAsync([ContentPart.TextPart(userMessage)], maxToolRounds, requestId: null, metadata: null, ct);

    public Task<string?> ChatAsync(
        string userMessage,
        int maxToolRounds,
        string? requestId,
        IReadOnlyDictionary<string, string>? metadata,
        CancellationToken ct = default) =>
        ChatAsync([ContentPart.TextPart(userMessage)], maxToolRounds, requestId, metadata, ct);

    public Task<string?> ChatAsync(
        IReadOnlyList<ContentPart> userContent,
        int maxToolRounds = 10,
        CancellationToken ct = default) =>
        ChatAsync(userContent, maxToolRounds, requestId: null, metadata: null, ct);

    /// <summary>单轮 Chat（含 Tool Calling 循环），显式传入稳定 request id 和 metadata。</summary>
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

        await MiddlewarePipeline.RunAgentAsync(_agentMiddlewares, runContext, async () =>
        {
            if (runContext.Terminate) return;

            _history.Add(ChatMessage.User(normalizedUserContent, runContext.UserMessage));
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
        ChatStreamAsync([ContentPart.TextPart(userMessage)], requestId: null, metadata: null, ct);

    public IAsyncEnumerable<LLMStreamChunk> ChatStreamAsync(
        string userMessage,
        string? requestId,
        IReadOnlyDictionary<string, string>? metadata = null,
        CancellationToken ct = default) =>
        ChatStreamAsync([ContentPart.TextPart(userMessage)], requestId, metadata, ct);

    public IAsyncEnumerable<LLMStreamChunk> ChatStreamAsync(
        IReadOnlyList<ContentPart> userContent,
        CancellationToken ct = default) =>
        ChatStreamAsync(userContent, requestId: null, metadata: null, ct);

    /// <summary>流式 Chat，显式传入稳定 request id 和 metadata。</summary>
    public async IAsyncEnumerable<LLMStreamChunk> ChatStreamAsync(
        IReadOnlyList<ContentPart> userContent,
        string? requestId,
        IReadOnlyDictionary<string, string>? metadata = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var normalizedUserContent = NormalizeUserContent(userContent);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var runToken = linkedCts.Token;

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

                    var userMsg = ChatMessage.User(normalizedUserContent, runContext.UserMessage);
                    pendingHistoryMessages.Add(userMsg);
                    var baseRequest = ApplyRequestIdentity(_requestBuilder(), requestId, metadata);
                    var provider = _providerFactory();
                    runContext.Items["gen_ai.provider.name"] = provider.Name;
                    // Build messages from a local snapshot + pending user message instead of mutating _history.
                    var messages = BuildMessagesWithPending(baseRequest, userMsg);

                    var request = new LLMRequest
                    {
                        Messages = messages,
                        RequestId = baseRequest.RequestId,
                        Metadata = baseRequest.Metadata,
                        Tools = baseRequest.Tools,
                        Model = baseRequest.Model,
                        Temperature = baseRequest.Temperature,
                        MaxTokens = baseRequest.MaxTokens,
                    };

                    var llmCallContext = new LLMCallContext
                    {
                        Request = request,
                        Provider = provider,
                        CancellationToken = runToken,
                        IsStreaming = true,
                    };
                    AnnotateRequestIdentity(llmCallContext);

                    string? streamedContent = null;
                    TokenUsage? streamedUsage = null;
                    IReadOnlyList<ToolCall>? streamedToolCalls = null;
                    List<ContentPart>? streamedContentParts = null;

                    await MiddlewarePipeline.RunLLMCallAsync(_llmMiddlewares, llmCallContext, async () =>
                    {
                        if (llmCallContext.Terminate) return;

                        var full = new StringBuilder();
                        TokenUsage? usage = null;
                        var toolCalls = new StreamingToolCallAccumulator();
                        var contentParts = new List<ContentPart>();

                        await foreach (var chunk in provider.ChatStreamAsync(llmCallContext.Request, runToken))
                        {
                            var normalizedChunk = NormalizeStreamChunk(chunk, toolCalls, full, contentParts, ref usage);
                            if (normalizedChunk == null)
                                continue;

                            await channel.Writer.WriteAsync(normalizedChunk, runToken);
                            wroteOutput = true;
                        }

                        streamedContent = full.Length > 0 ? full.ToString() : null;
                        streamedUsage = usage;
                        var finalizedToolCalls = toolCalls.BuildToolCalls();
                        streamedToolCalls = finalizedToolCalls.Count > 0 ? finalizedToolCalls : null;
                        streamedContentParts = contentParts.Count > 0 ? contentParts : null;
                        llmCallContext.Response = new LLMResponse
                        {
                            Content = streamedContent,
                            ContentParts = streamedContentParts,
                            Usage = streamedUsage,
                            ToolCalls = streamedToolCalls,
                        };
                    });

                    if (llmCallContext.Terminate)
                    {
                        streamedContent = llmCallContext.Response?.Content;
                        streamedUsage = llmCallContext.Response?.Usage;
                        streamedToolCalls = llmCallContext.Response?.ToolCalls;
                        streamedContentParts = llmCallContext.Response?.ContentParts?.ToList();

                        if (llmCallContext.Response != null)
                        {
                            foreach (var chunk in BuildSyntheticChunks(llmCallContext.Response))
                            {
                                await channel.Writer.WriteAsync(chunk, runToken);
                                wroteOutput = true;
                            }
                        }
                    }

                    if (!string.IsNullOrEmpty(streamedContent) ||
                        streamedToolCalls is { Count: > 0 } ||
                        streamedContentParts is { Count: > 0 })
                    {
                        pendingHistoryMessages.Add(new ChatMessage
                        {
                            Role = "assistant",
                            Content = streamedContent,
                            ContentParts = streamedContentParts,
                            ToolCalls = streamedToolCalls,
                        });
                    }

                    runContext.Result = streamedContent;
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
            catch (Exception ex)
            {
                // Primary error already surfaced via channel.Writer.TryComplete(ex).
                // Log here to capture history-collection failures that would otherwise be silent.
                System.Diagnostics.Trace.TraceWarning(
                    "ChatRuntime: streaming run task failed during history collection: {0}", ex.Message);
            }

            // Apply collected history mutations on the caller context after the background task completes.
            if (collectedHistory != null)
            {
                foreach (var msg in collectedHistory)
                    _history.Add(msg);
            }
        }
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
        List<ContentPart> fullContentParts,
        ref TokenUsage? usage)
    {
        ToolCall? normalizedToolCall = null;
        if (chunk.DeltaToolCall != null)
            normalizedToolCall = toolCalls.TrackDelta(chunk.DeltaToolCall);

        if (!string.IsNullOrEmpty(chunk.DeltaContent))
            fullContent.Append(chunk.DeltaContent);

        if (chunk.DeltaContentPart != null)
            fullContentParts.Add(chunk.DeltaContentPart);

        if (chunk.Usage != null)
            usage = chunk.Usage;

        if (string.IsNullOrEmpty(chunk.DeltaContent) &&
            chunk.DeltaContentPart == null &&
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

        if (response.ContentParts is { Count: > 0 })
        {
            chunks.AddRange(response.ContentParts.Select(contentPart => new LLMStreamChunk
            {
                DeltaContentPart = contentPart,
            }));
        }

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
                ContentPartKind.Pdf => "[pdf]",
                _ => "[content]",
            }));
    }

}
