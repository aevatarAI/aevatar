namespace Aevatar.GAgents.StudioMember;

/// <summary>
/// Starts the platform-side binding work for an admitted StudioMember binding run.
/// Implementations must return after accepting the work; completion is reported
/// later by dispatching <see cref="StudioMemberPlatformBindingSucceeded"/> or
/// <see cref="StudioMemberPlatformBindingFailed"/> to the run actor.
/// </summary>
public interface IStudioMemberPlatformBindingCommandPort
{
    Task<StudioMemberPlatformBindingAccepted> StartAsync(
        string replyActorId,
        StudioMemberPlatformBindingStartRequested request,
        CancellationToken ct = default);
}
