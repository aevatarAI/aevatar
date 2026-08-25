using System.Reflection;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Attributes;
using Aevatar.Foundation.Abstractions.Runtime.Callbacks;
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.GAgents.StudioMember;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.Studio.Tests;

public sealed class StudioMemberBindingRunGAgentStateTests
{
    private const string RootActorId = "studio-member-binding-run:bind-1";
    private readonly StudioMemberBindingRunStateApplier _agent = new();

    [Theory]
    [InlineData(nameof(StudioMemberBindingRunGAgent.HandleAdmissionWatchdogFired))]
    [InlineData(nameof(StudioMemberBindingRunGAgent.HandlePlatformBindingWatchdogFired))]
    [InlineData(nameof(StudioMemberBindingRunGAgent.HandlePlatformBindingCommandsCompleted))]
    [InlineData(nameof(StudioMemberBindingRunGAgent.HandlePlatformBindingReadinessObservationTimedOut))]
    [InlineData(nameof(StudioMemberBindingRunGAgent.HandlePlatformBindingExecutionFailed))]
    [InlineData(nameof(StudioMemberBindingRunGAgent.HandleMemberTerminalNotificationWatchdogFired))]
    public void PlatformBindingContinuationHandlers_ShouldAllowSelfHandling(string handlerName)
    {
        var handler = typeof(StudioMemberBindingRunGAgent).GetMethod(handlerName)
            ?? throw new InvalidOperationException($"Handler '{handlerName}' not found.");

        handler.GetCustomAttribute<EventHandlerAttribute>()
            .Should().NotBeNull()
            .And.Match<EventHandlerAttribute>(attribute => attribute.AllowSelfHandling);
    }

    [Fact]
    public void MemberTerminalNotificationWatchdogHandler_ShouldOnlyAcceptSelfAudience()
    {
        var handler = typeof(StudioMemberBindingRunGAgent).GetMethod(
            nameof(StudioMemberBindingRunGAgent.HandleMemberTerminalNotificationWatchdogFired))
            ?? throw new InvalidOperationException("Terminal notification watchdog handler not found.");

        handler.GetCustomAttribute<EventHandlerAttribute>()
            .Should().NotBeNull()
            .And.Match<EventHandlerAttribute>(attribute =>
                attribute.AllowSelfHandling && attribute.OnlySelfHandling);
    }

    [Fact]
    public void Requested_ShouldPersistAcceptedRunState()
    {
        var requestedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow);

        var state = _agent.Apply(new StudioMemberBindingRunState(), NewRequested(requestedAt));

        state.BindingRunId.Should().Be("bind-1");
        state.ScopeId.Should().Be("scope-1");
        state.MemberId.Should().Be("m-1");
        state.Status.Should().Be(StudioMemberBindingRunStatus.AdmissionPending);
        state.Request.Script.ScriptId.Should().Be("script-1");
        state.AcceptedAtUtc.Should().Be(requestedAt);
        state.UpdatedAtUtc.Should().Be(requestedAt);
    }

    [Fact]
    public void DuplicateRequested_WithDifferentPayload_ShouldBeDetectedAsConflict()
    {
        var requested = NewRequested();
        var state = _agent.Apply(new StudioMemberBindingRunState(), requested);
        var duplicate = NewRequested();
        duplicate.Request.Script.ScriptRevision = "rev-b";

        _agent.IsSameRequest(state.Request, duplicate.Request, state.RequestHash).Should().BeFalse();
    }

    [Fact]
    public void DuplicateRequested_WithSamePayloadAndHash_ShouldBeAcceptedAsIdempotent()
    {
        var requested = NewRequested();
        var state = _agent.Apply(new StudioMemberBindingRunState(), requested);
        var duplicate = NewRequested();

        _agent.IsSameRequest(state.Request, duplicate.Request, state.RequestHash).Should().BeTrue();
    }

    [Fact]
    public async Task ActivateAsync_WhenAdmissionPending_ShouldRestoreAdmissionWatchdog()
    {
        var state = _agent.Apply(new StudioMemberBindingRunState(), NewRequested());
        var publisher = new RecordingEventPublisher();
        var scheduler = new RecordingRuntimeCallbackScheduler();
        var agent = NewHandlerAgent(state, publisher, scheduler);

        await agent.ActivateAsync();

        publisher.SentMessages.Should().ContainSingle(message =>
            message.Event is StudioMemberBindAdmissionRequested);
        scheduler.Timeouts.Should().ContainSingle(request =>
            request.CallbackId == "studio-member-binding-admission-watchdog:bind-1");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ActivateAsync_AfterAuthorityTerminationBeforePlatformStart_ShouldResendAndAcceptCanonicalAck(
        bool admittedBeforeTermination)
    {
        var requested = _agent.Apply(new StudioMemberBindingRunState(), NewRequested());
        var beforeTermination = admittedBeforeTermination ? ApplyAdmitted(requested) : requested;
        var pendingNotification = _agent.Apply(beforeTermination, NewAuthorityTermination());
        pendingNotification.Status.Should()
            .Be(StudioMemberBindingRunStatus.MemberNotificationPending);
        pendingNotification.PlatformBindingCommandId.Should().BeEmpty();
        if (!admittedBeforeTermination)
            pendingNotification.Admitted.Should().BeNull();

        var publisher = new RecordingEventPublisher();
        var scheduler = new RecordingRuntimeCallbackScheduler();
        var eventSourcing = new RecordingEventSourcing(pendingNotification);
        var agent = NewHandlerAgent(
            pendingNotification,
            publisher,
            scheduler,
            eventSourcing: eventSourcing);

        await agent.ActivateAsync();

        StudioMemberBindingRunStateSetter.Get(agent).MemberNotificationAttempt.Should().Be(1);
        publisher.SentMessages.Should().ContainSingle().Which.Event.Should()
            .BeOfType<StudioMemberBindingFailedEvent>();
        scheduler.Timeouts.Should().ContainSingle(request =>
            request.CallbackId == "studio-member-binding-terminal-notification-watchdog:a1:bind-1");

        await agent.HandleEventAsync(new EventEnvelope
        {
            Id = "canonical-terminal-ack",
            Payload = Any.Pack(new StudioMemberBindingTerminalAcknowledged
            {
                BindingRunId = "bind-1",
                Status = StudioMemberBindingRunStatus.Failed,
                AcknowledgedAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            }),
            Route = EnvelopeRouteSemantics.CreateDirect(
                StudioMemberConventions.BuildActorId("scope-1", "m-1"),
                RootActorId),
        });

        StudioMemberBindingRunStateSetter.Get(agent).Status.Should()
            .Be(StudioMemberBindingRunStatus.Failed);
        eventSourcing.CommittedEvents.OfType<StudioMemberBindingTerminalAcknowledged>()
            .Should().ContainSingle();
    }

    [Fact]
    public async Task HandleRequested_WhenAdmissionPendingIsRedelivered_ShouldRestoreAdmissionWatchdog()
    {
        var requested = NewRequested();
        var state = _agent.Apply(new StudioMemberBindingRunState(), requested);
        var publisher = new RecordingEventPublisher();
        var scheduler = new RecordingRuntimeCallbackScheduler();
        var agent = NewHandlerAgent(state, publisher, scheduler);

        await agent.HandleRequested(requested);

        scheduler.Timeouts.Should().ContainSingle(request =>
            request.CallbackId == "studio-member-binding-admission-watchdog:bind-1");
        publisher.SentMessages.Should().ContainSingle(message =>
            message.Event is StudioMemberBindAdmissionRequested);
    }

    [Fact]
    public async Task HandleRequested_WhenAdmissionScheduleFailsAfterCommit_ShouldRequireRuntimeRedelivery()
    {
        var requested = NewRequested();
        var eventSourcing = new RecordingEventSourcing(new StudioMemberBindingRunState());
        var publisher = new RecordingEventPublisher();
        var scheduler = new RecordingRuntimeCallbackScheduler
        {
            ScheduleException = new InvalidOperationException("simulated admission schedule failure"),
        };
        var agent = NewHandlerAgent(
            new StudioMemberBindingRunState(),
            publisher,
            scheduler,
            eventSourcing: eventSourcing);

        Func<Task> firstDelivery = () => agent.HandleRequested(requested);
        var failure = await firstDelivery.Should().ThrowAsync<Exception>();
        failure.Which.Should().BeAssignableTo<IRuntimeEnvelopeRetryableException>();
        failure.Which.InnerException.Should().BeOfType<InvalidOperationException>()
            .Which.Message.Should().Be("simulated admission schedule failure");
        eventSourcing.CommittedEvents.OfType<StudioMemberBindingRunRequested>()
            .Should().ContainSingle();
        publisher.SentMessages.Should().BeEmpty();

        scheduler.ScheduleException = null;
        await agent.HandleRequested(requested);

        eventSourcing.CommittedEvents.OfType<StudioMemberBindingRunRequested>()
            .Should().ContainSingle();
        scheduler.Timeouts.Should().ContainSingle(request =>
            request.CallbackId == "studio-member-binding-admission-watchdog:bind-1");
        publisher.SentMessages.Should().ContainSingle(message =>
            message.Event is StudioMemberBindAdmissionRequested);
    }

    [Fact]
    public async Task HandleAdmissionWatchdogFired_WhenAdmissionPending_ShouldRedispatchAndReschedule()
    {
        var state = _agent.Apply(new StudioMemberBindingRunState(), NewRequested());
        var publisher = new RecordingEventPublisher();
        var scheduler = new RecordingRuntimeCallbackScheduler();
        var agent = NewHandlerAgent(state, publisher, scheduler);

        await agent.HandleAdmissionWatchdogFired(new StudioMemberBindingAdmissionWatchdogFired
        {
            BindingRunId = "bind-1",
        });

        publisher.SentMessages.Should().ContainSingle(message =>
            message.Event is StudioMemberBindAdmissionRequested);
        var callback = scheduler.Timeouts.Should().ContainSingle().Subject;
        callback.CallbackId.Should().Be("studio-member-binding-admission-watchdog:bind-1");
        callback.TriggerEnvelope.Payload.Unpack<StudioMemberBindingAdmissionWatchdogFired>()
            .BindingRunId.Should().Be("bind-1");
    }

    [Fact]
    public async Task HandleAdmissionWatchdogFired_AfterAdmission_ShouldBeIgnored()
    {
        var requested = _agent.Apply(new StudioMemberBindingRunState(), NewRequested());
        var admitted = ApplyAdmitted(requested);
        var publisher = new RecordingEventPublisher();
        var scheduler = new RecordingRuntimeCallbackScheduler();
        var agent = NewHandlerAgent(admitted, publisher, scheduler);

        await agent.HandleAdmissionWatchdogFired(new StudioMemberBindingAdmissionWatchdogFired
        {
            BindingRunId = "bind-1",
        });

        publisher.SentMessages.Should().BeEmpty();
        scheduler.Timeouts.Should().BeEmpty();
    }

    [Fact]
    public void Admitted_ShouldCaptureMemberSnapshot()
    {
        var accepted = _agent.Apply(new StudioMemberBindingRunState(), NewRequested());
        var admittedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow.AddSeconds(1));

        var admitted = _agent.Apply(accepted, new StudioMemberBindingAdmittedEvent
        {
            BindingRunId = "bind-1",
            ScopeId = "scope-1",
            MemberId = "m-1",
            PublishedServiceId = "member-m-1",
            ImplementationKind = StudioMemberImplementationKind.Script,
            DisplayName = "Script member",
            AdmittedAtUtc = admittedAt,
        });

        admitted.Status.Should().Be(StudioMemberBindingRunStatus.Admitted);
        admitted.Admitted.PublishedServiceId.Should().Be("member-m-1");
        admitted.Admitted.ImplementationKind.Should().Be(StudioMemberImplementationKind.Script);
        admitted.UpdatedAtUtc.Should().Be(admittedAt);
    }

    [Fact]
    public async Task HandleAdmitted_WhenSelfContinuationFailsAfterCommit_ShouldRecoverOnRuntimeRedelivery()
    {
        var state = _agent.Apply(new StudioMemberBindingRunState(), NewRequested());
        var admitted = new StudioMemberBindingAdmittedEvent
        {
            BindingRunId = "bind-1",
            ScopeId = "scope-1",
            MemberId = "m-1",
            PublishedServiceId = "member-m-1",
            ImplementationKind = StudioMemberImplementationKind.Script,
            DisplayName = "Script member",
            AdmittedAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
        };
        var eventSourcing = new RecordingEventSourcing(state);
        var publisher = new RecordingEventPublisher
        {
            SendException = new InvalidOperationException("simulated self continuation failure"),
        };
        var agent = NewHandlerAgent(state, publisher, eventSourcing: eventSourcing);

        Func<Task> firstDelivery = () => agent.HandleAdmitted(admitted);
        var failure = await firstDelivery.Should().ThrowAsync<Exception>();
        failure.Which.Should().BeAssignableTo<IRuntimeEnvelopeRetryableException>();
        failure.Which.InnerException.Should().BeOfType<InvalidOperationException>()
            .Which.Message.Should().Be("simulated self continuation failure");
        StudioMemberBindingRunStateSetter.Get(agent).Status.Should()
            .Be(StudioMemberBindingRunStatus.Admitted);
        eventSourcing.CommittedEvents.OfType<StudioMemberBindingAdmittedEvent>()
            .Should().ContainSingle();

        publisher.SendException = null;
        await agent.HandleEventAsync(RuntimeRetryEnvelope(admitted));

        eventSourcing.CommittedEvents.OfType<StudioMemberBindingAdmittedEvent>()
            .Should().ContainSingle();
        publisher.SentMessages.Should().ContainSingle().Which.Event.Should()
            .BeOfType<StudioMemberPlatformBindingExecutionStartRequested>();
    }

    [Fact]
    public void PlatformBindingStartRequested_ShouldPersistCommandIdForRecovery()
    {
        var requested = _agent.Apply(new StudioMemberBindingRunState(), NewRequested());
        var admitted = ApplyAdmitted(requested);

        var pending = _agent.Apply(admitted, new StudioMemberPlatformBindingExecutionStartRequested
        {
            BindingRunId = "bind-1",
            PlatformBindingCommandId = "platform-bind-1",
            RequestedAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow.AddSeconds(1)),
            ProtocolVersion = StudioMemberConventions.PlatformBindingProtocolVersion,
            ExecutionAttempt = 0,
        });

        pending.Status.Should().Be(StudioMemberBindingRunStatus.PlatformBindingPending);
        pending.PlatformBindingCommandId.Should().Be("platform-bind-1");
        pending.PlatformBindingProtocolVersion.Should().Be(StudioMemberConventions.PlatformBindingProtocolVersion);
        pending.PlatformExecutionAttempt.Should().Be(0);
        pending.PlatformExecutionStage.Should().Be(StudioMemberPlatformBindingExecutionStage.AcceptancePending);
        pending.AttemptCount.Should().Be(1);
    }

    [Fact]
    public async Task HandlePlatformBindingStartRequested_ShouldAtomicallyCommitLegacyRollbackFenceBeforePortCall()
    {
        var requested = _agent.Apply(new StudioMemberBindingRunState(), NewRequested());
        var admitted = ApplyAdmitted(requested);
        var publisher = new RecordingEventPublisher();
        var platformPort = new RecordingPlatformBindingCommandPort();
        var eventSourcing = new RecordingEventSourcing(admitted);
        var agent = NewHandlerAgent(
            admitted,
            publisher,
            platformPort: platformPort,
            eventSourcing: eventSourcing);
        var start = new StudioMemberPlatformBindingExecutionStartRequested
        {
            BindingRunId = "bind-1",
            PlatformBindingCommandId = "platform-1",
            Request = requested.Request.Clone(),
            Admitted = admitted.Admitted.Clone(),
            RequestedAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            ProtocolVersion = StudioMemberConventions.PlatformBindingProtocolVersion,
            ExecutionAttempt = 0,
        };

        await agent.HandlePlatformBindingStartRequested(start);

        var batch = eventSourcing.CommittedBatches.Should().ContainSingle().Subject;
        batch.Should().HaveCount(3);
        batch[0].Should().BeOfType<StudioMemberPlatformBindingExecutionStartRequested>();
        batch[1].Should().BeOfType<StudioMemberPlatformBindingStartRequested>();
        var legacyStarted = batch[2].Should()
            .BeOfType<StudioMemberPlatformBindingExecutionStarted>().Subject;
        legacyStarted.StartedAtUtc.ToDateTimeOffset().Year.Should().Be(9999);
        platformPort.StartRequests.Should().ContainSingle();

        var legacyEvents = NewLegacyPlatformEventPrefix().Take(2).Concat(batch);
        var legacyState = ReplayAsDfe98c8Reader(legacyEvents);
        legacyState.Status.Should().Be(StudioMemberBindingRunStatus.PlatformBindingPending);
        legacyState.PlatformExecutionInFlight.Should().BeTrue();
        legacyState.PlatformExecutionStartedAtUtc!.ToDateTimeOffset().Year.Should().Be(9999);
        LegacyReaderWouldRecoverPlatformCommand(legacyState).Should().BeFalse();
    }

    [Fact]
    public void ProtocolOneCommittedCutPoints_ShouldKeepDfe98c8ReaderBehindLegacyRollbackFence()
    {
        var requested = NewRequested();
        var admitted = new StudioMemberBindingAdmittedEvent
        {
            BindingRunId = "bind-1",
            ScopeId = "scope-1",
            MemberId = "m-1",
            PublishedServiceId = "member-m-1",
            ImplementationKind = StudioMemberImplementationKind.Script,
            DisplayName = "Script member",
            AdmittedAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
        };
        var protocolOneStart = new StudioMemberPlatformBindingExecutionStartRequested
        {
            BindingRunId = "bind-1",
            PlatformBindingCommandId = "platform-1",
            Request = requested.Request.Clone(),
            Admitted = new StudioMemberBindingAdmittedSnapshot
            {
                MemberId = "m-1",
                ScopeId = "scope-1",
                PublishedServiceId = "member-m-1",
                ImplementationKind = StudioMemberImplementationKind.Script,
                DisplayName = "Script member",
            },
            RequestedAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            ProtocolVersion = StudioMemberConventions.PlatformBindingProtocolVersion,
            ExecutionAttempt = 0,
        };
        var committed = new List<IMessage>
        {
            requested,
            admitted,
            protocolOneStart,
            new StudioMemberPlatformBindingStartRequested
            {
                BindingRunId = "bind-1",
                PlatformBindingCommandId = "platform-1",
                Request = protocolOneStart.Request.Clone(),
                Admitted = protocolOneStart.Admitted.Clone(),
                RequestedAtUtc = protocolOneStart.RequestedAtUtc.Clone(),
            },
            new StudioMemberPlatformBindingExecutionStarted
            {
                BindingRunId = "bind-1",
                PlatformBindingCommandId = "platform-1",
                StartedAtUtc = NewLegacyExecutionFenceTimestamp(),
            },
        };
        var laterCutPoints = new IMessage[]
        {
            new StudioMemberPlatformBindingExecutionStartAccepted
            {
                BindingRunId = "bind-1",
                PlatformBindingCommandId = "platform-1",
                AcceptedAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
                ProtocolVersion = StudioMemberConventions.PlatformBindingProtocolVersion,
                ExecutionAttempt = 0,
            },
            new StudioMemberPlatformBindingStageStarted
            {
                BindingRunId = "bind-1",
                PlatformBindingCommandId = "platform-1",
                ProtocolVersion = StudioMemberConventions.PlatformBindingProtocolVersion,
                ExecutionAttempt = 1,
                ExecutionStage = StudioMemberPlatformBindingExecutionStage.CommandInFlight,
                StageStartedAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            },
            NewCommandsCompleted(),
            new StudioMemberPlatformBindingStageStarted
            {
                BindingRunId = "bind-1",
                PlatformBindingCommandId = "platform-1",
                ProtocolVersion = StudioMemberConventions.PlatformBindingProtocolVersion,
                ExecutionAttempt = 2,
                ExecutionStage = StudioMemberPlatformBindingExecutionStage.ReadinessInFlight,
                StageStartedAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            },
            new StudioMemberPlatformBindingReadinessObservationTimedOut
            {
                BindingRunId = "bind-1",
                PlatformBindingCommandId = "platform-1",
                ProtocolVersion = StudioMemberConventions.PlatformBindingProtocolVersion,
                ExecutionAttempt = 2,
                TimedOutAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            },
            new StudioMemberPlatformBindingStageStarted
            {
                BindingRunId = "bind-1",
                PlatformBindingCommandId = "platform-1",
                ProtocolVersion = StudioMemberConventions.PlatformBindingProtocolVersion,
                ExecutionAttempt = 3,
                ExecutionStage = StudioMemberPlatformBindingExecutionStage.ReadinessInFlight,
                StageStartedAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            },
        };

        AssertDfe98c8ReaderRemainsFenced(committed, "v1 start fence");
        foreach (var cutPoint in laterCutPoints)
        {
            committed.Add(cutPoint);
            AssertDfe98c8ReaderRemainsFenced(committed, cutPoint.Descriptor.Name);
        }

        foreach (var terminal in new IMessage[]
        {
            NewExecutionSucceeded(3),
            new StudioMemberPlatformBindingExecutionFailed
            {
                BindingRunId = "bind-1",
                PlatformBindingCommandId = "platform-1",
                ProtocolVersion = StudioMemberConventions.PlatformBindingProtocolVersion,
                ExecutionAttempt = 3,
                ExecutionStage = StudioMemberPlatformBindingExecutionStage.ReadinessInFlight,
                Failure = new StudioMemberBindingFailure
                {
                    Code = "READINESS_FAILED",
                    Message = "synthetic terminal failure",
                    FailedAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
                },
            },
        })
        {
            AssertDfe98c8ReaderRemainsFenced(committed.Append(terminal), terminal.Descriptor.Name);
        }
    }

    [Fact]
    public void DuplicatePlatformBindingStart_AfterPlatformBindingPending_ShouldNotRegressOrIncrementAttempt()
    {
        var pending = NewPlatformPendingState();

        var afterDuplicateStart = _agent.Apply(pending, new StudioMemberPlatformBindingExecutionStartRequested
        {
            BindingRunId = "bind-1",
            PlatformBindingCommandId = "platform-2",
            RequestedAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow.AddSeconds(10)),
            ProtocolVersion = StudioMemberConventions.PlatformBindingProtocolVersion,
            ExecutionAttempt = 0,
        });

        afterDuplicateStart.Status.Should().Be(StudioMemberBindingRunStatus.PlatformBindingPending);
        afterDuplicateStart.PlatformBindingCommandId.Should().Be("platform-1");
        afterDuplicateStart.AttemptCount.Should().Be(1);
    }

    [Fact]
    public async Task HandlePlatformBindingStartRequested_AfterPlatformBindingPending_ShouldNotRestartPlatformBinding()
    {
        var pending = NewPlatformPendingState();
        var publisher = new RecordingEventPublisher();
        var platformPort = new RecordingPlatformBindingCommandPort();
        var agent = NewHandlerAgent(pending, publisher, platformPort: platformPort);

        await agent.HandlePlatformBindingStartRequested(new StudioMemberPlatformBindingExecutionStartRequested
        {
            BindingRunId = "bind-1",
            PlatformBindingCommandId = "platform-2",
            RequestedAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow.AddSeconds(10)),
            ProtocolVersion = StudioMemberConventions.PlatformBindingProtocolVersion,
            ExecutionAttempt = 0,
        });

        platformPort.StartRequests.Should().BeEmpty();
        publisher.SentMessages.Should().BeEmpty();
    }

    [Fact]
    public void DuplicateAdmission_AfterPlatformBindingPending_ShouldNotRegressOrRotateCommandId()
    {
        var pending = NewPlatformPendingState();

        var afterDuplicateAdmission = _agent.Apply(pending, new StudioMemberBindingAdmittedEvent
        {
            BindingRunId = "bind-1",
            ScopeId = "scope-1",
            MemberId = "m-1",
            PublishedServiceId = "member-m-1",
            ImplementationKind = StudioMemberImplementationKind.Script,
            DisplayName = "Script member",
            AdmittedAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow.AddSeconds(10)),
        });

        afterDuplicateAdmission.Status.Should().Be(StudioMemberBindingRunStatus.PlatformBindingPending);
        afterDuplicateAdmission.PlatformBindingCommandId.Should().Be("platform-1");
        afterDuplicateAdmission.AttemptCount.Should().Be(1);
    }

    [Fact]
    public void PlatformSucceeded_ShouldRecordTerminalResult()
    {
        var accepted = NewReadinessInFlightState(DateTimeOffset.UtcNow.AddSeconds(-1));
        var completedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow.AddSeconds(2));

        var succeeded = _agent.Apply(accepted, new StudioMemberPlatformBindingExecutionSucceeded
        {
            BindingRunId = "bind-1",
            PlatformBindingCommandId = "platform-1",
            CompletedAtUtc = completedAt,
            ProtocolVersion = StudioMemberConventions.PlatformBindingProtocolVersion,
            ExecutionAttempt = 2,
            Result = new StudioMemberPlatformBindingResult
            {
                PublishedServiceId = "member-m-1",
                RevisionId = "rev-1",
                ImplementationKind = StudioMemberImplementationKind.Script,
                ExpectedActorId = "actor-1",
            },
        });

        succeeded.Status.Should().Be(StudioMemberBindingRunStatus.MemberNotificationPending);
        succeeded.PlatformResult.RevisionId.Should().Be("rev-1");
        succeeded.PlatformBindingRecoverySnapshot.Should().BeNull();
        succeeded.PlatformExecutionStage.Should()
            .Be(StudioMemberPlatformBindingExecutionStage.Unspecified);
        succeeded.PlatformExecutionInFlight.Should().BeFalse();
        succeeded.PlatformExecutionStartedAtUtc.Should().BeNull();
        succeeded.PlatformExecutionStageStartedAtUtc.Should().BeNull();
        succeeded.UpdatedAtUtc.Should().Be(completedAt);
    }

    [Fact]
    public void MemberTerminalAcknowledged_AfterPlatformSuccess_ShouldMarkRunSucceeded()
    {
        var pendingNotification = _agent.Apply(
            NewReadinessInFlightState(DateTimeOffset.UtcNow.AddSeconds(-1)),
            new StudioMemberPlatformBindingExecutionSucceeded
        {
            BindingRunId = "bind-1",
            PlatformBindingCommandId = "platform-1",
            CompletedAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow.AddSeconds(2)),
            ProtocolVersion = StudioMemberConventions.PlatformBindingProtocolVersion,
            ExecutionAttempt = 2,
            Result = new StudioMemberPlatformBindingResult
            {
                PublishedServiceId = "member-m-1",
                RevisionId = "rev-1",
                ImplementationKind = StudioMemberImplementationKind.Script,
            },
        });
        var acknowledgedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow.AddSeconds(3));

        var succeeded = _agent.Apply(pendingNotification, new StudioMemberBindingTerminalAcknowledged
        {
            BindingRunId = "bind-1",
            Status = StudioMemberBindingRunStatus.Succeeded,
            AcknowledgedAtUtc = acknowledgedAt,
        });

        succeeded.Status.Should().Be(StudioMemberBindingRunStatus.Succeeded);
        succeeded.PlatformResult.RevisionId.Should().Be("rev-1");
        succeeded.UpdatedAtUtc.Should().Be(acknowledgedAt);
    }

    [Fact]
    public void PlatformSucceeded_WithDifferentCommandId_ShouldBeIgnored()
    {
        var accepted = NewReadinessInFlightState(DateTimeOffset.UtcNow.AddSeconds(-1));

        var stale = _agent.Apply(accepted, new StudioMemberPlatformBindingExecutionSucceeded
        {
            BindingRunId = "bind-1",
            PlatformBindingCommandId = "platform-stale",
            CompletedAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow.AddSeconds(2)),
            ProtocolVersion = StudioMemberConventions.PlatformBindingProtocolVersion,
            ExecutionAttempt = 2,
            Result = new StudioMemberPlatformBindingResult
            {
                PublishedServiceId = "member-m-1",
                RevisionId = "rev-stale",
                ImplementationKind = StudioMemberImplementationKind.Script,
            },
        });

        stale.Status.Should().Be(StudioMemberBindingRunStatus.PlatformBindingPending);
        stale.PlatformResult.Should().BeNull();
    }

    [Fact]
    public void PlatformFailed_ShouldRecordTerminalFailure()
    {
        var accepted = NewInFlightState(DateTimeOffset.UtcNow.AddSeconds(-1));
        var failedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow.AddSeconds(2));

        var failed = _agent.Apply(accepted, new StudioMemberPlatformBindingExecutionFailed
        {
            BindingRunId = "bind-1",
            PlatformBindingCommandId = "platform-1",
            ProtocolVersion = StudioMemberConventions.PlatformBindingProtocolVersion,
            ExecutionAttempt = 1,
            ExecutionStage = StudioMemberPlatformBindingExecutionStage.CommandInFlight,
            Failure = new StudioMemberBindingFailure
            {
                Code = "SCOPE_BINDING_FAILED",
                Message = "platform failed",
                FailedAtUtc = failedAt,
            },
        });

        failed.Status.Should().Be(StudioMemberBindingRunStatus.MemberNotificationPending);
        failed.Failure.Code.Should().Be("SCOPE_BINDING_FAILED");
        failed.PlatformBindingRecoverySnapshot.Should().BeNull();
        failed.PlatformExecutionInFlight.Should().BeFalse();
        failed.PlatformExecutionStartedAtUtc.Should().BeNull();
        failed.PlatformExecutionStageStartedAtUtc.Should().BeNull();
        failed.UpdatedAtUtc.Should().Be(failedAt);
    }

    [Fact]
    public async Task MemberBindingAuthorityTerminated_ShouldCommitNotifyAndConvergeAfterAck()
    {
        var inFlight = NewInFlightState(DateTimeOffset.UtcNow.AddSeconds(-10));
        var eventSourcing = new RecordingEventSourcing(inFlight);
        var publisher = new RecordingEventPublisher();
        var scheduler = new RecordingRuntimeCallbackScheduler();
        var agent = NewHandlerAgent(inFlight, publisher, scheduler, eventSourcing: eventSourcing);
        var terminated = NewAuthorityTermination();

        await agent.HandleMemberBindingAuthorityTerminated(terminated);

        var state = StudioMemberBindingRunStateSetter.Get(agent);
        state.Status.Should().Be(StudioMemberBindingRunStatus.MemberNotificationPending);
        state.Failure.Should().BeEquivalentTo(terminated.Failure);
        state.PlatformResult.Should().BeNull();
        state.PlatformExecutionInFlight.Should().BeFalse();
        state.PlatformExecutionStartedAtUtc.Should().BeNull();
        state.PlatformExecutionStage.Should()
            .Be(StudioMemberPlatformBindingExecutionStage.CommandInFlight);
        state.PlatformExecutionStageStartedAtUtc.Should().BeNull();
        state.PlatformExecutionAttempt.Should().Be(1);
        state.PlatformBindingRecoverySnapshot.Should().BeNull();
        state.MemberNotificationAttempt.Should().Be(1);

        eventSourcing.CommittedEvents.Should().HaveCount(2);
        eventSourcing.CommittedEvents[0].Should()
            .BeOfType<StudioMemberBindingAuthorityTerminated>();
        eventSourcing.CommittedEvents[1].Should()
            .BeOfType<StudioMemberBindingTerminalNotificationAttemptStarted>();
        scheduler.Timeouts.Should().ContainSingle(request =>
            request.CallbackId == "studio-member-binding-terminal-notification-watchdog:a1:bind-1");
        var sent = publisher.SentMessages.Should().ContainSingle().Subject;
        sent.TargetActorId.Should().Be(StudioMemberConventions.BuildActorId("scope-1", "m-1"));
        var failed = sent.Event.Should().BeOfType<StudioMemberBindingFailedEvent>().Subject;
        failed.Failure.Code.Should().Be("STUDIO_MEMBER_DELETED");

        await agent.HandleMemberBindingTerminalAcknowledged(new StudioMemberBindingTerminalAcknowledged
        {
            BindingRunId = "bind-1",
            Status = StudioMemberBindingRunStatus.Failed,
            AcknowledgedAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow.AddSeconds(1)),
        });
        await agent.HandleMemberTerminalNotificationWatchdogFired(
            new StudioMemberBindingTerminalNotificationWatchdogFired
            {
                BindingRunId = "bind-1",
                ExpectedNotificationAttempt = 1,
            });

        StudioMemberBindingRunStateSetter.Get(agent).Status.Should()
            .Be(StudioMemberBindingRunStatus.Failed);
        StudioMemberBindingRunStateSetter.Get(agent).MemberNotificationAttempt.Should().Be(1);
        publisher.SentMessages.Should().ContainSingle();
        scheduler.Timeouts.Should().ContainSingle();
        eventSourcing.CommittedEvents.OfType<StudioMemberBindingTerminalNotificationAttemptStarted>()
            .Should().ContainSingle();
    }

    [Fact]
    public async Task DuplicateMemberBindingAuthorityTermination_WithoutRuntimeRetry_ShouldHaveNoSideEffects()
    {
        var inFlight = NewInFlightState(DateTimeOffset.UtcNow.AddSeconds(-10));
        var eventSourcing = new RecordingEventSourcing(inFlight);
        var publisher = new RecordingEventPublisher();
        var scheduler = new RecordingRuntimeCallbackScheduler();
        var agent = NewHandlerAgent(inFlight, publisher, scheduler, eventSourcing: eventSourcing);
        var terminated = NewAuthorityTermination();

        await agent.HandleMemberBindingAuthorityTerminated(terminated);
        await agent.HandleMemberBindingAuthorityTerminated(terminated.Clone());

        var state = StudioMemberBindingRunStateSetter.Get(agent);
        state.Status.Should().Be(StudioMemberBindingRunStatus.MemberNotificationPending);
        state.Failure.Should().BeEquivalentTo(terminated.Failure);
        state.MemberNotificationAttempt.Should().Be(1);
        eventSourcing.CommittedEvents.OfType<StudioMemberBindingAuthorityTerminated>()
            .Should().ContainSingle();
        eventSourcing.CommittedEvents.OfType<StudioMemberBindingTerminalNotificationAttemptStarted>()
            .Should().ContainSingle().Which.NotificationAttempt.Should().Be(1);
        publisher.SentMessages.Should().ContainSingle();
        scheduler.Timeouts.Should().ContainSingle();
    }

    [Fact]
    public async Task LatePlatformOutcomes_AfterAuthorityTermination_ShouldNotMutateOrNotify()
    {
        var readinessInFlight = NewReadinessInFlightState(DateTimeOffset.UtcNow.AddSeconds(-10));
        var terminated = _agent.Apply(readinessInFlight, NewAuthorityTermination());
        var attempted = _agent.Apply(terminated, new StudioMemberBindingTerminalNotificationAttemptStarted
        {
            BindingRunId = "bind-1",
            NotificationAttempt = 1,
            AttemptedAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
        });
        var originalBytes = attempted.ToByteArray();
        var eventSourcing = new RecordingEventSourcing(attempted);
        var publisher = new RecordingEventPublisher();
        var scheduler = new RecordingRuntimeCallbackScheduler();
        var agent = NewHandlerAgent(attempted, publisher, scheduler, eventSourcing: eventSourcing);

        await agent.HandlePlatformBindingExecutionSucceeded(NewExecutionSucceeded(2));
        await agent.HandlePlatformBindingExecutionFailed(new StudioMemberPlatformBindingExecutionFailed
        {
            BindingRunId = "bind-1",
            PlatformBindingCommandId = "platform-1",
            ProtocolVersion = StudioMemberConventions.PlatformBindingProtocolVersion,
            ExecutionAttempt = 2,
            ExecutionStage = StudioMemberPlatformBindingExecutionStage.ReadinessInFlight,
            Failure = new StudioMemberBindingFailure
            {
                Code = "LATE_PLATFORM_FAILURE",
                Message = "late platform failure",
                FailedAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow.AddSeconds(1)),
            },
        });

        StudioMemberBindingRunStateSetter.Get(agent).ToByteArray().Should().Equal(originalBytes);
        eventSourcing.CommittedEvents.Should().BeEmpty();
        publisher.SentMessages.Should().BeEmpty();
        scheduler.Timeouts.Should().BeEmpty();
    }

    [Fact]
    public async Task AuthorityTermination_AfterUnacknowledgedPlatformSuccess_ShouldOverrideAndFenceOldWatchdog()
    {
        var successPending = _agent.Apply(
            NewReadinessInFlightState(DateTimeOffset.UtcNow.AddSeconds(-10)),
            NewExecutionSucceeded(2));
        successPending = _agent.Apply(successPending, new StudioMemberBindingTerminalNotificationAttemptStarted
        {
            BindingRunId = "bind-1",
            NotificationAttempt = 1,
            AttemptedAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow.AddSeconds(-2)),
        });
        var eventSourcing = new RecordingEventSourcing(successPending);
        var publisher = new RecordingEventPublisher();
        var scheduler = new RecordingRuntimeCallbackScheduler();
        var agent = NewHandlerAgent(successPending, publisher, scheduler, eventSourcing: eventSourcing);

        await agent.HandleMemberBindingAuthorityTerminated(NewAuthorityTermination());
        await agent.HandleMemberTerminalNotificationWatchdogFired(
            new StudioMemberBindingTerminalNotificationWatchdogFired
            {
                BindingRunId = "bind-1",
                ExpectedNotificationAttempt = 1,
            });

        var state = StudioMemberBindingRunStateSetter.Get(agent);
        state.Status.Should().Be(StudioMemberBindingRunStatus.MemberNotificationPending);
        state.PlatformResult.Should().BeNull();
        state.Failure.Code.Should().Be("STUDIO_MEMBER_DELETED");
        state.MemberNotificationAttempt.Should().Be(2);
        eventSourcing.CommittedEvents.OfType<StudioMemberBindingTerminalNotificationAttemptStarted>()
            .Should().ContainSingle().Which.NotificationAttempt.Should().Be(2);
        scheduler.Timeouts.Should().ContainSingle(request =>
            request.CallbackId == "studio-member-binding-terminal-notification-watchdog:a2:bind-1");
        publisher.SentMessages.Should().ContainSingle().Which.Event.Should()
            .BeOfType<StudioMemberBindingFailedEvent>();
    }

    [Fact]
    public void AuthorityTermination_AfterUnacknowledgedPlatformFailure_ShouldOverrideWithDeletionFailure()
    {
        var failurePending = _agent.Apply(
            NewInFlightState(DateTimeOffset.UtcNow.AddSeconds(-10)),
            new StudioMemberPlatformBindingExecutionFailed
            {
                BindingRunId = "bind-1",
                PlatformBindingCommandId = "platform-1",
                ProtocolVersion = StudioMemberConventions.PlatformBindingProtocolVersion,
                ExecutionAttempt = 1,
                ExecutionStage = StudioMemberPlatformBindingExecutionStage.CommandInFlight,
                Failure = new StudioMemberBindingFailure
                {
                    Code = "PLATFORM_FAILURE",
                    Message = "platform failure",
                    FailedAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow.AddSeconds(-2)),
                },
            });
        failurePending = _agent.Apply(failurePending, new StudioMemberBindingTerminalNotificationAttemptStarted
        {
            BindingRunId = "bind-1",
            NotificationAttempt = 1,
            AttemptedAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow.AddSeconds(-1)),
        });

        var terminated = _agent.Apply(failurePending, NewAuthorityTermination());

        terminated.Status.Should().Be(StudioMemberBindingRunStatus.MemberNotificationPending);
        terminated.Failure.Code.Should().Be("STUDIO_MEMBER_DELETED");
        terminated.MemberNotificationAttempt.Should().Be(1);
        terminated.PlatformExecutionStage.Should()
            .Be(StudioMemberPlatformBindingExecutionStage.CommandInFlight);
        terminated.PlatformExecutionAttempt.Should().Be(1);
    }

    [Fact]
    public async Task InvalidOrForeignAuthorityTermination_ShouldBeIgnored()
    {
        var inFlight = NewInFlightState(DateTimeOffset.UtcNow.AddSeconds(-10));
        var eventSourcing = new RecordingEventSourcing(inFlight);
        var publisher = new RecordingEventPublisher();
        var scheduler = new RecordingRuntimeCallbackScheduler();
        var agent = NewHandlerAgent(inFlight, publisher, scheduler, eventSourcing: eventSourcing);
        var invalid = new List<StudioMemberBindingAuthorityTerminated>();

        var staleRun = NewAuthorityTermination();
        staleRun.BindingRunId = "bind-stale";
        invalid.Add(staleRun);
        var foreignScope = NewAuthorityTermination();
        foreignScope.ScopeId = "scope-foreign";
        invalid.Add(foreignScope);
        var foreignMember = NewAuthorityTermination();
        foreignMember.MemberId = "member-foreign";
        invalid.Add(foreignMember);
        var wrongCode = NewAuthorityTermination();
        wrongCode.Failure.Code = "PLATFORM_FAILURE";
        invalid.Add(wrongCode);
        var missingTimestamp = NewAuthorityTermination();
        missingTimestamp.Failure.FailedAtUtc = null;
        invalid.Add(missingTimestamp);
        var missingFailure = NewAuthorityTermination();
        missingFailure.Failure = null;
        invalid.Add(missingFailure);

        foreach (var evt in invalid)
            await agent.HandleMemberBindingAuthorityTerminated(evt);

        await agent.HandleEventAsync(new EventEnvelope
        {
            Id = "foreign-authority-termination",
            Payload = Any.Pack(NewAuthorityTermination()),
            Route = EnvelopeRouteSemantics.CreateDirect("studio-member:scope-1:foreign", RootActorId),
        });

        StudioMemberBindingRunStateSetter.Get(agent).ToByteArray().Should().Equal(inFlight.ToByteArray());
        eventSourcing.CommittedEvents.Should().BeEmpty();
        publisher.SentMessages.Should().BeEmpty();
        scheduler.Timeouts.Should().BeEmpty();
    }

    [Fact]
    public async Task CanonicalMemberAuthorityTerminationEnvelope_ShouldCommitAndNotify()
    {
        var inFlight = NewInFlightState(DateTimeOffset.UtcNow.AddSeconds(-10));
        var eventSourcing = new RecordingEventSourcing(inFlight);
        var publisher = new RecordingEventPublisher();
        var scheduler = new RecordingRuntimeCallbackScheduler();
        var agent = NewHandlerAgent(inFlight, publisher, scheduler, eventSourcing: eventSourcing);

        await agent.HandleEventAsync(new EventEnvelope
        {
            Id = "canonical-authority-termination",
            Payload = Any.Pack(NewAuthorityTermination()),
            Route = EnvelopeRouteSemantics.CreateDirect(
                StudioMemberConventions.BuildActorId("scope-1", "m-1"),
                RootActorId),
        });

        StudioMemberBindingRunStateSetter.Get(agent).Failure.Code.Should()
            .Be("STUDIO_MEMBER_DELETED");
        eventSourcing.CommittedEvents.OfType<StudioMemberBindingAuthorityTerminated>()
            .Should().ContainSingle();
        publisher.SentMessages.Should().ContainSingle().Which.Event.Should()
            .BeOfType<StudioMemberBindingFailedEvent>();
        scheduler.Timeouts.Should().ContainSingle();
    }

    [Fact]
    public async Task ForeignMemberTerminalAcknowledgementEnvelope_ShouldNotStopWatchdogRecovery()
    {
        var pendingNotification = _agent.Apply(
            NewInFlightState(DateTimeOffset.UtcNow.AddSeconds(-10)),
            NewAuthorityTermination());
        pendingNotification = _agent.Apply(
            pendingNotification,
            new StudioMemberBindingTerminalNotificationAttemptStarted
            {
                BindingRunId = "bind-1",
                NotificationAttempt = 1,
                AttemptedAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow.AddSeconds(-1)),
            });
        var eventSourcing = new RecordingEventSourcing(pendingNotification);
        var publisher = new RecordingEventPublisher();
        var scheduler = new RecordingRuntimeCallbackScheduler();
        var agent = NewHandlerAgent(
            pendingNotification,
            publisher,
            scheduler,
            eventSourcing: eventSourcing);

        await agent.HandleEventAsync(new EventEnvelope
        {
            Id = "foreign-terminal-ack",
            Payload = Any.Pack(new StudioMemberBindingTerminalAcknowledged
            {
                BindingRunId = "bind-1",
                Status = StudioMemberBindingRunStatus.Failed,
                AcknowledgedAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            }),
            Route = EnvelopeRouteSemantics.CreateDirect(
                StudioMemberConventions.BuildActorId("scope-1", "foreign"),
                RootActorId),
        });

        StudioMemberBindingRunStateSetter.Get(agent).Status.Should()
            .Be(StudioMemberBindingRunStatus.MemberNotificationPending);
        eventSourcing.CommittedEvents.Should().BeEmpty();

        await agent.HandleMemberTerminalNotificationWatchdogFired(
            new StudioMemberBindingTerminalNotificationWatchdogFired
            {
                BindingRunId = "bind-1",
                ExpectedNotificationAttempt = 1,
            });

        StudioMemberBindingRunStateSetter.Get(agent).MemberNotificationAttempt.Should().Be(2);
        publisher.SentMessages.Should().ContainSingle().Which.Event.Should()
            .BeOfType<StudioMemberBindingFailedEvent>();
        scheduler.Timeouts.Should().ContainSingle(request =>
            request.CallbackId == "studio-member-binding-terminal-notification-watchdog:a2:bind-1");
    }

    [Fact]
    public async Task AuthorityTermination_AfterRunTerminal_ShouldBeIgnored()
    {
        var terminal = _agent.Apply(
            _agent.Apply(
                NewInFlightState(DateTimeOffset.UtcNow.AddSeconds(-10)),
                NewAuthorityTermination()),
            new StudioMemberBindingTerminalAcknowledged
            {
                BindingRunId = "bind-1",
                Status = StudioMemberBindingRunStatus.Failed,
                AcknowledgedAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            });
        var eventSourcing = new RecordingEventSourcing(terminal);
        var publisher = new RecordingEventPublisher();
        var scheduler = new RecordingRuntimeCallbackScheduler();
        var agent = NewHandlerAgent(terminal, publisher, scheduler, eventSourcing: eventSourcing);

        await agent.HandleMemberBindingAuthorityTerminated(NewAuthorityTermination());

        StudioMemberBindingRunStateSetter.Get(agent).ToByteArray().Should().Equal(terminal.ToByteArray());
        eventSourcing.CommittedEvents.Should().BeEmpty();
        publisher.SentMessages.Should().BeEmpty();
        scheduler.Timeouts.Should().BeEmpty();
    }

    [Fact]
    public void PlatformExecutionStarted_ShouldRecordTypedTimestampAndLegacyFence()
    {
        var accepted = NewPlatformPendingState();
        var startedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow.AddSeconds(2));

        var started = _agent.Apply(accepted, new StudioMemberPlatformBindingStageStarted
        {
            BindingRunId = "bind-1",
            PlatformBindingCommandId = "platform-1",
            ProtocolVersion = StudioMemberConventions.PlatformBindingProtocolVersion,
            ExecutionAttempt = 1,
            ExecutionStage = StudioMemberPlatformBindingExecutionStage.CommandInFlight,
            StageStartedAtUtc = startedAt,
        });

        started.PlatformExecutionStage.Should().Be(StudioMemberPlatformBindingExecutionStage.CommandInFlight);
        started.PlatformExecutionAttempt.Should().Be(1);
        started.PlatformExecutionStageStartedAtUtc.Should().Be(startedAt);
        started.PlatformExecutionInFlight.Should().BeTrue();
        started.PlatformExecutionStartedAtUtc!.ToDateTimeOffset().Year.Should().Be(9999);
        started.UpdatedAtUtc.Should().Be(startedAt);
    }

    [Fact]
    public void DuplicatePlatformBindingAccepted_AfterExecutionStarted_ShouldNotClearInFlight()
    {
        var startedAt = DateTimeOffset.UtcNow.AddSeconds(-10);
        var inFlight = NewInFlightState(startedAt);

        var afterDuplicateAccepted = _agent.Apply(inFlight, new StudioMemberPlatformBindingExecutionStartAccepted
        {
            BindingRunId = "bind-1",
            PlatformBindingCommandId = "platform-1",
            AcceptedAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            ProtocolVersion = StudioMemberConventions.PlatformBindingProtocolVersion,
            ExecutionAttempt = 0,
        });

        afterDuplicateAccepted.PlatformExecutionStage.Should()
            .Be(StudioMemberPlatformBindingExecutionStage.CommandInFlight);
        afterDuplicateAccepted.PlatformExecutionStageStartedAtUtc.Should()
            .Be(Timestamp.FromDateTimeOffset(startedAt));
        afterDuplicateAccepted.PlatformExecutionStartedAtUtc!.ToDateTimeOffset().Year.Should().Be(9999);
        afterDuplicateAccepted.PlatformBindingCommandId.Should().Be("platform-1");
    }

    [Fact]
    public async Task HandlePlatformBindingAccepted_AfterExecutionStarted_ShouldNotRescheduleExecute()
    {
        var inFlight = NewInFlightState(DateTimeOffset.UtcNow.AddSeconds(-10));
        var publisher = new RecordingEventPublisher();
        var scheduler = new RecordingRuntimeCallbackScheduler();
        var agent = NewHandlerAgent(inFlight, publisher, scheduler);

        await agent.HandlePlatformBindingAccepted(new StudioMemberPlatformBindingExecutionStartAccepted
        {
            BindingRunId = "bind-1",
            PlatformBindingCommandId = "platform-1",
            AcceptedAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            ProtocolVersion = StudioMemberConventions.PlatformBindingProtocolVersion,
            ExecutionAttempt = 0,
        });

        scheduler.Timeouts.Should().BeEmpty();
        publisher.SentMessages.Should().BeEmpty();
    }

    [Fact]
    public async Task HandlePlatformBindingAccepted_WhenExecuteScheduleFailsAfterCommit_ShouldRecoverOnRedelivery()
    {
        var state = NewAcceptancePendingState();
        var accepted = new StudioMemberPlatformBindingExecutionStartAccepted
        {
            BindingRunId = "bind-1",
            PlatformBindingCommandId = "platform-1",
            AcceptedAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            ProtocolVersion = StudioMemberConventions.PlatformBindingProtocolVersion,
            ExecutionAttempt = 0,
        };
        var eventSourcing = new RecordingEventSourcing(state);
        var publisher = new RecordingEventPublisher();
        var scheduler = new RecordingRuntimeCallbackScheduler
        {
            ScheduleException = new InvalidOperationException("simulated execute schedule failure"),
        };
        var agent = NewHandlerAgent(state, publisher, scheduler, eventSourcing: eventSourcing);

        Func<Task> firstDelivery = () => agent.HandlePlatformBindingAccepted(accepted);
        var failure = await firstDelivery.Should().ThrowAsync<Exception>();
        failure.Which.Should().BeAssignableTo<IRuntimeEnvelopeRetryableException>();
        eventSourcing.CommittedEvents.OfType<StudioMemberPlatformBindingExecutionStartAccepted>()
            .Should().ContainSingle();
        publisher.SentMessages.Should().BeEmpty();

        scheduler.ScheduleException = null;
        await agent.HandleEventAsync(RuntimeRetryEnvelope(accepted));

        eventSourcing.CommittedEvents.OfType<StudioMemberPlatformBindingExecutionStartAccepted>()
            .Should().ContainSingle();
        scheduler.Timeouts.Should().ContainSingle(request =>
            request.CallbackId == "studio-member-binding-execute:v1:a0:bind-1:platform-1");
        publisher.SentMessages.Should().ContainSingle().Which.Event.Should()
            .BeOfType<StudioMemberBindingPlatformPendingEvent>();
    }

    [Fact]
    public async Task HandlePlatformBindingWatchdogFired_WhenInFlightIsFresh_ShouldNotReexecute()
    {
        var state = NewInFlightState(DateTimeOffset.UtcNow.AddSeconds(-10));
        var publisher = new RecordingEventPublisher();
        var scheduler = new RecordingRuntimeCallbackScheduler();
        var agent = NewHandlerAgent(state, publisher, scheduler);

        await agent.HandlePlatformBindingWatchdogFired(new StudioMemberPlatformBindingExecutionWatchdogFired
        {
            BindingRunId = "bind-1",
            PlatformBindingCommandId = "platform-1",
            ProtocolVersion = StudioMemberConventions.PlatformBindingProtocolVersion,
            ExpectedExecutionAttempt = 1,
        });

        publisher.SentMessages.Should().BeEmpty();
        var callback = scheduler.Timeouts.Should().ContainSingle().Subject;
        callback.CallbackId.Should().Be("studio-member-binding-watchdog:v1:a1:bind-1:platform-1");
    }

    [Fact]
    public async Task HandlePlatformBindingWatchdogFired_WhenCommandIsStale_ShouldFailClosed()
    {
        var state = NewInFlightState(DateTimeOffset.UtcNow.AddMinutes(-3));
        var publisher = new RecordingEventPublisher();
        var eventSourcing = new RecordingEventSourcing(state);
        var agent = NewHandlerAgent(state, publisher, eventSourcing: eventSourcing);

        await agent.HandlePlatformBindingWatchdogFired(new StudioMemberPlatformBindingExecutionWatchdogFired
        {
            BindingRunId = "bind-1",
            PlatformBindingCommandId = "platform-1",
            ProtocolVersion = StudioMemberConventions.PlatformBindingProtocolVersion,
            ExpectedExecutionAttempt = 1,
        });

        var failed = eventSourcing.CommittedEvents.OfType<StudioMemberPlatformBindingExecutionFailed>()
            .Should().ContainSingle().Subject;
        failed.Failure.Code.Should().Be("STUDIO_MEMBER_PLATFORM_BINDING_CHECKPOINT_UNAVAILABLE");
        failed.ExecutionAttempt.Should().Be(1);
        failed.ExecutionStage.Should().Be(StudioMemberPlatformBindingExecutionStage.CommandInFlight);
    }

    [Fact]
    public async Task HandlePlatformBindingExecuteRequested_ShouldOnlyStartExecutionAndWaitForInboxContinuation()
    {
        var state = NewPlatformPendingState();
        var publisher = new RecordingEventPublisher();
        var scheduler = new RecordingRuntimeCallbackScheduler();
        var platformPort = new RecordingPlatformBindingCommandPort();
        var eventSourcing = new RecordingEventSourcing(state);
        var agent = NewHandlerAgent(state, publisher, scheduler, platformPort, eventSourcing);

        await agent.HandlePlatformBindingExecuteRequested(new StudioMemberPlatformBindingStageExecuteRequested
        {
            BindingRunId = "bind-1",
            PlatformBindingCommandId = "platform-1",
            ProtocolVersion = StudioMemberConventions.PlatformBindingProtocolVersion,
            ExpectedExecutionAttempt = 0,
        });

        platformPort.ExecuteRequests.Should().ContainSingle();
        platformPort.ExecuteRequests[0].ExecutionAttempt.Should().Be(1);
        platformPort.ExecuteRequests[0].ExecutionStage.Should()
            .Be(StudioMemberPlatformBindingExecutionStage.CommandInFlight);
        platformPort.ExecuteRequests[0].RecoverySnapshot.Should().BeNull();
        var started = eventSourcing.CommittedEvents.Should().ContainSingle()
            .Which.Should().BeOfType<StudioMemberPlatformBindingStageStarted>().Subject;
        started.StageStartedAtUtc.Should().NotBeNull();
        var committedState = StudioMemberBindingRunStateSetter.Get(agent);
        committedState.PlatformExecutionStartedAtUtc!.ToDateTimeOffset().Year.Should().Be(9999);
        publisher.SentMessages.Should().BeEmpty();
        scheduler.Timeouts.Should().ContainSingle(request =>
            request.CallbackId == "studio-member-binding-watchdog:v1:a1:bind-1:platform-1");
    }

    [Fact]
    public async Task HandlePlatformBindingExecuteRequested_WhenWatchdogScheduleFailsAfterExecute_ShouldNotRepeatSideEffectOnRedelivery()
    {
        var state = NewPlatformPendingState();
        var request = new StudioMemberPlatformBindingStageExecuteRequested
        {
            BindingRunId = "bind-1",
            PlatformBindingCommandId = "platform-1",
            ProtocolVersion = StudioMemberConventions.PlatformBindingProtocolVersion,
            ExpectedExecutionAttempt = 0,
        };
        var eventSourcing = new RecordingEventSourcing(state);
        var publisher = new RecordingEventPublisher();
        var scheduler = new RecordingRuntimeCallbackScheduler
        {
            ScheduleException = new InvalidOperationException("simulated watchdog schedule failure"),
        };
        var platformPort = new RecordingPlatformBindingCommandPort();
        var agent = NewHandlerAgent(state, publisher, scheduler, platformPort, eventSourcing);

        Func<Task> firstDelivery = () => agent.HandlePlatformBindingExecuteRequested(request);
        var failure = await firstDelivery.Should().ThrowAsync<Exception>();
        failure.Which.Should().BeAssignableTo<IRuntimeEnvelopeRetryableException>();
        platformPort.ExecuteRequests.Should().ContainSingle();
        eventSourcing.CommittedEvents.OfType<StudioMemberPlatformBindingStageStarted>()
            .Should().ContainSingle();

        scheduler.ScheduleException = null;
        await agent.HandleEventAsync(RuntimeRetryEnvelope(request));

        platformPort.ExecuteRequests.Should().ContainSingle();
        eventSourcing.CommittedEvents.OfType<StudioMemberPlatformBindingStageStarted>()
            .Should().ContainSingle();
        scheduler.Timeouts.Should().ContainSingle(timeout =>
            timeout.CallbackId == "studio-member-binding-watchdog:v1:a1:bind-1:platform-1");
    }

    [Fact]
    public async Task HandlePlatformBindingExecuteRequested_WhenExecuteThrowsAfterAmbiguousSideEffect_ShouldPreserveFailureAndArmWatchdog()
    {
        var state = NewPlatformPendingState();
        var scheduler = new RecordingRuntimeCallbackScheduler();
        var platformPort = new RecordingPlatformBindingCommandPort
        {
            ExecuteException = new InvalidOperationException("ambiguous execute failure"),
        };
        var agent = NewHandlerAgent(
            state,
            new RecordingEventPublisher(),
            scheduler,
            platformPort,
            new RecordingEventSourcing(state));
        var request = new StudioMemberPlatformBindingStageExecuteRequested
        {
            BindingRunId = "bind-1",
            PlatformBindingCommandId = "platform-1",
            ProtocolVersion = StudioMemberConventions.PlatformBindingProtocolVersion,
            ExpectedExecutionAttempt = 0,
        };

        Func<Task> delivery = () => agent.HandlePlatformBindingExecuteRequested(request);
        await delivery.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("ambiguous execute failure");

        platformPort.ExecuteRequests.Should().ContainSingle();
        scheduler.Timeouts.Should().ContainSingle(timeout =>
            timeout.CallbackId == "studio-member-binding-watchdog:v1:a1:bind-1:platform-1");
    }

    [Fact]
    public async Task ExecuteRedelivery_AfterAttemptCommittedTerminalFailure_ShouldRecoverNotificationAtNextAttemptFence()
    {
        var inFlight = NewInFlightState(DateTimeOffset.UtcNow.AddSeconds(-1));
        var pendingNotification = _agent.Apply(inFlight, new StudioMemberPlatformBindingExecutionFailed
        {
            BindingRunId = "bind-1",
            PlatformBindingCommandId = "platform-1",
            ProtocolVersion = StudioMemberConventions.PlatformBindingProtocolVersion,
            ExecutionAttempt = 1,
            ExecutionStage = StudioMemberPlatformBindingExecutionStage.CommandInFlight,
            Failure = new StudioMemberBindingFailure
            {
                Code = "AMBIGUOUS_EXECUTION_FAILED",
                Message = "platform execution outcome was ambiguous.",
                FailedAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            },
        });
        var publisher = new RecordingEventPublisher();
        var scheduler = new RecordingRuntimeCallbackScheduler();
        var eventSourcing = new RecordingEventSourcing(pendingNotification);
        var platformPort = new RecordingPlatformBindingCommandPort();
        var agent = NewHandlerAgent(
            pendingNotification,
            publisher,
            scheduler,
            platformPort,
            eventSourcing);

        await agent.HandleEventAsync(RuntimeRetryEnvelope(
            new StudioMemberPlatformBindingStageExecuteRequested
            {
                BindingRunId = "bind-1",
                PlatformBindingCommandId = "platform-1",
                ProtocolVersion = StudioMemberConventions.PlatformBindingProtocolVersion,
                ExpectedExecutionAttempt = 0,
            }));

        platformPort.ExecuteRequests.Should().BeEmpty();
        eventSourcing.CommittedEvents.OfType<StudioMemberBindingTerminalNotificationAttemptStarted>()
            .Should().ContainSingle().Which.NotificationAttempt.Should().Be(1);
        scheduler.Timeouts.Should().ContainSingle(timeout =>
            timeout.CallbackId == "studio-member-binding-terminal-notification-watchdog:a1:bind-1");
        publisher.SentMessages.Should().ContainSingle().Which.Event.Should()
            .BeOfType<StudioMemberBindingFailedEvent>();
    }

    [Fact]
    public async Task HandlePlatformBindingReadinessObservationTimedOut_ShouldRemainPendingAndScheduleWatchdog()
    {
        var state = NewReadinessInFlightState(DateTimeOffset.UtcNow.AddSeconds(-10));
        var timedOutAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow.AddSeconds(1));
        var afterTimeout = _agent.Apply(state, new StudioMemberPlatformBindingReadinessObservationTimedOut
        {
            BindingRunId = "bind-1",
            PlatformBindingCommandId = "platform-1",
            ReadinessStatus = StudioMemberPlatformBindingReadinessStatus.ServingSetMissing,
            TimedOutAtUtc = timedOutAt,
            ProtocolVersion = StudioMemberConventions.PlatformBindingProtocolVersion,
            ExecutionAttempt = 2,
        });
        var publisher = new RecordingEventPublisher();
        var scheduler = new RecordingRuntimeCallbackScheduler();
        var agent = NewHandlerAgent(state, publisher, scheduler);

        await agent.HandlePlatformBindingReadinessObservationTimedOut(
            new StudioMemberPlatformBindingReadinessObservationTimedOut
        {
            BindingRunId = "bind-1",
            PlatformBindingCommandId = "platform-1",
            ReadinessStatus = StudioMemberPlatformBindingReadinessStatus.ServingSetMissing,
            TimedOutAtUtc = timedOutAt,
            ProtocolVersion = StudioMemberConventions.PlatformBindingProtocolVersion,
            ExecutionAttempt = 2,
        });

        afterTimeout.Status.Should().Be(StudioMemberBindingRunStatus.PlatformBindingPending);
        afterTimeout.PlatformExecutionStage.Should().Be(StudioMemberPlatformBindingExecutionStage.ReadinessPending);
        afterTimeout.PlatformExecutionStageStartedAtUtc.Should().BeNull();
        afterTimeout.PlatformExecutionInFlight.Should().BeTrue();
        afterTimeout.PlatformExecutionStartedAtUtc!.ToDateTimeOffset().Year.Should().Be(9999);
        afterTimeout.PlatformBindingRecoverySnapshot.Should().BeEquivalentTo(NewRecoverySnapshot());
        afterTimeout.LastPlatformReadinessStatus.Should()
            .Be(StudioMemberPlatformBindingReadinessStatus.ServingSetMissing);
        afterTimeout.UpdatedAtUtc.Should().Be(timedOutAt);
        publisher.SentMessages.Should().BeEmpty();
        scheduler.Timeouts.Should().ContainSingle(request =>
            request.CallbackId == "studio-member-binding-watchdog:v1:a2:bind-1:platform-1");
    }

    [Fact]
    public async Task HandlePlatformBindingReadinessObservationTimedOut_WhenWatchdogScheduleFailsAfterCommit_ShouldRecoverOnRedelivery()
    {
        var state = NewReadinessInFlightState(DateTimeOffset.UtcNow.AddSeconds(-10));
        var timedOut = new StudioMemberPlatformBindingReadinessObservationTimedOut
        {
            BindingRunId = "bind-1",
            PlatformBindingCommandId = "platform-1",
            ReadinessStatus = StudioMemberPlatformBindingReadinessStatus.ServingSetMissing,
            TimedOutAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            ProtocolVersion = StudioMemberConventions.PlatformBindingProtocolVersion,
            ExecutionAttempt = 2,
        };
        var eventSourcing = new RecordingEventSourcing(state);
        var scheduler = new RecordingRuntimeCallbackScheduler
        {
            ScheduleException = new InvalidOperationException("simulated readiness watchdog schedule failure"),
        };
        var agent = NewHandlerAgent(
            state,
            new RecordingEventPublisher(),
            scheduler,
            eventSourcing: eventSourcing);

        Func<Task> firstDelivery = () =>
            agent.HandlePlatformBindingReadinessObservationTimedOut(timedOut);
        var failure = await firstDelivery.Should().ThrowAsync<Exception>();
        failure.Which.Should().BeAssignableTo<IRuntimeEnvelopeRetryableException>();
        eventSourcing.CommittedEvents
            .OfType<StudioMemberPlatformBindingReadinessObservationTimedOut>()
            .Should().ContainSingle();

        scheduler.ScheduleException = null;
        await agent.HandleEventAsync(RuntimeRetryEnvelope(timedOut));

        eventSourcing.CommittedEvents
            .OfType<StudioMemberPlatformBindingReadinessObservationTimedOut>()
            .Should().ContainSingle();
        scheduler.Timeouts.Should().ContainSingle(timeout =>
            timeout.CallbackId == "studio-member-binding-watchdog:v1:a2:bind-1:platform-1");
    }

    [Fact]
    public async Task HandlePlatformBindingReadinessObservationTimedOut_AfterDeadline_ShouldFailWithStableCode()
    {
        var now = DateTimeOffset.UtcNow;
        var state = NewReadinessInFlightState(now.AddSeconds(-10));
        state.PlatformReadinessDeadlineAtUtc = Timestamp.FromDateTimeOffset(now.AddSeconds(-1));
        var publisher = new RecordingEventPublisher();
        var scheduler = new RecordingRuntimeCallbackScheduler();
        var eventSourcing = new RecordingEventSourcing(state);
        var agent = NewHandlerAgent(state, publisher, scheduler, eventSourcing: eventSourcing);

        await agent.HandlePlatformBindingReadinessObservationTimedOut(
            new StudioMemberPlatformBindingReadinessObservationTimedOut
            {
                BindingRunId = "bind-1",
                PlatformBindingCommandId = "platform-1",
                ReadinessStatus = StudioMemberPlatformBindingReadinessStatus.ServingSetMissing,
                TimedOutAtUtc = Timestamp.FromDateTimeOffset(now),
                ProtocolVersion = StudioMemberConventions.PlatformBindingProtocolVersion,
                ExecutionAttempt = 2,
            });

        var committed = StudioMemberBindingRunStateSetter.Get(agent);
        committed.Status.Should().Be(StudioMemberBindingRunStatus.MemberNotificationPending);
        committed.Failure.Code.Should().Be("STUDIO_MEMBER_PLATFORM_BINDING_READINESS_TIMEOUT");
        committed.PlatformExecutionAttempt.Should().Be(2);
        committed.LastPlatformReadinessStatus.Should()
            .Be(StudioMemberPlatformBindingReadinessStatus.ServingSetMissing);
        eventSourcing.CommittedEvents.OfType<StudioMemberPlatformBindingExecutionFailed>()
            .Should().ContainSingle()
            .Which.ExecutionAttempt.Should().Be(2);
        scheduler.Timeouts.Should().ContainSingle(request =>
            request.CallbackId == "studio-member-binding-terminal-notification-watchdog:a1:bind-1");
        scheduler.Timeouts.Should().NotContain(request =>
            request.CallbackId.StartsWith("studio-member-binding-watchdog:", StringComparison.Ordinal));
        publisher.SentMessages.Should().ContainSingle().Which.Event.Should()
            .BeOfType<StudioMemberBindingFailedEvent>();
    }

    [Fact]
    public async Task HandlePlatformBindingExecutionSucceeded_AfterDeadline_ShouldFailInsteadOfWinningRace()
    {
        var now = DateTimeOffset.UtcNow;
        var state = NewReadinessInFlightState(now.AddSeconds(-10));
        state.PlatformReadinessDeadlineAtUtc = Timestamp.FromDateTimeOffset(now.AddSeconds(-1));
        var publisher = new RecordingEventPublisher();
        var scheduler = new RecordingRuntimeCallbackScheduler();
        var eventSourcing = new RecordingEventSourcing(state);
        var agent = NewHandlerAgent(state, publisher, scheduler, eventSourcing: eventSourcing);
        var lateSuccess = NewExecutionSucceeded(2);
        lateSuccess.CompletedAtUtc = Timestamp.FromDateTimeOffset(now.AddMinutes(-1));

        await agent.HandlePlatformBindingExecutionSucceeded(lateSuccess);

        var committed = StudioMemberBindingRunStateSetter.Get(agent);
        committed.Status.Should().Be(StudioMemberBindingRunStatus.MemberNotificationPending);
        committed.PlatformResult.Should().BeNull();
        committed.Failure.Code.Should().Be("STUDIO_MEMBER_PLATFORM_BINDING_READINESS_TIMEOUT");
        committed.PlatformExecutionStage.Should()
            .Be(StudioMemberPlatformBindingExecutionStage.ReadinessInFlight);
        committed.PlatformExecutionAttempt.Should().Be(2);
        eventSourcing.CommittedEvents.Should().NotContain(lateSuccess);
        eventSourcing.CommittedEvents.OfType<StudioMemberPlatformBindingExecutionFailed>()
            .Should().ContainSingle()
            .Which.ExecutionAttempt.Should().Be(2);
        scheduler.Timeouts.Should().ContainSingle(request =>
            request.CallbackId == "studio-member-binding-terminal-notification-watchdog:a1:bind-1");
        publisher.SentMessages.Should().ContainSingle().Which.Event.Should()
            .BeOfType<StudioMemberBindingFailedEvent>();
    }

    [Fact]
    public async Task LateSuccessRedelivery_WhenTerminalWatchdogScheduleFailed_ShouldRecoverGeneratedTimeoutNotification()
    {
        var now = DateTimeOffset.UtcNow;
        var state = NewReadinessInFlightState(now.AddSeconds(-10));
        state.PlatformReadinessDeadlineAtUtc = Timestamp.FromDateTimeOffset(now.AddSeconds(-1));
        var lateSuccess = NewExecutionSucceeded(2);
        var eventSourcing = new RecordingEventSourcing(state);
        var publisher = new RecordingEventPublisher();
        var scheduler = new RecordingRuntimeCallbackScheduler
        {
            ScheduleException = new InvalidOperationException("simulated terminal watchdog schedule failure"),
        };
        var agent = NewHandlerAgent(state, publisher, scheduler, eventSourcing: eventSourcing);

        Func<Task> firstDelivery = () =>
            agent.HandlePlatformBindingExecutionSucceeded(lateSuccess);
        var failure = await firstDelivery.Should().ThrowAsync<Exception>();
        failure.Which.Should().BeAssignableTo<IRuntimeEnvelopeRetryableException>();
        eventSourcing.CommittedEvents.OfType<StudioMemberPlatformBindingExecutionFailed>()
            .Should().ContainSingle().Which.Failure.Code.Should()
            .Be("STUDIO_MEMBER_PLATFORM_BINDING_READINESS_TIMEOUT");
        publisher.SentMessages.Should().BeEmpty();

        scheduler.ScheduleException = null;
        await agent.HandleEventAsync(RuntimeRetryEnvelope(lateSuccess));

        eventSourcing.CommittedEvents.OfType<StudioMemberPlatformBindingExecutionFailed>()
            .Should().ContainSingle();
        eventSourcing.CommittedEvents
            .OfType<StudioMemberBindingTerminalNotificationAttemptStarted>()
            .Should().ContainSingle().Which.NotificationAttempt.Should().Be(1);
        scheduler.Timeouts.Should().ContainSingle(timeout =>
            timeout.CallbackId == "studio-member-binding-terminal-notification-watchdog:a1:bind-1");
        publisher.SentMessages.Should().ContainSingle().Which.Event.Should()
            .BeOfType<StudioMemberBindingFailedEvent>();
    }

    [Fact]
    public async Task ActivateAsync_WhenReadinessDeadlineExpiredAndContinuationWasLost_ShouldFailClosed()
    {
        var state = NewReadinessInFlightState(DateTimeOffset.UtcNow.AddMinutes(-3));
        state.PlatformReadinessDeadlineAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow.AddSeconds(-1));
        var publisher = new RecordingEventPublisher();
        var scheduler = new RecordingRuntimeCallbackScheduler();
        var eventSourcing = new RecordingEventSourcing(state);
        var agent = NewHandlerAgent(state, publisher, scheduler, eventSourcing: eventSourcing);

        await agent.ActivateAsync();

        var watchdog = publisher.SentMessages.Should().ContainSingle().Subject.Event
            .Should().BeOfType<StudioMemberPlatformBindingExecutionWatchdogFired>().Subject;
        watchdog.ExpectedExecutionAttempt.Should().Be(2);
        await agent.HandlePlatformBindingWatchdogFired(watchdog);

        var committed = StudioMemberBindingRunStateSetter.Get(agent);
        committed.Status.Should().Be(StudioMemberBindingRunStatus.MemberNotificationPending);
        committed.Failure.Code.Should().Be("STUDIO_MEMBER_PLATFORM_BINDING_READINESS_TIMEOUT");
        eventSourcing.CommittedEvents.OfType<StudioMemberPlatformBindingExecutionFailed>()
            .Should().ContainSingle()
            .Which.ExecutionAttempt.Should().Be(2);
        scheduler.Timeouts.Should().ContainSingle(request =>
            request.CallbackId == "studio-member-binding-terminal-notification-watchdog:a1:bind-1");
        publisher.SentMessages.Should().HaveCount(2);
        publisher.SentMessages.Last().Event.Should().BeOfType<StudioMemberBindingFailedEvent>();
    }

    [Fact]
    public async Task ActivateAsync_WhenReadinessStateHasNoCommittedTimes_ShouldFailClosedFromUnixEpoch()
    {
        var state = NewReadinessInFlightState(DateTimeOffset.UtcNow);
        state.PlatformReadinessDeadlineAtUtc = null;
        state.UpdatedAtUtc = null;
        state.PlatformExecutionStageStartedAtUtc = null;
        state.AcceptedAtUtc = null;
        var publisher = new RecordingEventPublisher();
        var scheduler = new RecordingRuntimeCallbackScheduler();
        var platformPort = new RecordingPlatformBindingCommandPort();
        var eventSourcing = new RecordingEventSourcing(state);
        var agent = NewHandlerAgent(
            state,
            publisher,
            scheduler,
            platformPort,
            eventSourcing);

        await agent.ActivateAsync();

        var watchdog = publisher.SentMessages.Should().ContainSingle().Subject.Event
            .Should().BeOfType<StudioMemberPlatformBindingExecutionWatchdogFired>().Subject;
        await agent.HandlePlatformBindingWatchdogFired(watchdog);

        var committed = StudioMemberBindingRunStateSetter.Get(agent);
        committed.Status.Should().Be(StudioMemberBindingRunStatus.MemberNotificationPending);
        committed.Failure.Code.Should().Be("STUDIO_MEMBER_PLATFORM_BINDING_READINESS_TIMEOUT");
        platformPort.ExecuteRequests.Should().BeEmpty();
        eventSourcing.CommittedEvents.OfType<StudioMemberPlatformBindingExecutionFailed>()
            .Should().ContainSingle();
        scheduler.Timeouts.Should().ContainSingle(request =>
            request.CallbackId == "studio-member-binding-terminal-notification-watchdog:a1:bind-1");
    }

    [Fact]
    public async Task HandlePlatformBindingWatchdogFired_AfterReadinessTimeout_ShouldRetryExecution()
    {
        var state = NewReadinessInFlightState(DateTimeOffset.UtcNow.AddSeconds(-10));
        var afterTimeout = _agent.Apply(state, new StudioMemberPlatformBindingReadinessObservationTimedOut
        {
            BindingRunId = "bind-1",
            PlatformBindingCommandId = "platform-1",
            ReadinessStatus = StudioMemberPlatformBindingReadinessStatus.ServingSetMissing,
            TimedOutAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            ProtocolVersion = StudioMemberConventions.PlatformBindingProtocolVersion,
            ExecutionAttempt = 2,
        });
        var publisher = new RecordingEventPublisher();
        var agent = NewHandlerAgent(afterTimeout, publisher);

        await agent.HandlePlatformBindingWatchdogFired(new StudioMemberPlatformBindingExecutionWatchdogFired
        {
            BindingRunId = "bind-1",
            PlatformBindingCommandId = "platform-1",
            ProtocolVersion = StudioMemberConventions.PlatformBindingProtocolVersion,
            ExpectedExecutionAttempt = 2,
        });

        var retry = publisher.SentMessages.Should().ContainSingle().Subject.Event
            .Should().BeOfType<StudioMemberPlatformBindingStageExecuteRequested>().Subject;
        retry.BindingRunId.Should().Be("bind-1");
        retry.PlatformBindingCommandId.Should().Be("platform-1");
        retry.ProtocolVersion.Should().Be(StudioMemberConventions.PlatformBindingProtocolVersion);
        retry.ExpectedExecutionAttempt.Should().Be(2);
    }

    [Fact]
    public async Task TerminalActivationFailure_ShouldReachFailedStateAndFenceWatchdogRetry()
    {
        var state = NewReadinessInFlightState(DateTimeOffset.UtcNow.AddSeconds(-10));
        var publisher = new RecordingEventPublisher();
        var scheduler = new RecordingRuntimeCallbackScheduler();
        var platformPort = new RecordingPlatformBindingCommandPort();
        var eventSourcing = new RecordingEventSourcing(state);
        var agent = NewHandlerAgent(state, publisher, scheduler, platformPort, eventSourcing);
        var failure = new StudioMemberPlatformBindingExecutionFailed
        {
            BindingRunId = "bind-1",
            PlatformBindingCommandId = "platform-1",
            ProtocolVersion = StudioMemberConventions.PlatformBindingProtocolVersion,
            ExecutionAttempt = 2,
            ExecutionStage = StudioMemberPlatformBindingExecutionStage.ReadinessInFlight,
            Failure = new StudioMemberBindingFailure
            {
                Code = "STUDIO_MEMBER_PLATFORM_BINDING_ACTIVATION_PREPARED_ARTIFACT_MISSING",
                Message = "platform service activation failed because its prepared artifact was unavailable.",
                FailedAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            },
        };

        await agent.HandlePlatformBindingExecutionFailed(failure);
        StudioMemberBindingRunStateSetter.Get(agent).Status.Should()
            .Be(StudioMemberBindingRunStatus.MemberNotificationPending);
        await agent.HandlePlatformBindingWatchdogFired(new StudioMemberPlatformBindingExecutionWatchdogFired
        {
            BindingRunId = "bind-1",
            PlatformBindingCommandId = "platform-1",
            ProtocolVersion = StudioMemberConventions.PlatformBindingProtocolVersion,
            ExpectedExecutionAttempt = 2,
        });

        eventSourcing.CommittedEvents.Should().Contain(failure);
        scheduler.Timeouts.Should().ContainSingle(request =>
            request.CallbackId == "studio-member-binding-terminal-notification-watchdog:a1:bind-1");
        platformPort.ExecuteRequests.Should().BeEmpty();
        publisher.SentMessages.Should().ContainSingle().Which.Event.Should()
            .BeOfType<StudioMemberBindingFailedEvent>();

        await agent.HandleMemberBindingTerminalAcknowledged(new StudioMemberBindingTerminalAcknowledged
        {
            BindingRunId = "bind-1",
            Status = StudioMemberBindingRunStatus.Failed,
            AcknowledgedAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
        });

        StudioMemberBindingRunStateSetter.Get(agent).Status.Should().Be(StudioMemberBindingRunStatus.Failed);
    }

    [Fact]
    public async Task HandlePlatformBindingExecuteRequested_AfterReadinessTimeout_ShouldPassRecoverySnapshot()
    {
        var recoverySnapshot = NewRecoverySnapshot();
        var afterTimeout = _agent.Apply(
            NewReadinessInFlightState(DateTimeOffset.UtcNow.AddSeconds(-10)),
            new StudioMemberPlatformBindingReadinessObservationTimedOut
            {
                BindingRunId = "bind-1",
                PlatformBindingCommandId = "platform-1",
                ReadinessStatus = StudioMemberPlatformBindingReadinessStatus.ServingSetMissing,
                TimedOutAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
                ProtocolVersion = StudioMemberConventions.PlatformBindingProtocolVersion,
                ExecutionAttempt = 2,
            });
        var platformPort = new RecordingPlatformBindingCommandPort();
        var agent = NewHandlerAgent(afterTimeout, new RecordingEventPublisher(), platformPort: platformPort);

        await agent.HandlePlatformBindingExecuteRequested(new StudioMemberPlatformBindingStageExecuteRequested
        {
            BindingRunId = "bind-1",
            PlatformBindingCommandId = "platform-1",
            ProtocolVersion = StudioMemberConventions.PlatformBindingProtocolVersion,
            ExpectedExecutionAttempt = 2,
        });

        platformPort.ExecuteRequests.Should().ContainSingle().Which.RecoverySnapshot
            .Should().BeEquivalentTo(recoverySnapshot);
        platformPort.ExecuteRequests[0].ExecutionAttempt.Should().Be(3);
        platformPort.ExecuteRequests[0].ExecutionStage.Should()
            .Be(StudioMemberPlatformBindingExecutionStage.ReadinessInFlight);
    }

    [Fact]
    public async Task ActivateAsync_AfterReadinessTimeout_ShouldRestoreReadinessRecoveryExecution()
    {
        var recoverySnapshot = NewRecoverySnapshot();
        var afterTimeout = _agent.Apply(
            NewReadinessInFlightState(DateTimeOffset.UtcNow.AddSeconds(-10)),
            new StudioMemberPlatformBindingReadinessObservationTimedOut
            {
                BindingRunId = "bind-1",
                PlatformBindingCommandId = "platform-1",
                ReadinessStatus = StudioMemberPlatformBindingReadinessStatus.ServingSetMissing,
                TimedOutAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
                ProtocolVersion = StudioMemberConventions.PlatformBindingProtocolVersion,
                ExecutionAttempt = 2,
            });
        var scheduler = new RecordingRuntimeCallbackScheduler();
        var platformPort = new RecordingPlatformBindingCommandPort();
        var agent = NewHandlerAgent(
            afterTimeout,
            new RecordingEventPublisher(),
            scheduler,
            platformPort);

        await agent.ActivateAsync();

        var execute = scheduler.Timeouts.Should().ContainSingle(request =>
                request.CallbackId == "studio-member-binding-execute:v1:a2:bind-1:platform-1")
            .Subject.TriggerEnvelope.Payload.Unpack<StudioMemberPlatformBindingStageExecuteRequested>();
        execute.ExpectedExecutionAttempt.Should().Be(2);

        await agent.HandlePlatformBindingExecuteRequested(execute);
        platformPort.ExecuteRequests.Should().ContainSingle().Which.RecoverySnapshot
            .Should().BeEquivalentTo(recoverySnapshot);
    }

    [Fact]
    public async Task ActivateAsync_WhenInFlightIsFresh_ShouldOnlyRestoreWatchdog()
    {
        var state = NewInFlightState(DateTimeOffset.UtcNow.AddSeconds(-10));
        var publisher = new RecordingEventPublisher();
        var scheduler = new RecordingRuntimeCallbackScheduler();
        var agent = NewHandlerAgent(state, publisher, scheduler);

        await agent.ActivateAsync();

        publisher.SentMessages.Should().BeEmpty();
        var callback = scheduler.Timeouts.Should().ContainSingle().Subject;
        callback.CallbackId.Should().Be("studio-member-binding-watchdog:v1:a1:bind-1:platform-1");
        callback.TriggerEnvelope.Payload
            .Unpack<StudioMemberPlatformBindingExecutionWatchdogFired>()
            .PlatformBindingCommandId.Should().Be("platform-1");
    }

    [Fact]
    public async Task ActivationWatchdog_FromPreviousReadinessAttempt_ShouldBeIgnoredAfterRetryStarts()
    {
        var state = NewReadinessInFlightState(DateTimeOffset.UtcNow.AddSeconds(-10));
        var publisher = new RecordingEventPublisher();
        var scheduler = new RecordingRuntimeCallbackScheduler();
        var platformPort = new RecordingPlatformBindingCommandPort();
        var eventSourcing = new RecordingEventSourcing(state);
        var agent = NewHandlerAgent(state, publisher, scheduler, platformPort, eventSourcing);

        await agent.ActivateAsync();

        var activationWatchdog = scheduler.Timeouts.Should().ContainSingle().Subject
            .TriggerEnvelope.Payload.Unpack<StudioMemberPlatformBindingExecutionWatchdogFired>();
        activationWatchdog.ExpectedExecutionAttempt.Should().Be(2);

        var elapsed = StudioMemberBindingRunStateSetter.Get(agent);
        elapsed.PlatformExecutionStageStartedAtUtc = Timestamp.FromDateTimeOffset(
            DateTimeOffset.UtcNow.AddMinutes(-3));
        StudioMemberBindingRunStateSetter.Set(agent, elapsed);
        await agent.HandlePlatformBindingExecuteRequested(
            new StudioMemberPlatformBindingStageExecuteRequested
            {
                BindingRunId = "bind-1",
                PlatformBindingCommandId = "platform-1",
                ProtocolVersion = StudioMemberConventions.PlatformBindingProtocolVersion,
                ExpectedExecutionAttempt = 2,
            });

        StudioMemberBindingRunStateSetter.Get(agent).PlatformExecutionAttempt.Should().Be(3);
        platformPort.ExecuteRequests.Should().ContainSingle().Which.ExecutionAttempt.Should().Be(3);
        scheduler.Timeouts.Should().HaveCount(2);
        scheduler.Timeouts.Last().CallbackId.Should()
            .Be("studio-member-binding-watchdog:v1:a3:bind-1:platform-1");
        var committedCount = eventSourcing.CommittedEvents.Count;

        await agent.HandlePlatformBindingWatchdogFired(activationWatchdog);

        StudioMemberBindingRunStateSetter.Get(agent).PlatformExecutionAttempt.Should().Be(3);
        eventSourcing.CommittedEvents.Should().HaveCount(committedCount);
        platformPort.ExecuteRequests.Should().ContainSingle();
        scheduler.Timeouts.Should().HaveCount(2);
        publisher.SentMessages.Should().BeEmpty();
    }

    [Fact]
    public async Task ActivateAsync_WhenCommandInFlightIsStale_ShouldFailClosed()
    {
        var state = NewInFlightState(DateTimeOffset.UtcNow.AddMinutes(-3));
        var publisher = new RecordingEventPublisher();
        var scheduler = new RecordingRuntimeCallbackScheduler();
        var eventSourcing = new RecordingEventSourcing(state);
        var agent = NewHandlerAgent(state, publisher, scheduler, eventSourcing: eventSourcing);

        await agent.ActivateAsync();

        scheduler.Timeouts.Should().ContainSingle(request =>
            request.CallbackId == "studio-member-binding-terminal-notification-watchdog:a1:bind-1");
        var failed = eventSourcing.CommittedEvents.OfType<StudioMemberPlatformBindingExecutionFailed>()
            .Should().ContainSingle().Subject;
        failed.Failure.Code.Should().Be("STUDIO_MEMBER_PLATFORM_BINDING_CHECKPOINT_UNAVAILABLE");
    }

    [Fact]
    public async Task HandleCommandsCompleted_ShouldCommitCheckpointBeforeSchedulingReadiness()
    {
        var state = NewInFlightState(DateTimeOffset.UtcNow.AddSeconds(-1));
        var completed = NewCommandsCompleted();
        completed.CompletedAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow.AddYears(1));
        completed.ReadinessDeadlineAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow.AddYears(2));
        var publisher = new RecordingEventPublisher();
        var scheduler = new RecordingRuntimeCallbackScheduler();
        var platformPort = new RecordingPlatformBindingCommandPort();
        var agent = NewHandlerAgent(state, publisher, scheduler, platformPort);
        var beforeHandle = DateTimeOffset.UtcNow;

        await agent.HandlePlatformBindingCommandsCompleted(completed);
        var afterHandle = DateTimeOffset.UtcNow;

        var committed = StudioMemberBindingRunStateSetter.Get(agent);
        committed.PlatformExecutionStage.Should().Be(StudioMemberPlatformBindingExecutionStage.ReadinessPending);
        committed.PlatformExecutionAttempt.Should().Be(1);
        committed.PlatformBindingRecoverySnapshot.Should().BeEquivalentTo(completed.RecoverySnapshot);
        committed.PlatformBindingRecoverySnapshot.ActivationAttemptId.Should().Be("platform-1:a1");
        committed.PlatformExecutionInFlight.Should().BeTrue();
        committed.PlatformExecutionStartedAtUtc!.ToDateTimeOffset().Year.Should().Be(9999);
        committed.PlatformExecutionStageStartedAtUtc.Should().BeNull();
        committed.PlatformReadinessDeadlineAtUtc.Should().NotBeNull();
        committed.PlatformReadinessDeadlineAtUtc!.ToDateTimeOffset().Should()
            .BeOnOrAfter(beforeHandle.AddMinutes(6)).And
            .BeOnOrBefore(afterHandle.AddMinutes(6));
        committed.PlatformReadinessDeadlineAtUtc.Should().NotBe(completed.ReadinessDeadlineAtUtc);
        platformPort.ExecuteRequests.Should().BeEmpty();
        var execute = scheduler.Timeouts.Should().ContainSingle(request =>
                request.CallbackId == "studio-member-binding-execute:v1:a1:bind-1:platform-1")
            .Subject.TriggerEnvelope.Payload.Unpack<StudioMemberPlatformBindingStageExecuteRequested>();
        execute.ExpectedExecutionAttempt.Should().Be(1);
        publisher.SentMessages.Should().BeEmpty();
    }

    [Fact]
    public async Task HandleCommandsCompleted_WhenExecuteScheduleFailsAfterCommit_ShouldRecoverOnRedelivery()
    {
        var state = NewInFlightState(DateTimeOffset.UtcNow.AddSeconds(-1));
        var completed = NewCommandsCompleted();
        var eventSourcing = new RecordingEventSourcing(state);
        var scheduler = new RecordingRuntimeCallbackScheduler
        {
            ScheduleException = new InvalidOperationException("simulated readiness execute schedule failure"),
        };
        var agent = NewHandlerAgent(
            state,
            new RecordingEventPublisher(),
            scheduler,
            eventSourcing: eventSourcing);

        Func<Task> firstDelivery = () => agent.HandlePlatformBindingCommandsCompleted(completed);
        var failure = await firstDelivery.Should().ThrowAsync<Exception>();
        failure.Which.Should().BeAssignableTo<IRuntimeEnvelopeRetryableException>();
        eventSourcing.CommittedEvents.OfType<StudioMemberPlatformBindingCommandsCompleted>()
            .Should().ContainSingle();

        scheduler.ScheduleException = null;
        await agent.HandleEventAsync(RuntimeRetryEnvelope(completed));

        eventSourcing.CommittedEvents.OfType<StudioMemberPlatformBindingCommandsCompleted>()
            .Should().ContainSingle();
        scheduler.Timeouts.Should().ContainSingle(timeout =>
            timeout.CallbackId == "studio-member-binding-execute:v1:a1:bind-1:platform-1");
    }

    [Fact]
    public void CommandsCompletedReducer_WhenEventTimesAreMissing_ShouldUseCommittedStageTimestamp()
    {
        var committedStageStartedAt = DateTimeOffset.Parse("2026-08-15T01:02:03Z");
        var state = NewInFlightState(committedStageStartedAt);
        var completed = NewCommandsCompleted();
        completed.CompletedAtUtc = null;
        completed.ReadinessDeadlineAtUtc = null;

        var replayed = _agent.Apply(state, completed);

        replayed.PlatformReadinessDeadlineAtUtc.Should().NotBeNull();
        replayed.PlatformReadinessDeadlineAtUtc!.ToDateTimeOffset().Should()
            .Be(committedStageStartedAt.AddMinutes(6));
    }

    [Fact]
    public void CommandsCompletedReducer_WhenAllTimesAreMissing_ShouldReplayIdenticalFailClosedBytes()
    {
        var state = new StudioMemberBindingRunState
        {
            BindingRunId = "bind-1",
            Status = StudioMemberBindingRunStatus.PlatformBindingPending,
            PlatformBindingCommandId = "platform-1",
            PlatformBindingProtocolVersion = StudioMemberConventions.PlatformBindingProtocolVersion,
            PlatformExecutionAttempt = 1,
            PlatformExecutionStage = StudioMemberPlatformBindingExecutionStage.CommandInFlight,
        };
        var completed = new StudioMemberPlatformBindingCommandsCompleted
        {
            BindingRunId = "bind-1",
            PlatformBindingCommandId = "platform-1",
            RecoverySnapshot = NewRecoverySnapshot(),
            ProtocolVersion = StudioMemberConventions.PlatformBindingProtocolVersion,
            ExecutionAttempt = 1,
        };

        var firstReplay = _agent.Apply(state, completed);
        var secondReplay = _agent.Apply(
            StudioMemberBindingRunState.Parser.ParseFrom(state.ToByteArray()),
            StudioMemberPlatformBindingCommandsCompleted.Parser.ParseFrom(completed.ToByteArray()));

        firstReplay.PlatformReadinessDeadlineAtUtc.Should().NotBeNull();
        firstReplay.PlatformReadinessDeadlineAtUtc!.ToDateTimeOffset().Should()
            .Be(DateTimeOffset.UnixEpoch);
        firstReplay.ToByteArray().Should().Equal(secondReplay.ToByteArray());
    }

    [Fact]
    public async Task MemberTerminalNotificationWatchdog_ShouldRetryWithAttemptFenceUntilAcknowledged()
    {
        var pendingNotification = _agent.Apply(
            NewReadinessInFlightState(DateTimeOffset.UtcNow.AddSeconds(-1)),
            NewExecutionSucceeded(2));
        var publisher = new RecordingEventPublisher();
        var scheduler = new RecordingRuntimeCallbackScheduler();
        var eventSourcing = new RecordingEventSourcing(pendingNotification);
        var agent = NewHandlerAgent(
            pendingNotification,
            publisher,
            scheduler,
            eventSourcing: eventSourcing);

        await agent.ActivateAsync();

        StudioMemberBindingRunStateSetter.Get(agent).MemberNotificationAttempt.Should().Be(1);
        scheduler.Timeouts.Should().ContainSingle(request =>
            request.CallbackId == "studio-member-binding-terminal-notification-watchdog:a1:bind-1");
        publisher.SentMessages.Should().ContainSingle().Which.Event.Should()
            .BeOfType<StudioMemberBindingCompletedEvent>();

        await agent.HandleMemberTerminalNotificationWatchdogFired(
            new StudioMemberBindingTerminalNotificationWatchdogFired
            {
                BindingRunId = "bind-1",
                ExpectedNotificationAttempt = 1,
            });

        StudioMemberBindingRunStateSetter.Get(agent).MemberNotificationAttempt.Should().Be(2);
        scheduler.Timeouts.Should().Contain(request =>
            request.CallbackId == "studio-member-binding-terminal-notification-watchdog:a2:bind-1");
        publisher.SentMessages.Should().HaveCount(2);

        await agent.HandleMemberBindingTerminalAcknowledged(new StudioMemberBindingTerminalAcknowledged
        {
            BindingRunId = "bind-1",
            Status = StudioMemberBindingRunStatus.Succeeded,
            AcknowledgedAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
        });
        await agent.HandleMemberTerminalNotificationWatchdogFired(
            new StudioMemberBindingTerminalNotificationWatchdogFired
            {
                BindingRunId = "bind-1",
                ExpectedNotificationAttempt = 2,
            });

        StudioMemberBindingRunStateSetter.Get(agent).Status.Should().Be(StudioMemberBindingRunStatus.Succeeded);
        StudioMemberBindingRunStateSetter.Get(agent).MemberNotificationAttempt.Should().Be(2);
        publisher.SentMessages.Should().HaveCount(2);
        scheduler.Timeouts.Should().HaveCount(2);
        eventSourcing.CommittedEvents.OfType<StudioMemberBindingTerminalNotificationAttemptStarted>()
            .Select(evt => evt.NotificationAttempt).Should().Equal(1, 2);
    }

    [Fact]
    public async Task MemberTerminalNotification_WhenScheduleSucceedsButCommitFails_ShouldRecoverScheduledAttempt()
    {
        var pendingNotification = _agent.Apply(
            NewReadinessInFlightState(DateTimeOffset.UtcNow.AddSeconds(-1)),
            NewExecutionSucceeded(2));
        var scheduler = new RecordingRuntimeCallbackScheduler();
        var eventSourcing = new RecordingEventSourcing(pendingNotification)
        {
            ConfirmException = new InvalidOperationException("simulated commit failure"),
        };
        var publisher = new RecordingEventPublisher();
        var agent = NewHandlerAgent(
            pendingNotification,
            publisher,
            scheduler,
            eventSourcing: eventSourcing);

        var activate = () => agent.ActivateAsync();
        await activate.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("simulated commit failure");

        StudioMemberBindingRunStateSetter.Get(agent).MemberNotificationAttempt.Should().Be(0);
        eventSourcing.CommittedEvents.Should().BeEmpty();
        publisher.SentMessages.Should().BeEmpty();
        var scheduled = scheduler.Timeouts.Should().ContainSingle().Subject;
        scheduled.CallbackId.Should()
            .Be("studio-member-binding-terminal-notification-watchdog:a1:bind-1");
        scheduled.TriggerEnvelope.Payload.Unpack<StudioMemberBindingTerminalNotificationWatchdogFired>()
            .ExpectedNotificationAttempt.Should().Be(1);

        var recoveredPublisher = new RecordingEventPublisher();
        var recoveredScheduler = new RecordingRuntimeCallbackScheduler();
        var recoveredEventSourcing = new RecordingEventSourcing(pendingNotification);
        var recovered = NewHandlerAgent(
            pendingNotification,
            recoveredPublisher,
            recoveredScheduler,
            eventSourcing: recoveredEventSourcing);

        await recovered.HandleEventAsync(scheduled.TriggerEnvelope);

        StudioMemberBindingRunStateSetter.Get(recovered).MemberNotificationAttempt.Should().Be(1);
        recoveredEventSourcing.CommittedEvents
            .OfType<StudioMemberBindingTerminalNotificationAttemptStarted>()
            .Should().ContainSingle().Which.NotificationAttempt.Should().Be(1);
        recoveredScheduler.Timeouts.Should().ContainSingle(request =>
            request.CallbackId == "studio-member-binding-terminal-notification-watchdog:a1:bind-1");
        recoveredPublisher.SentMessages.Should().ContainSingle().Which.Event.Should()
            .BeOfType<StudioMemberBindingCompletedEvent>();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ExactPlatformTerminalOutcomeRedelivery_WhenNotificationScheduleFails_ShouldRecover(
        bool succeeded)
    {
        var state = NewReadinessInFlightState(DateTimeOffset.UtcNow.AddSeconds(-1));
        IMessage outcome = succeeded
            ? NewExecutionSucceeded(2)
            : new StudioMemberPlatformBindingExecutionFailed
            {
                BindingRunId = "bind-1",
                PlatformBindingCommandId = "platform-1",
                ProtocolVersion = StudioMemberConventions.PlatformBindingProtocolVersion,
                ExecutionAttempt = 2,
                ExecutionStage = StudioMemberPlatformBindingExecutionStage.ReadinessInFlight,
                Failure = new StudioMemberBindingFailure
                {
                    Code = "SCOPE_BINDING_FAILED",
                    Message = "platform binding failed",
                    FailedAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
                },
            };
        var scheduler = new RecordingRuntimeCallbackScheduler
        {
            ScheduleException = new InvalidOperationException("simulated schedule failure"),
        };
        var eventSourcing = new RecordingEventSourcing(state);
        var publisher = new RecordingEventPublisher();
        var agent = NewHandlerAgent(
            state,
            publisher,
            scheduler,
            eventSourcing: eventSourcing);

        async Task DeliverAsync()
        {
            if (outcome is StudioMemberPlatformBindingExecutionSucceeded success)
                await agent.HandlePlatformBindingExecutionSucceeded(success);
            else
                await agent.HandlePlatformBindingExecutionFailed(
                    (StudioMemberPlatformBindingExecutionFailed)outcome);
        }

        Func<Task> firstDelivery = DeliverAsync;
        var failure = await firstDelivery.Should().ThrowAsync<Exception>();
        failure.Which.Should().BeAssignableTo<IRuntimeEnvelopeRetryableException>();
        failure.Which.InnerException.Should().BeOfType<InvalidOperationException>()
            .Which.Message.Should().Be("simulated schedule failure");

        var pending = StudioMemberBindingRunStateSetter.Get(agent);
        pending.Status.Should().Be(StudioMemberBindingRunStatus.MemberNotificationPending);
        pending.MemberNotificationAttempt.Should().Be(0);
        eventSourcing.CommittedEvents.Should().ContainSingle();
        scheduler.Timeouts.Should().BeEmpty();
        publisher.SentMessages.Should().BeEmpty();

        scheduler.ScheduleException = null;
        await agent.HandleEventAsync(RuntimeRetryEnvelope(outcome));

        var recovered = StudioMemberBindingRunStateSetter.Get(agent);
        recovered.MemberNotificationAttempt.Should().Be(1);
        eventSourcing.CommittedEvents
            .OfType<StudioMemberBindingTerminalNotificationAttemptStarted>()
            .Should().ContainSingle().Which.NotificationAttempt.Should().Be(1);
        eventSourcing.CommittedEvents.Count(evt =>
                evt is StudioMemberPlatformBindingExecutionSucceeded
                    or StudioMemberPlatformBindingExecutionFailed)
            .Should().Be(1);
        scheduler.Timeouts.Should().ContainSingle(request =>
            request.CallbackId == "studio-member-binding-terminal-notification-watchdog:a1:bind-1");
        var notification = publisher.SentMessages.Should().ContainSingle().Subject.Event;
        if (succeeded)
            notification.Should().BeOfType<StudioMemberBindingCompletedEvent>();
        else
            notification.Should().BeOfType<StudioMemberBindingFailedEvent>();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ExactPlatformTerminalOutcomeDuplicate_WithoutRuntimeRetry_ShouldHaveNoSideEffects(
        bool succeeded)
    {
        var state = NewReadinessInFlightState(DateTimeOffset.UtcNow.AddSeconds(-1));
        IMessage outcome = succeeded
            ? NewExecutionSucceeded(2)
            : new StudioMemberPlatformBindingExecutionFailed
            {
                BindingRunId = "bind-1",
                PlatformBindingCommandId = "platform-1",
                ProtocolVersion = StudioMemberConventions.PlatformBindingProtocolVersion,
                ExecutionAttempt = 2,
                ExecutionStage = StudioMemberPlatformBindingExecutionStage.ReadinessInFlight,
                Failure = new StudioMemberBindingFailure
                {
                    Code = "SCOPE_BINDING_FAILED",
                    Message = "platform binding failed",
                    FailedAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
                },
            };
        var committed = _agent.Apply(state, outcome);
        var eventSourcing = new RecordingEventSourcing(committed);
        var publisher = new RecordingEventPublisher();
        var scheduler = new RecordingRuntimeCallbackScheduler();
        var agent = NewHandlerAgent(
            committed,
            publisher,
            scheduler,
            eventSourcing: eventSourcing);

        if (outcome is StudioMemberPlatformBindingExecutionSucceeded success)
            await agent.HandlePlatformBindingExecutionSucceeded(success);
        else
            await agent.HandlePlatformBindingExecutionFailed(
                (StudioMemberPlatformBindingExecutionFailed)outcome);

        StudioMemberBindingRunStateSetter.Get(agent).ToByteArray()
            .Should().Equal(committed.ToByteArray());
        eventSourcing.CommittedEvents.Should().BeEmpty();
        scheduler.Timeouts.Should().BeEmpty();
        publisher.SentMessages.Should().BeEmpty();
    }

    [Fact]
    public async Task MemberTerminalNotification_WhenCommitSucceedsButSendFails_ShouldRecoverWithNextAttempt()
    {
        var pendingNotification = _agent.Apply(
            NewReadinessInFlightState(DateTimeOffset.UtcNow.AddSeconds(-1)),
            NewExecutionSucceeded(2));
        var scheduler = new RecordingRuntimeCallbackScheduler();
        var eventSourcing = new RecordingEventSourcing(pendingNotification);
        var publisher = new RecordingEventPublisher
        {
            SendException = new InvalidOperationException("simulated send failure"),
        };
        var agent = NewHandlerAgent(
            pendingNotification,
            publisher,
            scheduler,
            eventSourcing: eventSourcing);

        var activate = () => agent.ActivateAsync();
        await activate.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("simulated send failure");

        var committed = StudioMemberBindingRunStateSetter.Get(agent);
        committed.MemberNotificationAttempt.Should().Be(1);
        eventSourcing.CommittedEvents
            .OfType<StudioMemberBindingTerminalNotificationAttemptStarted>()
            .Should().ContainSingle().Which.NotificationAttempt.Should().Be(1);
        publisher.SentMessages.Should().BeEmpty();
        var scheduled = scheduler.Timeouts.Should().ContainSingle().Subject;
        scheduled.CallbackId.Should()
            .Be("studio-member-binding-terminal-notification-watchdog:a1:bind-1");

        var recoveredPublisher = new RecordingEventPublisher();
        var recoveredScheduler = new RecordingRuntimeCallbackScheduler();
        var recoveredEventSourcing = new RecordingEventSourcing(committed);
        var recovered = NewHandlerAgent(
            committed,
            recoveredPublisher,
            recoveredScheduler,
            eventSourcing: recoveredEventSourcing);

        await recovered.HandleEventAsync(scheduled.TriggerEnvelope);

        StudioMemberBindingRunStateSetter.Get(recovered).MemberNotificationAttempt.Should().Be(2);
        recoveredEventSourcing.CommittedEvents
            .OfType<StudioMemberBindingTerminalNotificationAttemptStarted>()
            .Should().ContainSingle().Which.NotificationAttempt.Should().Be(2);
        recoveredScheduler.Timeouts.Should().ContainSingle(request =>
            request.CallbackId == "studio-member-binding-terminal-notification-watchdog:a2:bind-1");
        recoveredPublisher.SentMessages.Should().ContainSingle().Which.Event.Should()
            .BeOfType<StudioMemberBindingCompletedEvent>();
    }

    [Fact]
    public async Task MemberTerminalNotificationWatchdog_FromExternalEnvelope_ShouldBeRejected()
    {
        var pendingNotification = _agent.Apply(
            NewReadinessInFlightState(DateTimeOffset.UtcNow.AddSeconds(-1)),
            NewExecutionSucceeded(2));
        var publisher = new RecordingEventPublisher();
        var scheduler = new RecordingRuntimeCallbackScheduler();
        var eventSourcing = new RecordingEventSourcing(pendingNotification);
        var agent = NewHandlerAgent(
            pendingNotification,
            publisher,
            scheduler,
            eventSourcing: eventSourcing);

        await agent.HandleEventAsync(new EventEnvelope
        {
            Id = "external-terminal-watchdog",
            Payload = Any.Pack(new StudioMemberBindingTerminalNotificationWatchdogFired
            {
                BindingRunId = "bind-1",
                ExpectedNotificationAttempt = 1,
            }),
            Route = EnvelopeRouteSemantics.CreateDirect("external-actor", RootActorId),
        });

        StudioMemberBindingRunStateSetter.Get(agent).MemberNotificationAttempt.Should().Be(0);
        eventSourcing.CommittedEvents.Should().BeEmpty();
        scheduler.Timeouts.Should().BeEmpty();
        publisher.SentMessages.Should().BeEmpty();
    }

    [Fact]
    public async Task ActivateAsync_AfterCommittedCheckpoint_ShouldExecuteReadinessOnly()
    {
        var state = NewReadinessPendingState();
        var publisher = new RecordingEventPublisher();
        var scheduler = new RecordingRuntimeCallbackScheduler();
        var platformPort = new RecordingPlatformBindingCommandPort();
        var agent = NewHandlerAgent(state, publisher, scheduler, platformPort);

        await agent.ActivateAsync();

        var execute = scheduler.Timeouts.Should().ContainSingle(request =>
                request.CallbackId == "studio-member-binding-execute:v1:a1:bind-1:platform-1")
            .Subject.TriggerEnvelope.Payload.Unpack<StudioMemberPlatformBindingStageExecuteRequested>();
        await agent.HandlePlatformBindingExecuteRequested(execute);

        platformPort.StartRequests.Should().BeEmpty();
        var readiness = platformPort.ExecuteRequests.Should().ContainSingle().Subject;
        readiness.ExecutionStage.Should().Be(StudioMemberPlatformBindingExecutionStage.ReadinessInFlight);
        readiness.ExecutionAttempt.Should().Be(2);
        readiness.RecoverySnapshot.Should().BeEquivalentTo(NewRecoverySnapshot());
        publisher.SentMessages.Should().BeEmpty();
    }

    [Fact]
    public async Task HandleExecuteRequested_WhenDeliveredTwice_ShouldInvokePortOnce()
    {
        var state = NewPlatformPendingState();
        var publisher = new RecordingEventPublisher();
        var scheduler = new RecordingRuntimeCallbackScheduler();
        var platformPort = new RecordingPlatformBindingCommandPort();
        var agent = NewHandlerAgent(state, publisher, scheduler, platformPort);
        var execute = new StudioMemberPlatformBindingStageExecuteRequested
        {
            BindingRunId = "bind-1",
            PlatformBindingCommandId = "platform-1",
            ProtocolVersion = StudioMemberConventions.PlatformBindingProtocolVersion,
            ExpectedExecutionAttempt = 0,
        };

        await agent.HandlePlatformBindingExecuteRequested(execute);
        await agent.HandlePlatformBindingExecuteRequested(execute);

        platformPort.ExecuteRequests.Should().ContainSingle();
        scheduler.Timeouts.Should().ContainSingle();
        publisher.SentMessages.Should().BeEmpty();
    }

    [Fact]
    public async Task StaleExecuteAndWatchdogCallbacks_ShouldHaveNoSideEffects()
    {
        var state = NewInFlightState(DateTimeOffset.UtcNow.AddSeconds(-10));
        var publisher = new RecordingEventPublisher();
        var scheduler = new RecordingRuntimeCallbackScheduler();
        var platformPort = new RecordingPlatformBindingCommandPort();
        var agent = NewHandlerAgent(state, publisher, scheduler, platformPort);

        await agent.HandlePlatformBindingExecuteRequested(new StudioMemberPlatformBindingStageExecuteRequested
        {
            BindingRunId = "bind-1",
            PlatformBindingCommandId = "platform-1",
            ProtocolVersion = StudioMemberConventions.PlatformBindingProtocolVersion,
            ExpectedExecutionAttempt = 0,
        });
        await agent.HandlePlatformBindingWatchdogFired(new StudioMemberPlatformBindingExecutionWatchdogFired
        {
            BindingRunId = "bind-1",
            PlatformBindingCommandId = "platform-1",
            ProtocolVersion = 0,
            ExpectedExecutionAttempt = 1,
        });

        platformPort.ExecuteRequests.Should().BeEmpty();
        scheduler.Timeouts.Should().BeEmpty();
        publisher.SentMessages.Should().BeEmpty();
    }

    [Fact]
    public async Task DuplicateCommandsCompleted_AfterCheckpoint_ShouldHaveNoSideEffects()
    {
        var publisher = new RecordingEventPublisher();
        var scheduler = new RecordingRuntimeCallbackScheduler();
        var agent = NewHandlerAgent(
            NewInFlightState(DateTimeOffset.UtcNow.AddSeconds(-1)),
            publisher,
            scheduler);
        var completed = NewCommandsCompleted();

        await agent.HandlePlatformBindingCommandsCompleted(completed);
        await agent.HandlePlatformBindingCommandsCompleted(completed);

        scheduler.Timeouts.Should().ContainSingle();
        publisher.SentMessages.Should().BeEmpty();
    }

    [Fact]
    public async Task StaleCommandsCompleted_ShouldHaveNoSideEffects()
    {
        var publisher = new RecordingEventPublisher();
        var scheduler = new RecordingRuntimeCallbackScheduler();
        var agent = NewHandlerAgent(
            NewInFlightState(DateTimeOffset.UtcNow.AddSeconds(-1)),
            publisher,
            scheduler);
        var completed = NewCommandsCompleted();
        completed.ExecutionAttempt = 0;

        await agent.HandlePlatformBindingCommandsCompleted(completed);

        scheduler.Timeouts.Should().BeEmpty();
        publisher.SentMessages.Should().BeEmpty();
        StudioMemberBindingRunStateSetter.Get(agent).PlatformExecutionStage.Should()
            .Be(StudioMemberPlatformBindingExecutionStage.CommandInFlight);
    }

    [Fact]
    public async Task ReadinessStaleWatchdog_ShouldStartNewAttemptAndFenceOldContinuation()
    {
        var stale = NewReadinessInFlightState(DateTimeOffset.UtcNow.AddMinutes(-3));
        var publisher = new RecordingEventPublisher();
        var platformPort = new RecordingPlatformBindingCommandPort();
        var agent = NewHandlerAgent(stale, publisher, platformPort: platformPort);

        await agent.HandlePlatformBindingWatchdogFired(new StudioMemberPlatformBindingExecutionWatchdogFired
        {
            BindingRunId = "bind-1",
            PlatformBindingCommandId = "platform-1",
            ProtocolVersion = StudioMemberConventions.PlatformBindingProtocolVersion,
            ExpectedExecutionAttempt = 2,
        });

        var execute = publisher.SentMessages.Should().ContainSingle().Subject.Event
            .Should().BeOfType<StudioMemberPlatformBindingStageExecuteRequested>().Subject;
        await agent.HandlePlatformBindingExecuteRequested(execute);
        platformPort.ExecuteRequests.Should().ContainSingle().Which.ExecutionAttempt.Should().Be(3);

        var retried = StudioMemberBindingRunStateSetter.Get(agent);
        retried.PlatformExecutionAttempt.Should().Be(3);
        var afterOldSuccess = _agent.Apply(retried, NewExecutionSucceeded(executionAttempt: 2));
        afterOldSuccess.Should().BeEquivalentTo(retried);
    }

    [Fact]
    public void OldReadinessContinuations_AfterRetry_ShouldNotOverwriteState()
    {
        var retry = _agent.Apply(
            NewReadinessInFlightState(DateTimeOffset.UtcNow.AddMinutes(-3)),
            new StudioMemberPlatformBindingStageStarted
            {
                BindingRunId = "bind-1",
                PlatformBindingCommandId = "platform-1",
                ProtocolVersion = StudioMemberConventions.PlatformBindingProtocolVersion,
                ExecutionAttempt = 3,
                ExecutionStage = StudioMemberPlatformBindingExecutionStage.ReadinessInFlight,
                StageStartedAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            });

        var timeout = _agent.Apply(retry, new StudioMemberPlatformBindingReadinessObservationTimedOut
        {
            BindingRunId = "bind-1",
            PlatformBindingCommandId = "platform-1",
            TimedOutAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            ProtocolVersion = StudioMemberConventions.PlatformBindingProtocolVersion,
            ExecutionAttempt = 2,
        });
        var success = _agent.Apply(retry, NewExecutionSucceeded(executionAttempt: 2));
        var failure = _agent.Apply(retry, new StudioMemberPlatformBindingExecutionFailed
        {
            BindingRunId = "bind-1",
            PlatformBindingCommandId = "platform-1",
            ProtocolVersion = StudioMemberConventions.PlatformBindingProtocolVersion,
            ExecutionAttempt = 2,
            ExecutionStage = StudioMemberPlatformBindingExecutionStage.ReadinessInFlight,
            Failure = new StudioMemberBindingFailure
            {
                Code = "OLD_FAILURE",
                Message = "old attempt",
                FailedAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            },
        });

        timeout.Should().BeEquivalentTo(retry);
        success.Should().BeEquivalentTo(retry);
        failure.Should().BeEquivalentTo(retry);
    }

    [Fact]
    public async Task ActivateAsync_WhenLegacyPendingStateHasNoCheckpoint_ShouldFailClosedWithoutPortCalls()
    {
        var legacy = NewPlatformPendingState();
        legacy.PlatformBindingProtocolVersion = 0;
        legacy.PlatformExecutionAttempt = 0;
        legacy.PlatformExecutionStage = StudioMemberPlatformBindingExecutionStage.Unspecified;
        legacy.PlatformBindingRecoverySnapshot = null;
        var publisher = new RecordingEventPublisher();
        var scheduler = new RecordingRuntimeCallbackScheduler();
        var platformPort = new RecordingPlatformBindingCommandPort();
        var eventSourcing = new RecordingEventSourcing(legacy);
        var agent = NewHandlerAgent(legacy, publisher, scheduler, platformPort, eventSourcing);

        await agent.ActivateAsync();

        platformPort.StartRequests.Should().BeEmpty();
        platformPort.ExecuteRequests.Should().BeEmpty();
        scheduler.Timeouts.Should().ContainSingle(request =>
            request.CallbackId == "studio-member-binding-terminal-notification-watchdog:a1:bind-1");
        var failed = eventSourcing.CommittedEvents.OfType<StudioMemberPlatformBindingFailed>()
            .Should().ContainSingle().Subject;
        failed.Failure.Code.Should().Be("STUDIO_MEMBER_PLATFORM_BINDING_CHECKPOINT_UNAVAILABLE");
        _agent.Apply(legacy, failed).Status.Should().Be(StudioMemberBindingRunStatus.MemberNotificationPending);
        publisher.SentMessages.Should().ContainSingle().Which.Event.Should()
            .BeOfType<StudioMemberBindingFailedEvent>();
    }

    [Fact]
    public async Task LegacyWireReplay_WhenReadinessTimedOut_ShouldRebuildProtocolZeroPendingAndFailClosed()
    {
        var events = NewLegacyPlatformEventPrefix();
        events.Add(new StudioMemberPlatformBindingReadinessTimedOut
        {
            BindingRunId = "bind-1",
            PlatformBindingCommandId = "platform-1",
            ReadinessStatus = StudioMemberPlatformBindingReadinessStatus.ServingSetMissing,
            TimedOutAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
        });
        var legacy = ReplayWireEvents(events);
        var publisher = new RecordingEventPublisher();
        var platformPort = new RecordingPlatformBindingCommandPort();
        var eventSourcing = new RecordingEventSourcing(legacy);
        var agent = NewHandlerAgent(
            legacy,
            publisher,
            platformPort: platformPort,
            eventSourcing: eventSourcing);

        legacy.Status.Should().Be(StudioMemberBindingRunStatus.PlatformBindingPending);
        legacy.PlatformBindingProtocolVersion.Should().Be(0);
        legacy.PlatformExecutionAttempt.Should().Be(0);
        legacy.PlatformExecutionStage.Should().Be(StudioMemberPlatformBindingExecutionStage.Unspecified);
        legacy.PlatformExecutionInFlight.Should().BeFalse();

        await agent.ActivateAsync();

        platformPort.StartRequests.Should().BeEmpty();
        platformPort.ExecuteRequests.Should().BeEmpty();
        var failed = eventSourcing.CommittedEvents.OfType<StudioMemberPlatformBindingFailed>()
            .Should().ContainSingle().Subject;
        failed.Failure.Code.Should().Be("STUDIO_MEMBER_PLATFORM_BINDING_CHECKPOINT_UNAVAILABLE");
        events.Add(failed);
        var converted = ReplayWireEvents(events);
        converted.Status.Should().Be(StudioMemberBindingRunStatus.MemberNotificationPending);
        var legacyReaderWouldRecoverCommand = converted.Status == StudioMemberBindingRunStatus.PlatformBindingPending
            && (!converted.PlatformExecutionInFlight
                || converted.PlatformExecutionStartedAtUtc == null
                || DateTimeOffset.UtcNow - converted.PlatformExecutionStartedAtUtc.ToDateTimeOffset()
                    >= TimeSpan.FromMinutes(2));
        legacyReaderWouldRecoverCommand.Should().BeFalse();
    }

    [Fact]
    public async Task LegacyWireReplay_WhenSucceededAndAcknowledged_ShouldRebuildTerminalWithoutRecovery()
    {
        var events = NewLegacyPlatformEventPrefix();
        events.Add(new StudioMemberPlatformBindingReadinessTimedOut
        {
            BindingRunId = "bind-1",
            PlatformBindingCommandId = "platform-1",
            TimedOutAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
        });
        events.Add(NewLegacyExecutionStarted());
        events.Add(new StudioMemberPlatformBindingSucceeded
        {
            BindingRunId = "bind-1",
            PlatformBindingCommandId = "platform-1",
            CompletedAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            Result = NewRecoverySnapshot().Result.Clone(),
        });
        events.Add(new StudioMemberBindingTerminalAcknowledged
        {
            BindingRunId = "bind-1",
            Status = StudioMemberBindingRunStatus.Succeeded,
            AcknowledgedAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
        });
        var legacy = ReplayWireEvents(events);
        var publisher = new RecordingEventPublisher();
        var platformPort = new RecordingPlatformBindingCommandPort();
        var agent = NewHandlerAgent(legacy, publisher, platformPort: platformPort);

        legacy.Status.Should().Be(StudioMemberBindingRunStatus.Succeeded);
        legacy.PlatformResult.Should().NotBeNull();

        await agent.ActivateAsync();

        platformPort.StartRequests.Should().BeEmpty();
        platformPort.ExecuteRequests.Should().BeEmpty();
        publisher.SentMessages.Should().BeEmpty();
    }

    [Fact]
    public async Task LegacyWireReplay_WhenFailedAndAcknowledged_ShouldRebuildTerminalWithoutRecovery()
    {
        var events = NewLegacyPlatformEventPrefix();
        events.Add(new StudioMemberPlatformBindingFailed
        {
            BindingRunId = "bind-1",
            PlatformBindingCommandId = "platform-1",
            Failure = new StudioMemberBindingFailure
            {
                Code = "LEGACY_BINDING_FAILED",
                Message = "legacy terminal failure",
                FailedAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            },
        });
        events.Add(new StudioMemberBindingTerminalAcknowledged
        {
            BindingRunId = "bind-1",
            Status = StudioMemberBindingRunStatus.Failed,
            AcknowledgedAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
        });
        var legacy = ReplayWireEvents(events);
        var publisher = new RecordingEventPublisher();
        var platformPort = new RecordingPlatformBindingCommandPort();
        var agent = NewHandlerAgent(legacy, publisher, platformPort: platformPort);

        legacy.Status.Should().Be(StudioMemberBindingRunStatus.Failed);
        legacy.Failure.Code.Should().Be("LEGACY_BINDING_FAILED");

        await agent.ActivateAsync();

        platformPort.StartRequests.Should().BeEmpty();
        platformPort.ExecuteRequests.Should().BeEmpty();
        publisher.SentMessages.Should().BeEmpty();
    }

    [Theory]
    [InlineData(2, 0)]
    [InlineData(1, 3)]
    public async Task UnsupportedWireStartReplay_ShouldRebuildPendingAndFailClosedWithoutPortCalls(
        int protocolVersion,
        int executionAttempt)
    {
        var events = NewLegacyPlatformEventPrefix().Take(2).ToList();
        events.Add(new StudioMemberPlatformBindingExecutionStartRequested
        {
            BindingRunId = "bind-1",
            PlatformBindingCommandId = "platform-unsupported",
            RequestedAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            ProtocolVersion = protocolVersion,
            ExecutionAttempt = executionAttempt,
        });
        var pending = ReplayWireEvents(events);
        var publisher = new RecordingEventPublisher();
        var platformPort = new RecordingPlatformBindingCommandPort();
        var eventSourcing = new RecordingEventSourcing(pending);
        var agent = NewHandlerAgent(
            pending,
            publisher,
            platformPort: platformPort,
            eventSourcing: eventSourcing);

        pending.Status.Should().Be(StudioMemberBindingRunStatus.PlatformBindingPending);
        pending.PlatformBindingProtocolVersion.Should().Be(protocolVersion);
        pending.PlatformExecutionAttempt.Should().Be(executionAttempt);
        pending.PlatformExecutionStage.Should().Be(StudioMemberPlatformBindingExecutionStage.Unspecified);

        await agent.ActivateAsync();

        platformPort.StartRequests.Should().BeEmpty();
        platformPort.ExecuteRequests.Should().BeEmpty();
        eventSourcing.CommittedBatches.Should().HaveCount(2);
        var batch = eventSourcing.CommittedBatches[0];
        batch.Should().HaveCount(3);
        batch[0].Should().BeOfType<StudioMemberPlatformBindingStartRequested>();
        batch[1].Should().BeOfType<StudioMemberPlatformBindingExecutionStarted>();
        var failed = batch[2].Should().BeOfType<StudioMemberPlatformBindingExecutionFailed>().Subject;
        failed.ProtocolVersion.Should().Be(protocolVersion);
        failed.ExecutionAttempt.Should().Be(executionAttempt);
        failed.ExecutionStage.Should().Be(StudioMemberPlatformBindingExecutionStage.Unspecified);
        failed.Failure.Code.Should().Be("STUDIO_MEMBER_PLATFORM_BINDING_CHECKPOINT_UNAVAILABLE");
        var legacyState = ReplayAsDfe98c8Reader(events.Concat(batch));
        legacyState.Status.Should().Be(StudioMemberBindingRunStatus.PlatformBindingPending);
        LegacyReaderWouldRecoverPlatformCommand(legacyState).Should().BeFalse();
    }

    [Fact]
    public void LegacyPlatformBindingMessages_ShouldHaveNoLiveInboxHandlers()
    {
        var handlerPayloadTypes = typeof(StudioMemberBindingRunGAgent)
            .GetMethods(BindingFlags.Instance | BindingFlags.Public)
            .Where(method => method.GetCustomAttribute<EventHandlerAttribute>() != null)
            .SelectMany(method => method.GetParameters())
            .Select(parameter => parameter.ParameterType)
            .ToArray();

        handlerPayloadTypes.Should().NotContain(typeof(StudioMemberPlatformBindingSucceeded));
        handlerPayloadTypes.Should().NotContain(typeof(StudioMemberPlatformBindingFailed));
        handlerPayloadTypes.Should().NotContain(typeof(StudioMemberPlatformBindingReadinessTimedOut));
        handlerPayloadTypes.Should().NotContain(typeof(StudioMemberPlatformBindingStartRequested));
        handlerPayloadTypes.Should().NotContain(typeof(StudioMemberPlatformBindingAccepted));
        handlerPayloadTypes.Should().NotContain(typeof(StudioMemberPlatformBindingExecuteRequested));
        handlerPayloadTypes.Should().NotContain(typeof(StudioMemberPlatformBindingWatchdogFired));
        handlerPayloadTypes.Should().NotContain(typeof(StudioMemberPlatformBindingExecutionStarted));
    }

    [Fact]
    public async Task ActivateAsync_WhenLegacyProtocolHasCommandPendingStage_ShouldCommitMatchingFailClosedOutcome()
    {
        var mixed = NewPlatformPendingState();
        mixed.PlatformBindingProtocolVersion = 0;
        mixed.PlatformExecutionStage = StudioMemberPlatformBindingExecutionStage.CommandPending;
        var publisher = new RecordingEventPublisher();
        var scheduler = new RecordingRuntimeCallbackScheduler();
        var platformPort = new RecordingPlatformBindingCommandPort();
        var eventSourcing = new RecordingEventSourcing(mixed);
        var agent = NewHandlerAgent(mixed, publisher, scheduler, platformPort, eventSourcing);

        await agent.ActivateAsync();

        platformPort.ExecuteRequests.Should().BeEmpty();
        scheduler.Timeouts.Should().ContainSingle(request =>
            request.CallbackId == "studio-member-binding-terminal-notification-watchdog:a1:bind-1");
        var failed = eventSourcing.CommittedEvents.OfType<StudioMemberPlatformBindingExecutionFailed>()
            .Should().ContainSingle().Subject;
        failed.ProtocolVersion.Should().Be(0);
        failed.ExecutionStage.Should().Be(StudioMemberPlatformBindingExecutionStage.CommandPending);
        failed.Failure.Code.Should().Be("STUDIO_MEMBER_PLATFORM_BINDING_CHECKPOINT_UNAVAILABLE");
        _agent.Apply(mixed, failed).Status.Should().Be(StudioMemberBindingRunStatus.MemberNotificationPending);
    }

    [Fact]
    public async Task ActivateAsync_WhenCurrentProtocolStageIsUnspecified_ShouldCommitFailClosedOutcome()
    {
        var mixed = NewPlatformPendingState();
        mixed.PlatformExecutionStage = StudioMemberPlatformBindingExecutionStage.Unspecified;
        mixed.PlatformBindingRecoverySnapshot = null;
        var publisher = new RecordingEventPublisher();
        var scheduler = new RecordingRuntimeCallbackScheduler();
        var platformPort = new RecordingPlatformBindingCommandPort();
        var eventSourcing = new RecordingEventSourcing(mixed);
        var agent = NewHandlerAgent(mixed, publisher, scheduler, platformPort, eventSourcing);

        await agent.ActivateAsync();

        platformPort.StartRequests.Should().BeEmpty();
        platformPort.ExecuteRequests.Should().BeEmpty();
        scheduler.Timeouts.Should().ContainSingle(request =>
            request.CallbackId == "studio-member-binding-terminal-notification-watchdog:a1:bind-1");
        var failed = eventSourcing.CommittedEvents.OfType<StudioMemberPlatformBindingExecutionFailed>()
            .Should().ContainSingle().Subject;
        failed.ProtocolVersion.Should().Be(StudioMemberConventions.PlatformBindingProtocolVersion);
        failed.ExecutionAttempt.Should().Be(mixed.PlatformExecutionAttempt);
        failed.ExecutionStage.Should().Be(StudioMemberPlatformBindingExecutionStage.Unspecified);
        failed.Failure.Code.Should().Be("STUDIO_MEMBER_PLATFORM_BINDING_CHECKPOINT_UNAVAILABLE");
        _agent.Apply(mixed, failed).Status.Should().Be(StudioMemberBindingRunStatus.MemberNotificationPending);
    }

    [Fact]
    public async Task ActivateAsync_WhenFutureProtocolIsPending_ShouldCommitFailClosedWithoutExecutingCommand()
    {
        var future = NewPlatformPendingState();
        future.PlatformBindingProtocolVersion = StudioMemberConventions.PlatformBindingProtocolVersion + 1;
        future.PlatformExecutionStage = StudioMemberPlatformBindingExecutionStage.CommandPending;
        var publisher = new RecordingEventPublisher();
        var scheduler = new RecordingRuntimeCallbackScheduler();
        var platformPort = new RecordingPlatformBindingCommandPort();
        var eventSourcing = new RecordingEventSourcing(future);
        var agent = NewHandlerAgent(future, publisher, scheduler, platformPort, eventSourcing);

        await agent.ActivateAsync();

        platformPort.StartRequests.Should().BeEmpty();
        platformPort.ExecuteRequests.Should().BeEmpty();
        scheduler.Timeouts.Should().ContainSingle(request =>
            request.CallbackId == "studio-member-binding-terminal-notification-watchdog:a1:bind-1");
        var failed = eventSourcing.CommittedEvents.OfType<StudioMemberPlatformBindingExecutionFailed>()
            .Should().ContainSingle().Subject;
        failed.ProtocolVersion.Should().Be(future.PlatformBindingProtocolVersion);
        failed.ExecutionAttempt.Should().Be(future.PlatformExecutionAttempt);
        failed.ExecutionStage.Should().Be(StudioMemberPlatformBindingExecutionStage.CommandPending);
        failed.Failure.Code.Should().Be("STUDIO_MEMBER_PLATFORM_BINDING_CHECKPOINT_UNAVAILABLE");
        _agent.Apply(future, failed).Status.Should().Be(StudioMemberBindingRunStatus.MemberNotificationPending);
    }

    [Fact]
    public void FutureProtocolReadinessSuccess_ShouldNotCommitTerminalSuccess()
    {
        var future = NewReadinessInFlightState(DateTimeOffset.UtcNow.AddSeconds(-1));
        future.PlatformBindingProtocolVersion = StudioMemberConventions.PlatformBindingProtocolVersion + 1;
        var succeeded = NewExecutionSucceeded(future.PlatformExecutionAttempt);
        succeeded.ProtocolVersion = future.PlatformBindingProtocolVersion;

        var afterSuccess = _agent.Apply(future, succeeded);

        afterSuccess.Should().BeEquivalentTo(future);
    }

    [Fact]
    public void StateWireContract_ShouldPreserveDeprecatedInFlightBitAndDefaultLegacyFence()
    {
        var current = NewReadinessInFlightState(DateTimeOffset.UtcNow.AddSeconds(-1));

        var roundTripped = StudioMemberBindingRunState.Parser.ParseFrom(current.ToByteArray());

        roundTripped.PlatformExecutionInFlight.Should().BeTrue();
        roundTripped.PlatformExecutionStage.Should()
            .Be(StudioMemberPlatformBindingExecutionStage.ReadinessInFlight);
        roundTripped.PlatformExecutionAttempt.Should().Be(2);
        roundTripped.PlatformExecutionStartedAtUtc!.ToDateTimeOffset().Year.Should().Be(9999);
        roundTripped.PlatformExecutionStageStartedAtUtc.Should().NotBeNull();

        var legacy = StudioMemberBindingRunState.Parser.ParseFrom(new StudioMemberBindingRunState
        {
            BindingRunId = "bind-legacy",
            Status = StudioMemberBindingRunStatus.PlatformBindingPending,
            PlatformBindingCommandId = "platform-legacy",
            PlatformExecutionInFlight = true,
        }.ToByteArray());
        legacy.PlatformExecutionInFlight.Should().BeTrue();
        legacy.PlatformBindingProtocolVersion.Should().Be(0);
        legacy.PlatformExecutionAttempt.Should().Be(0);
        legacy.PlatformExecutionStage.Should().Be(StudioMemberPlatformBindingExecutionStage.Unspecified);
        legacy.PlatformExecutionStageStartedAtUtc.Should().BeNull();
    }

    [Fact]
    public void ProtocolOnePendingStages_ShouldFenceLegacyReaderFromCommandRecovery()
    {
        var now = DateTimeOffset.UtcNow;
        var states = new[]
        {
            NewAcceptancePendingState(),
            NewPlatformPendingState(),
            NewInFlightState(now.AddMinutes(-3)),
            NewReadinessPendingState(),
            NewReadinessInFlightState(now.AddMinutes(-3)),
        };

        foreach (var state in states)
        {
            state.PlatformExecutionInFlight.Should().BeTrue();
            state.PlatformExecutionStartedAtUtc.Should().NotBeNull();
            var legacyReaderWouldRecoverCommand = !state.PlatformExecutionInFlight
                || now - state.PlatformExecutionStartedAtUtc!.ToDateTimeOffset() >= TimeSpan.FromMinutes(2);
            legacyReaderWouldRecoverCommand.Should().BeFalse(
                $"legacy reader must not replay command for typed stage {state.PlatformExecutionStage}");
        }
    }

    [Fact]
    public void ProtocolOneReadinessTimeout_ShouldUseTypeUrlUnknownToLegacyReader()
    {
        var legacyTypeUrl = Any.Pack(new StudioMemberPlatformBindingReadinessTimedOut()).TypeUrl;
        var protocolOneTypeUrl = Any.Pack(
            new StudioMemberPlatformBindingReadinessObservationTimedOut()).TypeUrl;

        protocolOneTypeUrl.Should().NotBe(legacyTypeUrl);
        StudioMemberPlatformBindingReadinessTimedOut.Descriptor
            .FindFieldByName("protocol_version").Should().BeNull();
        StudioMemberPlatformBindingReadinessObservationTimedOut.Descriptor
            .FindFieldByName("protocol_version")!.FieldNumber.Should().Be(5);
    }

    [Fact]
    public void ProtocolOneTerminalOutcomes_ShouldUseTypeUrlsUnknownToLegacyReader()
    {
        var legacySuccessTypeUrl = Any.Pack(new StudioMemberPlatformBindingSucceeded()).TypeUrl;
        var protocolOneSuccessTypeUrl = Any.Pack(
            new StudioMemberPlatformBindingExecutionSucceeded()).TypeUrl;
        var legacyFailureTypeUrl = Any.Pack(new StudioMemberPlatformBindingFailed()).TypeUrl;
        var protocolOneFailureTypeUrl = Any.Pack(
            new StudioMemberPlatformBindingExecutionFailed()).TypeUrl;

        protocolOneSuccessTypeUrl.Should().NotBe(legacySuccessTypeUrl);
        protocolOneFailureTypeUrl.Should().NotBe(legacyFailureTypeUrl);
        StudioMemberPlatformBindingSucceeded.Descriptor
            .FindFieldByName("protocol_version").Should().BeNull();
        StudioMemberPlatformBindingSucceeded.Descriptor
            .FindFieldByName("execution_attempt").Should().BeNull();
        StudioMemberPlatformBindingFailed.Descriptor
            .FindFieldByName("protocol_version").Should().BeNull();
        StudioMemberPlatformBindingFailed.Descriptor
            .FindFieldByName("execution_attempt").Should().BeNull();
        StudioMemberPlatformBindingFailed.Descriptor
            .FindFieldByName("execution_stage").Should().BeNull();
        StudioMemberPlatformBindingExecutionSucceeded.Descriptor
            .FindFieldByName("protocol_version")!.FieldNumber.Should().Be(5);
        StudioMemberPlatformBindingExecutionFailed.Descriptor
            .FindFieldByName("protocol_version")!.FieldNumber.Should().Be(4);
    }

    [Fact]
    public void LegacyTerminalOutcomes_FromPriorAttempt_ShouldNotAffectProtocolOneRetry()
    {
        var retried = NewReadinessInFlightState(DateTimeOffset.UtcNow.AddSeconds(-1));
        var legacySuccess = new StudioMemberPlatformBindingSucceeded
        {
            BindingRunId = "bind-1",
            PlatformBindingCommandId = "platform-1",
            CompletedAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            Result = NewRecoverySnapshot().Result.Clone(),
        };
        var legacyFailure = new StudioMemberPlatformBindingFailed
        {
            BindingRunId = "bind-1",
            PlatformBindingCommandId = "platform-1",
            Failure = new StudioMemberBindingFailure
            {
                Code = "STALE_ATTEMPT",
                Message = "legacy attempt completed after protocol v1 retry",
                FailedAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            },
        };

        _agent.Apply(retried, legacySuccess).Should().BeEquivalentTo(retried);
        _agent.Apply(retried, legacyFailure).Should().BeEquivalentTo(retried);
    }

    [Fact]
    public void ProtocolOneControlMessages_ShouldUseTypeUrlsUnknownToLegacyReader()
    {
        Any.Pack(new StudioMemberPlatformBindingExecutionStartRequested()).TypeUrl.Should().NotBe(
            Any.Pack(new StudioMemberPlatformBindingStartRequested()).TypeUrl);
        Any.Pack(new StudioMemberPlatformBindingExecutionStartAccepted()).TypeUrl.Should().NotBe(
            Any.Pack(new StudioMemberPlatformBindingAccepted()).TypeUrl);
        Any.Pack(new StudioMemberPlatformBindingStageExecuteRequested()).TypeUrl.Should().NotBe(
            Any.Pack(new StudioMemberPlatformBindingExecuteRequested()).TypeUrl);
        Any.Pack(new StudioMemberPlatformBindingExecutionWatchdogFired()).TypeUrl.Should().NotBe(
            Any.Pack(new StudioMemberPlatformBindingWatchdogFired()).TypeUrl);
        Any.Pack(new StudioMemberPlatformBindingStageStarted()).TypeUrl.Should().NotBe(
            Any.Pack(new StudioMemberPlatformBindingExecutionStarted()).TypeUrl);

        StudioMemberPlatformBindingStartRequested.Descriptor.FindFieldByNumber(6).Should().BeNull();
        StudioMemberPlatformBindingStartRequested.Descriptor
            .FindFieldByName("protocol_version").Should().BeNull();
        StudioMemberPlatformBindingExecutionStartRequested.Descriptor
            .FindFieldByNumber(6)!.Name.Should().Be("protocol_version");
        StudioMemberPlatformBindingExecutionStartRequested.Descriptor
            .FindFieldByNumber(7)!.Name.Should().Be("execution_attempt");
    }

    [Fact]
    public void Rejected_ShouldRecordTerminalFailure()
    {
        var accepted = _agent.Apply(new StudioMemberBindingRunState(), NewRequested());
        var failedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow.AddSeconds(1));

        var rejected = _agent.Apply(accepted, new StudioMemberBindingRejectedEvent
        {
            BindingRunId = "bind-1",
            ScopeId = "scope-1",
            MemberId = "m-1",
            Failure = new StudioMemberBindingFailure
            {
                Code = "STUDIO_MEMBER_NOT_FOUND",
                Message = "member missing",
                FailedAtUtc = failedAt,
            },
        });

        rejected.Status.Should().Be(StudioMemberBindingRunStatus.Rejected);
        rejected.Failure.Code.Should().Be("STUDIO_MEMBER_NOT_FOUND");
        rejected.UpdatedAtUtc.Should().Be(failedAt);
    }

    [Fact]
    public void TerminalState_ShouldIgnoreLaterPlatformFailure()
    {
        var accepted = NewReadinessInFlightState(DateTimeOffset.UtcNow.AddSeconds(-1));
        var succeeded = _agent.Apply(accepted, new StudioMemberPlatformBindingExecutionSucceeded
        {
            BindingRunId = "bind-1",
            PlatformBindingCommandId = "platform-1",
            CompletedAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow.AddSeconds(1)),
            ProtocolVersion = StudioMemberConventions.PlatformBindingProtocolVersion,
            ExecutionAttempt = 2,
            Result = new StudioMemberPlatformBindingResult
            {
                PublishedServiceId = "member-m-1",
                RevisionId = "rev-1",
                ImplementationKind = StudioMemberImplementationKind.Script,
            },
        });

        var afterFailure = _agent.Apply(succeeded, new StudioMemberPlatformBindingExecutionFailed
        {
            BindingRunId = "bind-1",
            PlatformBindingCommandId = "platform-1",
            ProtocolVersion = StudioMemberConventions.PlatformBindingProtocolVersion,
            ExecutionAttempt = 2,
            ExecutionStage = StudioMemberPlatformBindingExecutionStage.ReadinessInFlight,
            Failure = new StudioMemberBindingFailure
            {
                Code = "SCOPE_BINDING_FAILED",
                Message = "late failure",
                FailedAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow.AddSeconds(2)),
            },
        });

        afterFailure.Status.Should().Be(StudioMemberBindingRunStatus.MemberNotificationPending);
        afterFailure.PlatformResult.RevisionId.Should().Be("rev-1");
        afterFailure.Failure.Should().BeNull();
    }

    private StudioMemberBindingRunState ReplayWireEvents(IEnumerable<IMessage> events)
    {
        var state = new StudioMemberBindingRunState();
        foreach (var evt in events)
        {
            var packed = Any.Pack(evt);
            var wireEvent = evt.Descriptor.Parser.ParseFrom(packed.Value);
            state = _agent.Apply(state, wireEvent);
        }

        return state;
    }

    private StudioMemberBindingRunState ReplayAsDfe98c8Reader(IEnumerable<IMessage> events)
    {
        var state = new StudioMemberBindingRunState();
        foreach (var evt in events)
        {
            if (evt is not (StudioMemberBindingRunRequested
                or StudioMemberBindingAdmittedEvent
                or StudioMemberBindingRejectedEvent
                or StudioMemberPlatformBindingStartRequested
                or StudioMemberPlatformBindingAccepted
                or StudioMemberPlatformBindingExecutionStarted
                or StudioMemberPlatformBindingReadinessTimedOut
                or StudioMemberPlatformBindingSucceeded
                or StudioMemberPlatformBindingFailed
                or StudioMemberBindingTerminalAcknowledged))
            {
                continue;
            }

            var packed = Any.Pack(evt);
            var wireEvent = evt.Descriptor.Parser.ParseFrom(packed.Value);
            state = _agent.Apply(state, wireEvent);
        }

        return state;
    }

    private void AssertDfe98c8ReaderRemainsFenced(IEnumerable<IMessage> events, string cutPoint)
    {
        var legacyState = ReplayAsDfe98c8Reader(events);
        legacyState.Status.Should().Be(
            StudioMemberBindingRunStatus.PlatformBindingPending,
            $"dfe98c8 replay must remain pending after committed cut point {cutPoint}");
        legacyState.PlatformExecutionInFlight.Should().BeTrue(
            $"dfe98c8 replay must retain the poison bit after committed cut point {cutPoint}");
        legacyState.PlatformExecutionStartedAtUtc!.ToDateTimeOffset().Year.Should().Be(
            9999,
            $"dfe98c8 replay must retain the poison timestamp after committed cut point {cutPoint}");
        LegacyReaderWouldRecoverPlatformCommand(legacyState).Should().BeFalse(
            $"dfe98c8 replay must not re-execute the platform command after {cutPoint}");
    }

    private static bool LegacyReaderWouldRecoverPlatformCommand(StudioMemberBindingRunState state)
    {
        if (state.Status == StudioMemberBindingRunStatus.Admitted)
            return true;
        if (state.Status != StudioMemberBindingRunStatus.PlatformBindingPending)
            return false;
        if (!state.PlatformExecutionInFlight || state.PlatformExecutionStartedAtUtc == null)
            return true;

        return DateTimeOffset.UtcNow - state.PlatformExecutionStartedAtUtc.ToDateTimeOffset()
            >= TimeSpan.FromMinutes(2);
    }

    private static List<IMessage> NewLegacyPlatformEventPrefix()
    {
        var requested = NewRequested();
        return
        [
            requested,
            new StudioMemberBindingAdmittedEvent
            {
                BindingRunId = "bind-1",
                ScopeId = "scope-1",
                MemberId = "m-1",
                PublishedServiceId = "member-m-1",
                ImplementationKind = StudioMemberImplementationKind.Script,
                DisplayName = "Script member",
                AdmittedAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            },
            new StudioMemberPlatformBindingStartRequested
            {
                BindingRunId = "bind-1",
                PlatformBindingCommandId = "platform-1",
                Request = requested.Request.Clone(),
                Admitted = new StudioMemberBindingAdmittedSnapshot
                {
                    MemberId = "m-1",
                    ScopeId = "scope-1",
                    PublishedServiceId = "member-m-1",
                    ImplementationKind = StudioMemberImplementationKind.Script,
                    DisplayName = "Script member",
                },
                RequestedAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            },
            new StudioMemberPlatformBindingAccepted
            {
                BindingRunId = "bind-1",
                PlatformBindingCommandId = "platform-1",
                AcceptedAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            },
            NewLegacyExecutionStarted(),
        ];
    }

    private static StudioMemberPlatformBindingExecutionStarted NewLegacyExecutionStarted() => new()
    {
        BindingRunId = "bind-1",
        PlatformBindingCommandId = "platform-1",
        StartedAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
    };

    private static StudioMemberBindingRunRequested NewRequested(
        Timestamp? requestedAt = null)
    {
        return new StudioMemberBindingRunRequested
        {
            RequestedAtUtc = requestedAt ?? Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            Request = new StudioMemberBindingRequest
            {
                BindingRunId = "bind-1",
                ScopeId = "scope-1",
                MemberId = "m-1",
                RequestHash = "hash-1",
                Script = new StudioMemberScriptBindingRequest
                {
                    ScriptId = "script-1",
                    ScriptRevision = "rev-a",
                },
            },
        };
    }

    private static EventEnvelope RuntimeRetryEnvelope(IMessage evt)
    {
        var envelope = new EventEnvelope
        {
            Id = $"retry-{Guid.NewGuid():N}",
            Payload = Any.Pack(evt),
            Route = EnvelopeRouteSemantics.CreateDirect("platform-binding-authority", RootActorId),
        };
        envelope.EnsureRuntime().Retry = new EnvelopeRetryContext
        {
            OriginEventId = "origin-binding-run-event",
            Attempt = 1,
            LastErrorType = nameof(IRuntimeEnvelopeRetryableException),
        };
        return envelope;
    }

    private static StudioMemberPlatformBindingRecoverySnapshot NewRecoverySnapshot()
    {
        var snapshot = new StudioMemberPlatformBindingRecoverySnapshot
        {
            Result = new StudioMemberPlatformBindingResult
            {
                PublishedServiceId = "member-m-1",
                RevisionId = "rev-platform-1",
                ImplementationKind = StudioMemberImplementationKind.Script,
                ExpectedActorId = "gagent-service:script-runtime:deployment-1",
                ImplementationRef = new StudioMemberImplementationRef
                {
                    Script = new StudioMemberScriptRef
                    {
                        ScriptId = "script-1",
                        ScriptRevision = "rev-platform-1",
                    },
                },
            },
            ExpectedDeploymentId = "deployment-1",
            ActivationAttemptId = "platform-1:a1",
        };
        return snapshot;
    }

    private static StudioMemberPlatformBindingCommandsCompleted NewCommandsCompleted() => new()
    {
        BindingRunId = "bind-1",
        PlatformBindingCommandId = "platform-1",
        RecoverySnapshot = NewRecoverySnapshot(),
        CompletedAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
        ProtocolVersion = StudioMemberConventions.PlatformBindingProtocolVersion,
        ExecutionAttempt = 1,
    };

    private static StudioMemberPlatformBindingExecutionSucceeded NewExecutionSucceeded(int executionAttempt) => new()
    {
        BindingRunId = "bind-1",
        PlatformBindingCommandId = "platform-1",
        ProtocolVersion = StudioMemberConventions.PlatformBindingProtocolVersion,
        ExecutionAttempt = executionAttempt,
        CompletedAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
        Result = NewRecoverySnapshot().Result.Clone(),
    };

    private static StudioMemberBindingAuthorityTerminated NewAuthorityTermination() => new()
    {
        BindingRunId = "bind-1",
        ScopeId = "scope-1",
        MemberId = "m-1",
        Failure = new StudioMemberBindingFailure
        {
            Code = "STUDIO_MEMBER_DELETED",
            Message = "member was deleted before binding completed.",
            FailedAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
        },
    };

    private static Timestamp NewLegacyExecutionFenceTimestamp() => Timestamp.FromDateTime(
        new DateTime(9999, 1, 1, 0, 0, 0, DateTimeKind.Utc));

    private StudioMemberBindingRunState NewAcceptancePendingState()
    {
        var requested = _agent.Apply(new StudioMemberBindingRunState(), NewRequested());
        var admitted = ApplyAdmitted(requested);
        return _agent.Apply(admitted, new StudioMemberPlatformBindingExecutionStartRequested
        {
            BindingRunId = "bind-1",
            PlatformBindingCommandId = "platform-1",
            RequestedAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow.AddSeconds(1)),
            ProtocolVersion = StudioMemberConventions.PlatformBindingProtocolVersion,
            ExecutionAttempt = 0,
        });
    }

    private StudioMemberBindingRunState NewPlatformPendingState()
    {
        var acceptancePending = NewAcceptancePendingState();
        return _agent.Apply(acceptancePending, new StudioMemberPlatformBindingExecutionStartAccepted
        {
            BindingRunId = "bind-1",
            PlatformBindingCommandId = "platform-1",
            AcceptedAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow.AddSeconds(1)),
            ProtocolVersion = StudioMemberConventions.PlatformBindingProtocolVersion,
            ExecutionAttempt = 0,
        });
    }

    private StudioMemberBindingRunState ApplyAdmitted(StudioMemberBindingRunState requested)
    {
        return _agent.Apply(requested, new StudioMemberBindingAdmittedEvent
        {
            BindingRunId = "bind-1",
            ScopeId = "scope-1",
            MemberId = "m-1",
            PublishedServiceId = "member-m-1",
            ImplementationKind = StudioMemberImplementationKind.Script,
            DisplayName = "Script member",
            AdmittedAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
        });
    }

    private StudioMemberBindingRunState NewInFlightState(DateTimeOffset startedAt)
    {
        var pending = NewPlatformPendingState();
        return _agent.Apply(pending, new StudioMemberPlatformBindingStageStarted
        {
            BindingRunId = "bind-1",
            PlatformBindingCommandId = "platform-1",
            ProtocolVersion = StudioMemberConventions.PlatformBindingProtocolVersion,
            ExecutionAttempt = 1,
            ExecutionStage = StudioMemberPlatformBindingExecutionStage.CommandInFlight,
            StageStartedAtUtc = Timestamp.FromDateTimeOffset(startedAt),
        });
    }

    private StudioMemberBindingRunState NewReadinessPendingState()
    {
        var commandInFlight = NewInFlightState(DateTimeOffset.UtcNow.AddSeconds(-10));
        return _agent.Apply(commandInFlight, new StudioMemberPlatformBindingCommandsCompleted
        {
            BindingRunId = "bind-1",
            PlatformBindingCommandId = "platform-1",
            RecoverySnapshot = NewRecoverySnapshot(),
            CompletedAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow.AddSeconds(-5)),
            ProtocolVersion = StudioMemberConventions.PlatformBindingProtocolVersion,
            ExecutionAttempt = 1,
        });
    }

    private StudioMemberBindingRunState NewReadinessInFlightState(DateTimeOffset startedAt)
    {
        var pending = NewReadinessPendingState();
        return _agent.Apply(pending, new StudioMemberPlatformBindingStageStarted
        {
            BindingRunId = "bind-1",
            PlatformBindingCommandId = "platform-1",
            ProtocolVersion = StudioMemberConventions.PlatformBindingProtocolVersion,
            ExecutionAttempt = 2,
            ExecutionStage = StudioMemberPlatformBindingExecutionStage.ReadinessInFlight,
            StageStartedAtUtc = Timestamp.FromDateTimeOffset(startedAt),
        });
    }

    private static StudioMemberBindingRunGAgent NewHandlerAgent(
        StudioMemberBindingRunState state,
        RecordingEventPublisher publisher,
        RecordingRuntimeCallbackScheduler? scheduler = null,
        RecordingPlatformBindingCommandPort? platformPort = null,
        RecordingEventSourcing? eventSourcing = null)
    {
        var agent = new StudioMemberBindingRunGAgent(platformPort)
        {
            EventSourcing = eventSourcing ?? new RecordingEventSourcing(state),
            EventPublisher = publisher,
            Services = new ServiceCollection()
                .AddSingleton<IActorRuntimeCallbackScheduler>(
                    scheduler ?? new RecordingRuntimeCallbackScheduler())
                .BuildServiceProvider(),
        };
        StudioMemberBindingRunStateSetter.Set(agent, state);
        GAgentIdSetter.Set(agent, RootActorId);
        return agent;
    }

    private sealed class StudioMemberBindingRunStateApplier
    {
        private static readonly MethodInfo TransitionStateMethod =
            typeof(StudioMemberBindingRunGAgent).GetMethod(
                "TransitionState",
                BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("TransitionState method not found.");

        private static readonly MethodInfo IsSameRequestMethod =
            typeof(StudioMemberBindingRunGAgent).GetMethod(
                "IsSameRequest",
                BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("IsSameRequest method not found.");

        private readonly StudioMemberBindingRunGAgent _agent = new();

        public StudioMemberBindingRunState Apply(StudioMemberBindingRunState current, IMessage evt)
        {
            var result = TransitionStateMethod.Invoke(_agent, [current, evt])
                ?? throw new InvalidOperationException("TransitionState returned null.");
            return (StudioMemberBindingRunState)result;
        }

        public bool IsSameRequest(
            StudioMemberBindingRequest current,
            StudioMemberBindingRequest incoming,
            string currentHash)
        {
            var result = IsSameRequestMethod.Invoke(null, [current, incoming, currentHash])
                ?? throw new InvalidOperationException("IsSameRequest returned null.");
            return (bool)result;
        }
    }

    private static class StudioMemberBindingRunStateSetter
    {
        private static readonly FieldInfo StateField =
            typeof(StudioMemberBindingRunGAgent).BaseType!
                .GetField("_state", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("GAgent state field not found.");

        public static void Set(StudioMemberBindingRunGAgent agent, StudioMemberBindingRunState state) =>
            StateField.SetValue(agent, state.Clone());

        public static StudioMemberBindingRunState Get(StudioMemberBindingRunGAgent agent) =>
            ((StudioMemberBindingRunState)StateField.GetValue(agent)!).Clone();
    }

    private static class GAgentIdSetter
    {
        private static readonly FieldInfo IdField =
            typeof(StudioMemberBindingRunGAgent).BaseType!.BaseType!
                .GetField("<Id>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("GAgent id field not found.");

        public static void Set(StudioMemberBindingRunGAgent agent, string id) =>
            IdField.SetValue(agent, id);
    }

    private sealed class RecordingEventSourcing(StudioMemberBindingRunState replayState)
        : IEventSourcingBehavior<StudioMemberBindingRunState>
    {
        private readonly List<IMessage> _pending = [];
        private readonly StudioMemberBindingRunStateApplier _applier = new();
        public Exception? ConfirmException { get; init; }
        public long CurrentVersion { get; private set; }
        public List<IMessage> CommittedEvents { get; } = [];
        public List<IReadOnlyList<IMessage>> CommittedBatches { get; } = [];

        public void RaiseEvent<TEvent>(TEvent evt) where TEvent : IMessage =>
            _pending.Add(evt);

        public Task<EventStoreCommitResult> ConfirmEventsAsync(CancellationToken ct = default)
        {
            if (ConfirmException != null)
                return Task.FromException<EventStoreCommitResult>(ConfirmException);

            var result = EventSourcingTestCommit.From(_pending, CurrentVersion);
            CommittedBatches.Add(_pending.ToArray());
            CommittedEvents.AddRange(_pending);
            CurrentVersion = result.LatestVersion;
            _pending.Clear();
            return Task.FromResult(result);
        }

        public Task PersistSnapshotAsync(StudioMemberBindingRunState currentState, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task<StudioMemberBindingRunState?> ReplayAsync(string agentId, CancellationToken ct = default) =>
            Task.FromResult<StudioMemberBindingRunState?>(replayState.Clone());

        public void DiscardPendingEvents()
        {
            _pending.Clear();
        }

        public StudioMemberBindingRunState TransitionState(StudioMemberBindingRunState current, IMessage evt) =>
            _applier.Apply(current, evt);
    }

    private sealed class RecordingEventPublisher : IEventPublisher
    {
        public Exception? SendException { get; set; }
        public List<SentMessage> SentMessages { get; } = [];

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
                return Task.FromException(SendException);

            SentMessages.Add(new SentMessage(targetActorId, evt));
            return Task.CompletedTask;
        }
    }

    private sealed record SentMessage(string TargetActorId, IMessage Event);

    private sealed class RecordingPlatformBindingCommandPort : IStudioMemberPlatformBindingCommandPort
    {
        public Exception? ExecuteException { get; init; }

        public List<StudioMemberPlatformBindingExecutionStartRequested> StartRequests { get; } = [];

        public List<StudioMemberPlatformBindingExecutionRequest> ExecuteRequests { get; } = [];

        public Task<StudioMemberPlatformBindingExecutionStartAccepted> StartAsync(
            string replyActorId,
            StudioMemberPlatformBindingExecutionStartRequested request,
            CancellationToken ct = default)
        {
            StartRequests.Add(request.Clone());
            return Task.FromResult(new StudioMemberPlatformBindingExecutionStartAccepted
            {
                BindingRunId = request.BindingRunId,
                PlatformBindingCommandId = request.PlatformBindingCommandId,
                AcceptedAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
                ProtocolVersion = request.ProtocolVersion,
                ExecutionAttempt = request.ExecutionAttempt,
            });
        }

        public Task<StudioMemberPlatformBindingExecutionAccepted> ExecuteAsync(
            string replyActorId,
            StudioMemberPlatformBindingExecutionRequest request,
            CancellationToken ct = default)
        {
            ExecuteRequests.Add(request.Clone());
            if (ExecuteException != null)
                return Task.FromException<StudioMemberPlatformBindingExecutionAccepted>(ExecuteException);

            return Task.FromResult(new StudioMemberPlatformBindingExecutionAccepted(
                request.BindingRunId,
                request.PlatformBindingCommandId,
                request.ProtocolVersion,
                request.ExecutionAttempt));
        }
    }

    private sealed class RecordingRuntimeCallbackScheduler : IActorRuntimeCallbackScheduler
    {
        public Exception? ScheduleException { get; set; }
        public List<RuntimeCallbackTimeoutRequest> Timeouts { get; } = [];

        public Task<RuntimeCallbackLease> ScheduleTimeoutAsync(
            RuntimeCallbackTimeoutRequest request,
            CancellationToken ct = default)
        {
            if (ScheduleException != null)
                return Task.FromException<RuntimeCallbackLease>(ScheduleException);

            Timeouts.Add(request);
            return Task.FromResult(new RuntimeCallbackLease(
                request.ActorId,
                request.CallbackId,
                Timeouts.Count,
                RuntimeCallbackBackend.InMemory));
        }

        public Task<RuntimeCallbackLease> ScheduleTimerAsync(
            RuntimeCallbackTimerRequest request,
            CancellationToken ct = default) =>
            throw new NotImplementedException("Timers are not used by this test.");

        public Task CancelAsync(RuntimeCallbackLease lease, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task PurgeActorAsync(string actorId, CancellationToken ct = default) =>
            Task.CompletedTask;
    }
}
