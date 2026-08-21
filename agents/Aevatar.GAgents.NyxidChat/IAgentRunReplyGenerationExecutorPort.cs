using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.Core.AgentProfiles;
using Aevatar.Foundation.Abstractions.Tools;
using Aevatar.GAgents.Channel.Runtime;

namespace Aevatar.GAgents.NyxidChat;

public interface IAgentRunReplyGenerationExecutorPort
{
    Task<AgentRunReplyStepState> BuildInitialStepStateAsync(AgentRunReplyGenerationExecutionRequest request, CancellationToken ct);

    Task<AgentRunLlmStepExecution> BuildLlmStepExecutionAsync(
        AgentRunReplyStepExecutionRequest request,
        CancellationToken ct);

    Task<AgentRunNextToolStepRequestedEvent> BuildToolStepContinuationAsync(
        AgentRunReplyStepExecutionRequest request,
        AgentRunAuthorizedToolStep? authorizedToolStep,
        CancellationToken ct);

    Task<AgentRunNextToolStepRequestedEvent> BuildApprovedToolStepContinuationAsync(
        AgentRunReplyStepExecutionRequest request,
        AgentRunPendingToolApprovalState pendingApproval,
        CancellationToken ct) =>
        Task.FromException<AgentRunNextToolStepRequestedEvent>(
            new NotSupportedException("Approved AgentRun tool continuation is not supported by this executor."));
}

public sealed record AgentRunLlmStepExecution(
    AgentRunNextLlmStepRequestedEvent Continuation,
    AgentRunAuthorizedToolStep? AuthorizedToolStep,
    IReadOnlyList<AgentRunAuthorizedToolCallSafety>? AuthorizedToolCallSafeties = null);

/// <summary>
/// Transient, provider-owned classification for one exact authorized call.
/// This snapshot stays beside the runtime capability and is never persisted as
/// actor state; NyxIdChat copies only its closed safe fields into its result.
/// </summary>
public sealed record AgentRunAuthorizedToolCallSafety(
    string CallId,
    string ToolName,
    string ArgumentsJson,
    AgentToolCallSafety CallSafety,
    string SideEffectKind,
    string ToolDefinitionFingerprint = "",
    ToolPresentationDescriptor? Presentation = null,
    bool RequiresApproval = false,
    AgentToolOperationAdmissionPayload? OperationAdmission = null);

public sealed class AgentRunAuthorizedToolStep
{
    private readonly AgentRunToolCall[] _toolCalls;
    private readonly AgentToolExecutionContext _toolContext;
    private readonly Func<AgentToolExecutionContext, AgentToolApprovalGrant?, CancellationToken,
        Task<AgentRunToolStepResult>> _executeAsync;
    private readonly AgentToolApprovalGrant? _approvalGrant;
    private readonly AgentToolCredentialsPayload? _refreshedCredentials;

    internal AgentRunAuthorizedToolStep(
        string runId,
        string correlationId,
        int attempt,
        int stepIndex,
        IReadOnlyList<AgentRunToolCall> toolCalls,
        Func<CancellationToken, Task<AgentRunToolStepResult>> executeAsync)
        : this(
            runId,
            correlationId,
            attempt,
            stepIndex,
            toolCalls,
            AgentToolExecutionContext.Empty,
            (_, _, token) => executeAsync(token),
            approvalGrant: null,
            refreshedCredentials: null)
    {
    }

    internal AgentRunAuthorizedToolStep(
        string runId,
        string correlationId,
        int attempt,
        int stepIndex,
        IReadOnlyList<AgentRunToolCall> toolCalls,
        AgentToolExecutionContext toolContext,
        Func<AgentToolExecutionContext, CancellationToken, Task<AgentRunToolStepResult>> executeAsync)
        : this(
            runId,
            correlationId,
            attempt,
            stepIndex,
            toolCalls,
            toolContext,
            (context, _, token) => executeAsync(context, token),
            approvalGrant: null,
            refreshedCredentials: null)
    {
    }

    internal AgentRunAuthorizedToolStep(
        string runId,
        string correlationId,
        int attempt,
        int stepIndex,
        IReadOnlyList<AgentRunToolCall> toolCalls,
        AgentToolExecutionContext toolContext,
        Func<AgentToolExecutionContext, AgentToolApprovalGrant?, CancellationToken,
            Task<AgentRunToolStepResult>> executeAsync)
        : this(
            runId,
            correlationId,
            attempt,
            stepIndex,
            toolCalls,
            toolContext,
            executeAsync,
            approvalGrant: null,
            refreshedCredentials: null)
    {
    }

    private AgentRunAuthorizedToolStep(
        string runId,
        string correlationId,
        int attempt,
        int stepIndex,
        IReadOnlyList<AgentRunToolCall> toolCalls,
        AgentToolExecutionContext toolContext,
        Func<AgentToolExecutionContext, AgentToolApprovalGrant?, CancellationToken,
            Task<AgentRunToolStepResult>> executeAsync,
        AgentToolApprovalGrant? approvalGrant,
        AgentToolCredentialsPayload? refreshedCredentials)
    {
        RunId = runId;
        CorrelationId = correlationId;
        Attempt = attempt;
        StepIndex = stepIndex;
        _toolCalls = toolCalls.Select(static call => call.Clone()).ToArray();
        _toolContext = toolContext ?? throw new ArgumentNullException(nameof(toolContext));
        _executeAsync = executeAsync ?? throw new ArgumentNullException(nameof(executeAsync));
        _approvalGrant = approvalGrant;
        _refreshedCredentials = refreshedCredentials?.Clone();
    }

    internal string RunId { get; }

    internal string CorrelationId { get; }

    internal int Attempt { get; }

    internal int StepIndex { get; }

    internal bool Matches(AgentRunReplyStepExecutionRequest request)
    {
        if (!string.Equals(RunId, request.RunId, StringComparison.Ordinal) ||
            !string.Equals(CorrelationId, request.Request.CorrelationId, StringComparison.Ordinal) ||
            Attempt != request.Attempt ||
            StepIndex != request.StepIndex ||
            _toolCalls.Length != request.StepState.PendingToolCalls.Count)
        {
            return false;
        }

        return _toolCalls.Zip(request.StepState.PendingToolCalls).All(static pair =>
            string.Equals(pair.First.Id, pair.Second.Id, StringComparison.Ordinal) &&
            string.Equals(pair.First.Name, pair.Second.Name, StringComparison.Ordinal) &&
            string.Equals(pair.First.ArgumentsJson, pair.Second.ArgumentsJson, StringComparison.Ordinal));
    }

    internal AgentRunAuthorizedToolStep WithChatOperation(
        NyxIdChatOperationKey key,
        string? idempotencyKey,
        AgentToolOperationAdmissionPayload? operationAdmission)
    {
        ArgumentNullException.ThrowIfNull(key);
        var restoredAdmission = operationAdmission is null
            ? null
            : AgentToolExecutionContextMapper.FromPayload(
                new AgentToolExecutionContextPayload
                {
                    OperationAdmission = operationAdmission.Clone(),
                }).OperationAdmission;
        if (operationAdmission is not null && restoredAdmission is null)
            throw new InvalidOperationException("The exact operation admission is invalid.");

        return new AgentRunAuthorizedToolStep(
            RunId,
            CorrelationId,
            Attempt,
            StepIndex,
            _toolCalls,
            _toolContext with
            {
                Request = _toolContext.Request with
                {
                    OperationId = Normalize(key.OperationId),
                    IdempotencyKey = Normalize(idempotencyKey),
                },
                OperationAdmission = restoredAdmission,
                Chat = _toolContext.Chat with
                {
                    TaskId = Normalize(key.TaskId),
                    StepId = Normalize(key.StepId),
                },
            },
            _executeAsync,
            _approvalGrant,
            _refreshedCredentials);
    }

    internal AgentRunAuthorizedToolStep WithApprovalGrant(
        string approvalRequestId,
        AgentToolCredentialsPayload? refreshedCredentials)
    {
        if (_toolCalls.Length != 1 ||
            string.IsNullOrWhiteSpace(approvalRequestId) ||
            string.IsNullOrWhiteSpace(_toolContext.Request.RequestId))
        {
            throw new InvalidOperationException("The exact approved tool call is unavailable.");
        }

        var call = _toolCalls[0];
        var grant = new AgentToolApprovalGrant(
            _toolContext.ExecutionOwner.Clone(),
            approvalRequestId.Trim(),
            _toolContext.Request.RequestId!,
            call.Name,
            call.Id,
            AgentToolArgumentsDigest.ComputeSha256(call.ArgumentsJson));
        return new AgentRunAuthorizedToolStep(
            RunId,
            CorrelationId,
            Attempt,
            StepIndex,
            _toolCalls,
            _toolContext,
            _executeAsync,
            grant,
            refreshedCredentials);
    }

    internal AgentRunAuthorizedToolStep WithRefreshedCredentials(
        AgentToolCredentialsPayload refreshedCredentials)
    {
        ArgumentNullException.ThrowIfNull(refreshedCredentials);
        return new AgentRunAuthorizedToolStep(
            RunId,
            CorrelationId,
            Attempt,
            StepIndex,
            _toolCalls,
            _toolContext,
            _executeAsync,
            _approvalGrant,
            refreshedCredentials);
    }

    internal Task<AgentRunToolStepResult> ExecuteAsync(CancellationToken ct)
    {
        var context = _toolContext;
        if (_refreshedCredentials is not null)
        {
            var refreshed = AgentToolExecutionContextMapper.FromPayload(
                new AgentToolExecutionContextPayload
                {
                    Credentials = _refreshedCredentials.Clone(),
                });
            context = context with { Credentials = refreshed.Credentials };
        }
        return _executeAsync(context, _approvalGrant, ct);
    }

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

public sealed record AgentRunReplyGenerationExecutionRequest(
    string RunId,
    string RunActorId,
    int Attempt,
    NeedsLlmReplyEvent Request,
    AgentTurnToolCatalog? TurnCatalog = null);

public sealed record AgentRunReplyStepExecutionRequest(
    string RunId,
    string RunActorId,
    int Attempt,
    int StepIndex,
    NeedsLlmReplyEvent Request,
    AgentRunReplyStepState StepState,
    Func<LLMStreamChunk, CancellationToken, Task>? ReportChunkAsync = null,
    AgentTurnToolCatalog? TurnCatalog = null,
    bool AllowDurableToolAuthorization = false,
    bool? AllowMultipleToolCalls = null);
