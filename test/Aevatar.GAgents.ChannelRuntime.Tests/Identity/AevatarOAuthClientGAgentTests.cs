using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Credentials;
using Aevatar.Foundation.Abstractions.Credentials.Testing;
using Aevatar.Foundation.Abstractions.Persistence;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.GAgents.Channel.Identity;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using System.Reflection;
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
    // Refactor (iter71/cluster-071-identity-projection-rebuild-events):
    //   Old pattern: emit no-op ProjectionRebuildRequested event in command handler to trigger projection materialization
    //   New principle: Identity actor only persists real identity facts; projection materialization owned by projection lifecycle/materializer/bootstrap
    private AevatarOAuthClientGAgent _agent = null!;
    private ServiceProvider _serviceProvider = null!;
    private RecordingDcrClient _registrar = null!;
    private InMemorySecretVault _secretVault = null!;
    private IdentityGAgentTestHarness.NoopCallbackScheduler _callbackScheduler = null!;

    public async Task InitializeAsync()
    {
        _registrar = new RecordingDcrClient();
        _secretVault = new InMemorySecretVault();
        _callbackScheduler = new IdentityGAgentTestHarness.NoopCallbackScheduler();

        var services = new ServiceCollection();
        services.AddSingleton<IEventStore, IdentityGAgentTestHarness.InMemoryEventStore>();
        services.AddSingleton<ISecretVault>(_secretVault);
        services.AddSingleton<EventSourcingRuntimeOptions>();
        services.AddTransient(
            typeof(IEventSourcingBehaviorFactory<>),
            typeof(DefaultEventSourcingBehaviorFactory<>));
        services.AddSingleton<Aevatar.Foundation.Abstractions.Runtime.Callbacks.IActorRuntimeCallbackScheduler>(
            _callbackScheduler);
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
        SetActorId(_agent, AevatarOAuthClientGAgent.WellKnownId);
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
        _registrar.Calls[0].RedirectUris.Should().Equal("https://aevatar.test/api/oauth/nyxid-callback");
        _agent.State.ClientId.Should().Be(_registrar.NextClientId);
        _agent.State.NyxidAuthority.Should().Be("https://nyxid.test");
        _agent.State.OauthScope.Should().Be(AevatarOAuthClientScopes.AuthorizationScope);
        // New writes store the key in the vault and leave [hmac_key] empty; the
        // state carries only a ref that resolves to 32 raw bytes.
        _agent.State.HmacKey.Length.Should().Be(0, "new writes leave the legacy plaintext field empty");
        _agent.State.HmacKeyRef.Should().NotBeNull();
        (await ResolveKeyAsync(_agent.State.HmacKeyRef!)).Should().HaveCount(32);
        _agent.State.ProvisioningRetryAttempt.Should().Be(0);
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
        var firstHmacKeyRef = _agent.State.HmacKeyRef;
        var beforeRefreshState = _agent.State.Clone();
        var beforeRefreshVersion = _agent.EventSourcing!.CurrentVersion;

        await _agent.HandleEnsureProvisioned(cmd);

        _registrar.Calls.Should().HaveCount(1, "actor must serialize the DCR side-effect");
        _agent.State.ClientId.Should().Be(firstClientId);
        _agent.State.HmacKeyRef.Should().Be(firstHmacKeyRef, "already-provisioned ensure must not rotate the key");
        _agent.State.Should().BeEquivalentTo(beforeRefreshState,
            "already-provisioned ensure must not mutate OAuth client facts");
        _agent.EventSourcing!.CurrentVersion.Should().Be(beforeRefreshVersion,
            "already-provisioned ensure must not append a projection-only no-op event");
    }

    [Fact]
    public async Task HandleEnsureProvisioned_ForceReprovision_ReDcrs_WhenAlreadyProvisioned()
    {
        var cmd = new EnsureAevatarOAuthClientProvisionedCommand
        {
            NyxidAuthority = "https://nyxid.test",
            RedirectUri = "https://aevatar.test/api/oauth/nyxid-callback",
            ClientName = "aevatar",
        };
        cmd.RedirectUris.Add("https://aevatar.test/api/oauth/nyxid-callback");
        cmd.RedirectUris.Add("https://console.test/auth/callback");

        await _agent.HandleEnsureProvisioned(cmd);
        var firstClientId = _agent.State.ClientId;

        _registrar.NextClientId = "client-after-force-dcr";
        cmd.ForceReprovision = true;
        await _agent.HandleEnsureProvisioned(cmd);

        _registrar.Calls.Should().HaveCount(2, "break-glass force DCR must bypass the already-provisioned no-op");
        _agent.State.ClientId.Should().Be("client-after-force-dcr");
        _agent.State.ClientId.Should().NotBe(firstClientId);
        _agent.State.RedirectUris.Should().Equal(
            "https://aevatar.test/api/oauth/nyxid-callback",
            "https://console.test/auth/callback");
        _agent.State.ForceReprovisionConsumed.Should().BeTrue();
    }

    [Fact]
    public async Task HandleEnsureProvisioned_ForceReprovision_IsConsumedOnce()
    {
        var cmd = new EnsureAevatarOAuthClientProvisionedCommand
        {
            NyxidAuthority = "https://nyxid.test",
            RedirectUri = "https://aevatar.test/api/oauth/nyxid-callback",
            ClientName = "aevatar",
        };
        cmd.RedirectUris.Add("https://aevatar.test/api/oauth/nyxid-callback");
        cmd.RedirectUris.Add("https://console.test/auth/callback");

        await _agent.HandleEnsureProvisioned(cmd);

        _registrar.NextClientId = "client-after-force-dcr";
        cmd.ForceReprovision = true;
        await _agent.HandleEnsureProvisioned(cmd);
        await _agent.HandleEnsureProvisioned(cmd);

        _registrar.Calls.Should().HaveCount(2, "only one startup force DCR should be consumed cluster-wide");
        _agent.State.ClientId.Should().Be("client-after-force-dcr");
        _agent.State.ForceReprovisionConsumed.Should().BeTrue();

        cmd.ForceReprovision = false;
        await _agent.HandleEnsureProvisioned(cmd);
        _agent.State.ForceReprovisionConsumed.Should().BeFalse("normal startup after env removal resets the break-glass guard");
    }

    [Fact]
    public async Task HandleProvisioningRetryFired_PreservesForceReprovision_WhenForcedDcrFails()
    {
        await _agent.HandleEnsureProvisioned(new EnsureAevatarOAuthClientProvisionedCommand
        {
            NyxidAuthority = "https://nyxid.test",
            RedirectUri = "https://aevatar.test/api/oauth/nyxid-callback",
            ClientName = "aevatar",
        });

        _registrar.ThrowOnRegister = new InvalidOperationException("nyxid unavailable");
        var force = new EnsureAevatarOAuthClientProvisionedCommand
        {
            NyxidAuthority = "https://nyxid.test",
            RedirectUri = "https://aevatar.test/api/oauth/nyxid-callback",
            ClientName = "aevatar",
            ForceReprovision = true,
        };
        force.RedirectUris.Add("https://aevatar.test/api/oauth/nyxid-callback");
        force.RedirectUris.Add("https://console.test/auth/callback");
        await _agent.HandleEnsureProvisioned(force);

        _agent.State.ProvisioningRetryForceReprovision.Should().BeTrue();
        _callbackScheduler.TimeoutRequests.Should().ContainSingle();
        _callbackScheduler.TimeoutRequests[0].TriggerEnvelope.Payload
            .Unpack<AevatarOAuthClientProvisioningRetryFiredEvent>()
            .ForceReprovision.Should().BeTrue();

        _registrar.ThrowOnRegister = null;
        _registrar.NextClientId = "client-after-force-retry";
        await _agent.HandleProvisioningRetryFired(AddRedirectUris(new AevatarOAuthClientProvisioningRetryFiredEvent
        {
            Attempt = _agent.State.ProvisioningRetryAttempt,
            DueUnixMs = _agent.State.ProvisioningRetryDueUnixMs,
            NyxidAuthority = _agent.State.ProvisioningRetryAuthority,
            RedirectUri = _agent.State.ProvisioningRetryRedirectUri,
            ClientName = _agent.State.ProvisioningRetryClientName,
            CallbackId = _agent.State.ProvisioningRetryCallbackId,
            CallbackGeneration = _agent.State.ProvisioningRetryCallbackGeneration,
            FiredAtUnixMs = _agent.State.ProvisioningRetryDueUnixMs,
            ForceReprovision = true,
        }, _agent.State.ProvisioningRetryRedirectUris));

        _registrar.Calls.Should().HaveCount(3, "the retry must preserve and execute the force-DCR intent");
        _agent.State.ClientId.Should().Be("client-after-force-retry");
        _agent.State.ProvisioningRetryAttempt.Should().Be(0);
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
            "first DCR persists whatever the explicit Ensure command supplied");

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
        (await ReadEventsAsync<AevatarOAuthClientDriftReconciledEvent>())
            .Should()
            .ContainSingle(e => e.DriftKind == "redirect_uri");
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
    public async Task HandleEnsureProvisioned_RegistersAndDetectsFullRedirectUriListDrift()
    {
        var initial = new EnsureAevatarOAuthClientProvisionedCommand
        {
            NyxidAuthority = "https://nyxid.test",
            RedirectUri = "https://aevatar.test/api/oauth/nyxid-callback",
        };
        initial.RedirectUris.Add("https://aevatar.test/api/oauth/nyxid-callback");
        initial.RedirectUris.Add("https://console.test/auth/callback");

        await _agent.HandleEnsureProvisioned(initial);

        _registrar.Calls.Should().HaveCount(1);
        _registrar.Calls[0].RedirectUris.Should().Equal(
            "https://aevatar.test/api/oauth/nyxid-callback",
            "https://console.test/auth/callback");
        _agent.State.RedirectUris.Should().Equal(
            "https://aevatar.test/api/oauth/nyxid-callback",
            "https://console.test/auth/callback");

        var expanded = new EnsureAevatarOAuthClientProvisionedCommand
        {
            NyxidAuthority = "https://nyxid.test",
            RedirectUri = "https://aevatar.test/api/oauth/nyxid-callback",
        };
        expanded.RedirectUris.Add("https://aevatar.test/api/oauth/nyxid-callback");
        expanded.RedirectUris.Add("https://console.test/auth/callback");
        expanded.RedirectUris.Add("https://ops.test/callback");

        _registrar.NextClientId = "client-after-list-drift";
        await _agent.HandleEnsureProvisioned(expanded);

        _registrar.Calls.Should().HaveCount(2);
        _registrar.Calls[1].RedirectUris.Should().Equal(
            "https://aevatar.test/api/oauth/nyxid-callback",
            "https://console.test/auth/callback",
            "https://ops.test/callback");
        _agent.State.ClientId.Should().Be("client-after-list-drift");
        _agent.State.RedirectUris.Should().Equal(
            "https://aevatar.test/api/oauth/nyxid-callback",
            "https://console.test/auth/callback",
            "https://ops.test/callback");
        (await ReadEventsAsync<AevatarOAuthClientDriftReconciledEvent>())
            .Should()
            .ContainSingle(e => e.DriftKind == "redirect_uris");
    }

    [Fact]
    public async Task HandleEnsureProvisioned_ReDcrs_WhenLegacyScopeIsMissingProxy()
    {
        var redirectUri = "https://aevatar-console-backend-api.aevatar.ai/api/oauth/nyxid-callback";
        var cmd = new EnsureAevatarOAuthClientProvisionedCommand
        {
            NyxidAuthority = "https://nyxid.test",
            RedirectUri = redirectUri,
        };

        await _agent.HandleEnsureProvisioned(cmd);
        _agent.State.OauthScope = "openid urn:nyxid:scope:broker_binding";

        _registrar.NextClientId = "client-after-scope-heal";
        await _agent.HandleEnsureProvisioned(cmd);

        _registrar.Calls.Should().HaveCount(2,
            "legacy clients without proxy scope cannot mint NyxID LLM API tokens");
        _agent.State.ClientId.Should().Be("client-after-scope-heal");
        _agent.State.OauthScope.Should().Be(AevatarOAuthClientScopes.AuthorizationScope);
        (await ReadEventsAsync<AevatarOAuthClientDriftReconciledEvent>())
            .Should()
            .ContainSingle(e => e.DriftKind == "oauth_scope");
    }

    [Fact]
    public async Task HandleEnsureProvisioned_SchedulesDurableRetry_WhenDcrFails()
    {
        _registrar.ThrowOnRegister = new InvalidOperationException("nyxid unavailable");

        await _agent.HandleEnsureProvisioned(new EnsureAevatarOAuthClientProvisionedCommand
        {
            NyxidAuthority = "https://nyxid.test",
            RedirectUri = "https://aevatar.test/api/oauth/nyxid-callback",
            ClientName = "aevatar",
        });

        _agent.State.ClientId.Should().BeEmpty();
        _agent.State.ProvisioningRetryAttempt.Should().Be(1);
        _agent.State.ProvisioningRetryAuthority.Should().Be("https://nyxid.test");
        _agent.State.ProvisioningRetryRedirectUri.Should().Be("https://aevatar.test/api/oauth/nyxid-callback");
        _agent.State.ProvisioningRetryRedirectUris.Should().Equal("https://aevatar.test/api/oauth/nyxid-callback");
        _agent.State.ProvisioningRetryClientName.Should().Be("aevatar");
        _agent.State.ProvisioningRetryDueUnixMs.Should().BeGreaterThan(0);
        _agent.State.ProvisioningRetryCallbackGeneration.Should().Be(1);
        _agent.State.ProvisioningRetryLastError.Should().Contain("nyxid unavailable");
        _callbackScheduler.TimeoutRequests.Should().ContainSingle();
        _callbackScheduler.TimeoutRequests[0].DueTime.Should().Be(AevatarOAuthClientGAgent.InitialRetryDelay);
        (await ReadEventsAsync<AevatarOAuthClientProvisioningRetryScheduledEvent>())
            .Should()
            .ContainSingle();
    }

    [Fact]
    public async Task HandleProvision_ConfiguredClientClearsLegacyDcrRetry()
    {
        _registrar.ThrowOnRegister = new InvalidOperationException("nyxid unavailable");
        await _agent.HandleEnsureProvisioned(new EnsureAevatarOAuthClientProvisionedCommand
        {
            NyxidAuthority = "https://nyxid.test",
            RedirectUri = "https://aevatar.test/api/oauth/nyxid-callback",
            ClientName = "aevatar",
        });
        var staleRetry = new AevatarOAuthClientProvisioningRetryFiredEvent
        {
            Attempt = _agent.State.ProvisioningRetryAttempt,
            DueUnixMs = _agent.State.ProvisioningRetryDueUnixMs,
            NyxidAuthority = _agent.State.ProvisioningRetryAuthority,
            RedirectUri = _agent.State.ProvisioningRetryRedirectUri,
            ClientName = _agent.State.ProvisioningRetryClientName,
            CallbackId = _agent.State.ProvisioningRetryCallbackId,
            CallbackGeneration = _agent.State.ProvisioningRetryCallbackGeneration,
            FiredAtUnixMs = _agent.State.ProvisioningRetryDueUnixMs,
        };

        await _agent.HandleProvision(new ProvisionAevatarOAuthClientCommand
        {
            ClientId = "configured-client",
            ClientIdIssuedAtUnix = 1700000000,
            NyxidAuthority = "https://nyxid.test",
            RedirectUri = "https://aevatar.test/api/oauth/nyxid-callback",
            OauthScope = AevatarOAuthClientScopes.AuthorizationScope,
        });

        _agent.State.ClientId.Should().Be("configured-client");
        _agent.State.ProvisioningRetryAttempt.Should().Be(0);
        _agent.State.ProvisioningRetryCallbackId.Should().BeEmpty();
        (await ReadEventsAsync<AevatarOAuthClientProvisioningRetryClearedEvent>())
            .Should()
            .ContainSingle(evt => evt.Reason == "configured_client_provisioned");

        _registrar.ThrowOnRegister = null;
        _registrar.NextClientId = "unexpected-dcr-client";
        await _agent.HandleProvisioningRetryFired(staleRetry);

        _registrar.Calls.Should().ContainSingle("the cleared retry callback must not re-enter DCR");
        _agent.State.ClientId.Should().Be("configured-client");
    }

    [Fact]
    public async Task HandleEnsureProvisioned_DuplicateExternalEnsures_DoNotBypassPendingBackoff()
    {
        _registrar.ThrowOnRegister = new InvalidOperationException("nyxid unavailable");
        var cmd = new EnsureAevatarOAuthClientProvisionedCommand
        {
            NyxidAuthority = "https://nyxid.test",
            RedirectUri = "https://aevatar.test/api/oauth/nyxid-callback",
            ClientName = "aevatar",
        };

        await _agent.HandleEnsureProvisioned(cmd);

        _registrar.Calls.Should().HaveCount(1);
        _agent.State.ProvisioningRetryAttempt.Should().Be(1);
        _callbackScheduler.TimeoutRequests.Should().ContainSingle();

        _registrar.ThrowOnRegister = null;
        _registrar.NextClientId = "client-after-self-callback";
        await _agent.HandleEnsureProvisioned(cmd);
        await _agent.HandleEnsureProvisioned(cmd);
        await _agent.HandleEnsureProvisioned(cmd);

        _registrar.Calls.Should().HaveCount(1,
            "duplicate cold-boot external ensures must not drive retry timing while backoff is pending");
        _agent.State.ProvisioningRetryAttempt.Should().Be(1);
        _callbackScheduler.TimeoutRequests.Should().ContainSingle();

        await _agent.HandleProvisioningRetryFired(AddRedirectUris(new AevatarOAuthClientProvisioningRetryFiredEvent
        {
            Attempt = _agent.State.ProvisioningRetryAttempt,
            DueUnixMs = _agent.State.ProvisioningRetryDueUnixMs,
            NyxidAuthority = _agent.State.ProvisioningRetryAuthority,
            RedirectUri = _agent.State.ProvisioningRetryRedirectUri,
            ClientName = _agent.State.ProvisioningRetryClientName,
            CallbackId = _agent.State.ProvisioningRetryCallbackId,
            CallbackGeneration = _agent.State.ProvisioningRetryCallbackGeneration,
            FiredAtUnixMs = _agent.State.ProvisioningRetryDueUnixMs,
        }, _agent.State.ProvisioningRetryRedirectUris));

        _registrar.Calls.Should().HaveCount(2,
            "only the durable self-callback may re-enter DCR before the external due-time gate opens");
        _agent.State.ClientId.Should().Be("client-after-self-callback");
        _agent.State.ProvisioningRetryAttempt.Should().Be(0);
    }

    [Fact]
    public async Task HandleProvisioningRetryFired_RetriesAndClearsRetry_WhenCallbackMatches()
    {
        _registrar.ThrowOnRegister = new InvalidOperationException("nyxid unavailable");
        await _agent.HandleEnsureProvisioned(new EnsureAevatarOAuthClientProvisionedCommand
        {
            NyxidAuthority = "https://nyxid.test",
            RedirectUri = "https://aevatar.test/api/oauth/nyxid-callback",
            ClientName = "aevatar",
        });

        _registrar.ThrowOnRegister = null;
        _registrar.NextClientId = "client-after-retry";
        await _agent.HandleProvisioningRetryFired(new AevatarOAuthClientProvisioningRetryFiredEvent
        {
            Attempt = _agent.State.ProvisioningRetryAttempt,
            DueUnixMs = _agent.State.ProvisioningRetryDueUnixMs,
            NyxidAuthority = _agent.State.ProvisioningRetryAuthority,
            RedirectUri = _agent.State.ProvisioningRetryRedirectUri,
            ClientName = _agent.State.ProvisioningRetryClientName,
            CallbackId = _agent.State.ProvisioningRetryCallbackId,
            CallbackGeneration = _agent.State.ProvisioningRetryCallbackGeneration,
            FiredAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        });

        _registrar.Calls.Should().HaveCount(2);
        _agent.State.ClientId.Should().Be("client-after-retry");
        _agent.State.ProvisioningRetryAttempt.Should().Be(0);
        _agent.State.ProvisioningRetryCallbackId.Should().BeEmpty();
        (await ReadEventsAsync<AevatarOAuthClientProvisioningRetryClearedEvent>())
            .Should()
            .ContainSingle();
    }

    [Fact]
    public async Task HandleProvisioningRetryFired_IgnoresStaleCallback()
    {
        _registrar.ThrowOnRegister = new InvalidOperationException("nyxid unavailable");
        await _agent.HandleEnsureProvisioned(new EnsureAevatarOAuthClientProvisionedCommand
        {
            NyxidAuthority = "https://nyxid.test",
            RedirectUri = "https://aevatar.test/api/oauth/nyxid-callback",
        });

        _registrar.ThrowOnRegister = null;
        await _agent.HandleProvisioningRetryFired(new AevatarOAuthClientProvisioningRetryFiredEvent
        {
            Attempt = _agent.State.ProvisioningRetryAttempt + 1,
            DueUnixMs = _agent.State.ProvisioningRetryDueUnixMs,
            NyxidAuthority = _agent.State.ProvisioningRetryAuthority,
            RedirectUri = _agent.State.ProvisioningRetryRedirectUri,
            ClientName = _agent.State.ProvisioningRetryClientName,
            CallbackId = _agent.State.ProvisioningRetryCallbackId,
            CallbackGeneration = _agent.State.ProvisioningRetryCallbackGeneration,
        });

        _registrar.Calls.Should().ContainSingle("stale callback must not re-enter DCR");
        _agent.State.ClientId.Should().BeEmpty();
        _agent.State.ProvisioningRetryAttempt.Should().Be(1);
    }

    [Fact]
    public async Task HandleProvisioningRetryFired_DoublesBackoff_WhenRetryFailsAgain()
    {
        _registrar.ThrowOnRegister = new InvalidOperationException("first failure");
        await _agent.HandleEnsureProvisioned(new EnsureAevatarOAuthClientProvisionedCommand
        {
            NyxidAuthority = "https://nyxid.test",
            RedirectUri = "https://aevatar.test/api/oauth/nyxid-callback",
        });

        _registrar.ThrowOnRegister = new InvalidOperationException("second failure");
        await _agent.HandleProvisioningRetryFired(new AevatarOAuthClientProvisioningRetryFiredEvent
        {
            Attempt = _agent.State.ProvisioningRetryAttempt,
            DueUnixMs = _agent.State.ProvisioningRetryDueUnixMs,
            NyxidAuthority = _agent.State.ProvisioningRetryAuthority,
            RedirectUri = _agent.State.ProvisioningRetryRedirectUri,
            ClientName = _agent.State.ProvisioningRetryClientName,
            CallbackId = _agent.State.ProvisioningRetryCallbackId,
            CallbackGeneration = _agent.State.ProvisioningRetryCallbackGeneration,
        });

        _agent.State.ProvisioningRetryAttempt.Should().Be(2);
        _callbackScheduler.TimeoutRequests.Should().HaveCount(2);
        _callbackScheduler.TimeoutRequests[1].DueTime.Should().Be(TimeSpan.FromSeconds(10));
        (await ReadEventsAsync<AevatarOAuthClientProvisioningRetryScheduledEvent>())
            .Should()
            .HaveCount(2);
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
        // ProvisionAevatarOAuthClientCommand is shared by configured bootstrap,
        // operator repair, and tests that pin a known client_id.
        await _agent.HandleProvision(new ProvisionAevatarOAuthClientCommand
        {
            ClientId = "manual-fixture-client",
            ClientIdIssuedAtUnix = 1700000000,
            NyxidAuthority = "https://nyxid.test",
        });

        _agent.State.ClientId.Should().Be("manual-fixture-client");
        _agent.State.HmacKey.Length.Should().Be(0, "new writes leave the legacy plaintext field empty");
        _agent.State.HmacKeyRef.Should().NotBeNull("HMAC key seeds into the vault on first provision regardless of which command path triggered it");
        (await ResolveKeyAsync(_agent.State.HmacKeyRef!)).Should().HaveCount(32);
        _registrar.Calls.Should().BeEmpty("manual provision must not call DCR");
    }

    [Fact]
    public async Task HandleProvision_PersistsRedirectUriAndScope_FromCommand()
    {
        // Pin issue #549 operator-rebuild path: when ops calls
        // POST /api/oauth/aevatar-client/rebuild after a wedge, the actor
        // must persist redirect_uri and oauth_scope so the next bootstrap
        // pass observes no drift and does not re-DCR away the pinned
        // client_id.
        await _agent.HandleProvision(new ProvisionAevatarOAuthClientCommand
        {
            ClientId = "operator-rebuilt-client",
            ClientIdIssuedAtUnix = 1700000000,
            NyxidAuthority = "https://nyxid.test",
            RedirectUri = "https://aevatar.test/api/oauth/nyxid-callback",
            OauthScope = AevatarOAuthClientScopes.AuthorizationScope,
        });

        _agent.State.ClientId.Should().Be("operator-rebuilt-client");
        _agent.State.RedirectUri.Should().Be("https://aevatar.test/api/oauth/nyxid-callback");
        _agent.State.OauthScope.Should().Be(AevatarOAuthClientScopes.AuthorizationScope);
    }

    [Fact]
    public async Task HandleProvision_IsNoOp_WhenSnapshotMatches()
    {
        // Same-snapshot idempotency: client_id + authority + redirect_uri +
        // oauth_scope all unchanged → no Provisioned event written. Only
        // HMAC-seed event on first call (subsequent call is a full no-op).
        var cmd = new ProvisionAevatarOAuthClientCommand
        {
            ClientId = "client-x",
            NyxidAuthority = "https://nyxid.test",
            RedirectUri = "https://aevatar.test/api/oauth/nyxid-callback",
            OauthScope = AevatarOAuthClientScopes.AuthorizationScope,
        };

        await _agent.HandleProvision(cmd);
        var versionAfterFirst = _agent.EventSourcing!.CurrentVersion;

        await _agent.HandleProvision(cmd);

        _agent.EventSourcing!.CurrentVersion.Should().Be(versionAfterFirst,
            "matching snapshot must not append additional events");
    }

    [Fact]
    public async Task HandleProvision_PreservesRedirectUriAndScope_WhenCommandFieldsEmpty()
    {
        // PR #570 Codex P1: legacy / pre-redirect_uri callers (manual operator
        // scripts, fixtures using only ClientId + NyxidAuthority) must NOT
        // clobber previously-persisted redirect_uri / oauth_scope with empty
        // strings — that would let the next bootstrap pass observe an empty
        // redirect_uri, detect drift, and re-DCR the freshly-pinned client.
        // Empty cmd field = "field not supplied", not "set to empty".
        await _agent.HandleProvision(new ProvisionAevatarOAuthClientCommand
        {
            ClientId = "client-x",
            NyxidAuthority = "https://nyxid.test",
            RedirectUri = "https://aevatar.test/api/oauth/nyxid-callback",
            OauthScope = AevatarOAuthClientScopes.AuthorizationScope,
        });

        await _agent.HandleProvision(new ProvisionAevatarOAuthClientCommand
        {
            ClientId = "client-y",
            NyxidAuthority = "https://nyxid.test",
        });

        _agent.State.ClientId.Should().Be("client-y");
        _agent.State.RedirectUri.Should().Be(
            "https://aevatar.test/api/oauth/nyxid-callback",
            "empty cmd.RedirectUri must preserve existing state, not clear it");
        _agent.State.OauthScope.Should().Be(
            AevatarOAuthClientScopes.AuthorizationScope,
            "empty cmd.OauthScope must preserve existing state, not clear it");
    }

    [Fact]
    public async Task HandleProvision_RewritesEvent_WhenRedirectUriChanges()
    {
        // The same-snapshot check covers redirect_uri so an operator can
        // heal a wedged actor that has the right client_id but stale
        // redirect_uri without changing client_id. Pre-fix the check was
        // (client_id, authority) only and this case silently no-op'd —
        // leaving redirect_uri empty meant the next bootstrap detected
        // drift and re-DCR'd the pinned client_id away.
        await _agent.HandleProvision(new ProvisionAevatarOAuthClientCommand
        {
            ClientId = "client-x",
            NyxidAuthority = "https://nyxid.test",
        });
        _agent.State.RedirectUri.Should().BeEmpty();

        await _agent.HandleProvision(new ProvisionAevatarOAuthClientCommand
        {
            ClientId = "client-x",
            NyxidAuthority = "https://nyxid.test",
            RedirectUri = "https://aevatar.test/api/oauth/nyxid-callback",
            OauthScope = AevatarOAuthClientScopes.AuthorizationScope,
        });

        _agent.State.ClientId.Should().Be("client-x");
        _agent.State.RedirectUri.Should().Be("https://aevatar.test/api/oauth/nyxid-callback");
        _agent.State.OauthScope.Should().Be(AevatarOAuthClientScopes.AuthorizationScope);
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
        var seededKeyRef = _agent.State.HmacKeyRef;
        var seededKeyBytes = await ResolveKeyAsync(seededKeyRef!);
        var seededKid = _agent.State.HmacKid;
        seededKid.Should().Be(AevatarOAuthClientGAgent.InitialHmacKid);

        await _agent.HandleRotateHmacKey(new RotateAevatarOAuthClientHmacKeyCommand
        {
            IdempotencyKey = "rotation-request-alpha",
            ExpectedCurrentKid = seededKid,
        });

        // The rotated key is a fresh vault ref; [hmac_key] stays empty for new
        // writes. The resolved current bytes differ from the seeded key.
        _agent.State.HmacKey.Length.Should().Be(0);
        _agent.State.HmacKeyRef.Should().NotBeNull();
        _agent.State.HmacKeyRef.Should().NotBe(seededKeyRef);
        var rotatedKeyBytes = await ResolveKeyAsync(_agent.State.HmacKeyRef!);
        rotatedKeyBytes.Should().HaveCount(32);
        rotatedKeyBytes.Should().NotBeEquivalentTo(seededKeyBytes);
        _agent.State.ClientId.Should().Be("client-x");
        // Kid increments deterministically (v1 → v2) so verifiers can route
        // signed tokens to the right key.
        _agent.State.HmacKid.Should().Be("v2");
        // Previous key must be carried for the grace window so in-flight
        // state tokens signed with the old key still verify. On a ref-backed
        // rotation the previous key is demoted as a ref (the demoted seed ref).
        _agent.State.PreviousHmacKid.Should().Be(seededKid);
        _agent.State.PreviousHmacKey.Length.Should().Be(0);
        _agent.State.PreviousHmacKeyRef.Should().Be(seededKeyRef);
        (await ResolveKeyAsync(_agent.State.PreviousHmacKeyRef!)).Should().Equal(seededKeyBytes);
        _agent.State.PreviousHmacDemotedAtUnix.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task HandleRotateHmacKey_IsIdempotentForRetriedRequest()
    {
        await _agent.HandleProvision(new ProvisionAevatarOAuthClientCommand
        {
            ClientId = "client-x",
            NyxidAuthority = "https://nyxid.test",
        });
        var command = new RotateAevatarOAuthClientHmacKeyCommand
        {
            IdempotencyKey = "rotation-request-alpha",
            ExpectedCurrentKid = "v1",
        };

        await _agent.HandleRotateHmacKey(command);
        var rotated = _agent.State.Clone();
        var rotatedVersion = _agent.EventSourcing!.CurrentVersion;

        await _agent.HandleRotateHmacKey(command);

        _agent.State.Should().BeEquivalentTo(rotated);
        _agent.EventSourcing!.CurrentVersion.Should().Be(rotatedVersion);
        _agent.State.HmacKid.Should().Be("v2");
        _agent.State.PreviousHmacKid.Should().Be("v1");
    }

    [Fact]
    public async Task HandleRotateHmacKey_IgnoresRequestWhenExpectedKidIsStale()
    {
        await _agent.HandleProvision(new ProvisionAevatarOAuthClientCommand
        {
            ClientId = "client-x",
            NyxidAuthority = "https://nyxid.test",
        });
        var before = _agent.State.Clone();
        var beforeVersion = _agent.EventSourcing!.CurrentVersion;

        await _agent.HandleRotateHmacKey(new RotateAevatarOAuthClientHmacKeyCommand
        {
            IdempotencyKey = "rotation-request-stale",
            ExpectedCurrentKid = "v0",
        });

        _agent.State.Should().BeEquivalentTo(before);
        _agent.EventSourcing!.CurrentVersion.Should().Be(beforeVersion);
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
                OauthScope = AevatarOAuthClientScopes.AuthorizationScope,
                PersistedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            };
            peerEvent.RedirectUris.Add(resolvedRedirect);
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
    public async Task HandleEnsureProvisioned_SchedulesRetry_WhenPeerCommitDoesNotHealDrift()
    {
        // The OCC absorber must NOT swallow conflicts where the peer's
        // commit was something unrelated (e.g. a future schema event the
        // actor doesn't know about). In the actor-owned retry model this
        // schedules a durable callback so the actor re-evaluates against
        // fresh state on a later turn.
        await _agent.HandleEnsureProvisioned(new EnsureAevatarOAuthClientProvisionedCommand
        {
            NyxidAuthority = "https://nyxid.test",
            RedirectUri = "http://+:8080/api/oauth/nyxid-callback",
        });

        _registrar.NextClientId = "loser-orphan-client";
        _registrar.OnRegistered = async () =>
        {
            // Peer's commit observes broker capability. It is a real fact,
            // but it does NOT update RedirectUri. State stays drifted after
            // refresh, the absorber returns false, and actor-owned retry
            // captures the failed attempt.
            var store = _serviceProvider.GetRequiredService<IEventStore>();
            var actorId = _agent.Id;
            var current = await store.GetVersionAsync(actorId);
            var peerEvent = new AevatarOAuthClientBrokerCapabilityObservedEvent
            {
                ObservedAtUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
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
                        EventType = AevatarOAuthClientBrokerCapabilityObservedEvent.Descriptor.FullName,
                        EventData = Any.Pack(peerEvent),
                        Timestamp = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
                    },
                },
                current);
        };

        await _agent.HandleEnsureProvisioned(new EnsureAevatarOAuthClientProvisionedCommand
        {
            NyxidAuthority = "https://nyxid.test",
            RedirectUri = "https://aevatar-console-backend-api.aevatar.ai/api/oauth/nyxid-callback",
        });

        _agent.State.RedirectUri.Should().Be("http://+:8080/api/oauth/nyxid-callback");
        _agent.State.ProvisioningRetryAttempt.Should().Be(1);
        _agent.State.ProvisioningRetryRedirectUri.Should()
            .Be("https://aevatar-console-backend-api.aevatar.ai/api/oauth/nyxid-callback");
        _callbackScheduler.TimeoutRequests.Should().ContainSingle();
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
        _agent.State.PreviousHmacKeyRef.Should().BeNull();
        _agent.State.PreviousHmacKid.Should().BeNullOrEmpty();
    }

    [Fact]
    public async Task HandleProvision_LegacyPlaintextKey_RotatesToRefAndDemotesLegacyBytes()
    {
        // Dual-read migration: a legacy actor whose state carries a plaintext
        // [hmac_key] with no ref must rotate cleanly — the new key is a vault
        // ref and the demoted previous key keeps the legacy plaintext bytes so
        // in-flight tokens signed with it still verify during the grace window.
        await _agent.HandleProvision(new ProvisionAevatarOAuthClientCommand
        {
            ClientId = "legacy-client",
            NyxidAuthority = "https://nyxid.test",
        });
        // Simulate pre-migration state: plaintext key, no ref.
        var legacyKey = new byte[32];
        Array.Fill(legacyKey, (byte)0x55);
        _agent.State.HmacKey = Google.Protobuf.ByteString.CopyFrom(legacyKey);
        _agent.State.HmacKeyRef = null;

        await _agent.HandleRotateHmacKey(new RotateAevatarOAuthClientHmacKeyCommand
        {
            IdempotencyKey = "legacy-rotation-alpha",
            ExpectedCurrentKid = _agent.State.HmacKid,
        });

        _agent.State.HmacKeyRef.Should().NotBeNull("rotation stores the new key in the vault");
        _agent.State.HmacKey.Length.Should().Be(0, "new writes leave the legacy field empty");
        (await ResolveKeyAsync(_agent.State.HmacKeyRef!)).Should().HaveCount(32);
        // The demoted previous key is the legacy plaintext (no ref existed).
        _agent.State.PreviousHmacKeyRef.Should().BeNull();
        _agent.State.PreviousHmacKey.ToByteArray().Should().Equal(legacyKey);
        _agent.State.PreviousHmacDemotedAtUnix.Should().BeGreaterThan(0);
    }

    private sealed class RecordingDcrClient
    {
        public string NextClientId { get; set; } = "client-first";
        public List<(string Authority, string ClientName, string[] RedirectUris)> Calls { get; } = new();
        public Exception? ThrowOnRegister { get; set; }

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
                string authority, string clientName, IReadOnlyCollection<string> redirectUris, CancellationToken ct = default)
            {
                _owner.Calls.Add((authority, clientName, redirectUris.ToArray()));
                if (_owner.OnRegistered is not null)
                    await _owner.OnRegistered().ConfigureAwait(false);
                if (_owner.ThrowOnRegister is not null)
                    throw _owner.ThrowOnRegister;
                return new RegistrationResult(_owner.NextClientId, DateTimeOffset.UtcNow);
            }
        }

        private sealed class NoOpHandler : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
                throw new InvalidOperationException("HTTP client must not be invoked in unit tests");
        }
    }

    private static AevatarOAuthClientProvisioningRetryFiredEvent AddRedirectUris(
        AevatarOAuthClientProvisioningRetryFiredEvent evt,
        IEnumerable<string> redirectUris)
    {
        evt.RedirectUris.AddRange(redirectUris);
        return evt;
    }

    private async Task<byte[]> ResolveKeyAsync(SecretReference reference)
    {
        var result = await _secretVault.ResolveAsync(new ResolveSecretRequest(
            reference.Ref,
            CredentialSecretPurposes.OAuthStateTokenHmacKey,
            AevatarOAuthClientGAgent.WellKnownId,
            AevatarOAuthClientGAgent.WellKnownId,
            "test.resolve"));
        result.Resolved.Should().BeTrue("the actor must have stored the key under this ref");
        return Convert.FromBase64String(result.Secret!);
    }

    private async Task<IReadOnlyList<T>> ReadEventsAsync<T>()
        where T : class, Google.Protobuf.IMessage<T>, new()
    {
        var store = _serviceProvider.GetRequiredService<IEventStore>();
        var events = await store.GetEventsAsync(_agent.Id);
        return events
            .Select(e => e.EventData)
            .OfType<Any>()
            .Where(any => any.Is(new T().Descriptor))
            .Select(any => any.Unpack<T>())
            .ToList();
    }

    private static void SetActorId(GAgentBase agent, string id)
    {
        var method = typeof(GAgentBase).GetMethod("SetId", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("SetId not found on GAgentBase");
        method.Invoke(agent, new object[] { id });
    }

    // Refactor (iter97/cluster-097): Old pattern: tests injected a hidden
    // committed-state activation service and expected the already-provisioned
    // no-op branch to side-dispatch projection envelopes. New principle:
    // OAuth identity commands only preserve/commit actor facts; committed-state
    // hook/plan provider own materialization, and repair is explicit
    // maintenance/admin.
}
