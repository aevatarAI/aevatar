namespace Aevatar.GAgents.StudioMember;

/// <summary>
/// Admits and executes platform-side binding work for an admitted StudioMember binding run.
/// <see cref="StartAsync"/> returns an accepted receipt only. <see cref="ExecuteAsync"/>
/// executes platform work and returns a typed outcome. The run actor owns and
/// emits the terminal continuation event.
/// </summary>
public interface IStudioMemberPlatformBindingCommandPort
{
    Task<StudioMemberPlatformBindingAccepted> StartAsync(
        string replyActorId,
        StudioMemberPlatformBindingStartRequested request,
        CancellationToken ct = default);

    Task<StudioMemberPlatformBindingExecutionOutcome> ExecuteAsync(
        string replyActorId,
        string platformBindingCommandId,
        StudioMemberPlatformBindingStartRequested request,
        CancellationToken ct = default);
}

public sealed record StudioMemberPlatformBindingExecutionOutcome
{
    private StudioMemberPlatformBindingExecutionOutcome(
        StudioMemberPlatformBindingSucceeded? succeeded,
        StudioMemberPlatformBindingFailed? failed)
    {
        Succeeded = succeeded;
        Failed = failed;
    }

    public StudioMemberPlatformBindingSucceeded? Succeeded { get; }

    public StudioMemberPlatformBindingFailed? Failed { get; }

    public bool IsSucceeded => Succeeded is not null;

    public static StudioMemberPlatformBindingExecutionOutcome FromSucceeded(
        StudioMemberPlatformBindingSucceeded succeeded)
    {
        ArgumentNullException.ThrowIfNull(succeeded);
        return new StudioMemberPlatformBindingExecutionOutcome(succeeded, null);
    }

    public static StudioMemberPlatformBindingExecutionOutcome FromFailed(
        StudioMemberPlatformBindingFailed failed)
    {
        ArgumentNullException.ThrowIfNull(failed);
        return new StudioMemberPlatformBindingExecutionOutcome(null, failed);
    }
}
