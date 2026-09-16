using Aevatar.Workflow.Abstractions;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Workflow.Core.Modules;

/// <summary>
/// Declares whether an uncertain actor recovery may redispatch the same logical tool call.
/// The default is fail-closed: tools must opt in to a recovery strategy explicitly.
/// </summary>
public enum WorkflowToolRecoverySafety
{
    Unspecified = 0,
    ReplayableReadOnly = 1,
    DurableStartOnceRedispatch = 2,
    EffectfulNonReplayable = 3,
}

public interface IWorkflowTool
{
    string Name { get; }

    WorkflowToolRecoverySafety RecoverySafety => WorkflowToolRecoverySafety.Unspecified;

    Task<WorkflowToolExecutionResult> ExecuteAsync(WorkflowToolExecutionRequest request, CancellationToken ct = default);
}

public interface IWorkflowDurableOperationTool : IWorkflowTool
{
    Task<WorkflowToolExecutionResult> ReconcileAsync(
        WorkflowToolExecutionRequest request,
        WorkflowToolPendingOperation pendingOperation,
        CancellationToken ct = default);

    Task<WorkflowToolCancellationResult> CancelAsync(
        WorkflowToolCancellationRequest request,
        CancellationToken ct = default) =>
        Task.FromResult(WorkflowToolCancellationResult.Failed(
            "tool_cancellation_not_supported",
            "This workflow tool does not support durable cancellation.",
            retryable: true));
}

public enum WorkflowToolOperationCancellationReason
{
    Unspecified = 0,
    WorkflowStopped = 1,
}

public sealed record WorkflowToolExecutionResult(
    string ResultJson,
    WorkflowManagedHandoffOutcome? ManagedHandoff = null,
    WorkflowToolApprovalPendingOutcome? PendingApproval = null,
    WorkflowToolExecutionFailure? Failure = null,
    WorkflowToolPendingOperation? PendingOperation = null,
    WorkflowToolCancellationTerminalAuditIntent? CancellationRecoveryIntent = null)
{
    public static WorkflowToolExecutionResult Success(
        string resultJson,
        WorkflowManagedHandoffOutcome? managedHandoff = null) =>
        new(resultJson ?? string.Empty, managedHandoff);

    public static WorkflowToolExecutionResult Failed(
        string resultJson,
        string errorCode,
        string errorMessage,
        bool terminalInvoked = false,
        bool retryable = false,
        WorkflowStepFailureOutcome failureOutcome = WorkflowStepFailureOutcome.CalleeConfirmed) =>
        new(
            resultJson ?? string.Empty,
            Failure: new WorkflowToolExecutionFailure(
                errorCode ?? string.Empty,
                errorMessage ?? string.Empty,
                terminalInvoked,
                retryable,
                failureOutcome));
}

public sealed record WorkflowToolPendingOperation(
    string OperationId,
    string ProviderOperationId,
    string StatusPath,
    string ResultPath,
    string CancelPath,
    WorkflowToolPendingOperationStatus Status,
    string? ETag,
    long RetryAfterMilliseconds,
    long ExpiresAtUnixMs,
    string ServiceSlug,
    string? UserServiceId,
    WorkflowToolPendingOperationRouteIdentitySource RouteIdentitySource);

public sealed record WorkflowToolCancellationRequest(
    WorkflowToolExecutionRequest ExecutionRequest,
    WorkflowToolPendingOperation PendingOperation,
    long DeadlineUnixMs,
    WorkflowToolCancellationTerminalAuditIntent? TerminalIntent = null,
    WorkflowToolOperationCancellationReason Reason = WorkflowToolOperationCancellationReason.WorkflowStopped);

public sealed record WorkflowToolCancellationTerminalAuditIntent(
    WorkflowToolExecutionResult Result,
    Any? ToolOwnedAuditIntent = null,
    string ArgumentsSha256 = "");

public enum WorkflowToolCancellationDisposition
{
    Completed = 1,
    Pending = 2,
    Failed = 3,
}

public sealed record WorkflowToolCancellationResult(
    WorkflowToolCancellationDisposition Disposition,
    WorkflowToolExecutionResult? CompletedResult = null,
    WorkflowToolPendingOperation? PendingOperation = null,
    WorkflowToolExecutionFailure? Failure = null,
    WorkflowToolCancellationTerminalAuditIntent? PendingTerminalIntent = null)
{
    public static WorkflowToolCancellationResult Completed(WorkflowToolExecutionResult result) =>
        new(WorkflowToolCancellationDisposition.Completed, CompletedResult: result);

    public static WorkflowToolCancellationResult Pending(
        WorkflowToolPendingOperation operation,
        WorkflowToolExecutionFailure? failure = null,
        WorkflowToolCancellationTerminalAuditIntent? terminalIntent = null) =>
        new(
            WorkflowToolCancellationDisposition.Pending,
            PendingOperation: operation,
            Failure: failure,
            PendingTerminalIntent: terminalIntent);

    public static WorkflowToolCancellationResult Failed(
        string errorCode,
        string errorMessage,
        bool retryable = false) =>
        new(
            WorkflowToolCancellationDisposition.Failed,
            Failure: new WorkflowToolExecutionFailure(
                errorCode ?? string.Empty,
                errorMessage ?? string.Empty,
                TerminalInvoked: false,
                Retryable: retryable));
}

public sealed record WorkflowToolExecutionFailure(
    string ErrorCode,
    string ErrorMessage,
    bool TerminalInvoked = false,
    bool Retryable = false,
    WorkflowStepFailureOutcome FailureOutcome = WorkflowStepFailureOutcome.CalleeConfirmed);

public sealed record WorkflowToolApprovalPendingOutcome(
    string ApprovalRequestId,
    string ToolName,
    string ToolCallId,
    string ArgumentsJson,
    string ApprovalMode,
    bool IsReadOnly,
    bool IsDestructive);

public sealed record WorkflowToolExecutionRequest
{
    public WorkflowToolExecutionRequest(
        string ArgumentsJson,
        string RunId,
        string StepId,
        string ExecutionId,
        string CallId,
        string ScopeId,
        WorkflowCallerCredential CallerCredential)
        : this(
            ArgumentsJson,
            RunId,
            StepId,
            ExecutionId,
            CallId,
            ScopeId,
            CallerCredential,
            WorkflowToolRuntimeContext.Empty,
            null,
            null,
            string.Empty,
            string.Empty)
    {
    }

    public WorkflowToolExecutionRequest(
        string ArgumentsJson,
        string RunId,
        string StepId,
        string ExecutionId,
        string CallId,
        string ScopeId,
        WorkflowCallerCredential CallerCredential,
        IReadOnlyList<WorkflowFileRef>? InputFileRefs)
        : this(
            ArgumentsJson,
            RunId,
            StepId,
            ExecutionId,
            CallId,
            ScopeId,
            CallerCredential,
            WorkflowToolRuntimeContext.Empty,
            null,
            InputFileRefs,
            string.Empty,
            string.Empty)
    {
    }

    public WorkflowToolExecutionRequest(
        string ArgumentsJson,
        string RunId,
        string StepId,
        string ExecutionId,
        string CallId,
        string ScopeId,
        WorkflowCallerCredential CallerCredential,
        WorkflowToolRuntimeContext RuntimeContext,
        ToolApprovalGrant? ApprovalGrant = null,
        IReadOnlyList<WorkflowFileRef>? InputFileRefs = null,
        string IdempotencyKey = "",
        string ScheduleId = "",
        WorkflowCapabilityInvocationAdmission? InvocationAdmission = null,
        WorkflowLlmControlContext? LlmControl = null,
        long IssuedAtUnixMs = 0,
        WorkflowUnattendedInvocationPermit? UnattendedInvocationPermit = null)
    {
        this.ArgumentsJson = ArgumentsJson;
        this.RunId = RunId;
        this.StepId = StepId;
        this.ExecutionId = ExecutionId;
        this.CallId = CallId;
        this.ScopeId = ScopeId;
        this.CallerCredential = CallerCredential;
        this.RuntimeContext = RuntimeContext;
        this.ApprovalGrant = ApprovalGrant;
        this.InputFileRefs = CopyInputFileRefs(InputFileRefs);
        this.IdempotencyKey = IdempotencyKey ?? string.Empty;
        this.ScheduleId = ScheduleId ?? string.Empty;
        this.InvocationAdmission = InvocationAdmission?.Clone();
        this.LlmControl = LlmControl?.Clone();
        this.IssuedAtUnixMs = IssuedAtUnixMs;
        this.UnattendedInvocationPermit = UnattendedInvocationPermit?.Clone();
    }

    public string ArgumentsJson { get; init; }

    public string RunId { get; init; }

    public string StepId { get; init; }

    public string ExecutionId { get; init; }

    public string CallId { get; init; }

    public string ScopeId { get; init; }

    public WorkflowCallerCredential CallerCredential { get; init; }

    public WorkflowToolRuntimeContext RuntimeContext { get; init; }

    public ToolApprovalGrant? ApprovalGrant { get; init; }

    public IReadOnlyList<WorkflowFileRef> InputFileRefs { get; private init; }

    public string IdempotencyKey { get; init; }

    public string ScheduleId { get; init; }

    public long IssuedAtUnixMs { get; init; }

    /// <summary>
    /// Server-generated proof for exactly this call site, resolved from actor-owned Run state.
    /// Null when the compiled step is not an admitted external tool invocation.
    /// </summary>
    public WorkflowCapabilityInvocationAdmission? InvocationAdmission { get; init; }

    public WorkflowLlmControlContext? LlmControl { get; init; }

    public WorkflowUnattendedInvocationPermit? UnattendedInvocationPermit { get; init; }

    private static IReadOnlyList<WorkflowFileRef> CopyInputFileRefs(
        IReadOnlyList<WorkflowFileRef>? inputFileRefs) =>
        inputFileRefs == null || inputFileRefs.Count == 0
            ? []
            : inputFileRefs.Select(static fileRef => fileRef.Clone()).ToArray();
}

public sealed record ToolApprovalGrant(
    string ApprovalRequestId,
    string ToolName,
    string ToolCallId);

public sealed record WorkflowToolRuntimeContext(
    string ParentActorId,
    string ParentRunId,
    string ParentStepId,
    string RootRunId,
    int Depth)
{
    public static WorkflowToolRuntimeContext Empty { get; } = new(
        string.Empty,
        string.Empty,
        string.Empty,
        string.Empty,
        0);
}

public interface IWorkflowToolSource
{
    Task<IReadOnlyList<IWorkflowTool>> GetToolsAsync(CancellationToken ct = default);
}
