using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Persistence;
using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Channel.Identity;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.GAgents.ChannelRuntime.Tests.Identity;

public sealed class ChannelIdentityCommittedStateActivationServiceTests
{
    [Fact]
    public async Task EnsureExternalIdentityCommittedStateActivatedAsync_ActivatesScopeAndDispatchesCurrentStateRoot()
    {
        var bindingActivation = new RecordingActivationService<ExternalIdentityBindingMaterializationRuntimeLease>(
            request => new ExternalIdentityBindingMaterializationRuntimeLease(new ExternalIdentityBindingMaterializationContext
            {
                RootActorId = request.RootActorId,
                ProjectionKind = request.ProjectionKind,
            }));
        var oauthActivation = new RecordingActivationService<AevatarOAuthClientMaterializationRuntimeLease>(
            request => new AevatarOAuthClientMaterializationRuntimeLease(new AevatarOAuthClientMaterializationContext
            {
                RootActorId = request.RootActorId,
                ProjectionKind = request.ProjectionKind,
            }));
        var dispatch = new RecordingActorDispatchPort();
        var eventStore = new RecordingEventStore();
        var service = new ChannelIdentityCommittedStateActivationService(
            bindingActivation,
            oauthActivation,
            dispatch,
            eventStore);
        const string actorId = "external-identity-binding:lark:t:u";
        var state = new ExternalIdentityBindingState
        {
            ExternalSubject = new ExternalSubjectRef
            {
                Platform = "lark",
                Tenant = "t",
                ExternalUserId = "u",
            },
            BindingId = "bnd-first",
            BoundAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
        };
        eventStore.Seed(actorId, new ExternalIdentityBoundEvent
        {
            ExternalSubject = state.ExternalSubject.Clone(),
            BindingId = state.BindingId,
            BoundAt = state.BoundAt,
        }, 7);

        await service.EnsureExternalIdentityCommittedStateActivatedAsync(actorId, state, 7);

        bindingActivation.Requests.Should().ContainSingle().Which.Should().BeEquivalentTo(
            new ProjectionScopeStartRequest
            {
                RootActorId = actorId,
                ProjectionKind = "external-identity-binding",
                Mode = ProjectionRuntimeMode.DurableMaterialization,
            });
        dispatch.Envelopes.Should().ContainSingle();
        var (targetActorId, envelope) = dispatch.Envelopes[0];
        targetActorId.Should().Be("projection.durable.scope:external-identity-binding:external-identity-binding:lark:t:u");
        envelope.Route.IsObserverPublication().Should().BeTrue();
        var published = envelope.Payload.Unpack<CommittedStateEventPublished>();
        published.StateRoot.Unpack<ExternalIdentityBindingState>().BindingId.Should().Be("bnd-first");
        published.StateEvent.Version.Should().Be(7);
        published.StateEvent.EventData.Unpack<ExternalIdentityBoundEvent>()
            .BindingId.Should().Be("bnd-first");
    }

    [Fact]
    public async Task EnsureAevatarOAuthClientCommittedStateActivatedAsync_DispatchesOAuthStateRoot()
    {
        var bindingActivation = new RecordingActivationService<ExternalIdentityBindingMaterializationRuntimeLease>(
            request => new ExternalIdentityBindingMaterializationRuntimeLease(new ExternalIdentityBindingMaterializationContext
            {
                RootActorId = request.RootActorId,
                ProjectionKind = request.ProjectionKind,
            }));
        var oauthActivation = new RecordingActivationService<AevatarOAuthClientMaterializationRuntimeLease>(
            request => new AevatarOAuthClientMaterializationRuntimeLease(new AevatarOAuthClientMaterializationContext
            {
                RootActorId = request.RootActorId,
                ProjectionKind = request.ProjectionKind,
            }));
        var dispatch = new RecordingActorDispatchPort();
        var eventStore = new RecordingEventStore();
        var service = new ChannelIdentityCommittedStateActivationService(
            bindingActivation,
            oauthActivation,
            dispatch,
            eventStore);
        const string actorId = AevatarOAuthClientGAgent.WellKnownId;
        const string clientId = "aevatar-client-first";
        var state = new AevatarOAuthClientState
        {
            ClientId = clientId,
            ClientIdIssuedAtUnix = 1_700_000_001,
            NyxidAuthority = "https://nyxid.test",
            RedirectUri = "https://aevatar.test/api/oauth/nyxid-callback",
            OauthScope = AevatarOAuthClientScopes.AuthorizationScope,
        };
        eventStore.Seed(actorId, new AevatarOAuthClientProvisionedEvent
        {
            ClientId = state.ClientId,
            ClientIdIssuedAtUnix = state.ClientIdIssuedAtUnix,
            NyxidAuthority = state.NyxidAuthority,
            RedirectUri = state.RedirectUri,
            OauthScope = state.OauthScope,
            PersistedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
        }, 11);

        await service.EnsureAevatarOAuthClientCommittedStateActivatedAsync(actorId, state, 11);

        bindingActivation.Requests.Should().BeEmpty();
        oauthActivation.Requests.Should().ContainSingle().Which.Should().BeEquivalentTo(
            new ProjectionScopeStartRequest
            {
                RootActorId = actorId,
                ProjectionKind = "aevatar-oauth-client",
                Mode = ProjectionRuntimeMode.DurableMaterialization,
            });
        dispatch.Envelopes.Should().ContainSingle();
        var (targetActorId, envelope) = dispatch.Envelopes[0];
        targetActorId.Should().Be("projection.durable.scope:aevatar-oauth-client:aevatar-oauth-client");
        envelope.Route.IsObserverPublication().Should().BeTrue();
        var published = envelope.Payload.Unpack<CommittedStateEventPublished>();
        published.StateRoot.Unpack<AevatarOAuthClientState>().ClientId.Should().Be(clientId);
        published.StateEvent.Version.Should().Be(11);
        published.StateEvent.EventData.Unpack<AevatarOAuthClientProvisionedEvent>()
            .ClientId.Should().Be(clientId);
    }

    private sealed class RecordingActivationService<TLease> : IProjectionScopeActivationService<TLease>
        where TLease : class, IProjectionRuntimeLease
    {
        private readonly Func<ProjectionScopeStartRequest, TLease> _leaseFactory;

        public RecordingActivationService(Func<ProjectionScopeStartRequest, TLease> leaseFactory)
        {
            _leaseFactory = leaseFactory;
        }

        public List<ProjectionScopeStartRequest> Requests { get; } = [];

        public Task<TLease> EnsureAsync(ProjectionScopeStartRequest request, CancellationToken ct = default)
        {
            Requests.Add(request);
            return Task.FromResult(_leaseFactory(request));
        }
    }

    private sealed class RecordingActorDispatchPort : IActorDispatchPort
    {
        public List<(string ActorId, EventEnvelope Envelope)> Envelopes { get; } = [];

        public Task<DispatchAdmission> DispatchAsync(string actorId, EventEnvelope envelope, CancellationToken ct = default)
        {
            Envelopes.Add((actorId, envelope));
            return Task.FromResult(DispatchAdmissionFactory.Create(actorId, envelope));
        }
    }

    private sealed class RecordingEventStore : IEventStore
    {
        private readonly Dictionary<string, List<StateEvent>> _events = new(StringComparer.Ordinal);

        public void Seed<TEvent>(string agentId, TEvent evt, long version)
            where TEvent : IMessage
        {
            _events[agentId] =
            [
                new StateEvent
                {
                    AgentId = agentId,
                    EventId = $"event-{version}",
                    EventType = evt.Descriptor.FullName,
                    EventData = Any.Pack(evt),
                    Timestamp = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
                    Version = version,
                },
            ];
        }

        public Task<EventStoreCommitResult> AppendAsync(
            string agentId,
            IEnumerable<StateEvent> events,
            long expectedVersion,
            CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<StateEvent>> GetEventsAsync(
            string agentId,
            long? fromVersion = null,
            CancellationToken ct = default)
        {
            IReadOnlyList<StateEvent> result = !_events.TryGetValue(agentId, out var events)
                ? []
                : events
                    .Where(evt => !fromVersion.HasValue || evt.Version > fromVersion.Value)
                    .Select(evt => evt.Clone())
                    .ToList();
            return Task.FromResult(result);
        }

        public Task<long> GetVersionAsync(string agentId, CancellationToken ct = default) =>
            Task.FromResult(_events.TryGetValue(agentId, out var events) && events.Count > 0 ? events[^1].Version : 0);

        public Task<long> DeleteEventsUpToAsync(string agentId, long toVersion, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }
}
