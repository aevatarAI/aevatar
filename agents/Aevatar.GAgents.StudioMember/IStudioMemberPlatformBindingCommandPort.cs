namespace Aevatar.GAgents.StudioMember;

/// <summary>
/// Admits and executes platform-side binding work for an admitted StudioMember binding run.
/// <see cref="StartAsync"/> returns an accepted receipt only; execution is driven
/// by the run actor through <see cref="ExecuteAsync"/> so restart recovery can
/// be derived from persisted run state.
/// </summary>
public interface IStudioMemberPlatformBindingCommandPort
{
    Task<StudioMemberPlatformBindingAccepted> StartAsync(
        string replyActorId,
        StudioMemberPlatformBindingStartRequested request,
        CancellationToken ct = default);

    Task ExecuteAsync(
        string replyActorId,
        string platformBindingCommandId,
        StudioMemberPlatformBindingStartRequested request,
        CancellationToken ct = default);
}
