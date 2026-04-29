using Aevatar.Foundation.Abstractions.Persistence;
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.GAgents.Channel.Identity;
using FluentAssertions;
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

        await _agent.HandleEnsureProvisioned(cmd);

        _registrar.Calls.Should().HaveCount(1, "actor must serialize the DCR side-effect");
        _agent.State.ClientId.Should().Be(firstClientId);
        _agent.State.HmacKey.Should().BeEquivalentTo(firstHmacKey);
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

        public NyxIdDynamicClientRegistrationClient AsRegistrar() => new RecordingRegistrar(this);

        private sealed class RecordingRegistrar : NyxIdDynamicClientRegistrationClient
        {
            private readonly RecordingDcrClient _owner;

            public RecordingRegistrar(RecordingDcrClient owner)
                : base(new HttpClient(new NoOpHandler()), NullLogger<NyxIdDynamicClientRegistrationClient>.Instance)
            {
                _owner = owner;
            }

            public override Task<RegistrationResult> RegisterPublicClientAsync(
                string authority, string clientName, string redirectUri, CancellationToken ct = default)
            {
                _owner.Calls.Add((authority, clientName, redirectUri));
                return Task.FromResult(new RegistrationResult(_owner.NextClientId, DateTimeOffset.UtcNow));
            }
        }

        private sealed class NoOpHandler : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
                throw new InvalidOperationException("HTTP client must not be invoked in unit tests");
        }
    }
}
