using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Persistence;
using Aevatar.Foundation.Abstractions.Streaming;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.GAgents.Channel.Identity;

internal sealed class ChannelIdentityCommittedStateActivationService
    : IChannelIdentityCommittedStateActivationService
{
    internal const string ExternalIdentityBindingProjectionKind = "external-identity-binding";
    internal const string AevatarOAuthClientProjectionKind = "aevatar-oauth-client";

    private readonly IProjectionScopeActivationService<ExternalIdentityBindingMaterializationRuntimeLease> _bindingActivation;
    private readonly IProjectionScopeActivationService<AevatarOAuthClientMaterializationRuntimeLease> _oauthClientActivation;
    private readonly IActorDispatchPort _dispatchPort;
    private readonly IEventStore _eventStore;

    public ChannelIdentityCommittedStateActivationService(
        IProjectionScopeActivationService<ExternalIdentityBindingMaterializationRuntimeLease> bindingActivation,
        IProjectionScopeActivationService<AevatarOAuthClientMaterializationRuntimeLease> oauthClientActivation,
        IActorDispatchPort dispatchPort,
        IEventStore eventStore)
    {
        _bindingActivation = bindingActivation ?? throw new ArgumentNullException(nameof(bindingActivation));
        _oauthClientActivation = oauthClientActivation ?? throw new ArgumentNullException(nameof(oauthClientActivation));
        _dispatchPort = dispatchPort ?? throw new ArgumentNullException(nameof(dispatchPort));
        _eventStore = eventStore ?? throw new ArgumentNullException(nameof(eventStore));
    }

    public async Task EnsureExternalIdentityCommittedStateActivatedAsync(
        string actorId,
        ExternalIdentityBindingState state,
        long stateVersion,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);
        ArgumentNullException.ThrowIfNull(state);

        await _bindingActivation.EnsureAsync(
            DurableRequest(actorId, ExternalIdentityBindingProjectionKind),
            ct).ConfigureAwait(false);

        await DispatchCommittedStateAsync(
            actorId,
            ExternalIdentityBindingProjectionKind,
            state,
            stateVersion,
            ct).ConfigureAwait(false);
    }

    public async Task EnsureAevatarOAuthClientCommittedStateActivatedAsync(
        string actorId,
        AevatarOAuthClientState state,
        long stateVersion,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);
        ArgumentNullException.ThrowIfNull(state);

        await _oauthClientActivation.EnsureAsync(
            DurableRequest(actorId, AevatarOAuthClientProjectionKind),
            ct).ConfigureAwait(false);

        await DispatchCommittedStateAsync(
            actorId,
            AevatarOAuthClientProjectionKind,
            state,
            stateVersion,
            ct).ConfigureAwait(false);
    }

    private async Task DispatchCommittedStateAsync<TState>(
        string actorId,
        string projectionKind,
        TState state,
        long stateVersion,
        CancellationToken ct)
        where TState : IMessage
    {
        if (stateVersion <= 0)
            return;

        var events = await _eventStore
            .GetEventsAsync(actorId, stateVersion - 1, ct)
            .ConfigureAwait(false);
        var committedEvent = events.LastOrDefault(evt => evt.Version == stateVersion);
        if (committedEvent == null)
            return;

        var scopeActorId = ProjectionScopeActorId.Build(new ProjectionRuntimeScopeKey(
            actorId,
            projectionKind,
            ProjectionRuntimeMode.DurableMaterialization));

        var envelope = new EventEnvelope
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            Payload = Any.Pack(new CommittedStateEventPublished
            {
                StateEvent = committedEvent.Clone(),
                StateRoot = Any.Pack(state),
            }),
            Route = EnvelopeRouteSemantics.CreateObserverPublication(actorId, ObserverAudience.CommittedFacts),
        };

        var forwarded = StreamForwardingRules.BuildForwardedEnvelope(
            envelope,
            actorId,
            scopeActorId,
            StreamForwardingMode.HandleThenForward);

        await _dispatchPort.DispatchAsync(scopeActorId, forwarded, ct).ConfigureAwait(false);
    }

    private static ProjectionScopeStartRequest DurableRequest(string actorId, string projectionKind) =>
        new()
        {
            RootActorId = actorId,
            ProjectionKind = projectionKind,
            Mode = ProjectionRuntimeMode.DurableMaterialization,
        };
}
