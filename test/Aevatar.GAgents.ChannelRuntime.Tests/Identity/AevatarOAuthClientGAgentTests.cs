using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Persistence;
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.GAgents.Channel.Identity;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Aevatar.GAgents.ChannelRuntime.Tests.Identity;

/// <summary>
/// Behaviour tests for <see cref="AevatarOAuthClientGAgent"/>: cold-boot
/// idempotent DCR via <see cref="EnsureAevatarOAuthClientProvisionedCommand"/>,
/// HMAC key auto-seed on first provision, and idempotent broker-capability
/// observation. Pins PR #521 review fix that moved the DCR call inside the
/// actor's single-threaded handler so racing silos can't create duplicate
/// OAuth clients at NyxID.
///
/// FOLLOW-UP (tracked at issue #517 alongside the binding GAgent suite):
/// these tests instantiate the agent directly with an in-memory event
/// store; an Orleans-test-cluster integration suite is the right home for
/// rehydration / activation timing checks.
/// </summary>
public sealed class AevatarOAuthClientGAgentTests : IAsyncLifetime
{
    private AevatarOAuthClientGAgent _agent = null!;
    private ServiceProvider _serviceProvider = null!;
    private RecordingDcrClient _registrar = null!;

    public async Task InitializeAsync()
    {
        _registrar = new RecordingDcrClient();

        var services = new ServiceCollection();
        services.AddSingleton<IEventStore, IdentityGAgentTestHarness.InMemoryEventStore>();
        services.AddSingleton<EventSourcingRuntimeOptions>();
        services.AddTransient(
            typeof(IEventSourcingBehaviorFactory<>),
            typeof(DefaultEventSourcingBehaviorFactory<>));
        services.AddSingleton<Aevatar.Foundation.Abstractions.Runtime.Callbacks.IActorRuntimeCallbackScheduler,
            IdentityGAgentTestHarness.NoopCallbackScheduler>();
        // The actor resolves the registrar by abstract NyxIdDynamicClientRegistrationClient
        // type; tests inject a recording stub so HandleEnsureProvisioned exercises the
        // full code path without a real HTTP call.
        services.AddSingleton(_registrar.AsRegistrar());

        _serviceProvider = services.BuildServiceProvider();

        _agent = new AevatarOAuthClientGAgent
        {
            Services = _serviceProvider,
            EventSourcingBehaviorFactory =
                _serviceProvider.GetRequiredService<IEventSourcingBehaviorFactory<AevatarOAuthClientState>>(),
        };
        await _agent.ActivateAsync();
    }

    public Task DisposeAsync()
    {
        _serviceProvider.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task HandleEnsureProvisioned_RegistersDcrAndSeedsHmacKey_OnFirstCall()
    {
        await _agent.HandleEnsureProvisioned(new EnsureAevatarOAuthClientProvisionedCommand
        {
            NyxidAuthority = "https://nyxid.test",
            RedirectUri = "https://aevatar.test/api/oauth/nyxid-callback",
            ClientName = "aevatar",
        });

        _registrar.Calls.Should().HaveCount(1);
        _registrar.Calls[0].Authority.Should().Be("https://nyxid.test");
        _registrar.Calls[0].RedirectUri.Should().Be("https://aevatar.test/api/oauth/nyxid-callback");
        _agent.State.ClientId.Should().Be(_registrar.NextClientId);
        _agent.State.NyxidAuthority.Should().Be("https://nyxid.test");
        _agent.State.HmacKey.Length.Should().Be(32);
    }

    [Fact]
    public async Task HandleEnsureProvisioned_IsIdempotent_OnSecondCallSameAuthority()
    {
        var cmd = new EnsureAevatarOAuthClientProvisionedCommand
        {
            NyxidAuthority = "https://nyxid.test",
            RedirectUri = "https://aevatar.test/api/oauth/nyxid-callback",
            ClientName = "aevatar",
        };

        await _agent.HandleEnsureProvisioned(cmd);
        var firstClientId = _agent.State.ClientId;
        var firstHmacKey = _agent.State.HmacKey;
        var beforeRefreshState = _agent.State.Clone();
        var beforeRefreshVersion = _agent.EventSourcing!.CurrentVersion;

        await _agent.HandleEnsureProvisioned(cmd);

        _registrar.Calls.Should().HaveCount(1, "actor must serialize the DCR side-effect");
        _agent.State.ClientId.Should().Be(firstClientId);
        _agent.State.HmacKey.Should().BeEquivalentTo(firstHmacKey);
        _agent.State.Should().BeEquivalentTo(beforeRefreshState,
            "projection rebuild is a state-root refresh and must not mutate OAuth client facts");
        _agent.EventSourcing!.CurrentVersion.Should().Be(beforeRefreshVersion + 1,
            "already-provisioned ensure must re-emit the authoritative state root so an empty projection can materialize");
    }

    [Fact]
    public async Task HandleEnsureProvisioned_ReDcrs_WhenRedirectUriDrifts()
    {
        // Pin the aismart-app-mainnet 2026-04-30 incident: cluster was
        // first DCR-registered with a wildcard listen address as
        // redirect_uri. After the resolver fix, the resolved URL changes;
        // the actor MUST detect the drift and re-DCR a fresh client_id at
        // NyxID with the corrected callback target.
        await _agent.HandleEnsureProvisioned(new EnsureAevatarOAuthClientProvisionedCommand
        {
            NyxidAuthority = "https://nyxid.test",
            RedirectUri = "http://+:8080/api/oauth/nyxid-callback",
        });
        var firstClientId = _agent.State.ClientId;
        _agent.State.RedirectUri.Should().Be("http://+:8080/api/oauth/nyxid-callback",
            "first DCR persists whatever the bootstrap supplied");

        _registrar.NextClientId = "client-after-redirect-fix";
        await _agent.HandleEnsureProvisioned(new EnsureAevatarOAuthClientProvisionedCommand
        {
            NyxidAuthority = "https://nyxid.test",
            RedirectUri = "https://aevatar-console-backend-api.aevatar.ai/api/oauth/nyxid-callback",
        });

        _registrar.Calls.Should().HaveCount(2,
            "redirect URI drift must trigger a fresh DCR; otherwise NyxID keeps the wrong callback");
        _agent.State.ClientId.Should().Be("client-after-redirect-fix");
        _agent.State.ClientId.Should().NotBe(firstClientId);
        _agent.State.RedirectUri.Should().Be("https://aevatar-console-backend-api.aevatar.ai/api/oauth/nyxid-callback");
    }

    [Fact]
    public async Task HandleEnsureProvisioned_ReDcrs_WhenLegacyRedirectUriIsMissing()
    {
        await _agent.HandleProvision(new ProvisionAevatarOAuthClientCommand
        {
            ClientId = "legacy-client-with-unknown-callback",
            NyxidAuthority = "https://nyxid.test",
        });
        _agent.State.RedirectUri.Should().BeEmpty();
        _registrar.Calls.Should().BeEmpty("manual legacy provision does not call DCR");

        _registrar.NextClientId = "client-after-legacy-heal";
        await _agent.HandleEnsureProvisioned(new EnsureAevatarOAuthClientProvisionedCommand
        {
            NyxidAuthority = "https://nyxid.test",
            RedirectUri = "https://aevatar-console-backend-api.aevatar.ai/api/oauth/nyxid-callback",
        });

        _registrar.Calls.Should().ContainSingle(
            "legacy empty redirect_uri is unknown and must be healed with a fresh DCR");
        _agent.State.ClientId.Should().Be("client-after-legacy-heal");
        _agent.State.RedirectUri.Should().Be("https://aevatar-console-backend-api.aevatar.ai/api/oauth/nyxid-callback");
    }

    [Fact]
    public async Task HandleEnsureProvisioned_DoesNotReDcr_WhenRedirectUriMatches()
    {
        var redirectUri = "https://aevatar-console-backend-api.aevatar.ai/api/oauth/nyxid-callback";
        await _agent.HandleEnsureProvisioned(new EnsureAevatarOAuthClientProvisionedCommand
        {
            NyxidAuthority = "https://nyxid.test",
            RedirectUri = redirectUri,
        });

        await _agent.HandleEnsureProvisioned(new EnsureAevatarOAuthClientProvisionedCommand
        {
            NyxidAuthority = "https://nyxid.test",
            RedirectUri = redirectUri,
        });

        _registrar.Calls.Should().HaveCount(1, "matching redirect URI must not re-DCR");
    }

    [Fact]
    public async Task HandleEnsureProvisioned_RegistersAgain_WhenAuthorityChanges()
    {
        await _agent.HandleEnsureProvisioned(new EnsureAevatarOAuthClientProvisionedCommand
        {
            NyxidAuthority = "https://prod.nyxid.test",
            RedirectUri = "https://aevatar.test/api/oauth/nyxid-callback",
        });
        var firstClientId = _agent.State.ClientId;

        _registrar.NextClientId = "client-after-rotate";
        await _agent.HandleEnsureProvisioned(new EnsureAevatarOAuthClientProvisionedCommand
        {
            NyxidAuthority = "https://staging.nyxid.test",
            RedirectUri = "https://aevatar.test/api/oauth/nyxid-callback",
        });

        _registrar.Calls.Should().HaveCount(2);
        _agent.State.ClientId.Should().Be("client-after-rotate");
        _agent.State.ClientId.Should().NotBe(firstClientId);
        _agent.State.NyxidAuthority.Should().Be("https://staging.nyxid.test");
        // Re-provision against a fresh authority MUST drop the broker
        // observation — the new client_id starts unobserved until ops re-
        // enables broker_capability_enabled.
        _agent.State.BrokerCapabilityObserved.Should().BeFalse();
    }

    [Fact]
    public async Task HandleEnsureProvisioned_RejectsMissingFields()
    {
        await _agent.HandleEnsureProvisioned(new EnsureAevatarOAuthClientProvisionedCommand
        {
            NyxidAuthority = string.Empty,
            RedirectUri = "https://aevatar.test/api/oauth/nyxid-callback",
        });
        _agent.State.ClientId.Should().BeEmpty();

        await _agent.HandleEnsureProvisioned(new EnsureAevatarOAuthClientProvisionedCommand
        {
            NyxidAuthority = "https://nyxid.test",
            RedirectUri = string.Empty,
        });
        _agent.State.ClientId.Should().BeEmpty();
        _registrar.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task HandleProvision_ManualOverride_AcceptsCallerSuppliedClientId()
    {
        // ProvisionAevatarOAuthClientCommand is the test / manual fixture
        // path; bootstrap NEVER uses it. Verifies the flow keeps working
        // for tests that pre-seed a known client_id (e.g. integration tests
        // pinning a fixture).
        await _agent.HandleProvision(new ProvisionAevatarOAuthClientCommand
        {
            ClientId = "manual-fixture-client",
            ClientIdIssuedAtUnix = 1700000000,
            NyxidAuthority = "https://nyxid.test",
        });

        _agent.State.ClientId.Should().Be("manual-fixture-client");
        _agent.State.HmacKey.Length.Should().Be(32, "HMAC key seeds on first provision regardless of which command path triggered it");
        _registrar.Calls.Should().BeEmpty("manual provision must not call DCR");
    }

    [Fact]
    public async Task HandleObserveBrokerCapability_IsIdempotent()
    {
        await _agent.HandleProvision(new ProvisionAevatarOAuthClientCommand
        {
            ClientId = "client-x",
            ClientIdIssuedAtUnix = 1700000000,
            NyxidAuthority = "https://nyxid.test",
        });

        await _agent.HandleObserveBrokerCapability(new ObserveBrokerCapabilityCommand());
        var firstObservedAt = _agent.State.BrokerCapabilityObservedAtUnix;
        _agent.State.BrokerCapabilityObserved.Should().BeTrue();

        await _agent.HandleObserveBrokerCapability(new ObserveBrokerCapabilityCommand());
        _agent.State.BrokerCapabilityObservedAtUnix.Should().Be(firstObservedAt,
            "second observation must not advance the timestamp");
    }

    [Fact]
    public async Task HandleRotateHmacKey_DemotesPreviousKeyToGraceWindow()
    {
        await _agent.HandleProvision(new ProvisionAevatarOAuthClientCommand
        {
            ClientId = "client-x",
            NyxidAuthority = "https://nyxid.test",
        });
        var seededKey = _agent.State.HmacKey;
        var seededKid = _agent.State.HmacKid;
        seededKid.Should().Be(AevatarOAuthClientGAgent.InitialHmacKid);

        await _agent.HandleRotateHmacKey(new RotateAevatarOAuthClientHmacKeyCommand());

        _agent.State.HmacKey.Should().NotBeEquivalentTo(seededKey);
        _agent.State.HmacKey.Length.Should().Be(32);
        _agent.State.ClientId.Should().Be("client-x");
        // Kid increments deterministically (v1 → v2) so verifiers can route
        // signed tokens to the right key.
        _agent.State.HmacKid.Should().Be("v2");
        // Previous key must be carried for the grace window so in-flight
        // state tokens signed with the old key still verify.
        _agent.State.PreviousHmacKid.Should().Be(seededKid);
        _agent.State.PreviousHmacKey.Should().BeEquivalentTo(seededKey);
        _agent.State.PreviousHmacDemotedAtUnix.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task HandleEnsureProvisioned_AbsorbsOcc_WhenPeerAlreadyHealedDriftMidDcr()
    {
        // Pin issue #549: cluster-shared Garnet event store + brief K8s
        // rolling-deploy two-pod overlap lets two grain activations of this
        // well-known actor each call DCR (each getting its own client_id
        // from NyxID) and each try to commit Provisioned at the same
        // expectedVersion. One wins, one sees OCC. The losing handler MUST
        // absorb the OCC as a no-op when the peer's commit already healed
        // the redirect drift this command came in to fix — otherwise the
        // losing pod retries and burns yet another orphan client at NyxID
        // on every backoff attempt.
        await _agent.HandleEnsureProvisioned(new EnsureAevatarOAuthClientProvisionedCommand
        {
            NyxidAuthority = "https://nyxid.test",
            RedirectUri = "http://+:8080/api/oauth/nyxid-callback",
        });

        var resolvedRedirect = "https://aevatar-console-backend-api.aevatar.ai/api/oauth/nyxid-callback";
        _registrar.NextClientId = "loser-orphan-client";
        _registrar.OnRegistered = async () =>
        {
            // Simulate the peer pod's grain activation winning the race:
            // it commits a Provisioned event with the correct redirect URI
            // to the shared event store while this handler is still mid-
            // DCR. When this handler comes back from its own DCR HTTP call
            // and tries to commit at expectedVersion=N, the store is
            // already at N+1.
            var store = _serviceProvider.GetRequiredService<IEventStore>();
            var actorId = _agent.Id;
            var current = await store.GetVersionAsync(actorId);
            var peerEvent = new AevatarOAuthClientProvisionedEvent
            {
                ClientId = "peer-pod-after-heal",
                ClientIdIssuedAtUnix = 1700000001,
                NyxidAuthority = "https://nyxid.test",
                RedirectUri = resolvedRedirect,
                PersistedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            };
            await store.AppendAsync(
                actorId,
                new[]
                {
                    new StateEvent
                    {
                        AgentId = actorId,
                        Version = current + 1,
                        EventType = AevatarOAuthClientProvisionedEvent.Descriptor.FullName,
                        EventData = Any.Pack(peerEvent),
                        Timestamp = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
                    },
                },
                current);
        };

        await _agent.HandleEnsureProvisioned(new EnsureAevatarOAuthClientProvisionedCommand
        {
            NyxidAuthority = "https://nyxid.test",
            RedirectUri = resolvedRedirect,
        });

        // Loser absorbed the OCC: state reflects the peer's commit, NOT
        // this handler's DCR result (which is now an orphan at NyxID).
        _agent.State.ClientId.Should().Be("peer-pod-after-heal");
        _agent.State.RedirectUri.Should().Be(resolvedRedirect);
        _registrar.Calls.Should().HaveCount(2,
            "the losing handler called DCR before discovering the peer's commit; that DCR result is logged as an orphan");
    }

    [Fact]
    public async Task HandleEnsureProvisioned_RethrowsOcc_WhenPeerCommitDoesNotHealDrift()
    {
        // The OCC absorber must NOT swallow conflicts where the peer's
        // commit was something unrelated (e.g. a future schema event the
        // actor doesn't know about). In that case the bootstrap retry
        // path must observe the failure and re-evaluate against fresh
        // state, otherwise we'd silently leave drift unhealed.
        await _agent.HandleEnsureProvisioned(new EnsureAevatarOAuthClientProvisionedCommand
        {
            NyxidAuthority = "https://nyxid.test",
            RedirectUri = "http://+:8080/api/oauth/nyxid-callback",
        });

        _registrar.NextClientId = "loser-orphan-client";
        _registrar.OnRegistered = async () =>
        {
            // Peer's commit is a rebuild request — Apply is identity, so
            // it does NOT update RedirectUri. State stays drifted after
            // refresh, the absorber returns false, OCC propagates.
            var store = _serviceProvider.GetRequiredService<IEventStore>();
            var actorId = _agent.Id;
            var current = await store.GetVersionAsync(actorId);
            var peerEvent = new AevatarOAuthClientProjectionRebuildRequestedEvent
            {
                Reason = "peer_rebuild",
                RequestedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            };
            await store.AppendAsync(
                actorId,
                new[]
                {
                    new StateEvent
                    {
                        AgentId = actorId,
                        Version = current + 1,
                        EventType = AevatarOAuthClientProjectionRebuildRequestedEvent.Descriptor.FullName,
                        EventData = Any.Pack(peerEvent),
                        Timestamp = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
                    },
                },
                current);
        };

        var act = () => _agent.HandleEnsureProvisioned(new EnsureAevatarOAuthClientProvisionedCommand
        {
            NyxidAuthority = "https://nyxid.test",
            RedirectUri = "https://aevatar-console-backend-api.aevatar.ai/api/oauth/nyxid-callback",
        });

        await act.Should().ThrowAsync<EventStoreOptimisticConcurrencyException>();
    }

    [Fact]
    public async Task HandleProvision_FirstSeed_DoesNotCarryPreviousKey()
    {
        // Initial seed has no prior key — the previous-key fields stay
        // empty so verifiers don't accidentally accept tokens signed with
        // never-issued material.
        await _agent.HandleProvision(new ProvisionAevatarOAuthClientCommand
        {
            ClientId = "client-x",
            NyxidAuthority = "https://nyxid.test",
        });

        _agent.State.HmacKid.Should().Be(AevatarOAuthClientGAgent.InitialHmacKid);
        _agent.State.PreviousHmacKey.Length.Should().Be(0);
        _agent.State.PreviousHmacKid.Should().BeNullOrEmpty();
    }

    private sealed class RecordingDcrClient
    {
        public string NextClientId { get; set; } = "client-first";
        public List<(string Authority, string ClientName, string RedirectUri)> Calls { get; } = new();

        /// <summary>
        /// Hook that runs after the DCR call records the request but before
        /// the result is returned. Tests use it to simulate a peer pod
        /// committing to the shared event store while the handler is mid-DCR
        /// (the cluster-shared Garnet race that issue #549 documents).
        /// </summary>
        public Func<Task>? OnRegistered { get; set; }

        public NyxIdDynamicClientRegistrationClient AsRegistrar() => new RecordingRegistrar(this);

        private sealed class RecordingRegistrar : NyxIdDynamicClientRegistrationClient
        {
            private readonly RecordingDcrClient _owner;

            public RecordingRegistrar(RecordingDcrClient owner)
                : base(new HttpClient(new NoOpHandler()), NullLogger<NyxIdDynamicClientRegistrationClient>.Instance)
            {
                _owner = owner;
            }

            public override async Task<RegistrationResult> RegisterPublicClientAsync(
                string authority, string clientName, string redirectUri, CancellationToken ct = default)
            {
                _owner.Calls.Add((authority, clientName, redirectUri));
                if (_owner.OnRegistered is not null)
                    await _owner.OnRegistered().ConfigureAwait(false);
                return new RegistrationResult(_owner.NextClientId, DateTimeOffset.UtcNow);
            }
        }

        private sealed class NoOpHandler : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
                throw new InvalidOperationException("HTTP client must not be invoked in unit tests");
        }
    }
}
