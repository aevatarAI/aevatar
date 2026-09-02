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
using Aevatar.Foundation.Runtime.Implementations.Local.Actors;
using Aevatar.Foundation.Runtime.Persistence;
using Aevatar.Foundation.Runtime.Streaming;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

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
        scheduler.TimeoutRequests.Select(static request => request.CallbackId)
            .Should().OnlyContain(callbackId => callbackId == callback.CallbackId);
        var retryOperationIds = scheduler.TimeoutRequests
            .Select(static request => request.TriggerEnvelope.Runtime!.DeliveryIdentity!.OperationId)
            .ToArray();
        retryOperationIds.Should().OnlyHaveUniqueItems();

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
    public async Task ActivateAsync_WhenPendingCompletionSenderIgnoresCancellation_ShouldReturnAndPersistRetry()
    {
        const int timeoutMs = 1_000;
        var store = new InMemoryEventStoreForTests();
        var seed = await CreateInitializedActorAsync(
            store,
            new RecordingRuntimeCallbackScheduler(),
            new RecordingEventPublisher(),
            "role-reactivate-ignoring-sender");
        await PersistPreparedCompletionAsync(seed, "session-1");

        var timeProvider = new FakeTimeProvider(Now);
        var scheduler = new RecordingRuntimeCallbackScheduler();
        var sendProbe = new IgnoringCancellationProbe();
        var publisher = new RecordingEventPublisher
        {
            BeforeSendAsync = (_, _, _) => sendProbe.WaitForReleaseAsync(),
        };
        var recovered = CreateActor(
            store,
            scheduler,
            publisher,
            "role-reactivate-ignoring-sender",
            timeProvider,
            new RoleChatExecutionOptions(postTurnProcessingTimeoutMs: timeoutMs));

        var activation = recovered.ActivateAsync();
        await sendProbe.Started;
        timeProvider.Advance(TimeSpan.FromMilliseconds(timeoutMs));
        await activation;

        var session = recovered.State.Sessions["session-1"];
        session.CompletionNotificationDeliveryStatus.Should()
            .Be(RoleChatCompletionNotificationDeliveryStatus.RetryScheduled);
        session.CompletionNotificationAttempt.Should().Be(1);
        scheduler.TimeoutRequests.Should().ContainSingle();
        publisher.SuccessfulSends.Should().BeEmpty();
    }

    [Fact]
    public async Task LiveCompletion_WhenSenderIgnoresCancellation_ShouldFenceLateDispatchAndRetryFromSelfCallback()
    {
        const int timeoutMs = 1_000;
        var store = new InMemoryEventStoreForTests();
        var timeProvider = new FakeTimeProvider(Now);
        var scheduler = new RecordingRuntimeCallbackScheduler();
        var sendProbe = new IgnoringCancellationProbe();
        var publisher = new RecordingEventPublisher
        {
            BeforeSendAsync = (_, _, _) => sendProbe.WaitForReleaseAsync(),
        };
        var actor = CreateActor(
            store,
            scheduler,
            publisher,
            "role-live-ignoring-sender",
            timeProvider,
            new RoleChatExecutionOptions(postTurnProcessingTimeoutMs: timeoutMs));
        await actor.ActivateAsync();
        await actor.HandleInitializeRoleAgent(new InitializeRoleAgentEvent
        {
            RoleName = "assistant",
            ProviderName = "completion-test",
            SystemPrompt = "system",
        });

        var turn = CompleteSessionAsync(
            actor,
            "session-1",
            Now.AddMinutes(1).ToUnixTimeMilliseconds());
        await sendProbe.Started;
        actor.State.Sessions["session-1"].CompletionNotificationDeliveryStatus.Should()
            .Be(RoleChatCompletionNotificationDeliveryStatus.Prepared);
        timeProvider.Advance(TimeSpan.FromMilliseconds(timeoutMs));
        await turn;

        actor.State.Sessions["session-1"].CompletionNotificationDeliveryStatus.Should()
            .Be(RoleChatCompletionNotificationDeliveryStatus.RetryScheduled);
        var retry = scheduler.TimeoutRequests.Should().ContainSingle().Subject.TriggerEnvelope;

        sendProbe.Release();
        await sendProbe.Completed;
        actor.State.Sessions["session-1"].CompletionNotificationDeliveryStatus.Should()
            .Be(RoleChatCompletionNotificationDeliveryStatus.RetryScheduled);
        (await store.GetEventsAsync(actor.Id)).Should().NotContain(stateEvent =>
            stateEvent.EventData.Is(RoleChatCompletionNotificationDispatchedEvent.Descriptor));

        publisher.BeforeSendAsync = null;
        await actor.HandleEventAsync(retry);
        await actor.HandleEventAsync(retry);

        actor.State.Sessions["session-1"].CompletionNotificationDeliveryStatus.Should()
            .Be(RoleChatCompletionNotificationDeliveryStatus.Dispatched);
        publisher.SuccessfulSends.Should().ContainSingle();
    }

    [Fact]
    public async Task RetryScheduler_WhenAdapterIgnoresCancellation_ShouldReturnWithoutLateStateMutation()
    {
        const int timeoutMs = 1_000;
        var store = new InMemoryEventStoreForTests();
        var timeProvider = new FakeTimeProvider(Now);
        var scheduler = new IgnoringCancellationRuntimeCallbackScheduler();
        var publisher = new RecordingEventPublisher
        {
            SendException = new InvalidOperationException("simulated completion send failure"),
        };
        var actor = CreateActor(
            store,
            scheduler,
            publisher,
            "role-ignoring-retry-scheduler",
            timeProvider,
            new RoleChatExecutionOptions(postTurnProcessingTimeoutMs: timeoutMs));
        await actor.ActivateAsync();
        await actor.HandleInitializeRoleAgent(new InitializeRoleAgentEvent
        {
            RoleName = "assistant",
            ProviderName = "completion-test",
            SystemPrompt = "system",
        });

        var turn = CompleteSessionAsync(
            actor,
            "session-1",
            Now.AddMinutes(1).ToUnixTimeMilliseconds());
        await scheduler.Started;
        timeProvider.Advance(TimeSpan.FromMilliseconds(timeoutMs));
        await turn;

        actor.State.Sessions["session-1"].CompletionNotificationDeliveryStatus.Should()
            .Be(RoleChatCompletionNotificationDeliveryStatus.Prepared);
        scheduler.Release();
        await scheduler.Completed;
        actor.State.Sessions["session-1"].CompletionNotificationDeliveryStatus.Should()
            .Be(RoleChatCompletionNotificationDeliveryStatus.Prepared);
        (await store.GetEventsAsync(actor.Id)).Should().NotContain(stateEvent =>
            stateEvent.EventData.Is(RoleChatCompletionNotificationRetryScheduledEvent.Descriptor));
    }

    [Fact]
    public async Task CompletionPublisherCancellation_ShouldScheduleDurableRetry()
    {
        var store = new InMemoryEventStoreForTests();
        var scheduler = new RecordingRuntimeCallbackScheduler();
        var publisher = new RecordingEventPublisher
        {
            SendException = new OperationCanceledException("simulated dispatch cancellation"),
        };
        var actor = await CreateInitializedActorAsync(store, scheduler, publisher, "role-cancelled");

        await CompleteSessionAsync(
            actor,
            "session-1",
            Now.AddMinutes(1).ToUnixTimeMilliseconds());

        actor.State.Sessions["session-1"].CompletionNotificationDeliveryStatus.Should()
            .Be(RoleChatCompletionNotificationDeliveryStatus.RetryScheduled);
        actor.State.Sessions["session-1"].CompletionNotificationAttempt.Should().Be(1);
        scheduler.TimeoutRequests.Should().ContainSingle();
    }

    [Fact]
    public async Task DispatchedCommitFailure_ShouldScheduleRetryWithoutInvalidatingCommittedTerminal()
    {
        var store = new FailOnceDispatchedEventStore();
        var scheduler = new RecordingRuntimeCallbackScheduler();
        var publisher = new RecordingEventPublisher();
        var actor = await CreateInitializedActorAsync(store, scheduler, publisher, "role-dispatch-commit");

        await CompleteSessionAsync(
            actor,
            "session-1",
            Now.AddMinutes(1).ToUnixTimeMilliseconds());

        actor.State.Sessions["session-1"].CompletionNotificationDeliveryStatus.Should()
            .Be(RoleChatCompletionNotificationDeliveryStatus.RetryScheduled);
        actor.State.Sessions["session-1"].CompletionNotificationAttempt.Should().Be(1);
        var callback = scheduler.TimeoutRequests.Should().ContainSingle().Subject.TriggerEnvelope;

        await actor.HandleEventAsync(callback);

        publisher.SuccessfulSends.Should().HaveCount(2);
        publisher.SuccessfulSends.Select(static send => send.Options!.Delivery!.OperationId)
            .Should().OnlyContain(static operationId =>
                operationId == "role-chat-terminal:delivery-session-1:outcome:1");
        actor.State.Sessions["session-1"].CompletionNotificationDeliveryStatus.Should()
            .Be(RoleChatCompletionNotificationDeliveryStatus.Dispatched);
    }

    [Theory]
    [InlineData(RoleChatSessionOutcome.Completed, 1)]
    [InlineData(RoleChatSessionOutcome.Failed, 2)]
    public async Task OutcomeUncertainReconciliation_ShouldResetAndRedeliverCompletionOutboxOnce(
        RoleChatSessionOutcome reconciledOutcome,
        int expectedOutcomeValue)
    {
        var store = new InMemoryEventStoreForTests();
        var publisher = new RecordingEventPublisher();
        var actor = await CreateInitializedActorAsync(
            store,
            new RecordingRuntimeCallbackScheduler(),
            publisher,
            $"role-terminal-reconciliation-{expectedOutcomeValue}");
        var runContext = new RoleChatRunContext
        {
            RunId = "run-reconciliation",
            CommandId = "command-reconciliation",
            CompletionNotificationActorId = "service-run:reconciliation",
            CompletionNotificationDeliveryId = "delivery-reconciliation",
        };
        await actor.PersistForTestAsync(new RoleChatSessionStartedEvent
        {
            SessionId = "session-reconciliation",
            Prompt = "perform work",
            RunContext = runContext.Clone(),
        });
        await actor.PersistForTestAsync(new RoleChatSessionCompletedEvent
        {
            SessionId = "session-reconciliation",
            Prompt = "perform work",
            Outcome = RoleChatSessionOutcome.OutcomeUncertain,
            FailureCode = "SESSION_OUTCOME_UNCERTAIN",
            RunContext = runContext.Clone(),
        });
        await actor.PersistForTestAsync(new RoleChatCompletionNotificationDispatchedEvent
        {
            SessionId = "session-reconciliation",
            DeliveryId = "delivery-reconciliation",
            Attempt = 0,
        });
        var request = new ChatRequestEvent
        {
            SessionId = "session-reconciliation",
            Prompt = "perform work",
            RunContext = runContext.Clone(),
        };

        await actor.CompleteForTestAsync(request, reconciledOutcome);
        await actor.CompleteForTestAsync(request, reconciledOutcome);

        var sent = publisher.SuccessfulSends.Should().ContainSingle().Which;
        sent.Event.Should().BeOfType<RoleChatSessionCompletedEvent>()
            .Which.Outcome.Should().Be(reconciledOutcome);
        sent.Options!.Delivery!.OperationId.Should().Be(
            $"role-chat-terminal:delivery-reconciliation:outcome:{expectedOutcomeValue}");
        actor.State.Sessions["session-reconciliation"].CompletionNotificationDeliveryStatus
            .Should().Be(RoleChatCompletionNotificationDeliveryStatus.Dispatched);
        (await store.GetEventsAsync(actor.Id))
            .Where(stateEvent => stateEvent.EventData.Is(RoleChatSessionCompletedEvent.Descriptor))
            .Select(stateEvent => stateEvent.EventData.Unpack<RoleChatSessionCompletedEvent>())
            .Count(completion => completion.Outcome == reconciledOutcome)
            .Should().Be(1);
    }

    [Theory]
    [InlineData(RoleChatCompletionNotificationDeliveryStatus.Prepared, RoleChatSessionOutcome.Completed)]
    [InlineData(RoleChatCompletionNotificationDeliveryStatus.Prepared, RoleChatSessionOutcome.Failed)]
    [InlineData(RoleChatCompletionNotificationDeliveryStatus.RetryScheduled, RoleChatSessionOutcome.Completed)]
    [InlineData(RoleChatCompletionNotificationDeliveryStatus.RetryScheduled, RoleChatSessionOutcome.Failed)]
    public async Task OutcomeUncertainReconciliation_WithPendingOutbox_ShouldCommitReplacementBeforeRedelivery(
        RoleChatCompletionNotificationDeliveryStatus pendingStatus,
        RoleChatSessionOutcome reconciledOutcome)
    {
        const string sessionId = "session-pending-reconciliation";
        var store = new InMemoryEventStoreForTests();
        var publisher = new RecordingEventPublisher();
        var actor = await CreateInitializedActorAsync(
            store,
            new RecordingRuntimeCallbackScheduler(),
            publisher,
            $"role-pending-reconciliation-{(int)pendingStatus}-{(int)reconciledOutcome}");
        var runContext = new RoleChatRunContext
        {
            RunId = "run-pending-reconciliation",
            CommandId = "command-pending-reconciliation",
            CompletionNotificationActorId = "service-run:pending-reconciliation",
            CompletionNotificationDeliveryId = $"delivery-{sessionId}",
        };
        await actor.PersistForTestAsync(new RoleChatSessionStartedEvent
        {
            SessionId = sessionId,
            Prompt = "perform work",
            RunContext = runContext.Clone(),
        });
        await actor.PersistForTestAsync(new RoleChatSessionCompletedEvent
        {
            SessionId = sessionId,
            Prompt = "perform work",
            Outcome = RoleChatSessionOutcome.OutcomeUncertain,
            FailureCode = "SESSION_OUTCOME_UNCERTAIN",
            RunContext = runContext.Clone(),
        });
        if (pendingStatus == RoleChatCompletionNotificationDeliveryStatus.RetryScheduled)
            await actor.PersistForTestAsync(RetryScheduledEvent(sessionId, attempt: 1, "stale-retry"));

        actor.State.Sessions[sessionId].CompletionNotificationDeliveryStatus.Should().Be(pendingStatus);
        var request = new ChatRequestEvent
        {
            SessionId = sessionId,
            Prompt = "perform work",
            RunContext = runContext.Clone(),
        };

        await actor.CompleteForTestAsync(request, reconciledOutcome);
        await actor.CompleteForTestAsync(request, reconciledOutcome);

        var committedSession = actor.TerminalCommittedSessions.Should().ContainSingle().Which;
        committedSession.Outcome.Should().Be(reconciledOutcome);
        committedSession.CompletionNotificationDeliveryStatus.Should()
            .Be(RoleChatCompletionNotificationDeliveryStatus.Prepared);
        committedSession.CompletionNotificationAttempt.Should().Be(0);
        committedSession.CompletionNotificationRetryCallbackId.Should().BeEmpty();
        committedSession.CompletionNotificationRetryAt.Should().BeNull();
        publisher.SuccessfulSends.Should().ContainSingle().Which.Event
            .Should().BeOfType<RoleChatSessionCompletedEvent>()
            .Which.Outcome.Should().Be(reconciledOutcome);
        (await store.GetEventsAsync(actor.Id))
            .Where(stateEvent => stateEvent.EventData.Is(RoleChatSessionCompletedEvent.Descriptor))
            .Select(stateEvent => stateEvent.EventData.Unpack<RoleChatSessionCompletedEvent>())
            .Count(completion => completion.Outcome == reconciledOutcome)
            .Should().Be(1);
    }

    [Fact]
    public async Task DurableRetrySchedulerFailure_ShouldRemainObservableAndRecoverThroughSelfInbox()
    {
        var store = new InMemoryEventStoreForTests();
        var scheduler = new RecordingRuntimeCallbackScheduler
        {
            ScheduleException = new InvalidOperationException("simulated durable scheduler failure"),
        };
        var publisher = new RecordingEventPublisher
        {
            FailurePredicate = static targetActorId => targetActorId == "service-run:session-1",
        };
        var actor = await CreateInitializedActorAsync(store, scheduler, publisher, "role-scheduler-failure");

        var act = () => CompleteSessionAsync(
            actor,
            "session-1",
            Now.AddMinutes(1).ToUnixTimeMilliseconds());

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("simulated durable scheduler failure");
        actor.State.Sessions["session-1"].CompletionNotificationDeliveryStatus.Should()
            .Be(RoleChatCompletionNotificationDeliveryStatus.Prepared);
        scheduler.TimeoutRequests.Should().BeEmpty();
        var recovery = publisher.SuccessfulPublications.Should()
            .ContainSingle(publication => publication.Audience == TopologyAudience.Self)
            .Subject;
        var retry = recovery.Event.Should().BeOfType<RoleChatCompletionNotificationRetryFiredEvent>().Subject;
        retry.SessionId.Should().Be("session-1");
        retry.DeliveryId.Should().Be("delivery-session-1");
        retry.Attempt.Should().Be(1);
        recovery.Options!.Delivery!.OperationId.Should()
            .Be("role-chat-completion-retry:session-1:delivery-session-1:1");

        scheduler.ScheduleException = null;
        var recoveryEnvelope = new EventEnvelope
        {
            Id = "self-recovery-1",
            Payload = Any.Pack(retry),
            Route = EnvelopeRouteSemantics.CreateTopologyPublication(actor.Id, TopologyAudience.Self),
            Runtime = new EnvelopeRuntime
            {
                DeliveryIdentity = new DeliveryIdentity
                {
                    OperationId = recovery.Options.Delivery.OperationId,
                },
            },
        };
        await actor.HandleEventAsync(recoveryEnvelope);

        var durableAttempt2 = scheduler.TimeoutRequests.Should().ContainSingle().Subject;
        durableAttempt2.CallbackId.Should().Be("role-chat-completion-retry:session-1:delivery-session-1");
        durableAttempt2.TriggerEnvelope.Payload.Unpack<RoleChatCompletionNotificationRetryFiredEvent>()
            .Attempt.Should().Be(2);
        var durableAttempt2OperationId = durableAttempt2.TriggerEnvelope.Runtime!.DeliveryIdentity!.OperationId;
        durableAttempt2OperationId.Should().Be("role-chat-completion-retry:session-1:delivery-session-1:2");
        durableAttempt2OperationId.Should().NotBe(recovery.Options.Delivery.OperationId);
        publisher.FailurePredicate = null;
        await actor.HandleEventAsync(durableAttempt2.TriggerEnvelope);

        publisher.SuccessfulSends.Should().ContainSingle();
        actor.State.Sessions["session-1"].CompletionNotificationDeliveryStatus.Should()
            .Be(RoleChatCompletionNotificationDeliveryStatus.Dispatched);
        actor.State.Sessions["session-1"].CompletionNotificationAttempt.Should().Be(2);
    }

    [Fact]
    public async Task DurableRetrySchedulerFailure_WhenSelfPublisherIgnoresCancellation_ShouldKeepOriginalFailureAndPreparedOutbox()
    {
        const int timeoutMs = 1_000;
        var store = new InMemoryEventStoreForTests();
        var timeProvider = new FakeTimeProvider(Now);
        var scheduler = new RecordingRuntimeCallbackScheduler
        {
            ScheduleException = new InvalidOperationException("simulated durable scheduler failure"),
        };
        var publicationProbe = new IgnoringCancellationProbe();
        var publisher = new RecordingEventPublisher
        {
            FailurePredicate = static targetActorId => targetActorId == "service-run:session-1",
            BeforePublishAsync = (evt, _) => evt is RoleChatCompletionNotificationRetryFiredEvent
                ? publicationProbe.WaitForReleaseAsync()
                : Task.CompletedTask,
        };
        var actor = CreateActor(
            store,
            scheduler,
            publisher,
            "role-scheduler-failure-hanging-recovery",
            timeProvider,
            new RoleChatExecutionOptions(postTurnProcessingTimeoutMs: timeoutMs));
        await actor.ActivateAsync();
        await actor.HandleInitializeRoleAgent(new InitializeRoleAgentEvent
        {
            RoleName = "assistant",
            ProviderName = "completion-test",
            SystemPrompt = "system",
        });

        var turn = CompleteSessionAsync(
            actor,
            "session-1",
            Now.AddMinutes(1).ToUnixTimeMilliseconds());
        await publicationProbe.Started;
        actor.State.Sessions["session-1"].CompletionNotificationDeliveryStatus.Should()
            .Be(RoleChatCompletionNotificationDeliveryStatus.Prepared);

        timeProvider.Advance(TimeSpan.FromMilliseconds(timeoutMs));
        Func<Task> waitForTurn = async () => await turn;
        await waitForTurn.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("simulated durable scheduler failure");

        actor.State.Sessions["session-1"].CompletionNotificationDeliveryStatus.Should()
            .Be(RoleChatCompletionNotificationDeliveryStatus.Prepared);
        publisher.SuccessfulPublications.Should().NotContain(publication =>
            publication.Audience == TopologyAudience.Self &&
            publication.Event is RoleChatCompletionNotificationRetryFiredEvent);
        publicationProbe.Release();
        await publicationProbe.Completed;
        actor.State.Sessions["session-1"].CompletionNotificationDeliveryStatus.Should()
            .Be(RoleChatCompletionNotificationDeliveryStatus.Prepared);
        (await store.GetEventsAsync(actor.Id)).Should().NotContain(stateEvent =>
            stateEvent.EventData.Is(RoleChatCompletionNotificationRetryScheduledEvent.Descriptor));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ApprovalContinuationCollision_ShouldPreservePendingDelivery(bool retryScheduled)
    {
        var store = new InMemoryEventStoreForTests();
        var scheduler = new RecordingRuntimeCallbackScheduler();
        var actor = await CreateInitializedActorAsync(
            store,
            scheduler,
            new RecordingEventPublisher(),
            $"role-approval-collision-{retryScheduled}");
        await PersistPreparedCompletionAsync(actor, "session-1");
        if (retryScheduled)
        {
            await actor.PersistForTestAsync(new RoleChatCompletionNotificationRetryScheduledEvent
            {
                SessionId = "session-1",
                DeliveryId = "delivery-session-1",
                Attempt = 1,
                CallbackId = "callback-1",
                RetryAt = Timestamp.FromDateTimeOffset(Now.AddMilliseconds(250)),
            });
        }
        var before = actor.State.Sessions["session-1"].Clone();
        var streams = new InMemoryStreamProvider();
        var committedPublisher = new LocalActorPublisher(actor.Id, static () => null, static () => 0, streams);
        typeof(GAgentBase)
            .GetProperty("CommittedStateEventPublisher", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(actor, committedPublisher);
        var publishedCompletions = new List<RoleChatSessionCompletedEvent>();
        await using var committedSubscription = await streams.GetStream(actor.Id)
            .SubscribeAsync<EventEnvelope>(envelope =>
            {
                if (envelope.Payload?.Is(CommittedStateEventPublished.Descriptor) == true)
                {
                    var published = envelope.Payload.Unpack<CommittedStateEventPublished>();
                    if (published.StateEvent?.EventData?.Is(RoleChatSessionCompletedEvent.Descriptor) == true)
                    {
                        publishedCompletions.Add(
                            published.StateEvent.EventData.Unpack<RoleChatSessionCompletedEvent>());
                    }
                }

                return Task.CompletedTask;
            });
        var completionCountBefore = (await store.GetEventsAsync(actor.Id)).Count(stateEvent =>
            stateEvent.EventData.Is(RoleChatSessionCompletedEvent.Descriptor));

        await actor.HandleToolApprovalDecision(new ToolApprovalDecisionEvent
        {
            RequestId = "request-not-pending",
            ContinuationTurnId = "session-1",
            Approved = true,
        });

        var completionCountAfter = (await store.GetEventsAsync(actor.Id)).Count(stateEvent =>
            stateEvent.EventData.Is(RoleChatSessionCompletedEvent.Descriptor));
        completionCountAfter.Should().Be(completionCountBefore);
        publishedCompletions.Should().BeEmpty();
        var after = actor.State.Sessions["session-1"];
        after.RunContext.Should().BeEquivalentTo(before.RunContext);
        after.CompletionNotificationDeliveryStatus.Should()
            .Be(before.CompletionNotificationDeliveryStatus);
        after.CompletionNotificationAttempt.Should().Be(before.CompletionNotificationAttempt);
        after.CompletionNotificationRetryCallbackId.Should()
            .Be(before.CompletionNotificationRetryCallbackId);
        after.CompletionNotificationRetryAt.Should().Be(before.CompletionNotificationRetryAt);
        after.FinalContent.Should().Be("original terminal content");
        after.Outcome.Should().Be(RoleChatSessionOutcome.Completed);
    }

    [Fact]
    public async Task ApprovalDenialContinuationCollision_ShouldNotCommitConflictingCompletion()
    {
        var store = new InMemoryEventStoreForTests();
        var actor = await CreateInitializedActorAsync(
            store,
            new RecordingRuntimeCallbackScheduler(),
            new RecordingEventPublisher(),
            "role-approval-denial-collision");
        await PersistPreparedCompletionAsync(actor, "session-1");
        actor.State.PendingApproval = new PendingToolApprovalState
        {
            RequestId = "request-pending",
            SessionId = "approval-origin-session",
            ScopeId = "scope-a",
            ToolName = "dangerous_tool",
            ToolCallId = "tool-call-1",
            ArgumentsJson = "{}",
        };
        var before = actor.State.Sessions["session-1"].Clone();
        var completionCountBefore = (await store.GetEventsAsync(actor.Id)).Count(stateEvent =>
            stateEvent.EventData.Is(RoleChatSessionCompletedEvent.Descriptor));

        await actor.HandleToolApprovalDecision(new ToolApprovalDecisionEvent
        {
            RequestId = "request-pending",
            ContinuationTurnId = "session-1",
            Approved = false,
            Reason = "denied",
        });

        var completionCountAfter = (await store.GetEventsAsync(actor.Id)).Count(stateEvent =>
            stateEvent.EventData.Is(RoleChatSessionCompletedEvent.Descriptor));
        completionCountAfter.Should().Be(completionCountBefore);
        actor.State.PendingApproval.Should().BeNull();
        actor.State.Sessions["session-1"].Should().BeEquivalentTo(before);
    }

    [Fact]
    public async Task RetryScheduledReducer_ShouldRequireEligibleStatusAndNextAttemptOnApplyAndReplay()
    {
        var store = new InMemoryEventStoreForTests();
        var actor = await CreateInitializedActorAsync(
            store,
            new RecordingRuntimeCallbackScheduler(),
            new RecordingEventPublisher(),
            "role-retry-reducer");
        await PersistPreparedCompletionAsync(actor, "session-1");

        await actor.PersistForTestAsync(RetryScheduledEvent("session-1", attempt: 2, "skipped-prepared"));
        AssertDeliveryState(actor, RoleChatCompletionNotificationDeliveryStatus.Prepared, 0, string.Empty);

        await actor.PersistForTestAsync(RetryScheduledEvent("session-1", attempt: 1, "callback-1"));
        AssertDeliveryState(actor, RoleChatCompletionNotificationDeliveryStatus.RetryScheduled, 1, "callback-1");

        await actor.PersistForTestAsync(RetryScheduledEvent("session-1", attempt: 1, "stale-callback"));
        await actor.PersistForTestAsync(RetryScheduledEvent("session-1", attempt: 3, "skipped-retry"));
        AssertDeliveryState(actor, RoleChatCompletionNotificationDeliveryStatus.RetryScheduled, 1, "callback-1");

        await actor.PersistForTestAsync(RetryScheduledEvent("session-1", attempt: 2, "callback-2"));
        await actor.PersistForTestAsync(new RoleChatCompletionNotificationDispatchedEvent
        {
            SessionId = "session-1",
            DeliveryId = "delivery-session-1",
            Attempt = 2,
        });
        await actor.PersistForTestAsync(RetryScheduledEvent("session-1", attempt: 3, "terminal-reopen"));
        AssertDeliveryState(actor, RoleChatCompletionNotificationDeliveryStatus.Dispatched, 2, string.Empty);

        var recovered = CreateActor(
            store,
            new RecordingRuntimeCallbackScheduler(),
            new RecordingEventPublisher(),
            "role-retry-reducer");
        await recovered.ActivateAsync();

        AssertDeliveryState(recovered, RoleChatCompletionNotificationDeliveryStatus.Dispatched, 2, string.Empty);
    }

    [Fact]
    public async Task TerminalDeliveryReducers_ShouldRequireEligibleStatusAndExactAttemptOnApplyAndReplay()
    {
        var store = new InMemoryEventStoreForTests();
        var actor = await CreateInitializedActorAsync(
            store,
            new RecordingRuntimeCallbackScheduler(),
            new RecordingEventPublisher(),
            "role-terminal-reducers");
        await PersistPreparedCompletionAsync(actor, "dispatched-session");
        await PersistPreparedCompletionAsync(actor, "expired-session");

        await actor.PersistForTestAsync(new RoleChatCompletionNotificationDispatchedEvent
        {
            SessionId = "dispatched-session",
            DeliveryId = "delivery-dispatched-session",
            Attempt = 2,
        });
        actor.State.Sessions["dispatched-session"].CompletionNotificationDeliveryStatus.Should()
            .Be(RoleChatCompletionNotificationDeliveryStatus.Prepared);
        await actor.PersistForTestAsync(new RoleChatCompletionNotificationDispatchedEvent
        {
            SessionId = "dispatched-session",
            DeliveryId = "delivery-dispatched-session",
            Attempt = 0,
        });
        await actor.PersistForTestAsync(new RoleChatCompletionNotificationExpiredEvent
        {
            SessionId = "dispatched-session",
            DeliveryId = "delivery-dispatched-session",
            Attempt = 0,
        });
        actor.State.Sessions["dispatched-session"].CompletionNotificationDeliveryStatus.Should()
            .Be(RoleChatCompletionNotificationDeliveryStatus.Dispatched);

        await actor.PersistForTestAsync(RetryScheduledEvent("expired-session", attempt: 1, "callback-expired"));
        await actor.PersistForTestAsync(new RoleChatCompletionNotificationExpiredEvent
        {
            SessionId = "expired-session",
            DeliveryId = "delivery-expired-session",
            Attempt = 0,
        });
        await actor.PersistForTestAsync(new RoleChatCompletionNotificationExpiredEvent
        {
            SessionId = "expired-session",
            DeliveryId = "delivery-expired-session",
            Attempt = 3,
        });
        AssertSessionDeliveryState(
            actor,
            "expired-session",
            RoleChatCompletionNotificationDeliveryStatus.RetryScheduled,
            1);
        await actor.PersistForTestAsync(new RoleChatCompletionNotificationExpiredEvent
        {
            SessionId = "expired-session",
            DeliveryId = "delivery-expired-session",
            Attempt = 2,
        });
        await actor.PersistForTestAsync(new RoleChatCompletionNotificationDispatchedEvent
        {
            SessionId = "expired-session",
            DeliveryId = "delivery-expired-session",
            Attempt = 2,
        });
        AssertSessionDeliveryState(
            actor,
            "expired-session",
            RoleChatCompletionNotificationDeliveryStatus.Expired,
            2);

        var recovered = CreateActor(
            store,
            new RecordingRuntimeCallbackScheduler(),
            new RecordingEventPublisher(),
            "role-terminal-reducers");
        await recovered.ActivateAsync();

        AssertSessionDeliveryState(
            recovered,
            "dispatched-session",
            RoleChatCompletionNotificationDeliveryStatus.Dispatched,
            0);
        AssertSessionDeliveryState(
            recovered,
            "expired-session",
            RoleChatCompletionNotificationDeliveryStatus.Expired,
            2);
    }

    private static async Task<TestRoleGAgent> CreateInitializedActorAsync(
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

    private static TestRoleGAgent CreateActor(
        IEventStore store,
        IActorRuntimeCallbackScheduler scheduler,
        RecordingEventPublisher publisher,
        string actorId,
        TimeProvider? timeProvider = null,
        RoleChatExecutionOptions? chatExecutionOptions = null)
    {
        var services = new ServiceCollection()
            .AddSingleton(store)
            .AddSingleton<EventSourcingRuntimeOptions>()
            .AddSingleton<IActorRuntimeCallbackScheduler>(scheduler)
            .AddTransient(typeof(IEventSourcingBehaviorFactory<>), typeof(DefaultEventSourcingBehaviorFactory<>))
            .BuildServiceProvider();
        var provider = new CompletionLlmProvider();
        var actor = new TestRoleGAgent(
            provider,
            timeProvider ?? new FixedTimeProvider(Now),
            chatExecutionOptions)
        {
            Services = services,
            EventPublisher = publisher,
            EventSourcingBehaviorFactory = services.GetRequiredService<IEventSourcingBehaviorFactory<RoleGAgentState>>(),
        };
        typeof(GAgentBase).GetMethod("SetId", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(actor, [actorId]);
        return actor;
    }

    private static async Task PersistPreparedCompletionAsync(TestRoleGAgent actor, string sessionId)
    {
        var runContext = new RoleChatRunContext
        {
            RunId = $"run-{sessionId}",
            CommandId = $"cmd-{sessionId}",
            CorrelationId = $"corr-{sessionId}",
            CompletionNotificationActorId = $"service-run:{sessionId}",
            CompletionNotificationDeliveryId = $"delivery-{sessionId}",
            CompletionNotificationExpiresAtUnixMs = Now.AddMinutes(1).ToUnixTimeMilliseconds(),
        };
        await actor.PersistForTestAsync(new RoleChatSessionStartedEvent
        {
            SessionId = sessionId,
            Prompt = $"prompt-{sessionId}",
            RunContext = runContext.Clone(),
        });
        await actor.PersistForTestAsync(new RoleChatSessionCompletedEvent
        {
            SessionId = sessionId,
            Prompt = $"prompt-{sessionId}",
            Content = "original terminal content",
            Outcome = RoleChatSessionOutcome.Completed,
            TerminalTime = Timestamp.FromDateTimeOffset(Now),
            RunContext = runContext,
            ActorId = actor.Id,
        });
    }

    private static RoleChatCompletionNotificationRetryScheduledEvent RetryScheduledEvent(
        string sessionId,
        int attempt,
        string callbackId) =>
        new()
        {
            SessionId = sessionId,
            DeliveryId = $"delivery-{sessionId}",
            Attempt = attempt,
            CallbackId = callbackId,
            RetryAt = Timestamp.FromDateTimeOffset(Now.AddMilliseconds(250 * attempt)),
        };

    private static void AssertDeliveryState(
        RoleGAgent actor,
        RoleChatCompletionNotificationDeliveryStatus status,
        int attempt,
        string callbackId) =>
        AssertSessionDeliveryState(actor, "session-1", status, attempt, callbackId);

    private static void AssertSessionDeliveryState(
        RoleGAgent actor,
        string sessionId,
        RoleChatCompletionNotificationDeliveryStatus status,
        int attempt,
        string? callbackId = null)
    {
        var session = actor.State.Sessions[sessionId];
        session.CompletionNotificationDeliveryStatus.Should().Be(status);
        session.CompletionNotificationAttempt.Should().Be(attempt);
        if (callbackId != null)
            session.CompletionNotificationRetryCallbackId.Should().Be(callbackId);
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

        public Func<string, bool>? FailurePredicate { get; set; }

        public Func<string, IMessage, CancellationToken, Task>? BeforeSendAsync { get; set; }

        public Func<IMessage, CancellationToken, Task>? BeforePublishAsync { get; set; }

        public List<(IMessage Event, TopologyAudience Audience, EventEnvelopePublishOptions? Options)>
            SuccessfulPublications { get; } = [];

        public List<(string TargetActorId, IMessage Event, EventEnvelopePublishOptions? Options)> SuccessfulSends { get; } = [];

        public async Task PublishAsync<TEvent>(
            TEvent evt,
            TopologyAudience audience = TopologyAudience.Children,
            CancellationToken ct = default,
            EventEnvelope? sourceEnvelope = null,
            EventEnvelopePublishOptions? options = null)
            where TEvent : IMessage
        {
            ct.ThrowIfCancellationRequested();
            if (BeforePublishAsync is not null)
                await BeforePublishAsync(evt, ct);
            ct.ThrowIfCancellationRequested();
            SuccessfulPublications.Add((evt, audience, options));
        }

        public async Task SendToAsync<TEvent>(
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
            if (BeforeSendAsync is not null)
                await BeforeSendAsync(targetActorId, evt, ct);
            ct.ThrowIfCancellationRequested();

            SuccessfulSends.Add((targetActorId, evt, options));
        }
    }

    private sealed class IgnoringCancellationProbe
    {
        private readonly TaskCompletionSource _started =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _completed =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Started => _started.Task;
        public Task Completed => _completed.Task;

        public async Task WaitForReleaseAsync()
        {
            _started.TrySetResult();
            await _release.Task;
            _completed.TrySetResult();
        }

        public void Release() => _release.TrySetResult();
    }

    private sealed class IgnoringCancellationRuntimeCallbackScheduler : IActorRuntimeCallbackScheduler
    {
        private readonly IgnoringCancellationProbe _probe = new();

        public Task Started => _probe.Started;
        public Task Completed => _probe.Completed;

        public async Task<RuntimeCallbackLease> ScheduleTimeoutAsync(
            RuntimeCallbackTimeoutRequest request,
            CancellationToken ct = default)
        {
            _ = ct;
            await _probe.WaitForReleaseAsync();
            return new RuntimeCallbackLease(
                request.ActorId,
                request.CallbackId,
                1,
                RuntimeCallbackBackend.InMemory);
        }

        public Task<RuntimeCallbackLease> ScheduleTimerAsync(
            RuntimeCallbackTimerRequest request,
            CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task CancelAsync(RuntimeCallbackLease lease, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task PurgeActorAsync(string actorId, CancellationToken ct = default) =>
            Task.CompletedTask;

        public void Release() => _probe.Release();
    }

    private sealed class RecordingRuntimeCallbackScheduler : IActorRuntimeCallbackScheduler
    {
        public Exception? ScheduleException { get; set; }

        public List<RuntimeCallbackTimeoutRequest> TimeoutRequests { get; } = [];

        public Task<RuntimeCallbackLease> ScheduleTimeoutAsync(
            RuntimeCallbackTimeoutRequest request,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            if (ScheduleException != null)
                throw ScheduleException;
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

    private sealed class FailOnceDispatchedEventStore : IEventStore
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
                    stateEvent.EventData.Is(RoleChatCompletionNotificationDispatchedEvent.Descriptor)))
            {
                _failed = true;
                throw new InvalidOperationException("simulated dispatched event commit failure");
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

    private sealed class TestRoleGAgent(
        ILLMProviderFactory provider,
        TimeProvider timeProvider,
        RoleChatExecutionOptions? chatExecutionOptions = null)
        : RoleGAgent(
            TestAgentToolExecutionPort.Instance,
            provider,
            timeProvider: timeProvider,
            chatExecutionOptions: chatExecutionOptions)
    {
        public List<RoleChatSessionState> TerminalCommittedSessions { get; } = [];

        public Task PersistForTestAsync(IMessage evt) => PersistDomainEventAsync(evt);

        public Task CompleteForTestAsync(
            ChatRequestEvent request,
            RoleChatSessionOutcome outcome) =>
            PersistRoleChatSessionCompletionAsync(
                request,
                content: outcome == RoleChatSessionOutcome.Completed ? "confirmed result" : string.Empty,
                reasoningContent: string.Empty,
                toolCalls: [],
                contentParts: [],
                contentEmitted: false,
                outcome: outcome,
                failureCode: outcome == RoleChatSessionOutcome.Failed ? "CONFIRMED_FAILURE" : string.Empty,
                safeMessage: outcome == RoleChatSessionOutcome.Failed ? "The operation failed." : string.Empty);

        protected override Task OnRoleChatSessionTerminalCommittedAsync(
            string sessionId,
            CancellationToken ct)
        {
            TerminalCommittedSessions.Add(State.Sessions[sessionId].Clone());
            return Task.CompletedTask;
        }
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
