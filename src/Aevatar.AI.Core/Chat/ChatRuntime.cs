// ─── ChatRuntime — Chat/ChatStream 执行逻辑 ───
// 组合 LLMProvider + History + ToolCallLoop + Hooks + Middleware。

using Aevatar.AI.Core.Hooks;
using Aevatar.AI.Core.Middleware;
using Aevatar.AI.Core.Tools;
using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.Middleware;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.Core.AgentProfiles;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Aevatar.Foundation.Abstractions.Helpers;
using System.Diagnostics;
using System.Runtime.ExceptionServices;
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
    private const int MaxIdenticalReadOnlyFailures = 2;
    private const int ModelInputSummaryMaxLength = 500;
    private readonly Func<ILLMProvider> _providerFactory;
    private readonly ChatHistory _history;
    private readonly ToolCallLoop _toolLoop;
    private readonly AgentHookPipeline? _hooks;
    private readonly Func<AgentTurnToolCatalog?, LLMRequest> _requestBuilder;
    private readonly IReadOnlyList<IAgentRunMiddleware> _agentMiddlewares;
    private readonly IReadOnlyList<ILLMCallMiddleware> _llmMiddlewares;
    private readonly string? _agentId;
    private readonly string? _agentName;
    private readonly ContextCompressionConfig _compressionConfig;
    private readonly bool _suppressToolCallRoundText;
    private readonly IChatToolCheckpointPort _toolCheckpointPort;
    private readonly ILogger _logger;

    public ChatRuntime(
        Func<ILLMProvider> providerFactory,
        ChatHistory history,
        ToolCallLoop toolLoop,
        AgentHookPipeline? hooks,
        Func<AgentTurnToolCatalog?, LLMRequest> requestBuilder,
        IReadOnlyList<IAgentRunMiddleware>? agentMiddlewares = null,
        IReadOnlyList<ILLMCallMiddleware>? llmMiddlewares = null,
        string? agentId = null,
        string? agentName = null,
        ContextCompressionConfig? compressionConfig = null,
        bool suppressToolCallRoundText = false,
        IChatToolCheckpointPort? toolCheckpointPort = null,
        ILogger? logger = null)
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
        _toolCheckpointPort = toolCheckpointPort ?? NoOpChatToolCheckpointPort.Instance;
        _logger = logger ?? NullLogger.Instance;
    }

    public ChatRuntimeStepExecutor CreateStepExecutor(AgentTurnToolCatalog? turnCatalog) =>
        new(
            _providerFactory,
            _toolLoop,
            _hooks,
            _requestBuilder,
            _llmMiddlewares,
            _history.Budget,
            _toolCheckpointPort,
            turnCatalog);

    /// <summary>流式 Chat，包裹 LLM Call Middleware。</summary>
    public IAsyncEnumerable<LLMStreamChunk> ChatStreamAsync(
        string userMessage,
        AgentTurnToolCatalog? turnCatalog,
        CancellationToken ct = default) =>
        ChatStreamAsync(
            [ContentPart.TextPart(userMessage)],
            DefaultMaxToolRounds,
            requestId: null,
            turnCatalog: turnCatalog,
            metadata: null,
            ct);

    /// <summary>流式 Chat（多模态内容）。</summary>
    public IAsyncEnumerable<LLMStreamChunk> ChatStreamAsync(
        IReadOnlyList<ContentPart> userContent,
        AgentTurnToolCatalog? turnCatalog,
        CancellationToken ct = default) =>
        ChatStreamAsync(
            userContent,
            DefaultMaxToolRounds,
            requestId: null,
            turnCatalog: turnCatalog,
            metadata: null,
            ct);

    /// <summary>流式 Chat，允许显式控制 tool calling 轮数。</summary>
    public IAsyncEnumerable<LLMStreamChunk> ChatStreamAsync(
        string userMessage,
        int maxToolRounds,
        AgentTurnToolCatalog? turnCatalog,
        CancellationToken ct = default) =>
        ChatStreamAsync(
            [ContentPart.TextPart(userMessage)],
            maxToolRounds,
            requestId: null,
            turnCatalog: turnCatalog,
            metadata: null,
            ct);

    /// <summary>流式 Chat（多模态内容），允许显式控制 tool calling 轮数。</summary>
    public IAsyncEnumerable<LLMStreamChunk> ChatStreamAsync(
        IReadOnlyList<ContentPart> userContent,
        int maxToolRounds,
        AgentTurnToolCatalog? turnCatalog,
        CancellationToken ct = default) =>
        ChatStreamAsync(
            userContent,
            maxToolRounds,
            requestId: null,
            turnCatalog: turnCatalog,
            metadata: null,
            ct);

    /// <summary>流式 Chat，显式传入稳定 request id 和 metadata（默认 tool 轮数）。</summary>
    public IAsyncEnumerable<LLMStreamChunk> ChatStreamAsync(
        string userMessage,
        string? requestId,
        AgentTurnToolCatalog? turnCatalog,
        IReadOnlyDictionary<string, string>? metadata = null,
        CancellationToken ct = default) =>
        ChatStreamAsync([ContentPart.TextPart(userMessage)], DefaultMaxToolRounds, requestId, turnCatalog, metadata, ct);

    public IAsyncEnumerable<LLMStreamChunk> ChatStreamAsync(
        IReadOnlyList<ContentPart> userContent,
        int maxToolRounds,
        string? requestId,
        LLMControlContext? llmControl,
        AgentToolExecutionContext? toolContext,
        AgentTurnToolCatalog? turnCatalog,
        IReadOnlyDictionary<string, string>? metadata = null,
        CancellationToken ct = default) =>
        ChatStreamAsync(userContent, maxToolRounds, requestId, metadata, toolContext, llmControl, turnCatalog, ct);

    internal IAsyncEnumerable<LLMStreamChunk> ContinueChatStreamAsync(
        IReadOnlyList<ContentPart> userContent,
        IReadOnlyList<ChatMessage> committedToolTranscript,
        int maxToolRounds,
        string? requestId,
        LLMControlContext? llmControl,
        AgentToolExecutionContext? toolContext,
        AgentTurnToolCatalog? turnCatalog,
        IReadOnlyDictionary<string, string>? metadata = null,
        CancellationToken ct = default) =>
        ChatStreamCoreAsync(
            userContent,
            maxToolRounds,
            requestId,
            metadata,
            toolContext,
            llmControl,
            turnCatalog,
            committedToolTranscript,
            ct);

    public IAsyncEnumerable<LLMStreamChunk> ChatStreamAsync(
        IReadOnlyList<ContentPart> userContent,
        int maxToolRounds,
        string? requestId,
        AgentToolExecutionContext? toolContext,
        AgentTurnToolCatalog? turnCatalog,
        IReadOnlyDictionary<string, string>? metadata = null,
        CancellationToken ct = default) =>
        ChatStreamAsync(
            userContent,
            maxToolRounds,
            requestId,
            metadata,
            toolContext,
            llmControl: null,
            turnCatalog: turnCatalog,
            ct);

    /// <summary>流式 Chat，显式传入稳定 request id 和 metadata + tool 轮数。</summary>
    public async IAsyncEnumerable<LLMStreamChunk> ChatStreamAsync(
        string userMessage,
        int maxToolRounds,
        string? requestId,
        AgentTurnToolCatalog? turnCatalog,
        IReadOnlyDictionary<string, string>? metadata = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var chunk in ChatStreamAsync(
                           [ContentPart.TextPart(userMessage)],
                           maxToolRounds,
                           requestId,
                           turnCatalog,
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
        AgentTurnToolCatalog? turnCatalog,
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
                           turnCatalog: turnCatalog,
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
        AgentTurnToolCatalog? turnCatalog,
        [EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var chunk in ChatStreamCoreAsync(
                           userContent,
                           maxToolRounds,
                           requestId,
                           metadata,
                           toolContext,
                           llmControl,
                           turnCatalog,
                           committedToolTranscript: null,
                           ct))
        {
            yield return chunk;
        }
    }

    private async IAsyncEnumerable<LLMStreamChunk> ChatStreamCoreAsync(
        IReadOnlyList<ContentPart> userContent,
        int maxToolRounds,
        string? requestId,
        IReadOnlyDictionary<string, string>? metadata,
        AgentToolExecutionContext? toolContext,
        LLMControlContext? llmControl,
        AgentTurnToolCatalog? turnCatalog,
        IReadOnlyList<ChatMessage>? committedToolTranscript,
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
        var readyTask = await Task.WhenAny(coreTurnTask, middlewareWaitTask);
        await readyTask;

        if (readyTask == coreTurnTask && !runContext.Terminate)
        {
            await using var streamEnumerator = RunChatStreamCoreAsync(
                    normalizedUserContent,
                    effectiveMaxToolRounds,
                    requestId,
                    metadata,
                    toolContext,
                    llmControl,
                    turnCatalog,
                    committedToolTranscript,
                    runContext,
                    runToken)
                .GetAsyncEnumerator(runToken);
            while (true)
            {
                LLMStreamChunk current;
                try
                {
                    if (!await streamEnumerator.MoveNextAsync())
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
        await middlewareTask;
    }

    private async IAsyncEnumerable<LLMStreamChunk> RunChatStreamCoreAsync(
        IReadOnlyList<ContentPart> normalizedUserContent,
        int effectiveMaxToolRounds,
        string? requestId,
        IReadOnlyDictionary<string, string>? metadata,
        AgentToolExecutionContext? toolContext,
        LLMControlContext? llmControl,
        AgentTurnToolCatalog? turnCatalog,
        IReadOnlyList<ChatMessage>? committedToolTranscript,
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
                           turnCatalog,
                           committedToolTranscript,
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
        AgentTurnToolCatalog? turnCatalog,
        IReadOnlyList<ChatMessage>? committedToolTranscript,
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
        var baseRequest = ChatRuntimeRequestBuilder.Build(
            _requestBuilder(turnCatalog),
            requestId,
            metadata,
            toolContext,
            llmControl,
            turnCatalog);
        var provider = _providerFactory();
        runContext.Items["gen_ai.provider.name"] = provider.Name;
        var messages = BuildMessagesWithPending(baseRequest, userMsg);
        if (committedToolTranscript is { Count: > 0 })
        {
            var validTranscript = ChatMessageToolCallTranscript.WithoutInvalidToolCallPairs(committedToolTranscript);
            messages.AddRange(validTranscript);
            pendingHistoryMessages.AddRange(validTranscript);
        }
        string? finalContent = null;
        var lengthRecoveryCount = 0;
        var hasStreamedTextContent = false;
        var authorizedTools = ToolCallLoop.CreateRequestToolManager(baseRequest.Tools);
        var skillRecovery = CreateSkillRecoveryOrchestrator(baseRequest, () => authorizedTools);
        var executedToolOutcomes = new List<ToolOutcomeReplyFact>();
        var readOnlyFailureCounts = new Dictionary<ReadOnlyFailureKey, int>();
        var retiredToolNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var toolLoopSuspended = false;
        var toolLoopTerminated = false;
        var modelInvocationRound = -1;

        if (skillRecovery.RequiresInitialSearch)
        {
            await foreach (var progress in skillRecovery.ApplyInitialDirectivesAsync(
                               AgentToolExecutionContextMapper.FromRequest(baseRequest),
                               messages,
                               pendingHistoryMessages,
                               ToolCallLoop.ComposeRoundCallId(baseRequest.RequestId, 0),
                               runToken))
            {
                wroteOutput = true;
                yield return BuildSkillRecoveryChunk(progress, authorizedTools);
            }
        }

        for (var round = 0; round < effectiveMaxToolRounds; round++)
        {
            // Keep the final catalog immutable for the entire turn. One-shot reuse state is
            // execution state and must not mutate the model-visible schema between rounds.
            var roundTools = baseRequest.Tools;
            authorizedTools = ToolCallLoop.CreateRequestToolManager(roundTools);
            if (hasStreamedTextContent)
            {
                wroteOutput = true;
                yield return new LLMStreamChunk { DeltaContent = "\n\n" };
            }

            var authorizedToolContext = AgentToolExecutionContextMapper.FromRequest(baseRequest);
            var streamingExecutor = new StreamingToolExecutor(
                authorizedTools, _hooks,
                toolContext: authorizedToolContext,
                toolExecutionPort: _toolLoop.ToolExecutionPort,
                checkpointPort: _toolCheckpointPort,
                approvalContinuationMode: _toolLoop.ApprovalContinuationMode,
                logger: _logger);
            using var streamingToolState = streamingExecutor.CreateExecutionState();

            void BindAuthorizedRequest(LLMRequest authorizedRequest)
            {
                authorizedToolContext = AgentToolExecutionContextMapper.FromRequest(authorizedRequest);
                authorizedTools = ToolCallLoop.CreateRequestToolManager(authorizedRequest.Tools);
                streamingExecutor = new StreamingToolExecutor(
                    authorizedTools, _hooks,
                    toolContext: authorizedToolContext,
                    toolExecutionPort: _toolLoop.ToolExecutionPort,
                    checkpointPort: _toolCheckpointPort,
                    approvalContinuationMode: _toolLoop.ApprovalContinuationMode,
                    logger: _logger);
            }

            var roundRequest = new LLMRequest
            {
                Messages = BuildMutationClaimConstrainedMessages(
                    messages,
                    executedToolOutcomes,
                    toolReceipts: null,
                    retiredToolNames: retiredToolNames,
                    mergeIntoExistingSystem: round == 0),
                RequestId = baseRequest.RequestId,
                Metadata = AgentToolExecutionContextMapper.StripOwnedControlKeys(baseRequest.Metadata),
                CallerContext = baseRequest.CallerContext,
                ToolContext = AgentToolExecutionContextMapper.FromRequestWithCallId(
                    baseRequest,
                    ToolCallLoop.ComposeRoundCallId(baseRequest.RequestId, round)),
                RoutingContext = baseRequest.RoutingContext,
                LlmControl = baseRequest.LlmControl,
                RouteTarget = baseRequest.RouteTarget?.Clone(),
                Tools = roundTools,
                ToolCatalogProof = baseRequest.ToolCatalogProof,
                Model = baseRequest.Model,
                Temperature = baseRequest.Temperature,
                MaxTokens = baseRequest.MaxTokens,
                AllowMultipleToolCalls = baseRequest.AllowMultipleToolCalls,
                ResponseFormat = baseRequest.ResponseFormat,
            };
            var roundScope = new StreamingRoundScope();
            var currentModelInvocationRound = checked(++modelInvocationRound);
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
                                   currentModelInvocationRound,
                                   emitResolvedToolCallStarts: false,
                                   onRequestAuthorized: BindAuthorizedRequest))
                {
                    if (chunk.ToolCallStarted != null)
                    {
                        continue;
                    }

                    if (chunk.LLMInvocationStarted != null || chunk.LLMInvocationCompleted != null)
                    {
                        yield return chunk;
                        continue;
                    }

                    roundChunks.Add(chunk);
                }

                roundResult = roundScope.RequireResult();
                parsedTextToolCall = roundResult.ToolCalls is not { Count: > 0 } && roundResult.Content != null
                    ? TextToolCallParser.Parse(roundResult.Content)
                    : null;
                var roundCallsTools = !roundResult.Terminated &&
                                      (roundResult.ToolCalls is { Count: > 0 } ||
                                       parsedTextToolCall?.ToolCalls.Count > 0);

                var recoveryCallId = ToolCallLoop.ComposeRoundCallId(baseRequest.RequestId, round);
                if (!roundCallsTools && skillRecovery.ShouldRecoverFinalAnswer(
                        pendingHistoryMessages,
                        roundResult.Content,
                        recoveryCallId))
                {
                    streamingExecutor.Discard(streamingToolState);
                    await foreach (var progress in skillRecovery.RecoverFinalAnswerAsync(
                                       roundRequest.ToolContext,
                                       messages,
                                       pendingHistoryMessages,
                                       roundResult.Content,
                                       recoveryCallId,
                                       runToken))
                    {
                        wroteOutput = true;
                        yield return BuildSkillRecoveryChunk(progress, authorizedTools);
                    }
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
                                   currentModelInvocationRound,
                                   emitResolvedToolCallStarts: false,
                                   onRequestAuthorized: BindAuthorizedRequest))
                {
                    if (chunk.ToolCallStarted != null)
                        continue;

                    if (IsVisibleOutputChunk(chunk))
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

                        var parsedAssistantToolCallMessage = AppendAssistantMessage(
                            messages,
                            pendingHistoryMessages,
                            parsed.CleanedContent,
                            roundResult.ReasoningContent,
                            parsed.ToolCalls)!;

                        var textToolExecutor = new StreamingToolExecutor(
                            authorizedTools, _hooks,
                            toolContext: authorizedToolContext,
                            toolExecutionPort: _toolLoop.ToolExecutionPort,
                            checkpointPort: _toolCheckpointPort,
                            approvalContinuationMode: _toolLoop.ApprovalContinuationMode,
                            logger: _logger);
                        using var textToolState = textToolExecutor.CreateExecutionState();
                        var executableTextCalls = parsed.ToolCalls
                            .Where(call => !retiredToolNames.Contains(call.Name))
                            .ToArray();
                        var retiredTextResults = BuildRetiredToolResults(parsed.ToolCalls, retiredToolNames);
                        var preparedTextOperations = await textToolExecutor.PrepareBatchAsync(
                            baseRequest.RequestId ?? string.Empty,
                            round,
                            executableTextCalls,
                            runToken);
                        foreach (var operation in preparedTextOperations)
                        {
                            yield return BuildToolCallStartedChunk(operation);
                            textToolExecutor.AddTool(textToolState, operation);
                        }
                        await foreach (var result in textToolExecutor.GetRemainingResultsAsync(textToolState, runToken))
                        {
                            executedToolOutcomes.Add(BuildToolOutcomeReplyFact(
                                authorizedTools,
                                result,
                                parsed.ToolCalls));
                            RetireTurnToolAfterSuccess(authorizedTools, result, retiredToolNames);
                            parsedAssistantToolCallMessage = FailedToolCallArgumentRedactor.Redact(
                                messages,
                                pendingHistoryMessages,
                                parsedAssistantToolCallMessage,
                                result);
                            yield return BuildToolCallCompletedChunk(
                                result,
                                ResolveOperationId(preparedTextOperations, result.CallId));
                            var toolMsg = ToolCallLoop.BuildToolResultMessage(
                                result.CallId,
                                result.ToolName,
                                ToolExecutionResultHistory.ResolveSafeContent(result),
                                result.Receipt);
                            messages.Add(toolMsg);
                            pendingHistoryMessages.Add(toolMsg);
                            if (RequiresToolLoopSuspension(result))
                                toolLoopSuspended = true;
                            if (ReachedPersistentReadOnlyFailureLimit(
                                    result,
                                    preparedTextOperations,
                                    readOnlyFailureCounts))
                            {
                                toolLoopTerminated = true;
                            }
                        }

                        foreach (var result in retiredTextResults)
                        {
                            yield return BuildToolCallCompletedChunk(result);
                            var toolMsg = ToolCallLoop.BuildToolResultMessage(
                                result.CallId,
                                result.ToolName,
                                result.Result,
                                receipt: null);
                            messages.Add(toolMsg);
                            pendingHistoryMessages.Add(toolMsg);
                        }

                        if (toolLoopSuspended || toolLoopTerminated)
                            break;

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

                var recoveryCallId = ToolCallLoop.ComposeRoundCallId(baseRequest.RequestId, round);
                if (skillRecovery.ShouldRecoverFinalAnswer(
                        pendingHistoryMessages,
                        roundResult.Content,
                        recoveryCallId))
                {
                    await foreach (var progress in skillRecovery.RecoverFinalAnswerAsync(
                                       roundRequest.ToolContext,
                                       messages,
                                       pendingHistoryMessages,
                                       roundResult.Content,
                                       recoveryCallId,
                                       runToken))
                    {
                        wroteOutput = true;
                        yield return BuildSkillRecoveryChunk(progress, authorizedTools);
                    }
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

            }

            var executableRoundCalls = roundResult.ToolCalls!
                .Where(call => !retiredToolNames.Contains(call.Name))
                .ToArray();
            var retiredRoundResults = BuildRetiredToolResults(
                roundResult.ToolCalls,
                retiredToolNames);
            var preparedRoundOperations = await streamingExecutor.PrepareBatchAsync(
                baseRequest.RequestId ?? string.Empty,
                round,
                executableRoundCalls,
                runToken);
            foreach (var operation in preparedRoundOperations)
            {
                yield return BuildToolCallStartedChunk(operation);
                streamingExecutor.AddTool(streamingToolState, operation);
            }

            var assistantToolCallMessage = AppendAssistantMessage(
                messages,
                pendingHistoryMessages,
                roundResult.Content,
                roundResult.ReasoningContent,
                roundResult.ToolCalls)!;

            await foreach (var result in streamingExecutor.GetRemainingResultsAsync(streamingToolState, runToken))
            {
                executedToolOutcomes.Add(BuildToolOutcomeReplyFact(
                    authorizedTools,
                    result,
                    roundResult.ToolCalls));
                RetireTurnToolAfterSuccess(authorizedTools, result, retiredToolNames);
                assistantToolCallMessage = FailedToolCallArgumentRedactor.Redact(
                    messages,
                    pendingHistoryMessages,
                    assistantToolCallMessage,
                    result);
                yield return BuildToolCallCompletedChunk(
                    result,
                    ResolveOperationId(preparedRoundOperations, result.CallId));
                var toolMsg = ToolCallLoop.BuildToolResultMessage(
                    result.CallId,
                    result.ToolName,
                    ToolExecutionResultHistory.ResolveSafeContent(result),
                    result.Receipt);
                messages.Add(toolMsg);
                pendingHistoryMessages.Add(toolMsg);
                if (RequiresToolLoopSuspension(result))
                    toolLoopSuspended = true;
                if (ReachedPersistentReadOnlyFailureLimit(
                        result,
                        preparedRoundOperations,
                        readOnlyFailureCounts))
                {
                    toolLoopTerminated = true;
                }
            }

            foreach (var result in retiredRoundResults)
            {
                yield return BuildToolCallCompletedChunk(result);
                var toolMsg = ToolCallLoop.BuildToolResultMessage(
                    result.CallId,
                    result.ToolName,
                    result.Result,
                    receipt: null);
                messages.Add(toolMsg);
                pendingHistoryMessages.Add(toolMsg);
            }

            if (toolLoopSuspended || toolLoopTerminated)
                break;
        }

        if (finalContent == null && !toolLoopSuspended)
        {
            if (hasStreamedTextContent)
            {
                wroteOutput = true;
                yield return new LLMStreamChunk { DeltaContent = "\n\n" };
            }

            var finalRequest = new LLMRequest
            {
                Messages = BuildMutationClaimConstrainedMessages(
                    messages,
                    executedToolOutcomes,
                    toolReceipts: null,
                    retiredToolNames: retiredToolNames),
                RequestId = baseRequest.RequestId,
                Metadata = AgentToolExecutionContextMapper.StripOwnedControlKeys(baseRequest.Metadata),
                CallerContext = baseRequest.CallerContext,
                ToolContext = AgentToolExecutionContextMapper.FromRequestWithCallId(
                    baseRequest,
                    ToolCallLoop.ComposeFinalCallId(baseRequest.RequestId)),
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
            var finalScope = new StreamingRoundScope();
            authorizedTools = ToolCallLoop.CreateRequestToolManager(finalRequest.Tools);
            await foreach (var chunk in StreamLlmRoundAsync(
                               provider,
                               finalRequest,
                               finalScope,
                               runToken,
                               checked(++modelInvocationRound),
                               onRequestAuthorized: request =>
                                   authorizedTools = ToolCallLoop.CreateRequestToolManager(request.Tools)))
            {
                if (IsVisibleOutputChunk(chunk))
                    wroteOutput = true;
                yield return chunk;
            }

            var finalRound = finalScope.RequireResult();
            var finalParsed = finalRound.Content != null
                ? TextToolCallParser.Parse(finalRound.Content)
                : null;
            if (finalParsed?.ToolCalls.Count > 0)
            {
                var assistantToolCallMessage = AppendAssistantMessage(
                    messages,
                    pendingHistoryMessages,
                    finalParsed.CleanedContent,
                    finalRound.ReasoningContent,
                    finalParsed.ToolCalls)!;

                var finalToolExecutor = new StreamingToolExecutor(
                    authorizedTools, _hooks,
                    toolContext: finalRequest.ToolContext,
                    toolExecutionPort: _toolLoop.ToolExecutionPort,
                    checkpointPort: _toolCheckpointPort,
                    approvalContinuationMode: _toolLoop.ApprovalContinuationMode,
                    logger: _logger);
                using var finalToolState = finalToolExecutor.CreateExecutionState();
                var preparedFinalOperations = await finalToolExecutor.PrepareBatchAsync(
                    baseRequest.RequestId ?? string.Empty,
                    effectiveMaxToolRounds,
                    finalParsed.ToolCalls,
                    runToken);
                foreach (var operation in preparedFinalOperations)
                {
                    yield return BuildToolCallStartedChunk(operation);
                    finalToolExecutor.AddTool(finalToolState, operation);
                }
                await foreach (var result in finalToolExecutor.GetRemainingResultsAsync(finalToolState, runToken))
                {
                    executedToolOutcomes.Add(BuildToolOutcomeReplyFact(
                        authorizedTools,
                        result,
                        finalParsed.ToolCalls));
                    assistantToolCallMessage = FailedToolCallArgumentRedactor.Redact(
                        messages,
                        pendingHistoryMessages,
                        assistantToolCallMessage,
                        result);
                    yield return BuildToolCallCompletedChunk(
                        result,
                        ResolveOperationId(preparedFinalOperations, result.CallId));
                    var toolMsg = ToolCallLoop.BuildToolResultMessage(
                        result.CallId,
                        result.ToolName,
                        ToolExecutionResultHistory.ResolveSafeContent(result),
                        result.Receipt);
                    messages.Add(toolMsg);
                    pendingHistoryMessages.Add(toolMsg);
                    if (RequiresToolLoopSuspension(result))
                        toolLoopSuspended = true;
                }

                if (!toolLoopSuspended)
                {
                    var summaryRequest = new LLMRequest
                    {
                        Messages = BuildMutationClaimConstrainedMessages(
                            messages,
                            executedToolOutcomes,
                            toolReceipts: null,
                            retiredToolNames: retiredToolNames),
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
                    var summaryScope = new StreamingRoundScope();
                    await foreach (var chunk in StreamLlmRoundAsync(
                                       provider,
                                       summaryRequest,
                                       summaryScope,
                                       runToken,
                                       checked(++modelInvocationRound)))
                    {
                        if (IsVisibleOutputChunk(chunk))
                            wroteOutput = true;
                        yield return chunk;
                    }

                    var summaryRound = summaryScope.RequireResult();
                    AppendAssistantMessage(messages, pendingHistoryMessages, summaryRound.Content, summaryRound.ReasoningContent, toolCalls: null);
                    finalContent = summaryRound.Content;
                }
            }
            else
            {
                AppendAssistantMessage(messages, pendingHistoryMessages, finalRound.Content, finalRound.ReasoningContent, toolCalls: null);
                finalContent = finalRound.Content;
            }
        }

        runContext.Result = finalContent;
        _history.AddRange(pendingHistoryMessages);

        await RunStopHookAsync(runContext.Result, pendingHistoryMessages, runToken);

        if (runContext.Terminate && runContext.Result != null && !wroteOutput)
            yield return new LLMStreamChunk { DeltaContent = runContext.Result };
    }

    private SkillRecoveryOrchestrator CreateSkillRecoveryOrchestrator(
        LLMRequest baseRequest,
        Func<ToolManager> authorizedTools) =>
        new(
            baseRequest.ToolContext?.SkillRecovery ?? AgentSkillRecoveryContext.Empty,
            toolContext => new StreamingToolExecutor(
                authorizedTools(),
                _hooks,
                requestMetadata: baseRequest.Metadata,
                toolContext: toolContext ?? AgentToolExecutionContextMapper.FromRequest(baseRequest),
                toolExecutionPort: _toolLoop.ToolExecutionPort,
                checkpointPort: _toolCheckpointPort,
                approvalContinuationMode: _toolLoop.ApprovalContinuationMode,
                logger: _logger),
            baseRequest.RequestId ?? string.Empty);

    private static List<ChatMessage> BuildMutationClaimConstrainedMessages(
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<ToolOutcomeReplyFact> toolOutcomes,
        IReadOnlyList<AgentToolReceipt>? toolReceipts,
        IReadOnlySet<string>? retiredToolNames = null,
        bool mergeIntoExistingSystem = false)
    {
        var constraints = ToolOutcomeReplyConstraintBuilder
            .BuildMutationClaimConstraints(toolOutcomes, toolReceipts)
            .ToList();
        if (retiredToolNames is { Count: > 0 })
        {
            constraints.Add(ChatMessage.System(
                "System constraint: These tools already completed their single successful execution for this turn and are no longer available: " +
                string.Join(", ", retiredToolNames.OrderBy(static name => name, StringComparer.Ordinal)) +
                ". Do not call them again. Continue with the available tools, using read-only observation when a receipt identifies an asynchronous run."));
        }

        return ToolOutcomeReplyConstraintBuilder.ApplyConstraints(
            messages,
            constraints,
            mergeIntoExistingSystem);
    }

    private static void RetireTurnToolAfterSuccess(
        ToolManager tools,
        ToolExecutionResult result,
        ISet<string> retiredToolNames)
    {
        if (result.IsError || result.Receipt?.Status != AgentToolReceiptStatus.Success)
            return;

        var tool = tools.Get(result.ToolName);
        if (tool?.TurnReusePolicy == AgentToolTurnReusePolicy.RetireAfterSuccess)
            retiredToolNames.Add(tool.Name);
    }

    private static IReadOnlyList<ToolExecutionResult> BuildRetiredToolResults(
        IEnumerable<ToolCall> calls,
        IReadOnlySet<string> retiredToolNames) =>
        calls
            .Where(call => retiredToolNames.Contains(call.Name))
            .Select(static call => new ToolExecutionResult(
                call.Id,
                call.Name,
                "{\"error\":true,\"error_code\":\"TOOL_RETIRED_FOR_TURN\",\"message\":\"This single-use tool already succeeded in the current turn.\"}",
                IsError: true))
            .ToArray();

    private ToolOutcomeReplyFact BuildToolOutcomeReplyFact(
        ToolManager tools,
        ToolExecutionResult result,
        IReadOnlyList<ToolCall>? toolCalls)
    {
        var matchingCall = toolCalls?.FirstOrDefault(call =>
            string.Equals(call.Id, result.CallId, StringComparison.Ordinal) ||
            string.Equals(call.Name, result.ToolName, StringComparison.OrdinalIgnoreCase));
        var tool = tools.Get(result.ToolName)
                   ?? (matchingCall is null ? null : tools.Get(matchingCall.Name));
        return new ToolOutcomeReplyFact(
            tool,
            matchingCall?.ArgumentsJson,
            Succeeded: !result.IsError,
            result.Receipt?.Clone());
    }

    private static LLMStreamChunk BuildToolCallStartedChunk(ToolCall toolCall, ToolManager tools) =>
        new()
        {
            ToolCallStarted = new ToolCallStartedChunk
            {
                ToolCall = CloneProgressToolCall(toolCall),
                Presentation = ToolPresentationDescriptors.Snapshot(
                    tools.Get(toolCall.Name),
                    toolCall.Name,
                    toolCall.ArgumentsJson),
            },
        };

    private static LLMStreamChunk BuildToolCallStartedChunk(PreparedChatToolOperation operation) =>
        new()
        {
            ToolCallStarted = new ToolCallStartedChunk
            {
                ToolCall = CloneProgressToolCall(operation.ToolCall),
                Presentation = operation.Presentation.Clone(),
                OperationId = operation.OperationId,
            },
        };

    private static ToolCall CloneProgressToolCall(ToolCall toolCall) => new()
    {
        Id = toolCall.Id,
        Name = toolCall.Name,
        ArgumentsJson = toolCall.ArgumentsJson,
    };

    private static string ResolveOperationId(
        IReadOnlyList<PreparedChatToolOperation> operations,
        string callId) =>
        operations.FirstOrDefault(operation =>
            string.Equals(operation.ToolCall.Id, callId, StringComparison.Ordinal))?.OperationId ?? string.Empty;

    private static LLMStreamChunk BuildSkillRecoveryChunk(
        SkillRecoveryToolProgress progress,
        ToolManager tools)
    {
        if (progress.StartedToolCall != null)
        {
            var chunk = BuildToolCallStartedChunk(progress.StartedToolCall, tools);
            return new LLMStreamChunk
            {
                ToolCallStarted = new ToolCallStartedChunk
                {
                    ToolCall = chunk.ToolCallStarted!.ToolCall,
                    Presentation = chunk.ToolCallStarted.Presentation,
                    OperationId = progress.OperationId,
                },
            };
        }
        if (progress.CompletedResult != null)
            return BuildToolCallCompletedChunk(progress.CompletedResult.Value, progress.OperationId);

        throw new InvalidOperationException("Skill recovery progress requires a typed tool lifecycle payload.");
    }

    private static LLMStreamChunk BuildToolCallCompletedChunk(
        ToolExecutionResult result,
        string operationId = "") =>
        new()
        {
            ToolReceipt = result.Receipt?.Clone(),
            ToolCallCompleted = new ToolCallCompletedChunk
            {
                CallId = result.CallId,
                ToolName = result.ToolName,
                ResultJson = ToolExecutionResultHistory.ResolveSafeContent(result),
                Success = !result.IsError,
                Error = result.Receipt?.ErrorMessage ?? string.Empty,
                Receipt = result.Receipt?.Clone(),
                OperationId = operationId,
            },
        };

    private static bool RequiresToolLoopSuspension(ToolExecutionResult result) =>
        result.Receipt?.Status is AgentToolReceiptStatus.ApprovalRequired or
            AgentToolReceiptStatus.AuthorizationRequired;

    private static bool ReachedPersistentReadOnlyFailureLimit(
        ToolExecutionResult result,
        IReadOnlyList<PreparedChatToolOperation> operations,
        Dictionary<ReadOnlyFailureKey, int> failureCounts)
    {
        if (!result.IsError || result.Receipt?.Status != AgentToolReceiptStatus.Error)
            return false;

        var operation = operations.FirstOrDefault(candidate =>
            string.Equals(candidate.ToolCall.Id, result.CallId, StringComparison.Ordinal));
        if (operation?.ReplayPolicy != AgentToolReplayPolicy.ReadOnlyRetryable)
            return false;

        var key = new ReadOnlyFailureKey(
            operation.ToolCall.Name,
            AgentToolArgumentsDigest.ComputeSha256(operation.ToolCall.ArgumentsJson),
            string.IsNullOrWhiteSpace(result.Receipt.ErrorCode)
                ? "tool_error"
                : result.Receipt.ErrorCode.Trim());
        var count = failureCounts.TryGetValue(key, out var existing) ? existing + 1 : 1;
        failureCounts[key] = count;
        return count >= MaxIdenticalReadOnlyFailures;
    }

    private readonly record struct ReadOnlyFailureKey(
        string ToolName,
        string ArgumentsSha256,
        string ErrorCode);

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
        catch (Exception hookException)
        {
            _logger.LogWarning(
                hookException,
                "Stop hook failed for agent {AgentId}; continuing because hooks are best-effort.",
                _agentId);
        }
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
        catch (Exception hookException)
        {
            _logger.LogWarning(
                hookException,
                "Stop-failure hook failed for agent {AgentId}; continuing because hooks are best-effort.",
                _agentId);
        }
    }

    private async IAsyncEnumerable<LLMStreamChunk> StreamLlmRoundAsync(
        ILLMProvider provider,
        LLMRequest request,
        StreamingRoundScope roundScope,
        [EnumeratorCancellation] CancellationToken ct,
        int round,
        bool emitResolvedToolCallStarts = false,
        Action<LLMRequest>? onRequestAuthorized = null)
    {
        var operationId = BuildModelInvocationOperationId(request.RequestId, round);
        LLMInvocationStartedChunk? started = null;

        Exception? failure = null;
        await using var enumerator = StreamLlmRoundCoreAsync(
                provider,
                request,
                roundScope,
                operationId,
                round,
                ct,
                emitResolvedToolCallStarts,
                onRequestAuthorized)
            .GetAsyncEnumerator(ct);
        while (true)
        {
            if (started is not null && ct.IsCancellationRequested)
            {
                failure = new OperationCanceledException(ct);
                break;
            }

            bool moved;
            try
            {
                moved = await enumerator.MoveNextAsync();
            }
            catch (Exception ex)
            {
                failure = ex;
                break;
            }

            if (!moved)
                break;

            var current = enumerator.Current;
            if (current.LLMInvocationStarted != null)
            {
                started = current.LLMInvocationStarted;
                AgentTurnToolCatalogTelemetry.RecordToolRound(request.ToolCatalogProof, round);
                yield return current;
                continue;
            }

            if (started is not null && ct.IsCancellationRequested)
            {
                failure = new OperationCanceledException(ct);
                break;
            }

            yield return current;
        }

        if (failure is not null)
        {
            AgentTurnToolCatalogTelemetry.RecordOutcome(
                request.ToolCatalogProof,
                failure is OperationCanceledException ? "cancelled" : "failed");
            if (started is null)
            {
                ExceptionDispatchInfo.Capture(failure).Throw();
            }

            yield return new LLMStreamChunk
            {
                LLMInvocationCompleted = new LLMInvocationCompletedChunk
                {
                    OperationId = operationId,
                    Round = round,
                    Model = started.Model,
                    Success = false,
                    Error = failure is OperationCanceledException
                        ? "Model invocation was cancelled."
                        : "Model invocation failed.",
                },
            };
            ExceptionDispatchInfo.Capture(failure).Throw();
        }

        var result = roundScope.RequireResult();
        if (started is null)
            yield break;

        AgentTurnToolCatalogTelemetry.RecordOutcome(
            request.ToolCatalogProof,
            result.FinishReason ?? "success");

        yield return new LLMStreamChunk
        {
            LLMInvocationCompleted = new LLMInvocationCompletedChunk
            {
                OperationId = operationId,
                Round = round,
                Model = started.Model,
                Content = result.Content ?? string.Empty,
                ReasoningContent = result.ReasoningContent ?? string.Empty,
                Usage = result.Usage,
                FinishReason = result.FinishReason ?? string.Empty,
                Success = true,
            },
        };
    }

    private async IAsyncEnumerable<LLMStreamChunk> StreamLlmRoundCoreAsync(
        ILLMProvider provider,
        LLMRequest request,
        StreamingRoundScope roundScope,
        string operationId,
        int round,
        [EnumeratorCancellation] CancellationToken ct,
        bool emitResolvedToolCallStarts = false,
        Action<LLMRequest>? onRequestAuthorized = null)
    {
        // Refactor (iter31/cluster-032-chatruntime-taskrun-business-loop):
        //   Old pattern: ChatRuntime.ChatStreamAsync 用 Task.Run + Channel<LLMStreamChunk>/ChannelWriter 在 actor turn 外跑 LLM/tool/hook/history 业务循环,违反 actor execution integrity
        //   New principle: ChatStreamAsync owns the stream flow directly; the Task.Run + Channel owned-stream loop and stream_buffer_capacity config were removed; middleware wrapping stays inside private bridge adapters.
        var authorizationFence = ChatRuntimeRequestBuilder.CaptureAuthorizationFence(request);
        var hasRequestExtensionPoint = _hooks is not null || _llmMiddlewares.Count > 0;
        var catalogBoundRequest = authorizationFence.Apply(request, forceCopy: hasRequestExtensionPoint);
        var llmHookContext = new AIGAgentExecutionHookContext { LLMRequest = catalogBoundRequest };
        if (_hooks != null) await _hooks.RunLLMRequestStartAsync(llmHookContext, ct);
        var llmStartedAt = Stopwatch.GetTimestamp();

        var llmCallContext = new LLMCallContext
        {
            Request = authorizationFence.Apply(catalogBoundRequest),
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
        IReadOnlyList<IAgentTool> authorizedTools = [];
        var authorizedToolManager = ToolCallLoop.CreateRequestToolManager(authorizedTools);
        var authorizedToolContext = AgentToolExecutionContext.Empty;
        var firstOutputRecorded = false;

        var llmBridge = new LLMCallMiddlewareBridge();
        var middlewareTask = MiddlewarePipeline.RunLLMCallAsync(
            _llmMiddlewares,
            llmCallContext,
            llmBridge.WaitForCoreCompletionAsync);

        var coreTurnTask = llmBridge.WaitForCoreTurnAsync(ct);
        var middlewareWaitTask = middlewareTask.WaitAsync(ct);
        var readyTask = await Task.WhenAny(coreTurnTask, middlewareWaitTask);
        await readyTask;

        if (readyTask == coreTurnTask && !llmCallContext.Terminate)
        {
            llmCallContext.Request = authorizationFence.Apply(llmCallContext.Request);
            authorizedTools = llmCallContext.Request.Tools?.ToArray() ?? [];
            authorizedToolManager = ToolCallLoop.CreateRequestToolManager(authorizedTools);
            authorizedToolContext = AgentToolExecutionContextMapper.FromRequest(llmCallContext.Request);
            onRequestAuthorized?.Invoke(llmCallContext.Request);
            yield return new LLMStreamChunk
            {
                LLMInvocationStarted = BuildModelInvocationStartedChunk(
                    operationId,
                    round,
                    provider,
                    llmCallContext.Request),
            };
            var full = new StringBuilder();
            var fullReasoning = new StringBuilder();
            TokenUsage? usage = null;
            string? finishReason = null;
            var completedToolCalls = new Queue<ToolCall>();
            var anonymousToolCallPrefix = authorizedToolContext.Request.CallId;
            var toolCalls = emitResolvedToolCallStarts
                ? new StreamingToolCallAccumulator(
                    toolCall => completedToolCalls.Enqueue(toolCall),
                    anonymousToolCallPrefix)
                : new StreamingToolCallAccumulator(anonymousToolCallPrefix);

            using var toolContextScope = AgentToolContextScope.Push(authorizedToolContext);
            await using var providerEnumerator = provider.ChatStreamAsync(llmCallContext.Request, ct)
                .GetAsyncEnumerator(ct);
            while (true)
            {
                LLMStreamChunk chunk;
                try
                {
                    if (!await providerEnumerator.MoveNextAsync())
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
                    normalizedChunk = NormalizeStreamChunk(
                        chunk,
                        toolCalls,
                        full,
                        fullReasoning,
                        ref usage,
                        ref finishReason,
                        emitToolCallDeltas: !emitResolvedToolCallStarts);
                }
                catch (Exception ex)
                {
                    llmBridge.FailCore(ex);
                    throw;
                }

                while (completedToolCalls.TryDequeue(out var completedToolCall))
                {
                    yield return BuildToolCallStartedChunk(completedToolCall, authorizedToolManager);
                }

                if (normalizedChunk != null)
                {
                    if (!firstOutputRecorded && IsModelOutputChunk(normalizedChunk))
                    {
                        AgentTurnToolCatalogTelemetry.RecordTimeToFirstOutput(
                            llmCallContext.Request.ToolCatalogProof,
                            Stopwatch.GetElapsedTime(llmStartedAt));
                        firstOutputRecorded = true;
                    }
                    yield return normalizedChunk;
                }
            }

            var finalizedToolCalls = toolCalls.BuildToolCalls();
            while (completedToolCalls.TryDequeue(out var completedToolCall))
            {
                yield return BuildToolCallStartedChunk(completedToolCall, authorizedToolManager);
            }

            streamedContent = full.Length > 0 ? full.ToString() : null;
            streamedReasoningContent = fullReasoning.Length > 0 ? fullReasoning.ToString() : null;
            streamedUsage = usage;
            streamedFinishReason = finishReason;
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
            await middlewareTask;
        }

        if (llmCallContext.Terminate)
        {
            var authorizedRequest = authorizationFence.Apply(llmCallContext.Request);
            authorizedTools = authorizedRequest.Tools?.ToArray() ?? [];
            authorizedToolContext = AgentToolExecutionContextMapper.FromRequest(authorizedRequest);
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
            response.Usage,
            authorizedTools,
            authorizedToolContext);
    }

    private static bool IsModelOutputChunk(LLMStreamChunk chunk) =>
        !string.IsNullOrEmpty(chunk.DeltaContent) ||
        chunk.DeltaContentPart is not null ||
        !string.IsNullOrEmpty(chunk.DeltaReasoningContent) ||
        chunk.DeltaToolCall is not null;

    internal async Task<StreamingRoundResult> ExecuteSingleLlmStepAsync(
        ILLMProvider provider,
        LLMRequest request,
        CancellationToken ct,
        Func<LLMStreamChunk, CancellationToken, Task>? onChunkAsync = null)
    {
        var roundScope = new StreamingRoundScope();
        await foreach (var _ in StreamLlmRoundAsync(
                           provider,
                           request,
                           roundScope,
                           ct,
                           round: 0,
                           emitResolvedToolCallStarts: true))
        {
            if (onChunkAsync is not null)
                await onChunkAsync(_, ct);
        }

        return roundScope.RequireResult();
    }

    internal async Task<IReadOnlyList<ToolExecutionResult>> ExecuteSingleToolStepAsync(
        IReadOnlyList<ToolCall> toolCalls,
        IReadOnlyList<IAgentTool>? tools,
        AgentToolExecutionContext? toolContext,
        CancellationToken ct,
        AgentToolApprovalGrant? approvalGrant = null)
    {
        var executor = new StreamingToolExecutor(
            ToolCallLoop.CreateRequestToolManager(tools),
            _hooks,
            toolContext: toolContext,
            toolExecutionPort: _toolLoop.ToolExecutionPort,
            checkpointPort: _toolCheckpointPort,
            approvalContinuationMode: _toolLoop.ApprovalContinuationMode,
            approvalGrant: approvalGrant,
            logger: _logger);
        using var toolState = executor.CreateExecutionState();
        var prepared = await executor.PrepareBatchAsync(
            toolContext?.Request.RequestId ?? string.Empty,
            round: 0,
            toolCalls,
            ct);
        foreach (var operation in prepared)
            executor.AddTool(toolState, operation);

        var results = new List<ToolExecutionResult>();
        await foreach (var result in executor.GetRemainingResultsAsync(toolState, ct))
            results.Add(result);

        return results;
    }

    private static ChatMessage? AppendAssistantMessage(
        List<ChatMessage> messages,
        List<ChatMessage> pendingHistoryMessages,
        string? content,
        string? reasoningContent,
        IReadOnlyList<ToolCall>? toolCalls)
    {
        if (string.IsNullOrEmpty(content) && string.IsNullOrEmpty(reasoningContent) && toolCalls is not { Count: > 0 })
            return null;

        var assistantMessage = new ChatMessage
        {
            Role = "assistant",
            Content = content,
            ReasoningContent = reasoningContent,
            ToolCalls = toolCalls?.Select(CloneToolCall).ToArray(),
        };
        messages.Add(assistantMessage);
        pendingHistoryMessages.Add(assistantMessage);
        return assistantMessage;
    }

    private static ToolCall CloneToolCall(ToolCall toolCall) => new()
    {
        Id = toolCall.Id,
        Name = toolCall.Name,
        ArgumentsJson = toolCall.ArgumentsJson,
    };

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
        messages.AddRange(ChatMessageToolCallTranscript.WithoutInvalidToolCallPairs(_history.Messages));
        messages.Add(pendingUserMessage);
        return messages;
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

    private static string BuildModelInvocationOperationId(string? requestId, int round)
    {
        var normalizedRequestId = string.IsNullOrWhiteSpace(requestId)
            ? "anonymous"
            : requestId.Trim();
        return $"{normalizedRequestId}:model:{round}:{Guid.NewGuid():N}";
    }

    private static string ResolveModelIdentity(LLMRequest request) =>
        request.LlmControl?.ModelOverride?.Trim()
        ?? request.RoutingContext?.ModelOverride?.Trim()
        ?? request.Model?.Trim()
        ?? string.Empty;

    private static LLMInvocationStartedChunk BuildModelInvocationStartedChunk(
        string operationId,
        int round,
        ILLMProvider provider,
        LLMRequest authorizedRequest) =>
        new()
        {
            OperationId = operationId,
            Round = round,
            Model = ResolveModelIdentity(authorizedRequest),
            Provider = provider.Name?.Trim() ?? string.Empty,
            InputSummary = BuildSafeModelInputSummary(authorizedRequest),
            AvailableToolNames = (authorizedRequest.Tools ?? [])
                .Select(static tool => tool.Name?.Trim() ?? string.Empty)
                .Where(static name => name.Length > 0)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(static name => name, StringComparer.Ordinal)
                .ToArray(),
        };

    private static string BuildSafeModelInputSummary(LLMRequest request)
    {
        var lastUserMessage = request.Messages.LastOrDefault(static message =>
            string.Equals(message.Role, "user", StringComparison.OrdinalIgnoreCase));
        if (lastUserMessage is null)
            return string.Empty;

        var parts = new List<string>();
        if (lastUserMessage.ContentParts is { Count: > 0 })
        {
            parts.AddRange(lastUserMessage.ContentParts.Select(static part => part.Kind switch
            {
                ContentPartKind.Text when !string.IsNullOrWhiteSpace(part.Text) => part.Text.Trim(),
                ContentPartKind.Image => "[image]",
                ContentPartKind.Audio => "[audio]",
                ContentPartKind.Video => "[video]",
                _ => string.Empty,
            }).Where(static value => value.Length > 0));
        }
        if (parts.Count == 0 && !string.IsNullOrWhiteSpace(lastUserMessage.Content))
            parts.Add(lastUserMessage.Content.Trim());

        if (parts.Count == 0)
            return string.Empty;

        var normalized = string.Join(' ', string.Join("\n", parts)
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        var scrubbed = SecretScrubber.ScrubJson(normalized);
        return scrubbed.Length <= ModelInputSummaryMaxLength
            ? scrubbed
            : scrubbed[..ModelInputSummaryMaxLength] + "...";
    }

    private static LLMStreamChunk? NormalizeStreamChunk(
        LLMStreamChunk chunk,
        StreamingToolCallAccumulator toolCalls,
        StringBuilder fullContent,
        StringBuilder fullReasoningContent,
        ref TokenUsage? usage,
        ref string? finishReason,
        bool emitToolCallDeltas = true)
    {
        ToolCall? normalizedToolCall = null;
        if (chunk.DeltaToolCall != null)
            normalizedToolCall = toolCalls.TrackDelta(chunk.DeltaToolCall);
        var emittedToolCall = emitToolCallDeltas ? normalizedToolCall : null;

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
            emittedToolCall == null &&
            !chunk.IsLast &&
            chunk.Usage == null &&
            chunk.ToolReceipt == null)
        {
            return null;
        }

        return new LLMStreamChunk
        {
            DeltaContent = chunk.DeltaContent,
            DeltaContentPart = chunk.DeltaContentPart,
            DeltaReasoningContent = chunk.DeltaReasoningContent,
            DeltaToolCall = emittedToolCall,
            Usage = chunk.Usage,
            IsLast = chunk.IsLast,
            ToolReceipt = chunk.ToolReceipt?.Clone(),
            // Field-level patch (ADR-0021 §6 / canon §8): forward FinishReason so
            // the actor-edge closeout in ConversationReplyGenerator can observe
            // it. ChatRuntime itself remains transitional per aevatar#596 Phase A;
            // the cross-round aggregation and stream-local terminal contract live
            // at the actor edge, not here.
            FinishReason = chunk.FinishReason,
        };
    }

    private static bool IsVisibleOutputChunk(LLMStreamChunk chunk) =>
        !string.IsNullOrEmpty(chunk.DeltaContent) ||
        !string.IsNullOrEmpty(chunk.DeltaReasoningContent) ||
        chunk.DeltaContentPart != null;

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
        TokenUsage? Usage,
        IReadOnlyList<IAgentTool> AuthorizedTools,
        AgentToolExecutionContext AuthorizedToolContext);

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
