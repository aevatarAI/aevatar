namespace Aevatar.Studio.Application.Provisioning;

public interface IStudioMemberAutomationQueryPort
{
    Task<StudioMemberAutomationListResponse> ListAsync(
        string scopeId,
        string teamId,
        string? memberId,
        int take = 50,
        string? cursor = null,
        bool includeTotalCount = false,
        CancellationToken ct = default);

    Task<StudioMemberAutomationView?> GetAsync(
        string scopeId,
        string teamId,
        string memberId,
        string scheduleId,
        CancellationToken ct = default);
}

public interface IStudioMemberWorkflowSchedulePort : IStudioMemberAutomationQueryPort
{
    Task<StudioMemberWorkflowAuthorizationResult> PreflightAsync(
        StudioMemberWorkflowScheduleRequest request,
        CancellationToken ct = default);

    Task<StudioMemberWorkflowAuthorizationResult> PreflightForWriteAsync(
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

    Task<StudioMemberAutomationMutationReceipt> UpdateAsync(
        StudioMemberAutomationUpdateCommand command,
        CancellationToken ct = default);

    Task<StudioMemberAutomationMutationReceipt> PauseAsync(
        StudioMemberAutomationActionCommand command,
        CancellationToken ct = default);

    Task<StudioMemberAutomationMutationReceipt> ResumeAsync(
        StudioMemberAutomationActionCommand command,
        CancellationToken ct = default);

    Task<StudioMemberAutomationMutationReceipt> RunNowAsync(
        StudioMemberAutomationActionCommand command,
        CancellationToken ct = default);

    Task<StudioMemberAutomationMutationReceipt> DeleteAsync(
        StudioMemberAutomationActionCommand command,
        CancellationToken ct = default);

    Task<StudioMemberAutomationMutationReceipt> RetryRevocationAsync(
        StudioMemberAutomationRetryRevocationCommand command,
        CancellationToken ct = default) =>
        throw new NotSupportedException();
}
