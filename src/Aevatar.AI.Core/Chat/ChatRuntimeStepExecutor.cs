using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.Middleware;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.Core.AgentProfiles;
using Aevatar.AI.Core.Hooks;
using Aevatar.AI.Core.Middleware;
using Aevatar.AI.Core.Tools;

namespace Aevatar.AI.Core.Chat;

public sealed class ChatRuntimeStepExecutor
{
    private readonly Func<ILLMProvider> _providerFactory;
    private readonly ToolCallLoop _toolLoop;
    private readonly AgentHookPipeline? _hooks;
    private readonly Func<AgentTurnToolCatalog?, LLMRequest> _requestBuilder;
    private readonly IReadOnlyList<ILLMCallMiddleware> _llmMiddlewares;
    private readonly TokenBudgetTracker _budgetTracker;
    private readonly IChatToolCheckpointPort _toolCheckpointPort;
    private readonly AgentTurnToolCatalog? _turnCatalog;

    internal ChatRuntimeStepExecutor(
        Func<ILLMProvider> providerFactory,
        ToolCallLoop toolLoop,
        AgentHookPipeline? hooks,
        Func<AgentTurnToolCatalog?, LLMRequest> requestBuilder,
        IReadOnlyList<ILLMCallMiddleware> llmMiddlewares,
        TokenBudgetTracker budgetTracker,
        IChatToolCheckpointPort toolCheckpointPort,
        AgentTurnToolCatalog? turnCatalog)
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
        IReadOnlyList<AgentToolReceipt>? toolReceipts = null,
        bool? allowMultipleToolCalls = null)
    {
        var baseRequest = BuildBaseRequest(requestId, metadata, toolContext, llmControl);
        return new LLMRequest
        {
            Messages = BuildStepMessages(messages, round, finalNoTools, toolReceipts),
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
            RouteTarget = baseRequest.RouteTarget?.Clone(),
            Tools = finalNoTools ? null : baseRequest.Tools,
            ToolCatalogProof = finalNoTools
                ? AgentTurnToolCatalogProof.RestrictedEmpty(baseRequest.ToolCatalogProof?.Budget)
                : baseRequest.ToolCatalogProof,
            Model = baseRequest.Model,
            Temperature = baseRequest.Temperature,
            MaxTokens = baseRequest.MaxTokens,
            AllowMultipleToolCalls = allowMultipleToolCalls ?? baseRequest.AllowMultipleToolCalls,
            ResponseFormat = baseRequest.ResponseFormat,
        };
    }

    public ILLMProvider ResolveProvider() => _providerFactory();

    public async Task<ChatRuntimeStepRecoveryToolCall?> TryPlanSkillRecoveryToolCallAsync(
        LLMRequest request,
        IReadOnlyList<ChatMessage> recoveryMessages,
        string? finalContent,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(recoveryMessages);
        if (!TryPlanSkillRecoveryToolCall(request, recoveryMessages, finalContent, out _))
        {
            return null;
        }

        var authorized = await TryAuthorizePlannedToolCallAsync(
                request,
                authorizedRequest => TryPlanSkillRecoveryToolCall(
                    authorizedRequest,
                    recoveryMessages,
                    finalContent,
                    out var plannedToolCall)
                        ? plannedToolCall
                        : null,
                ct)
            .ConfigureAwait(false);
        return authorized;
    }

    public Task<ChatRuntimeStepRecoveryToolCall?> TryAuthorizeRequiredToolCallAsync(
        LLMRequest request,
        AgentProfileRequiredToolInvocation invocation,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(invocation);
        var normalized = invocation.Normalize();
        if (string.IsNullOrWhiteSpace(normalized.ToolName) ||
            normalized.ArgumentsJson.Length > 8 * 1024)
        {
            return Task.FromResult<ChatRuntimeStepRecoveryToolCall?>(null);
        }

        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(normalized.ArgumentsJson);
            if (document.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object)
                return Task.FromResult<ChatRuntimeStepRecoveryToolCall?>(null);
        }
        catch (System.Text.Json.JsonException)
        {
            return Task.FromResult<ChatRuntimeStepRecoveryToolCall?>(null);
        }

        var callId = string.Concat(
            request.ToolContext?.Request.CallId ?? request.RequestId ?? "profile",
            ":required");
        return TryAuthorizePlannedToolCallAsync(
            request,
            _ => new ToolCall
            {
                Id = callId,
                Name = normalized.ToolName,
                ArgumentsJson = normalized.ArgumentsJson,
            },
            ct);
    }

    public async Task<ChatRuntimeStepRecoveryToolCall?> TryAuthorizePlannedToolCallAsync(
        LLMRequest request,
        Func<LLMRequest, ToolCall?> resolveToolCall,
        CancellationToken ct)
    {
        var authorizationFence = ChatRuntimeRequestBuilder.CaptureAuthorizationFence(request);
        var context = new LLMCallContext
        {
            Request = authorizationFence.Apply(request, forceCopy: _llmMiddlewares.Count > 0),
            Provider = ResolveProvider(),
            CancellationToken = ct,
            IsStreaming = true,
        };
        var reachedCore = false;
        await MiddlewarePipeline.RunLLMCallAsync(
                _llmMiddlewares,
                context,
                () =>
                {
                    reachedCore = true;
                    return Task.CompletedTask;
                })
            .ConfigureAwait(false);
        if (!reachedCore || context.Terminate)
            return null;

        var authorizedRequest = authorizationFence.Apply(context.Request);
        var toolCall = resolveToolCall(authorizedRequest);
        if (toolCall is null)
            return null;
        var authorizedTools = authorizedRequest.Tools?
            .Where(tool => string.Equals(tool.Name, toolCall.Name, StringComparison.Ordinal))
            .ToArray() ?? [];
        if (authorizedTools.Length != 1)
            return null;

        return new ChatRuntimeStepRecoveryToolCall(
            new ToolCall
            {
                Id = toolCall.Id,
                Name = toolCall.Name,
                ArgumentsJson = toolCall.ArgumentsJson,
            },
            authorizedTools,
            AgentToolExecutionContextMapper.FromRequest(authorizedRequest));
    }

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

    private static bool TryPlanSkillRecoveryToolCall(
        LLMRequest request,
        IReadOnlyList<ChatMessage> recoveryMessages,
        string? finalContent,
        out ToolCall toolCall)
    {
        toolCall = default!;
        var recovery = request.ToolContext?.SkillRecovery ?? AgentSkillRecoveryContext.Empty;
        var searchAttempts = recoveryMessages.Sum(message =>
            message.ToolCalls?.Count(call => string.Equals(
                call.Name,
                "ornn_search_skills",
                StringComparison.Ordinal)) ?? 0);
        if (!SkillRecoveryPlanner.TryPlanNextDirective(
                recovery,
                recoveryMessages,
                finalContent,
                searchAttempts,
                request.ToolContext?.Request.CallId ?? request.RequestId,
                primarySkillAttempted: SkillRecoveryPlanner.HasPrimarySkillAttempt(
                    recoveryMessages,
                    recovery.PrimarySkillName),
                out var directive) ||
            directive.ToolCall is null)
        {
            return false;
        }

        toolCall = directive.ToolCall;
        return true;
    }

    private static List<ChatMessage> BuildStepMessages(
        IReadOnlyList<ChatMessage> messages,
        int round,
        bool finalNoTools,
        IReadOnlyList<AgentToolReceipt>? toolReceipts)
    {
        var constraints = ToolOutcomeReplyConstraintBuilder.BuildMutationClaimConstraints(
            toolOutcomes: null,
            toolReceipts);
        return ToolOutcomeReplyConstraintBuilder.ApplyConstraints(
            messages,
            constraints,
            mergeIntoExistingSystem: round == 0 && !finalNoTools);
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

public sealed record ChatRuntimeStepRecoveryToolCall(
    ToolCall ToolCall,
    IReadOnlyList<IAgentTool> AuthorizedTools,
    AgentToolExecutionContext AuthorizedToolContext);
