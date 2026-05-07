namespace Aevatar.GAgents.StudioMember;

/// <summary>
/// Admits and executes platform-side binding work for an admitted StudioMember binding run.
/// <see cref="StartAsync"/> returns an accepted receipt only. <see cref="ExecuteAsync"/>
/// performs the platform work for the current actor continuation and reports completion
/// or failure back to the run actor. Callers must only invoke it from a durable actor
/// state that can re-drive the same command after activation.
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
