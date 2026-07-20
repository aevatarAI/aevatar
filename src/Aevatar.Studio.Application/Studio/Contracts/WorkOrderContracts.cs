namespace Aevatar.Studio.Application.Studio.Contracts;

public static class WorkOrderLifecycleStatusNames
{
    public const string Accepted = "accepted";
    public const string WaitingApproval = "waiting_approval";
    public const string Ready = "ready";
    public const string DispatchPending = "dispatch_pending";
    public const string Running = "running";
    public const string Completed = "completed";
    public const string Failed = "failed";
    public const string Stopped = "stopped";
    public const string Denied = "denied";
    public const string Cancelled = "cancelled";
    public const string TimedOut = "timed_out";
}

public static class WorkOrderApprovalStatusNames
{
    public const string NotRequired = "not_required";
    public const string Pending = "pending";
    public const string Approved = "approved";
    public const string Denied = "denied";
}

public static class WorkOrderCommandStageNames
{
    public const string DispatchAccepted = "dispatch_accepted";
}

public sealed record WorkOrderPrincipalContract(string PrincipalId, string PrincipalKind);

public sealed record WorkOrderArtifactReferenceContract(
    string ArtifactId,
    string ArtifactKind,
    string? Uri = null,
    string? RevisionId = null);

public sealed record WorkOrderExternalActionReferenceContract(
    string ActionId,
    string System,
    string Action,
    string ResourceId);

public sealed record WorkOrderPermissionRequirementContract(
    string PermissionId,
    string ActionId,
    string Capability,
    bool RequiresApproval = false);

public sealed record WorkOrderPermissionPlanContract(
    IReadOnlyList<WorkOrderExternalActionReferenceContract>? ExternalActions = null,
    IReadOnlyList<WorkOrderPermissionRequirementContract>? Requirements = null,
    IReadOnlyList<string>? ApproverPrincipalIds = null);

public sealed record WorkOrderChatInputContract(string Prompt);

public sealed record WorkOrderServiceInputContract(
    WorkOrderChatInputContract Chat,
    IReadOnlyList<WorkOrderArtifactReferenceContract>? InputArtifacts = null,
    IReadOnlyList<WorkOrderArtifactReferenceContract>? DeclaredResultArtifacts = null);

public sealed record CreateWorkOrderRequest(
    string TeamId,
    string MemberId,
    string PublishedServiceId,
    string EndpointId,
    string Intent,
    string DedupKey,
    WorkOrderServiceInputContract Input,
    WorkOrderPermissionPlanContract? PermissionPlan = null,
    DateTimeOffset? TimeoutAtUtc = null);

public sealed record ReassignWorkOrderRequest(
    string MemberId,
    string PublishedServiceId,
    long ExpectedLifecycleVersion);

public sealed record DecideWorkOrderApprovalRequest(
    string DecisionId,
    long ExpectedLifecycleVersion,
    string? Reason = null);

public sealed record DispatchWorkOrderRequest(long ExpectedLifecycleVersion);

public sealed record CancelWorkOrderRequest(
    long ExpectedLifecycleVersion,
    string? Reason = null);

public sealed record WorkOrderAcceptedReceipt(
    string WorkOrderId,
    string CommandId,
    string CorrelationId,
    string Stage,
    DateTimeOffset? AcceptedAtUtc = null);

public sealed record WorkOrderApprovalResponse(
    string? ApprovalId,
    string Status,
    string? DecisionId,
    WorkOrderPrincipalContract? DecidedBy,
    string? Reason,
    DateTimeOffset? DecidedAtUtc);

public sealed record WorkOrderExecutionResponse(
    string RunId,
    string RunActorId,
    string CommandId,
    string CorrelationId,
    string RevisionId,
    string DeploymentId,
    DateTimeOffset AcceptedAtUtc,
    DateTimeOffset? StartedAtUtc);

public sealed record WorkOrderFailureResponse(
    string Code,
    string Message,
    string Source,
    string? ReferenceId);

public sealed record WorkOrderTerminalEvidenceResponse(
    string DeliveryId,
    string RunId,
    string RunActorId,
    string CommandId,
    string CorrelationId,
    string Outcome,
    string? Output,
    string? Error,
    DateTimeOffset TerminalAtUtc,
    IReadOnlyList<WorkOrderArtifactReferenceContract> ResultArtifacts);

public sealed record WorkOrderCurrentStateResponse(
    string WorkOrderId,
    string ScopeId,
    string TeamId,
    WorkOrderPrincipalContract Requester,
    string MemberId,
    string PublishedServiceId,
    string? WorkflowId,
    string ServiceRevisionId,
    string ImplementationKind,
    string EndpointId,
    string Intent,
    string DedupKey,
    string LifecycleStatus,
    long LifecycleVersion,
    long StateVersion,
    WorkOrderServiceInputContract Input,
    WorkOrderPermissionPlanContract PermissionPlan,
    WorkOrderApprovalResponse Approval,
    WorkOrderExecutionResponse? Execution,
    WorkOrderTerminalEvidenceResponse? TerminalEvidence,
    WorkOrderTerminalEvidenceResponse? LateTerminalEvidence,
    WorkOrderFailureResponse? Failure,
    string? TerminalReason,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    DateTimeOffset? TimeoutAtUtc);

public sealed record WorkOrderListResponse(
    string ScopeId,
    IReadOnlyList<WorkOrderCurrentStateResponse> WorkOrders,
    string? NextPageToken = null);

public sealed record WorkOrderQueryRequest(
    int? PageSize = null,
    string? PageToken = null,
    string? Status = null,
    string? RequesterPrincipalId = null,
    string? TeamId = null,
    string? MemberId = null,
    string? PublishedServiceId = null,
    string? WorkflowId = null,
    string? RunId = null,
    DateTimeOffset? CreatedFromUtc = null,
    DateTimeOffset? CreatedToUtc = null);

public sealed record WorkOrderValidatedAssignment(
    string MemberId,
    string PublishedServiceId,
    string? WorkflowId,
    string ServiceRevisionId,
    string ImplementationKind);

public sealed class WorkOrderNotFoundException : InvalidOperationException
{
    public WorkOrderNotFoundException(string scopeId, string workOrderId)
        : base($"WorkOrder '{workOrderId}' was not found in scope '{scopeId}'.")
    {
        ScopeId = scopeId;
        WorkOrderId = workOrderId;
    }

    public string ScopeId { get; }

    public string WorkOrderId { get; }
}
