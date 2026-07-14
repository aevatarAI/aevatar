namespace Aevatar.Studio.Application.Provisioning;

public interface IStudioMemberWorkflowSchedulePort
{
    Task<StudioMemberWorkflowAuthorizationResult> PreflightAsync(
        StudioMemberWorkflowScheduleRequest request,
        CancellationToken ct = default);

    Task<StudioMemberWorkflowScheduleResult> CreateAsync(
        StudioMemberWorkflowScheduleRequest request,
        string confirmedPermissionDigest,
        CancellationToken ct = default);

    Task<StudioMemberWorkflowScheduleResult> ReauthorizeAsync(
        StudioMemberWorkflowScheduleRequest request,
        string confirmedPermissionDigest,
        CancellationToken ct = default);
}
