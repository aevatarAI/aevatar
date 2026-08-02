using System.Reflection;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.TypeSystem;
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.Foundation.Core.TypeSystem;
using Aevatar.GAgents.Registry;
using FluentAssertions;
using Google.Protobuf;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.Studio.Tests;

public sealed class GAgentRegistryGAgentTests
{
    private const string CanonicalKind = "tests.registry-agent";
    private const string OtherKind = "tests.other-agent";
    private const string LegacyKey = "Legacy.Registry.Agent, Tests";

    [Fact]
    public async Task Admission_ShouldCanonicalizeSingleLegacyGroup_WhenProbeConfirmsRequestedKind()
    {
        var state = StateWith((LegacyKey, ["actor-1"]));
        var eventSourcing = new RecordingEventSourcing(state);
        var probe = new RecordingActorKindProbe { RuntimeKind = CanonicalKind };
        var agent = NewAgent(state, eventSourcing, probe);

        await agent.HandleScopeResourceAdmissionRequested(new ScopeResourceAdmissionRequested
        {
            ScopeId = "scope-a",
            AgentKind = CanonicalKind,
            ActorId = "actor-1",
            Operation = GAgentRegistryOperation.Use,
        });

        var canonicalized = eventSourcing.RaisedEvents.Should().ContainSingle().Subject
            .Should().BeOfType<ActorRegistrationKeyCanonicalizedEvent>().Subject;
        canonicalized.PreviousRegistryKey.Should().Be(LegacyKey);
        canonicalized.AgentKind.Should().Be(CanonicalKind);
        canonicalized.ActorId.Should().Be("actor-1");
        agent.State.Groups.Should().ContainSingle()
            .Which.Should().BeEquivalentTo(new GAgentRegistryEntry
            {
                AgentKind = CanonicalKind,
                ActorIds = { "actor-1" },
            });
        probe.Calls.Should().ContainSingle().Which.Should().Be("actor-1");
    }

    [Theory]
    [MemberData(nameof(UnmappableLegacyRows))]
    public async Task Admission_ShouldNotCanonicalizeUnmappableLegacyRows(
        GAgentRegistryState state,
        string? runtimeKind,
        bool includeProbe)
    {
        var eventSourcing = new RecordingEventSourcing(state);
        var probe = includeProbe
            ? new RecordingActorKindProbe { RuntimeKind = runtimeKind }
            : null;
        var agent = NewAgent(state, eventSourcing, probe);

        var act = () => agent.HandleScopeResourceAdmissionRequested(new ScopeResourceAdmissionRequested
        {
            ScopeId = "scope-a",
            AgentKind = CanonicalKind,
            ActorId = "actor-1",
            Operation = GAgentRegistryOperation.Use,
        });

        await act.Should().ThrowAsync<GAgentRegistryAdmissionNotFoundException>();
        eventSourcing.RaisedEvents.Should().BeEmpty();
        agent.State.Should().BeEquivalentTo(state);
    }

    [Fact]
    public async Task Admission_ShouldNotCanonicalize_WhenProbeThrows()
    {
        var state = StateWith((LegacyKey, ["actor-1"]));
        var eventSourcing = new RecordingEventSourcing(state);
        var probe = new RecordingActorKindProbe
        {
            Failure = new InvalidOperationException("probe unavailable"),
        };
        var agent = NewAgent(state, eventSourcing, probe);

        var act = () => agent.HandleScopeResourceAdmissionRequested(new ScopeResourceAdmissionRequested
        {
            ScopeId = "scope-a",
            AgentKind = CanonicalKind,
            ActorId = "actor-1",
            Operation = GAgentRegistryOperation.Use,
        });

        await act.Should().ThrowAsync<GAgentRegistryAdmissionNotFoundException>();
        eventSourcing.RaisedEvents.Should().BeEmpty();
        agent.State.Should().BeEquivalentTo(state);
        probe.Calls.Should().ContainSingle().Which.Should().Be("actor-1");
    }

    [Fact]
    public async Task Register_ShouldRemoveSameActorFromLegacyGroups()
    {
        var state = StateWith(
            (LegacyKey, ["actor-1", "actor-2"]),
            (OtherKind, ["actor-1"]));
        var eventSourcing = new RecordingEventSourcing(state);
        var agent = NewAgent(state, eventSourcing);

        await agent.HandleActorRegistered(new ActorRegisteredEvent
        {
            AgentKind = CanonicalKind,
            ActorId = "actor-1",
        });

        eventSourcing.RaisedEvents.Should().ContainSingle()
            .Which.Should().BeEquivalentTo(new ActorRegisteredEvent
            {
                AgentKind = CanonicalKind,
                ActorId = "actor-1",
                RemovedLegacyKeys = { LegacyKey },
            });
        agent.State.Groups.Single(g => g.AgentKind == CanonicalKind).ActorIds.Should().ContainSingle("actor-1");
        agent.State.Groups.Single(g => g.AgentKind == LegacyKey).ActorIds.Should().ContainSingle("actor-2");
        agent.State.Groups.Single(g => g.AgentKind == OtherKind).ActorIds.Should().ContainSingle("actor-1");
    }

    [Fact]
    public void Replay_ShouldBeDeterministic_WhenRegistryShapeChanges()
    {
        var initial = StateWith(
            (LegacyKey, ["actor-1", "actor-2"]),
            (OtherKind, ["actor-1"]));
        var stream = new IMessage[]
        {
            new ActorRegisteredEvent
            {
                AgentKind = CanonicalKind,
                ActorId = "actor-1",
                RemovedLegacyKeys = { LegacyKey },
            },
        };

        var withoutRegistry = Replay(initial, stream, new ServiceCollection().BuildServiceProvider());
        var legacyKeyNowRegistered = Replay(
            initial,
            stream,
            new ServiceCollection()
                .AddSingleton<IAgentKindRegistry>(BuildRegistry(includeLegacyKey: true))
                .BuildServiceProvider());

        withoutRegistry.Should().BeEquivalentTo(legacyKeyNowRegistered);
        withoutRegistry.Groups.Single(g => g.AgentKind == CanonicalKind).ActorIds.Should().ContainSingle("actor-1");
        withoutRegistry.Groups.Single(g => g.AgentKind == LegacyKey).ActorIds.Should().ContainSingle("actor-2");
        withoutRegistry.Groups.Single(g => g.AgentKind == OtherKind).ActorIds.Should().ContainSingle("actor-1");
    }

    public static TheoryData<GAgentRegistryState, string?, bool> UnmappableLegacyRows() =>
        new()
        {
            { StateWith((OtherKind, ["actor-1"])), CanonicalKind, true },
            { StateWith(("Legacy.One, Tests", ["actor-1"]), ("Legacy.Two, Tests", ["actor-1"])), CanonicalKind, true },
            { StateWith((LegacyKey, ["actor-1"])), null, true },
            { StateWith((LegacyKey, ["actor-1"])), OtherKind, true },
            { StateWith((LegacyKey, ["actor-1"])), CanonicalKind, false },
        };

    private static GAgentRegistryGAgent NewAgent(
        GAgentRegistryState state,
        RecordingEventSourcing eventSourcing,
        IActorKindProbe? probe = null)
    {
        var services = new ServiceCollection()
            .AddSingleton<IAgentKindRegistry>(BuildRegistry())
            .BuildServiceProvider();
        if (probe is not null)
        {
            services = new ServiceCollection()
                .AddSingleton<IAgentKindRegistry>(BuildRegistry())
                .AddSingleton(probe)
                .BuildServiceProvider();
        }

        var agent = new GAgentRegistryGAgent
        {
            Services = services,
            EventSourcing = eventSourcing,
        };
        GAgentRegistryStateSetter.Set(agent, state);
        return agent;
    }

    private static IAgentKindRegistry BuildRegistry(bool includeLegacyKey = false)
    {
        var registrations = new List<AgentRegistration>
        {
            new(CanonicalKind, typeof(TestRegistryAgent), typeof(object)),
            new(OtherKind, typeof(OtherRegistryAgent), typeof(object)),
        };

        if (includeLegacyKey)
            registrations.Add(new AgentRegistration(LegacyKey, typeof(LegacyKeyRegistryAgent), typeof(object)));

        return new AgentKindRegistry(registrations);
    }

    private static GAgentRegistryState Replay(
        GAgentRegistryState initial,
        IEnumerable<IMessage> stream,
        IServiceProvider services)
    {
        var applier = new GAgentRegistryStateApplier(services);
        var state = initial.Clone();
        foreach (var evt in stream)
            state = applier.Apply(state, evt);

        return state;
    }

    private static GAgentRegistryState StateWith(params (string AgentKind, string[] ActorIds)[] groups)
    {
        var state = new GAgentRegistryState();
        foreach (var (agentKind, actorIds) in groups)
        {
            state.Groups.Add(new GAgentRegistryEntry
            {
                AgentKind = agentKind,
                ActorIds = { actorIds },
            });
        }

        return state;
    }

    private sealed class RecordingActorKindProbe : IActorKindProbe
    {
        public string? RuntimeKind { get; init; }
        public Exception? Failure { get; init; }
        public List<string> Calls { get; } = [];

        public Task<string?> GetRuntimeAgentKindAsync(string actorId, CancellationToken ct = default)
        {
            Calls.Add(actorId);
            if (Failure is not null)
                throw Failure;
            return Task.FromResult(RuntimeKind);
        }
    }

    private sealed class RecordingEventSourcing(GAgentRegistryState initialState)
        : IEventSourcingBehavior<GAgentRegistryState>
    {
        private readonly GAgentRegistryStateApplier _applier = new();
        private readonly List<IMessage> _pending = [];
        public List<IMessage> RaisedEvents { get; } = [];
        public long CurrentVersion { get; private set; }

        public void RaiseEvent<TEvent>(TEvent evt) where TEvent : IMessage
        {
            RaisedEvents.Add(evt);
            _pending.Add(evt);
        }

        public Task<EventStoreCommitResult> ConfirmEventsAsync(CancellationToken ct = default)
        {
            var result = EventSourcingTestCommit.From(_pending, CurrentVersion);
            CurrentVersion = result.LatestVersion;
            _pending.Clear();
            return Task.FromResult(result);
        }

        public Task PersistSnapshotAsync(GAgentRegistryState currentState, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task<GAgentRegistryState?> ReplayAsync(string agentId, CancellationToken ct = default) =>
            Task.FromResult<GAgentRegistryState?>(initialState.Clone());

        public void DiscardPendingEvents()
        {
            RaisedEvents.Clear();
            _pending.Clear();
        }

        public GAgentRegistryState TransitionState(GAgentRegistryState current, IMessage evt) =>
            _applier.Apply(current, evt);
    }

    private sealed class GAgentRegistryStateApplier
    {
        private static readonly MethodInfo TransitionStateMethod =
            typeof(GAgentRegistryGAgent)
                .GetMethod("TransitionState", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("TransitionState method not found.");

        private readonly GAgentRegistryGAgent _agent;

        public GAgentRegistryStateApplier()
            : this(NewDefaultServiceProvider())
        {
        }

        public GAgentRegistryStateApplier(IServiceProvider services)
        {
            _agent = NewAgentWithoutEventSourcing(services);
        }

        public GAgentRegistryState Apply(GAgentRegistryState current, IMessage evt)
        {
            var result = TransitionStateMethod.Invoke(_agent, [current, evt])
                ?? throw new InvalidOperationException("TransitionState returned null.");
            return (GAgentRegistryState)result;
        }
    }

    private static IServiceProvider NewDefaultServiceProvider() =>
        new ServiceCollection()
            .AddSingleton<IAgentKindRegistry>(BuildRegistry())
            .BuildServiceProvider();

    private static GAgentRegistryGAgent NewAgentWithoutEventSourcing(IServiceProvider services) =>
        new() { Services = services };

    private static class GAgentRegistryStateSetter
    {
        private static readonly FieldInfo StateField =
            typeof(GAgentRegistryGAgent).BaseType!
                .GetField("_state", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("GAgent state field not found.");

        public static void Set(GAgentRegistryGAgent agent, GAgentRegistryState state) =>
            StateField.SetValue(agent, state.Clone());
    }

    [GAgent(CanonicalKind)]
    private sealed class TestRegistryAgent : IAgent
    {
        public string Id { get; } = "test-registry-agent";
        public Task ActivateAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task<string> GetDescriptionAsync() => Task.FromResult(string.Empty);
        public Task<IReadOnlyList<System.Type>> GetSubscribedEventTypesAsync() =>
            Task.FromResult<IReadOnlyList<System.Type>>([]);
        public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default) => Task.CompletedTask;
    }

    [GAgent(OtherKind)]
    private sealed class OtherRegistryAgent : IAgent
    {
        public string Id { get; } = "other-registry-agent";
        public Task ActivateAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task<string> GetDescriptionAsync() => Task.FromResult(string.Empty);
        public Task<IReadOnlyList<System.Type>> GetSubscribedEventTypesAsync() =>
            Task.FromResult<IReadOnlyList<System.Type>>([]);
        public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default) => Task.CompletedTask;
    }

    [GAgent(LegacyKey)]
    private sealed class LegacyKeyRegistryAgent : IAgent
    {
        public string Id { get; } = "legacy-key-registry-agent";
        public Task ActivateAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task<string> GetDescriptionAsync() => Task.FromResult(string.Empty);
        public Task<IReadOnlyList<System.Type>> GetSubscribedEventTypesAsync() =>
            Task.FromResult<IReadOnlyList<System.Type>>([]);
        public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default) => Task.CompletedTask;
    }
}
