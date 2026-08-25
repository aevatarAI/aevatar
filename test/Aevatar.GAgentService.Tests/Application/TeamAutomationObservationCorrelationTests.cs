using Aevatar.CQRS.Core.Abstractions.Streaming;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Credentials;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Schedules;
using Aevatar.GAgentService.Application.Schedules;
using FluentAssertions;
using Any = Google.Protobuf.WellKnownTypes.Any;
using StringValue = Google.Protobuf.WellKnownTypes.StringValue;

namespace Aevatar.GAgentService.Tests.Application;

public sealed class TeamAutomationObservationCorrelationTests
{
    private const string BackendFailureDetail = "backend-secret-should-not-escape";

    [Fact]
    public async Task ConcurrentExactBeginDispatches_ShouldConsumeOnlyTheirCorrelatedObservation()
    {
        var projection = new BroadcastingObservationProjection(expectedAttachments: 2);
        var actorPort = new CorrelatedBeginActorPort(projection);
        var service = new ScheduledDispatchApplicationService(
            actorPort,
            new EmptyScheduleQueryPort(),
            new ScheduledDispatchTargetPreparationService(),
            new NoopScheduledDispatchCredentialAdmissionPort(),
            teamOperationObservationPreparation: new ObservationPreparationPort(),
            teamOperationObservationProjection: projection);
        var operation = CreateOperation();

        var results = await Task.WhenAll(
            service.BeginTeamAutomationCredentialOperationAsync(operation),
            service.BeginTeamAutomationCredentialOperationAsync(operation));

        results.Select(result => result.Outcome.OwnsEffectAttempt).Should().BeEquivalentTo([true, false]);
        results.Select(result => result.Outcome.ObservationRequestId)
            .Should().OnlyHaveUniqueItems().And.NotContain(string.Empty);
    }

    [Theory]
    [InlineData(
        TeamAutomationOperationObservationStatus.RejectedConflict,
        typeof(ScheduledDispatchConflictException))]
    [InlineData(
        TeamAutomationOperationObservationStatus.RejectedUnauthorized,
        typeof(UnauthorizedAccessException))]
    [InlineData(
        TeamAutomationOperationObservationStatus.RejectedNotFound,
        typeof(ScheduledDispatchNotFoundException))]
    [InlineData(
        TeamAutomationOperationObservationStatus.RejectedInvalidRequest,
        typeof(InvalidOperationException))]
    public async Task RejectedBeginDispatch_ShouldMapCommittedDispositionWithoutWaiting(
        TeamAutomationOperationObservationStatus status,
        Type expectedExceptionType)
    {
        var projection = new BroadcastingObservationProjection(expectedAttachments: 1);
        var actorPort = new CorrelatedBeginActorPort(
            projection,
            status,
            "team_automation_operation_conflict");
        var service = new ScheduledDispatchApplicationService(
            actorPort,
            new EmptyScheduleQueryPort(),
            new ScheduledDispatchTargetPreparationService(),
            new NoopScheduledDispatchCredentialAdmissionPort(),
            teamOperationObservationPreparation: new ObservationPreparationPort(),
            teamOperationObservationProjection: projection);

        var exception = await Record.ExceptionAsync(() =>
            service.BeginTeamAutomationCredentialOperationAsync(CreateOperation()));

        exception.Should().BeOfType(expectedExceptionType);
    }

    [Fact]
    public async Task CompleteRevocation_ShouldUseCommittedIdempotencyWithoutReadingCurrentProjection()
    {
        var projection = new BroadcastingObservationProjection(expectedAttachments: 1);
        var actorPort = new RevocationActorPort(projection);
        var queryPort = new EmptyScheduleQueryPort();
        var service = new ScheduledDispatchApplicationService(
            actorPort,
            queryPort,
            new ScheduledDispatchTargetPreparationService(),
            new NoopScheduledDispatchCredentialAdmissionPort(),
            teamOperationObservationPreparation: new ObservationPreparationPort(),
            teamOperationObservationProjection: projection);

        var result = await service.CompleteTeamAutomationRevocationAsync(
            "schedule-1",
            new TeamMemberAutomationOwner("scope-1", "member-1", "team-1"),
            "operation-1",
            "idempotency-committed",
            "attempt-1",
            nyxIdRevoked: true,
            vaultRevoked: true,
            errorCode: string.Empty);

        queryPort.GetCallCount.Should().Be(0);
        actorPort.IdempotencyKey.Should().Be("idempotency-committed");
        result.Outcome.IdempotencyKey.Should().Be("idempotency-committed");
        result.Outcome.Stage.Should().Be(TeamAutomationOperationObservationStages.Revocation);
    }

    [Fact]
    public async Task PreparationFailure_ShouldUseStableObservationUnavailableCode()
    {
        var projection = new BroadcastingObservationProjection(expectedAttachments: 1);
        var preparation = new ObservationPreparationPort(
            prepareException: new InvalidOperationException(BackendFailureDetail));
        var service = CreateService(
            new CorrelatedBeginActorPort(projection),
            preparation,
            projection);

        var act = () => service.BeginTeamAutomationCredentialOperationAsync(CreateOperation());

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("team_automation_commit_observation_unavailable");
    }

    [Fact]
    public async Task AttachmentFailure_ShouldUseStableObservationUnavailableCode()
    {
        var projection = new BroadcastingObservationProjection(
            expectedAttachments: 1,
            attachException: new InvalidOperationException(BackendFailureDetail));
        var preparation = new ObservationPreparationPort();
        var service = CreateService(
            new CorrelatedBeginActorPort(projection),
            preparation,
            projection);

        var act = () => service.BeginTeamAutomationCredentialOperationAsync(CreateOperation());

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("team_automation_commit_observation_unavailable");
        preparation.ReleaseCalls.Should().Be(1);
    }

    [Fact]
    public async Task ActorDispatchFailure_ShouldUseStableDispatchRejectedCode()
    {
        var projection = new BroadcastingObservationProjection(expectedAttachments: 1);
        var preparation = new ObservationPreparationPort();
        var service = CreateService(
            new CorrelatedBeginActorPort(
                projection,
                dispatchException: new InvalidOperationException(BackendFailureDetail)),
            preparation,
            projection);

        var act = () => service.BeginTeamAutomationCredentialOperationAsync(CreateOperation());

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("team_automation_dispatch_rejected");
        projection.DetachCalls.Should().Be(1);
        projection.ReleaseCalls.Should().Be(1);
        preparation.ReleaseCalls.Should().Be(1);
    }

    [Fact]
    public async Task ObservationSinkEndingBeforeCorrelatedOutcome_ShouldUseStableEndedCode()
    {
        var projection = new BroadcastingObservationProjection(expectedAttachments: 1);
        var preparation = new ObservationPreparationPort();
        var service = CreateService(
            new CorrelatedBeginActorPort(projection, completeObservationWithoutOutcome: true),
            preparation,
            projection);

        var act = () => service.BeginTeamAutomationCredentialOperationAsync(CreateOperation());

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("team_automation_commit_observation_ended");
    }

    [Fact]
    public async Task DetachFailure_AfterCommittedOutcome_ShouldReturnReceipt_AndContinueCleanup()
    {
        var projection = new BroadcastingObservationProjection(
            expectedAttachments: 1,
            detachException: new InvalidOperationException(BackendFailureDetail));
        var preparation = new ObservationPreparationPort();
        var service = CreateService(
            new CorrelatedBeginActorPort(projection),
            preparation,
            projection);

        var result = await service.BeginTeamAutomationCredentialOperationAsync(CreateOperation());

        result.Outcome.Status.Should().Be(TeamAutomationOperationObservationStatus.Committed);
        result.Outcome.OwnsEffectAttempt.Should().BeTrue();
        projection.ReleaseCalls.Should().Be(1);
        preparation.ReleaseCalls.Should().Be(1);
    }

    [Fact]
    public async Task ProjectionReleaseFailure_AfterCommittedOutcome_ShouldReturnReceipt_AndContinueCleanup()
    {
        var projection = new BroadcastingObservationProjection(
            expectedAttachments: 1,
            releaseException: new InvalidOperationException(BackendFailureDetail));
        var preparation = new ObservationPreparationPort();
        var service = CreateService(
            new CorrelatedBeginActorPort(projection),
            preparation,
            projection);

        var result = await service.BeginTeamAutomationCredentialOperationAsync(CreateOperation());

        result.Outcome.Status.Should().Be(TeamAutomationOperationObservationStatus.Committed);
        result.Outcome.OwnsEffectAttempt.Should().BeTrue();
        projection.DetachCalls.Should().Be(1);
        preparation.ReleaseCalls.Should().Be(1);
    }

    [Fact]
    public async Task PreparationReleaseFailure_AfterCommittedOutcome_ShouldReturnReceipt()
    {
        var projection = new BroadcastingObservationProjection(expectedAttachments: 1);
        var preparation = new ObservationPreparationPort(
            releaseException: new InvalidOperationException(BackendFailureDetail));
        var service = CreateService(
            new CorrelatedBeginActorPort(projection),
            preparation,
            projection);

        var result = await service.BeginTeamAutomationCredentialOperationAsync(CreateOperation());

        result.Outcome.Status.Should().Be(TeamAutomationOperationObservationStatus.Committed);
        result.Outcome.OwnsEffectAttempt.Should().BeTrue();
        projection.DetachCalls.Should().Be(1);
        projection.ReleaseCalls.Should().Be(1);
    }

    [Fact]
    public async Task CallerCancellationDuringObservationRead_ShouldNotBeWrappedOrReplacedByCleanupFailure()
    {
        using var cts = new CancellationTokenSource();
        var projection = new BroadcastingObservationProjection(
            expectedAttachments: 1,
            detachException: new InvalidOperationException(BackendFailureDetail));
        var preparation = new ObservationPreparationPort();
        var service = CreateService(
            new CorrelatedBeginActorPort(
                projection,
                beforeOutcome: cts.Cancel,
                omitOutcome: true),
            preparation,
            projection);

        var act = () => service.BeginTeamAutomationCredentialOperationAsync(CreateOperation(), cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        projection.ReleaseCalls.Should().Be(1);
        preparation.ReleaseCalls.Should().Be(1);
    }

    [Fact]
    public async Task CommittedBusinessRejection_ShouldNotBeWrappedOrReplacedByCleanupFailure()
    {
        var projection = new BroadcastingObservationProjection(
            expectedAttachments: 1,
            detachException: new InvalidOperationException(BackendFailureDetail));
        var preparation = new ObservationPreparationPort();
        var service = CreateService(
            new CorrelatedBeginActorPort(
                projection,
                TeamAutomationOperationObservationStatus.RejectedConflict,
                "team_automation_operation_conflict"),
            preparation,
            projection);

        var act = () => service.BeginTeamAutomationCredentialOperationAsync(CreateOperation());

        await act.Should().ThrowAsync<ScheduledDispatchConflictException>()
            .WithMessage("team_automation_operation_conflict");
        projection.ReleaseCalls.Should().Be(1);
        preparation.ReleaseCalls.Should().Be(1);
    }

    private static ScheduledDispatchApplicationService CreateService(
        IScheduledDispatchActorPort actorPort,
        ITeamAutomationOperationObservationScopeLeasePreparationPort preparation,
        ITeamAutomationOperationObservationProjectionPort projection) =>
        new(
            actorPort,
            new EmptyScheduleQueryPort(),
            new ScheduledDispatchTargetPreparationService(),
            new NoopScheduledDispatchCredentialAdmissionPort(),
            teamOperationObservationPreparation: preparation,
            teamOperationObservationProjection: projection);

    private static TeamAutomationCredentialOperation CreateOperation() =>
        new(
            "schedule-1",
            new TeamMemberAutomationOwner("scope-1", "member-1", "team-1"),
            "operation-1",
            "idempotency-1",
            "permission-1",
            "policy-1",
            TeamAutomationOperationKind.Create,
            new ScheduledCredentialEffectLocator(
                "credential-1",
                "secret-1",
                "scheduled-invocation-agent-key",
                "schedule:schedule-1",
                new ScheduledInvocationAuthorizationOwner("nyxid", "Personal", "owner-1")),
            CreateActivationDecision(),
            "mutation-1");

    private static TeamAutomationActivationDecision CreateActivationDecision()
    {
        var observedAt = new DateTimeOffset(2026, 7, 24, 0, 0, 0, TimeSpan.Zero);
        return new TeamAutomationActivationDecision(
            "schedule-1",
            "Schedule 1",
            new TeamMemberAutomationOwner("scope-1", "member-1", "team-1"),
            new ServiceIdentity
            {
                TenantId = "scope-1",
                AppId = "app-1",
                Namespace = "default",
                ServiceId = "service-1",
            },
            "chat",
            Any.Pack(new StringValue { Value = "payload-1" }),
            new ScheduledCallerNyxIdAuthority
            {
                Platform = "nyxid",
                Tenant = "tenant-1",
                ExternalUserId = "owner-1",
                Scope = "proxy",
                BindingId = "binding-1",
            },
            new ScheduledInvocationAuthorizationFact(
                "permission-1",
                "policy-1",
                new ScheduledInvocationAuthorizationOwner("nyxid", "Personal", "owner-1"),
                [],
                "proxy",
                observedAt.AddHours(1),
                true,
                new ScheduledInvocationAuthorizationDisclosure(true, true, false, true, false),
                new ScheduledInvocationAuthorizationAuthority(
                    1,
                    2,
                    3,
                    4,
                    5,
                    observedAt,
                    observedAt.AddMinutes(30),
                    "catalog-digest-1",
                    "catalog-contract-1",
                    "catalog-policy-1",
                    observedAt)),
            "0 * * * *",
            "UTC",
            false,
            ScheduledDispatchScheduleKind.Workflow,
            new Dictionary<string, string>(StringComparer.Ordinal),
            ScheduledDispatchScheduleMode.RecurringCron,
            null,
            ScheduledDispatchCredentialRequirementTargetKind.WorkflowService,
            string.Empty,
            null);
    }

    private class CorrelatedBeginActorPort(
        BroadcastingObservationProjection projection,
        TeamAutomationOperationObservationStatus status =
            TeamAutomationOperationObservationStatus.Committed,
        string errorCode = "",
        Exception? dispatchException = null,
        bool completeObservationWithoutOutcome = false,
        Action? beforeOutcome = null,
        bool omitOutcome = false)
        : IScheduledDispatchActorPort
    {
        private int _dispatchCount;

        public Task<string> EnsureScheduleActorAsync(string scheduleId, CancellationToken ct = default) =>
            Task.FromResult("scheduled-dispatch:" + scheduleId);

        public Task<string?> ResolveScheduleActorAsync(string scheduleId, CancellationToken ct = default) =>
            Task.FromResult<string?>("scheduled-dispatch:" + scheduleId);

        public async Task<DispatchAdmission> DispatchBeginTeamAutomationCredentialOperationAsync(
            string actorId,
            TeamAutomationCredentialOperation operation,
            string observationRequestId,
            CancellationToken ct = default)
        {
            await projection.WaitUntilReadyAsync(ct);
            if (dispatchException != null)
                throw dispatchException;

            var dispatchNumber = Interlocked.Increment(ref _dispatchCount);
            beforeOutcome?.Invoke();
            if (omitOutcome)
            {
                return new DispatchAdmission(
                    true,
                    "command-" + dispatchNumber,
                    DateTimeOffset.UtcNow,
                    actorId,
                    "correlation-" + dispatchNumber);
            }

            if (completeObservationWithoutOutcome)
            {
                projection.Complete();
                return new DispatchAdmission(
                    true,
                    "command-" + dispatchNumber,
                    DateTimeOffset.UtcNow,
                    actorId,
                    "correlation-" + dispatchNumber);
            }

            projection.Broadcast(new TeamAutomationOperationCommittedOutcome(
                operation.ScheduleId,
                operation.OperationId,
                operation.IdempotencyKey,
                TeamAutomationOperationObservationStages.Begin,
                OwnsEffectAttempt: status == TeamAutomationOperationObservationStatus.Committed &&
                                   dispatchNumber == 1,
                StateVersion: dispatchNumber,
                ErrorCode: errorCode,
                ErrorMessage: string.Empty,
                ObservedAtUtc: DateTimeOffset.UtcNow,
                PendingRevocationCredential: null,
                PendingRevocationOwner: null,
                NyxIdRevocationPending: false,
                VaultRevocationPending: false,
                ObservationRequestId: observationRequestId,
                Status: status));
            return new DispatchAdmission(
                true,
                "command-" + dispatchNumber,
                DateTimeOffset.UtcNow,
                actorId,
                "correlation-" + dispatchNumber);
        }

        public Task<DispatchAdmission> DispatchCreateAsync(
            string actorId,
            ScheduledDispatchConfiguration configuration,
            PreparedScheduledDispatchTarget dispatch,
            CancellationToken ct = default) => throw new NotSupportedException();

        public Task<DispatchAdmission> DispatchUpdateAsync(
            string actorId,
            ScheduledDispatchConfiguration configuration,
            PreparedScheduledDispatchTarget dispatch,
            ScheduledDispatchExpectedServiceTarget? expectedTarget = null,
            CancellationToken ct = default) => throw new NotSupportedException();

        public Task<DispatchAdmission> DispatchEnsureAsync(
            string actorId,
            ScheduledDispatchConfiguration configuration,
            PreparedScheduledDispatchTarget dispatch,
            CancellationToken ct = default) => throw new NotSupportedException();

        public Task<DispatchAdmission> DispatchEnableAsync(
            string actorId,
            string reason,
            ScheduledDispatchExpectedServiceTarget? expectedTarget = null,
            CancellationToken ct = default) => throw new NotSupportedException();

        public Task<DispatchAdmission> DispatchDisableAsync(
            string actorId,
            string reason,
            ScheduledDispatchExpectedServiceTarget? expectedTarget = null,
            CancellationToken ct = default) => throw new NotSupportedException();

        public Task<DispatchAdmission> DispatchDeleteAsync(
            string actorId,
            string reason,
            ScheduledDispatchExpectedServiceTarget? expectedTarget = null,
            CancellationToken ct = default) => throw new NotSupportedException();

        public Task<DispatchAdmission> DispatchRunNowAsync(
            string actorId,
            DateTimeOffset scheduledFireAt,
            ScheduledDispatchExpectedServiceTarget? expectedTarget = null,
            CancellationToken ct = default) =>
            throw new NotSupportedException();

        public virtual Task<DispatchAdmission> DispatchCompleteTeamAutomationRevocationAsync(
            string actorId,
            TeamMemberAutomationOwner owner,
            string operationId,
            string idempotencyKey,
            string effectAttemptId,
            bool nyxIdRevoked,
            bool vaultRevoked,
            string errorCode,
            string observationRequestId,
            CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class RevocationActorPort : CorrelatedBeginActorPort
    {
        private readonly BroadcastingObservationProjection _projection;

        public RevocationActorPort(BroadcastingObservationProjection projection)
            : base(projection)
        {
            _projection = projection;
        }

        public string IdempotencyKey { get; private set; } = string.Empty;

        public override async Task<DispatchAdmission> DispatchCompleteTeamAutomationRevocationAsync(
            string actorId,
            TeamMemberAutomationOwner owner,
            string operationId,
            string idempotencyKey,
            string effectAttemptId,
            bool nyxIdRevoked,
            bool vaultRevoked,
            string errorCode,
            string observationRequestId,
            CancellationToken ct = default)
        {
            IdempotencyKey = idempotencyKey;
            await _projection.WaitUntilReadyAsync(ct);
            _projection.Broadcast(new TeamAutomationOperationCommittedOutcome(
                "schedule-1",
                operationId,
                idempotencyKey,
                TeamAutomationOperationObservationStages.Revocation,
                OwnsEffectAttempt: false,
                StateVersion: 10,
                ErrorCode: errorCode,
                ErrorMessage: string.Empty,
                ObservedAtUtc: DateTimeOffset.UtcNow,
                PendingRevocationCredential: null,
                PendingRevocationOwner: null,
                NyxIdRevocationPending: false,
                VaultRevocationPending: false,
                ObservationRequestId: observationRequestId));
            return new DispatchAdmission(
                true,
                "command-revocation",
                DateTimeOffset.UtcNow,
                actorId,
                "correlation-revocation");
        }
    }

    private sealed class EmptyScheduleQueryPort : IScheduledDispatchQueryPort
    {
        public int GetCallCount { get; private set; }

        public Task<ScheduledDispatchDetail?> GetAsync(string scheduleId, CancellationToken ct = default)
        {
            GetCallCount++;
            return Task.FromResult<ScheduledDispatchDetail?>(null);
        }

        public Task<ScheduledDispatchListResult> ListAsync(
            int take = 50,
            string? cursor = null,
            bool includeTotalCount = false,
            CancellationToken ct = default) => throw new NotSupportedException();

        public Task<ScheduledDispatchListResult> ListAsync(
            ScheduledDispatchListQuery query,
            CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class ObservationPreparationPort(
        Exception? prepareException = null,
        Exception? releaseException = null)
        : ITeamAutomationOperationObservationScopeLeasePreparationPort
    {
        public int ReleaseCalls { get; private set; }

        public Task<TeamAutomationOperationObservationScopeLeasePreparation?> PrepareAsync(
            string actorId,
            string operationId,
            CancellationToken ct = default)
        {
            if (prepareException != null)
                return Task.FromException<TeamAutomationOperationObservationScopeLeasePreparation?>(
                    prepareException);

            return Task.FromResult<TeamAutomationOperationObservationScopeLeasePreparation?>(
                new TeamAutomationOperationObservationScopeLeasePreparation(actorId, operationId));
        }

        public Task ReleaseAsync(
            TeamAutomationOperationObservationScopeLeasePreparation preparation,
            CancellationToken ct = default)
        {
            ReleaseCalls++;
            return releaseException == null
                ? Task.CompletedTask
                : Task.FromException(releaseException);
        }
    }

    private sealed class BroadcastingObservationProjection(
        int expectedAttachments,
        Exception? attachException = null,
        Exception? detachException = null,
        Exception? releaseException = null)
        : ITeamAutomationOperationObservationProjectionPort
    {
        private readonly object _gate = new();
        private readonly List<IEventSink<TeamAutomationOperationCommittedOutcome>> _sinks = [];
        private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool ProjectionEnabled => true;

        public int DetachCalls { get; private set; }

        public int ReleaseCalls { get; private set; }

        public Task WaitUntilReadyAsync(CancellationToken ct) => _ready.Task.WaitAsync(ct);

        public void Complete()
        {
            IEventSink<TeamAutomationOperationCommittedOutcome>[] sinks;
            lock (_gate)
                sinks = _sinks.ToArray();
            foreach (var sink in sinks)
                sink.Complete();
        }

        public void Broadcast(TeamAutomationOperationCommittedOutcome outcome)
        {
            IEventSink<TeamAutomationOperationCommittedOutcome>[] sinks;
            lock (_gate)
                sinks = _sinks.ToArray();
            foreach (var sink in sinks)
            {
                try
                {
                    sink.Push(outcome);
                }
                catch (EventSinkCompletedException)
                {
                }
            }
        }

        public Task<EventSinkProjectionAttachment<ITeamAutomationOperationObservationProjectionLease>?>
            AttachExistingOperationProjectionAsync(
                string actorId,
                string operationId,
                IEventSink<TeamAutomationOperationCommittedOutcome> sink,
                CancellationToken ct = default)
        {
            if (attachException != null)
            {
                return Task.FromException<
                    EventSinkProjectionAttachment<ITeamAutomationOperationObservationProjectionLease>?>(
                    attachException);
            }

            lock (_gate)
            {
                _sinks.Add(sink);
                if (_sinks.Count == expectedAttachments)
                    _ready.TrySetResult();
            }
            var lease = new ObservationLease(actorId, operationId);
            return Task.FromResult<EventSinkProjectionAttachment<ITeamAutomationOperationObservationProjectionLease>?>(
                new EventSinkProjectionAttachment<ITeamAutomationOperationObservationProjectionLease>(
                    lease,
                    new SinkLease(this, sink)));
        }

        public Task<IAsyncDisposable?> AttachLiveSinkAsync(
            ITeamAutomationOperationObservationProjectionLease lease,
            IEventSink<TeamAutomationOperationCommittedOutcome> sink,
            CancellationToken ct = default) => throw new NotSupportedException();

        public Task DetachLiveSinkAsync(IAsyncDisposable? liveSinkLease, CancellationToken ct = default)
        {
            DetachCalls++;
            return detachException != null
                ? Task.FromException(detachException)
                : liveSinkLease?.DisposeAsync().AsTask() ?? Task.CompletedTask;
        }

        public Task ReleaseActorProjectionAsync(
            ITeamAutomationOperationObservationProjectionLease lease,
            CancellationToken ct = default)
        {
            ReleaseCalls++;
            return releaseException == null
                ? Task.CompletedTask
                : Task.FromException(releaseException);
        }

        private void Remove(IEventSink<TeamAutomationOperationCommittedOutcome> sink)
        {
            lock (_gate)
                _sinks.Remove(sink);
        }

        private sealed record ObservationLease(string ActorId, string OperationId)
            : ITeamAutomationOperationObservationProjectionLease;

        private sealed class SinkLease(
            BroadcastingObservationProjection owner,
            IEventSink<TeamAutomationOperationCommittedOutcome> sink) : IAsyncDisposable
        {
            public ValueTask DisposeAsync()
            {
                owner.Remove(sink);
                return ValueTask.CompletedTask;
            }
        }
    }
}
