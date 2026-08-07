namespace Aevatar.GAgents.StudioMember;

/// <summary>
/// Starts one out-of-turn schedule provisioning attempt. The implementation
/// must deliver a typed success, retry, or terminal failure continuation to the
/// member actor inbox; it must not mutate member state from its callback.
/// </summary>
public interface IStudioMemberWorkflowScheduleProvisioningPort
{
    Task<StudioMemberWorkflowScheduleProvisioningExecutionAccepted> ExecuteAsync(
        string replyActorId,
        StudioMemberWorkflowScheduleProvisioningIntent intent,
        DateTimeOffset? oneShotFireAt,
        int attempt,
        CancellationToken ct = default);
}

public sealed record StudioMemberWorkflowScheduleProvisioningExecutionAccepted(
    string ProvisioningId,
    int Attempt);
