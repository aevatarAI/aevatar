using System.Reflection;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Runtime.Callbacks;
using Aevatar.Foundation.Abstractions.TypeSystem;
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.Foundation.Core.TypeSystem;
using Aevatar.GAgents.Registry;
using Aevatar.GAgentService.Abstractions.ScopeGAgents;
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

    [Fact]
    public async Task UnregistrationOperation_ShouldCommitRemovalBeforeCompletionAndDeduplicate()
    {
        const string registryActorId = "gagent-registry-scope-a";
        const string targetActorId = "nyxid-conversation-alpha";
        var state = StateWith((CanonicalKind, [targetActorId]));
        var eventSourcing = new RecordingEventSourcing(state);
        var committedAtDispatch = false;
        var dispatch = new RecordingDispatchPort(() =>
        {
            committedAtDispatch = eventSourcing.RaisedEvents
                .OfType<GAgentRegistryUnregistrationCommittedEvent>()
                .Count() == 1;
        });
        var agent = NewAgent(state, eventSourcing, dispatchPort: dispatch, actorId: registryActorId);
        var request = new GAgentRegistryUnregistrationRequest
        {
            OperationId = "registry-unregister-operation-alpha",
            RegistryActorId = registryActorId,
            ScopeId = "scope-a",
            AgentKind = CanonicalKind,
            ActorId = targetActorId,
            CompletionActorId = targetActorId,
        };

        await agent.HandleEventAsync(Envelope(registryActorId, request));
        await agent.HandleEventAsync(Envelope(registryActorId, request.Clone()));
        var changedCallback = request.Clone();
        changedCallback.CompletionActorId = "nyxid-conversation-other";
        await agent.HandleEventAsync(Envelope(registryActorId, changedCallback));

        committedAtDispatch.Should().BeTrue();
        eventSourcing.RaisedEvents.OfType<GAgentRegistryUnregistrationCommittedEvent>()
            .Should().ContainSingle();
        eventSourcing.RaisedEvents.OfType<GAgentRegistryUnregistrationCompletionDispatchAcceptedEvent>()
            .Should().ContainSingle();
        agent.State.Groups.Should().BeEmpty();
        dispatch.Calls.Should().HaveCount(2);
        dispatch.Calls.Should().OnlyContain(call => call.ActorId == request.CompletionActorId);
        var completion = dispatch.Calls[0].Envelope.Payload
            .Unpack<GAgentRegistryUnregistrationCompleted>();
        completion.OperationId.Should().Be(request.OperationId);
        completion.RegistryActorId.Should().Be(registryActorId);
        completion.ScopeId.Should().Be(request.ScopeId);
        completion.AgentKind.Should().Be(request.AgentKind);
        completion.ActorId.Should().Be(request.ActorId);
        completion.CompletionActorId.Should().Be(request.CompletionActorId);
        completion.Outcome.Should().Be(GAgentRegistryUnregistrationOutcome.CommittedRemoved);
        completion.CompletedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task UnregistrationOperation_ShouldCommitAuthoritativeAbsence()
    {
        const string registryActorId = "gagent-registry-scope-a";
        var state = StateWith((CanonicalKind, ["actor-other"]));
        var eventSourcing = new RecordingEventSourcing(state);
        var dispatch = new RecordingDispatchPort();
        var agent = NewAgent(state, eventSourcing, dispatchPort: dispatch, actorId: registryActorId);

        await agent.HandleEventAsync(Envelope(registryActorId, new GAgentRegistryUnregistrationRequest
        {
            OperationId = "registry-unregister-operation-absent",
            RegistryActorId = registryActorId,
            ScopeId = "scope-a",
            AgentKind = CanonicalKind,
            ActorId = "nyxid-conversation-alpha",
            CompletionActorId = "nyxid-conversation-alpha",
        }));

        eventSourcing.RaisedEvents.OfType<GAgentRegistryUnregistrationCommittedEvent>()
            .Should().ContainSingle();
        eventSourcing.RaisedEvents.OfType<GAgentRegistryUnregistrationCompletionDispatchAcceptedEvent>()
            .Should().ContainSingle();
        agent.State.Groups.Single().ActorIds.Should().ContainSingle("actor-other");
        dispatch.Calls.Should().ContainSingle();
        dispatch.Calls[0].Envelope.Payload.Unpack<GAgentRegistryUnregistrationCompleted>()
            .Outcome.Should().Be(GAgentRegistryUnregistrationOutcome.AuthoritativeAbsent);
    }

    [Fact]
    public async Task UnregistrationOperation_ShouldRejectMismatchedAuthorityTuples()
    {
        const string registryActorId = "gagent-registry-scope-a";
        const string targetActorId = "nyxid-conversation-alpha";
        var state = StateWith((CanonicalKind, [targetActorId]));
        var eventSourcing = new RecordingEventSourcing(state);
        var dispatch = new RecordingDispatchPort();
        var agent = NewAgent(state, eventSourcing, dispatchPort: dispatch, actorId: registryActorId);
        var valid = new GAgentRegistryUnregistrationRequest
        {
            OperationId = "registry-unregister-operation-alpha",
            RegistryActorId = registryActorId,
            ScopeId = "scope-a",
            AgentKind = CanonicalKind,
            ActorId = targetActorId,
            CompletionActorId = targetActorId,
        };
        var wrongRegistryActor = valid.Clone();
        wrongRegistryActor.RegistryActorId = "gagent-registry-scope-other";
        var wrongScope = valid.Clone();
        wrongScope.ScopeId = "scope-other";
        var unknownKind = valid.Clone();
        unknownKind.AgentKind = "tests.unknown-agent";
        var missingCompletion = valid.Clone();
        missingCompletion.CompletionActorId = string.Empty;
        var foreignCompletion = valid.Clone();
        foreignCompletion.CompletionActorId = "nyxid-conversation-foreign";

        await agent.HandleEventAsync(Envelope(registryActorId, wrongRegistryActor));
        await agent.HandleEventAsync(Envelope(registryActorId, wrongScope));
        await agent.HandleEventAsync(Envelope(registryActorId, unknownKind));
        await agent.HandleEventAsync(Envelope(registryActorId, missingCompletion));
        await agent.HandleEventAsync(Envelope(registryActorId, foreignCompletion));

        eventSourcing.RaisedEvents.Should().BeEmpty();
        agent.State.Should().BeEquivalentTo(state);
        dispatch.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task UnregistrationOperation_RejectedCompletionDispatch_ShouldRedeliverCommittedCompletion()
    {
        const string registryActorId = "gagent-registry-scope-a";
        const string targetActorId = "nyxid-conversation-alpha";
        var state = StateWith((CanonicalKind, [targetActorId]));
        var eventSourcing = new RecordingEventSourcing(state);
        var dispatch = new RecordingDispatchPort { Accepted = false };
        var agent = NewAgent(state, eventSourcing, dispatchPort: dispatch, actorId: registryActorId);
        var request = new GAgentRegistryUnregistrationRequest
        {
            OperationId = "registry-unregister-operation-redelivery",
            RegistryActorId = registryActorId,
            ScopeId = "scope-a",
            AgentKind = CanonicalKind,
            ActorId = targetActorId,
            CompletionActorId = targetActorId,
        };

        var first = () => agent.HandleEventAsync(Envelope(registryActorId, request));

        await first.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*completion dispatch was rejected*");
        eventSourcing.RaisedEvents.Should().ContainSingle();
        agent.State.Groups.Should().BeEmpty();
        dispatch.Accepted = true;

        await agent.HandleEventAsync(Envelope(registryActorId, request.Clone()));

        eventSourcing.RaisedEvents.OfType<GAgentRegistryUnregistrationCommittedEvent>()
            .Should().ContainSingle();
        eventSourcing.RaisedEvents.OfType<GAgentRegistryUnregistrationCompletionDispatchAcceptedEvent>()
            .Should().ContainSingle();
        dispatch.Calls.Should().HaveCount(2);
        dispatch.Calls[1].Envelope.Payload.Unpack<GAgentRegistryUnregistrationCompleted>()
            .Should().BeEquivalentTo(
                dispatch.Calls[0].Envelope.Payload.Unpack<GAgentRegistryUnregistrationCompleted>());
    }

    [Fact]
    public async Task UnregistrationOperationRetention_ShouldStayBoundedAndPinRejectedCompletionForRedrive()
    {
        const int retentionLimit = 256;
        const string registryActorId = "gagent-registry-scope-a";
        const string pinnedActorId = "nyxid-conversation-pinned";
        var state = StateWith();
        var eventSourcing = new RecordingEventSourcing(state);
        var dispatch = new RecordingDispatchPort { Accepted = false };
        var agent = NewAgent(state, eventSourcing, dispatchPort: dispatch, actorId: registryActorId);
        var pinned = new GAgentRegistryUnregistrationRequest
        {
            OperationId = "registry-unregister-pinned",
            RegistryActorId = registryActorId,
            ScopeId = "scope-a",
            AgentKind = CanonicalKind,
            ActorId = pinnedActorId,
            CompletionActorId = pinnedActorId,
        };

        await ((Func<Task>)(() => agent.HandleEventAsync(Envelope(registryActorId, pinned))))
            .Should().ThrowAsync<InvalidOperationException>();
        dispatch.Accepted = true;
        for (var index = 0; index < retentionLimit; index++)
        {
            var actorId = $"nyxid-conversation-{index:D3}";
            await agent.HandleEventAsync(Envelope(registryActorId, new GAgentRegistryUnregistrationRequest
            {
                OperationId = $"registry-unregister-{index:D3}",
                RegistryActorId = registryActorId,
                ScopeId = "scope-a",
                AgentKind = CanonicalKind,
                ActorId = actorId,
                CompletionActorId = actorId,
            }));
        }

        agent.State.UnregistrationOperations.Should().HaveCount(retentionLimit);
        agent.State.UnregistrationOperations.Should().ContainKey(pinned.OperationId);
        agent.State.UnregistrationOperations.Should().ContainKey(
            $"registry-unregister-{retentionLimit - 1:D3}");
        var firstCompletion = dispatch.Calls[0].Envelope.Payload
            .Unpack<GAgentRegistryUnregistrationCompleted>();

        await agent.HandleEventAsync(Envelope(registryActorId, pinned.Clone()));

        dispatch.Calls[^1].Envelope.Payload.Unpack<GAgentRegistryUnregistrationCompleted>()
            .Should().BeEquivalentTo(firstCompletion);
    }

    [Fact]
    public async Task UnregistrationOperationRetention_WhenAllCallbacksArePending_ShouldRejectOverflow()
    {
        const int retentionLimit = 256;
        const string registryActorId = "gagent-registry-scope-a";
        var state = StateWith();
        var eventSourcing = new RecordingEventSourcing(state);
        var dispatch = new RecordingDispatchPort { Accepted = false };
        var agent = NewAgent(state, eventSourcing, dispatchPort: dispatch, actorId: registryActorId);

        for (var index = 0; index < retentionLimit; index++)
        {
            var actorId = $"nyxid-conversation-pending-{index:D3}";
            try
            {
                await agent.HandleEventAsync(Envelope(registryActorId, new GAgentRegistryUnregistrationRequest
                {
                    OperationId = $"registry-unregister-pending-{index:D3}",
                    RegistryActorId = registryActorId,
                    ScopeId = "scope-a",
                    AgentKind = CanonicalKind,
                    ActorId = actorId,
                    CompletionActorId = actorId,
                }));
            }
            catch (InvalidOperationException exception) when (
                exception.Message.Contains("completion dispatch was rejected", StringComparison.Ordinal))
            {
            }
        }

        var overflowActorId = "nyxid-conversation-pending-overflow";
        var overflow = () => agent.HandleEventAsync(Envelope(
            registryActorId,
            new GAgentRegistryUnregistrationRequest
            {
                OperationId = "registry-unregister-pending-overflow",
                RegistryActorId = registryActorId,
                ScopeId = "scope-a",
                AgentKind = CanonicalKind,
                ActorId = overflowActorId,
                CompletionActorId = overflowActorId,
            }));

        await overflow.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*retention capacity*");
        agent.State.UnregistrationOperations.Should().HaveCount(retentionLimit);
        agent.State.UnregistrationOperations.Should().ContainKey("registry-unregister-pending-000");
        agent.State.UnregistrationOperations.Should().NotContainKey("registry-unregister-pending-overflow");
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
        IActorKindProbe? probe = null,
        IActorDispatchPort? dispatchPort = null,
        string actorId = "gagent-registry-scope-a")
    {
        var serviceCollection = new ServiceCollection()
            .AddSingleton<IAgentKindRegistry>(BuildRegistry())
            .AddSingleton<IActorRuntimeCallbackScheduler>(new NoOpCallbackScheduler());
        if (probe is not null)
            serviceCollection.AddSingleton(probe);
        if (dispatchPort is not null)
            serviceCollection.AddSingleton(dispatchPort);
        var services = serviceCollection.BuildServiceProvider();

        var agent = new GAgentRegistryGAgent
        {
            Services = services,
            EventSourcing = eventSourcing,
        };
        typeof(Aevatar.Foundation.Core.GAgentBase)
            .GetMethod("SetId", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(agent, [actorId]);
        GAgentRegistryStateSetter.Set(agent, state);
        return agent;
    }

    private static EventEnvelope Envelope(string actorId, IMessage payload) => new()
    {
        Id = Guid.NewGuid().ToString("N"),
        Timestamp = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
        Payload = Google.Protobuf.WellKnownTypes.Any.Pack(payload),
        Route = EnvelopeRouteSemantics.CreateDirect("test", actorId),
    };

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
        public List<IMessage> RaisedEvents { get; } = [];
        public long CurrentVersion => 0;

        public void RaiseEvent<TEvent>(TEvent evt) where TEvent : IMessage =>
            RaisedEvents.Add(evt);

        public Task<EventStoreCommitResult> ConfirmEventsAsync(CancellationToken ct = default) =>
            Task.FromResult(new EventStoreCommitResult());

        public Task PersistSnapshotAsync(GAgentRegistryState currentState, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task<GAgentRegistryState?> ReplayAsync(string agentId, CancellationToken ct = default) =>
            Task.FromResult<GAgentRegistryState?>(initialState.Clone());

        public void DiscardPendingEvents() =>
            RaisedEvents.Clear();

        public GAgentRegistryState TransitionState(GAgentRegistryState current, IMessage evt) =>
            _applier.Apply(current, evt);
    }

    private sealed class RecordingDispatchPort(Action? onDispatch = null) : IActorDispatchPort
    {
        public List<(string ActorId, EventEnvelope Envelope)> Calls { get; } = [];
        public bool Accepted { get; set; } = true;

        public Task<DispatchAdmission> DispatchAsync(
            string actorId,
            EventEnvelope envelope,
            CancellationToken ct = default)
        {
            Calls.Add((actorId, envelope.Clone()));
            onDispatch?.Invoke();
            var admission = DispatchAdmissionFactory.Create(actorId, envelope);
            return Task.FromResult(admission with { Accepted = Accepted });
        }
    }

    private sealed class NoOpCallbackScheduler : IActorRuntimeCallbackScheduler
    {
        public Task<RuntimeCallbackLease> ScheduleTimeoutAsync(
            RuntimeCallbackTimeoutRequest request,
            CancellationToken ct = default) =>
            Task.FromResult(new RuntimeCallbackLease(
                request.ActorId,
                request.CallbackId,
                1,
                RuntimeCallbackBackend.InMemory));

        public Task<RuntimeCallbackLease> ScheduleTimerAsync(
            RuntimeCallbackTimerRequest request,
            CancellationToken ct = default) =>
            Task.FromResult(new RuntimeCallbackLease(
                request.ActorId,
                request.CallbackId,
                1,
                RuntimeCallbackBackend.InMemory));

        public Task CancelAsync(RuntimeCallbackLease lease, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task PurgeActorAsync(string actorId, CancellationToken ct = default) =>
            Task.CompletedTask;
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
