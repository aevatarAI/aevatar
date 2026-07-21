using System.Reflection;
using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Core;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Attributes;
using Aevatar.Foundation.Abstractions.Persistence;
using Aevatar.Foundation.Abstractions.Runtime.Callbacks;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Core.EventSourcing;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.AI.Tests;

public sealed class RoleGAgentCompletionNotificationTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-07-22T08:00:00Z");

    [Fact]
    public async Task CompletionSendFailure_ShouldScheduleDurableRetry()
    {
        var store = new InMemoryEventStoreForTests();
        var scheduler = new RecordingRuntimeCallbackScheduler();
        var publisher = new RecordingEventPublisher
        {
            SendException = new InvalidOperationException("simulated completion send failure"),
        };
        var actor = await CreateInitializedActorAsync(store, scheduler, publisher, "role-retry");

        await CompleteSessionAsync(actor, "session-1", expiresAtUnixMs: Now.AddMinutes(1).ToUnixTimeMilliseconds());

        var session = actor.State.Sessions["session-1"];
        session.CompletionNotificationDeliveryStatus.Should()
            .Be(RoleChatCompletionNotificationDeliveryStatus.RetryScheduled);
        session.CompletionNotificationAttempt.Should().Be(1);
        var callback = scheduler.TimeoutRequests.Should().ContainSingle().Subject;
        callback.CallbackId.Should().Be(session.CompletionNotificationRetryCallbackId);
        callback.DueTime.Should().Be(TimeSpan.FromMilliseconds(250));
        var retry = callback.TriggerEnvelope.Payload.Unpack<RoleChatCompletionNotificationRetryFiredEvent>();
        retry.SessionId.Should().Be("session-1");
        retry.DeliveryId.Should().Be("delivery-session-1");
        retry.Attempt.Should().Be(1);

        var handler = typeof(RoleGAgent).GetMethod(nameof(RoleGAgent.HandleCompletionNotificationRetryFiredAsync));
        handler.Should().NotBeNull();
        var attribute = handler!.GetCustomAttribute<EventHandlerAttribute>();
        attribute.Should().NotBeNull();
        attribute!.AllowSelfHandling.Should().BeTrue();
        attribute.OnlySelfHandling.Should().BeTrue();

        for (var index = 0; index < 7; index++)
            await actor.HandleEventAsync(scheduler.TimeoutRequests[index].TriggerEnvelope);
        scheduler.TimeoutRequests.Select(static request => request.DueTime).Should().Equal(
            TimeSpan.FromMilliseconds(250),
            TimeSpan.FromMilliseconds(500),
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(2),
            TimeSpan.FromSeconds(4),
            TimeSpan.FromSeconds(8),
            TimeSpan.FromSeconds(16),
            TimeSpan.FromSeconds(30));

        var deadlineScheduler = new RecordingRuntimeCallbackScheduler();
        var deadlineActor = await CreateInitializedActorAsync(
            new InMemoryEventStoreForTests(),
            deadlineScheduler,
            new RecordingEventPublisher
            {
                SendException = new InvalidOperationException("simulated completion send failure"),
            },
            "role-deadline-cap");
        await CompleteSessionAsync(
            deadlineActor,
            "session-deadline",
            expiresAtUnixMs: Now.AddMilliseconds(100).ToUnixTimeMilliseconds());
        deadlineScheduler.TimeoutRequests.Should().ContainSingle()
            .Which.DueTime.Should().Be(TimeSpan.FromMilliseconds(100));
    }

    [Fact]
    public async Task PendingCompletion_ShouldNotBeTrimmedWhenSessionLimitExceeded()
    {
        var store = new InMemoryEventStoreForTests();
        var scheduler = new RecordingRuntimeCallbackScheduler();
        var publisher = new RecordingEventPublisher
        {
            FailurePredicate = static targetActorId => targetActorId == "service-run:session-1",
        };
        var actor = await CreateInitializedActorAsync(store, scheduler, publisher, "role-trimming");

        for (var index = 1; index <= 129; index++)
        {
            await CompleteSessionAsync(
                actor,
                $"session-{index}",
                expiresAtUnixMs: Now.AddMinutes(5).ToUnixTimeMilliseconds());
        }

        actor.State.Sessions.Should().HaveCount(128);
        actor.State.Sessions.Should().ContainKey("session-1");
        actor.State.Sessions["session-1"].CompletionNotificationDeliveryStatus.Should()
            .Be(RoleChatCompletionNotificationDeliveryStatus.RetryScheduled);
        actor.State.Sessions.Should().NotContainKey("session-2");
        actor.State.Sessions["session-129"].CompletionNotificationDeliveryStatus.Should()
            .Be(RoleChatCompletionNotificationDeliveryStatus.Dispatched);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RetryFired_ShouldDispatchMatchingSessionOnce(bool retryScheduleWasCommitted)
    {
        IEventStore store = retryScheduleWasCommitted
            ? new InMemoryEventStoreForTests()
            : new FailOnceRetryScheduledEventStore(failedAttempt: 2);
        var scheduler = new RecordingRuntimeCallbackScheduler();
        var publisher = new RecordingEventPublisher
        {
            SendException = new InvalidOperationException("simulated completion send failure"),
        };
        var actor = await CreateInitializedActorAsync(store, scheduler, publisher, "role-retry-callback");
        await CompleteSessionAsync(actor, "session-1", Now.AddMinutes(1).ToUnixTimeMilliseconds());
        var callbackEnvelope = scheduler.TimeoutRequests.Should().ContainSingle().Subject.TriggerEnvelope;
        if (!retryScheduleWasCommitted)
        {
            var fireFirstAttempt = () => actor.HandleEventAsync(callbackEnvelope);
            await fireFirstAttempt.Should().ThrowAsync<InvalidOperationException>()
                .WithMessage("simulated retry-scheduled commit failure");
            scheduler.TimeoutRequests.Should().HaveCount(2);
            callbackEnvelope = scheduler.TimeoutRequests[1].TriggerEnvelope;
            callbackEnvelope.Payload.Unpack<RoleChatCompletionNotificationRetryFiredEvent>()
                .Attempt.Should().Be(2);
            actor.State.Sessions["session-1"].CompletionNotificationAttempt.Should().Be(1);
        }
        publisher.SendException = null;

        await actor.HandleEventAsync(callbackEnvelope);
        await actor.HandleEventAsync(callbackEnvelope);

        publisher.SuccessfulSends.Should().ContainSingle();
        actor.State.Sessions["session-1"].CompletionNotificationDeliveryStatus.Should()
            .Be(RoleChatCompletionNotificationDeliveryStatus.Dispatched);
    }

    [Fact]
    public async Task RetryFired_WhenSessionOrAttemptIsStale_ShouldNotSend()
    {
        var store = new InMemoryEventStoreForTests();
        var scheduler = new RecordingRuntimeCallbackScheduler();
        var publisher = new RecordingEventPublisher
        {
            SendException = new InvalidOperationException("simulated completion send failure"),
        };
        var actor = await CreateInitializedActorAsync(store, scheduler, publisher, "role-stale-retry");
        await CompleteSessionAsync(actor, "session-1", Now.AddMinutes(1).ToUnixTimeMilliseconds());
        var callback = scheduler.TimeoutRequests.Should().ContainSingle().Subject.TriggerEnvelope;
        publisher.SendException = null;

        var staleSession = callback.Clone();
        staleSession.Payload = Any.Pack(new RoleChatCompletionNotificationRetryFiredEvent
        {
            SessionId = "session-missing",
            DeliveryId = "delivery-session-1",
            Attempt = 1,
        });
        var staleAttempt = callback.Clone();
        staleAttempt.Payload = Any.Pack(new RoleChatCompletionNotificationRetryFiredEvent
        {
            SessionId = "session-1",
            DeliveryId = "delivery-session-1",
            Attempt = 3,
        });
        var foreign = callback.Clone();
        foreign.Route = EnvelopeRouteSemantics.CreateDirect("foreign-actor", actor.Id);

        await actor.HandleEventAsync(staleSession);
        await actor.HandleEventAsync(staleAttempt);
        await actor.HandleEventAsync(foreign);

        publisher.SuccessfulSends.Should().BeEmpty();
        actor.State.Sessions["session-1"].CompletionNotificationDeliveryStatus.Should()
            .Be(RoleChatCompletionNotificationDeliveryStatus.RetryScheduled);
    }

    [Fact]
    public async Task CompletionDeadlineElapsed_ShouldCommitExpired()
    {
        var store = new InMemoryEventStoreForTests();
        var scheduler = new RecordingRuntimeCallbackScheduler();
        var publisher = new RecordingEventPublisher();
        var actor = await CreateInitializedActorAsync(store, scheduler, publisher, "role-expired");

        await CompleteSessionAsync(actor, "session-1", Now.AddMilliseconds(-1).ToUnixTimeMilliseconds());

        publisher.SuccessfulSends.Should().BeEmpty();
        scheduler.TimeoutRequests.Should().BeEmpty();
        actor.State.Sessions["session-1"].CompletionNotificationDeliveryStatus.Should()
            .Be(RoleChatCompletionNotificationDeliveryStatus.Expired);
        var events = await store.GetEventsAsync(actor.Id);
        var expired = events.Should().ContainSingle(stateEvent =>
                stateEvent.EventData.Is(RoleChatCompletionNotificationExpiredEvent.Descriptor))
            .Subject.EventData.Unpack<RoleChatCompletionNotificationExpiredEvent>();
        expired.SessionId.Should().Be("session-1");
        expired.DeliveryId.Should().Be("delivery-session-1");
        expired.Attempt.Should().Be(0);
    }

    [Fact]
    public async Task ActivateAsync_WhenCompletionPending_ShouldRestoreRetry()
    {
        var store = new InMemoryEventStoreForTests();
        var firstScheduler = new RecordingRuntimeCallbackScheduler();
        var firstPublisher = new RecordingEventPublisher
        {
            SendException = new InvalidOperationException("simulated completion send failure"),
        };
        var first = await CreateInitializedActorAsync(
            store,
            firstScheduler,
            firstPublisher,
            "role-reactivate");
        await CompleteSessionAsync(first, "session-1", Now.AddMinutes(1).ToUnixTimeMilliseconds());

        var recoveredScheduler = new RecordingRuntimeCallbackScheduler();
        var recoveredPublisher = new RecordingEventPublisher
        {
            SendException = new InvalidOperationException("simulated completion send failure"),
        };
        var recovered = CreateActor(
            store,
            recoveredScheduler,
            recoveredPublisher,
            "role-reactivate");

        await recovered.ActivateAsync();

        var callback = recoveredScheduler.TimeoutRequests.Should().ContainSingle().Subject;
        callback.TriggerEnvelope.Payload.Unpack<RoleChatCompletionNotificationRetryFiredEvent>()
            .Attempt.Should().Be(2);
        recovered.State.Sessions["session-1"].CompletionNotificationDeliveryStatus.Should()
            .Be(RoleChatCompletionNotificationDeliveryStatus.RetryScheduled);
        recovered.State.Sessions["session-1"].CompletionNotificationAttempt.Should().Be(2);
    }

    [Fact]
    public async Task CompletionCancellation_ShouldRemainObservable()
    {
        var store = new InMemoryEventStoreForTests();
        var scheduler = new RecordingRuntimeCallbackScheduler();
        var publisher = new RecordingEventPublisher
        {
            SendException = new OperationCanceledException("simulated dispatch cancellation"),
        };
        var actor = await CreateInitializedActorAsync(store, scheduler, publisher, "role-cancelled");

        var act = () => CompleteSessionAsync(
            actor,
            "session-1",
            Now.AddMinutes(1).ToUnixTimeMilliseconds());

        await act.Should().ThrowAsync<OperationCanceledException>();
        actor.State.Sessions["session-1"].CompletionNotificationDeliveryStatus.Should()
            .Be(RoleChatCompletionNotificationDeliveryStatus.Prepared);
        scheduler.TimeoutRequests.Should().BeEmpty();
    }

    private static async Task<RoleGAgent> CreateInitializedActorAsync(
        IEventStore store,
        RecordingRuntimeCallbackScheduler scheduler,
        RecordingEventPublisher publisher,
        string actorId)
    {
        var actor = CreateActor(store, scheduler, publisher, actorId);
        await actor.ActivateAsync();
        await actor.HandleInitializeRoleAgent(new InitializeRoleAgentEvent
        {
            RoleName = "assistant",
            ProviderName = "completion-test",
            SystemPrompt = "system",
        });
        return actor;
    }

    private static RoleGAgent CreateActor(
        IEventStore store,
        RecordingRuntimeCallbackScheduler scheduler,
        RecordingEventPublisher publisher,
        string actorId)
    {
        var services = new ServiceCollection()
            .AddSingleton(store)
            .AddSingleton<EventSourcingRuntimeOptions>()
            .AddSingleton<IActorRuntimeCallbackScheduler>(scheduler)
            .AddTransient(typeof(IEventSourcingBehaviorFactory<>), typeof(DefaultEventSourcingBehaviorFactory<>))
            .BuildServiceProvider();
        var provider = new CompletionLlmProvider();
        var actor = new RoleGAgent(provider, timeProvider: new FixedTimeProvider(Now))
        {
            Services = services,
            EventPublisher = publisher,
            EventSourcingBehaviorFactory = services.GetRequiredService<IEventSourcingBehaviorFactory<RoleGAgentState>>(),
        };
        typeof(GAgentBase).GetMethod("SetId", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(actor, [actorId]);
        return actor;
    }

    private static Task CompleteSessionAsync(
        RoleGAgent actor,
        string sessionId,
        long expiresAtUnixMs) =>
        actor.HandleChatRequest(new ChatRequestEvent
        {
            Prompt = $"prompt-{sessionId}",
            SessionId = sessionId,
            RunContext = new RoleChatRunContext
            {
                RunId = $"run-{sessionId}",
                CommandId = $"cmd-{sessionId}",
                CorrelationId = $"corr-{sessionId}",
                CompletionNotificationActorId = $"service-run:{sessionId}",
                CompletionNotificationDeliveryId = $"delivery-{sessionId}",
                CompletionNotificationExpiresAtUnixMs = expiresAtUnixMs,
            },
        });

    private sealed class CompletionLlmProvider : ILLMProviderFactory, ILLMProvider
    {
        public string Name => "completion-test";

        public ILLMProvider GetProvider(string name) => this;

        public ILLMProvider GetDefault() => this;

        public IReadOnlyList<string> GetAvailableProviders() => [Name];

        public async IAsyncEnumerable<LLMStreamChunk> ChatStreamAsync(
            LLMRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            yield return new LLMStreamChunk { DeltaContent = "done" };
            await Task.CompletedTask;
            yield return new LLMStreamChunk { IsLast = true };
        }
    }

    private sealed class RecordingEventPublisher : IEventPublisher
    {
        public Exception? SendException { get; set; }

        public Func<string, bool>? FailurePredicate { get; init; }

        public List<(string TargetActorId, IMessage Event, EventEnvelopePublishOptions? Options)> SuccessfulSends { get; } = [];

        public Task PublishAsync<TEvent>(
            TEvent evt,
            TopologyAudience audience = TopologyAudience.Children,
            CancellationToken ct = default,
            EventEnvelope? sourceEnvelope = null,
            EventEnvelopePublishOptions? options = null)
            where TEvent : IMessage =>
            Task.CompletedTask;

        public Task SendToAsync<TEvent>(
            string targetActorId,
            TEvent evt,
            CancellationToken ct = default,
            EventEnvelope? sourceEnvelope = null,
            EventEnvelopePublishOptions? options = null)
            where TEvent : IMessage
        {
            ct.ThrowIfCancellationRequested();
            if (SendException != null)
                throw SendException;
            if (FailurePredicate?.Invoke(targetActorId) == true)
                throw new InvalidOperationException("simulated completion send failure");

            SuccessfulSends.Add((targetActorId, evt, options));
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingRuntimeCallbackScheduler : IActorRuntimeCallbackScheduler
    {
        public List<RuntimeCallbackTimeoutRequest> TimeoutRequests { get; } = [];

        public Task<RuntimeCallbackLease> ScheduleTimeoutAsync(
            RuntimeCallbackTimeoutRequest request,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            TimeoutRequests.Add(new RuntimeCallbackTimeoutRequest
            {
                ActorId = request.ActorId,
                CallbackId = request.CallbackId,
                TriggerEnvelope = request.TriggerEnvelope.Clone(),
                DueTime = request.DueTime,
                DeliveryMode = request.DeliveryMode,
            });
            return Task.FromResult(new RuntimeCallbackLease(
                request.ActorId,
                request.CallbackId,
                TimeoutRequests.Count,
                RuntimeCallbackBackend.InMemory));
        }

        public Task<RuntimeCallbackLease> ScheduleTimerAsync(
            RuntimeCallbackTimerRequest request,
            CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task CancelAsync(RuntimeCallbackLease lease, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task PurgeActorAsync(string actorId, CancellationToken ct = default) =>
            Task.CompletedTask;
    }

    private sealed class FailOnceRetryScheduledEventStore(int failedAttempt) : IEventStore
    {
        private readonly InMemoryEventStoreForTests _inner = new();
        private bool _failed;

        public Task<EventStoreCommitResult> AppendAsync(
            string agentId,
            IEnumerable<StateEvent> events,
            long expectedVersion,
            CancellationToken ct = default)
        {
            var buffered = events.ToArray();
            if (!_failed && buffered.Any(stateEvent =>
                    stateEvent.EventData.Is(RoleChatCompletionNotificationRetryScheduledEvent.Descriptor) &&
                    stateEvent.EventData.Unpack<RoleChatCompletionNotificationRetryScheduledEvent>().Attempt ==
                    failedAttempt))
            {
                _failed = true;
                throw new InvalidOperationException("simulated retry-scheduled commit failure");
            }

            return _inner.AppendAsync(agentId, buffered, expectedVersion, ct);
        }

        public Task<IReadOnlyList<StateEvent>> GetEventsAsync(
            string agentId,
            long? fromVersion = null,
            CancellationToken ct = default) =>
            _inner.GetEventsAsync(agentId, fromVersion, ct);

        public Task<long> GetVersionAsync(string agentId, CancellationToken ct = default) =>
            _inner.GetVersionAsync(agentId, ct);

        public Task<long> DeleteEventsUpToAsync(
            string agentId,
            long toVersion,
            CancellationToken ct = default) =>
            _inner.DeleteEventsUpToAsync(agentId, toVersion, ct);
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
