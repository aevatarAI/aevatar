using System.Reflection;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Attributes;
using Aevatar.Foundation.Abstractions.Hooks;
using Aevatar.Foundation.Abstractions.Persistence;
using Aevatar.Foundation.Abstractions.Runtime.Callbacks;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.Foundation.Runtime.Persistence;
using Aevatar.Scripting.Abstractions;
using Aevatar.Scripting.Application.Runtime;
using Aevatar.Scripting.Core;
using Aevatar.Scripting.Core.Compilation;
using Aevatar.Scripting.Core.Runtime;
using Aevatar.Scripting.Core.Tests.Messages;
using Aevatar.Scripting.Infrastructure.Compilation;
using Aevatar.Scripting.Infrastructure.Serialization;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.Scripting.Core.Tests.Runtime;

public sealed class ScriptBehaviorCompletionNotificationTests
{
    private const string ActorId = "script-runtime-1";
    private const string CompletionActorId = "service-run:tenant:svc:run-1";
    private static readonly DateTimeOffset FixedNow = DateTimeOffset.Parse("2026-07-22T08:00:00Z");

    [Fact]
    public async Task SecondRun_ShouldNotOverwriteFirstPendingOutcome()
    {
        var eventStore = new InMemoryEventStore();
        var scheduler = new RecordingCallbackScheduler();
        var publisher = new RecordingEventPublisher
        {
            FailurePredicate = static targetActorId => targetActorId == "completion-a",
        };
        var actor = await CreateBoundAgentAsync(eventStore, publisher, scheduler);

        await CompleteRunAsync(actor, "run-a", "cmd-a", "delivery-a", "completion-a");
        await CompleteRunAsync(actor, "run-b", "cmd-b", "delivery-b", "completion-b");

        actor.State.RunOutcomes.Should().ContainKeys("run-a", "run-b");
        actor.State.RunOutcomes["run-a"].Status.Should()
            .Be(ScriptRunOutcomeDeliveryStatus.RetryScheduled);
        actor.State.RunOutcomes["run-b"].Status.Should()
            .Be(ScriptRunOutcomeDeliveryStatus.Dispatched);
    }

    [Fact]
    public async Task Replay_ShouldResolveOutcomeByRequestedRunId()
    {
        var eventStore = new InMemoryEventStore();
        var scheduler = new RecordingCallbackScheduler();
        var publisher = new RecordingEventPublisher
        {
            FailurePredicate = static targetActorId => targetActorId == "completion-a",
        };
        var actor = await CreateBoundAgentAsync(eventStore, publisher, scheduler);

        var runA = BuildRunRequest("run-a", "cmd-a", "delivery-a", "completion-a");
        await actor.HandleEnvelopeAsync(BuildEnvelope(runA, "corr-run-a"));
        await CompleteRunAsync(actor, "run-b", "cmd-b", "delivery-b", "completion-b");
        publisher.FailurePredicate = null;
        publisher.SuccessfulSends.Clear();

        await actor.HandleEnvelopeAsync(BuildEnvelope(runA, "corr-run-a"));

        var replayed = publisher.SuccessfulSends.Should().ContainSingle().Subject;
        replayed.Event.Should().BeOfType<ScriptRunOutcomeRecordedEvent>()
            .Which.ScriptRunId.Should().Be("run-a");
        actor.State.RunOutcomes["run-a"].Status.Should()
            .Be(ScriptRunOutcomeDeliveryStatus.Dispatched);
        (await eventStore.GetEventsAsync(ActorId)).Count(stateEvent =>
                stateEvent.EventData.Is(ScriptRunOutcomeRecordedEvent.Descriptor) &&
                stateEvent.EventData.Unpack<ScriptRunOutcomeRecordedEvent>().ScriptRunId == "run-a")
            .Should().Be(1);
    }

    [Fact]
    public async Task SendFailure_ShouldScheduleDurableRetry()
    {
        var scheduler = new RecordingCallbackScheduler();
        var publisher = new RecordingEventPublisher
        {
            SendException = new InvalidOperationException("simulated completion notification failure"),
        };
        var actor = await CreateBoundAgentAsync(new InMemoryEventStore(), publisher, scheduler);

        await CompleteRunAsync(actor, "run-1", "cmd-1", "delivery-1", CompletionActorId);
        for (var index = 0; index < 3; index++)
            await actor.HandleEventAsync(scheduler.TimeoutRequests[index].TriggerEnvelope);

        var delivery = actor.State.RunOutcomes["run-1"];
        delivery.Status.Should().Be(ScriptRunOutcomeDeliveryStatus.RetryScheduled);
        delivery.Attempt.Should().Be(4);
        scheduler.TimeoutRequests.Select(static request => request.CallbackId)
            .Should().OnlyContain(static callbackId =>
                callbackId == "script-run-terminal-retry:run-1:delivery-1");
        scheduler.TimeoutRequests.Select(static request => request.DueTime).Should().Equal(
            TimeSpan.FromMilliseconds(250),
            TimeSpan.FromMilliseconds(500),
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(2));

        var operationIds = scheduler.TimeoutRequests
            .Select(static request => request.TriggerEnvelope.Runtime!.DeliveryIdentity!.OperationId)
            .ToArray();
        operationIds.Should().OnlyHaveUniqueItems();

        var handler = typeof(ScriptBehaviorGAgent).GetMethod(
            nameof(ScriptBehaviorGAgent.HandleRunOutcomeRetryFiredAsync));
        handler.Should().NotBeNull();
        var attribute = handler!.GetCustomAttribute<EventHandlerAttribute>();
        attribute.Should().NotBeNull();
        attribute!.AllowSelfHandling.Should().BeTrue();
        attribute.OnlySelfHandling.Should().BeTrue();
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task RetryFired_ShouldDispatchMatchingOutcome(int failedRetryScheduledCommitAttempt)
    {
        var eventStore = new FailOnceRetryScheduledEventStore(failedRetryScheduledCommitAttempt);
        var scheduler = new RecordingCallbackScheduler();
        var publisher = new RecordingEventPublisher
        {
            SendException = new InvalidOperationException("simulated completion notification failure"),
        };
        var actor = await CreateBoundAgentAsync(eventStore, publisher, scheduler);

        if (failedRetryScheduledCommitAttempt == 1)
        {
            var complete = () => CompleteRunAsync(
                actor,
                "run-1",
                "cmd-1",
                "delivery-1",
                CompletionActorId);
            await complete.Should().ThrowAsync<InvalidOperationException>()
                .WithMessage("simulated retry-scheduled commit failure");
            actor.State.RunOutcomes["run-1"].Status.Should()
                .Be(ScriptRunOutcomeDeliveryStatus.Prepared);
        }
        else
        {
            await CompleteRunAsync(actor, "run-1", "cmd-1", "delivery-1", CompletionActorId);
            var firstRetry = () => actor.HandleEventAsync(scheduler.TimeoutRequests[0].TriggerEnvelope);
            await firstRetry.Should().ThrowAsync<InvalidOperationException>()
                .WithMessage("simulated retry-scheduled commit failure");
            actor.State.RunOutcomes["run-1"].Status.Should()
                .Be(ScriptRunOutcomeDeliveryStatus.RetryScheduled);
            actor.State.RunOutcomes["run-1"].Attempt.Should().Be(1);
        }

        var recovery = scheduler.TimeoutRequests[^1].TriggerEnvelope;
        recovery.Payload.Unpack<ScriptRunOutcomeNotificationRetryFiredEvent>().Attempt.Should()
            .Be(failedRetryScheduledCommitAttempt);
        publisher.SendException = null;

        await actor.HandleEventAsync(recovery);
        await actor.HandleEventAsync(recovery);

        publisher.SuccessfulSends.Should().ContainSingle();
        actor.State.RunOutcomes["run-1"].Status.Should()
            .Be(ScriptRunOutcomeDeliveryStatus.Dispatched);
        actor.State.RunOutcomes["run-1"].Attempt.Should().Be(failedRetryScheduledCommitAttempt);
    }

    [Fact]
    public async Task Pruning_ShouldRemoveOnlyOldestDispatchedOrExpiredOutcome()
    {
        var publisher = new RecordingEventPublisher
        {
            FailurePredicate = static targetActorId => targetActorId == "completion-pending",
        };
        var actor = await CreateBoundAgentAsync(
            new InMemoryEventStore(),
            publisher,
            new RecordingCallbackScheduler());

        await CompleteRunAsync(
            actor,
            "run-pending",
            "cmd-pending",
            "delivery-pending",
            "completion-pending");
        await CompleteRunAsync(
            actor,
            "run-expired-oldest",
            "cmd-expired",
            "delivery-expired",
            "completion-expired",
            DateTimeOffset.UtcNow.AddSeconds(-1).ToUnixTimeMilliseconds());
        for (var index = 0; index < 64; index++)
        {
            await CompleteRunAsync(
                actor,
                $"run-terminal-{index:D2}",
                $"cmd-terminal-{index:D2}",
                $"delivery-terminal-{index:D2}",
                $"completion-terminal-{index:D2}");
        }

        actor.State.RunOutcomes.Should().HaveCount(65);
        actor.State.RunOutcomes.Should().ContainKey("run-pending");
        actor.State.RunOutcomes["run-pending"].Status.Should()
            .Be(ScriptRunOutcomeDeliveryStatus.RetryScheduled);
        actor.State.RunOutcomes.Should().NotContainKey("run-expired-oldest");
        actor.State.RunOutcomes.Values.Count(static delivery =>
                delivery.Status is ScriptRunOutcomeDeliveryStatus.Dispatched or
                    ScriptRunOutcomeDeliveryStatus.Expired)
            .Should().Be(64);
    }

    [Fact]
    public async Task DeadlineElapsed_ShouldExpirePendingOutcome()
    {
        var scheduler = new RecordingCallbackScheduler();
        var publisher = new RecordingEventPublisher();
        var actor = await CreateBoundAgentAsync(new InMemoryEventStore(), publisher, scheduler);

        await CompleteRunAsync(
            actor,
            "run-1",
            "cmd-1",
            "delivery-1",
            CompletionActorId,
            DateTimeOffset.UtcNow.AddSeconds(-1).ToUnixTimeMilliseconds());

        publisher.SuccessfulSends.Should().BeEmpty();
        scheduler.TimeoutRequests.Should().BeEmpty();
        actor.State.RunOutcomes["run-1"].Status.Should()
            .Be(ScriptRunOutcomeDeliveryStatus.Expired);
    }

    [Fact]
    public async Task DeadlineZero_ShouldRemainUnlimitedAndDispatchOutcome()
    {
        var scheduler = new RecordingCallbackScheduler();
        var publisher = new RecordingEventPublisher();
        var actor = await CreateBoundAgentAsync(
            new InMemoryEventStore(),
            publisher,
            scheduler,
            new FixedTimeProvider(FixedNow));

        await CompleteRunAsync(
            actor,
            "run-no-deadline",
            "cmd-no-deadline",
            "delivery-no-deadline",
            CompletionActorId,
            expiresAtUnixMs: 0);

        publisher.SuccessfulSends.Should().ContainSingle()
            .Which.TargetActorId.Should().Be(CompletionActorId);
        scheduler.TimeoutRequests.Should().BeEmpty();
        var delivery = actor.State.RunOutcomes.Should()
            .ContainKey("run-no-deadline").WhoseValue;
        delivery.ExpiresAtUnixTimeMs.Should().Be(0);
        delivery.Attempt.Should().Be(0);
        delivery.Status.Should().Be(ScriptRunOutcomeDeliveryStatus.Dispatched);
        delivery.Status.Should().NotBe(ScriptRunOutcomeDeliveryStatus.Expired);
    }

    [Fact]
    public async Task ActivateAsync_ShouldAttemptEveryPendingOutcomeInOccurrenceOrder()
    {
        var eventStore = new InMemoryEventStore();
        var failingPublisher = new RecordingEventPublisher
        {
            SendException = new InvalidOperationException("simulated completion notification failure"),
        };
        var first = await CreateBoundAgentAsync(
            eventStore,
            failingPublisher,
            new RecordingCallbackScheduler());
        await CompleteRunAsync(first, "run-z-first", "cmd-first", "delivery-first", "completion-first");
        await CompleteRunAsync(first, "run-a-second", "cmd-second", "delivery-second", "completion-second");

        var recoveredPublisher = new RecordingEventPublisher();
        var recovered = CreateAgent(eventStore, recoveredPublisher, new RecordingCallbackScheduler());

        await recovered.ActivateAsync();

        recoveredPublisher.SuccessfulSends
            .Select(static send => ((ScriptRunOutcomeRecordedEvent)send.Event).ScriptRunId)
            .Should().Equal("run-z-first", "run-a-second");
        recovered.State.RunOutcomes.Values.Should().OnlyContain(static delivery =>
            delivery.Status == ScriptRunOutcomeDeliveryStatus.Dispatched);
    }

    [Fact]
    public async Task DispatchedCommitFailure_ShouldScheduleRetryAndRemainObservable()
    {
        var eventStore = new FailOnceDispatchedEventStore();
        var scheduler = new RecordingCallbackScheduler();
        var publisher = new RecordingEventPublisher();
        var actor = await CreateBoundAgentAsync(eventStore, publisher, scheduler);

        var complete = () => CompleteRunAsync(
            actor,
            "run-1",
            "cmd-1",
            "delivery-1",
            CompletionActorId);

        await complete.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("simulated dispatched event commit failure");
        publisher.SuccessfulSends.Should().ContainSingle();
        scheduler.TimeoutRequests.Should().ContainSingle();
        actor.State.RunOutcomes["run-1"].Status.Should()
            .Be(ScriptRunOutcomeDeliveryStatus.RetryScheduled);
        actor.State.RunOutcomes["run-1"].Attempt.Should().Be(1);
    }

    [Fact]
    public async Task ActivateAsync_WhenFirstPendingFails_ShouldAttemptLaterOutcomesBeforeRethrowing()
    {
        var eventStore = new InMemoryEventStore();
        var first = await CreateBoundAgentAsync(
            eventStore,
            new RecordingEventPublisher
            {
                SendException = new InvalidOperationException("seed pending outcomes"),
            },
            new RecordingCallbackScheduler());
        await CompleteRunAsync(first, "run-z-first", "cmd-first", "delivery-first", "completion-first");
        await CompleteRunAsync(first, "run-a-second", "cmd-second", "delivery-second", "completion-second");
        var scheduler = new RecordingCallbackScheduler
        {
            FailureFactory = static request =>
                request.TriggerEnvelope.Payload.Unpack<ScriptRunOutcomeNotificationRetryFiredEvent>().ScriptRunId ==
                "run-z-first"
                    ? new InvalidOperationException("simulated first pending scheduler failure")
                    : null,
        };
        var publisher = new RecordingEventPublisher
        {
            FailurePredicate = static targetActorId => targetActorId == "completion-first",
        };
        var recovered = CreateAgent(eventStore, publisher, scheduler);

        var activate = () => recovered.ActivateAsync();

        await activate.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("simulated first pending scheduler failure");
        publisher.SendAttempts.Select(static attempt => attempt.TargetActorId).Should()
            .Equal("completion-first", "completion-second");
        publisher.SuccessfulSends.Should().ContainSingle()
            .Which.TargetActorId.Should().Be("completion-second");
        recovered.State.RunOutcomes["run-z-first"].Status.Should()
            .Be(ScriptRunOutcomeDeliveryStatus.RetryScheduled);
        recovered.State.RunOutcomes["run-a-second"].Status.Should()
            .Be(ScriptRunOutcomeDeliveryStatus.Dispatched);
    }

    [Fact]
    public async Task ForeignRetryFiredEnvelope_ShouldNotSendOrMutateState()
    {
        var scheduler = new RecordingCallbackScheduler();
        var publisher = new RecordingEventPublisher
        {
            SendException = new InvalidOperationException("seed retry"),
        };
        var actor = await CreateBoundAgentAsync(new InMemoryEventStore(), publisher, scheduler);
        await CompleteRunAsync(actor, "run-1", "cmd-1", "delivery-1", CompletionActorId);
        var selfRetry = scheduler.TimeoutRequests.Should().ContainSingle().Subject.TriggerEnvelope;
        var foreignRetry = selfRetry.Clone();
        foreignRetry.Route = EnvelopeRouteSemantics.CreateDirect("foreign-actor", actor.Id);
        var before = actor.State.ToByteArray();
        publisher.SendException = null;
        publisher.SendAttempts.Clear();

        await actor.HandleEventAsync(foreignRetry);

        publisher.SendAttempts.Should().BeEmpty();
        actor.State.ToByteArray().Should().Equal(before);

        await actor.HandleEventAsync(selfRetry);

        publisher.SuccessfulSends.Should().ContainSingle();
        actor.State.RunOutcomes["run-1"].Status.Should()
            .Be(ScriptRunOutcomeDeliveryStatus.Dispatched);
    }

    [Fact]
    public async Task SendCancellation_ShouldPropagateWithoutRetry()
    {
        var scheduler = new RecordingCallbackScheduler();
        var publisher = new RecordingEventPublisher
        {
            SendException = new OperationCanceledException("simulated send cancellation"),
        };
        var actor = await CreateBoundAgentAsync(new InMemoryEventStore(), publisher, scheduler);

        var complete = () => CompleteRunAsync(
            actor,
            "run-1",
            "cmd-1",
            "delivery-1",
            CompletionActorId);

        await complete.Should().ThrowAsync<OperationCanceledException>()
            .WithMessage("simulated send cancellation");
        scheduler.TimeoutRequests.Should().BeEmpty();
        publisher.SuccessfulPublications.Should().BeEmpty();
        actor.State.RunOutcomes["run-1"].Status.Should()
            .Be(ScriptRunOutcomeDeliveryStatus.Prepared);
    }

    [Fact]
    public async Task SchedulerCancellation_ShouldPropagateWithoutRetryOrFallback()
    {
        var scheduler = new RecordingCallbackScheduler
        {
            ScheduleException = new OperationCanceledException("simulated scheduler cancellation"),
        };
        var publisher = new RecordingEventPublisher
        {
            SendException = new InvalidOperationException("simulated send failure"),
        };
        var actor = await CreateBoundAgentAsync(new InMemoryEventStore(), publisher, scheduler);

        var complete = () => CompleteRunAsync(
            actor,
            "run-1",
            "cmd-1",
            "delivery-1",
            CompletionActorId);

        await complete.Should().ThrowAsync<OperationCanceledException>()
            .WithMessage("simulated scheduler cancellation");
        scheduler.TimeoutRequests.Should().BeEmpty();
        publisher.SuccessfulPublications.Should().BeEmpty();
        actor.State.RunOutcomes["run-1"].Status.Should()
            .Be(ScriptRunOutcomeDeliveryStatus.Prepared);
    }

    [Fact]
    public async Task RetryBackoff_ShouldCapAtThirtySecondsAndDeadline()
    {
        var timeProvider = new FixedTimeProvider(FixedNow);
        var scheduler = new RecordingCallbackScheduler();
        var publisher = new RecordingEventPublisher
        {
            SendException = new InvalidOperationException("simulated send failure"),
        };
        var actor = await CreateBoundAgentAsync(
            new InMemoryEventStore(),
            publisher,
            scheduler,
            timeProvider);
        await CompleteRunAsync(
            actor,
            "run-cap",
            "cmd-cap",
            "delivery-cap",
            CompletionActorId,
            FixedNow.AddMinutes(5).ToUnixTimeMilliseconds());
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

        var deadlineScheduler = new RecordingCallbackScheduler();
        var deadlineActor = await CreateBoundAgentAsync(
            new InMemoryEventStore(),
            new RecordingEventPublisher
            {
                SendException = new InvalidOperationException("simulated send failure"),
            },
            deadlineScheduler,
            timeProvider);
        await CompleteRunAsync(
            deadlineActor,
            "run-deadline",
            "cmd-deadline",
            "delivery-deadline",
            CompletionActorId,
            FixedNow.AddMilliseconds(100).ToUnixTimeMilliseconds());

        deadlineScheduler.TimeoutRequests.Should().ContainSingle()
            .Which.DueTime.Should().Be(TimeSpan.FromMilliseconds(100));
    }

    [Fact]
    public async Task LaterAttemptSchedulerFailure_ShouldNotPublishSelfFallback()
    {
        var scheduler = new RecordingCallbackScheduler();
        var publisher = new RecordingEventPublisher
        {
            SendException = new InvalidOperationException("simulated send failure"),
        };
        var actor = await CreateBoundAgentAsync(new InMemoryEventStore(), publisher, scheduler);
        await CompleteRunAsync(actor, "run-1", "cmd-1", "delivery-1", CompletionActorId);
        var attemptOne = scheduler.TimeoutRequests.Should().ContainSingle().Subject.TriggerEnvelope;
        scheduler.ScheduleException = new InvalidOperationException("simulated later scheduler failure");

        for (var repeat = 0; repeat < 2; repeat++)
        {
            var retry = () => actor.HandleEventAsync(attemptOne);
            await retry.Should().ThrowAsync<InvalidOperationException>()
                .WithMessage("simulated later scheduler failure");
        }

        publisher.SuccessfulPublications.Should().BeEmpty();
        scheduler.TimeoutRequests.Should().ContainSingle();
        actor.State.RunOutcomes["run-1"].Status.Should()
            .Be(ScriptRunOutcomeDeliveryStatus.RetryScheduled);
        actor.State.RunOutcomes["run-1"].Attempt.Should().Be(1);
    }

    [Fact]
    public async Task DurableRetrySchedulerFailure_ShouldPublishOnlyOneSelfRecovery()
    {
        var scheduler = new RecordingCallbackScheduler
        {
            ScheduleException = new InvalidOperationException("simulated durable scheduler failure"),
        };
        var publisher = new RecordingEventPublisher
        {
            SendException = new InvalidOperationException("simulated completion notification failure"),
        };
        var actor = await CreateBoundAgentAsync(new InMemoryEventStore(), publisher, scheduler);

        var complete = () => CompleteRunAsync(
            actor,
            "run-1",
            "cmd-1",
            "delivery-1",
            CompletionActorId);

        await complete.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("simulated durable scheduler failure");
        var recovery = publisher.SuccessfulPublications.Should().ContainSingle(publication =>
            publication.Audience == TopologyAudience.Self).Subject;
        var retry = recovery.Event.Should()
            .BeOfType<ScriptRunOutcomeNotificationRetryFiredEvent>().Subject;
        retry.Attempt.Should().Be(1);
        recovery.Options!.Delivery!.OperationId.Should()
            .Be("script-run-terminal-retry:run-1:delivery-1:1");

        scheduler.ScheduleException = null;
        var recoveryEnvelope = BuildSelfEnvelope(actor.Id, retry, recovery.Options);
        publisher.SendException = null;
        await actor.HandleEventAsync(recoveryEnvelope);
        await actor.HandleEventAsync(recoveryEnvelope);

        publisher.SuccessfulPublications.Should().ContainSingle();
        publisher.SuccessfulSends.Should().ContainSingle();
        actor.State.RunOutcomes["run-1"].Status.Should()
            .Be(ScriptRunOutcomeDeliveryStatus.Dispatched);
    }

    [Fact]
    public void DeliveryReducers_ShouldRequireMatchingIdentityStatusAndExactAttempt()
    {
        var actor = CreateAgent(
            new InMemoryEventStore(),
            new RecordingEventPublisher(),
            new RecordingCallbackScheduler());
        var state = new ScriptBehaviorState
        {
            LastAppliedEventVersion = 41,
            LastEventId = "before-rejected-delivery-event",
            RunOutcomes =
            {
                ["run-1"] = new ScriptRunOutcomeDeliveryState
                {
                    Outcome = new ScriptRunOutcomeRecordedEvent
                    {
                        ScriptRunId = "run-1",
                        DeliveryId = "delivery-1",
                        StateVersion = 1,
                    },
                    DeliveryId = "delivery-1",
                    Status = ScriptRunOutcomeDeliveryStatus.Prepared,
                },
            },
        };

        AssertRejected(actor, state, RetryScheduled("run-other", "delivery-1", 1, "wrong-run"));
        AssertRejected(actor, state, RetryScheduled("run-1", "delivery-other", 1, "wrong-delivery"));
        AssertRejected(actor, state, RetryScheduled("run-1", "delivery-1", 2, "skipped"));
        state.RunOutcomes["run-1"].Status.Should().Be(ScriptRunOutcomeDeliveryStatus.Prepared);

        state = Reduce(actor, state, RetryScheduled("run-1", "delivery-1", 1, "callback-1"));
        AssertRejected(actor, state, RetryScheduled("run-1", "delivery-1", 1, "stale"));
        AssertRejected(actor, state, RetryScheduled("run-1", "delivery-1", 3, "skipped"));
        state.RunOutcomes["run-1"].RetryCallbackId.Should().Be("callback-1");
        AssertRejected(actor, state, new ScriptRunOutcomeNotificationDispatchedEvent
        {
            ScriptRunId = "run-1",
            DeliveryId = "delivery-other",
            Attempt = 1,
        });
        AssertRejected(actor, state, new ScriptRunOutcomeNotificationDispatchedEvent
        {
            ScriptRunId = "run-1",
            DeliveryId = "delivery-1",
            Attempt = 0,
        });
        AssertRejected(actor, state, new ScriptRunOutcomeNotificationExpiredEvent
        {
            ScriptRunId = "run-1",
            DeliveryId = "delivery-1",
            Attempt = 2,
        });
        state.RunOutcomes["run-1"].Status.Should()
            .Be(ScriptRunOutcomeDeliveryStatus.RetryScheduled);

        state = Reduce(actor, state, new ScriptRunOutcomeNotificationDispatchedEvent
        {
            ScriptRunId = "run-1",
            DeliveryId = "delivery-1",
            Attempt = 1,
        });
        AssertRejected(actor, state, RetryScheduled("run-1", "delivery-1", 2, "terminal-reopen"));
        AssertRejected(actor, state, new ScriptRunOutcomeNotificationExpiredEvent
        {
            ScriptRunId = "run-1",
            DeliveryId = "delivery-1",
            Attempt = 1,
        });
        AssertRejected(actor, state, new ScriptRunOutcomeNotificationDispatchedEvent
        {
            ScriptRunId = "run-1",
            DeliveryId = "delivery-1",
            Attempt = 1,
        });
        state.RunOutcomes["run-1"].Status.Should().Be(ScriptRunOutcomeDeliveryStatus.Dispatched);
        state.RunOutcomes["run-1"].Attempt.Should().Be(1);
    }

    private static void AssertRejected(
        ScriptBehaviorGAgent actor,
        ScriptBehaviorState state,
        IMessage evt)
    {
        var before = state.ToByteArray();

        var reduced = Reduce(actor, state, evt);

        reduced.Should().BeSameAs(state);
        reduced.ToByteArray().Should().Equal(before);
        reduced.LastAppliedEventVersion.Should().Be(state.LastAppliedEventVersion);
        reduced.LastEventId.Should().Be(state.LastEventId);
    }

    private static ScriptBehaviorState Reduce(
        ScriptBehaviorGAgent actor,
        ScriptBehaviorState state,
        IMessage evt)
    {
        var transition = typeof(ScriptBehaviorGAgent).GetMethod(
            "TransitionState",
            BindingFlags.Instance | BindingFlags.NonPublic);
        transition.Should().NotBeNull();
        return (ScriptBehaviorState)transition!.Invoke(actor, [state, evt])!;
    }

    private static ScriptRunOutcomeNotificationRetryScheduledEvent RetryScheduled(
        string runId,
        string deliveryId,
        int attempt,
        string callbackId) =>
        new()
        {
            ScriptRunId = runId,
            DeliveryId = deliveryId,
            Attempt = attempt,
            RetryCallbackId = callbackId,
            RetryAtUnixTimeMs = DateTimeOffset.UtcNow.AddSeconds(1).ToUnixTimeMilliseconds(),
        };

    private static async Task<ScriptBehaviorGAgent> CreateBoundAgentAsync(
        IEventStore eventStore,
        RecordingEventPublisher publisher,
        RecordingCallbackScheduler scheduler,
        TimeProvider? timeProvider = null)
    {
        var actor = CreateAgent(eventStore, publisher, scheduler, timeProvider);
        await actor.ActivateAsync();
        await BindAsync(actor);
        return actor;
    }

    private static ScriptBehaviorGAgent CreateAgent(
        IEventStore eventStore,
        IEventPublisher publisher,
        IActorRuntimeCallbackScheduler scheduler,
        TimeProvider? timeProvider = null)
    {
        var artifactResolver = new CachedScriptBehaviorArtifactResolver(
            new RoslynScriptBehaviorCompiler(new ScriptSandboxPolicy()));
        var codec = new ProtobufMessageCodec();
        var dispatcher = new ScriptBehaviorDispatcher(artifactResolver, codec);
        var capabilityFactory = new ScriptBehaviorRuntimeCapabilityFactory(
                new RecordingAICapability(),
                new RecordingProposalPort(),
                new RecordingDefinitionCommandPort(),
                new RecordingRuntimeProvisioningPort(),
                new RecordingRuntimeCommandPort(),
                new RecordingCatalogCommandPort());
        var agent = new ScriptBehaviorGAgent(
            dispatcher,
            capabilityFactory,
            artifactResolver,
            codec,
            timeProvider);
        agent.EventPublisher = publisher;
        agent.EventSourcingBehaviorFactory = new DefaultEventSourcingBehaviorFactory<ScriptBehaviorState>(eventStore);
        agent.Services = new ServiceCollection()
            .AddSingleton(scheduler)
            .AddSingleton<IEnumerable<IGAgentExecutionHook>>([])
            .BuildServiceProvider();
        var setId = typeof(GAgentBase).GetMethod(
            "SetId",
            BindingFlags.Instance | BindingFlags.NonPublic);
        setId.Should().NotBeNull();
        setId!.Invoke(agent, [ActorId]);
        return agent;
    }

    private static Task BindAsync(ScriptBehaviorGAgent agent) =>
        agent.HandleEnvelopeAsync(BuildEnvelope(new BindScriptBehaviorRequestedEvent
        {
            DefinitionActorId = "definition-1",
            ScriptId = "script-1",
            Revision = "rev-1",
            SourceHash = ScriptSources.UppercaseBehaviorHash,
            ScriptPackage = ScriptPackageSpecExtensions.CreateSingleSource(ScriptSources.UppercaseBehavior),
            StateTypeUrl = ScriptSources.UppercaseStateTypeUrl,
            ReadModelTypeUrl = ScriptSources.UppercaseReadModelTypeUrl,
            ReadModelSchemaVersion = "1",
            ReadModelSchemaHash = "schema-hash",
            ScopeId = "scope-1",
        }, "bind-1"));

    private static Task CompleteRunAsync(
        ScriptBehaviorGAgent actor,
        string runId,
        string commandId,
        string deliveryId,
        string completionActorId,
        long? expiresAtUnixMs = null) =>
        actor.HandleEnvelopeAsync(BuildEnvelope(
            BuildRunRequest(runId, commandId, deliveryId, completionActorId, expiresAtUnixMs),
            $"corr-{runId}"));

    private static RunScriptRequestedEvent BuildRunRequest(
        string runId,
        string commandId,
        string deliveryId,
        string completionActorId,
        long? expiresAtUnixMs = null) =>
        new()
        {
            RunId = runId,
            CommandId = commandId,
            CorrelationId = $"corr-{runId}",
            DefinitionActorId = "definition-1",
            ScriptRevision = "rev-1",
            RequestedEventType = "integration.requested",
            ScopeId = "scope-1",
            CompletionNotificationActorId = completionActorId,
            CompletionNotificationDeliveryId = deliveryId,
            CompletionNotificationExpiresAtUnixMs = expiresAtUnixMs ??
                DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeMilliseconds(),
            InputPayload = Any.Pack(new SimpleTextCommand
            {
                CommandId = commandId,
                Value = "hello",
            }),
        };

    private static EventEnvelope BuildEnvelope(IMessage payload, string correlationId) =>
        new()
        {
            Id = Guid.NewGuid().ToString("N"),
            Payload = Any.Pack(payload),
            Timestamp = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            Route = EnvelopeRouteSemantics.CreateTopologyPublication(
                "script-completion-notification-test",
                TopologyAudience.Self),
            Propagation = new EnvelopePropagation { CorrelationId = correlationId },
        };

    private static EventEnvelope BuildSelfEnvelope(
        string actorId,
        IMessage payload,
        EventEnvelopePublishOptions options) =>
        new()
        {
            Id = Guid.NewGuid().ToString("N"),
            Payload = Any.Pack(payload),
            Route = EnvelopeRouteSemantics.CreateTopologyPublication(actorId, TopologyAudience.Self),
            Runtime = new EnvelopeRuntime
            {
                DeliveryIdentity = new DeliveryIdentity
                {
                    OperationId = options.Delivery!.OperationId,
                },
            },
        };

    private sealed class RecordingEventPublisher : IEventPublisher
    {
        public Exception? SendException { get; set; }

        public Func<string, bool>? FailurePredicate { get; set; }

        public List<SentMessage> SuccessfulSends { get; } = [];

        public List<SentMessage> SendAttempts { get; } = [];

        public List<PublishedMessage> SuccessfulPublications { get; } = [];

        public Task PublishAsync<TEvent>(
            TEvent evt,
            TopologyAudience audience = TopologyAudience.Children,
            CancellationToken ct = default,
            EventEnvelope? sourceEnvelope = null,
            EventEnvelopePublishOptions? options = null)
            where TEvent : IMessage
        {
            ct.ThrowIfCancellationRequested();
            SuccessfulPublications.Add(new PublishedMessage(evt, audience, options));
            return Task.CompletedTask;
        }

        public Task SendToAsync<TEvent>(
            string targetActorId,
            TEvent evt,
            CancellationToken ct = default,
            EventEnvelope? sourceEnvelope = null,
            EventEnvelopePublishOptions? options = null)
            where TEvent : IMessage
        {
            ct.ThrowIfCancellationRequested();
            SendAttempts.Add(new SentMessage(targetActorId, evt, options));
            if (SendException != null || FailurePredicate?.Invoke(targetActorId) == true)
                throw SendException ?? new InvalidOperationException("simulated completion notification failure");

            SuccessfulSends.Add(new SentMessage(targetActorId, evt, options));
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingCallbackScheduler : IActorRuntimeCallbackScheduler
    {
        public Exception? ScheduleException { get; set; }

        public Func<RuntimeCallbackTimeoutRequest, Exception?>? FailureFactory { get; set; }

        public List<RuntimeCallbackTimeoutRequest> TimeoutRequests { get; } = [];

        public Task<RuntimeCallbackLease> ScheduleTimeoutAsync(
            RuntimeCallbackTimeoutRequest request,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            if (ScheduleException != null)
                throw ScheduleException;
            if (FailureFactory?.Invoke(request) is { } failure)
                throw failure;

            TimeoutRequests.Add(request);
            return Task.FromResult(new RuntimeCallbackLease(
                request.ActorId,
                request.CallbackId,
                TimeoutRequests.Count,
                RuntimeCallbackBackend.InMemory));
        }

        public Task<RuntimeCallbackLease> ScheduleTimerAsync(
            RuntimeCallbackTimerRequest request,
            CancellationToken ct = default) =>
            Task.FromResult(new RuntimeCallbackLease(
                request.ActorId,
                request.CallbackId,
                0,
                RuntimeCallbackBackend.InMemory));

        public Task CancelAsync(RuntimeCallbackLease lease, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task PurgeActorAsync(string actorId, CancellationToken ct = default) =>
            Task.CompletedTask;
    }

    private sealed class FailOnceRetryScheduledEventStore(int failedAttempt) : IEventStore
    {
        private readonly InMemoryEventStore _inner = new();
        private bool _failed;

        public Task<EventStoreCommitResult> AppendAsync(
            string agentId,
            IEnumerable<StateEvent> events,
            long expectedVersion,
            CancellationToken ct = default)
        {
            var buffered = events.ToArray();
            if (!_failed && buffered.Any(stateEvent =>
                    stateEvent.EventData.Is(ScriptRunOutcomeNotificationRetryScheduledEvent.Descriptor) &&
                    stateEvent.EventData.Unpack<ScriptRunOutcomeNotificationRetryScheduledEvent>().Attempt ==
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

    private sealed class FailOnceDispatchedEventStore : IEventStore
    {
        private readonly InMemoryEventStore _inner = new();
        private bool _failed;

        public Task<EventStoreCommitResult> AppendAsync(
            string agentId,
            IEnumerable<StateEvent> events,
            long expectedVersion,
            CancellationToken ct = default)
        {
            var buffered = events.ToArray();
            if (!_failed && buffered.Any(stateEvent =>
                    stateEvent.EventData.Is(ScriptRunOutcomeNotificationDispatchedEvent.Descriptor)))
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

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed record SentMessage(
        string TargetActorId,
        IMessage Event,
        EventEnvelopePublishOptions? Options);

    private sealed record PublishedMessage(
        IMessage Event,
        TopologyAudience Audience,
        EventEnvelopePublishOptions? Options);
}
