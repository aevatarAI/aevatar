namespace Aevatar.Studio.Application.Provisioning;

public interface IStudioMemberWorkflowBindingPort
{
    /// <summary>
    /// Validates and dispatches a member workflow binding. A successful result
    /// is an accepted receipt; completion is observed through the binding-run URL.
    /// </summary>
    Task<StudioMemberWorkflowBindingResult> BindAsync(
        StudioMemberWorkflowBindingRequest request,
        CancellationToken ct = default);
}
