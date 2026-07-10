using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.Middleware;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.Core.Hooks;
using Aevatar.AI.Core.Tools;

namespace Aevatar.AI.Core.Chat;

public sealed class ChatRuntimeStepExecutor
{
    private readonly Func<ILLMProvider> _providerFactory;
    private readonly ToolCallLoop _toolLoop;
    private readonly AgentHookPipeline? _hooks;
    private readonly Func<LLMRequest> _requestBuilder;
    private readonly IReadOnlyList<ILLMCallMiddleware> _llmMiddlewares;
    private readonly TokenBudgetTracker _budgetTracker;

    internal ChatRuntimeStepExecutor(
        Func<ILLMProvider> providerFactory,
        ToolCallLoop toolLoop,
        AgentHookPipeline? hooks,
        Func<LLMRequest> requestBuilder,
        IReadOnlyList<ILLMCallMiddleware> llmMiddlewares,
        TokenBudgetTracker budgetTracker)
    {
        _providerFactory = providerFactory;
        _toolLoop = toolLoop;
        _hooks = hooks;
        _requestBuilder = requestBuilder;
        _llmMiddlewares = llmMiddlewares;
        _budgetTracker = budgetTracker;
    }

    public LLMRequest BuildBaseRequest(
        string? requestId,
        IReadOnlyDictionary<string, string>? metadata,
        AgentToolExecutionContext? toolContext,
        LLMControlContext? llmControl) =>
        ApplyRequestIdentity(_requestBuilder(), requestId, metadata, toolContext, llmControl);

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
        var runtime = new ChatRuntime(
            () => provider,
            new ChatHistory(),
            _toolLoop,
            _hooks,
            () => request,
            llmMiddlewares: _llmMiddlewares);
        return ExecuteAsync(runtime, provider, request, onChunkAsync, ct);

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
                result.Usage);
        }
    }

    public async Task<IReadOnlyList<ToolExecutionResult>> ExecuteToolStepAsync(
        IReadOnlyList<ToolCall> toolCalls,
        IReadOnlyDictionary<string, string>? requestMetadata,
        AgentToolExecutionContext? toolContext,
        CancellationToken ct)
    {
        var runtime = new ChatRuntime(
            _providerFactory,
            new ChatHistory(),
            _toolLoop,
            _hooks,
            _requestBuilder,
            llmMiddlewares: _llmMiddlewares);
        // Refactor (issue1574): Old pattern: core tool step accepted Metadata as a fallback control source.
        // New principle: metadata is retained for outer legacy planning only; core tool execution uses typed context.
        return await runtime.ExecuteSingleToolStepAsync(toolCalls, toolContext, ct)
            .ConfigureAwait(false);
    }

    public void RecordUsage(TokenUsage? usage) => _budgetTracker.RecordUsage(usage);

    private static LLMRequest ApplyRequestIdentity(
        LLMRequest baseRequest,
        string? requestId,
        IReadOnlyDictionary<string, string>? metadata,
        AgentToolExecutionContext? toolContext,
        LLMControlContext? llmControl)
    {
        var effectiveLlmControl = llmControl ?? baseRequest.LlmControl;
        var effectiveToolContext = toolContext ?? baseRequest.ToolContext ?? AgentToolExecutionContextMapper.FromRequest(baseRequest);
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
            Tools = baseRequest.Tools,
            Model = baseRequest.Model,
            Temperature = baseRequest.Temperature,
            MaxTokens = baseRequest.MaxTokens,
            ResponseFormat = baseRequest.ResponseFormat,
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
    TokenUsage? Usage);
