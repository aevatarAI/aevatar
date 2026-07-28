using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Core.Abstractions.Orchestration;
using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.Foundation.Abstractions;
using Aevatar.GAgents.Channel.Identity.Abstractions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.GAgents.Channel.Identity;

internal sealed class ManagedCodexCredentialReadinessProjectionContext
    : IProjectionSessionContext
{
    public required string SessionId { get; init; }
    public required string RootActorId { get; init; }
    public required string ProjectionKind { get; init; }
}

internal sealed class ManagedCodexCredentialReadinessRuntimeLease
    : EventSinkProjectionRuntimeLeaseBase<ManagedCodexCredentialSnapshot>,
      IProjectionContextRuntimeLease<ManagedCodexCredentialReadinessProjectionContext>
{
    public ManagedCodexCredentialReadinessRuntimeLease(
        ManagedCodexCredentialReadinessProjectionContext context)
        : base(context.RootActorId)
    {
        Context = context;
        SessionId = context.SessionId;
    }

    public string SessionId { get; }
    public ManagedCodexCredentialReadinessProjectionContext Context { get; }
}

internal sealed class ManagedCodexCredentialReadinessProjector
    : ProjectionSessionEventProjectorBase<
        ManagedCodexCredentialReadinessProjectionContext,
        ManagedCodexCredentialSnapshot>
{
    public ManagedCodexCredentialReadinessProjector(
        IProjectionSessionEventHub<ManagedCodexCredentialSnapshot> eventHub)
        : base(eventHub)
    {
    }

    protected override IReadOnlyList<ProjectionSessionEventEntry<ManagedCodexCredentialSnapshot>>
        ResolveSessionEventEntries(
            ManagedCodexCredentialReadinessProjectionContext context,
            EventEnvelope envelope)
    {
        if (!CommittedStateEventEnvelope.TryUnpackState<ManagedCodexCredentialState>(
                envelope,
                out _,
                out var stateEvent,
                out var state) ||
            stateEvent is null ||
            state?.Credential is null)
        {
            return EmptyEntries;
        }

        var readinessEvidence = ResolveReadinessEvidence(stateEvent.EventData);
        if (readinessEvidence == ManagedCodexCredentialReadinessEvidence.Unspecified)
            return EmptyEntries;

        var snapshot = new ManagedCodexCredentialSnapshot
        {
            Credential = state.Credential.Clone(),
            StateVersion = stateEvent.Version,
            LastEventId = stateEvent.EventId ?? string.Empty,
            ReadinessEvidence = readinessEvidence,
        };
        snapshot.PendingRevocations.Add(
            state.PendingRevocations.Select(static item => item.Clone()));
        return
        [
            new ProjectionSessionEventEntry<ManagedCodexCredentialSnapshot>(
                context.RootActorId,
                context.SessionId,
                snapshot),
        ];
    }

    private static ManagedCodexCredentialReadinessEvidence ResolveReadinessEvidence(
        Any? payload)
    {
        if (payload?.Is(ManagedCodexCredentialProvisionedEvent.Descriptor) == true ||
            payload?.Is(ManagedCodexCredentialRotatedEvent.Descriptor) == true ||
            payload?.Is(ManagedCodexCredentialPolicyReconciledEvent.Descriptor) == true)
        {
            return ManagedCodexCredentialReadinessEvidence.CurrentStateConfirmed;
        }

        if (payload?.Is(ManagedCodexCredentialReadinessConfirmedEvent.Descriptor) != true)
            return ManagedCodexCredentialReadinessEvidence.Unspecified;

        var confirmed = payload.Unpack<ManagedCodexCredentialReadinessConfirmedEvent>();
        return confirmed.ReadinessEvidence is
            (ManagedCodexCredentialReadinessEvidence.CurrentStateConfirmed or
             ManagedCodexCredentialReadinessEvidence.RemoteValidated)
            ? confirmed.ReadinessEvidence
            : ManagedCodexCredentialReadinessEvidence.Unspecified;
    }
}

internal sealed class ManagedCodexCredentialSnapshotCodec
    : IProjectionSessionEventCodec<ManagedCodexCredentialSnapshot>
{
    private const string SnapshotEventType = "snapshot";

    public string Channel => "managed-codex-credential-readiness";

    public string GetEventType(ManagedCodexCredentialSnapshot evt)
    {
        ArgumentNullException.ThrowIfNull(evt);
        return SnapshotEventType;
    }

    public ByteString Serialize(ManagedCodexCredentialSnapshot evt)
    {
        ArgumentNullException.ThrowIfNull(evt);
        return evt.ToByteString();
    }

    public ManagedCodexCredentialSnapshot? Deserialize(string eventType, ByteString payload)
    {
        if (!string.Equals(eventType, SnapshotEventType, StringComparison.Ordinal) ||
            payload is null ||
            payload.IsEmpty)
        {
            return null;
        }

        try
        {
            return ManagedCodexCredentialSnapshot.Parser.ParseFrom(payload);
        }
        catch (InvalidProtocolBufferException)
        {
            return null;
        }
    }
}
