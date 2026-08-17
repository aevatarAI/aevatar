namespace Aevatar.GAgents.StudioMember;

/// <summary>
/// Admits and executes platform-side binding work for an admitted StudioMember binding run.
/// <see cref="StartAsync"/> returns an accepted receipt only. <see cref="ExecuteAsync"/>
/// executes exactly one committed stage. Command completion is checkpointed through
/// <see cref="StudioMemberPlatformBindingCommandsCompleted"/> before a later execution
/// may observe readiness. A bounded readiness observation reports
/// <see cref="StudioMemberPlatformBindingReadinessObservationTimedOut"/>; protocol v1
/// never emits the legacy <see cref="StudioMemberPlatformBindingReadinessTimedOut"/>
/// TypeUrl. Terminal outcomes use
/// <see cref="StudioMemberPlatformBindingExecutionSucceeded"/> or
/// <see cref="StudioMemberPlatformBindingExecutionFailed"/>; protocol v1 never emits
/// the corresponding legacy TypeUrls. Every outcome is delivered through the actor inbox.
/// </summary>
public interface IStudioMemberPlatformBindingCommandPort
{
    Task<StudioMemberPlatformBindingExecutionStartAccepted> StartAsync(
        string replyActorId,
        StudioMemberPlatformBindingExecutionStartRequested request,
        CancellationToken ct = default);

    Task<StudioMemberPlatformBindingExecutionAccepted> ExecuteAsync(
        string replyActorId,
        StudioMemberPlatformBindingExecutionRequest request,
        CancellationToken ct = default);
}

public sealed record StudioMemberPlatformBindingExecutionAccepted(
    string BindingRunId,
    string PlatformBindingCommandId,
    int ProtocolVersion,
    int ExecutionAttempt);
