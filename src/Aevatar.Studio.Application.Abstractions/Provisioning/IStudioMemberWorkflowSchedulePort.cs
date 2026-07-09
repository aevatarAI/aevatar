namespace Aevatar.Studio.Application.Provisioning;

public interface IStudioMemberWorkflowSchedulePort
{
    Task<StudioMemberWorkflowScheduleResult> EnsureAsync(
        StudioMemberWorkflowScheduleRequest request,
        CancellationToken ct = default);
}
