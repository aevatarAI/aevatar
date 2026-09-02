using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.GAgentService.Abstractions.Schedules.Authorization;
using Aevatar.GAgentService.Core.Schedules.Authorization;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.GAgentService.Projection.Orchestration;

public sealed class NyxIdAuthorizationCatalogRefreshObservationSessionEventCodec
    : IProjectionSessionEventCodec<NyxIdAuthorizationCatalogRefreshCommittedOutcome>
{
    public string Channel => "nyxid-authorization-catalog-refresh-observation";

    public string GetEventType(NyxIdAuthorizationCatalogRefreshCommittedOutcome evt)
    {
        ArgumentNullException.ThrowIfNull(evt);
        return NyxIdAuthorizationCatalogRefreshOutcomeEvent.Descriptor.FullName;
    }

    public ByteString Serialize(NyxIdAuthorizationCatalogRefreshCommittedOutcome evt)
    {
        ArgumentNullException.ThrowIfNull(evt);
        return ToProto(evt).ToByteString();
    }

    public NyxIdAuthorizationCatalogRefreshCommittedOutcome? Deserialize(
        string eventType,
        ByteString payload)
    {
        if (!string.Equals(
                eventType,
                NyxIdAuthorizationCatalogRefreshOutcomeEvent.Descriptor.FullName,
                StringComparison.Ordinal) ||
            payload == null ||
            payload.IsEmpty)
        {
            return null;
        }

        try
        {
            return ToOutcome(NyxIdAuthorizationCatalogRefreshOutcomeEvent.Parser.ParseFrom(payload));
        }
        catch (InvalidProtocolBufferException)
        {
            return null;
        }
    }

    internal static NyxIdAuthorizationCatalogRefreshCommittedOutcome ToOutcome(
        NyxIdAuthorizationCatalogRefreshOutcomeEvent observed)
    {
        ArgumentNullException.ThrowIfNull(observed);
        return new NyxIdAuthorizationCatalogRefreshCommittedOutcome(
            observed.RefreshId ?? string.Empty,
            ToOutcomeStatus(observed.Status),
            observed.StateVersion,
            observed.FailureCode ?? string.Empty,
            observed.ObservedAtUtc?.ToDateTimeOffset() ?? DateTimeOffset.UnixEpoch);
    }

    private static NyxIdAuthorizationCatalogRefreshOutcomeEvent ToProto(
        NyxIdAuthorizationCatalogRefreshCommittedOutcome outcome) => new()
    {
        RefreshId = outcome.RefreshId ?? string.Empty,
        Status = ToOutcomeStatusState(outcome.Status),
        StateVersion = outcome.StateVersion,
        FailureCode = outcome.FailureCode ?? string.Empty,
        ObservedAtUtc = Timestamp.FromDateTimeOffset(outcome.ObservedAtUtc.ToUniversalTime()),
    };

    private static NyxIdAuthorizationCatalogRefreshOutcomeStatus ToOutcomeStatus(
        NyxIdAuthorizationCatalogRefreshOutcomeStatusState status) => status switch
    {
        NyxIdAuthorizationCatalogRefreshOutcomeStatusState.Started =>
            NyxIdAuthorizationCatalogRefreshOutcomeStatus.Started,
        NyxIdAuthorizationCatalogRefreshOutcomeStatusState.Observed =>
            NyxIdAuthorizationCatalogRefreshOutcomeStatus.Observed,
        NyxIdAuthorizationCatalogRefreshOutcomeStatusState.Failed =>
            NyxIdAuthorizationCatalogRefreshOutcomeStatus.Failed,
        NyxIdAuthorizationCatalogRefreshOutcomeStatusState.AccessDenied =>
            NyxIdAuthorizationCatalogRefreshOutcomeStatus.AccessDenied,
        NyxIdAuthorizationCatalogRefreshOutcomeStatusState.CatalogUnstable =>
            NyxIdAuthorizationCatalogRefreshOutcomeStatus.CatalogUnstable,
        NyxIdAuthorizationCatalogRefreshOutcomeStatusState.Superseded =>
            NyxIdAuthorizationCatalogRefreshOutcomeStatus.Superseded,
        _ => throw new InvalidOperationException($"Unknown catalog refresh outcome status '{status}'."),
    };

    private static NyxIdAuthorizationCatalogRefreshOutcomeStatusState ToOutcomeStatusState(
        NyxIdAuthorizationCatalogRefreshOutcomeStatus status) => status switch
    {
        NyxIdAuthorizationCatalogRefreshOutcomeStatus.Started =>
            NyxIdAuthorizationCatalogRefreshOutcomeStatusState.Started,
        NyxIdAuthorizationCatalogRefreshOutcomeStatus.Observed =>
            NyxIdAuthorizationCatalogRefreshOutcomeStatusState.Observed,
        NyxIdAuthorizationCatalogRefreshOutcomeStatus.Failed =>
            NyxIdAuthorizationCatalogRefreshOutcomeStatusState.Failed,
        NyxIdAuthorizationCatalogRefreshOutcomeStatus.AccessDenied =>
            NyxIdAuthorizationCatalogRefreshOutcomeStatusState.AccessDenied,
        NyxIdAuthorizationCatalogRefreshOutcomeStatus.CatalogUnstable =>
            NyxIdAuthorizationCatalogRefreshOutcomeStatusState.CatalogUnstable,
        NyxIdAuthorizationCatalogRefreshOutcomeStatus.Superseded =>
            NyxIdAuthorizationCatalogRefreshOutcomeStatusState.Superseded,
        _ => throw new InvalidOperationException($"Unknown catalog refresh outcome status '{status}'."),
    };
}
