using Aevatar.Foundation.Abstractions.EventSourcing;
using Aevatar.Foundation.Abstractions.Persistence;
using Aevatar.Foundation.Abstractions.Runtime.Callbacks;
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Channel.Identity;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Aevatar.GAgents.ChannelRuntime.Tests.Identity;

/// <summary>
/// Disaster-recovery rebuild path for a wiped ExternalIdentityBinding current-state
/// readmodel: the actor re-emits its <em>current</em> committed state so a projection
/// materializer rebuilds the row, WITHOUT appending a new domain event. Downstream
/// materialization of that committed-state publication into the readmodel is covered by
/// <see cref="ChannelIdentityOrleansDispatchProjectionTests"/>; here we pin the actor
/// contract via a capturing <see cref="ICommittedStatePublicationHook"/>: the correct
/// current state is re-published, at the authoritative version, with no new event.
/// </summary>
public sealed class ExternalIdentityBindingRebuildTests : IAsyncLifetime
{
    private readonly CapturingCommittedStatePublicationHook _publications = new();
    private ExternalIdentityBindingGAgent _agent = null!;
    private ServiceProvider _serviceProvider = null!;

    public async Task InitializeAsync()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IEventStore, IdentityGAgentTestHarness.InMemoryEventStore>();
        services.AddSingleton<EventSourcingRuntimeOptions>();
        services.AddTransient(
            typeof(IEventSourcingBehaviorFactory<>),
            typeof(DefaultEventSourcingBehaviorFactory<>));
        services.AddSingleton<IActorRuntimeCallbackScheduler, IdentityGAgentTestHarness.NoopCallbackScheduler>();
        services.AddSingleton<ICommittedStatePublicationHook>(_publications);

        _serviceProvider = services.BuildServiceProvider();

        _agent = new ExternalIdentityBindingGAgent
        {
            Services = _serviceProvider,
            EventSourcingBehaviorFactory =
                _serviceProvider.GetRequiredService<IEventSourcingBehaviorFactory<ExternalIdentityBindingState>>(),
        };

        await _agent.ActivateAsync();
    }

    public Task DisposeAsync()
    {
        _serviceProvider.Dispose();
        return Task.CompletedTask;
    }

    private static ExternalSubjectRef Subject() => new()
    {
        Platform = "lark",
        Tenant = "ou_tenant_x",
        ExternalUserId = "ou_user_y",
    };

    [Fact]
    public async Task RebuildCommand_ReemitsCurrentCommittedState_WithoutAppendingEvent()
    {
        await _agent.HandleCommitBinding(new CommitBindingCommand
        {
            ExternalSubject = Subject(),
            BindingId = "bnd_first",
            OwnerScopeId = "owner-user-1",
        });
        var committedVersion = _agent.EventSourcing!.CurrentVersion;
        _publications.Captured.Clear();

        await _agent.HandleRebuildBindingProjection(new RebuildBindingProjectionCommand
        {
            ExternalSubject = Subject(),
        });

        _agent.EventSourcing!.CurrentVersion.Should().Be(
            committedVersion,
            "rebuild re-emits current state and must not append a projection-only no-op event");

        _publications.Captured.Should().ContainSingle();
        var published = _publications.Captured[0].Published;
        published.StateEvent.Version.Should().Be(
            committedVersion,
            "the readmodel is rebuilt at the authoritative committed version");
        published.StateEvent.EventData.Is(ExternalIdentityBoundEvent.Descriptor).Should().BeTrue(
            "the routing payload must activate the binding projection");
        published.StateRoot.Unpack<ExternalIdentityBindingState>().BindingId.Should().Be("bnd_first");
    }

    [Fact]
    public async Task DuplicateCommit_SelfHealsWipedReadmodel_WithoutAppendingEvent()
    {
        await _agent.HandleCommitBinding(new CommitBindingCommand
        {
            ExternalSubject = Subject(),
            BindingId = "bnd_first",
            OwnerScopeId = "owner-user-1",
        });
        var committedVersion = _agent.EventSourcing!.CurrentVersion;
        _publications.Captured.Clear();

        // A re-auth after a projection-store wipe re-dispatches CommitBindingCommand:
        // the identity fact is unchanged (no event appended), but the actor re-emits its
        // current state so the wiped readmodel row is rebuilt instead of dead-locking on
        // the idempotent discard.
        await _agent.HandleCommitBinding(new CommitBindingCommand
        {
            ExternalSubject = Subject(),
            BindingId = "bnd_second",
            OwnerScopeId = "owner-user-1",
        });

        _agent.EventSourcing!.CurrentVersion.Should().Be(committedVersion);
        _publications.Captured.Should().ContainSingle();
        _publications.Captured[0].Published.StateRoot
            .Unpack<ExternalIdentityBindingState>().BindingId.Should().Be(
                "bnd_first",
                "self-heal re-emits the surviving binding, not the discarded incoming one");
    }

    [Fact]
    public async Task RebuildCommand_IsNoOp_WhenNoActiveBinding()
    {
        await _agent.HandleRebuildBindingProjection(new RebuildBindingProjectionCommand
        {
            ExternalSubject = Subject(),
        });

        _publications.Captured.Should().BeEmpty("there is no surviving binding to rebuild");
        _agent.EventSourcing!.CurrentVersion.Should().Be(0);
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
}
