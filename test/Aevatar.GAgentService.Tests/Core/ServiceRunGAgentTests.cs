using Aevatar.AI.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Runtime.Persistence;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Core.GAgents;
using Aevatar.GAgentService.Tests.TestSupport;
using Aevatar.Scripting.Abstractions;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

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
    public async Task ActivateAsync_ShouldRedeliverPreparedTerminalNotificationAfterRestart()
    {
        var eventStore = new InMemoryEventStore();
        var failingPublisher = new RecordingEventPublisher { FailSends = true };
        var first = GAgentServiceTestKit.CreateStatefulAgent<ServiceRunGAgent, ServiceRunState>(
            eventStore,
            "service-run:run-1",
            static () => new ServiceRunGAgent());
        first.EventPublisher = failingPublisher;
        var record = BuildRecord("run-1");
        record.CompletionNotificationTarget = new ServiceRunCompletionNotificationTarget
        {
            ActorId = "work-order:scope-1:wo-1",
            DeliveryId = "delivery-1",
            ExpiresAtUnixMs = long.MaxValue,
        };
        record.TargetActorId = "role-actor-1";
        await first.HandleRegisterAsync(new RegisterServiceRunRequested { Record = record });

        var terminalUpdate = () => first.HandleRoleChatCompletedAsync(new RoleChatSessionCompletedEvent
        {
            ActorId = "role-actor-1",
            RunContext = new RoleChatRunContext
            {
                RunId = "run-1",
                CommandId = "cmd-run-1",
                CorrelationId = "corr-run-1",
                CompletionNotificationActorId = first.Id,
            },
            Outcome = RoleChatSessionOutcome.Failed,
            FailureCode = "ROLE_EXECUTION_FAILED",
            SafeMessage = "failed",
            TerminalTime = Timestamp.FromDateTimeOffset(
                DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_000_000)),
        });
        await terminalUpdate.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("simulated terminal notification failure");
        first.State.TerminalNotificationDeliveryStatus.Should()
            .Be(ServiceRunTerminalNotificationDeliveryStatus.Prepared);

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
        public bool FailSends { get; init; }

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
            if (FailSends)
                throw new InvalidOperationException("simulated terminal notification failure");

            Sends.Add((targetActorId, evt, options));
            return Task.CompletedTask;
        }
    }
}
