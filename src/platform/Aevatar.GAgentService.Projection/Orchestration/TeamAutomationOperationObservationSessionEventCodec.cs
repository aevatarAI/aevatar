using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.GAgentService.Abstractions.Schedules;
using Aevatar.GAgentService.Core.Schedules;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.GAgentService.Projection.Orchestration;

public sealed class TeamAutomationOperationObservationSessionEventCodec
    : IProjectionSessionEventCodec<TeamAutomationOperationCommittedOutcome>
{
    public string Channel => "team-automation-operation-observation";

    public string GetEventType(TeamAutomationOperationCommittedOutcome evt)
    {
        ArgumentNullException.ThrowIfNull(evt);
        return TeamAutomationOperationObservedEvent.Descriptor.FullName;
    }

    public ByteString Serialize(TeamAutomationOperationCommittedOutcome evt)
    {
        ArgumentNullException.ThrowIfNull(evt);
        return ToProto(evt).ToByteString();
    }

    public TeamAutomationOperationCommittedOutcome? Deserialize(
        string eventType,
        ByteString payload)
    {
        if (!string.Equals(
                eventType,
                TeamAutomationOperationObservedEvent.Descriptor.FullName,
                StringComparison.Ordinal) ||
            payload == null ||
            payload.IsEmpty)
        {
            return null;
        }

        try
        {
            return ToOutcome(TeamAutomationOperationObservedEvent.Parser.ParseFrom(payload));
        }
        catch (InvalidProtocolBufferException)
        {
            return null;
        }
    }

    internal static TeamAutomationOperationCommittedOutcome ToOutcome(
        TeamAutomationOperationObservedEvent observed)
    {
        ArgumentNullException.ThrowIfNull(observed);
        return new TeamAutomationOperationCommittedOutcome(
            observed.ScheduleId ?? string.Empty,
            observed.OperationId ?? string.Empty,
            observed.IdempotencyKey ?? string.Empty,
            observed.Stage ?? string.Empty,
            observed.OwnsEffectAttempt,
            observed.StateVersion,
            observed.ErrorCode ?? string.Empty,
            observed.ErrorMessage ?? string.Empty,
            observed.ObservedAtUtc?.ToDateTimeOffset() ?? DateTimeOffset.UnixEpoch,
            ToCredentialReference(observed.PendingRevocationCredential),
            ToAuthorizationOwner(observed.PendingRevocationOwner),
            observed.NyxidRevocationPending,
            observed.VaultRevocationPending,
            observed.EffectAttemptId ?? string.Empty,
            observed.EffectAttemptGeneration,
            observed.EffectAttemptExpiresAt?.ToDateTimeOffset(),
            ToCredentialReference(observed.CandidateCredential),
            ToAuthorizationOwner(observed.CandidateOwner),
            ToCredentialEffectLocator(observed.CredentialEffectLocator),
            observed.MutationDigest ?? string.Empty,
            observed.ObservationRequestId ?? string.Empty,
            ToObservationStatus(observed.ObservationStatus),
            observed.NewOperationCommitted);
    }

    private static TeamAutomationOperationObservedEvent ToProto(
        TeamAutomationOperationCommittedOutcome outcome) =>
        new()
        {
            ScheduleId = outcome.ScheduleId ?? string.Empty,
            OperationId = outcome.OperationId ?? string.Empty,
            IdempotencyKey = outcome.IdempotencyKey ?? string.Empty,
            Stage = outcome.Stage ?? string.Empty,
            OwnsEffectAttempt = outcome.OwnsEffectAttempt,
            StateVersion = outcome.StateVersion,
            ErrorCode = outcome.ErrorCode ?? string.Empty,
            ErrorMessage = outcome.ErrorMessage ?? string.Empty,
            ObservedAtUtc = Timestamp.FromDateTimeOffset(outcome.ObservedAtUtc.ToUniversalTime()),
            PendingRevocationCredential = ToCredentialReferenceState(
                outcome.PendingRevocationCredential),
            PendingRevocationOwner = ToAuthorizationOwnerState(outcome.PendingRevocationOwner),
            NyxidRevocationPending = outcome.NyxIdRevocationPending,
            VaultRevocationPending = outcome.VaultRevocationPending,
            EffectAttemptId = outcome.EffectAttemptId ?? string.Empty,
            EffectAttemptGeneration = outcome.EffectAttemptGeneration,
            EffectAttemptExpiresAt = outcome.EffectAttemptExpiresAtUtc.HasValue
                ? Timestamp.FromDateTimeOffset(outcome.EffectAttemptExpiresAtUtc.Value.ToUniversalTime())
                : null,
            CandidateCredential = ToCredentialReferenceState(outcome.CandidateCredential),
            CandidateOwner = ToAuthorizationOwnerState(outcome.CandidateOwner),
            CredentialEffectLocator = ToCredentialEffectLocatorState(outcome.CredentialEffectLocator),
            MutationDigest = outcome.MutationDigest ?? string.Empty,
            ObservationRequestId = outcome.ObservationRequestId ?? string.Empty,
            ObservationStatus = ToObservationStatusState(outcome.Status),
            NewOperationCommitted = outcome.NewOperationCommitted,
        };

    private static TeamAutomationOperationObservationStatus ToObservationStatus(
        TeamAutomationOperationObservationStatusState status) => status switch
    {
        TeamAutomationOperationObservationStatusState.Unspecified or
            TeamAutomationOperationObservationStatusState.Committed =>
            TeamAutomationOperationObservationStatus.Committed,
        TeamAutomationOperationObservationStatusState.RejectedInvalidRequest =>
            TeamAutomationOperationObservationStatus.RejectedInvalidRequest,
        TeamAutomationOperationObservationStatusState.RejectedConflict =>
            TeamAutomationOperationObservationStatus.RejectedConflict,
        TeamAutomationOperationObservationStatusState.RejectedUnauthorized =>
            TeamAutomationOperationObservationStatus.RejectedUnauthorized,
        TeamAutomationOperationObservationStatusState.RejectedNotFound =>
            TeamAutomationOperationObservationStatus.RejectedNotFound,
        _ => throw new InvalidOperationException(
            $"Unknown Team automation operation observation status '{status}'."),
    };

    private static TeamAutomationOperationObservationStatusState ToObservationStatusState(
        TeamAutomationOperationObservationStatus status) => status switch
    {
        TeamAutomationOperationObservationStatus.Committed =>
            TeamAutomationOperationObservationStatusState.Committed,
        TeamAutomationOperationObservationStatus.RejectedInvalidRequest =>
            TeamAutomationOperationObservationStatusState.RejectedInvalidRequest,
        TeamAutomationOperationObservationStatus.RejectedConflict =>
            TeamAutomationOperationObservationStatusState.RejectedConflict,
        TeamAutomationOperationObservationStatus.RejectedUnauthorized =>
            TeamAutomationOperationObservationStatusState.RejectedUnauthorized,
        TeamAutomationOperationObservationStatus.RejectedNotFound =>
            TeamAutomationOperationObservationStatusState.RejectedNotFound,
        _ => throw new InvalidOperationException(
            $"Unknown Team automation operation observation status '{status}'."),
    };

    private static ScheduledCredentialEffectLocator? ToCredentialEffectLocator(
        ScheduledCredentialEffectLocatorState? locator) =>
        locator == null
            ? null
            : new ScheduledCredentialEffectLocator(
                locator.CredentialName ?? string.Empty,
                locator.RequestedSecretReference ?? string.Empty,
                locator.SecretPurpose ?? string.Empty,
                locator.SecretOwnerScopeKey ?? string.Empty,
                ToAuthorizationOwner(locator.CredentialOwner)
                    ?? new ScheduledInvocationAuthorizationOwner(string.Empty, string.Empty, string.Empty));

    private static ScheduledCredentialEffectLocatorState? ToCredentialEffectLocatorState(
        ScheduledCredentialEffectLocator? locator) =>
        locator == null
            ? null
            : new ScheduledCredentialEffectLocatorState
            {
                CredentialName = locator.CredentialName ?? string.Empty,
                RequestedSecretReference = locator.RequestedSecretReference ?? string.Empty,
                SecretPurpose = locator.SecretPurpose ?? string.Empty,
                SecretOwnerScopeKey = locator.SecretOwnerScopeKey ?? string.Empty,
                CredentialOwner = ToAuthorizationOwnerState(locator.CredentialOwner),
            };

    private static ScheduledInvocationAgentKeyCredentialReference? ToCredentialReference(
        ScheduledInvocationAgentKeyCredentialReferenceState? credential) =>
        credential == null
            ? null
            : new ScheduledInvocationAgentKeyCredentialReference(
                credential.SecretReference?.Clone() ?? new Aevatar.Foundation.Abstractions.Credentials.SecretReference(),
                credential.ApiKeyId ?? string.Empty,
                credential.KeyExpiresAtUnixMs,
                credential.NyxIdDurableOperationGrants
                    .Select(static grant => grant.Clone())
                    .ToArray());

    private static ScheduledInvocationAuthorizationOwner? ToAuthorizationOwner(
        ScheduledInvocationAuthorizationOwnerState? owner) =>
        owner == null
            ? null
            : new ScheduledInvocationAuthorizationOwner(
                owner.Authority ?? string.Empty,
                owner.OwnerKind ?? string.Empty,
                owner.OwnerSubject ?? string.Empty);

    private static ScheduledInvocationAgentKeyCredentialReferenceState? ToCredentialReferenceState(
        ScheduledInvocationAgentKeyCredentialReference? credential) =>
        credential == null
            ? null
            : ToCredentialReferenceStateCore(credential);

    private static ScheduledInvocationAgentKeyCredentialReferenceState ToCredentialReferenceStateCore(
        ScheduledInvocationAgentKeyCredentialReference credential)
    {
        var state = new ScheduledInvocationAgentKeyCredentialReferenceState
            {
                SecretReference = credential.SecretReference?.Clone(),
                ApiKeyId = credential.ApiKeyId ?? string.Empty,
                KeyExpiresAtUnixMs = credential.KeyExpiresAtUnixMs,
            };
        state.NyxIdDurableOperationGrants.Add(
            credential.DurableOperationGrants?.Select(static grant => grant.Clone()) ?? []);
        return state;
    }

    private static ScheduledInvocationAuthorizationOwnerState? ToAuthorizationOwnerState(
        ScheduledInvocationAuthorizationOwner? owner) =>
        owner == null
            ? null
            : new ScheduledInvocationAuthorizationOwnerState
            {
                Authority = owner.Authority ?? string.Empty,
                OwnerKind = owner.OwnerKind ?? string.Empty,
                OwnerSubject = owner.OwnerSubject ?? string.Empty,
            };
}
