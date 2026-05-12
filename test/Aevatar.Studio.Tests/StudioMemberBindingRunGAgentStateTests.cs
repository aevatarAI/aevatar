using System.Reflection;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Attributes;
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.GAgents.StudioMember;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Studio.Tests;

public sealed class StudioMemberBindingRunGAgentStateTests
{
    private readonly StudioMemberBindingRunStateApplier _agent = new();

    [Theory]
    [InlineData(nameof(StudioMemberBindingRunGAgent.HandlePlatformBindingWatchdogFired))]
    [InlineData(nameof(StudioMemberBindingRunGAgent.HandlePlatformBindingFailed))]
    public void PlatformBindingContinuationHandlers_ShouldAllowSelfHandling(string handlerName)
    {
        var handler = typeof(StudioMemberBindingRunGAgent).GetMethod(handlerName)
            ?? throw new InvalidOperationException($"Handler '{handlerName}' not found.");

        handler.GetCustomAttribute<EventHandlerAttribute>()
            .Should().NotBeNull()
            .And.Match<EventHandlerAttribute>(attribute => attribute.AllowSelfHandling);
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
    public void PlatformBindingStartRequested_ShouldPersistCommandIdForRecovery()
    {
        var requested = _agent.Apply(new StudioMemberBindingRunState(), NewRequested());
        var admitted = _agent.Apply(requested, new StudioMemberBindingAdmittedEvent
        {
            BindingRunId = "bind-1",
            ScopeId = "scope-1",
            MemberId = "m-1",
            PublishedServiceId = "member-m-1",
            ImplementationKind = StudioMemberImplementationKind.Script,
            DisplayName = "Script member",
            AdmittedAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
        });

        var pending = _agent.Apply(admitted, new StudioMemberPlatformBindingStartRequested
        {
            BindingRunId = "bind-1",
            PlatformBindingCommandId = "platform-bind-1",
            RequestedAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow.AddSeconds(1)),
        });

        pending.Status.Should().Be(StudioMemberBindingRunStatus.PlatformBindingPending);
        pending.PlatformBindingCommandId.Should().Be("platform-bind-1");
        pending.AttemptCount.Should().Be(1);
    }

    [Fact]
    public void PlatformSucceeded_ShouldRecordTerminalResult()
    {
        var accepted = NewPlatformPendingState();
        var completedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow.AddSeconds(2));

        var succeeded = _agent.Apply(accepted, new StudioMemberPlatformBindingSucceeded
        {
            BindingRunId = "bind-1",
            PlatformBindingCommandId = "platform-1",
            CompletedAtUtc = completedAt,
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
        succeeded.UpdatedAtUtc.Should().Be(completedAt);
    }

    [Fact]
    public void MemberTerminalAcknowledged_AfterPlatformSuccess_ShouldMarkRunSucceeded()
    {
        var pendingNotification = _agent.Apply(NewPlatformPendingState(), new StudioMemberPlatformBindingSucceeded
        {
            BindingRunId = "bind-1",
            PlatformBindingCommandId = "platform-1",
            CompletedAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow.AddSeconds(2)),
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
        var accepted = NewPlatformPendingState();

        var stale = _agent.Apply(accepted, new StudioMemberPlatformBindingSucceeded
        {
            BindingRunId = "bind-1",
            PlatformBindingCommandId = "platform-stale",
            CompletedAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow.AddSeconds(2)),
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
        var accepted = NewPlatformPendingState();
        var failedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow.AddSeconds(2));

        var failed = _agent.Apply(accepted, new StudioMemberPlatformBindingFailed
        {
            BindingRunId = "bind-1",
            PlatformBindingCommandId = "platform-1",
            Failure = new StudioMemberBindingFailure
            {
                Code = "SCOPE_BINDING_FAILED",
                Message = "platform failed",
                FailedAtUtc = failedAt,
            },
        });

        failed.Status.Should().Be(StudioMemberBindingRunStatus.MemberNotificationPending);
        failed.Failure.Code.Should().Be("SCOPE_BINDING_FAILED");
        failed.UpdatedAtUtc.Should().Be(failedAt);
    }

    [Fact]
    public void PlatformExecutionStarted_ShouldMarkExecutionInFlight()
    {
        var accepted = NewPlatformPendingState();
        var startedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow.AddSeconds(2));

        var started = _agent.Apply(accepted, new StudioMemberPlatformBindingExecutionStarted
        {
            BindingRunId = "bind-1",
            PlatformBindingCommandId = "platform-1",
            StartedAtUtc = startedAt,
        });

        started.PlatformExecutionInFlight.Should().BeTrue();
        started.PlatformExecutionStartedAtUtc.Should().Be(startedAt);
        started.UpdatedAtUtc.Should().Be(startedAt);
    }

    [Fact]
    public async Task HandlePlatformBindingWatchdogFired_WhenInFlightIsFresh_ShouldNotReexecute()
    {
        var state = NewInFlightState(DateTimeOffset.UtcNow.AddSeconds(-10));
        var publisher = new RecordingEventPublisher();
        var agent = NewHandlerAgent(state, publisher);

        await agent.HandlePlatformBindingWatchdogFired(new StudioMemberPlatformBindingWatchdogFired
        {
            BindingRunId = "bind-1",
            PlatformBindingCommandId = "platform-1",
        });

        publisher.SentMessages.Should().BeEmpty();
    }

    [Fact]
    public async Task HandlePlatformBindingWatchdogFired_WhenInFlightIsStale_ShouldReexecuteAsRecovery()
    {
        var state = NewInFlightState(DateTimeOffset.UtcNow.AddMinutes(-3));
        var publisher = new RecordingEventPublisher();
        var agent = NewHandlerAgent(state, publisher);

        await agent.HandlePlatformBindingWatchdogFired(new StudioMemberPlatformBindingWatchdogFired
        {
            BindingRunId = "bind-1",
            PlatformBindingCommandId = "platform-1",
        });

        var retry = publisher.SentMessages.Should().ContainSingle().Subject.Event
            .Should().BeOfType<StudioMemberPlatformBindingExecuteRequested>().Subject;
        retry.BindingRunId.Should().Be("bind-1");
        retry.PlatformBindingCommandId.Should().Be("platform-1");
        retry.RecoveryExecution.Should().BeTrue();
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
        var accepted = NewPlatformPendingState();
        var succeeded = _agent.Apply(accepted, new StudioMemberPlatformBindingSucceeded
        {
            BindingRunId = "bind-1",
            PlatformBindingCommandId = "platform-1",
            CompletedAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow.AddSeconds(1)),
            Result = new StudioMemberPlatformBindingResult
            {
                PublishedServiceId = "member-m-1",
                RevisionId = "rev-1",
                ImplementationKind = StudioMemberImplementationKind.Script,
            },
        });

        var afterFailure = _agent.Apply(succeeded, new StudioMemberPlatformBindingFailed
        {
            BindingRunId = "bind-1",
            PlatformBindingCommandId = "platform-1",
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

    private StudioMemberBindingRunState NewPlatformPendingState()
    {
        var requested = _agent.Apply(new StudioMemberBindingRunState(), NewRequested());
        return _agent.Apply(requested, new StudioMemberPlatformBindingStartRequested
        {
            BindingRunId = "bind-1",
            PlatformBindingCommandId = "platform-1",
            RequestedAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow.AddSeconds(1)),
        });
    }

    private StudioMemberBindingRunState NewInFlightState(DateTimeOffset startedAt)
    {
        var pending = NewPlatformPendingState();
        return _agent.Apply(pending, new StudioMemberPlatformBindingExecutionStarted
        {
            BindingRunId = "bind-1",
            PlatformBindingCommandId = "platform-1",
            StartedAtUtc = Timestamp.FromDateTimeOffset(startedAt),
        });
    }

    private static StudioMemberBindingRunGAgent NewHandlerAgent(
        StudioMemberBindingRunState state,
        RecordingEventPublisher publisher)
    {
        var agent = new StudioMemberBindingRunGAgent
        {
            EventSourcing = new RecordingEventSourcing(state),
            EventPublisher = publisher,
        };
        StudioMemberBindingRunStateSetter.Set(agent, state);
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
    }

    private sealed class RecordingEventSourcing(StudioMemberBindingRunState replayState)
        : IEventSourcingBehavior<StudioMemberBindingRunState>
    {
        public long CurrentVersion => 0;

        public void RaiseEvent<TEvent>(TEvent evt) where TEvent : IMessage
        {
        }

        public Task<EventStoreCommitResult> ConfirmEventsAsync(CancellationToken ct = default) =>
            Task.FromResult(new EventStoreCommitResult());

        public Task PersistSnapshotAsync(StudioMemberBindingRunState currentState, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task<StudioMemberBindingRunState?> ReplayAsync(string agentId, CancellationToken ct = default) =>
            Task.FromResult<StudioMemberBindingRunState?>(replayState.Clone());

        public void DiscardPendingEvents()
        {
        }

        public StudioMemberBindingRunState TransitionState(StudioMemberBindingRunState current, IMessage evt) =>
            current.Clone();
    }

    private sealed class RecordingEventPublisher : IEventPublisher
    {
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
            SentMessages.Add(new SentMessage(targetActorId, evt));
            return Task.CompletedTask;
        }
    }

    private sealed record SentMessage(string TargetActorId, IMessage Event);
}
