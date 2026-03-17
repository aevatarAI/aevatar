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
        ChatStreamAsync(userMessage, requestId: null, headers: null, ct);

    /// <summary>流式 Chat，显式传入稳定 request id 和 headers。</summary>
    public async IAsyncEnumerable<LLMStreamChunk> ChatStreamAsync(
        string userMessage,
        string? requestId,
        IReadOnlyDictionary<string, string>? headers = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
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
            UserMessage = userMessage,
            AgentId = _agentId,
            AgentName = _agentName,
            CancellationToken = runToken,
        };

        var runTask = Task.Run(async () =>
        {
            var wroteOutput = false;
            try
            {
                await MiddlewarePipeline.RunAgentAsync(_agentMiddlewares, runContext, async () =>
                {
                    if (runContext.Terminate) return;

                    _history.Add(ChatMessage.User(runContext.UserMessage));
                    var baseRequest = ApplyRequestIdentity(_requestBuilder(), requestId, headers);
                    var provider = _providerFactory();
                    runContext.Items["gen_ai.provider.name"] = provider.Name;
                    var messages = _history.BuildMessages(baseRequest.Messages.FirstOrDefault(m => m.Role == "system")?.Content);

                    var request = new LLMRequest
                    {
                        Messages = messages,
                        RequestId = baseRequest.RequestId,
                        CallId = baseRequest.CallId,
                        Headers = baseRequest.Headers,
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

                    await MiddlewarePipeline.RunLLMCallAsync(_llmMiddlewares, llmCallContext, async () =>
                    {
                        if (llmCallContext.Terminate) return;

                        var full = new StringBuilder();
                        TokenUsage? usage = null;
                        var toolCalls = new StreamingToolCallAccumulator();

                        await foreach (var chunk in provider.ChatStreamAsync(llmCallContext.Request, runToken))
                        {
                            var normalizedChunk = NormalizeStreamChunk(chunk, toolCalls, full, ref usage);
                            if (normalizedChunk == null)
                                continue;

                            await channel.Writer.WriteAsync(normalizedChunk, runToken);
                            wroteOutput = true;
                        }

                        streamedContent = full.Length > 0 ? full.ToString() : null;
                        streamedUsage = usage;
                        var finalizedToolCalls = toolCalls.BuildToolCalls();
                        streamedToolCalls = finalizedToolCalls.Count > 0 ? finalizedToolCalls : null;
                        llmCallContext.Response = new LLMResponse
                        {
                            Content = streamedContent,
                            Usage = streamedUsage,
                            ToolCalls = streamedToolCalls,
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
                                await channel.Writer.WriteAsync(chunk, runToken);
                                wroteOutput = true;
                            }
                        }
                    }

                    if (!string.IsNullOrEmpty(streamedContent) || streamedToolCalls is { Count: > 0 })
                    {
                        _history.Add(new ChatMessage
                        {
                            Role = "assistant",
                            Content = streamedContent,
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
            }
            catch (Exception ex)
            {
                channel.Writer.TryComplete(ex);
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
            try { await runTask.ConfigureAwait(false); } catch { /* best-effort */ }
        }
    }

    private static LLMRequest ApplyRequestIdentity(
        LLMRequest baseRequest,
        string? requestId,
        IReadOnlyDictionary<string, string>? headers)
    {
        return new LLMRequest
        {
            Messages = baseRequest.Messages,
            RequestId = string.IsNullOrWhiteSpace(requestId) ? baseRequest.RequestId : requestId.Trim(),
            CallId = baseRequest.CallId,
            Headers = MergeHeaders(baseRequest.Headers, headers),
            Tools = baseRequest.Tools,
            Model = baseRequest.Model,
            Temperature = baseRequest.Temperature,
            MaxTokens = baseRequest.MaxTokens,
        };
    }

    private static IReadOnlyDictionary<string, string>? MergeHeaders(
        IReadOnlyDictionary<string, string>? baseHeaders,
        IReadOnlyDictionary<string, string>? overrideHeaders)
    {
        if ((baseHeaders == null || baseHeaders.Count == 0) &&
            (overrideHeaders == null || overrideHeaders.Count == 0))
        {
            return null;
        }

        var merged = new Dictionary<string, string>(StringComparer.Ordinal);
        if (baseHeaders != null)
        {
            foreach (var pair in baseHeaders)
                merged[pair.Key] = pair.Value;
        }

        if (overrideHeaders != null)
        {
            foreach (var pair in overrideHeaders)
                merged[pair.Key] = pair.Value;
        }

        return merged;
    }

    private static void AnnotateRequestIdentity(LLMCallContext context)
    {
        if (!string.IsNullOrWhiteSpace(context.Request.RequestId))
            context.Items[LLMRequestMetadataKeys.RequestId] = context.Request.RequestId;

        if (!string.IsNullOrWhiteSpace(context.Request.CallId))
            context.Items[LLMRequestMetadataKeys.CallId] = context.Request.CallId;
    }

    private static LLMStreamChunk? NormalizeStreamChunk(
        LLMStreamChunk chunk,
        StreamingToolCallAccumulator toolCalls,
        StringBuilder fullContent,
        ref TokenUsage? usage)
    {
        ToolCall? normalizedToolCall = null;
        if (chunk.DeltaToolCall != null)
            normalizedToolCall = toolCalls.TrackDelta(chunk.DeltaToolCall);

        if (!string.IsNullOrEmpty(chunk.DeltaContent))
            fullContent.Append(chunk.DeltaContent);

        if (chunk.Usage != null)
            usage = chunk.Usage;

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

}
