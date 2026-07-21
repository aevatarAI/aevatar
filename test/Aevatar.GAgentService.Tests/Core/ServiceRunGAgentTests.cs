using Aevatar.AI.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Runtime.Callbacks;
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
    public async Task RetryFired_WhenAttemptIsStale_ShouldNotSend()
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
            Attempt = 2,
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

    private sealed class RecordingEventPublisher : IEventPublisher
    {
        public Exception? SendException { get; set; }

        public List<(string TargetActorId, IMessage Event, EventEnvelopePublishOptions? Options)> Sends { get; } = [];

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
            if (SendException != null)
                throw SendException;

            Sends.Add((targetActorId, evt, options));
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
}
