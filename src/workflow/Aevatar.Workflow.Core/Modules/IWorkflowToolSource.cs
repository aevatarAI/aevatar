using Aevatar.Workflow.Abstractions;

namespace Aevatar.Workflow.Core.Modules;

public interface IWorkflowTool
{
    string Name { get; }

    Task<WorkflowToolExecutionResult> ExecuteAsync(WorkflowToolExecutionRequest request, CancellationToken ct = default);
}

public sealed record WorkflowToolExecutionResult(
    string ResultJson,
    WorkflowManagedHandoffOutcome? ManagedHandoff = null,
    WorkflowToolApprovalPendingOutcome? PendingApproval = null,
    WorkflowToolExecutionFailure? Failure = null)
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
        bool retryable = false) =>
        new(
            resultJson ?? string.Empty,
            Failure: new WorkflowToolExecutionFailure(
                errorCode ?? string.Empty,
                errorMessage ?? string.Empty,
                terminalInvoked,
                retryable));
}

public sealed record WorkflowToolExecutionFailure(
    string ErrorCode,
    string ErrorMessage,
    bool TerminalInvoked = false,
    bool Retryable = false);

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
        long IssuedAtUnixMs = 0)
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
