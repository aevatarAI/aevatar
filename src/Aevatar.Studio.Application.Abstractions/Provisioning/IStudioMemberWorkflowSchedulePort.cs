namespace Aevatar.Studio.Application.Provisioning;

public interface IStudioMemberWorkflowSchedulePort
{
    Task<StudioMemberWorkflowAuthorizationResult> PreflightAsync(
        StudioMemberWorkflowScheduleRequest request,
        CancellationToken ct = default) =>
        Task.FromResult(new StudioMemberWorkflowAuthorizationResult(
            true,
            new Aevatar.Studio.Application.Authorization.ScheduledInvocationAuthorizationPlan
            {
                PermissionDigest = "legacy-port-adapter",
            },
            Aevatar.Studio.Application.Authorization.ScheduledInvocationAuthorizationFailureCode.Unspecified,
            string.Empty));

    Task<StudioMemberWorkflowScheduleResult> CreateAsync(
        StudioMemberWorkflowScheduleRequest request,
        string confirmedPermissionDigest,
        CancellationToken ct = default) => EnsureAsync(request, ct);

    Task<StudioMemberWorkflowScheduleResult> ReauthorizeAsync(
        StudioMemberWorkflowScheduleRequest request,
        string confirmedPermissionDigest,
        CancellationToken ct = default) => EnsureAsync(request, ct);

    Task<StudioMemberWorkflowScheduleResult> EnsureAsync(
        StudioMemberWorkflowScheduleRequest request,
        CancellationToken ct = default);
}
