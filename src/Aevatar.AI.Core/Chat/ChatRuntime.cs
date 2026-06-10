// ─── ChatRuntime — Chat/ChatStream 执行逻辑 ───
// 组合 LLMProvider + History + ToolCallLoop + Hooks + Middleware。

using Aevatar.AI.Core.Hooks;
using Aevatar.AI.Core.Middleware;
using Aevatar.AI.Core.Tools;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.Middleware;
using Aevatar.AI.Abstractions.ToolProviders;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;

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
// Refactor (iter39/cluster-039-public-chatasync-adapter):
//   Old pattern: ChatRuntime 暴露 public ChatAsync 方法作为 non-streaming adapter,callers 可以选 non-streaming conversation API。
//   New principle: Public runtime surface 仅暴露 ChatStreamAsync;explicit offline aggregation 放到 narrowly named offline/test adapter(明确不能与 realtime chat 混淆)。Provider contract stream-only。
// Refactor (iter31/cluster-032-chatruntime-taskrun-business-loop):
//   Old pattern: ChatRuntime.ChatStreamAsync 用 Task.Run + Channel<LLMStreamChunk>/ChannelWriter 在 actor turn 外跑 LLM/tool/hook/history 业务循环,违反 actor execution integrity
//   New principle: ChatStreamAsync owns the stream flow directly; the Task.Run + Channel owned-stream loop and stream_buffer_capacity config were removed; middleware wrapping stays inside private bridge adapters.
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
    private readonly ContextCompressionConfig _compressionConfig;
    private readonly bool _suppressToolCallRoundText;

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
        ContextCompressionConfig? compressionConfig = null,
        bool suppressToolCallRoundText = false)
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
        _compressionConfig = compressionConfig ?? new ContextCompressionConfig();
        _suppressToolCallRoundText = suppressToolCallRoundText;
    }

    public ChatRuntimeStepExecutor CreateStepExecutor() =>
        new(
            _providerFactory,
            _toolLoop,
            _hooks,
            _requestBuilder,
            _llmMiddlewares,
            _history.Budget);

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

    public IAsyncEnumerable<LLMStreamChunk> ChatStreamAsync(
        IReadOnlyList<ContentPart> userContent,
        int maxToolRounds,
        string? requestId,
        LLMControlContext? llmControl,
        AgentToolExecutionContext? toolContext,
        IReadOnlyDictionary<string, string>? metadata = null,
        CancellationToken ct = default) =>
        ChatStreamAsync(userContent, maxToolRounds, requestId, metadata, toolContext, llmControl, ct);

    public IAsyncEnumerable<LLMStreamChunk> ChatStreamAsync(
        IReadOnlyList<ContentPart> userContent,
        int maxToolRounds,
        string? requestId,
        AgentToolExecutionContext? toolContext,
        IReadOnlyDictionary<string, string>? metadata = null,
        CancellationToken ct = default) =>
        ChatStreamAsync(userContent, maxToolRounds, requestId, metadata, toolContext, llmControl: null, ct);

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
        await foreach (var chunk in ChatStreamAsync(
                           userContent,
                           maxToolRounds,
                           requestId,
                           metadata,
                           toolContext: null,
                           llmControl: null,
                           ct))
        {
            yield return chunk;
        }
    }

    private async IAsyncEnumerable<LLMStreamChunk> ChatStreamAsync(
        IReadOnlyList<ContentPart> userContent,
        int maxToolRounds,
        string? requestId,
        IReadOnlyDictionary<string, string>? metadata,
        AgentToolExecutionContext? toolContext,
        LLMControlContext? llmControl,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var normalizedUserContent = NormalizeUserContent(userContent);
        var runToken = ct;
        var effectiveMaxToolRounds = maxToolRounds > 0 ? maxToolRounds : DefaultMaxToolRounds;

        var runContext = new AgentRunContext
        {
            UserMessage = DescribeUserContent(normalizedUserContent),
            AgentId = _agentId,
            AgentName = _agentName,
            CancellationToken = runToken,
        };

        var agentBridge = new AgentRunMiddlewareBridge();
        var middlewareTask = MiddlewarePipeline.RunAgentAsync(
            _agentMiddlewares,
            runContext,
            agentBridge.WaitForCoreCompletionAsync);

        var coreTurnTask = agentBridge.WaitForCoreTurnAsync(runToken);
        var middlewareWaitTask = middlewareTask.WaitAsync(runToken);
        var readyTask = await Task.WhenAny(coreTurnTask, middlewareWaitTask).ConfigureAwait(false);
        await readyTask.ConfigureAwait(false);

        if (readyTask == coreTurnTask && !runContext.Terminate)
        {
            await using var streamEnumerator = RunChatStreamCoreAsync(
                    normalizedUserContent,
                    effectiveMaxToolRounds,
                    requestId,
                    metadata,
                    toolContext,
                    llmControl,
                    runContext,
                    runToken)
                .GetAsyncEnumerator(runToken);
            while (true)
            {
                LLMStreamChunk current;
                try
                {
                    if (!await streamEnumerator.MoveNextAsync().ConfigureAwait(false))
                        break;

                    current = streamEnumerator.Current;
                }
                catch (Exception ex)
                {
                    agentBridge.FailCore(ex);
                    await RunStopFailureHookAsync(ex);
                    throw;
                }

                yield return current;
            }
        }

        if (runContext.Terminate && runContext.Result != null)
        {
            yield return new LLMStreamChunk { DeltaContent = runContext.Result };
        }

        agentBridge.CompleteCore();
        await middlewareTask.ConfigureAwait(false);
    }

    private async IAsyncEnumerable<LLMStreamChunk> RunChatStreamCoreAsync(
        IReadOnlyList<ContentPart> normalizedUserContent,
        int effectiveMaxToolRounds,
        string? requestId,
        IReadOnlyDictionary<string, string>? metadata,
        AgentToolExecutionContext? toolContext,
        LLMControlContext? llmControl,
        AgentRunContext runContext,
        [EnumeratorCancellation] CancellationToken runToken)
    {
        // Refactor (iter31/cluster-032-chatruntime-taskrun-business-loop):
        //   Old pattern: ChatRuntime.ChatStreamAsync 用 Task.Run + Channel<LLMStreamChunk>/ChannelWriter 在 actor turn 外跑 LLM/tool/hook/history 业务循环,违反 actor execution integrity
        //   New principle: ChatStreamAsync owns the stream flow directly; the Task.Run + Channel owned-stream loop and stream_buffer_capacity config were removed; middleware wrapping stays inside private bridge adapters.
        var pendingHistoryMessages = new List<ChatMessage>();
        var wroteOutput = false;

        await RunCompressionIfNeededAsync(runToken);
        await foreach (var chunk in RunChatStreamCoreAfterCompressionAsync(
                           normalizedUserContent,
                           effectiveMaxToolRounds,
                           requestId,
                           metadata,
                           toolContext,
                           llmControl,
                           runContext,
                           pendingHistoryMessages,
                           wroteOutput,
                           runToken))
        {
            yield return chunk;
        }
    }

    private async IAsyncEnumerable<LLMStreamChunk> RunChatStreamCoreAfterCompressionAsync(
        IReadOnlyList<ContentPart> normalizedUserContent,
        int effectiveMaxToolRounds,
        string? requestId,
        IReadOnlyDictionary<string, string>? metadata,
        AgentToolExecutionContext? toolContext,
        LLMControlContext? llmControl,
        AgentRunContext runContext,
        List<ChatMessage> pendingHistoryMessages,
        bool wroteOutput,
        [EnumeratorCancellation] CancellationToken runToken)
    {
        // Refactor (iter35/cluster-040-streaming-tool-executor):
        //   Old pattern: StreamingToolExecutor owns process-local channel coordinator + TaskCompletionSource waiters + List<TrackedTool>/List<TaskCompletionSource> as object fields for tool execution ordering.
        //   New principle: Tool execution state kept in owning chat/actor turn,或 narrow runtime-neutral tool scheduling abstraction(no process-local progress storage)。Streaming tool progress advanced by owning execution flow;process-local channels 仅作 transport mechanics,不作 business progress 来源。
        // Refactor (iter31/cluster-032-chatruntime-taskrun-business-loop):
        //   Old pattern: ChatRuntime.ChatStreamAsync 用 Task.Run + Channel<LLMStreamChunk>/ChannelWriter 在 actor turn 外跑 LLM/tool/hook/history 业务循环,违反 actor execution integrity
        //   New principle: ChatStreamAsync owns the stream flow directly; the Task.Run + Channel owned-stream loop and stream_buffer_capacity config were removed; middleware wrapping stays inside private bridge adapters.
        var userMsg = ChatMessage.User(normalizedUserContent, runContext.UserMessage);
        pendingHistoryMessages.Add(userMsg);
        var baseRequest = ApplyRequestIdentity(_requestBuilder(), requestId, metadata, toolContext, llmControl);
        var provider = _providerFactory();
        runContext.Items["gen_ai.provider.name"] = provider.Name;
        var messages = BuildMessagesWithPending(baseRequest, userMsg);
        string? finalContent = null;
        var lengthRecoveryCount = 0;
        var hasStreamedTextContent = false;
        var skillRecovery = CreateSkillRecoveryOrchestrator(baseRequest);

        if (skillRecovery.RequiresInitialSearch)
        {
            await skillRecovery.ApplyInitialDirectivesAsync(
                AgentToolExecutionContextMapper.FromRequest(baseRequest),
                messages,
                pendingHistoryMessages,
                ToolCallLoop.ComposeRoundCallId(baseRequest.RequestId, 0),
                runToken);
        }

        for (var round = 0; round < effectiveMaxToolRounds; round++)
        {
            if (hasStreamedTextContent)
            {
                wroteOutput = true;
                yield return new LLMStreamChunk { DeltaContent = "\n\n" };
            }

            var streamingExecutor = new StreamingToolExecutor(
                _toolLoop.Tools, _hooks, _toolLoop.ToolMiddlewares,
                toolContext: AgentToolExecutionContextMapper.FromRequest(baseRequest));
            using var streamingToolState = streamingExecutor.CreateExecutionState();

            List<ToolCall>? deferredToolCalls = _hooks != null ? [] : null;

            var roundRequest = new LLMRequest
            {
                Messages = [..messages],
                RequestId = baseRequest.RequestId,
                Metadata = AgentToolExecutionContextMapper.StripOwnedControlKeys(baseRequest.Metadata),
                CallerContext = baseRequest.CallerContext,
                ToolContext = AgentToolExecutionContextMapper.FromRequestWithCallId(
                    baseRequest,
                    ToolCallLoop.ComposeRoundCallId(baseRequest.RequestId, round)),
                RoutingContext = baseRequest.RoutingContext,
                LlmControl = baseRequest.LlmControl,
                Tools = baseRequest.Tools,
                Model = baseRequest.Model,
                Temperature = baseRequest.Temperature,
                MaxTokens = baseRequest.MaxTokens,
                ResponseFormat = baseRequest.ResponseFormat,
            };
            var roundScope = new StreamingRoundScope();
            TextToolCallParser.ParseResult? parsedTextToolCall = null;
            StreamingRoundResult roundResult;
            if (_suppressToolCallRoundText)
            {
                var roundChunks = new List<LLMStreamChunk>();
                await foreach (var chunk in StreamLlmRoundAsync(
                                   provider,
                                   roundRequest,
                                   roundScope,
                                   runToken,
                                   toolCall =>
                                   {
                                       if (deferredToolCalls != null)
                                           deferredToolCalls.Add(toolCall);
                                       else
                                           streamingExecutor.AddTool(streamingToolState, toolCall);
                                   }))
                {
                    roundChunks.Add(chunk);
                }

                roundResult = roundScope.RequireResult();
                parsedTextToolCall = roundResult.ToolCalls is not { Count: > 0 } && roundResult.Content != null
                    ? TextToolCallParser.Parse(roundResult.Content)
                    : null;
                var roundCallsTools = !roundResult.Terminated &&
                                      (roundResult.ToolCalls is { Count: > 0 } ||
                                       parsedTextToolCall?.ToolCalls.Count > 0);

                if (!roundCallsTools &&
                    await skillRecovery.TryRecoverFinalAnswerAsync(
                        roundRequest.ToolContext,
                        messages,
                        pendingHistoryMessages,
                        roundResult.Content,
                        ToolCallLoop.ComposeRoundCallId(baseRequest.RequestId, round),
                        runToken))
                {
                    streamingExecutor.Discard(streamingToolState);
                    hasStreamedTextContent = false;
                    continue;
                }

                foreach (var chunk in roundChunks)
                {
                    var visibleChunk = roundCallsTools ? SuppressVisibleToolCallRoundText(chunk) : chunk;
                    if (visibleChunk is null)
                        continue;

                    wroteOutput = true;
                    yield return visibleChunk;
                }

                if (!roundCallsTools && !string.IsNullOrEmpty(roundResult.Content))
                    hasStreamedTextContent = true;
            }
            else
            {
                await foreach (var chunk in StreamLlmRoundAsync(
                                   provider,
                                   roundRequest,
                                   roundScope,
                                   runToken,
                                   toolCall =>
                                   {
                                       if (deferredToolCalls != null)
                                           deferredToolCalls.Add(toolCall);
                                       else
                                           streamingExecutor.AddTool(streamingToolState, toolCall);
                                   }))
                {
                    wroteOutput = true;
                    yield return chunk;
                }

                roundResult = roundScope.RequireResult();
                if (!string.IsNullOrEmpty(roundResult.Content))
                    hasStreamedTextContent = true;
            }

            if (roundResult.Terminated)
            {
                streamingExecutor.Discard(streamingToolState);
                AppendAssistantMessage(messages, pendingHistoryMessages, roundResult.Content, roundResult.ReasoningContent, roundResult.ToolCalls);
                finalContent = roundResult.Content;
                break;
            }

            if (roundResult.ToolCalls is not { Count: > 0 })
            {
                if (roundResult.Content != null)
                {
                    var parsed = parsedTextToolCall ?? TextToolCallParser.Parse(roundResult.Content);
                    if (parsed.ToolCalls.Count > 0)
                    {
                        var fallbackBlocked = false;
                        if (_hooks != null)
                        {
                            var postCtx = new AIGAgentExecutionHookContext
                            {
                                LLMResponse = new LLMResponse
                                {
                                    Content = parsed.CleanedContent,
                                    ReasoningContent = roundResult.ReasoningContent,
                                    ToolCalls = parsed.ToolCalls,
                                },
                            };
                            postCtx.Items["tool_call_count"] = parsed.ToolCalls.Count;
                            await _hooks.RunPostSamplingAsync(postCtx, runToken);

                            if (postCtx.Items.TryGetValue("block_tool_calls", out var block) && block is true)
                                fallbackBlocked = true;
                        }

                        if (fallbackBlocked)
                        {
                            AppendAssistantMessage(messages, pendingHistoryMessages, parsed.CleanedContent, roundResult.ReasoningContent, toolCalls: null);
                            finalContent = parsed.CleanedContent;
                            break;
                        }

                        AppendAssistantMessage(
                            messages,
                            pendingHistoryMessages,
                            parsed.CleanedContent,
                            roundResult.ReasoningContent,
                            parsed.ToolCalls);

                        var textToolExecutor = new StreamingToolExecutor(
                            _toolLoop.Tools, _hooks, _toolLoop.ToolMiddlewares,
                            toolContext: AgentToolExecutionContextMapper.FromRequest(roundRequest));
                        using var textToolState = textToolExecutor.CreateExecutionState();
                        foreach (var tc in parsed.ToolCalls)
                            textToolExecutor.AddTool(textToolState, tc);
                        await foreach (var result in textToolExecutor.GetRemainingResultsAsync(textToolState, runToken))
                        {
                            if (result.Receipt is not null)
                                yield return new LLMStreamChunk { ToolReceipt = result.Receipt.Clone() };
                            var toolMsg = ToolCallLoop.BuildToolResultMessage(result.CallId, result.ToolName, result.Result);
                            messages.Add(toolMsg);
                            pendingHistoryMessages.Add(toolMsg);
                        }

                        continue;
                    }
                }

                if (ToolCallLoop.IsLengthTruncated(roundResult.FinishReason)
                    && lengthRecoveryCount < ToolCallLoop.MaxLengthRecoveries)
                {
                    AppendAssistantMessage(messages, pendingHistoryMessages, roundResult.Content, roundResult.ReasoningContent, toolCalls: null);
                    var nudge = ChatMessage.User(ToolCallLoop.LengthRecoveryNudge);
                    messages.Add(nudge);
                    pendingHistoryMessages.Add(nudge);
                    lengthRecoveryCount++;
                    continue;
                }

                if (await skillRecovery.TryRecoverFinalAnswerAsync(
                        roundRequest.ToolContext,
                        messages,
                        pendingHistoryMessages,
                        roundResult.Content,
                        ToolCallLoop.ComposeRoundCallId(baseRequest.RequestId, round),
                        runToken))
                {
                    hasStreamedTextContent = false;
                    continue;
                }

                AppendAssistantMessage(messages, pendingHistoryMessages, roundResult.Content, roundResult.ReasoningContent, toolCalls: null);
                finalContent = roundResult.Content;
                break;
            }

            if (_hooks != null)
            {
                var postSamplingCtx = new AIGAgentExecutionHookContext
                {
                    LLMResponse = new LLMResponse
                    {
                        Content = roundResult.Content,
                        ReasoningContent = roundResult.ReasoningContent,
                        ToolCalls = roundResult.ToolCalls,
                    },
                };
                postSamplingCtx.Items["tool_call_count"] = roundResult.ToolCalls?.Count ?? 0;
                await _hooks.RunPostSamplingAsync(postSamplingCtx, runToken);

                if (postSamplingCtx.Items.TryGetValue("block_tool_calls", out var block) && block is true)
                {
                    AppendAssistantMessage(messages, pendingHistoryMessages, roundResult.Content, roundResult.ReasoningContent, toolCalls: null);
                    finalContent = roundResult.Content;
                    break;
                }

                if (deferredToolCalls != null)
                {
                    foreach (var tc in deferredToolCalls)
                        streamingExecutor.AddTool(streamingToolState, tc);
                }
            }

            var assistantToolCallMessage = new ChatMessage
            {
                Role = "assistant",
                Content = roundResult.Content,
                ReasoningContent = roundResult.ReasoningContent,
                ToolCalls = roundResult.ToolCalls,
            };
            messages.Add(assistantToolCallMessage);
            pendingHistoryMessages.Add(assistantToolCallMessage);

            await foreach (var result in streamingExecutor.GetRemainingResultsAsync(streamingToolState, runToken))
            {
                if (result.Receipt is not null)
                    yield return new LLMStreamChunk { ToolReceipt = result.Receipt.Clone() };
                var toolMsg = ToolCallLoop.BuildToolResultMessage(result.CallId, result.ToolName, result.Result);
                messages.Add(toolMsg);
                pendingHistoryMessages.Add(toolMsg);
            }
        }

        if (finalContent == null)
        {
            if (hasStreamedTextContent)
            {
                wroteOutput = true;
                yield return new LLMStreamChunk { DeltaContent = "\n\n" };
            }

            var finalRequest = new LLMRequest
            {
                Messages = [..messages],
                RequestId = baseRequest.RequestId,
                Metadata = AgentToolExecutionContextMapper.StripOwnedControlKeys(baseRequest.Metadata),
                CallerContext = baseRequest.CallerContext,
                ToolContext = AgentToolExecutionContextMapper.FromRequestWithCallId(
                    baseRequest,
                    ToolCallLoop.ComposeFinalCallId(baseRequest.RequestId)),
                RoutingContext = baseRequest.RoutingContext,
                LlmControl = baseRequest.LlmControl,
                Tools = null,
                Model = baseRequest.Model,
                Temperature = baseRequest.Temperature,
                MaxTokens = baseRequest.MaxTokens,
                ResponseFormat = baseRequest.ResponseFormat,
            };
            var finalScope = new StreamingRoundScope();
            await foreach (var chunk in StreamLlmRoundAsync(provider, finalRequest, finalScope, runToken))
            {
                wroteOutput = true;
                yield return chunk;
            }

            var finalRound = finalScope.RequireResult();
            var finalParsed = finalRound.Content != null
                ? TextToolCallParser.Parse(finalRound.Content)
                : null;
            if (finalParsed?.ToolCalls.Count > 0)
            {
                AppendAssistantMessage(
                    messages,
                    pendingHistoryMessages,
                    finalParsed.CleanedContent,
                    finalRound.ReasoningContent,
                    finalParsed.ToolCalls);

                var finalToolExecutor = new StreamingToolExecutor(
                    _toolLoop.Tools, _hooks, _toolLoop.ToolMiddlewares,
                    toolContext: finalRequest.ToolContext);
                using var finalToolState = finalToolExecutor.CreateExecutionState();
                foreach (var tc in finalParsed.ToolCalls)
                    finalToolExecutor.AddTool(finalToolState, tc);
                await foreach (var result in finalToolExecutor.GetRemainingResultsAsync(finalToolState, runToken))
                {
                    if (result.Receipt is not null)
                        yield return new LLMStreamChunk { ToolReceipt = result.Receipt.Clone() };
                    var toolMsg = ToolCallLoop.BuildToolResultMessage(result.CallId, result.ToolName, result.Result);
                    messages.Add(toolMsg);
                    pendingHistoryMessages.Add(toolMsg);
                }

                var summaryRequest = new LLMRequest
                {
                    Messages = [..messages],
                    RequestId = finalRequest.RequestId,
                    Metadata = finalRequest.Metadata,
                    CallerContext = finalRequest.CallerContext,
                    ToolContext = finalRequest.ToolContext,
                    RoutingContext = finalRequest.RoutingContext,
                    LlmControl = finalRequest.LlmControl,
                    Tools = null,
                    Model = finalRequest.Model,
                    Temperature = finalRequest.Temperature,
                    MaxTokens = finalRequest.MaxTokens,
                    ResponseFormat = finalRequest.ResponseFormat,
                };
                var summaryScope = new StreamingRoundScope();
                await foreach (var chunk in StreamLlmRoundAsync(provider, summaryRequest, summaryScope, runToken))
                {
                    wroteOutput = true;
                    yield return chunk;
                }

                var summaryRound = summaryScope.RequireResult();
                AppendAssistantMessage(messages, pendingHistoryMessages, summaryRound.Content, summaryRound.ReasoningContent, toolCalls: null);
                finalContent = summaryRound.Content;
            }
            else
            {
                AppendAssistantMessage(messages, pendingHistoryMessages, finalRound.Content, finalRound.ReasoningContent, toolCalls: null);
                finalContent = finalRound.Content;
            }
        }

        runContext.Result = finalContent;
        foreach (var msg in pendingHistoryMessages)
            _history.Add(msg);

        await RunStopHookAsync(runContext.Result, pendingHistoryMessages, runToken);

        if (runContext.Terminate && runContext.Result != null && !wroteOutput)
            yield return new LLMStreamChunk { DeltaContent = runContext.Result };
    }

    private SkillRecoveryOrchestrator CreateSkillRecoveryOrchestrator(LLMRequest baseRequest) =>
        new(
            baseRequest.ToolContext?.SkillRecovery ?? AgentSkillRecoveryContext.Empty,
            toolContext => new StreamingToolExecutor(
                _toolLoop.Tools,
                _hooks,
                _toolLoop.ToolMiddlewares,
                requestMetadata: baseRequest.Metadata,
                toolContext: toolContext ?? AgentToolExecutionContextMapper.FromRequest(baseRequest)));

    private async Task RunStopHookAsync(
        string? finalContent,
        IReadOnlyList<ChatMessage> pendingHistoryMessages,
        CancellationToken ct)
    {
        if (_hooks == null)
            return;

        var stopCtx = new AIGAgentExecutionHookContext { AgentId = _agentId };
        stopCtx.Items["final_content"] = finalContent ?? "";
        stopCtx.Items["total_rounds"] = pendingHistoryMessages
            .Count(m => m.Role == "assistant" && m.ToolCalls is { Count: > 0 });
        try { await _hooks.RunStopAsync(stopCtx, ct); }
        catch { /* best-effort */ }
    }

    private async Task RunStopFailureHookAsync(Exception ex)
    {
        if (_hooks == null || ex is OperationCanceledException)
            return;

        var failCtx = new AIGAgentExecutionHookContext { AgentId = _agentId };
        failCtx.Items["error"] = ex;
        failCtx.Items["error_message"] = ex.Message;
        failCtx.Items["error_phase"] = "streaming_llm_or_tool_execution";
        try { await _hooks.RunStopFailureAsync(failCtx, CancellationToken.None); }
        catch { /* best-effort */ }
    }

    private async IAsyncEnumerable<LLMStreamChunk> StreamLlmRoundAsync(
        ILLMProvider provider,
        LLMRequest request,
        StreamingRoundScope roundScope,
        [EnumeratorCancellation] CancellationToken ct,
        Action<ToolCall>? onToolCallCompleted = null)
    {
        // Refactor (iter31/cluster-032-chatruntime-taskrun-business-loop):
        //   Old pattern: ChatRuntime.ChatStreamAsync 用 Task.Run + Channel<LLMStreamChunk>/ChannelWriter 在 actor turn 外跑 LLM/tool/hook/history 业务循环,违反 actor execution integrity
        //   New principle: ChatStreamAsync owns the stream flow directly; the Task.Run + Channel owned-stream loop and stream_buffer_capacity config were removed; middleware wrapping stays inside private bridge adapters.
        var llmHookContext = new AIGAgentExecutionHookContext { LLMRequest = request };
        if (_hooks != null) await _hooks.RunLLMRequestStartAsync(llmHookContext, ct);
        var llmStartedAt = Stopwatch.GetTimestamp();

        var llmCallContext = new LLMCallContext
        {
            Request = request,
            Provider = provider,
            CancellationToken = ct,
            IsStreaming = true,
        };
        AnnotateRequestIdentity(llmCallContext);

        string? streamedContent = null;
        string? streamedReasoningContent = null;
        TokenUsage? streamedUsage = null;
        IReadOnlyList<ToolCall>? streamedToolCalls = null;
        string? streamedFinishReason = null;

        var llmBridge = new LLMCallMiddlewareBridge();
        var middlewareTask = MiddlewarePipeline.RunLLMCallAsync(
            _llmMiddlewares,
            llmCallContext,
            llmBridge.WaitForCoreCompletionAsync);

        var coreTurnTask = llmBridge.WaitForCoreTurnAsync(ct);
        var middlewareWaitTask = middlewareTask.WaitAsync(ct);
        var readyTask = await Task.WhenAny(coreTurnTask, middlewareWaitTask).ConfigureAwait(false);
        await readyTask.ConfigureAwait(false);

        if (readyTask == coreTurnTask && !llmCallContext.Terminate)
        {
            var full = new StringBuilder();
            var fullReasoning = new StringBuilder();
            TokenUsage? usage = null;
            string? finishReason = null;
            var toolCalls = onToolCallCompleted != null
                ? new StreamingToolCallAccumulator(onToolCallCompleted)
                : new StreamingToolCallAccumulator();

            await using var providerEnumerator = provider.ChatStreamAsync(llmCallContext.Request, ct)
                .GetAsyncEnumerator(ct);
            while (true)
            {
                LLMStreamChunk chunk;
                try
                {
                    if (!await providerEnumerator.MoveNextAsync().ConfigureAwait(false))
                        break;

                    chunk = providerEnumerator.Current;
                }
                catch (Exception ex)
                {
                    llmBridge.FailCore(ex);
                    throw;
                }

                LLMStreamChunk? normalizedChunk;
                try
                {
                    normalizedChunk = NormalizeStreamChunk(chunk, toolCalls, full, fullReasoning, ref usage, ref finishReason);
                }
                catch (Exception ex)
                {
                    llmBridge.FailCore(ex);
                    throw;
                }

                if (normalizedChunk == null)
                    continue;

                yield return normalizedChunk;
            }

            streamedContent = full.Length > 0 ? full.ToString() : null;
            streamedReasoningContent = fullReasoning.Length > 0 ? fullReasoning.ToString() : null;
            streamedUsage = usage;
            streamedFinishReason = finishReason;
            var finalizedToolCalls = toolCalls.BuildToolCalls();
            streamedToolCalls = finalizedToolCalls.Count > 0 ? finalizedToolCalls : null;
            llmCallContext.Response = new LLMResponse
            {
                Content = streamedContent,
                ReasoningContent = streamedReasoningContent,
                Usage = streamedUsage,
                ToolCalls = streamedToolCalls,
                FinishReason = finishReason,
            };
            llmBridge.CompleteCore();
            await middlewareTask.ConfigureAwait(false);
        }

        if (llmCallContext.Terminate)
        {
            streamedContent = llmCallContext.Response?.Content;
            streamedReasoningContent = llmCallContext.Response?.ReasoningContent;
            streamedUsage = llmCallContext.Response?.Usage;
            streamedToolCalls = llmCallContext.Response?.ToolCalls;

            if (llmCallContext.Response != null)
            {
                foreach (var chunk in BuildSyntheticChunks(llmCallContext.Response))
                    yield return chunk;
            }
        }

        var response = llmCallContext.Response ?? new LLMResponse
        {
            Content = streamedContent,
            ReasoningContent = streamedReasoningContent,
            Usage = streamedUsage,
            ToolCalls = streamedToolCalls,
        };
        _history.Budget.RecordUsage(response.Usage);
        llmHookContext.LLMResponse = response;
        llmHookContext.Duration = Stopwatch.GetElapsedTime(llmStartedAt);
        if (_hooks != null) await _hooks.RunLLMRequestEndAsync(llmHookContext, ct);

        roundScope.Result = new StreamingRoundResult(
            response.Content,
            response.ReasoningContent,
            response.ToolCalls,
            llmCallContext.Terminate,
            response.FinishReason ?? streamedFinishReason,
            response.Usage);
    }

    internal async Task<StreamingRoundResult> ExecuteSingleLlmStepAsync(
        ILLMProvider provider,
        LLMRequest request,
        CancellationToken ct,
        Func<LLMStreamChunk, CancellationToken, Task>? onChunkAsync = null,
        Action<ToolCall>? onToolCallCompleted = null)
    {
        var roundScope = new StreamingRoundScope();
        await foreach (var _ in StreamLlmRoundAsync(provider, request, roundScope, ct, onToolCallCompleted))
        {
            if (onChunkAsync is not null)
                await onChunkAsync(_, ct).ConfigureAwait(false);
        }

        return roundScope.RequireResult();
    }

    internal async Task<IReadOnlyList<ToolExecutionResult>> ExecuteSingleToolStepAsync(
        IReadOnlyList<ToolCall> toolCalls,
        AgentToolExecutionContext? toolContext,
        CancellationToken ct)
    {
        var executor = new StreamingToolExecutor(
            _toolLoop.Tools,
            _hooks,
            _toolLoop.ToolMiddlewares,
            toolContext: toolContext);
        using var toolState = executor.CreateExecutionState();
        foreach (var toolCall in toolCalls)
            executor.AddTool(toolState, toolCall);

        var results = new List<ToolExecutionResult>();
        await foreach (var result in executor.GetRemainingResultsAsync(toolState, ct))
            results.Add(result);

        return results;
    }

    private static void AppendAssistantMessage(
        List<ChatMessage> messages,
        List<ChatMessage> pendingHistoryMessages,
        string? content,
        string? reasoningContent,
        IReadOnlyList<ToolCall>? toolCalls)
    {
        if (string.IsNullOrEmpty(content) && string.IsNullOrEmpty(reasoningContent) && toolCalls is not { Count: > 0 })
            return;

        var assistantMessage = new ChatMessage
        {
            Role = "assistant",
            Content = content,
            ReasoningContent = reasoningContent,
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
        IReadOnlyDictionary<string, string>? metadata,
        AgentToolExecutionContext? toolContext,
        LLMControlContext? llmControl)
    {
        var effectiveLlmControl = llmControl ?? baseRequest.LlmControl;
        var effectiveToolContext = toolContext is null
            ? AgentToolExecutionContextMapper.FromRequest(baseRequest)
            : AgentToolExecutionContextMapper.MergeExternalMetadata(
                toolContext,
                MergeMetadata(baseRequest.Metadata, metadata));
        effectiveToolContext = effectiveLlmControl?.ToToolContext(effectiveToolContext) ?? effectiveToolContext;
        if (!string.IsNullOrWhiteSpace(requestId))
        {
            effectiveToolContext = effectiveToolContext with
            {
                Request = effectiveToolContext.Request with { RequestId = requestId.Trim() },
            };
        }

        return new LLMRequest
        {
            Messages = baseRequest.Messages,
            RequestId = string.IsNullOrWhiteSpace(requestId) ? baseRequest.RequestId : requestId.Trim(),
            Metadata = AgentToolExecutionContextMapper.StripOwnedControlKeys(MergeMetadata(baseRequest.Metadata, metadata)),
            CallerContext = baseRequest.CallerContext,
            ToolContext = effectiveToolContext,
            RoutingContext = effectiveLlmControl?.ToRoutingContext(baseRequest.RoutingContext) ?? baseRequest.RoutingContext,
            LlmControl = effectiveLlmControl,
            Tools = FilterVisibleTools(baseRequest.Tools, effectiveToolContext.ToolVisibility),
            Model = baseRequest.Model,
            Temperature = baseRequest.Temperature,
            MaxTokens = baseRequest.MaxTokens,
            ResponseFormat = baseRequest.ResponseFormat,
        };
    }

    private static IReadOnlyList<IAgentTool>? FilterVisibleTools(
        IReadOnlyList<IAgentTool>? tools,
        AgentToolVisibilityScope visibility)
    {
        if (tools is not { Count: > 0 })
            return null;

        if (!visibility.IsRestricted)
            return tools;

        var visibleTools = tools.Where(tool => visibility.Allows(tool.Name)).ToList();
        return visibleTools.Count > 0 ? visibleTools : null;
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

        var callId = context.Request.ToolContext?.Request.CallId;
        if (!string.IsNullOrWhiteSpace(callId))
        {
            context.Items[LLMRequestMetadataKeys.CallId] = callId;
        }
    }

    private static LLMStreamChunk? NormalizeStreamChunk(
        LLMStreamChunk chunk,
        StreamingToolCallAccumulator toolCalls,
        StringBuilder fullContent,
        StringBuilder fullReasoningContent,
        ref TokenUsage? usage,
        ref string? finishReason)
    {
        ToolCall? normalizedToolCall = null;
        if (chunk.DeltaToolCall != null)
            normalizedToolCall = toolCalls.TrackDelta(chunk.DeltaToolCall);

        if (!string.IsNullOrEmpty(chunk.DeltaContent))
            fullContent.Append(chunk.DeltaContent);

        if (!string.IsNullOrEmpty(chunk.DeltaReasoningContent))
            fullReasoningContent.Append(chunk.DeltaReasoningContent);

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
            // Field-level patch (ADR-0021 §6 / canon §8): forward FinishReason so
            // the actor-edge closeout in ConversationReplyGenerator can observe
            // it. ChatRuntime itself remains transitional per aevatar#596 Phase A;
            // the cross-round aggregation and stream-local terminal contract live
            // at the actor edge, not here.
            FinishReason = chunk.FinishReason,
        };
    }

    private static LLMStreamChunk? SuppressVisibleToolCallRoundText(LLMStreamChunk chunk)
    {
        if (string.IsNullOrEmpty(chunk.DeltaContent) &&
            string.IsNullOrEmpty(chunk.DeltaReasoningContent) &&
            chunk.DeltaContentPart == null)
        {
            return chunk;
        }

        if (chunk.DeltaToolCall == null &&
            !chunk.IsLast &&
            chunk.Usage == null &&
            string.IsNullOrEmpty(chunk.FinishReason))
        {
            return null;
        }

        return new LLMStreamChunk
        {
            DeltaToolCall = chunk.DeltaToolCall,
            Usage = chunk.Usage,
            IsLast = chunk.IsLast,
            FinishReason = chunk.FinishReason,
        };
    }

    private static IReadOnlyList<LLMStreamChunk> BuildSyntheticChunks(LLMResponse response)
    {
        var chunks = new List<LLMStreamChunk>();

        if (!string.IsNullOrEmpty(response.ReasoningContent))
            chunks.Add(new LLMStreamChunk { DeltaReasoningContent = response.ReasoningContent });

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

    internal sealed record StreamingRoundResult(
        string? Content,
        string? ReasoningContent,
        IReadOnlyList<ToolCall>? ToolCalls,
        bool Terminated,
        string? FinishReason,
        TokenUsage? Usage);

    // Refactor (iter31/cluster-032-chatruntime-taskrun-business-loop):
    //   Old pattern: ChatRuntime.ChatStreamAsync 用 Task.Run + Channel<LLMStreamChunk>/ChannelWriter 在 actor turn 外跑 LLM/tool/hook/history 业务循环,违反 actor execution integrity
    //   New principle: ChatStreamAsync owns the stream flow directly; the Task.Run + Channel owned-stream loop and stream_buffer_capacity config were removed; middleware wrapping stays inside private bridge adapters.
    // refactor helper, no behavior change: private adapter for legacy Func<Task> agent middleware around the stream-owned core turn.
    private sealed class AgentRunMiddlewareBridge
    {
        private readonly TaskCompletionSource _coreTurn = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _coreCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task WaitForCoreCompletionAsync()
        {
            _coreTurn.TrySetResult();
            return _coreCompletion.Task;
        }

        public Task WaitForCoreTurnAsync(CancellationToken ct) => _coreTurn.Task.WaitAsync(ct);

        public void CompleteCore() => _coreCompletion.TrySetResult();

        public void FailCore(Exception ex)
        {
            _coreTurn.TrySetException(ex);
            _coreCompletion.TrySetException(ex);
        }
    }

    // Refactor (iter31/cluster-032-chatruntime-taskrun-business-loop):
    //   Old pattern: ChatRuntime.ChatStreamAsync 用 Task.Run + Channel<LLMStreamChunk>/ChannelWriter 在 actor turn 外跑 LLM/tool/hook/history 业务循环,违反 actor execution integrity
    //   New principle: ChatStreamAsync owns the stream flow directly; the Task.Run + Channel owned-stream loop and stream_buffer_capacity config were removed; middleware wrapping stays inside private bridge adapters.
    // refactor helper, no behavior change: private adapter lets Task-based LLM middleware wrap the stream-owned provider turn.
    private sealed class LLMCallMiddlewareBridge
    {
        private readonly TaskCompletionSource _coreTurn = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _coreCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task WaitForCoreCompletionAsync()
        {
            _coreTurn.TrySetResult();
            return _coreCompletion.Task;
        }

        public Task WaitForCoreTurnAsync(CancellationToken ct) => _coreTurn.Task.WaitAsync(ct);

        public void CompleteCore() => _coreCompletion.TrySetResult();

        public void FailCore(Exception ex)
        {
            _coreTurn.TrySetException(ex);
            _coreCompletion.TrySetException(ex);
        }
    }

    // Refactor (iter31/cluster-032-chatruntime-taskrun-business-loop):
    //   Old pattern: ChatRuntime.ChatStreamAsync 用 Task.Run + Channel<LLMStreamChunk>/ChannelWriter 在 actor turn 外跑 LLM/tool/hook/history 业务循环,违反 actor execution integrity
    //   New principle: ChatStreamAsync owns the stream flow directly; the Task.Run + Channel owned-stream loop and stream_buffer_capacity config were removed; middleware wrapping stays inside private bridge adapters.
    // refactor helper, no behavior change: carries the private stream round closeout without exposing a public stream middleware contract.
    private sealed class StreamingRoundScope
    {
        public StreamingRoundResult? Result { get; set; }

        public StreamingRoundResult RequireResult() =>
            Result ?? throw new InvalidOperationException("Streaming round completed without a result.");
    }

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
