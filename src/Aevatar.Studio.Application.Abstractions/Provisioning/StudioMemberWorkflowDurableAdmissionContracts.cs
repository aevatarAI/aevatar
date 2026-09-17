using Aevatar.GAgentService.Abstractions;

namespace Aevatar.Studio.Application.Provisioning;

public enum StudioMemberWorkflowDurableAdmissionStatus
{
    Unspecified = 0,
    AlreadyDurable = 1,
    RevisionAccepted = 2,
    RevisionReady = 3,
}

public sealed record StudioMemberWorkflowDurableAdmissionRequest(
    string ScopeId,
    string MemberId,
    WorkflowCapabilityAdmissionContext CapabilityAdmission);

public sealed record StudioMemberWorkflowDurableAdmissionResult(
    StudioMemberWorkflowDurableAdmissionStatus Status,
    string ScopeId,
    string TeamId,
    string MemberId,
    string WorkflowId,
    string PublishedServiceId,
    string ServingRevisionId,
    string TargetRevisionId,
    string BindingOperation,
    string BindingStatus)
{
    public bool ReadyForSchedule =>
        Status is StudioMemberWorkflowDurableAdmissionStatus.AlreadyDurable or
            StudioMemberWorkflowDurableAdmissionStatus.RevisionReady;
}

public interface IStudioMemberWorkflowDurableAdmissionPort
{
    Task<StudioMemberWorkflowDurableAdmissionResult> AdmitAsync(
        StudioMemberWorkflowDurableAdmissionRequest request,
        CancellationToken ct = default);
}
