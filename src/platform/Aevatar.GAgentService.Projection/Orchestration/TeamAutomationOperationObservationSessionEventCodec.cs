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
            observed.VaultRevocationPending);
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
        };

    private static ScheduledInvocationAgentKeyCredentialReference? ToCredentialReference(
        ScheduledInvocationAgentKeyCredentialReferenceState? credential) =>
        credential == null
            ? null
            : new ScheduledInvocationAgentKeyCredentialReference(
                credential.SecretReference?.Clone() ?? new Aevatar.Foundation.Abstractions.Credentials.SecretReference(),
                credential.ApiKeyId ?? string.Empty,
                credential.KeyExpiresAtUnixMs);

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
            : new ScheduledInvocationAgentKeyCredentialReferenceState
            {
                SecretReference = credential.SecretReference?.Clone(),
                ApiKeyId = credential.ApiKeyId ?? string.Empty,
                KeyExpiresAtUnixMs = credential.KeyExpiresAtUnixMs,
            };

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
