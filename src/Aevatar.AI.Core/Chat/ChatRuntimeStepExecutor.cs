using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.Middleware;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.Core.AgentProfiles;
using Aevatar.AI.Core.Hooks;
using Aevatar.AI.Core.Tools;

namespace Aevatar.AI.Core.Chat;

public sealed class ChatRuntimeStepExecutor
{
    private readonly Func<ILLMProvider> _providerFactory;
    private readonly ToolCallLoop _toolLoop;
    private readonly AgentHookPipeline? _hooks;
    private readonly Func<AgentProfileTurnCatalog?, LLMRequest> _requestBuilder;
    private readonly IReadOnlyList<ILLMCallMiddleware> _llmMiddlewares;
    private readonly TokenBudgetTracker _budgetTracker;
    private readonly IChatToolCheckpointPort _toolCheckpointPort;
    private readonly AgentProfileTurnCatalog? _turnCatalog;

    internal ChatRuntimeStepExecutor(
        Func<ILLMProvider> providerFactory,
        ToolCallLoop toolLoop,
        AgentHookPipeline? hooks,
        Func<AgentProfileTurnCatalog?, LLMRequest> requestBuilder,
        IReadOnlyList<ILLMCallMiddleware> llmMiddlewares,
        TokenBudgetTracker budgetTracker,
        IChatToolCheckpointPort toolCheckpointPort,
        AgentProfileTurnCatalog? turnCatalog)
    {
        _providerFactory = providerFactory;
        _toolLoop = toolLoop;
        _hooks = hooks;
        _requestBuilder = requestBuilder;
        _llmMiddlewares = llmMiddlewares;
        _budgetTracker = budgetTracker;
        _toolCheckpointPort = toolCheckpointPort;
        _turnCatalog = turnCatalog;
    }

    public LLMRequest BuildBaseRequest(
        string? requestId,
        IReadOnlyDictionary<string, string>? metadata,
        AgentToolExecutionContext? toolContext,
        LLMControlContext? llmControl) =>
        ChatRuntimeRequestBuilder.Build(
            _requestBuilder(_turnCatalog),
            requestId,
            metadata,
            toolContext,
            llmControl,
            _turnCatalog);

    public LLMRequest BuildLlmStepRequest(
        IReadOnlyList<ChatMessage> messages,
        string? requestId,
        IReadOnlyDictionary<string, string>? metadata,
        AgentToolExecutionContext? toolContext,
        LLMControlContext? llmControl,
        int round,
        bool finalNoTools,
        IReadOnlyList<AgentToolReceipt>? toolReceipts = null)
    {
        var baseRequest = BuildBaseRequest(requestId, metadata, toolContext, llmControl);
        return new LLMRequest
        {
            Messages = BuildStepMessages(messages, finalNoTools, toolReceipts),
            RequestId = baseRequest.RequestId,
            Metadata = AgentToolExecutionContextMapper.StripOwnedControlKeys(baseRequest.Metadata),
            CallerContext = baseRequest.CallerContext,
            ToolContext = AgentToolExecutionContextMapper.FromRequestWithCallId(
                baseRequest,
                finalNoTools
                    ? ToolCallLoop.ComposeFinalCallId(baseRequest.RequestId)
                    : ToolCallLoop.ComposeRoundCallId(baseRequest.RequestId, round)),
            RoutingContext = baseRequest.RoutingContext,
            LlmControl = baseRequest.LlmControl,
            Tools = finalNoTools ? null : baseRequest.Tools,
            Model = baseRequest.Model,
            Temperature = baseRequest.Temperature,
            MaxTokens = baseRequest.MaxTokens,
            ResponseFormat = baseRequest.ResponseFormat,
        };
    }

    public ILLMProvider ResolveProvider() => _providerFactory();

    public Task<ChatRuntimeStepLlmResult> ExecuteLlmStepAsync(
        ILLMProvider provider,
        LLMRequest request,
        Func<LLMStreamChunk, CancellationToken, Task>? onChunkAsync,
        CancellationToken ct)
    {
        var catalogBoundRequest = _turnCatalog is null
            ? request
            : ChatRuntimeRequestBuilder.Build(
                request,
                request.RequestId,
                request.Metadata,
                request.ToolContext,
                request.LlmControl,
                _turnCatalog);
        var runtime = new ChatRuntime(
            () => provider,
            new ChatHistory(),
            _toolLoop,
            _hooks,
            _ => catalogBoundRequest,
            llmMiddlewares: _llmMiddlewares,
            toolCheckpointPort: _toolCheckpointPort);
        return ExecuteAsync(runtime, provider, catalogBoundRequest, onChunkAsync, ct);

        static async Task<ChatRuntimeStepLlmResult> ExecuteAsync(
            ChatRuntime runtime,
            ILLMProvider provider,
            LLMRequest request,
            Func<LLMStreamChunk, CancellationToken, Task>? onChunkAsync,
            CancellationToken ct)
        {
            var result = await runtime.ExecuteSingleLlmStepAsync(provider, request, ct, onChunkAsync)
                .ConfigureAwait(false);
            return new ChatRuntimeStepLlmResult(
                result.Content,
                result.ReasoningContent,
                result.ToolCalls,
                result.Terminated,
                result.FinishReason,
                result.Usage,
                result.AuthorizedTools,
                result.AuthorizedToolContext);
        }
    }

    public async Task<IReadOnlyList<ToolExecutionResult>> ExecuteAuthorizedToolStepAsync(
        IReadOnlyList<ToolCall> toolCalls,
        IReadOnlyList<IAgentTool> authorizedTools,
        AgentToolExecutionContext authorizedToolContext,
        CancellationToken ct,
        AgentToolApprovalGrant? approvalGrant = null)
    {
        var runtime = new ChatRuntime(
            _providerFactory,
            new ChatHistory(),
            _toolLoop,
            _hooks,
            _requestBuilder,
            llmMiddlewares: _llmMiddlewares,
            toolCheckpointPort: _toolCheckpointPort);
        return await runtime.ExecuteSingleToolStepAsync(
                toolCalls,
                authorizedTools,
                authorizedToolContext,
                ct,
                approvalGrant)
            .ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ToolExecutionResult>> ExecuteToolStepAsync(
        IReadOnlyList<ToolCall> toolCalls,
        IReadOnlyDictionary<string, string>? requestMetadata,
        AgentToolExecutionContext? toolContext,
        CancellationToken ct)
    {
        var baseRequest = BuildBaseRequest(
            requestId: null,
            metadata: requestMetadata,
            toolContext: toolContext,
            llmControl: null);
        var runtime = new ChatRuntime(
            _providerFactory,
            new ChatHistory(),
            _toolLoop,
            _hooks,
            _requestBuilder,
            llmMiddlewares: _llmMiddlewares,
            toolCheckpointPort: _toolCheckpointPort);
        var executionToolContext = _turnCatalog is null
            ? toolContext
            : baseRequest.ToolContext;
        // Refactor (issue1574): Old pattern: core tool step accepted Metadata as a fallback control source.
        // New principle: metadata is retained for outer legacy planning only; core tool execution uses typed context.
        return await runtime.ExecuteSingleToolStepAsync(toolCalls, baseRequest.Tools, executionToolContext, ct)
            .ConfigureAwait(false);
    }

    public void RecordUsage(TokenUsage? usage) => _budgetTracker.RecordUsage(usage);

    private static List<ChatMessage> BuildStepMessages(
        IReadOnlyList<ChatMessage> messages,
        bool finalNoTools,
        IReadOnlyList<AgentToolReceipt>? toolReceipts)
    {
        if (!finalNoTools)
            return [..messages];

        var constraints = ToolOutcomeReplyConstraintBuilder.BuildFinalNoToolsConstraints(toolOutcomes: null, toolReceipts);
        if (constraints.Count == 0)
            return [..messages];

        return [..messages, ..constraints];
    }
}

public sealed record ChatRuntimeStepLlmResult(
    string? Content,
    string? ReasoningContent,
    IReadOnlyList<ToolCall>? ToolCalls,
    bool Terminated,
    string? FinishReason,
    TokenUsage? Usage,
    IReadOnlyList<IAgentTool> AuthorizedTools,
    AgentToolExecutionContext AuthorizedToolContext);
