namespace Aevatar.GAgents.StudioMember;

/// <summary>
/// Admits and executes platform-side binding work for an admitted StudioMember binding run.
/// <see cref="StartAsync"/> returns an accepted receipt only. <see cref="ExecuteAsync"/>
/// only accepts the execution command; the implementation must deliver the terminal
/// <see cref="StudioMemberPlatformBindingSucceeded"/> or
/// <see cref="StudioMemberPlatformBindingFailed"/> continuation back to
/// <paramref name="replyActorId"/> through the actor inbox.
/// </summary>
public interface IStudioMemberPlatformBindingCommandPort
{
    Task<StudioMemberPlatformBindingAccepted> StartAsync(
        string replyActorId,
        StudioMemberPlatformBindingStartRequested request,
        CancellationToken ct = default);

    Task<StudioMemberPlatformBindingExecutionAccepted> ExecuteAsync(
        string replyActorId,
        string platformBindingCommandId,
        StudioMemberPlatformBindingStartRequested request,
        CancellationToken ct = default);
}

public sealed record StudioMemberPlatformBindingExecutionAccepted(
    string BindingRunId,
    string PlatformBindingCommandId);
