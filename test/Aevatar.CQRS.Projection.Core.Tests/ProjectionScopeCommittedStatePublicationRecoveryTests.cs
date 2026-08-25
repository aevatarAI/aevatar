using System.Reflection;
using Aevatar.CQRS.Projection.Core.Abstractions.Orchestration;
using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.EventSourcing;
using Aevatar.Foundation.Abstractions.Persistence;
using Aevatar.Foundation.Abstractions.Runtime.Callbacks;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Core.EventSourcing;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.CQRS.Projection.Core.Tests;

public sealed class ProjectionScopeCommittedStatePublicationRecoveryTests
{
    private const int MaxPublicationBytes = 1024 * 1024;
    private const int FailureCount = 8;
    private const int FailureEnvelopeBytes = 120 * 1024;

    [Fact]
    public async Task Activation_ShouldRecoverOversizedPublication_AndReplayTerminalObservation()
    {
        const string actorId = "projection.durable.scope:publication-recovery";
        var eventStore = new TestEventStore();
        var publicationStore = new TestPublicationStateStore();
        using var firstServices = BuildServices(
            eventStore,
            publicationStore,
            registerRedactionHook: false);
        var failedPublisher = CreateSizeLimitedPublisher(MaxPublicationBytes);
        var firstEvents = new RecordingEventPublisher(actorId);
        var first = CreateAgent(actorId, firstServices, failedPublisher.Instance, firstEvents);
        await first.ActivateAsync();
        await first.CommitStartedAsync();
        failedPublisher.Proxy.AttemptedSizes.Clear();
        failedPublisher.Proxy.Accepted.Clear();

        for (var index = 1; index < FailureCount; index++)
            await first.CommitFailureAsync(BuildFailureEvent(index, BuildFailureEnvelope(index)));

        var finalFailure = BuildFailureEvent(FailureCount, BuildFailureEnvelope(FailureCount));
        await FluentActions.Invoking(() => first.CommitFailureAsync(finalFailure))
            .Should().ThrowAsync<CommittedStatePublicationException>();

        var committedEvents = await eventStore.GetEventsAsync(actorId);
        committedEvents.Should().HaveCount(FailureCount + 1);
        var committed = committedEvents[^1];
        first.State.Failures.Should().HaveCount(FailureCount);
        first.State.Failures.Should().OnlyContain(failure =>
            failure.Envelope != null && failure.Envelope.CalculateSize() < MaxPublicationBytes);
        failedPublisher.Proxy.AttemptedSizes.Should().HaveCount(FailureCount);
        failedPublisher.Proxy.AttemptedSizes.Take(FailureCount - 1)
            .Should().OnlyContain(size => size < MaxPublicationBytes);
        failedPublisher.Proxy.AttemptedSizes[^1].Should().BeGreaterThan(MaxPublicationBytes);
        failedPublisher.Proxy.Accepted.Should().HaveCount(FailureCount - 1);
        var failedCheckpoint = await publicationStore.LoadAsync(actorId);
        failedCheckpoint.Should().NotBeNull();
        failedCheckpoint!.PublishedVersion.Should().Be(committed.Version - 1);

        using var recoveryServices = BuildServices(
            eventStore,
            publicationStore,
            registerRedactionHook: true);
        var recoveredPublisher = CreateSizeLimitedPublisher(MaxPublicationBytes);
        var recoveredEvents = new RecordingEventPublisher(actorId);
        var recovered = CreateAgent(
            actorId,
            recoveryServices,
            recoveredPublisher.Instance,
            recoveredEvents);
        await recovered.ActivateAsync();

        var published = recoveredPublisher.Proxy.Accepted.Should().ContainSingle().Subject;
        recoveredPublisher.Proxy.AttemptedSizes.Should().ContainSingle()
            .Which.Should().BeLessThan(MaxPublicationBytes);
        published.StateEvent.EventId.Should().Be(committed.EventId);
        published.StateEvent.Version.Should().Be(committed.Version);
        var publishedFailure = published.StateEvent.EventData.Unpack<ProjectionScopeDispatchFailedEvent>();
        publishedFailure.Envelope.Should().BeNull();
        publishedFailure.Reason.Should().BeEmpty();
        var publishedState = published.StateRoot.Unpack<ProjectionScopeState>();
        publishedState.FailureSummary.UnresolvedFailureCount.Should().Be(FailureCount);
        publishedState.Failures.Should().HaveCount(FailureCount);
        publishedState.Failures.Should().OnlyContain(failure =>
            failure.Envelope == null && string.IsNullOrEmpty(failure.Reason));
        published.CalculateSize().Should().BeLessThan(64 * 1024);

        recovered.State.Failures.Should().HaveCount(FailureCount);
        recovered.State.Failures.Should().OnlyContain(failure =>
            failure.Envelope != null && !string.IsNullOrEmpty(failure.Reason),
            "publication sanitization must not remove any actor-owned repair payload");
        var checkpoint = await publicationStore.LoadAsync(actorId);
        checkpoint.Should().NotBeNull();
        checkpoint!.PublishedVersion.Should().Be(committed.Version);
        checkpoint.PublishedEventId.Should().Be(committed.EventId);
        checkpoint.Failure.Should().BeNull();

        var replayEnvelope = recoveredEvents.Published.Should().ContainSingle().Subject;
        var replayCommand = replayEnvelope.Payload!.Unpack<ReplayProjectionFailuresCommand>();
        replayEnvelope.Route.GetTopologyAudience().Should().Be(TopologyAudience.Self);
        replayCommand.MaxItems.Should().Be(FailureCount);
        replayCommand.AutomaticRecovery.Should().BeTrue();

        await recovered.HandleEventAsync(replayEnvelope);

        recovered.MaterializedStatus.Should().Be("failed");
        recovered.State.LastSuccessfulVersion.Should().Be(1466 + FailureCount);
        recovered.State.Failures.Should().BeEmpty(
            "activation recovery must replay the retained terminal observation without operator action");

        using var confirmedServices = BuildServices(
            eventStore,
            publicationStore,
            registerRedactionHook: true);
        var confirmedPublisher = CreateSizeLimitedPublisher(MaxPublicationBytes);
        var confirmedEvents = new RecordingEventPublisher(actorId);
        var confirmed = CreateAgent(
            actorId,
            confirmedServices,
            confirmedPublisher.Instance,
            confirmedEvents);
        await confirmed.ActivateAsync();

        confirmedPublisher.Proxy.Accepted.Should().BeEmpty(
            "a recovered publication must not remain pending after its checkpoint advances");
        confirmedPublisher.Proxy.AttemptedSizes.Should().BeEmpty();
        confirmedEvents.Published.Should().BeEmpty(
            "resolved failures must not schedule another automatic replay");
    }

    [Fact]
    public async Task Activation_ShouldNotScheduleAutomaticReplay_WhenFailureIsRetryExhausted()
    {
        const string actorId = "projection.durable.scope:retry-exhausted";
        var eventStore = new TestEventStore();
        var publicationStore = new TestPublicationStateStore();
        using var firstServices = BuildServices(
            eventStore,
            publicationStore,
            registerRedactionHook: true);
        var first = CreateAgent(
            actorId,
            firstServices,
            CreateSizeLimitedPublisher(MaxPublicationBytes).Instance,
            new RecordingEventPublisher(actorId));
        await first.ActivateAsync();
        await first.CommitStartedAsync();
        var failure = BuildFailureEvent(1, BuildFailureEnvelope(1));
        await first.CommitFailureAsync(failure);
        for (var attempt = 0;
             attempt < ProjectionFailureRetentionPolicy.DefaultMaxReplayAttempts;
             attempt++)
        {
            await first.CommitReplayFailureAsync(failure.FailureId);
        }

        first.State.Failures.Should().ContainSingle()
            .Which.RetryExhausted.Should().BeTrue();

        using var recoveredServices = BuildServices(
            eventStore,
            publicationStore,
            registerRedactionHook: true);
        var recoveredEvents = new RecordingEventPublisher(actorId);
        var recovered = CreateAgent(
            actorId,
            recoveredServices,
            CreateSizeLimitedPublisher(MaxPublicationBytes).Instance,
            recoveredEvents);

        await recovered.ActivateAsync();

        recovered.State.Failures.Should().ContainSingle()
            .Which.RetryExhausted.Should().BeTrue();
        recoveredEvents.Published.Should().BeEmpty(
            "retry exhaustion must stop activation-driven replay loops");
    }

    private static ServiceProvider BuildServices(
        IEventStore eventStore,
        ICommittedStatePublicationStateStore publicationStore,
        bool registerRedactionHook)
    {
        var services = new ServiceCollection()
            .AddSingleton<IEventStore>(eventStore)
            .AddSingleton<ICommittedStatePublicationStateStore>(publicationStore)
            .AddSingleton<IActorRuntimeCallbackScheduler, UnsupportedCallbackScheduler>()
            .AddSingleton(new EventSourcingRuntimeOptions())
            .AddSingleton<Func<ProjectionRuntimeScopeKey, RecoveryContext>>(static scopeKey =>
                new RecoveryContext
                {
                    RootActorId = scopeKey.RootActorId,
                    ProjectionKind = scopeKey.ProjectionKind,
                })
            .AddTransient(typeof(IEventSourcingBehaviorFactory<>), typeof(DefaultEventSourcingBehaviorFactory<>));
        if (registerRedactionHook)
            services.AddSingleton<ICommittedStatePublicationHook, ProjectionScopeCommittedStateRedactionHook>();

        return services.BuildServiceProvider();
    }

    private static RecoveryScopeAgent CreateAgent(
        string actorId,
        ServiceProvider services,
        object publisher,
        IEventPublisher eventPublisher)
    {
        var agent = new RecoveryScopeAgent
        {
            Services = services,
            EventPublisher = eventPublisher,
            EventSourcingBehaviorFactory =
                services.GetRequiredService<IEventSourcingBehaviorFactory<ProjectionScopeState>>(),
        };
        typeof(GAgentBase)
            .GetProperty("CommittedStateEventPublisher", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(agent, publisher);
        typeof(GAgentBase)
            .GetProperty(nameof(GAgentBase.Id), BindingFlags.Instance | BindingFlags.Public)!
            .SetValue(agent, actorId);
        return agent;
    }

    private static ProjectionScopeDispatchFailedEvent BuildFailureEvent(
        int index,
        EventEnvelope envelope) =>
        new()
        {
            FailureId = $"failure-{index}",
            Stage = "projection-execution",
            EventId = $"source-event-{1466 + index}",
            EventType = "type.googleapis.com/aevatar.workflow.WorkflowCompletedEvent",
            SourceVersion = 1466 + index,
            Reason = $"Injected materialization failure {index}.",
            Envelope = envelope,
            OccurredAtUtc = Timestamp.FromDateTimeOffset(
                new DateTimeOffset(2026, 8, 17, 14, 47, 56, TimeSpan.Zero).AddMinutes(index)),
            SourceActorId = "workflow.definition:wf-alpha:run:run-alpha",
        };

    private static EventEnvelope BuildFailureEnvelope(int index)
    {
        var payload = new byte[FailureEnvelopeBytes];
        new Random(613 + index).NextBytes(payload);
        return new EventEnvelope
        {
            Id = $"source-envelope-{1466 + index}",
            Route = EnvelopeRouteSemantics.CreateObserverPublication(
                "workflow.definition:wf-alpha:run:run-alpha"),
            Payload = Any.Pack(new CommittedStateEventPublished
            {
                StateEvent = new StateEvent
                {
                    AgentId = "workflow.definition:wf-alpha:run:run-alpha",
                    EventId = $"source-event-{1466 + index}",
                    EventType = StringValue.Descriptor.FullName,
                    EventData = Any.Pack(new StringValue
                    {
                        Value = index == FailureCount ? "failed" : "running",
                    }),
                    Version = 1466 + index,
                },
                StateRoot = Any.Pack(new BytesValue { Value = ByteString.CopyFrom(payload) }),
            }),
        };
    }

    private sealed class RecoveryScopeAgent : ProjectionScopeGAgentBase<RecoveryContext>
    {
        public string MaterializedStatus { get; private set; } = string.Empty;

        protected override ProjectionRuntimeMode RuntimeMode =>
            ProjectionRuntimeMode.DurableMaterialization;

        public Task CommitStartedAsync() =>
            PersistDomainEventAsync(new ProjectionScopeStartedEvent
            {
                ProjectionKind = "workflow-execution-materialization",
                Mode = ProjectionScopeMode.DurableMaterialization,
                ActivationGeneration = 1,
                OccurredAtUtc = Timestamp.FromDateTimeOffset(
                    new DateTimeOffset(2026, 8, 17, 14, 40, 0, TimeSpan.Zero)),
            });

        public Task CommitFailureAsync(ProjectionScopeDispatchFailedEvent failure) =>
            PersistDomainEventAsync(failure);

        public Task CommitReplayFailureAsync(string failureId) =>
            PersistDomainEventAsync(ProjectionScopeFailureLog.BuildReplayResultEvent(
                failureId,
                succeeded: false,
                reason: "still failing"));

        protected override ValueTask<ProjectionScopeDispatchResult> ProcessObservationCoreAsync(
            RecoveryContext context,
            EventEnvelope envelope,
            CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (!CommittedStateEventEnvelope.TryUnpack(envelope, out var published) ||
                published?.StateEvent?.EventData?.Is(StringValue.Descriptor) != true)
            {
                return ValueTask.FromResult(ProjectionScopeDispatchResult.Skip());
            }

            MaterializedStatus = published.StateEvent.EventData.Unpack<StringValue>().Value;
            return ValueTask.FromResult(ProjectionScopeDispatchResult.Success(
                published.StateEvent.Version,
                published.StateEvent.EventData.TypeUrl));
        }
    }

    private sealed class RecoveryContext : IProjectionMaterializationContext
    {
        public string RootActorId { get; init; } = string.Empty;
        public string ProjectionKind { get; init; } = string.Empty;
    }

    private static PublisherHandle CreateSizeLimitedPublisher(int maxPublicationBytes)
    {
        var publisherInterface = typeof(GAgentBase).Assembly.GetType(
            "Aevatar.Foundation.Core.EventSourcing.ICommittedStateEventPublisher",
            throwOnError: true)!;
        var instance = DispatchProxy.Create(publisherInterface, typeof(SizeLimitedPublisherProxy));
        var proxy = (SizeLimitedPublisherProxy)instance;
        proxy.MaxPublicationBytes = maxPublicationBytes;
        return new PublisherHandle(instance, proxy);
    }

    private sealed record PublisherHandle(object Instance, SizeLimitedPublisherProxy Proxy);

    private sealed class RecordingEventPublisher(string actorId) : IEventPublisher
    {
        public List<EventEnvelope> Published { get; } = [];

        public Task PublishAsync<TEvent>(
            TEvent evt,
            TopologyAudience audience = TopologyAudience.Children,
            CancellationToken ct = default,
            EventEnvelope? sourceEnvelope = null,
            EventEnvelopePublishOptions? options = null)
            where TEvent : IMessage
        {
            ct.ThrowIfCancellationRequested();
            Published.Add(new EventEnvelope
            {
                Id = Guid.NewGuid().ToString("N"),
                Route = EnvelopeRouteSemantics.CreateTopologyPublication(actorId, audience),
                Payload = Any.Pack(evt),
            });
            return Task.CompletedTask;
        }

        public Task SendToAsync<TEvent>(
            string targetActorId,
            TEvent evt,
            CancellationToken ct = default,
            EventEnvelope? sourceEnvelope = null,
            EventEnvelopePublishOptions? options = null)
            where TEvent : IMessage =>
            throw new NotSupportedException();
    }

    private sealed class UnsupportedCallbackScheduler : IActorRuntimeCallbackScheduler
    {
        public Task<RuntimeCallbackLease> ScheduleTimeoutAsync(
            RuntimeCallbackTimeoutRequest request,
            CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<RuntimeCallbackLease> ScheduleTimerAsync(
            RuntimeCallbackTimerRequest request,
            CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task CancelAsync(RuntimeCallbackLease lease, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task PurgeActorAsync(string actorId, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    public class SizeLimitedPublisherProxy : DispatchProxy
    {
        public int MaxPublicationBytes { get; set; }
        public List<int> AttemptedSizes { get; } = [];
        public List<CommittedStateEventPublished> Accepted { get; } = [];

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name != "PublishAsync" ||
                args is not { Length: 5 } ||
                args[0] is not CommittedStateEventPublished evt)
            {
                throw new NotSupportedException($"Unexpected publisher call: {targetMethod?.Name}");
            }

            if (args[2] is CancellationToken ct)
                ct.ThrowIfCancellationRequested();
            var size = evt.CalculateSize();
            AttemptedSizes.Add(size);
            if (size > MaxPublicationBytes)
            {
                throw new InvalidOperationException(
                    $"Broker rejected publication size {size}; max is {MaxPublicationBytes}.");
            }

            Accepted.Add(evt.Clone());
            return Task.CompletedTask;
        }
    }

    private sealed class TestEventStore : IEventStore
    {
        private readonly Dictionary<string, List<StateEvent>> _streams = new(StringComparer.Ordinal);
        private readonly object _gate = new();

        public Task<EventStoreCommitResult> AppendAsync(
            string agentId,
            IEnumerable<StateEvent> events,
            long expectedVersion,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            lock (_gate)
            {
                if (!_streams.TryGetValue(agentId, out var stream))
                {
                    stream = [];
                    _streams[agentId] = stream;
                }

                var currentVersion = stream.Count == 0 ? 0 : stream[^1].Version;
                if (currentVersion != expectedVersion)
                {
                    throw new EventStoreOptimisticConcurrencyException(
                        agentId,
                        expectedVersion,
                        currentVersion);
                }

                var committed = events.Select(static evt => evt.Clone()).ToArray();
                stream.AddRange(committed.Select(static evt => evt.Clone()));
                return Task.FromResult(new EventStoreCommitResult
                {
                    AgentId = agentId,
                    LatestVersion = committed.Length == 0 ? currentVersion : committed[^1].Version,
                    CommittedEvents = { committed },
                });
            }
        }

        public Task<IReadOnlyList<StateEvent>> GetEventsAsync(
            string agentId,
            long? fromVersion = null,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            lock (_gate)
            {
                if (!_streams.TryGetValue(agentId, out var stream))
                    return Task.FromResult<IReadOnlyList<StateEvent>>([]);

                return Task.FromResult<IReadOnlyList<StateEvent>>(stream
                    .Where(evt => !fromVersion.HasValue || evt.Version > fromVersion.Value)
                    .Select(static evt => evt.Clone())
                    .ToArray());
            }
        }

        public Task<long> GetVersionAsync(string agentId, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            lock (_gate)
            {
                return Task.FromResult(
                    !_streams.TryGetValue(agentId, out var stream) || stream.Count == 0
                        ? 0L
                        : stream[^1].Version);
            }
        }

        public Task<long> DeleteEventsUpToAsync(
            string agentId,
            long toVersion,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            lock (_gate)
            {
                if (!_streams.TryGetValue(agentId, out var stream))
                    return Task.FromResult(0L);

                var before = stream.Count;
                stream.RemoveAll(evt => evt.Version <= toVersion);
                return Task.FromResult((long)(before - stream.Count));
            }
        }
    }

    private sealed class TestPublicationStateStore : ICommittedStatePublicationStateStore
    {
        private readonly Dictionary<string, CommittedStatePublicationState> _states =
            new(StringComparer.Ordinal);
        private readonly object _gate = new();

        public Task<CommittedStatePublicationState?> LoadAsync(
            string actorId,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            lock (_gate)
            {
                return Task.FromResult(
                    _states.TryGetValue(actorId, out var state) ? state.Clone() : null);
            }
        }

        public Task<CommittedStatePublicationState> InitializeAsync(
            string actorId,
            long baselinePublishedVersion,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            lock (_gate)
            {
                if (!_states.TryGetValue(actorId, out var state))
                {
                    state = new CommittedStatePublicationState
                    {
                        ActorId = actorId,
                        Initialized = true,
                        PublishedVersion = baselinePublishedVersion,
                        Revision = 1,
                        UpdatedAt = Timestamp.FromDateTime(DateTime.UtcNow),
                    };
                    _states[actorId] = state;
                }

                return Task.FromResult(state.Clone());
            }
        }

        public Task<CommittedStatePublicationState> AdvanceAsync(
            string actorId,
            long expectedPublishedVersion,
            StateEvent publishedEvent,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            lock (_gate)
            {
                var state = GetInitialized(actorId, expectedPublishedVersion);
                state.PublishedVersion = publishedEvent.Version;
                state.PublishedEventId = publishedEvent.EventId;
                state.Revision += 1;
                state.UpdatedAt = Timestamp.FromDateTime(DateTime.UtcNow);
                state.Failure = null;
                return Task.FromResult(state.Clone());
            }
        }

        public Task<CommittedStatePublicationState> RecordFailureAsync(
            string actorId,
            long expectedPublishedVersion,
            StateEvent failedEvent,
            CommittedStatePublicationFailureStage stage,
            Exception error,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            lock (_gate)
            {
                var state = GetInitialized(actorId, expectedPublishedVersion);
                state.Failure = new CommittedStatePublicationFailure
                {
                    Version = failedEvent.Version,
                    EventId = failedEvent.EventId,
                    Attempts = (state.Failure?.Attempts ?? 0) + 1,
                    ErrorType = error.GetType().FullName ?? error.GetType().Name,
                    ErrorMessage = "Committed-state publication failed.",
                    LastFailedAt = Timestamp.FromDateTime(DateTime.UtcNow),
                    Stage = stage,
                };
                state.Revision += 1;
                state.UpdatedAt = state.Failure.LastFailedAt;
                return Task.FromResult(state.Clone());
            }
        }

        private CommittedStatePublicationState GetInitialized(
            string actorId,
            long expectedPublishedVersion)
        {
            if (!_states.TryGetValue(actorId, out var state) || !state.Initialized)
                throw new InvalidOperationException($"Publication state for '{actorId}' is not initialized.");
            if (state.PublishedVersion != expectedPublishedVersion)
            {
                throw new InvalidOperationException(
                    $"Publication state for '{actorId}' expected {expectedPublishedVersion}, " +
                    $"but was {state.PublishedVersion}.");
            }

            return state;
        }
    }
}
