using System.Reflection;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Credentials;
using Aevatar.Foundation.Abstractions.Credentials.Testing;
using Aevatar.Foundation.Abstractions.EventSourcing;
using Aevatar.Foundation.Abstractions.Persistence;
using Aevatar.Foundation.Abstractions.Runtime.Callbacks;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.GAgents.Channel.Identity;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Aevatar.GAgents.ChannelRuntime.Tests.Identity;

/// <summary>
/// Disaster-recovery rebuild path for a wiped AevatarOAuthClient current-state
/// readmodel: the cluster-singleton actor re-emits its <em>current</em> committed
/// state so a projection materializer rebuilds the document, WITHOUT appending a
/// new domain event. Mirrors <see cref="ExternalIdentityBindingRebuildTests"/>;
/// the actor contract is pinned via a capturing
/// <see cref="ICommittedStatePublicationHook"/>: the correct current state is
/// re-published, at the authoritative version, with no new event.
/// </summary>
public sealed class AevatarOAuthClientProjectionRebuildTests : IAsyncLifetime
{
    private const string ConfiguredClientId = "17cecaad-214b-4521-9dba-d435462e4095";

    private readonly CapturingCommittedStatePublicationHook _publications = new();
    private readonly OverrideableVersionEventStore _eventStore =
        new(new IdentityGAgentTestHarness.InMemoryEventStore());
    private AevatarOAuthClientGAgent _agent = null!;
    private ServiceProvider _serviceProvider = null!;

    public async Task InitializeAsync()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IEventStore>(_eventStore);
        services.AddSingleton<ISecretVault>(new InMemorySecretVault());
        services.AddSingleton<EventSourcingRuntimeOptions>();
        services.AddTransient(
            typeof(IEventSourcingBehaviorFactory<>),
            typeof(DefaultEventSourcingBehaviorFactory<>));
        services.AddSingleton<IActorRuntimeCallbackScheduler, IdentityGAgentTestHarness.NoopCallbackScheduler>();
        services.AddSingleton<ICommittedStatePublicationHook>(_publications);

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
    public async Task RebuildCommand_ReemitsCurrentCommittedState_WithoutAppendingEvent()
    {
        await _agent.HandleProvision(new ProvisionAevatarOAuthClientCommand
        {
            ClientId = ConfiguredClientId,
            ClientIdIssuedAtUnix = 1_700_000_000,
            NyxidAuthority = "https://nyx.example.test",
            RedirectUri = "https://api.example.test/api/oauth/nyxid-callback",
            OauthScope = AevatarOAuthClientScopes.AuthorizationScope,
        });
        var committedVersion = _agent.EventSourcing!.CurrentVersion;
        _publications.Captured.Clear();

        await _agent.HandleRebuildProjection(new RebuildAevatarOAuthClientProjectionCommand());

        _agent.EventSourcing!.CurrentVersion.Should().Be(
            committedVersion,
            "rebuild re-emits current state and must not append a projection-only no-op event");

        _publications.Captured.Should().ContainSingle();
        var published = _publications.Captured[0].Published;
        published.StateEvent.Version.Should().Be(
            committedVersion,
            "the readmodel is rebuilt at the authoritative committed version");
        published.StateEvent.EventData.Is(AevatarOAuthClientProvisionedEvent.Descriptor).Should().BeTrue(
            "the routing payload must activate the aevatar-oauth-client projection");
        var stateRoot = published.StateRoot.Unpack<AevatarOAuthClientState>();
        stateRoot.ClientId.Should().Be(ConfiguredClientId);
        stateRoot.HmacKeyRef.Should().NotBeNull("the surviving HMAC key reference must reach the rebuilt document");
    }

    [Fact]
    public async Task RebuildCommand_IsNoOp_WhenNoProvisionedClient()
    {
        await _agent.HandleRebuildProjection(new RebuildAevatarOAuthClientProjectionCommand());

        _publications.Captured.Should().BeEmpty("there is no surviving OAuth client to rebuild");
        _agent.EventSourcing!.CurrentVersion.Should().Be(0);
    }

    [Fact]
    public async Task RepairCommand_WhenExpectedVersionMatches_ReemitsExactCommittedState()
    {
        await _agent.HandleProvision(new ProvisionAevatarOAuthClientCommand
        {
            ClientId = ConfiguredClientId,
            NyxidAuthority = "https://nyx.example.test",
        });
        var committedVersion = _agent.EventSourcing!.CurrentVersion;
        _publications.Captured.Clear();

        await _agent.HandleRepairProjection(new RepairAevatarOAuthClientProjectionCommand
        {
            ExpectedStateVersion = committedVersion,
            RepairRequestId = "repair-1",
        });

        _agent.EventSourcing.CurrentVersion.Should().Be(committedVersion);
        _publications.Captured.Should().ContainSingle();
        _publications.Captured[0].Published.StateEvent.Version.Should().Be(committedVersion);
    }

    [Fact]
    public async Task RepairCommand_WhenActorIsNewerAndMatchesCommittedStore_ReemitsCurrentState()
    {
        await _agent.HandleProvision(new ProvisionAevatarOAuthClientCommand
        {
            ClientId = ConfiguredClientId,
            NyxidAuthority = "https://nyx.example.test",
        });
        var committedVersion = _agent.EventSourcing!.CurrentVersion;
        _publications.Captured.Clear();

        await _agent.HandleRepairProjection(new RepairAevatarOAuthClientProjectionCommand
        {
            ExpectedStateVersion = committedVersion - 1,
            RepairRequestId = "repair-1",
        });

        _publications.Captured.Should().ContainSingle();
        _publications.Captured[0].Published.StateEvent.Version.Should().Be(committedVersion);
        _agent.EventSourcing.CurrentVersion.Should().Be(committedVersion);
    }

    [Fact]
    public async Task RepairCommand_WhenActorIsOlderThanManifest_RejectsWithoutRepublish()
    {
        await _agent.HandleProvision(new ProvisionAevatarOAuthClientCommand
        {
            ClientId = ConfiguredClientId,
            NyxidAuthority = "https://nyx.example.test",
        });
        var committedVersion = _agent.EventSourcing!.CurrentVersion;
        _publications.Captured.Clear();

        var act = () => _agent.HandleRepairProjection(new RepairAevatarOAuthClientProjectionCommand
        {
            ExpectedStateVersion = committedVersion + 1,
            RepairRequestId = "repair-1",
        });

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*source version changed*");
        _publications.Captured.Should().BeEmpty();
        _agent.EventSourcing.CurrentVersion.Should().Be(committedVersion);
    }

    [Fact]
    public async Task RepairCommand_WhenActorIsAheadOfCommittedStore_RejectsWithoutRepublish()
    {
        await _agent.HandleProvision(new ProvisionAevatarOAuthClientCommand
        {
            ClientId = ConfiguredClientId,
            NyxidAuthority = "https://nyx.example.test",
        });
        var actorVersion = _agent.EventSourcing!.CurrentVersion;
        _publications.Captured.Clear();

        _eventStore.ReportedVersion = actorVersion - 1;
        try
        {
            var act = () => _agent.HandleRepairProjection(new RepairAevatarOAuthClientProjectionCommand
            {
                ExpectedStateVersion = actorVersion,
                RepairRequestId = "repair-1",
            });

            await act.Should().ThrowAsync<InvalidOperationException>()
                .WithMessage("*does not match the committed source*");
        }
        finally
        {
            _eventStore.ReportedVersion = null;
        }

        _publications.Captured.Should().BeEmpty();
        _agent.EventSourcing.CurrentVersion.Should().Be(actorVersion);
    }

    private static void SetActorId(GAgentBase agent, string id)
    {
        var method = typeof(GAgentBase).GetMethod("SetId", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("SetId not found on GAgentBase");
        method.Invoke(agent, new object[] { id });
    }

    private sealed class CapturingCommittedStatePublicationHook : ICommittedStatePublicationHook
    {
        public List<CommittedStatePublicationContext> Captured { get; } = new();

        public Task BeforePublishAsync(CommittedStatePublicationContext context, CancellationToken ct)
        {
            Captured.Add(context);
            return Task.CompletedTask;
        }
    }

    private sealed class OverrideableVersionEventStore(IEventStore inner) : IEventStore
    {
        public long? ReportedVersion { get; set; }

        public Task<long> GetVersionAsync(string agentId, CancellationToken ct = default) =>
            ReportedVersion.HasValue
                ? Task.FromResult(ReportedVersion.Value)
                : inner.GetVersionAsync(agentId, ct);

        public Task<EventStoreCommitResult> AppendAsync(
            string agentId,
            IEnumerable<StateEvent> events,
            long expectedVersion,
            CancellationToken ct = default) =>
            inner.AppendAsync(agentId, events, expectedVersion, ct);

        public Task<IReadOnlyList<StateEvent>> GetEventsAsync(
            string agentId,
            long? fromVersion = null,
            CancellationToken ct = default) =>
            inner.GetEventsAsync(agentId, fromVersion, ct);

        public Task<long> DeleteEventsUpToAsync(
            string agentId,
            long toVersion,
            CancellationToken ct = default) =>
            inner.DeleteEventsUpToAsync(agentId, toVersion, ct);
    }
}
