namespace Aevatar.Studio.Application.Provisioning;

public interface IStudioMemberWorkflowBindingPort
{
    Task<StudioMemberWorkflowBindingResult> BindAsync(
        StudioMemberWorkflowBindingRequest request,
        CancellationToken ct = default);
}
