using Aevatar.CQRS.Core.Abstractions.Streaming;

namespace Aevatar.GAgentService.Abstractions.Schedules;

public static class TeamAutomationOperationObservationStages
{
    public const string Begin = "begin";
    public const string Complete = "complete";
    public const string Delete = "delete";
    public const string Fail = "fail";
    public const string Revocation = "revocation";
}

public sealed record TeamAutomationOperationCommittedOutcome(
    string ScheduleId,
    string OperationId,
    string IdempotencyKey,
    string Stage,
    bool OwnsEffectAttempt,
    long StateVersion,
    string ErrorCode,
    string ErrorMessage,
    DateTimeOffset ObservedAtUtc,
    ScheduledInvocationAgentKeyCredentialReference? PendingRevocationCredential,
    ScheduledInvocationAuthorizationOwner? PendingRevocationOwner,
    bool NyxIdRevocationPending,
    bool VaultRevocationPending);

public sealed record TeamAutomationOperationObservationScopeLeasePreparation(
    string ActorId,
    string OperationId);

public interface ITeamAutomationOperationObservationProjectionLease
{
    string ActorId { get; }

    string OperationId { get; }
}

public interface ITeamAutomationOperationObservationProjectionPort
    : IEventSinkProjectionLifecyclePort<
        ITeamAutomationOperationObservationProjectionLease,
        TeamAutomationOperationCommittedOutcome>
{
    Task<EventSinkProjectionAttachment<ITeamAutomationOperationObservationProjectionLease>?>
        AttachExistingOperationProjectionAsync(
            string actorId,
            string operationId,
            IEventSink<TeamAutomationOperationCommittedOutcome> sink,
            CancellationToken ct = default);
}

public interface ITeamAutomationOperationObservationScopeLeasePreparationPort
{
    Task<TeamAutomationOperationObservationScopeLeasePreparation?> PrepareAsync(
        string actorId,
        string operationId,
        CancellationToken ct = default);

    Task ReleaseAsync(
        TeamAutomationOperationObservationScopeLeasePreparation preparation,
        CancellationToken ct = default);
}
