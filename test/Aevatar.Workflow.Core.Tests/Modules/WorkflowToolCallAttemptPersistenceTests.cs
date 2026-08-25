using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.EventSourcing;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Core.Execution;
using Aevatar.Workflow.Core.Modules;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Aevatar.Workflow.Core.Tests.Modules;

public sealed class WorkflowToolCallAttemptPersistenceTests
{
    private static readonly DateTimeOffset ObservedAt =
        new(2026, 8, 20, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void BuildNewFacts_ShouldEmitOnceForANewFullAttemptIdentityAndRemainStableForSameAttemptSaves()
    {
        var attemptOne = Pending("run-a", "step-a", "call-a", "exec-a", "continuation-a", 1);
        var authoritative = State(("current", attemptOne));
        var attemptTwo = attemptOne.Clone();
        attemptTwo.Attempt = 2;
        attemptTwo.AttemptPreparationStartedAtUtc = Timestamp.FromDateTimeOffset(
            ObservedAt.AddMilliseconds(-125));
        var other = Pending("run-a", "step-0", "call-z", "exec-z", "continuation-z", 1);
        other.AttemptPreparationStartedAtUtc = Timestamp.FromDateTimeOffset(ObservedAt.AddMilliseconds(-30));
        var incoming = State(
            ("z", attemptTwo),
            ("duplicate", attemptTwo.Clone()),
            ("a", other));

        var facts = WorkflowToolCallAttemptPersistence.BuildNewFacts(
            authoritative,
            incoming,
            "scope-a",
            ObservedAt);

        facts.Select(static fact => (fact.StepId, fact.Attempt)).Should().Equal(
            ("step-0", 1),
            ("step-a", 2));
        facts.Should().OnlyContain(static fact => fact.ScopeId == "scope-a");
        facts.Single(static fact => fact.Attempt == 2).PreparationElapsedMs.Should().Be(125);
        WorkflowToolCallAttemptPersistence.BuildNewFacts(incoming, incoming.Clone(), "scope-a", ObservedAt)
            .Should().BeEmpty("lease saves and same-attempt phase changes retain the same full identity");
    }

    [Fact]
    public void PersistenceContracts_ShouldKeepStableTypedFieldNumbers()
    {
        WorkflowExecutionStateUpsertedEvent.Descriptor
            .FindFieldByName("tool_call_attempt_persistence_facts")!.FieldNumber.Should().Be(3);
        PendingToolCallExecutionState.Descriptor
            .FindFieldByName("attempt_preparation_started_at_utc")!.FieldNumber.Should().Be(26);
        WorkflowToolCallAttemptTimingObservation.Descriptor
            .FindFieldByName("committed_event_id")!.FieldNumber.Should().Be(20);
        WorkflowToolCallAttemptTimingObservation.Descriptor
            .FindFieldByName("committed_state_version")!.FieldNumber.Should().Be(21);

        var factFields = WorkflowToolCallAttemptPersistenceFact.Descriptor.Fields
            .InFieldNumberOrder()
            .Select(static field => (field.Name, field.FieldNumber));
        factFields.Should().Equal(
            ("scope_id", 1),
            ("run_id", 2),
            ("step_id", 3),
            ("call_id", 4),
            ("execution_id", 5),
            ("continuation_id", 6),
            ("attempt", 7),
            ("observed_at_utc", 8),
            ("preparation_elapsed_ms", 9));
    }

    [Fact]
    public void AddAevatarWorkflow_ShouldRegisterPersistenceTelemetryAlongsideRedaction()
    {
        var services = new ServiceCollection();

        services.AddAevatarWorkflow();

        var hookImplementations = services
            .Where(static descriptor =>
                descriptor.ServiceType == typeof(ICommittedStatePublicationHook))
            .Select(static descriptor => descriptor.ImplementationType)
            .ToArray();
        hookImplementations.Should().Contain(typeof(WorkflowRunCommittedStateRedactionHook));
        hookImplementations.Should().Contain(typeof(WorkflowToolCallAttemptPersistenceTelemetryHook));
    }

    [Fact]
    public void AddAevatarWorkflow_WithoutLogging_ShouldResolveCommittedStatePublicationHooks()
    {
        var services = new ServiceCollection();
        services.AddAevatarWorkflow();

        using var provider = services.BuildServiceProvider();

        provider.GetServices<ICommittedStatePublicationHook>()
            .Should().ContainSingle(static hook =>
                hook is WorkflowToolCallAttemptPersistenceTelemetryHook);
    }

    [Fact]
    public void BuildCommittedObservations_ShouldCarryTheCommittedIdentityWithoutChangingMetricCorrelation()
    {
        var observedAt = Timestamp.FromDateTimeOffset(ObservedAt);
        var upserted = new WorkflowExecutionStateUpsertedEvent
        {
            ScopeKey = ToolCallModule.ModuleStateKey,
        };
        upserted.ToolCallAttemptPersistenceFacts.Add(new WorkflowToolCallAttemptPersistenceFact
        {
            ScopeId = "scope-a",
            RunId = "run-a",
            StepId = "step-a",
            CallId = "call-a",
            ExecutionId = "exec-a",
            ContinuationId = "continuation-a",
            Attempt = 2,
            ObservedAtUtc = observedAt,
            PreparationElapsedMs = 125,
        });
        var committed = new StateEvent
        {
            AgentId = "run-a",
            EventId = "event-a",
            Version = 42,
            EventType = WorkflowExecutionStateUpsertedEvent.Descriptor.FullName,
            EventData = Any.Pack(upserted),
        };

        var observation = WorkflowToolCallAttemptPersistence.BuildCommittedObservations(committed)
            .Should().ContainSingle().Subject;

        observation.Waterline.Should().Be(WorkflowToolCallAttemptWaterline.PendingStatePersisted);
        observation.ScopeId.Should().Be("scope-a");
        observation.RunId.Should().Be("run-a");
        observation.StepId.Should().Be("step-a");
        observation.CallId.Should().Be("call-a");
        observation.ExecutionId.Should().Be("exec-a");
        observation.ContinuationId.Should().Be("continuation-a");
        observation.Attempt.Should().Be(2);
        observation.ObservedAtUtc.Should().Be(observedAt);
        observation.PreparationElapsedMs.Should().Be(125);
        observation.CommittedEventId.Should().Be("event-a");
        observation.CommittedStateVersion.Should().Be(42);
    }

    [Fact]
    public async Task TelemetryHook_ShouldSkipSyntheticCommittedStateRepublish()
    {
        var logger = new RecordingLogger<WorkflowToolCallAttemptPersistenceTelemetryHook>();
        var hook = new WorkflowToolCallAttemptPersistenceTelemetryHook(logger);
        var published = Publication(
            CommittedStateRepublish.BuildEventId("run-a", 42),
            version: 42);

        await hook.BeforePublishAsync(new CommittedStatePublicationContext
        {
            ActorId = "run-a",
            ActorType = typeof(WorkflowRunGAgent),
            Published = published,
        }, CancellationToken.None);

        logger.Entries.Should().BeEmpty();
    }

    private static CommittedStateEventPublished Publication(string eventId, long version)
    {
        var upserted = new WorkflowExecutionStateUpsertedEvent
        {
            ScopeKey = ToolCallModule.ModuleStateKey,
        };
        upserted.ToolCallAttemptPersistenceFacts.Add(new WorkflowToolCallAttemptPersistenceFact
        {
            ScopeId = "scope-a",
            RunId = "run-a",
            StepId = "step-a",
            CallId = "call-a",
            ExecutionId = "exec-a",
            ContinuationId = "continuation-a",
            Attempt = 1,
            ObservedAtUtc = Timestamp.FromDateTimeOffset(ObservedAt),
            PreparationElapsedMs = 10,
        });
        return new CommittedStateEventPublished
        {
            StateEvent = new StateEvent
            {
                AgentId = "run-a",
                EventId = eventId,
                Version = version,
                EventType = WorkflowExecutionStateUpsertedEvent.Descriptor.FullName,
                EventData = Any.Pack(upserted),
            },
        };
    }

    private static ToolCallModuleState State(
        params (string Key, PendingToolCallExecutionState Pending)[] entries)
    {
        var state = new ToolCallModuleState();
        foreach (var (key, pending) in entries)
            state.PendingExecutions[key] = pending;
        return state;
    }

    private static PendingToolCallExecutionState Pending(
        string runId,
        string stepId,
        string callId,
        string executionId,
        string continuationId,
        int attempt) =>
        new()
        {
            RunId = runId,
            StepId = stepId,
            CallId = callId,
            ExecutionId = executionId,
            ContinuationId = continuationId,
            Attempt = attempt,
            ExecutionPhase = WorkflowToolCallExecutionPhase.ExecutionPending,
        };

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<IReadOnlyDictionary<string, object?>> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            _ = logLevel;
            _ = eventId;
            _ = formatter;
            Entries.Add(state is IEnumerable<KeyValuePair<string, object?>> values
                ? values.ToDictionary(static value => value.Key, static value => value.Value, StringComparer.Ordinal)
                : new Dictionary<string, object?>(StringComparer.Ordinal));
        }
    }
}
