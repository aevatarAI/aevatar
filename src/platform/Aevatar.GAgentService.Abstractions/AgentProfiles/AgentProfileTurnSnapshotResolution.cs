using Aevatar.AI.Abstractions;
using Aevatar.ChatRouting.Abstractions;

namespace Aevatar.GAgentService.Abstractions.AgentProfiles;

public enum AgentProfileTurnSnapshotResolutionStatus
{
    Unprofiled = 0,
    Selected = 1,
    ExplicitReferenceInvalid = 2,
    BindingUnavailable = 3,
    ProfileUnavailable = 4,
    ProfileNotPublished = 5,
    ReadModelUnavailable = 6,
    SnapshotDigestMismatch = 7,
}

public sealed record AgentProfileTurnSnapshotResolution(
    AgentProfileTurnSnapshotResolutionStatus Status,
    AgentProfileSnapshot? Profile)
{
    public bool IsSelected => Status == AgentProfileTurnSnapshotResolutionStatus.Selected;
    public bool IsFailure => Status is not (
        AgentProfileTurnSnapshotResolutionStatus.Unprofiled or
        AgentProfileTurnSnapshotResolutionStatus.Selected);

    public static AgentProfileTurnSnapshotResolution Selected(AgentProfileSnapshot profile) =>
        new(AgentProfileTurnSnapshotResolutionStatus.Selected, profile.Clone());

    public static AgentProfileTurnSnapshotResolution Unprofiled() =>
        new(AgentProfileTurnSnapshotResolutionStatus.Unprofiled, null);

    public static AgentProfileTurnSnapshotResolution Failure(
        AgentProfileTurnSnapshotResolutionStatus status) => new(status, null);
}

public sealed class AgentProfileTurnSnapshotResolutionException(
    AgentProfileTurnSnapshotResolutionStatus status,
    string message)
    : InvalidOperationException(message)
{
    public AgentProfileTurnSnapshotResolutionStatus Status { get; } = status;
}

public interface IAgentProfileTurnSnapshotResolver
{
    Task<AgentProfileTurnSnapshotResolution> ResolveAsync(
        string scopeId,
        string turnIdentity,
        ChatRouteAgentProfileKind profileKind,
        ChatRouteAgentProfileRef? explicitReference,
        CancellationToken ct = default);
}
