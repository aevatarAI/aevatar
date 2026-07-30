using Aevatar.CQRS.Core.Abstractions.Streaming;

namespace Aevatar.GAgentService.Abstractions.Schedules;

public static class TeamAutomationOperationObservationStages
{
    public const string Begin = "begin";
    public const string Candidate = "candidate";
    public const string Complete = "complete";
    public const string Delete = "delete";
    public const string Fail = "fail";
    public const string Revocation = "revocation";
}

public enum TeamAutomationOperationObservationStatus
{
    Committed = 1,
    RejectedInvalidRequest = 2,
    RejectedConflict = 3,
    RejectedUnauthorized = 4,
    RejectedNotFound = 5,
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
    bool VaultRevocationPending,
    string EffectAttemptId = "",
    long EffectAttemptGeneration = 0,
    DateTimeOffset? EffectAttemptExpiresAtUtc = null,
    ScheduledInvocationAgentKeyCredentialReference? CandidateCredential = null,
    ScheduledInvocationAuthorizationOwner? CandidateOwner = null,
    ScheduledCredentialEffectLocator? CredentialEffectLocator = null,
    string MutationDigest = "",
    string ObservationRequestId = "",
    TeamAutomationOperationObservationStatus Status = TeamAutomationOperationObservationStatus.Committed,
    bool NewOperationCommitted = false);

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
