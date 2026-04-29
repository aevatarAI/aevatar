using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Attributes;
using Aevatar.Foundation.Abstractions.Streaming;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Core.EventSourcing;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.Studio.Projection.Continuations;

internal sealed class StudioMemberBindingObservationGAgent
    : GAgentBase<StudioMemberBindingObservationState>
{
    private readonly StudioMemberBindingObservationHandler _handler;

    public StudioMemberBindingObservationGAgent(
        StudioMemberBindingObservationHandler handler)
    {
        _handler = handler ?? throw new ArgumentNullException(nameof(handler));
    }

    protected override Task OnActivateAsync(CancellationToken ct)
    {
        if (!State.Active)
            return Task.CompletedTask;

        return EnsureObservationRelayAsync(State.RootActorId, ct);
    }

    protected override async Task OnDeactivateAsync(CancellationToken ct)
    {
        await RemoveObservationRelayAsync(State.RootActorId, ct);
        await base.OnDeactivateAsync(ct);
    }

    [EventHandler(EndpointName = "ensureStudioMemberBindingObservation")]
    public async Task HandleEnsureAsync(EnsureStudioMemberBindingObservationCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.RootActorId);

        if (!State.Active ||
            !string.Equals(State.RootActorId, command.RootActorId, StringComparison.Ordinal))
        {
            await PersistDomainEventAsync(new StudioMemberBindingObservationStartedEvent
            {
                RootActorId = command.RootActorId,
                OccurredAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            });
        }

        await EnsureObservationRelayAsync(command.RootActorId, CancellationToken.None);
        if (!State.ObservationAttached)
        {
            await PersistDomainEventAsync(new StudioMemberBindingObservationAttachmentUpdatedEvent
            {
                Attached = true,
                OccurredAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            });
        }
    }

    [AllEventHandler(Priority = 50, AllowSelfHandling = true)]
    public async Task HandleObservedEnvelopeAsync(EventEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);

        if (!State.Active)
            return;

        if (!envelope.Route.IsObserverPublication())
            return;

        if (!StreamForwardingRules.IsForwardedEnvelopeForTarget(envelope, Id) ||
            StreamForwardingRules.IsTransitOnlyForwarding(envelope))
        {
            return;
        }

        await _handler.HandleAsync(envelope, CancellationToken.None);
    }

    protected override StudioMemberBindingObservationState TransitionState(
        StudioMemberBindingObservationState current,
        IMessage evt) =>
        StateTransitionMatcher
            .Match(current, evt)
            .On<StudioMemberBindingObservationStartedEvent>(ApplyStarted)
            .On<StudioMemberBindingObservationAttachmentUpdatedEvent>(ApplyAttachmentUpdated)
            .OrCurrent();

    private Task EnsureObservationRelayAsync(string? rootActorId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(rootActorId))
            return Task.CompletedTask;

        return Services
            .GetRequiredService<IStreamProvider>()
            .GetStream(rootActorId)
            .UpsertRelayAsync(BuildObservationRelayBinding(rootActorId), ct);
    }

    private Task RemoveObservationRelayAsync(string? rootActorId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(rootActorId))
            return Task.CompletedTask;

        return Services
            .GetRequiredService<IStreamProvider>()
            .GetStream(rootActorId)
            .RemoveRelayAsync(Id, ct);
    }

    private StreamForwardingBinding BuildObservationRelayBinding(string rootActorId)
    {
        var typeUrl = $"type.googleapis.com/{CommittedStateEventPublished.Descriptor.FullName}";
        return new StreamForwardingBinding
        {
            SourceStreamId = rootActorId,
            TargetStreamId = Id,
            ForwardingMode = StreamForwardingMode.HandleThenForward,
            DirectionFilter = [],
            EventTypeFilter = new HashSet<string>(StringComparer.Ordinal)
            {
                typeUrl,
            },
        };
    }

    private static StudioMemberBindingObservationState ApplyStarted(
        StudioMemberBindingObservationState state,
        StudioMemberBindingObservationStartedEvent evt) =>
        new()
        {
            RootActorId = evt.RootActorId,
            Active = true,
            ObservationAttached = false,
        };

    private static StudioMemberBindingObservationState ApplyAttachmentUpdated(
        StudioMemberBindingObservationState state,
        StudioMemberBindingObservationAttachmentUpdatedEvent evt) =>
        new()
        {
            RootActorId = state.RootActorId,
            Active = state.Active,
            ObservationAttached = evt.Attached,
        };
}
