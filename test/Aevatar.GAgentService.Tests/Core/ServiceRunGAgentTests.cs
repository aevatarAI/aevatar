using System.Reflection;
using Aevatar.AI.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Persistence;
using Aevatar.Foundation.Abstractions.Runtime.Callbacks;
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.Foundation.Runtime.Persistence;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Core.GAgents;
using Aevatar.GAgentService.Tests.TestSupport;
using Aevatar.Scripting.Abstractions;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.GAgentService.Tests.Core;

public sealed class ServiceRunGAgentTests
{
    [Fact]
    public async Task HandleRegisterAsync_ShouldPersistRecord_AndDefaultStatusToAccepted()
    {
        var actor = GAgentServiceTestKit.CreateStatefulAgent<ServiceRunGAgent, ServiceRunState>(
            new InMemoryEventStore(),
            "service-run:run-1",
            static () => new ServiceRunGAgent());
        await actor.ActivateAsync();

        await actor.HandleRegisterAsync(new RegisterServiceRunRequested
        {
            Record = BuildRecord("run-1"),
        });

        actor.State.Record.Should().NotBeNull();
        actor.State.Record!.RunId.Should().Be("run-1");
        actor.State.Record.Status.Should().Be(ServiceRunStatus.Accepted);
        actor.State.LastAppliedEventVersion.Should().Be(1);
    }

    [Fact]
    public async Task HandleRegisterAsync_ShouldBeIdempotent_WhenRunIdAlreadyBound()
    {
        var actor = GAgentServiceTestKit.CreateStatefulAgent<ServiceRunGAgent, ServiceRunState>(
            new InMemoryEventStore(),
            "service-run:run-1",
            static () => new ServiceRunGAgent());

        await actor.HandleRegisterAsync(new RegisterServiceRunRequested
        {
            Record = BuildRecord("run-1"),
        });
        await actor.HandleRegisterAsync(new RegisterServiceRunRequested
        {
            Record = BuildRecord("run-1"),
        });

        actor.State.LastAppliedEventVersion.Should().Be(1);
    }

    [Fact]
    public async Task HandleRegisterAsync_ShouldRejectMismatchedRunId()
    {
        var actor = GAgentServiceTestKit.CreateStatefulAgent<ServiceRunGAgent, ServiceRunState>(
            new InMemoryEventStore(),
            "service-run:run-1",
            static () => new ServiceRunGAgent());
        await actor.HandleRegisterAsync(new RegisterServiceRunRequested
        {
            Record = BuildRecord("run-1"),
        });

        var act = () => actor.HandleRegisterAsync(new RegisterServiceRunRequested
        {
            Record = BuildRecord("run-2"),
        });

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*run-1*cannot register run 'run-2'*");
    }

    [Fact]
    public async Task HandleRegisterAsync_ShouldRejectScopeMismatchOnReRegister()
    {
        var actor = GAgentServiceTestKit.CreateStatefulAgent<ServiceRunGAgent, ServiceRunState>(
            new InMemoryEventStore(),
            "service-run:tenant-1:svc-1:run-1",
            static () => new ServiceRunGAgent());
        await actor.HandleRegisterAsync(new RegisterServiceRunRequested
        {
            Record = BuildRecord("run-1"),
        });

        var foreign = BuildRecord("run-1");
        foreign.ScopeId = "tenant-2";
        var act = () => actor.HandleRegisterAsync(new RegisterServiceRunRequested { Record = foreign });

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*tenant-1*cannot re-register under scope 'tenant-2'*");
    }

    [Fact]
    public async Task HandleRegisterAsync_ShouldRejectServiceMismatchOnReRegister()
    {
        var actor = GAgentServiceTestKit.CreateStatefulAgent<ServiceRunGAgent, ServiceRunState>(
            new InMemoryEventStore(),
            "service-run:tenant-1:svc-1:run-1",
            static () => new ServiceRunGAgent());
        await actor.HandleRegisterAsync(new RegisterServiceRunRequested
        {
            Record = BuildRecord("run-1"),
        });

        var foreign = BuildRecord("run-1");
        foreign.ServiceId = "svc-2";
        var act = () => actor.HandleRegisterAsync(new RegisterServiceRunRequested { Record = foreign });

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*svc-1*cannot re-register under service 'svc-2'*");
    }

    [Fact]
    public async Task HandleRegisterAsync_ShouldRejectTargetMismatchOnReRegister()
    {
        var actor = GAgentServiceTestKit.CreateStatefulAgent<ServiceRunGAgent, ServiceRunState>(
            new InMemoryEventStore(),
            "service-run:tenant-1:svc-1:run-1",
            static () => new ServiceRunGAgent());
        await actor.HandleRegisterAsync(new RegisterServiceRunRequested
        {
            Record = BuildRecord("run-1"),
        });

        var foreign = BuildRecord("run-1");
        foreign.TargetActorId = "different-target";
        var act = () => actor.HandleRegisterAsync(new RegisterServiceRunRequested { Record = foreign });

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*target-run-1*cannot re-register against target 'different-target'*");
    }

    [Fact]
    public async Task HandleRegisterAsync_ShouldRejectCommandIdentityMismatchOnReRegister()
    {
        var actor = GAgentServiceTestKit.CreateStatefulAgent<ServiceRunGAgent, ServiceRunState>(
            new InMemoryEventStore(),
            "service-run:run-1",
            static () => new ServiceRunGAgent());
        await actor.HandleRegisterAsync(new RegisterServiceRunRequested { Record = BuildRecord("run-1") });
        var conflicting = BuildRecord("run-1");
        conflicting.CommandId = "different-command";

        var act = () => actor.HandleRegisterAsync(new RegisterServiceRunRequested { Record = conflicting });

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*command*cannot re-register*");
    }

    [Fact]
    public async Task HandleRegisterAsync_ShouldRejectMissingRequiredFields()
    {
        var actor = GAgentServiceTestKit.CreateStatefulAgent<ServiceRunGAgent, ServiceRunState>(
            new InMemoryEventStore(),
            "service-run:bad",
            static () => new ServiceRunGAgent());

        var noRunId = () => actor.HandleRegisterAsync(new RegisterServiceRunRequested
        {
            Record = new ServiceRunRecord { ScopeId = "t", ServiceId = "s", CommandId = "c" },
        });
        await noRunId.Should().ThrowAsync<InvalidOperationException>().WithMessage("run_id*");
    }

    [Fact]
    public async Task HandleUpdateStatusAsync_ShouldAdvanceStatusAndStamp()
    {
        var actor = GAgentServiceTestKit.CreateStatefulAgent<ServiceRunGAgent, ServiceRunState>(
            new InMemoryEventStore(),
            "service-run:run-1",
            static () => new ServiceRunGAgent());
        await actor.HandleRegisterAsync(new RegisterServiceRunRequested
        {
            Record = BuildRecord("run-1"),
        });

        await actor.HandleUpdateStatusAsync(new UpdateServiceRunStatusRequested
        {
            RunId = "run-1",
            Status = ServiceRunStatus.Completed,
            LastOutput = "done",
        });

        actor.State.Record!.Status.Should().Be(ServiceRunStatus.Completed);
        actor.State.Record.LastOutput.Should().Be("done");
        actor.State.LastAppliedEventVersion.Should().Be(2);
    }

    [Fact]
    public async Task HandleUpdateStatusAsync_ShouldPersistTerminalError()
    {
        var actor = GAgentServiceTestKit.CreateStatefulAgent<ServiceRunGAgent, ServiceRunState>(
            new InMemoryEventStore(),
            "service-run:run-1",
            static () => new ServiceRunGAgent());
        await actor.HandleRegisterAsync(new RegisterServiceRunRequested
        {
            Record = BuildRecord("run-1"),
        });

        await actor.HandleUpdateStatusAsync(new UpdateServiceRunStatusRequested
        {
            RunId = "run-1",
            Status = ServiceRunStatus.Failed,
            LastError = "failed",
        });

        actor.State.Record!.Status.Should().Be(ServiceRunStatus.Failed);
        actor.State.Record.LastError.Should().Be("failed");
        actor.State.LastAppliedEventVersion.Should().Be(2);
    }

    [Fact]
    public async Task HandleUpdateStatusAsync_ShouldNoOp_WhenStatusUnchanged()
    {
        var actor = GAgentServiceTestKit.CreateStatefulAgent<ServiceRunGAgent, ServiceRunState>(
            new InMemoryEventStore(),
            "service-run:run-1",
            static () => new ServiceRunGAgent());
        await actor.HandleRegisterAsync(new RegisterServiceRunRequested
        {
            Record = BuildRecord("run-1"),
        });

        await actor.HandleUpdateStatusAsync(new UpdateServiceRunStatusRequested
        {
            RunId = "run-1",
            Status = ServiceRunStatus.Accepted,
        });

        actor.State.LastAppliedEventVersion.Should().Be(1);
    }

    [Fact]
    public async Task HandleUpdateStatusAsync_ShouldRejectWhenNotRegistered()
    {
        var actor = GAgentServiceTestKit.CreateStatefulAgent<ServiceRunGAgent, ServiceRunState>(
            new InMemoryEventStore(),
            "service-run:run-1",
            static () => new ServiceRunGAgent());

        var act = () => actor.HandleUpdateStatusAsync(new UpdateServiceRunStatusRequested
        {
            RunId = "run-1",
            Status = ServiceRunStatus.Completed,
        });
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*has no registered run*");
    }

    [Fact]
    public async Task HandleUpdateStatusAsync_ShouldNotDispatchWorkOrderNotificationWithoutImplementationTerminalFact()
    {
        var publisher = new RecordingEventPublisher();
        var actor = GAgentServiceTestKit.CreateStatefulAgent<ServiceRunGAgent, ServiceRunState>(
            new InMemoryEventStore(),
            "service-run:run-1",
            static () => new ServiceRunGAgent());
        actor.EventPublisher = publisher;
        var record = BuildRecord("run-1");
        record.CompletionNotificationTarget = new ServiceRunCompletionNotificationTarget
        {
            ActorId = "work-order:scope-1:wo-1",
            DeliveryId = "delivery-1",
            ExpiresAtUnixMs = long.MaxValue,
        };
        await actor.HandleRegisterAsync(new RegisterServiceRunRequested { Record = record });

        await actor.HandleUpdateStatusAsync(new UpdateServiceRunStatusRequested
        {
            RunId = "run-1",
            Status = ServiceRunStatus.Completed,
            LastOutput = "done",
        });

        publisher.Sends.Should().BeEmpty();
        actor.State.PendingTerminalNotification.Should().BeNull();
        actor.State.TerminalNotificationDeliveryStatus.Should()
            .Be(ServiceRunTerminalNotificationDeliveryStatus.Unspecified);
    }

    [Fact]
    public async Task ImplementationTerminalFact_ShouldPrepareNotificationAfterMatchingGenericTerminalStatus()
    {
        var publisher = new RecordingEventPublisher();
        var actor = GAgentServiceTestKit.CreateStatefulAgent<ServiceRunGAgent, ServiceRunState>(
            new InMemoryEventStore(),
            "service-run:run-1",
            static () => new ServiceRunGAgent());
        actor.EventPublisher = publisher;
        var record = BuildRecord("run-1");
        record.TargetActorId = "role-actor-1";
        record.CompletionNotificationTarget = new ServiceRunCompletionNotificationTarget
        {
            ActorId = "work-order:scope-1:wo-1",
            DeliveryId = "delivery-1",
            ExpiresAtUnixMs = long.MaxValue,
        };
        await actor.HandleRegisterAsync(new RegisterServiceRunRequested { Record = record });
        await actor.HandleUpdateStatusAsync(new UpdateServiceRunStatusRequested
        {
            RunId = "run-1",
            Status = ServiceRunStatus.Completed,
            LastOutput = "done",
        });

        await actor.HandleRoleChatCompletedAsync(new RoleChatSessionCompletedEvent
        {
            ActorId = "role-actor-1",
            RunContext = new RoleChatRunContext
            {
                RunId = "run-1",
                CommandId = "cmd-run-1",
                CorrelationId = "corr-run-1",
                CompletionNotificationActorId = actor.Id,
            },
            Outcome = RoleChatSessionOutcome.Completed,
            Content = "done",
            TerminalTime = Timestamp.FromDateTimeOffset(
                DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_000_000)),
        });

        publisher.Sends.Should().ContainSingle()
            .Which.Event.Should().BeOfType<ServiceRunTerminalNotification>()
            .Which.TerminalAt.Should().Be(Timestamp.FromDateTimeOffset(
                DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_000_000)));
        actor.State.TerminalNotificationDeliveryStatus.Should()
            .Be(ServiceRunTerminalNotificationDeliveryStatus.Dispatched);
    }

    [Fact]
    public async Task TerminalSendFailure_ShouldScheduleDurableRetryWithoutDeactivation()
    {
        var scheduler = new RecordingRuntimeCallbackScheduler();
        var publisher = new RecordingEventPublisher
        {
            SendException = new InvalidOperationException("simulated terminal notification failure"),
        };
        var actor = GAgentServiceTestKit.CreateStatefulAgent<ServiceRunGAgent, ServiceRunState>(
            new InMemoryEventStore(),
            "service-run:run-1",
            static () => new ServiceRunGAgent(),
            services => services.AddSingleton<IActorRuntimeCallbackScheduler>(scheduler));
        actor.EventPublisher = publisher;
        await RegisterNotificationRunAsync(actor, DateTimeOffset.UtcNow.AddMinutes(1));

        await actor.HandleRoleChatCompletedAsync(BuildTerminalEvent(actor.Id));

        actor.State.TerminalNotificationDeliveryStatus.Should()
            .Be(ServiceRunTerminalNotificationDeliveryStatus.RetryScheduled);
        actor.State.TerminalNotificationAttempt.Should().Be(1);
        actor.State.PendingTerminalNotification.Should().NotBeNull();
        var callback = scheduler.TimeoutRequests.Should().ContainSingle().Subject;
        callback.DueTime.Should().BeCloseTo(TimeSpan.FromMilliseconds(250), TimeSpan.FromMilliseconds(50));
        callback.CallbackId.Should().Be(actor.State.TerminalNotificationRetryCallbackId);
        var retry = callback.TriggerEnvelope.Payload.Unpack<ServiceRunTerminalNotificationRetryFiredEvent>();
        retry.DeliveryId.Should().Be("delivery-1");
        retry.Attempt.Should().Be(1);
    }

    [Fact]
    public async Task DurableRetrySchedulerFailure_ShouldRemainObservableAndRecoverThroughSelfInbox()
    {
        var scheduler = new RecordingRuntimeCallbackScheduler
        {
            ScheduleException = new InvalidOperationException("simulated durable scheduler failure"),
        };
        var publisher = new RecordingEventPublisher
        {
            SendException = new InvalidOperationException("simulated terminal notification failure"),
        };
        var actor = GAgentServiceTestKit.CreateStatefulAgent<ServiceRunGAgent, ServiceRunState>(
            new InMemoryEventStore(),
            "service-run:run-1",
            static () => new ServiceRunGAgent(),
            services => services.AddSingleton<IActorRuntimeCallbackScheduler>(scheduler));
        actor.EventPublisher = publisher;
        await RegisterNotificationRunAsync(actor, DateTimeOffset.UtcNow.AddMinutes(1));

        var terminal = () => actor.HandleRoleChatCompletedAsync(BuildTerminalEvent(actor.Id));

        await terminal.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("simulated durable scheduler failure");
        actor.State.TerminalNotificationDeliveryStatus.Should()
            .Be(ServiceRunTerminalNotificationDeliveryStatus.Prepared);
        scheduler.TimeoutRequests.Should().BeEmpty();
        var recovery = publisher.Publications.Should()
            .ContainSingle(publication => publication.Audience == TopologyAudience.Self)
            .Subject;
        var retry = recovery.Event.Should()
            .BeOfType<ServiceRunTerminalNotificationRetryFiredEvent>().Subject;
        retry.DeliveryId.Should().Be("delivery-1");
        retry.Attempt.Should().Be(1);
        recovery.Options!.Delivery!.OperationId.Should()
            .Be("service-run-terminal-retry:delivery-1:1");

        var recoveryEnvelope = BuildSelfEnvelope(actor.Id, retry, recovery.Options);

        scheduler.ScheduleException = null;
        await actor.HandleEventAsync(recoveryEnvelope);
        var attemptTwo = scheduler.TimeoutRequests.Should().ContainSingle().Subject;
        attemptTwo.CallbackId.Should().Be("service-run-terminal-retry:delivery-1");
        attemptTwo.TriggerEnvelope.Payload.Unpack<ServiceRunTerminalNotificationRetryFiredEvent>()
            .Attempt.Should().Be(2);
        attemptTwo.TriggerEnvelope.Runtime!.DeliveryIdentity!.OperationId.Should()
            .Be("service-run-terminal-retry:delivery-1:2");

        publisher.SendException = null;
        await actor.HandleEventAsync(attemptTwo.TriggerEnvelope);

        publisher.Sends.Should().ContainSingle();
        actor.State.TerminalNotificationDeliveryStatus.Should()
            .Be(ServiceRunTerminalNotificationDeliveryStatus.Dispatched);
        actor.State.TerminalNotificationAttempt.Should().Be(2);
    }

    [Fact]
    public async Task LaterAttemptSchedulerFailure_ShouldRemainObservableWithoutSelfFallbackOrLoop()
    {
        var scheduler = new RecordingRuntimeCallbackScheduler();
        var publisher = new RecordingEventPublisher
        {
            SendException = new InvalidOperationException("simulated terminal notification failure"),
        };
        var actor = GAgentServiceTestKit.CreateStatefulAgent<ServiceRunGAgent, ServiceRunState>(
            new InMemoryEventStore(),
            "service-run:run-1",
            static () => new ServiceRunGAgent(),
            services => services.AddSingleton<IActorRuntimeCallbackScheduler>(scheduler));
        actor.EventPublisher = publisher;
        await RegisterNotificationRunAsync(actor, DateTimeOffset.UtcNow.AddMinutes(1));
        await actor.HandleRoleChatCompletedAsync(BuildTerminalEvent(actor.Id));
        var attemptOne = scheduler.TimeoutRequests.Should().ContainSingle().Subject.TriggerEnvelope;
        scheduler.ScheduleException = new InvalidOperationException("simulated later scheduler failure");

        var retry = () => actor.HandleEventAsync(attemptOne);

        await retry.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("simulated later scheduler failure");
        publisher.Publications.Should().BeEmpty();
        scheduler.TimeoutRequests.Should().ContainSingle();
        actor.State.TerminalNotificationDeliveryStatus.Should()
            .Be(ServiceRunTerminalNotificationDeliveryStatus.RetryScheduled);
        actor.State.TerminalNotificationAttempt.Should().Be(1);
    }

    [Fact]
    public async Task RetrySchedulerCancellation_ShouldPropagateWithoutSelfFallback()
    {
        var scheduler = new RecordingRuntimeCallbackScheduler
        {
            ScheduleException = new OperationCanceledException("simulated scheduler cancellation"),
        };
        var publisher = new RecordingEventPublisher
        {
            SendException = new InvalidOperationException("simulated terminal notification failure"),
        };
        var actor = GAgentServiceTestKit.CreateStatefulAgent<ServiceRunGAgent, ServiceRunState>(
            new InMemoryEventStore(),
            "service-run:run-1",
            static () => new ServiceRunGAgent(),
            services => services.AddSingleton<IActorRuntimeCallbackScheduler>(scheduler));
        actor.EventPublisher = publisher;
        await RegisterNotificationRunAsync(actor, DateTimeOffset.UtcNow.AddMinutes(1));

        var terminal = () => actor.HandleRoleChatCompletedAsync(BuildTerminalEvent(actor.Id));

        await terminal.Should().ThrowAsync<OperationCanceledException>()
            .WithMessage("simulated scheduler cancellation");
        publisher.Publications.Should().BeEmpty();
        scheduler.TimeoutRequests.Should().BeEmpty();
        actor.State.TerminalNotificationDeliveryStatus.Should()
            .Be(ServiceRunTerminalNotificationDeliveryStatus.Prepared);
    }

    [Fact]
    public async Task DispatchedCommitFailure_ShouldScheduleRetryAndRethrowCommitFailure()
    {
        var eventStore = new FailOnceDispatchedEventStore();
        var scheduler = new RecordingRuntimeCallbackScheduler();
        var publisher = new RecordingEventPublisher();
        var actor = GAgentServiceTestKit.CreateStatefulAgent<ServiceRunGAgent, ServiceRunState>(
            new InMemoryEventStore(),
            "service-run:run-1",
            static () => new ServiceRunGAgent(),
            services => services.AddSingleton<IActorRuntimeCallbackScheduler>(scheduler));
        actor.EventSourcingBehaviorFactory = new DefaultEventSourcingBehaviorFactory<ServiceRunState>(eventStore);
        actor.EventPublisher = publisher;
        await RegisterNotificationRunAsync(actor, DateTimeOffset.UtcNow.AddMinutes(1));

        var terminal = () => actor.HandleRoleChatCompletedAsync(BuildTerminalEvent(actor.Id));

        await terminal.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("simulated dispatched event commit failure");
        publisher.Sends.Should().ContainSingle();
        actor.State.TerminalNotificationDeliveryStatus.Should()
            .Be(ServiceRunTerminalNotificationDeliveryStatus.RetryScheduled);
        actor.State.TerminalNotificationAttempt.Should().Be(1);
        var callback = scheduler.TimeoutRequests.Should().ContainSingle().Subject.TriggerEnvelope;

        await actor.HandleEventAsync(callback);

        publisher.Sends.Should().HaveCount(2);
        publisher.Sends.Select(static send => send.Options!.Delivery!.OperationId)
            .Should().OnlyContain(static operationId => operationId == "service-run-terminal-delivery-1");
        actor.State.TerminalNotificationDeliveryStatus.Should()
            .Be(ServiceRunTerminalNotificationDeliveryStatus.Dispatched);
    }

    [Fact]
    public async Task RetryCallbackEnvelopes_ShouldKeepStableCallbackId_AndUseDistinctDeliveryIdentityPerAttempt()
    {
        var scheduler = new RecordingRuntimeCallbackScheduler();
        var publisher = new RecordingEventPublisher
        {
            SendException = new InvalidOperationException("simulated terminal notification failure"),
        };
        var actor = GAgentServiceTestKit.CreateStatefulAgent<ServiceRunGAgent, ServiceRunState>(
            new InMemoryEventStore(),
            "service-run:run-1",
            static () => new ServiceRunGAgent(),
            services => services.AddSingleton<IActorRuntimeCallbackScheduler>(scheduler));
        actor.EventPublisher = publisher;
        await RegisterNotificationRunAsync(actor, DateTimeOffset.UtcNow.AddMinutes(1));
        await actor.HandleRoleChatCompletedAsync(BuildTerminalEvent(actor.Id));
        var attemptOne = scheduler.TimeoutRequests.Should().ContainSingle().Subject;
        var attemptOneOperationId = attemptOne.TriggerEnvelope.Runtime?.DeliveryIdentity?.OperationId;

        attemptOne.TriggerEnvelope.Payload.Unpack<ServiceRunTerminalNotificationRetryFiredEvent>()
            .Attempt.Should().Be(1);
        attemptOneOperationId.Should().Be("service-run-terminal-retry:delivery-1:1");

        await actor.HandleEventAsync(attemptOne.TriggerEnvelope);

        scheduler.TimeoutRequests.Should().HaveCount(2);
        var attemptTwo = scheduler.TimeoutRequests[1];
        var attemptTwoOperationId = attemptTwo.TriggerEnvelope.Runtime?.DeliveryIdentity?.OperationId;
        attemptTwo.CallbackId.Should().Be(attemptOne.CallbackId);
        attemptTwo.TriggerEnvelope.Payload.Unpack<ServiceRunTerminalNotificationRetryFiredEvent>()
            .Attempt.Should().Be(2);
        attemptTwoOperationId.Should().Be("service-run-terminal-retry:delivery-1:2");
        attemptTwoOperationId.Should().NotBe(attemptOneOperationId);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RetryFired_WhenPending_ShouldDispatchAndCommitDispatched(
        bool retryScheduleWasCommitted)
    {
        var scheduler = new RecordingRuntimeCallbackScheduler();
        var publisher = new RecordingEventPublisher
        {
            SendException = retryScheduleWasCommitted
                ? new InvalidOperationException("simulated terminal notification failure")
                : new OperationCanceledException("simulated schedule-before-commit crash"),
        };
        var actor = GAgentServiceTestKit.CreateStatefulAgent<ServiceRunGAgent, ServiceRunState>(
            new InMemoryEventStore(),
            "service-run:run-1",
            static () => new ServiceRunGAgent(),
            services => services.AddSingleton<IActorRuntimeCallbackScheduler>(scheduler));
        actor.EventPublisher = publisher;
        await RegisterNotificationRunAsync(actor, DateTimeOffset.UtcNow.AddMinutes(1));
        var terminalUpdate = () => actor.HandleRoleChatCompletedAsync(BuildTerminalEvent(actor.Id));
        if (retryScheduleWasCommitted)
            await terminalUpdate();
        else
            await terminalUpdate.Should().ThrowAsync<OperationCanceledException>();
        publisher.SendException = null;

        await actor.HandleTerminalNotificationRetryFiredAsync(new ServiceRunTerminalNotificationRetryFiredEvent
        {
            DeliveryId = "delivery-1",
            Attempt = 1,
        });

        publisher.Sends.Should().ContainSingle();
        actor.State.TerminalNotificationDeliveryStatus.Should()
            .Be(ServiceRunTerminalNotificationDeliveryStatus.Dispatched);
        actor.State.PendingTerminalNotification.Should().BeNull();
        actor.State.TerminalNotificationRetryCallbackId.Should().BeEmpty();
        actor.State.TerminalNotificationRetryAt.Should().BeNull();
    }

    [Fact]
    public async Task RetryCallbackEnvelope_WhenSelf_ShouldDispatchAndCommitDispatched()
    {
        var scheduler = new RecordingRuntimeCallbackScheduler();
        var publisher = new RecordingEventPublisher
        {
            SendException = new InvalidOperationException("simulated terminal notification failure"),
        };
        var actor = GAgentServiceTestKit.CreateStatefulAgent<ServiceRunGAgent, ServiceRunState>(
            new InMemoryEventStore(),
            "service-run:run-1",
            static () => new ServiceRunGAgent(),
            services => services.AddSingleton<IActorRuntimeCallbackScheduler>(scheduler));
        actor.EventPublisher = publisher;
        await RegisterNotificationRunAsync(actor, DateTimeOffset.UtcNow.AddMinutes(1));
        await actor.HandleRoleChatCompletedAsync(BuildTerminalEvent(actor.Id));
        publisher.SendException = null;
        var callbackEnvelope = scheduler.TimeoutRequests.Should().ContainSingle().Subject.TriggerEnvelope;

        await actor.HandleEventAsync(callbackEnvelope);

        publisher.Sends.Should().ContainSingle();
        actor.State.TerminalNotificationDeliveryStatus.Should()
            .Be(ServiceRunTerminalNotificationDeliveryStatus.Dispatched);
        actor.State.PendingTerminalNotification.Should().BeNull();
    }

    [Fact]
    public async Task RetryCallbackEnvelope_WhenNotSelf_ShouldNotDispatch()
    {
        var scheduler = new RecordingRuntimeCallbackScheduler();
        var publisher = new RecordingEventPublisher
        {
            SendException = new InvalidOperationException("simulated terminal notification failure"),
        };
        var actor = GAgentServiceTestKit.CreateStatefulAgent<ServiceRunGAgent, ServiceRunState>(
            new InMemoryEventStore(),
            "service-run:run-1",
            static () => new ServiceRunGAgent(),
            services => services.AddSingleton<IActorRuntimeCallbackScheduler>(scheduler));
        actor.EventPublisher = publisher;
        await RegisterNotificationRunAsync(actor, DateTimeOffset.UtcNow.AddMinutes(1));
        await actor.HandleRoleChatCompletedAsync(BuildTerminalEvent(actor.Id));
        publisher.SendException = null;
        var foreignEnvelope = scheduler.TimeoutRequests.Should().ContainSingle().Subject.TriggerEnvelope.Clone();
        foreignEnvelope.Route = EnvelopeRouteSemantics.CreateDirect("foreign-actor", actor.Id);
        var version = actor.State.LastAppliedEventVersion;

        await actor.HandleEventAsync(foreignEnvelope);

        publisher.Sends.Should().BeEmpty();
        actor.State.LastAppliedEventVersion.Should().Be(version);
        actor.State.TerminalNotificationDeliveryStatus.Should()
            .Be(ServiceRunTerminalNotificationDeliveryStatus.RetryScheduled);
    }

    [Fact]
    public async Task RetryCallbackEnvelope_WhenNextScheduledCommitFails_ShouldRecoverAndAdvanceMonotonically()
    {
        var eventStore = new FailOnceRetryScheduledEventStore(failedAttempt: 2);
        var scheduler = new RecordingRuntimeCallbackScheduler();
        var publisher = new RecordingEventPublisher
        {
            SendException = new InvalidOperationException("simulated terminal notification failure"),
        };
        var actor = GAgentServiceTestKit.CreateStatefulAgent<ServiceRunGAgent, ServiceRunState>(
            new InMemoryEventStore(),
            "service-run:run-1",
            static () => new ServiceRunGAgent(),
            services => services.AddSingleton<IActorRuntimeCallbackScheduler>(scheduler));
        actor.EventSourcingBehaviorFactory = new DefaultEventSourcingBehaviorFactory<ServiceRunState>(eventStore);
        actor.EventPublisher = publisher;
        await RegisterNotificationRunAsync(actor, DateTimeOffset.UtcNow.AddMinutes(1));
        await actor.HandleRoleChatCompletedAsync(BuildTerminalEvent(actor.Id));
        var retryOneEnvelope = scheduler.TimeoutRequests.Should().ContainSingle().Subject.TriggerEnvelope;

        var retryOne = () => actor.HandleEventAsync(retryOneEnvelope);

        await retryOne.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("simulated retry-scheduled commit failure");
        eventStore.FailureObserved.Should().BeTrue();
        actor.State.TerminalNotificationDeliveryStatus.Should()
            .Be(ServiceRunTerminalNotificationDeliveryStatus.RetryScheduled);
        actor.State.TerminalNotificationAttempt.Should().Be(1);
        scheduler.TimeoutRequests.Should().HaveCount(2);
        var retryTwoEnvelope = scheduler.TimeoutRequests[1].TriggerEnvelope;
        retryTwoEnvelope.Payload.Unpack<ServiceRunTerminalNotificationRetryFiredEvent>()
            .Attempt.Should().Be(2);

        await actor.HandleEventAsync(retryTwoEnvelope);

        actor.State.TerminalNotificationDeliveryStatus.Should()
            .Be(ServiceRunTerminalNotificationDeliveryStatus.RetryScheduled);
        actor.State.TerminalNotificationAttempt.Should().Be(3);
        scheduler.TimeoutRequests.Should().HaveCount(3);
        var retryThreeEnvelope = scheduler.TimeoutRequests[2].TriggerEnvelope;
        retryThreeEnvelope.Payload.Unpack<ServiceRunTerminalNotificationRetryFiredEvent>()
            .Attempt.Should().Be(3);
        publisher.SendException = null;

        await actor.HandleEventAsync(retryThreeEnvelope);

        publisher.Sends.Should().ContainSingle();
        actor.State.TerminalNotificationDeliveryStatus.Should()
            .Be(ServiceRunTerminalNotificationDeliveryStatus.Dispatched);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    public async Task RetryFired_WhenAttemptIsStale_ShouldNotSend(int staleAttempt)
    {
        var scheduler = new RecordingRuntimeCallbackScheduler();
        var publisher = new RecordingEventPublisher
        {
            SendException = new InvalidOperationException("simulated terminal notification failure"),
        };
        var actor = GAgentServiceTestKit.CreateStatefulAgent<ServiceRunGAgent, ServiceRunState>(
            new InMemoryEventStore(),
            "service-run:run-1",
            static () => new ServiceRunGAgent(),
            services => services.AddSingleton<IActorRuntimeCallbackScheduler>(scheduler));
        actor.EventPublisher = publisher;
        await RegisterNotificationRunAsync(actor, DateTimeOffset.UtcNow.AddMinutes(1));
        await actor.HandleRoleChatCompletedAsync(BuildTerminalEvent(actor.Id));
        publisher.SendException = null;
        var version = actor.State.LastAppliedEventVersion;

        await actor.HandleTerminalNotificationRetryFiredAsync(new ServiceRunTerminalNotificationRetryFiredEvent
        {
            DeliveryId = "delivery-1",
            Attempt = staleAttempt,
        });

        publisher.Sends.Should().BeEmpty();
        actor.State.LastAppliedEventVersion.Should().Be(version);
        actor.State.TerminalNotificationDeliveryStatus.Should()
            .Be(ServiceRunTerminalNotificationDeliveryStatus.RetryScheduled);
    }

    [Fact]
    public async Task TerminalDelivery_WhenDeadlineElapsed_ShouldCommitExpired()
    {
        var publisher = new RecordingEventPublisher();
        var actor = GAgentServiceTestKit.CreateStatefulAgent<ServiceRunGAgent, ServiceRunState>(
            new InMemoryEventStore(),
            "service-run:run-1",
            static () => new ServiceRunGAgent());
        actor.EventPublisher = publisher;
        await RegisterNotificationRunAsync(actor, DateTimeOffset.UtcNow.AddSeconds(-1));

        await actor.HandleRoleChatCompletedAsync(BuildTerminalEvent(actor.Id));

        publisher.Sends.Should().BeEmpty();
        actor.State.TerminalNotificationDeliveryStatus.Should()
            .Be(ServiceRunTerminalNotificationDeliveryStatus.Expired);
        actor.State.PendingTerminalNotification.Should().BeNull();
        actor.State.TerminalNotificationRetryCallbackId.Should().BeEmpty();
        actor.State.TerminalNotificationRetryAt.Should().BeNull();
    }

    [Fact]
    public async Task ActivateAsync_WhenRetryScheduled_ShouldRecoverPendingNotification()
    {
        var eventStore = new InMemoryEventStore();
        var scheduler = new RecordingRuntimeCallbackScheduler();
        var failingPublisher = new RecordingEventPublisher
        {
            SendException = new InvalidOperationException("simulated terminal notification failure"),
        };
        var first = GAgentServiceTestKit.CreateStatefulAgent<ServiceRunGAgent, ServiceRunState>(
            eventStore,
            "service-run:run-1",
            static () => new ServiceRunGAgent(),
            services => services.AddSingleton<IActorRuntimeCallbackScheduler>(scheduler));
        first.EventPublisher = failingPublisher;
        await RegisterNotificationRunAsync(first, DateTimeOffset.UtcNow.AddMinutes(1));
        await first.HandleRoleChatCompletedAsync(BuildTerminalEvent(first.Id));
        first.State.TerminalNotificationDeliveryStatus.Should()
            .Be(ServiceRunTerminalNotificationDeliveryStatus.RetryScheduled);

        var recoveredPublisher = new RecordingEventPublisher();
        var recovered = GAgentServiceTestKit.CreateStatefulAgent<ServiceRunGAgent, ServiceRunState>(
            eventStore,
            "service-run:run-1",
            static () => new ServiceRunGAgent());
        recovered.EventPublisher = recoveredPublisher;

        await recovered.ActivateAsync();

        recoveredPublisher.Sends.Should().ContainSingle()
            .Which.Event.Should().BeOfType<ServiceRunTerminalNotification>()
            .Which.Error.Should().Be("failed");
        recovered.State.TerminalNotificationDeliveryStatus.Should()
            .Be(ServiceRunTerminalNotificationDeliveryStatus.Dispatched);
        recovered.State.PendingTerminalNotification.Should().BeNull();
    }

    [Fact]
    public async Task TerminalRedelivery_AfterCommitAndReactivation_ShouldNotReapplyStateOrSideEffect()
    {
        const string actorId = "service-run:tenant-1:svc-1:run-1";
        const string operationId = "role-chat-terminal:run-1";
        var eventStore = new InMemoryEventStore();
        var firstPublisher = new RecordingEventPublisher();
        var first = GAgentServiceTestKit.CreateStatefulAgent<ServiceRunGAgent, ServiceRunState>(
            eventStore,
            actorId,
            static () => new ServiceRunGAgent());
        first.EventPublisher = firstPublisher;
        await first.ActivateAsync();
        await RegisterNotificationRunAsync(first, DateTimeOffset.UtcNow.AddMinutes(1));
        var terminalEnvelope = BuildInboundEnvelope(BuildTerminalEvent(actorId), "role-actor-1");
        terminalEnvelope.Id = "role-chat-terminal-envelope-1";
        terminalEnvelope.EnsureRuntime().EnsureDeliveryIdentity().OperationId = operationId;

        await first.HandleEventAsync(terminalEnvelope);

        var committedVersion = await eventStore.GetVersionAsync(actorId);
        var authoritativeVersion = first.State.LastAppliedEventVersion;
        first.State.Record!.Status.Should().Be(ServiceRunStatus.Failed);
        first.State.Record.LastError.Should().Be("failed");
        first.State.TerminalNotificationDeliveryStatus.Should()
            .Be(ServiceRunTerminalNotificationDeliveryStatus.Dispatched);
        authoritativeVersion.Should().Be(committedVersion);
        firstPublisher.Sends.Should().ContainSingle()
            .Which.Options!.Delivery!.OperationId.Should()
            .Be("service-run-terminal-delivery-1");

        // The handler committed successfully, but the transport ACK is assumed lost before process exit.
        var recoveredPublisher = new RecordingEventPublisher();
        var recovered = GAgentServiceTestKit.CreateStatefulAgent<ServiceRunGAgent, ServiceRunState>(
            eventStore,
            actorId,
            static () => new ServiceRunGAgent());
        recovered.EventPublisher = recoveredPublisher;
        await recovered.ActivateAsync();

        await recovered.HandleEventAsync(terminalEnvelope.Clone());

        recovered.State.Record!.Status.Should().Be(ServiceRunStatus.Failed);
        recovered.State.Record.LastError.Should().Be("failed");
        recovered.State.LastAppliedEventVersion.Should().Be(authoritativeVersion);
        (await eventStore.GetVersionAsync(actorId)).Should().Be(committedVersion);
        firstPublisher.Sends.Count.Should().Be(1);
        recoveredPublisher.Sends.Should().BeEmpty();
        var committedEvents = await eventStore.GetEventsAsync(actorId);
        committedEvents.Count(stateEvent => stateEvent.EventData.Is(ServiceRunStatusUpdatedEvent.Descriptor))
            .Should().Be(1);
        committedEvents.Count(stateEvent =>
                stateEvent.EventData.Is(ServiceRunTerminalNotificationPreparedEvent.Descriptor))
            .Should().Be(1);
        committedEvents.Count(stateEvent =>
                stateEvent.EventData.Is(ServiceRunTerminalNotificationDispatchedEvent.Descriptor))
            .Should().Be(1);
    }

    [Fact]
    public async Task TerminalSendCancellation_ShouldPropagateWithoutSchedulingRetry()
    {
        var scheduler = new RecordingRuntimeCallbackScheduler();
        var publisher = new RecordingEventPublisher
        {
            SendException = new OperationCanceledException("simulated cancellation"),
        };
        var actor = GAgentServiceTestKit.CreateStatefulAgent<ServiceRunGAgent, ServiceRunState>(
            new InMemoryEventStore(),
            "service-run:run-1",
            static () => new ServiceRunGAgent(),
            services => services.AddSingleton<IActorRuntimeCallbackScheduler>(scheduler));
        actor.EventPublisher = publisher;
        await RegisterNotificationRunAsync(actor, DateTimeOffset.UtcNow.AddMinutes(1));

        var act = () => actor.HandleRoleChatCompletedAsync(BuildTerminalEvent(actor.Id));

        await act.Should().ThrowAsync<OperationCanceledException>();
        scheduler.TimeoutRequests.Should().BeEmpty();
        actor.State.TerminalNotificationDeliveryStatus.Should()
            .Be(ServiceRunTerminalNotificationDeliveryStatus.Prepared);
    }

    [Fact]
    public void TerminalDeliveryEventAttempts_ShouldUseAdditiveOptionalFieldThree()
    {
        ServiceRunTerminalNotificationDispatchedEvent.Descriptor.FindFieldByNumber(3)!.Name
            .Should().Be("attempt");
        ServiceRunTerminalNotificationExpiredEvent.Descriptor.FindFieldByNumber(3)!.Name
            .Should().Be("attempt");

        var currentDispatched = new ServiceRunTerminalNotificationDispatchedEvent { Attempt = 0 };
        var currentExpired = new ServiceRunTerminalNotificationExpiredEvent { Attempt = 0 };
        currentDispatched.HasAttempt.Should().BeTrue();
        currentExpired.HasAttempt.Should().BeTrue();

        var legacyDispatched = ServiceRunTerminalNotificationDispatchedEvent.Parser.ParseFrom(
            new ServiceRunTerminalNotificationDispatchedEvent
            {
                DeliveryId = "delivery-1",
                DispatchedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            }.ToByteArray());
        var legacyExpired = ServiceRunTerminalNotificationExpiredEvent.Parser.ParseFrom(
            new ServiceRunTerminalNotificationExpiredEvent
            {
                DeliveryId = "delivery-1",
                ExpiredAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            }.ToByteArray());
        legacyDispatched.HasAttempt.Should().BeFalse();
        legacyExpired.HasAttempt.Should().BeFalse();
    }

    [Fact]
    public void TerminalDeliveryReducers_ShouldRejectInvalidEventsWithoutMutatingState()
    {
        var actor = GAgentServiceTestKit.CreateStatefulAgent<ServiceRunGAgent, ServiceRunState>(
            new InMemoryEventStore(),
            "service-run:run-1",
            static () => new ServiceRunGAgent());
        var state = BuildPendingDeliveryState();

        AssertRejected(actor, state, RetryScheduled("delivery-other", 1, "wrong-delivery"));
        AssertRejected(actor, state, RetryScheduled("delivery-1", 2, "skipped-prepared"));
        state = Reduce(actor, state, RetryScheduled("delivery-1", 1, "callback-1"));
        AssertRejected(actor, state, RetryScheduled("delivery-1", 1, "stale"));
        AssertRejected(actor, state, RetryScheduled("delivery-1", 3, "skipped"));
        AssertRejected(actor, state, new ServiceRunTerminalNotificationDispatchedEvent
        {
            DeliveryId = "delivery-other",
            Attempt = 1,
        });
        AssertRejected(actor, state, new ServiceRunTerminalNotificationDispatchedEvent
        {
            DeliveryId = "delivery-1",
            Attempt = 0,
        });
        AssertRejected(actor, state, new ServiceRunTerminalNotificationExpiredEvent
        {
            DeliveryId = "delivery-1",
            Attempt = 3,
        });

        state = Reduce(actor, state, new ServiceRunTerminalNotificationExpiredEvent
        {
            DeliveryId = "delivery-1",
            Attempt = 2,
        });
        state.TerminalNotificationDeliveryStatus.Should()
            .Be(ServiceRunTerminalNotificationDeliveryStatus.Expired);
        state.TerminalNotificationAttempt.Should().Be(2);
        AssertRejected(actor, state, new ServiceRunTerminalNotificationPreparedEvent
        {
            Notification = new ServiceRunTerminalNotification
            {
                DeliveryId = "delivery-2",
                RunId = "run-1",
            },
        });
        AssertRejected(actor, state, RetryScheduled("delivery-1", 3, "terminal-reopen"));
        AssertRejected(actor, state, new ServiceRunTerminalNotificationDispatchedEvent
        {
            DeliveryId = "delivery-1",
            Attempt = 2,
        });
        AssertRejected(actor, state, new ServiceRunTerminalNotificationExpiredEvent
        {
            DeliveryId = "delivery-1",
            Attempt = 2,
        });
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void LegacyTerminalDeliveryEventWithoutAttempt_ShouldPreservePendingReplay(bool dispatched)
    {
        var actor = GAgentServiceTestKit.CreateStatefulAgent<ServiceRunGAgent, ServiceRunState>(
            new InMemoryEventStore(),
            "service-run:run-1",
            static () => new ServiceRunGAgent());
        var state = BuildPendingDeliveryState();
        state.TerminalNotificationDeliveryStatus =
            ServiceRunTerminalNotificationDeliveryStatus.RetryScheduled;
        state.TerminalNotificationAttempt = 3;
        IMessage legacy = dispatched
            ? ServiceRunTerminalNotificationDispatchedEvent.Parser.ParseFrom(
                new ServiceRunTerminalNotificationDispatchedEvent
                {
                    DeliveryId = "delivery-1",
                }.ToByteArray())
            : ServiceRunTerminalNotificationExpiredEvent.Parser.ParseFrom(
                new ServiceRunTerminalNotificationExpiredEvent
                {
                    DeliveryId = "delivery-1",
                }.ToByteArray());

        var reduced = Reduce(actor, state, legacy);

        reduced.Should().NotBeSameAs(state);
        reduced.TerminalNotificationDeliveryStatus.Should().Be(dispatched
            ? ServiceRunTerminalNotificationDeliveryStatus.Dispatched
            : ServiceRunTerminalNotificationDeliveryStatus.Expired);
        reduced.TerminalNotificationAttempt.Should().Be(3);
        reduced.LastAppliedEventVersion.Should().Be(state.LastAppliedEventVersion + 1);
    }

    [Fact]
    public async Task HandleRoleChatCompletedAsync_ShouldMapMatchingCommittedTerminalFact()
    {
        var publisher = new RecordingEventPublisher();
        var actor = GAgentServiceTestKit.CreateStatefulAgent<ServiceRunGAgent, ServiceRunState>(
            new InMemoryEventStore(),
            "service-run:tenant-1:svc-1:run-1",
            static () => new ServiceRunGAgent());
        actor.EventPublisher = publisher;
        var record = BuildRecord("run-1");
        record.TargetActorId = "role-actor-1";
        record.CompletionNotificationTarget = new ServiceRunCompletionNotificationTarget
        {
            ActorId = "work-order:tenant-1:wo-1",
            DeliveryId = "delivery-1",
            ExpiresAtUnixMs = long.MaxValue,
        };
        await actor.HandleRegisterAsync(new RegisterServiceRunRequested { Record = record });

        await actor.HandleRoleChatCompletedAsync(new RoleChatSessionCompletedEvent
        {
            ActorId = "role-actor-1",
            SessionId = "session-1",
            RunContext = new RoleChatRunContext
            {
                RunId = "run-1",
                CommandId = "cmd-run-1",
                CorrelationId = "corr-run-1",
                CompletionNotificationActorId = actor.Id,
            },
            Outcome = RoleChatSessionOutcome.Completed,
            Content = "role output",
            TerminalTime = Timestamp.FromDateTimeOffset(
                DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_000_000)),
        });

        actor.State.Record!.Status.Should().Be(ServiceRunStatus.Completed);
        actor.State.Record.LastOutput.Should().Be("role output");
        publisher.Sends.Should().ContainSingle()
            .Which.Event.Should().BeOfType<ServiceRunTerminalNotification>()
            .Which.TerminalAt.Should().Be(Timestamp.FromDateTimeOffset(
                DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_000_000)));
    }

    [Fact]
    public async Task HandleRoleChatCompletedAsync_ShouldRetainOutcomeUncertain()
    {
        var publisher = new RecordingEventPublisher();
        var actor = GAgentServiceTestKit.CreateStatefulAgent<ServiceRunGAgent, ServiceRunState>(
            new InMemoryEventStore(),
            "service-run:tenant-1:svc-1:run-1",
            static () => new ServiceRunGAgent());
        actor.EventPublisher = publisher;
        var record = BuildRecord("run-1");
        record.TargetActorId = "role-actor-1";
        record.CompletionNotificationTarget = new ServiceRunCompletionNotificationTarget
        {
            ActorId = "work-order:tenant-1:wo-1",
            DeliveryId = "delivery-1",
            ExpiresAtUnixMs = long.MaxValue,
        };
        await actor.HandleRegisterAsync(new RegisterServiceRunRequested { Record = record });
        var terminal = BuildTerminalEvent(actor.Id);
        terminal.Outcome = RoleChatSessionOutcome.OutcomeUncertain;
        terminal.FailureCode = "SESSION_OUTCOME_UNCERTAIN";
        terminal.SafeMessage = "The chat outcome could not be confirmed.";

        await actor.HandleRoleChatCompletedAsync(terminal);

        actor.State.Record!.Status.Should().Be(ServiceRunStatus.OutcomeUncertain);
        actor.State.Record.LastError.Should().Be("The chat outcome could not be confirmed.");
        actor.State.PendingTerminalNotification.Should().BeNull();
        actor.State.TerminalNotificationDeliveryStatus.Should()
            .Be(ServiceRunTerminalNotificationDeliveryStatus.Unspecified);
        publisher.Sends.Should().BeEmpty();
    }

    [Theory]
    [InlineData(RoleChatSessionOutcome.Completed, ServiceRunStatus.Completed)]
    [InlineData(RoleChatSessionOutcome.Failed, ServiceRunStatus.Failed)]
    public async Task HandleRoleChatCompletedAsync_ShouldNotifyOnceAfterOutcomeUncertainIsReconciled(
        RoleChatSessionOutcome reconciledOutcome,
        ServiceRunStatus reconciledStatus)
    {
        var publisher = new RecordingEventPublisher();
        var actor = GAgentServiceTestKit.CreateStatefulAgent<ServiceRunGAgent, ServiceRunState>(
            new InMemoryEventStore(),
            "service-run:tenant-1:svc-1:run-1",
            static () => new ServiceRunGAgent());
        actor.EventPublisher = publisher;
        await RegisterNotificationRunAsync(actor, DateTimeOffset.MaxValue);
        var uncertain = BuildTerminalEvent(actor.Id);
        uncertain.Outcome = RoleChatSessionOutcome.OutcomeUncertain;
        uncertain.FailureCode = "SESSION_OUTCOME_UNCERTAIN";
        uncertain.SafeMessage = "The chat outcome could not be confirmed.";

        await actor.HandleRoleChatCompletedAsync(uncertain);

        actor.State.PendingTerminalNotification.Should().BeNull();
        actor.State.TerminalNotificationDeliveryStatus.Should()
            .Be(ServiceRunTerminalNotificationDeliveryStatus.Unspecified);
        publisher.Sends.Should().BeEmpty();

        var reconciled = uncertain.Clone();
        reconciled.Outcome = reconciledOutcome;
        reconciled.Content = reconciledOutcome == RoleChatSessionOutcome.Completed
            ? "confirmed output"
            : string.Empty;
        reconciled.FailureCode = reconciledOutcome == RoleChatSessionOutcome.Failed
            ? "CONFIRMED_FAILURE"
            : string.Empty;
        reconciled.SafeMessage = reconciledOutcome == RoleChatSessionOutcome.Failed
            ? "confirmed failure"
            : string.Empty;

        await actor.HandleRoleChatCompletedAsync(reconciled);
        await actor.HandleRoleChatCompletedAsync(reconciled.Clone());

        actor.State.Record!.Status.Should().Be(reconciledStatus);
        actor.State.TerminalNotificationDeliveryStatus.Should()
            .Be(ServiceRunTerminalNotificationDeliveryStatus.Dispatched);
        actor.State.PendingTerminalNotification.Should().BeNull();
        publisher.Sends.Should().ContainSingle();
    }

    [Theory]
    [InlineData(ServiceRunStatus.Completed)]
    [InlineData(ServiceRunStatus.Failed)]
    public async Task HandleUpdateStatusAsync_ShouldReconcileOutcomeUncertain(
        ServiceRunStatus reconciledStatus)
    {
        var actor = GAgentServiceTestKit.CreateStatefulAgent<ServiceRunGAgent, ServiceRunState>(
            new InMemoryEventStore(),
            "service-run:run-uncertain-reconcile",
            static () => new ServiceRunGAgent());
        await actor.HandleRegisterAsync(new RegisterServiceRunRequested
        {
            Record = BuildRecord("run-uncertain-reconcile"),
        });
        await actor.HandleUpdateStatusAsync(new UpdateServiceRunStatusRequested
        {
            RunId = "run-uncertain-reconcile",
            Status = ServiceRunStatus.OutcomeUncertain,
            LastError = "outcome uncertain",
        });

        await actor.HandleUpdateStatusAsync(new UpdateServiceRunStatusRequested
        {
            RunId = "run-uncertain-reconcile",
            Status = reconciledStatus,
            LastOutput = reconciledStatus == ServiceRunStatus.Completed ? "confirmed output" : null,
            LastError = reconciledStatus == ServiceRunStatus.Failed ? "confirmed failure" : null,
        });

        actor.State.Record!.Status.Should().Be(reconciledStatus);
    }

    [Theory]
    [InlineData(ServiceRunStatus.Completed, ServiceRunStatus.Failed)]
    [InlineData(ServiceRunStatus.Failed, ServiceRunStatus.Completed)]
    [InlineData(ServiceRunStatus.Completed, ServiceRunStatus.OutcomeUncertain)]
    public async Task HandleUpdateStatusAsync_ShouldRejectConflictingAbsorbingTerminalTransition(
        ServiceRunStatus currentStatus,
        ServiceRunStatus conflictingStatus)
    {
        var actor = GAgentServiceTestKit.CreateStatefulAgent<ServiceRunGAgent, ServiceRunState>(
            new InMemoryEventStore(),
            "service-run:run-terminal-conflict",
            static () => new ServiceRunGAgent());
        await actor.HandleRegisterAsync(new RegisterServiceRunRequested
        {
            Record = BuildRecord("run-terminal-conflict"),
        });
        await actor.HandleUpdateStatusAsync(new UpdateServiceRunStatusRequested
        {
            RunId = "run-terminal-conflict",
            Status = currentStatus,
        });

        var act = () => actor.HandleUpdateStatusAsync(new UpdateServiceRunStatusRequested
        {
            RunId = "run-terminal-conflict",
            Status = conflictingStatus,
        });

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*already terminal*cannot adopt*");
        actor.State.Record!.Status.Should().Be(currentStatus);
    }

    [Fact]
    public async Task HandleRoleChatCompletedAsync_ShouldRejectDifferentEnvelopePublisher()
    {
        var actor = GAgentServiceTestKit.CreateStatefulAgent<ServiceRunGAgent, ServiceRunState>(
            new InMemoryEventStore(),
            "service-run:tenant-1:svc-1:run-1",
            static () => new ServiceRunGAgent());
        var record = BuildRecord("run-1");
        record.TargetActorId = "role-actor-1";
        await actor.HandleRegisterAsync(new RegisterServiceRunRequested { Record = record });
        var terminal = new RoleChatSessionCompletedEvent
        {
            ActorId = "role-actor-1",
            RunContext = new RoleChatRunContext
            {
                RunId = "run-1",
                CommandId = "cmd-run-1",
                CorrelationId = "corr-run-1",
                CompletionNotificationActorId = actor.Id,
            },
            Outcome = RoleChatSessionOutcome.Completed,
            TerminalTime = Timestamp.FromDateTimeOffset(
                DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_000_000)),
        };

        var act = () => actor.HandleEventAsync(BuildInboundEnvelope(terminal, "forged-role-actor"));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*publisher*does not match*");
        actor.State.Record!.Status.Should().Be(ServiceRunStatus.Accepted);
    }

    [Fact]
    public async Task HandleScriptRunOutcomeAsync_ShouldMapMatchingCommittedFailureFact()
    {
        var actor = GAgentServiceTestKit.CreateStatefulAgent<ServiceRunGAgent, ServiceRunState>(
            new InMemoryEventStore(),
            "service-run:tenant-1:svc-1:run-1",
            static () => new ServiceRunGAgent());
        var record = BuildRecord("run-1");
        record.ImplementationKind = ServiceImplementationKind.Scripting;
        record.TargetActorId = "script-actor-1";
        await actor.HandleRegisterAsync(new RegisterServiceRunRequested { Record = record });

        await actor.HandleScriptRunOutcomeAsync(new ScriptRunOutcomeRecordedEvent
        {
            ScriptRunId = "run-1",
            ActorId = "script-actor-1",
            CommandId = "cmd-run-1",
            CorrelationId = "corr-run-1",
            CompletionNotificationActorId = actor.Id,
            Status = ScriptRunOutcomeStatus.Failed,
            Error = "script failed",
            StateVersion = 2,
            OccurredAtUnixTimeMs = 1_700_000_000_000,
        });

        actor.State.Record!.Status.Should().Be(ServiceRunStatus.Failed);
        actor.State.Record.LastError.Should().Be("script failed");
    }

    [Theory]
    [InlineData("other-run", "role-actor-1", "cmd-run-1", "corr-run-1")]
    [InlineData("run-1", "other-role", "cmd-run-1", "corr-run-1")]
    [InlineData("run-1", "role-actor-1", "other-command", "corr-run-1")]
    [InlineData("run-1", "role-actor-1", "cmd-run-1", "other-correlation")]
    public async Task HandleRoleChatCompletedAsync_ShouldRejectMismatchedExecutionIdentity(
        string runId,
        string actorId,
        string commandId,
        string correlationId)
    {
        var actor = GAgentServiceTestKit.CreateStatefulAgent<ServiceRunGAgent, ServiceRunState>(
            new InMemoryEventStore(),
            "service-run:tenant-1:svc-1:run-1",
            static () => new ServiceRunGAgent());
        var record = BuildRecord("run-1");
        record.TargetActorId = "role-actor-1";
        await actor.HandleRegisterAsync(new RegisterServiceRunRequested { Record = record });

        var act = () => actor.HandleRoleChatCompletedAsync(new RoleChatSessionCompletedEvent
        {
            ActorId = actorId,
            RunContext = new RoleChatRunContext
            {
                RunId = runId,
                CommandId = commandId,
                CorrelationId = correlationId,
                CompletionNotificationActorId = actor.Id,
            },
            Outcome = RoleChatSessionOutcome.Completed,
            TerminalTime = Timestamp.FromDateTimeOffset(
                DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_000_000)),
        });

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*does not match registered service Run identity*");
        actor.State.Record!.Status.Should().Be(ServiceRunStatus.Accepted);
    }

    private static void AssertRejected(
        ServiceRunGAgent actor,
        ServiceRunState state,
        IMessage evt)
    {
        var before = state.ToByteArray();

        var reduced = Reduce(actor, state, evt);

        reduced.Should().BeSameAs(state);
        reduced.ToByteArray().Should().Equal(before);
        reduced.LastAppliedEventVersion.Should().Be(state.LastAppliedEventVersion);
        reduced.LastEventId.Should().Be(state.LastEventId);
    }

    private static ServiceRunState Reduce(
        ServiceRunGAgent actor,
        ServiceRunState state,
        IMessage evt)
    {
        var transition = typeof(ServiceRunGAgent).GetMethod(
            "TransitionState",
            BindingFlags.Instance | BindingFlags.NonPublic);
        transition.Should().NotBeNull();
        return (ServiceRunState)transition!.Invoke(actor, [state, evt])!;
    }

    private static ServiceRunState BuildPendingDeliveryState() =>
        new()
        {
            Record = BuildRecord("run-1"),
            PendingTerminalNotification = new ServiceRunTerminalNotification
            {
                DeliveryId = "delivery-1",
                RunId = "run-1",
            },
            TerminalNotificationDeliveryStatus =
                ServiceRunTerminalNotificationDeliveryStatus.Prepared,
            LastAppliedEventVersion = 41,
            LastEventId = "before-rejected-delivery-event",
        };

    private static ServiceRunTerminalNotificationRetryScheduledEvent RetryScheduled(
        string deliveryId,
        int attempt,
        string callbackId) =>
        new()
        {
            DeliveryId = deliveryId,
            Attempt = attempt,
            CallbackId = callbackId,
            RetryAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow.AddSeconds(1)),
        };

    private static ServiceRunRecord BuildRecord(string runId) =>
        new()
        {
            ScopeId = "tenant-1",
            ServiceId = "svc-1",
            ServiceKey = "tenant-1:svc-1",
            RunId = runId,
            CommandId = $"cmd-{runId}",
            CorrelationId = $"corr-{runId}",
            EndpointId = "run",
            ImplementationKind = ServiceImplementationKind.Static,
            TargetActorId = $"target-{runId}",
            RevisionId = "r1",
            DeploymentId = "dep-1",
            Status = ServiceRunStatus.Unspecified,
            CreatedAt = Timestamp.FromDateTime(DateTime.UtcNow),
        };

    private static async Task RegisterNotificationRunAsync(
        ServiceRunGAgent actor,
        DateTimeOffset expiresAt)
    {
        var record = BuildRecord("run-1");
        record.TargetActorId = "role-actor-1";
        record.CompletionNotificationTarget = new ServiceRunCompletionNotificationTarget
        {
            ActorId = "work-order:scope-1:wo-1",
            DeliveryId = "delivery-1",
            ExpiresAtUnixMs = expiresAt.ToUnixTimeMilliseconds(),
        };
        await actor.HandleRegisterAsync(new RegisterServiceRunRequested { Record = record });
    }

    private static RoleChatSessionCompletedEvent BuildTerminalEvent(string completionNotificationActorId) =>
        new()
        {
            ActorId = "role-actor-1",
            RunContext = new RoleChatRunContext
            {
                RunId = "run-1",
                CommandId = "cmd-run-1",
                CorrelationId = "corr-run-1",
                CompletionNotificationActorId = completionNotificationActorId,
            },
            Outcome = RoleChatSessionOutcome.Failed,
            FailureCode = "ROLE_EXECUTION_FAILED",
            SafeMessage = "failed",
            TerminalTime = Timestamp.FromDateTimeOffset(
                DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_000_000)),
        };

    private static EventEnvelope BuildInboundEnvelope(IMessage payload, string publisherActorId) =>
        new()
        {
            Id = $"test-{Guid.NewGuid():N}",
            Payload = Any.Pack(payload),
            Timestamp = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            Route = EnvelopeRouteSemantics.CreateDirect(
                publisherActorId,
                "service-run:tenant-1:svc-1:run-1"),
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

        public List<(string TargetActorId, IMessage Event, EventEnvelopePublishOptions? Options)> Sends { get; } = [];

        public List<(IMessage Event, TopologyAudience Audience, EventEnvelopePublishOptions? Options)> Publications { get; } = [];

        public Task PublishAsync<TEvent>(
            TEvent evt,
            TopologyAudience audience = TopologyAudience.Children,
            CancellationToken ct = default,
            EventEnvelope? sourceEnvelope = null,
            EventEnvelopePublishOptions? options = null)
            where TEvent : IMessage
        {
            ct.ThrowIfCancellationRequested();
            Publications.Add((evt, audience, options));
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
            if (SendException != null)
                throw SendException;

            Sends.Add((targetActorId, evt, options));
            return Task.CompletedTask;
        }
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

    private sealed class FailOnceRetryScheduledEventStore(int failedAttempt) : IEventStore
    {
        private readonly InMemoryEventStore _inner = new();

        public bool FailureObserved { get; private set; }

        public Task<EventStoreCommitResult> AppendAsync(
            string agentId,
            IEnumerable<StateEvent> events,
            long expectedVersion,
            CancellationToken ct = default)
        {
            var buffered = events.ToArray();
            if (!FailureObserved && buffered.Any(IsFailedRetryScheduledEvent))
            {
                FailureObserved = true;
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

        private bool IsFailedRetryScheduledEvent(StateEvent stateEvent) =>
            stateEvent.EventData.Is(ServiceRunTerminalNotificationRetryScheduledEvent.Descriptor) &&
            stateEvent.EventData.Unpack<ServiceRunTerminalNotificationRetryScheduledEvent>().Attempt == failedAttempt;
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
                    stateEvent.EventData.Is(ServiceRunTerminalNotificationDispatchedEvent.Descriptor)))
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
}
