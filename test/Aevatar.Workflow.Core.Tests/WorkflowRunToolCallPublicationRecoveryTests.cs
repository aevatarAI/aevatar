using System.Diagnostics.Metrics;
using System.Reflection;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Credentials;
using Aevatar.Foundation.Abstractions.EventModules;
using Aevatar.Foundation.Abstractions.EventSourcing;
using Aevatar.Foundation.Abstractions.Hooks;
using Aevatar.Foundation.Abstractions.Persistence;
using Aevatar.Foundation.Abstractions.Runtime.Callbacks;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.Foundation.Runtime.Persistence;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Abstractions.Execution;
using Aevatar.Workflow.Core.Composition;
using Aevatar.Workflow.Core.Execution;
using Aevatar.Workflow.Core.Modules;
using Aevatar.Workflow.Core.Primitives;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Aevatar.Workflow.Core.Tests;

#pragma warning disable CS0612 // Recovery coverage intentionally seeds and inspects legacy payload fields.
public sealed class WorkflowRunToolCallPublicationRecoveryTests
{
    [Fact]
    public async Task Activation_WhenStartIntentWasCommittedBeforeSelfDispatch_ShouldRepublishUntilKernelCheckpoint()
    {
        const string actorId = "run-start-outbox-recovery";
        var store = new InMemoryEventStore();
        var seed = CreateAgent(
            actorId,
            store,
            new RecordingCallbackScheduler(),
            out _,
            out _);
        await seed.ActivateAsync();
        await BindToolWorkflowAsync(seed, actorId);
        var start = new StartWorkflowEvent
        {
            RunId = actorId,
            WorkflowName = "tool_recovery",
            Input = "recover-me",
            BindingGeneration = seed.State.BindingGeneration,
            ValueRepresentation = WorkflowExecutionValueRepresentation.Legacy,
        };
        await PersistForTestAsync(seed, new WorkflowRunExecutionStartedEvent
        {
            RunId = actorId,
            WorkflowName = start.WorkflowName,
            Input = start.Input,
            StartedAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            PendingStartWorkflow = start.Clone(),
        });

        seed.State.PendingStartWorkflow.Should().NotBeNull();

        var recovered = CreateAgent(
            actorId,
            store,
            new RecordingCallbackScheduler(),
            out _,
            out var recoveryPublisher);
        await recovered.ActivateAsync();

        var republished = recoveryPublisher.Published
            .Where(static publication => publication.Audience == TopologyAudience.Self)
            .Select(static publication => publication.Event)
            .OfType<StartWorkflowEvent>()
            .Should().ContainSingle().Subject;
        republished.ToByteString().Should().Equal(start.ToByteString());

        await recovered.HandleEventAsync(EnvelopeFrom(actorId, republished));

        recovered.State.PendingStartWorkflow.Should().BeNull();
        recovered.State.ExecutionStates.Should().ContainKey(WorkflowExecutionKernel.ModuleStateKey);

        var afterCheckpoint = CreateAgent(
            actorId,
            store,
            new RecordingCallbackScheduler(),
            out _,
            out var afterCheckpointPublisher);
        await afterCheckpoint.ActivateAsync();

        afterCheckpointPublisher.Published
            .Select(static publication => publication.Event)
            .OfType<StartWorkflowEvent>()
            .Should().BeEmpty();
    }

    [Fact]
    public async Task PendingAttemptCommit_WhenPublicationHookFails_ShouldRecoverOriginalPersistenceFactOnReactivation()
    {
        const string actorId = "run-tool-attempt-fact-recovery";
        var now = new DateTimeOffset(2026, 8, 20, 12, 0, 0, TimeSpan.Zero);
        var clock = new FakeTimeProvider(now);
        var store = new InMemoryEventStore();
        var publicationStore = new InMemoryCommittedStatePublicationStateStore();
        var telemetryLogger = new RecordingPersistenceLogger();
        var telemetryHook = new WorkflowToolCallAttemptPersistenceTelemetryHook(telemetryLogger);
        var failingHook = new FailOnceCommittedPublicationHook();
        var hooks = new OrderedCommittedPublicationHook(failingHook, telemetryHook);
        var first = CreateAgent(
            actorId,
            store,
            new RecordingCallbackScheduler(),
            out _,
            out _,
            publicationHook: hooks,
            publicationStateStore: publicationStore,
            timeProvider: clock);
        await first.ActivateAsync();
        await BindToolWorkflowAsync(first, actorId);
        var pending = CreatePendingExecution(
            actorId,
            index: 1,
            WorkflowToolCallExecutionPhase.ExecutionPending);
        pending.TimeoutDeadlineUnixMs = now.AddMinutes(5).ToUnixTimeMilliseconds();
        pending.AttemptPreparationStartedAtUtc = Timestamp.FromDateTimeOffset(now.AddMilliseconds(-250));
        var state = new ToolCallModuleState();
        state.PendingExecutions[$"{pending.CallId}|{pending.ExecutionId}"] = pending;
        failingHook.FailNext = true;

        await FluentActions.Awaiting(() =>
                ((IWorkflowExecutionStateHost)first).UpsertExecutionStateAsync(
                    ToolCallModule.ModuleStateKey,
                    Any.Pack(state)))
            .Should().ThrowAsync<CommittedStatePublicationException>();

        var committedBeforeRecovery = await store.GetEventsAsync(actorId);
        var committedFact = committedBeforeRecovery
            .Where(static evt => evt.EventData?.Is(WorkflowExecutionStateUpsertedEvent.Descriptor) == true)
            .SelectMany(static evt => evt.EventData!
                .Unpack<WorkflowExecutionStateUpsertedEvent>()
                .ToolCallAttemptPersistenceFacts
                .Select(fact => (Event: evt, Fact: fact)))
            .Should().ContainSingle().Subject;
        committedFact.Fact.ScopeId.Should().Be("scope-1");
        committedFact.Fact.RunId.Should().Be(actorId);
        committedFact.Fact.StepId.Should().Be(pending.StepId);
        committedFact.Fact.CallId.Should().Be(pending.CallId);
        committedFact.Fact.ExecutionId.Should().Be(pending.ExecutionId);
        committedFact.Fact.ContinuationId.Should().Be(pending.ContinuationId);
        committedFact.Fact.Attempt.Should().Be(1);
        committedFact.Fact.ObservedAtUtc.ToDateTimeOffset().Should().Be(now);
        committedFact.Fact.PreparationElapsedMs.Should().Be(250);
        telemetryLogger.PendingPersistenceEntries.Should().BeEmpty(
            "the injected failure happens after commit but before the telemetry hook runs");

        var recovered = CreateAgent(
            actorId,
            store,
            new RecordingCallbackScheduler(),
            out _,
            out _,
            publicationHook: hooks,
            publicationStateStore: publicationStore,
            timeProvider: clock);
        await recovered.ActivateAsync();

        recovered.State.ExecutionStates[ToolCallModule.ModuleStateKey]
            .Unpack<ToolCallModuleState>()
            .PendingExecutions.Values.Should().ContainSingle()
            .Which.ContinuationId.Should().Be(pending.ContinuationId);
        var allCommittedFacts = (await store.GetEventsAsync(actorId))
            .Where(static evt => evt.EventData?.Is(WorkflowExecutionStateUpsertedEvent.Descriptor) == true)
            .SelectMany(static evt => evt.EventData!
                .Unpack<WorkflowExecutionStateUpsertedEvent>()
                .ToolCallAttemptPersistenceFacts)
            .ToArray();
        allCommittedFacts.Should().ContainSingle()
            .Which.ToByteArray().Should().Equal(committedFact.Fact.ToByteArray());
        telemetryLogger.PendingPersistenceEntries.Should().ContainSingle()
            .And.OnlyContain(entry =>
                Equals(entry["ObservedAtUtc"], now) &&
                Equals(entry["CommittedEventId"], committedFact.Event.EventId) &&
                Equals(entry["CommittedStateVersion"], committedFact.Event.Version) &&
                Equals(entry["ContinuationId"], pending.ContinuationId));
        (await publicationStore.LoadAsync(actorId))!.PublishedVersion
            .Should().BeGreaterThanOrEqualTo(committedFact.Event.Version);
    }

    [Fact]
    public async Task PendingAttemptTelemetry_WhenCheckpointFailsAfterHook_ShouldRetryWithStableCommittedIdentity()
    {
        const string actorId = "run-tool-attempt-telemetry-retry";
        var now = new DateTimeOffset(2026, 8, 20, 13, 0, 0, TimeSpan.Zero);
        var clock = new FakeTimeProvider(now);
        var store = new InMemoryEventStore();
        var publicationStore = new InMemoryCommittedStatePublicationStateStore();
        var failingCheckpointStore = new FailOnceAdvancePublicationStateStore(publicationStore);
        var telemetryLogger = new RecordingPersistenceLogger();
        var telemetryHook = new WorkflowToolCallAttemptPersistenceTelemetryHook(telemetryLogger);
        var first = CreateAgent(
            actorId,
            store,
            new RecordingCallbackScheduler(),
            out _,
            out _,
            publicationHook: telemetryHook,
            publicationStateStore: failingCheckpointStore,
            timeProvider: clock);
        await first.ActivateAsync();
        await BindToolWorkflowAsync(first, actorId);
        failingCheckpointStore.AdvanceAttempts.Clear();

        var pending = CreatePendingExecution(
            actorId,
            index: 1,
            WorkflowToolCallExecutionPhase.ExecutionPending);
        pending.TimeoutDeadlineUnixMs = now.AddMinutes(5).ToUnixTimeMilliseconds();
        pending.AttemptPreparationStartedAtUtc = Timestamp.FromDateTimeOffset(now.AddMilliseconds(-250));
        var state = new ToolCallModuleState();
        state.PendingExecutions[$"{pending.CallId}|{pending.ExecutionId}"] = pending;
        var measurements = new List<MetricMeasurement>();

        using (ListenForWorkflowToolCallMetrics(measurements))
        {
            failingCheckpointStore.FailNext = true;
            var publicationFailure = await FluentActions.Awaiting(() =>
                    ((IWorkflowExecutionStateHost)first).UpsertExecutionStateAsync(
                        ToolCallModule.ModuleStateKey,
                        Any.Pack(state)))
                .Should().ThrowAsync<CommittedStatePublicationException>();
            publicationFailure.Which.Stage.Should().Be(CommittedStatePublicationFailureStage.Checkpoint);

            var recovered = CreateAgent(
                actorId,
                store,
                new RecordingCallbackScheduler(),
                out _,
                out _,
                publicationHook: telemetryHook,
                publicationStateStore: failingCheckpointStore,
                timeProvider: clock);
            await recovered.ActivateAsync();

            recovered.State.ExecutionStates[ToolCallModule.ModuleStateKey]
                .Unpack<ToolCallModuleState>()
                .PendingExecutions.Values.Should().ContainSingle()
                .Which.Attempt.Should().Be(1);
        }

        var committedFact = (await store.GetEventsAsync(actorId))
            .Where(static evt => evt.EventData?.Is(WorkflowExecutionStateUpsertedEvent.Descriptor) == true)
            .SelectMany(static evt => evt.EventData!
                .Unpack<WorkflowExecutionStateUpsertedEvent>()
                .ToolCallAttemptPersistenceFacts
                .Select(fact => (Event: evt, Fact: fact)))
            .Should().ContainSingle().Subject;
        var entries = telemetryLogger.PendingPersistenceEntries;
        entries.Should().HaveCount(2);
        entries.Should().OnlyContain(entry =>
            Equals(entry["CommittedEventId"], committedFact.Event.EventId) &&
            Equals(entry["CommittedStateVersion"], committedFact.Event.Version) &&
            Equals(entry["Attempt"], 1));

        failingCheckpointStore.AdvanceAttempts.Should().HaveCount(2);
        failingCheckpointStore.AdvanceAttempts.Should().OnlyContain(attempt =>
            attempt.EventId == committedFact.Event.EventId &&
            attempt.Version == committedFact.Event.Version);

        measurements.Should().HaveCount(4);
        measurements.Count(measurement =>
                measurement.Instrument == WorkflowToolCallTelemetry.WaterlineTotalMetricName)
            .Should().Be(2);
        measurements.Count(measurement =>
                measurement.Instrument == WorkflowToolCallTelemetry.PhaseDurationMetricName)
            .Should().Be(2);
        var allowedTagKeys = new[]
        {
            WorkflowToolCallTelemetry.WaterlineTag,
            WorkflowToolCallTelemetry.PhaseTag,
            WorkflowToolCallTelemetry.DispositionTag,
            WorkflowToolCallTelemetry.DeliveryMethodTag,
        };
        measurements.Should().OnlyContain(measurement =>
            measurement.Tags.Keys.SequenceEqual(allowedTagKeys) &&
            Equals(measurement.Tags[WorkflowToolCallTelemetry.WaterlineTag], "pending_state_persisted") &&
            Equals(measurement.Tags[WorkflowToolCallTelemetry.PhaseTag], "actor_preparation") &&
            Equals(measurement.Tags[WorkflowToolCallTelemetry.DispositionTag], "none") &&
            Equals(measurement.Tags[WorkflowToolCallTelemetry.DeliveryMethodTag], "none"));
    }

    [Fact]
    public async Task RetryAttemptCommit_WhenPublicationHookFails_ShouldRecoverOneCommittedFactPerAttempt()
    {
        const string actorId = "run-tool-retry-attempt-fact-recovery";
        var now = new DateTimeOffset(2026, 8, 20, 14, 0, 0, TimeSpan.Zero);
        var clock = new FakeTimeProvider(now);
        var store = new InMemoryEventStore();
        var publicationStore = new InMemoryCommittedStatePublicationStateStore();
        var telemetryLogger = new RecordingPersistenceLogger();
        var telemetryHook = new WorkflowToolCallAttemptPersistenceTelemetryHook(telemetryLogger);
        var failingHook = new FailOnceCommittedPublicationHook();
        var hooks = new OrderedCommittedPublicationHook(failingHook, telemetryHook);
        var first = CreateAgent(
            actorId,
            store,
            new RecordingCallbackScheduler(),
            out _,
            out _,
            publicationHook: hooks,
            publicationStateStore: publicationStore,
            timeProvider: clock);
        await first.ActivateAsync();
        await BindToolWorkflowAsync(first, actorId);

        var pending = CreatePendingExecution(
            actorId,
            index: 1,
            WorkflowToolCallExecutionPhase.ExecutionPending);
        pending.TimeoutDeadlineUnixMs = now.AddMinutes(5).ToUnixTimeMilliseconds();
        pending.AttemptPreparationStartedAtUtc = Timestamp.FromDateTimeOffset(now.AddMilliseconds(-250));
        var attemptOneState = new ToolCallModuleState();
        var pendingKey = $"{pending.CallId}|{pending.ExecutionId}";
        attemptOneState.PendingExecutions[pendingKey] = pending;
        await ((IWorkflowExecutionStateHost)first).UpsertExecutionStateAsync(
            ToolCallModule.ModuleStateKey,
            Any.Pack(attemptOneState));

        clock.Advance(TimeSpan.FromSeconds(1));
        var attemptTwoState = attemptOneState.Clone();
        var retryPending = attemptTwoState.PendingExecutions[pendingKey];
        retryPending.Attempt = 2;
        retryPending.ExecutionPhase = WorkflowToolCallExecutionPhase.RetryPending;
        retryPending.AttemptPreparationStartedAtUtc =
            Timestamp.FromDateTimeOffset(clock.GetUtcNow().AddMilliseconds(-125));
        failingHook.FailNext = true;

        var publicationFailure = await FluentActions.Awaiting(() =>
                ((IWorkflowExecutionStateHost)first).UpsertExecutionStateAsync(
                    ToolCallModule.ModuleStateKey,
                    Any.Pack(attemptTwoState)))
            .Should().ThrowAsync<CommittedStatePublicationException>();
        publicationFailure.Which.Stage.Should().Be(CommittedStatePublicationFailureStage.AdapterAcceptance);
        telemetryLogger.PendingPersistenceEntries.Select(ReadAttempt).Should().Equal(1);

        var recovered = CreateAgent(
            actorId,
            store,
            new RecordingCallbackScheduler(),
            out _,
            out _,
            publicationHook: hooks,
            publicationStateStore: publicationStore,
            timeProvider: clock);
        await recovered.ActivateAsync();

        var committedFacts = (await store.GetEventsAsync(actorId))
            .Where(static evt => evt.EventData?.Is(WorkflowExecutionStateUpsertedEvent.Descriptor) == true)
            .SelectMany(static evt => evt.EventData!
                .Unpack<WorkflowExecutionStateUpsertedEvent>()
                .ToolCallAttemptPersistenceFacts
                .Select(fact => (Event: evt, Fact: fact)))
            .OrderBy(static committed => committed.Fact.Attempt)
            .ToArray();
        committedFacts.Select(static committed => committed.Fact.Attempt).Should().Equal(1, 2);
        committedFacts.GroupBy(static committed => committed.Fact.Attempt)
            .Should().OnlyContain(group => group.Count() == 1);

        var entries = telemetryLogger.PendingPersistenceEntries;
        entries.Should().HaveCount(2);
        entries.Select(ReadAttempt).Should().Equal(1, 2);
        var attemptTwoFact = committedFacts.Single(static committed => committed.Fact.Attempt == 2);
        entries.Single(entry => ReadAttempt(entry) == 2).Should().Match<IReadOnlyDictionary<string, object?>>(
            entry =>
                Equals(entry["CommittedEventId"], attemptTwoFact.Event.EventId) &&
                Equals(entry["CommittedStateVersion"], attemptTwoFact.Event.Version));
        recovered.State.ExecutionStates[ToolCallModule.ModuleStateKey]
            .Unpack<ToolCallModuleState>()
            .PendingExecutions.Values.Should().ContainSingle()
            .Which.Attempt.Should().Be(2);
    }

    [Fact]
    public async Task Activation_ShouldSchedulePersistedForEachPublicationForNonTerminalRun()
    {
        const string actorId = "run-foreach-publication-recovery";
        var store = new InMemoryEventStore();
        var seed = CreateAgent(actorId, store, new RecordingCallbackScheduler(), out _, out _);
        await seed.ActivateAsync();
        await BindForEachWorkflowAsync(seed, actorId);
        await ((IWorkflowExecutionStateHost)seed).UpsertExecutionStateAsync(
            ForEachModule.ModuleStateKey,
            Any.Pack(CreatePendingForEachState(actorId)));

        var scheduler = new RecordingCallbackScheduler();
        var recovered = CreateAgent(actorId, store, scheduler, out _, out var publisher);
        await recovered.ActivateAsync();

        recovered.State.Compiled.Should().BeTrue(recovered.State.CompilationError);
        recovered.State.ExecutionStates[ForEachModule.ModuleStateKey]
            .Unpack<ForEachModuleState>()
            .Parents[$"{actorId}:foreach-step"]
            .PendingDispatches.Should().ContainSingle();

        var retry = scheduler.TimeoutRequests
            .Where(request => request.TriggerEnvelope.Payload?.Is(
                ForEachPublicationRetryFiredEvent.Descriptor) == true)
            .Should().ContainSingle().Subject;
        retry.TriggerEnvelope.Payload!.Unpack<ForEachPublicationRetryFiredEvent>()
            .ParentKey.Should().Be($"{actorId}:foreach-step");

        await recovered.HandleEventAsync(retry.TriggerEnvelope);

        var replayed = publisher.Published.Select(item => item.Event).OfType<StepRequestEvent>()
            .Should().ContainSingle().Subject;
        replayed.StepId.Should().Be("foreach-step_item_0");
        replayed.ExecutionId.Should().Be("foreach-child-execution");
        replayed.IdempotencyKey.Should().Be("foreach-child-idempotency");
    }

    [Fact]
    public async Task TerminalActivation_ShouldNotSchedulePersistedForEachPublication()
    {
        const string actorId = "run-foreach-terminal-publication";
        var store = new InMemoryEventStore();
        var seed = CreateAgent(actorId, store, new RecordingCallbackScheduler(), out _, out _);
        await seed.ActivateAsync();
        await BindForEachWorkflowAsync(seed, actorId);
        await ((IWorkflowExecutionStateHost)seed).UpsertExecutionStateAsync(
            ForEachModule.ModuleStateKey,
            Any.Pack(CreatePendingForEachState(actorId)));
        await PersistForTestAsync(seed, new WorkflowCompletedEvent
        {
            RunId = actorId,
            WorkflowName = "tool_recovery",
            Success = true,
            Output = "done",
        });

        var scheduler = new RecordingCallbackScheduler();
        var recovered = CreateAgent(actorId, store, scheduler, out _, out _);
        await recovered.ActivateAsync();

        recovered.State.Status.Should().Be("completed");
        scheduler.TimeoutRequests
            .Where(request => request.TriggerEnvelope.Payload?.Is(
                ForEachPublicationRetryFiredEvent.Descriptor) == true)
            .Should().BeEmpty();
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task ForEachChildCompletion_ShouldAvoidKernelCheckpointAndPreserveObservatoryArtifact(
        int itemCount)
    {
        const string actorId = "run-foreach-child-checkpoint";
        var store = new InMemoryEventStore();
        var agent = CreateAgent(
            actorId,
            store,
            new RecordingCallbackScheduler(),
            out _,
            out var publisher);
        await agent.ActivateAsync();
        await BindForEachWorkflowAsync(agent, actorId);

        await agent.HandleEventAsync(EnvelopeFrom(actorId, new StartWorkflowEvent
        {
            RunId = actorId,
            WorkflowName = "foreach_recovery",
            Input = string.Join("\n---\n", Enumerable.Range(0, itemCount).Select(static index => $"item-{index}")),
        }));
        var parentRequest = publisher.Published
            .Select(static publication => publication.Event)
            .OfType<StepRequestEvent>()
            .Should().ContainSingle(request => request.StepId == "foreach-step")
            .Subject;
        publisher.Published.Clear();

        await agent.HandleEventAsync(EnvelopeFrom(actorId, parentRequest));
        var childRequests = publisher.Published
            .Select(static publication => publication.Event)
            .OfType<StepRequestEvent>()
            .OrderBy(static request => request.StepId, StringComparer.Ordinal)
            .ToArray();
        childRequests.Should().HaveCount(itemCount);
        publisher.Published.Clear();

        var child = childRequests[0];
        var completion = new StepCompletedEvent
        {
            RunId = actorId,
            StepId = child.StepId,
            ExecutionId = child.ExecutionId,
            Success = true,
            Output = "child-output-0",
        };
        var versionBeforeCompletion = await store.GetVersionAsync(actorId);

        await agent.HandleEventAsync(EnvelopeFrom(actorId, completion));

        var committed = await store.GetEventsAsync(actorId, versionBeforeCompletion);
        var executionStateUpserts = committed
            .Where(static item => item.EventData?.Is(WorkflowExecutionStateUpsertedEvent.Descriptor) == true)
            .Select(static item => item.EventData!.Unpack<WorkflowExecutionStateUpsertedEvent>())
            .ToArray();
        var expectedForEachCheckpoints = itemCount == 1 ? 2 : 1;
        executionStateUpserts.Should().HaveCount(expectedForEachCheckpoints)
            .And.OnlyContain(upsert => upsert.ScopeKey == ForEachModule.ModuleStateKey);
        committed.Should().HaveCount(expectedForEachCheckpoints + 1,
            "schema-v0 children remain foreach-owned artifacts and do not checkpoint the kernel");
        (await store.GetVersionAsync(actorId))
            .Should().Be(versionBeforeCompletion + expectedForEachCheckpoints + 1);

        var observableCompletion = committed
            .Where(static item => item.EventData?.Is(StepCompletedEvent.Descriptor) == true)
            .Select(static item => item.EventData!.Unpack<StepCompletedEvent>())
            .Should().ContainSingle().Subject;
        observableCompletion.Should().BeEquivalentTo(completion);
        agent.State.ProcessedStepCompletionKeys.Should().ContainSingle();

        var forEachState = agent.State.ExecutionStates[ForEachModule.ModuleStateKey]
            .Unpack<ForEachModuleState>();
        if (itemCount == 1)
        {
            forEachState.Parents.Should().BeEmpty();
            forEachState.CompletionTombstones.Should().ContainSingle();
            forEachState.CompletedChildOutputs.Should().Contain(child.StepId, completion.Output);
            publisher.Published
                .Select(static publication => publication.Event)
                .OfType<StepCompletedEvent>()
                .Should().ContainSingle(parentCompletion => parentCompletion.StepId == "foreach-step");
        }
        else
        {
            var parent = forEachState.Parents.Values.Should().ContainSingle().Subject;
            parent.Collected.Should().ContainSingle(result =>
                result.StepId == child.StepId && result.Output == completion.Output);
            publisher.Published
                .Select(static publication => publication.Event)
                .OfType<StepCompletedEvent>()
                .Should().BeEmpty();
        }

        agent.State.ExecutionStates[WorkflowExecutionKernel.ModuleStateKey]
            .Unpack<WorkflowExecutionKernelState>()
            .Variables.Should().NotContainKey(child.StepId);
    }

    [Fact]
    public async Task Reconciliation_ShouldCompleteActiveForEachWithPersistedFailedChild()
    {
        const string actorId = "run-foreach-stranded-failure";
        var store = new InMemoryEventStore();
        var scheduler = new RecordingCallbackScheduler();
        var agent = CreateAgent(actorId, store, scheduler, out _, out var publisher);
        await agent.ActivateAsync();
        await BindForEachWorkflowAsync(agent, actorId);
        await PersistForTestAsync(agent, new WorkflowRunExecutionStartedEvent
        {
            RunId = actorId,
            WorkflowName = "foreach_recovery",
            Input = "alpha\n---\nbeta",
            StartedAtUtc = Timestamp.FromDateTime(DateTime.UtcNow),
        });

        await agent.HandleEventAsync(EnvelopeFrom(actorId, new StartWorkflowEvent
        {
            RunId = actorId,
            WorkflowName = "foreach_recovery",
            Input = "alpha\n---\nbeta",
        }));
        var parentRequest = publisher.Published
            .Select(static publication => publication.Event)
            .OfType<StepRequestEvent>()
            .Should().ContainSingle(request => request.StepId == "foreach-step")
            .Subject;
        publisher.Published.Clear();
        await agent.HandleEventAsync(EnvelopeFrom(actorId, parentRequest));
        var childRequests = publisher.Published
            .Select(static publication => publication.Event)
            .OfType<StepRequestEvent>()
            .OrderBy(static request => request.StepId, StringComparer.Ordinal)
            .ToArray();
        childRequests.Should().HaveCount(2);

        var strandedState = agent.State.ExecutionStates[ForEachModule.ModuleStateKey]
            .Unpack<ForEachModuleState>();
        var parent = strandedState.Parents.Values.Should().ContainSingle().Subject;
        parent.PendingDispatches.Clear();
        parent.DispatchedStepIds.Clear();
        parent.DispatchedStepIds.Add(childRequests.Select(static request => request.StepId));
        parent.Collected.Add(new ForEachItemResult
        {
            StepId = childRequests[0].StepId,
            Index = 0,
            Success = false,
            Error = "historical child failure",
        });
        parent.CollectedStepIds.Add(childRequests[0].StepId);
        parent.SettledWorkerStepIds.Add(childRequests[0].StepId);
        strandedState.Backpressure.Queue.Clear();
        strandedState.Backpressure.HeadIndex = 0;
        strandedState.Backpressure.ActiveWorkers = 1;
        await ((IWorkflowExecutionStateHost)agent).UpsertExecutionStateAsync(
            ForEachModule.ModuleStateKey,
            Any.Pack(strandedState));
        var activeKernel = agent.State.ExecutionStates[WorkflowExecutionKernel.ModuleStateKey]
            .Unpack<WorkflowExecutionKernelState>();
        activeKernel.Active.Should().BeTrue();
        activeKernel.RunId.Should().Be(actorId);
        activeKernel.CurrentStepId.Should().Be("foreach-step");
        activeKernel.CurrentStepDispatchPending.Should().BeFalse();
        activeKernel.ExecutionIdsByStepId["foreach-step"].Should().Be(parentRequest.ExecutionId);
        parent.ParentExecutionId.Should().Be(parentRequest.ExecutionId);
        var reconciliationProbe = agent.State.ExecutionStates[ForEachModule.ModuleStateKey]
            .Unpack<ForEachModuleState>();
        ForEachModule.TryPrepareFailedParentCompletion(
                reconciliationProbe,
                agent.State.ExecutionStates[WorkflowExecutionKernel.ModuleStateKey]
                    .Unpack<WorkflowExecutionKernelState>(),
                actorId,
                "foreach-step",
                parentRequest.ExecutionId,
                0,
                out _,
                out var probeChanged,
                out _,
                out _)
            .Should().BeTrue();
        probeChanged.Should().BeTrue();
        publisher.Published.Clear();

        await agent.HandleEventAsync(EnvelopeFrom(
            "workflow.run.terminal-recovery",
            new ReconcileWorkflowTerminalStateCommand
            {
                RunId = actorId,
                ObservedStateVersion = 1550,
            }));

        var pending = agent.State.ExecutionStates[ForEachModule.ModuleStateKey]
            .Unpack<ForEachModuleState>()
            .Parents.Values.Should().ContainSingle().Subject.PendingCompletion;
        pending.Should().NotBeNull();
        pending.ExecutionId.Should().Be(parentRequest.ExecutionId);
        pending.Success.Should().BeFalse();
        pending.FailureOutcome.Should().Be(WorkflowStepFailureOutcome.OutcomeUncertain);
        pending.RetryDisposition.Should().Be(WorkflowStepRetryDisposition.Forbidden);

        var recovery = scheduler.TimeoutRequests
            .Where(request => request.TriggerEnvelope.Payload?.Is(
                ForEachPublicationRetryFiredEvent.Descriptor) == true)
            .Should().ContainSingle().Subject;
        await agent.HandleEventAsync(recovery.TriggerEnvelope);
        var parentCompletion = publisher.Published
            .Select(static publication => publication.Event)
            .OfType<StepCompletedEvent>()
            .Should().ContainSingle(completion => completion.StepId == "foreach-step")
            .Subject;

        publisher.Published.Clear();
        await agent.HandleEventAsync(EnvelopeFrom(actorId, parentCompletion));
        var workflowCompletion = publisher.Published
            .Select(static publication => publication.Event)
            .OfType<WorkflowCompletedEvent>()
            .Should().ContainSingle().Subject;
        workflowCompletion.Success.Should().BeFalse();

        await agent.HandleEventAsync(EnvelopeFrom(actorId, workflowCompletion));
        agent.State.Status.Should().Be("failed");
        agent.State.FinalError.Should().Contain(ForEachModule.FailedItemsError);
    }

    [Fact]
    public async Task Activation_ShouldScheduleAndDrainPersistedCompletionOutboxWithoutExecutingTool()
    {
        const string actorId = "run-tool-completion-recovery";
        var store = new InMemoryEventStore();
        var seed = CreateAgent(actorId, store, new RecordingCallbackScheduler(), out _, out _);
        await seed.ActivateAsync();
        await BindToolWorkflowAsync(seed, actorId);
        await ((IWorkflowExecutionStateHost)seed).UpsertExecutionStateAsync(
            ToolCallModule.ModuleStateKey,
            Any.Pack(new ToolCallModuleState
            {
                Completions =
                {
                    new WorkflowToolCallCompletionOutboxEntry
                    {
                        RunId = actorId,
                        StepId = "tool-step",
                        CallId = $"workflow:{actorId}:tool-step:exec-1",
                        ExecutionId = "exec-1",
                        TerminalDecision = WorkflowToolCallTerminalDecision.NoApproval,
                        ToolCompletion = new WorkflowToolCallCompletedEvent
                        {
                            RunId = actorId,
                            StepId = "tool-step",
                            CallId = $"workflow:{actorId}:tool-step:exec-1",
                            Success = true,
                            ResultJson = "{}",
                        },
                        StepCompletion = new StepCompletedEvent
                        {
                            RunId = actorId,
                            StepId = "tool-step",
                            ExecutionId = "exec-1",
                            Success = true,
                            Output = "{}",
                        },
                    },
                },
            }));

        var scheduler = new RecordingCallbackScheduler();
        var recovered = CreateAgent(actorId, store, scheduler, out var tool, out var publisher);
        await recovered.ActivateAsync();

        var scheduled = scheduler.TimeoutRequests.Should().ContainSingle().Subject;
        var retry = scheduled.TriggerEnvelope.Payload!
            .Unpack<WorkflowToolCallPublicationRetryFiredEvent>();
        retry.PublicationKind.Should().Be(WorkflowToolCallPublicationKind.Completion);
        retry.RunId.Should().Be(actorId);
        retry.StepId.Should().Be("tool-step");
        retry.ExecutionId.Should().Be("exec-1");

        await recovered.HandleEventAsync(scheduled.TriggerEnvelope);

        tool.ExecuteCalls.Should().Be(0);
        publisher.Published.Select(x => x.Event).OfType<WorkflowToolCallCompletedEvent>().Should().ContainSingle();
        publisher.Published.Select(x => x.Event).OfType<StepCompletedEvent>().Should().ContainSingle();
        var state = recovered.State.ExecutionStates[ToolCallModule.ModuleStateKey].Unpack<ToolCallModuleState>();
        state.Completions.Should().BeEmpty();
        state.CompletionTombstones.Should().ContainSingle();
    }

    [Fact]
    public async Task Activation_ShouldRebuildAndDrainLegacyApprovalSuspensionWithoutExecutingTool()
    {
        const string actorId = "run-tool-suspension-recovery";
        const string legacyPayloadMarker = "legacy-tool-payload-must-not-be-rewritten";
        var store = new InMemoryEventStore();
        var callId = $"workflow:{actorId}:tool-step:exec-1";
        var pending = new PendingToolCallApprovalState
        {
            RunId = actorId,
            StepId = "tool-step",
            ExecutionId = "exec-1",
            ToolName = "counting_tool",
            ToolCallId = callId,
            ApprovalRequestId = "approval-1",
            ArgumentsJson = legacyPayloadMarker,
            Input = legacyPayloadMarker,
            IdempotencyKey = legacyPayloadMarker,
            DisplayName = legacyPayloadMarker,
            ExternalInvocation = new ExternalToolInvocationSpec
            {
                CallSiteId = legacyPayloadMarker,
                ToolName = legacyPayloadMarker,
            },
            TimeoutMs = 60_000,
            TimeoutDeadlineUnixMs = DateTimeOffset.UtcNow.AddMinutes(1).ToUnixTimeMilliseconds(),
            ContinuationId = "continuation-1",
            ExecutionPhase = WorkflowToolCallExecutionPhase.ApprovalPending,
            InputFileRefs =
            {
                new WorkflowFileRef { FileId = legacyPayloadMarker },
            },
        };
        var seededState = new ToolCallModuleState();
        seededState.PendingApprovals[$"{actorId}:tool-step:exec-1:{callId}:approval-1"] = pending;
        var legacyExecution = CreatePendingExecution(
            actorId,
            2,
            WorkflowToolCallExecutionPhase.Unspecified);
        legacyExecution.ArgumentsJson = legacyPayloadMarker;
        legacyExecution.IdempotencyKey = legacyPayloadMarker;
        legacyExecution.DisplayName = legacyPayloadMarker;
        legacyExecution.ExternalInvocation = new ExternalToolInvocationSpec
        {
            CallSiteId = legacyPayloadMarker,
            ToolName = legacyPayloadMarker,
        };
        legacyExecution.InputFileRefs.Add(new WorkflowFileRef { FileId = legacyPayloadMarker });
        legacyExecution.TimeoutLease = new WorkflowRuntimeCallbackLeaseState
        {
            ActorId = actorId,
            CallbackId = legacyExecution.TimeoutCallbackId,
            Generation = 1,
            Backend = WorkflowRuntimeCallbackBackendState.Dedicated,
        };
        seededState.PendingExecutions[$"{legacyExecution.CallId}|{legacyExecution.ExecutionId}"] =
            legacyExecution;
        var seed = CreateAgent(actorId, store, new RecordingCallbackScheduler(), out _, out _);
        await seed.ActivateAsync();
        await BindToolWorkflowAsync(seed, actorId);
        await PersistForTestAsync(seed, new WorkflowExecutionStateUpsertedEvent
        {
            ScopeKey = ToolCallModule.ModuleStateKey,
            State = Any.Pack(seededState),
        });

        var scheduler = new RecordingCallbackScheduler();
        var recovered = CreateAgent(actorId, store, scheduler, out var tool, out var publisher);
        await recovered.ActivateAsync();

        var scheduled = scheduler.TimeoutRequests
            .Where(request => request.TriggerEnvelope.Payload?.Is(
                WorkflowToolCallPublicationRetryFiredEvent.Descriptor) == true)
            .Should().ContainSingle().Subject;
        var retry = scheduled.TriggerEnvelope.Payload!
            .Unpack<WorkflowToolCallPublicationRetryFiredEvent>();
        retry.PublicationKind.Should().Be(WorkflowToolCallPublicationKind.Suspension);
        retry.ApprovalRequestId.Should().Be("approval-1");
        var approvalWatchdog = scheduler.TimeoutRequests
            .Where(request => request.TriggerEnvelope.Payload?.Is(
                WorkflowToolCallTimeoutFiredEvent.Descriptor) == true)
            .Should().ContainSingle().Subject;
        approvalWatchdog.TriggerEnvelope.Payload!
            .Unpack<WorkflowToolCallTimeoutFiredEvent>()
            .ContinuationId.Should().Be("continuation-1");

        var recoveredToolState = recovered.State.ExecutionStates[ToolCallModule.ModuleStateKey]
            .Unpack<ToolCallModuleState>();
        var recoveredPending = recoveredToolState.PendingApprovals.Values.Should().ContainSingle().Subject;
        recoveredPending.TimeoutCallbackId.Should().Be(approvalWatchdog.CallbackId);
        recoveredPending.TimeoutLease.Should().NotBeNull();
        recoveredPending.TimeoutLease.CallbackId.Should().Be(approvalWatchdog.CallbackId);
        AssertLegacyPayloadFieldsScrubbed(recoveredToolState);

        var persistedToolStates = (await store.GetEventsAsync(actorId))
            .Where(evt => evt.EventData?.Is(WorkflowExecutionStateUpsertedEvent.Descriptor) == true)
            .Select(evt => evt.EventData!.Unpack<WorkflowExecutionStateUpsertedEvent>())
            .Where(evt =>
                evt.ScopeKey == ToolCallModule.ModuleStateKey &&
                evt.State?.Is(ToolCallModuleState.Descriptor) == true)
            .Select(evt => evt.State!.Unpack<ToolCallModuleState>())
            .ToList();
        persistedToolStates.Should().HaveCountGreaterThan(1);
        persistedToolStates[0].PendingApprovals.Values.Single().ArgumentsJson
            .Should().Be(legacyPayloadMarker, "the fixture must contain a real legacy journal payload");
        AssertLegacyPayloadFieldsScrubbed(persistedToolStates[^1]);

        await recovered.HandleEventAsync(scheduled.TriggerEnvelope);

        tool.ExecuteCalls.Should().Be(0);
        publisher.Published.Select(x => x.Event).OfType<WorkflowSuspendedEvent>().Should().ContainSingle();
        recovered.State.ExecutionStates[ToolCallModule.ModuleStateKey]
            .Unpack<ToolCallModuleState>()
            .PendingApprovals.Values.Should().ContainSingle()
            .Which.Should().Match<PendingToolCallApprovalState>(value =>
                value.SuspensionPublished &&
                value.Suspension != null &&
                value.Suspension.ToolApproval.ApprovalRequestId == "approval-1");
    }

    [Fact]
    public async Task Activation_WhenApprovalChangesDuringWatchdogSchedule_ShouldCancelOrphanLease()
    {
        const string actorId = "run-tool-approval-watchdog-orphan";
        var store = new InMemoryEventStore();
        var seed = CreateAgent(actorId, store, new RecordingCallbackScheduler(), out _, out _);
        await seed.ActivateAsync();
        await BindToolWorkflowAsync(seed, actorId);
        await ((IWorkflowExecutionStateHost)seed).UpsertExecutionStateAsync(
            ToolCallModule.ModuleStateKey,
            Any.Pack(CreatePendingApprovalToolState(actorId)));

        WorkflowRunGAgent? recovered = null;
        var stateRemoved = false;
        var scheduler = new RecordingCallbackScheduler(
            afterSchedule: async (request, ct) =>
            {
                if (stateRemoved ||
                    request.TriggerEnvelope.Payload?.Is(WorkflowToolCallTimeoutFiredEvent.Descriptor) != true)
                {
                    return;
                }

                stateRemoved = true;
                await ((IWorkflowExecutionStateHost)recovered!).ClearExecutionStateAsync(
                    ToolCallModule.ModuleStateKey,
                    ct);
            });
        recovered = CreateAgent(actorId, store, scheduler, out var tool, out _);

        await recovered.ActivateAsync();

        var watchdog = scheduler.TimeoutRequests.Should().ContainSingle().Subject;
        scheduler.CancelledLeases.Should().ContainSingle().Which.CallbackId.Should().Be(watchdog.CallbackId);
        recovered.State.ExecutionStates.Should().NotContainKey(ToolCallModule.ModuleStateKey);
        tool.ExecuteCalls.Should().Be(0);
    }

    [Fact]
    public async Task Activation_WhenApprovalWatchdogCheckpointPublicationFails_ShouldKeepCommittedLease()
    {
        const string actorId = "run-tool-approval-watchdog-committed";
        var store = new InMemoryEventStore();
        var seed = CreateAgent(actorId, store, new RecordingCallbackScheduler(), out _, out _);
        await seed.ActivateAsync();
        await BindToolWorkflowAsync(seed, actorId);
        await ((IWorkflowExecutionStateHost)seed).UpsertExecutionStateAsync(
            ToolCallModule.ModuleStateKey,
            Any.Pack(CreatePendingApprovalToolState(actorId)));

        var scheduler = new RecordingCallbackScheduler();
        var hook = new FailOnceCommittedPublicationHook { FailNext = true };
        var recovered = CreateAgent(
            actorId,
            store,
            scheduler,
            out var tool,
            out _,
            publicationHook: hook);

        await FluentActions.Awaiting(() => recovered.ActivateAsync())
            .Should().ThrowAsync<CommittedStatePublicationException>();

        var watchdog = scheduler.TimeoutRequests.Should().ContainSingle().Subject;
        scheduler.CancelledLeases.Should().BeEmpty();
        var pending = recovered.State.ExecutionStates[ToolCallModule.ModuleStateKey]
            .Unpack<ToolCallModuleState>()
            .PendingApprovals.Values.Should().ContainSingle().Subject;
        pending.TimeoutCallbackId.Should().Be(watchdog.CallbackId);
        pending.TimeoutLease.Should().NotBeNull();
        pending.TimeoutLease.CallbackId.Should().Be(watchdog.CallbackId);
        tool.ExecuteCalls.Should().Be(0);
    }

    [Fact]
    public async Task Activation_ShouldPublishSingleTypedContinuation_WhenRecoverySchedulingFails()
    {
        const string actorId = "run-tool-activation-scheduler-failure";
        var store = new InMemoryEventStore();
        var seed = CreateAgent(actorId, store, new RecordingCallbackScheduler(), out _, out _);
        await seed.ActivateAsync();
        await BindToolWorkflowAsync(seed, actorId);
        await ((IWorkflowExecutionStateHost)seed).UpsertExecutionStateAsync(
            ToolCallModule.ModuleStateKey,
            Any.Pack(new ToolCallModuleState
            {
                Completions =
                {
                    new WorkflowToolCallCompletionOutboxEntry
                    {
                        RunId = actorId,
                        StepId = "tool-step",
                        CallId = $"workflow:{actorId}:tool-step:exec-1",
                        ExecutionId = "exec-1",
                        TerminalDecision = WorkflowToolCallTerminalDecision.NoApproval,
                        ToolCompletion = new WorkflowToolCallCompletedEvent
                        {
                            RunId = actorId,
                            StepId = "tool-step",
                            CallId = $"workflow:{actorId}:tool-step:exec-1",
                            Success = true,
                            ResultJson = "{}",
                        },
                        StepCompletion = new StepCompletedEvent
                        {
                            RunId = actorId,
                            StepId = "tool-step",
                            ExecutionId = "exec-1",
                            Success = true,
                            Output = "{}",
                        },
                    },
                },
            }));

        var scheduler = new RecordingCallbackScheduler(failSchedule: true);
        var recovered = CreateAgent(actorId, store, scheduler, out var tool, out var publisher);
        await recovered.ActivateAsync();

        scheduler.ScheduleAttempts.Should().Be(1);
        publisher.Published.Select(x => x.Event)
            .OfType<WorkflowToolCallPublicationRetryFiredEvent>()
            .Should().ContainSingle();
        tool.ExecuteCalls.Should().Be(0);
        publisher.Published.Select(x => x.Event).OfType<WorkflowToolCallCompletedEvent>().Should().BeEmpty();
        publisher.Published.Select(x => x.Event).OfType<StepCompletedEvent>().Should().BeEmpty();
        var state = recovered.State.ExecutionStates[ToolCallModule.ModuleStateKey].Unpack<ToolCallModuleState>();
        state.Completions.Should().ContainSingle();
        state.CompletionTombstones.Should().BeEmpty();
    }

    [Fact]
    public async Task RuntimePublicationRetry_WithPendingToolState_ShouldRestoreAllActorLocalContinuationsInOrder()
    {
        const string actorId = "run-tool-runtime-recovery";
        var store = new InMemoryEventStore();
        var scheduler = new RecordingCallbackScheduler();
        var hook = new FailOnceCommittedPublicationHook();
        var agent = CreateAgent(actorId, store, scheduler, out var tool, out _, publicationHook: hook);
        await agent.ActivateAsync();
        await BindToolWorkflowAsync(agent, actorId);

        hook.FailNext = true;
        var pendingState = CreateRecoverableToolState(actorId);
        var publicationFailure = await FluentActions.Awaiting(() =>
                ((IWorkflowExecutionStateHost)agent).UpsertExecutionStateAsync(
                    ToolCallModule.ModuleStateKey,
                    Any.Pack(pendingState)))
            .Should().ThrowAsync<CommittedStatePublicationException>();
        publicationFailure.Which.Stage.Should().Be(CommittedStatePublicationFailureStage.AdapterAcceptance);
        scheduler.TimeoutRequests.Clear();

        await agent.HandleEventAsync(CreatePublicationRetryEnvelope(
            actorId,
            new WorkflowToolCallExecutionRecoveryFiredEvent()));

        var scheduledPayloads = scheduler.TimeoutRequests
            .Select(static request => request.TriggerEnvelope.Payload)
            .ToArray();
        scheduledPayloads.Should().HaveCount(5);
        scheduledPayloads.Take(2).Should().OnlyContain(payload =>
            payload != null && payload.Is(WorkflowToolCallTimeoutFiredEvent.Descriptor));
        scheduledPayloads[2]!.Is(WorkflowToolCallRetryFiredEvent.Descriptor).Should().BeTrue();
        scheduledPayloads[3]!.Is(WorkflowToolCallExecutionRecoveryFiredEvent.Descriptor).Should().BeTrue();
        scheduledPayloads[4]!.Is(WorkflowToolCallPublicationRetryFiredEvent.Descriptor).Should().BeTrue();
        scheduler.TimeoutRequests.Should().OnlyContain(request => request.ActorId == actorId);
        hook.FailureCount.Should().Be(1);
        tool.ExecuteCalls.Should().Be(0);
        agent.GetModules().Should().NotBeEmpty();
    }

    [Fact]
    public async Task BindPublicationRetry_ShouldRebuildCompiledDefinitionAndExecutionModules()
    {
        const string actorId = "run-tool-bind-publication-recovery";
        var store = new InMemoryEventStore();
        var hook = new FailOnceCommittedPublicationHook { FailNext = true };
        var agent = CreateAgent(
            actorId,
            store,
            new RecordingCallbackScheduler(),
            out _,
            out _,
            publicationHook: hook);
        await agent.ActivateAsync();
        var bind = CreateBindEvent(actorId);
        var original = EnvelopeFrom("workflow-run-actor-port", bind);

        await FluentActions.Awaiting(() => agent.HandleEventAsync(original))
            .Should().ThrowAsync<CommittedStatePublicationException>();

        agent.State.Compiled.Should().BeTrue();
        agent.State.WorkflowYaml.Should().Be(bind.WorkflowYaml);
        GetCompiledWorkflow(agent).Should().BeNull();
        agent.GetModules().Should().BeEmpty();

        await agent.HandleEventAsync(CreatePublicationRetryEnvelope(original));

        GetCompiledWorkflow(agent).Should().NotBeNull();
        GetCompiledWorkflow(agent)!.Name.Should().Be("tool_recovery");
        agent.GetModules().Should().Contain(module => module.Name == "workflow_execution_bridge");
        agent.State.WorkflowYaml.Should().Be(bind.WorkflowYaml);
        hook.FailureCount.Should().Be(1);
    }

    [Fact]
    public async Task DynamicBindPublicationRetry_ShouldDrainCommittedStartContinuationExactlyOnce()
    {
        const string actorId = "run-tool-dynamic-bind-recovery";
        const string replacementInput = "replacement-input";
        var store = new InMemoryEventStore();
        var hook = new FailOnceCommittedPublicationHook();
        var agent = CreateAgent(
            actorId,
            store,
            new RecordingCallbackScheduler(),
            out _,
            out var publisher,
            publicationHook: hook);
        await agent.ActivateAsync();
        await BindToolWorkflowAsync(agent, actorId);
        hook.FailNext = true;
        var original = EnvelopeFrom("workflow-runtime", new ReplaceWorkflowDefinitionAndExecuteEvent
        {
            WorkflowYaml = """
                           name: tool_replacement
                           roles: []
                           steps:
                             - id: tool-step
                               type: tool_call
                               parameters:
                                 tool: counting_tool
                           """,
            Input = replacementInput,
        });

        await FluentActions.Awaiting(() => agent.HandleEventAsync(original))
            .Should().ThrowAsync<CommittedStatePublicationException>();

        agent.State.Status.Should().Be("bound");
        agent.State.PendingDefinitionBindingContinuation.Should().NotBeNull();
        publisher.Published.Select(static item => item.Event)
            .OfType<StartWorkflowEvent>().Should().BeEmpty();

        await agent.HandleEventAsync(CreatePublicationRetryEnvelope(original));

        agent.State.Status.Should().Be("running");
        agent.State.Input.Should().Be(replacementInput);
        agent.State.PendingDefinitionBindingContinuation.Should().BeNull();
        GetCompiledWorkflow(agent).Should().NotBeNull();
        GetCompiledWorkflow(agent)!.Name.Should().Be("tool_replacement");
        publisher.Published.Select(static item => item.Event)
            .OfType<StartWorkflowEvent>().Should().ContainSingle()
            .Which.Input.Should().Be(replacementInput);
        var events = await store.GetEventsAsync(actorId);
        events.Count(evt => evt.EventData?.Is(BindWorkflowRunDefinitionEvent.Descriptor) == true)
            .Should().Be(2);
        events.Count(evt => evt.EventData?.Is(WorkflowRunExecutionStartedEvent.Descriptor) == true)
            .Should().Be(1);
        hook.FailureCount.Should().Be(1);
    }

    [Fact]
    public async Task DynamicBind_WithPendingToolState_ShouldRejectWithoutClearingExecution()
    {
        const string actorId = "run-tool-dynamic-bind-pending";
        var store = new InMemoryEventStore();
        var secretStore = new RecordingRuntimeSecretStore();
        var agent = CreateAgent(
            actorId,
            store,
            new RecordingCallbackScheduler(),
            out _,
            out var publisher,
            runtimeSecretStore: secretStore);
        await agent.ActivateAsync();
        await BindToolWorkflowAsync(agent, actorId);
        var pending = CreateTerminalToolState(
            actorId,
            CreateProtectedReference("material-dynamic-active", actorId, 1));
        await ((IWorkflowExecutionStateHost)agent).UpsertExecutionStateAsync(
            ToolCallModule.ModuleStateKey,
            Any.Pack(pending));
        var workflowYamlBefore = agent.State.WorkflowYaml;

        await agent.HandleEventAsync(EnvelopeFrom(
            "workflow-runtime",
            new ReplaceWorkflowDefinitionAndExecuteEvent
            {
                WorkflowYaml = """
                               name: rejected_replacement
                               roles: []
                               steps:
                                 - id: tool-step
                                   type: tool_call
                                   parameters:
                                     tool: counting_tool
                               """,
                Input = "must-not-start",
            }));

        agent.State.WorkflowYaml.Should().Be(workflowYamlBefore);
        agent.State.ExecutionStates[ToolCallModule.ModuleStateKey]
            .Unpack<ToolCallModuleState>().Should().BeEquivalentTo(pending);
        agent.State.PendingDefinitionBindingContinuation.Should().BeNull();
        secretStore.RevokeRequests.Should().BeEmpty();
        publisher.Published.Select(static item => item.Event)
            .OfType<StartWorkflowEvent>().Should().BeEmpty();
        publisher.Published.Select(static item => item.Event)
            .OfType<WorkflowLlmInvocationCompletedEvent>().Should().ContainSingle()
            .Which.Success.Should().BeFalse();
        (await store.GetEventsAsync(actorId))
            .Count(evt => evt.EventData?.Is(BindWorkflowRunDefinitionEvent.Descriptor) == true)
            .Should().Be(1);
    }

    [Fact]
    public async Task Bind_WithPendingDefinitionContinuation_ShouldRejectBeforeCommitWithoutOverwritingContinuation()
    {
        const string actorId = "run-tool-bind-continuation-pending";
        var store = new InMemoryEventStore();
        var hook = new FailOnceCommittedPublicationHook { FailNext = true };
        var agent = CreateAgent(
            actorId,
            store,
            new RecordingCallbackScheduler(),
            out _,
            out _,
            publicationHook: hook);
        await agent.ActivateAsync();
        var original = EnvelopeFrom("workflow-run-actor-port", CreateBindEvent(actorId));

        await FluentActions.Awaiting(() => agent.HandleEventAsync(original))
            .Should().ThrowAsync<CommittedStatePublicationException>();

        agent.State.PendingDefinitionBindingContinuation.Should().NotBeNull();
        var pending = agent.State.PendingDefinitionBindingContinuation!.Clone();
        var eventCountBefore = (await store.GetEventsAsync(actorId)).Count;

        await FluentActions.Awaiting(() => BindToolWorkflowAsync(agent, actorId))
            .Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*definition binding cleanup is pending*");

        agent.State.PendingDefinitionBindingContinuation.Should().BeEquivalentTo(pending);
        (await store.GetEventsAsync(actorId)).Should().HaveCount(eventCountBefore);
    }

    [Fact]
    public async Task DynamicBind_WithPendingDefinitionContinuation_ShouldRejectWithoutOverwritingContinuation()
    {
        const string actorId = "run-tool-dynamic-bind-continuation-pending";
        var store = new InMemoryEventStore();
        var hook = new FailOnceCommittedPublicationHook { FailNext = true };
        var agent = CreateAgent(
            actorId,
            store,
            new RecordingCallbackScheduler(),
            out _,
            out var publisher,
            publicationHook: hook);
        await agent.ActivateAsync();

        await FluentActions.Awaiting(() => agent.HandleEventAsync(
                EnvelopeFrom("workflow-run-actor-port", CreateBindEvent(actorId))))
            .Should().ThrowAsync<CommittedStatePublicationException>();

        agent.State.PendingDefinitionBindingContinuation.Should().NotBeNull();
        var pending = agent.State.PendingDefinitionBindingContinuation!.Clone();
        var eventCountBefore = (await store.GetEventsAsync(actorId)).Count;

        await agent.HandleEventAsync(EnvelopeFrom(
            "workflow-runtime",
            new ReplaceWorkflowDefinitionAndExecuteEvent
            {
                WorkflowYaml = """
                               name: rejected_replacement
                               roles: []
                               steps:
                                 - id: tool-step
                                   type: tool_call
                                   parameters:
                                     tool: counting_tool
                               """,
                Input = "must-not-start",
            }));

        agent.State.PendingDefinitionBindingContinuation.Should().BeEquivalentTo(pending);
        (await store.GetEventsAsync(actorId)).Should().HaveCount(eventCountBefore);
        publisher.Published.Select(static item => item.Event)
            .OfType<StartWorkflowEvent>().Should().BeEmpty();
        publisher.Published.Select(static item => item.Event)
            .OfType<WorkflowLlmInvocationCompletedEvent>().Should().ContainSingle()
            .Which.Success.Should().BeFalse();
    }

    [Fact]
    public async Task Bind_WithPendingToolState_ShouldFailBeforeCommitWithoutRevokingActiveMaterial()
    {
        const string actorId = "run-tool-bind-pending";
        var store = new InMemoryEventStore();
        var secretStore = new RecordingRuntimeSecretStore();
        var agent = CreateAgent(
            actorId,
            store,
            new RecordingCallbackScheduler(),
            out _,
            out _,
            runtimeSecretStore: secretStore);
        await agent.ActivateAsync();
        await ((IWorkflowExecutionStateHost)agent).UpsertExecutionStateAsync(
            ToolCallModule.ModuleStateKey,
            Any.Pack(CreateTerminalToolState(actorId, CreateProtectedReference("material-active", actorId, 1))));

        await FluentActions.Awaiting(() => BindToolWorkflowAsync(agent, actorId))
            .Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*tool call cleanup is pending*");

        secretStore.RevokeRequests.Should().BeEmpty();
        agent.State.WorkflowYaml.Should().BeEmpty();
        agent.State.ExecutionStates.Should().ContainKey(ToolCallModule.ModuleStateKey);
        (await store.GetEventsAsync(actorId)).Should().ContainSingle();
    }

    [Theory]
    [InlineData("completed")]
    [InlineData("stopped")]
    [InlineData("run-stopped")]
    public async Task TerminalPublicationRetry_ShouldCleanupPendingToolStateAndDisableModules(string terminalKind)
    {
        const string actorId = "run-tool-terminal-publication-recovery";
        var store = new InMemoryEventStore();
        var scheduler = new RecordingCallbackScheduler();
        var secretStore = new RecordingRuntimeSecretStore();
        var hook = new FailOnceCommittedPublicationHook();
        var agent = CreateAgent(
            actorId,
            store,
            scheduler,
            out _,
            out _,
            runtimeSecretStore: secretStore,
            publicationHook: hook);
        await agent.ActivateAsync();
        await BindToolWorkflowAsync(agent, actorId);
        await ((IWorkflowExecutionStateHost)agent).UpsertExecutionStateAsync(
            ToolCallModule.ModuleStateKey,
            Any.Pack(CreateTerminalToolState(actorId, CreateProtectedReference("material-terminal", actorId, 1))));
        hook.FailNext = true;
        var terminal = terminalKind switch
        {
            "completed" => new WorkflowCompletedEvent
            {
                RunId = actorId,
                WorkflowName = "tool_recovery",
                Success = true,
                Output = "done",
            },
            "stopped" => new WorkflowStoppedEvent
            {
                RunId = actorId,
                WorkflowName = "tool_recovery",
                Reason = "operator stop",
            },
            _ => (IMessage)new WorkflowRunStoppedEvent
            {
                RunId = actorId,
                Reason = "runtime stop",
            },
        };
        var original = EnvelopeFrom(actorId, terminal);

        await FluentActions.Awaiting(() => agent.HandleEventAsync(original))
            .Should().ThrowAsync<CommittedStatePublicationException>();

        agent.State.Status.Should().Be(terminalKind == "completed" ? "completed" : "stopped");
        agent.State.ExecutionStates.Should().ContainKey(ToolCallModule.ModuleStateKey);
        scheduler.CancelledLeases.Should().BeEmpty();
        secretStore.RevokeRequests.Should().BeEmpty();
        agent.GetModules().Should().NotBeEmpty();

        await agent.HandleEventAsync(CreatePublicationRetryEnvelope(original));

        scheduler.CancelledLeases.Select(static lease => lease.CallbackId)
            .Should().BeEquivalentTo("timeout-1", "retry-1");
        secretStore.RevokeRequests.Should().ContainSingle(request => request.Ref == "material-terminal");
        agent.State.ExecutionStates.Should().NotContainKey(ToolCallModule.ModuleStateKey);
        agent.GetModules().Should().BeEmpty();
    }

    [Fact]
    public async Task TerminalCleanup_ShouldCancelPendingApprovalWatchdog()
    {
        const string actorId = "run-tool-terminal-approval-watchdog";
        const string callbackId = "approval-timeout-1";
        var store = new InMemoryEventStore();
        var scheduler = new RecordingCallbackScheduler();
        var agent = CreateAgent(actorId, store, scheduler, out var tool, out var publisher);
        await agent.ActivateAsync();
        await BindToolWorkflowAsync(agent, actorId);
        var toolState = CreatePendingApprovalToolState(actorId);
        var pending = toolState.PendingApprovals.Values.Should().ContainSingle().Subject;
        pending.TimeoutCallbackId = callbackId;
        pending.TimeoutLease = new WorkflowRuntimeCallbackLeaseState
        {
            ActorId = actorId,
            CallbackId = callbackId,
            Generation = 7,
            SlotEpoch = 11,
            Backend = WorkflowRuntimeCallbackBackendState.Dedicated,
        };
        await ((IWorkflowExecutionStateHost)agent).UpsertExecutionStateAsync(
            ToolCallModule.ModuleStateKey,
            Any.Pack(toolState));

        await agent.HandleEventAsync(EnvelopeFrom(actorId, new WorkflowCompletedEvent
        {
            RunId = actorId,
            WorkflowName = "tool_recovery",
            Success = true,
            Output = "done",
        }));

        var cancelled = scheduler.CancelledLeases.Should().ContainSingle().Subject;
        cancelled.ActorId.Should().Be(actorId);
        cancelled.CallbackId.Should().Be(callbackId);
        cancelled.Generation.Should().Be(7);
        cancelled.SlotEpoch.Should().Be(11);
        agent.State.ExecutionStates.Should().NotContainKey(ToolCallModule.ModuleStateKey);
        agent.State.Status.Should().Be("completed");
        agent.GetModules().Should().BeEmpty();
        tool.ExecuteCalls.Should().Be(0);
        publisher.Published.Select(static item => item.Event)
            .OfType<StepCompletedEvent>().Should().BeEmpty();
    }

    [Fact]
    public async Task AdoptedTerminalPublicationRetry_ShouldCleanupLocalToolStateWithoutReexecutingAdoption()
    {
        const string actorId = "run-tool-adopted-publication-recovery";
        var store = new InMemoryEventStore();
        var scheduler = new RecordingCallbackScheduler();
        var secretStore = new RecordingRuntimeSecretStore();
        var hook = new FailOnceCommittedPublicationHook();
        var agent = CreateAgent(
            actorId,
            store,
            scheduler,
            out _,
            out _,
            runtimeSecretStore: secretStore,
            publicationHook: hook);
        await agent.ActivateAsync();
        await BindToolWorkflowAsync(agent, actorId);
        await PersistForTestAsync(agent, new WorkflowRunExecutionStartedEvent
        {
            RunId = actorId,
            WorkflowName = "tool_recovery",
            ScopeId = "scope-1",
            StartedAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
        });
        await ((IWorkflowExecutionStateHost)agent).UpsertExecutionStateAsync(
            ToolCallModule.ModuleStateKey,
            Any.Pack(CreateTerminalToolState(actorId, CreateProtectedReference("material-adopted", actorId, 1))));
        hook.FailNext = true;
        var original = EnvelopeFrom("inner-workflow-executor", new WorkflowCompletedEvent
        {
            RunId = actorId,
            WorkflowName = "tool_recovery",
            Success = false,
            Error = "inner failure",
        });

        await FluentActions.Awaiting(() => agent.HandleEventAsync(original))
            .Should().ThrowAsync<CommittedStatePublicationException>();
        agent.State.Status.Should().Be("failed");
        agent.State.ExecutionStates.Should().ContainKey(ToolCallModule.ModuleStateKey);

        await agent.HandleEventAsync(CreatePublicationRetryEnvelope(original));

        secretStore.RevokeRequests.Should().ContainSingle(request => request.Ref == "material-adopted");
        scheduler.CancelledLeases.Should().HaveCount(2);
        agent.State.ExecutionStates.Should().NotContainKey(ToolCallModule.ModuleStateKey);
        agent.GetModules().Should().BeEmpty();
        (await store.GetEventsAsync(actorId))
            .Count(evt => evt.EventData?.Is(WorkflowCompletedEvent.Descriptor) == true)
            .Should().Be(1);
    }

    [Fact]
    public async Task TerminalCleanup_WhenOnlySomeRevocationsSucceed_ShouldPersistFailedReferencesForActivationRetry()
    {
        const string actorId = "run-tool-partial-revocation";
        var store = new InMemoryEventStore();
        var secretStore = new RecordingRuntimeSecretStore();
        secretStore.FailReferences.Add("material-b");
        var agent = CreateAgent(
            actorId,
            store,
            new RecordingCallbackScheduler(),
            out _,
            out _,
            runtimeSecretStore: secretStore);
        await agent.ActivateAsync();
        await BindToolWorkflowAsync(agent, actorId);
        await ((IWorkflowExecutionStateHost)agent).UpsertExecutionStateAsync(
            ToolCallModule.ModuleStateKey,
            Any.Pack(CreateTerminalToolState(
                actorId,
                CreateProtectedReference("material-a", actorId, 1),
                CreateProtectedReference("material-b", actorId, 2))));

        await agent.HandleWorkflowCompleted(new WorkflowCompletedEvent
        {
            RunId = actorId,
            WorkflowName = "tool_recovery",
            Success = true,
            Output = "done",
        });

        var retained = agent.State.ExecutionStates[ToolCallModule.ModuleStateKey]
            .Unpack<ToolCallModuleState>();
        retained.PendingExecutions.Values.Should().ContainSingle(pending =>
            pending.ProtectedMaterialReference != null &&
            pending.ProtectedMaterialReference.Ref == "material-b");
        retained.PendingExecutions.Values.Should().ContainSingle(pending =>
            pending.ProtectedMaterialReference == null);
        agent.GetModules().Should().BeEmpty();

        secretStore.FailReferences.Clear();
        var recovered = CreateAgent(
            actorId,
            store,
            new RecordingCallbackScheduler(),
            out _,
            out _,
            runtimeSecretStore: secretStore);
        await recovered.ActivateAsync();

        secretStore.RevokeRequests.Count(request => request.Ref == "material-a").Should().Be(1);
        secretStore.RevokeRequests.Count(request => request.Ref == "material-b").Should().Be(2);
        recovered.State.ExecutionStates.Should().NotContainKey(ToolCallModule.ModuleStateKey);
        recovered.GetModules().Should().BeEmpty();
    }

    [Fact]
    public async Task TerminalCleanup_WhenCompletionOutboxRevocationFails_ShouldRetainHandleForActivationRetry()
    {
        const string actorId = "run-tool-outbox-revocation";
        var store = new InMemoryEventStore();
        var secretStore = new RecordingRuntimeSecretStore();
        secretStore.FailReferences.Add("material-outbox");
        var agent = CreateAgent(
            actorId,
            store,
            new RecordingCallbackScheduler(),
            out _,
            out _,
            runtimeSecretStore: secretStore);
        await agent.ActivateAsync();
        await BindToolWorkflowAsync(agent, actorId);
        var toolState = new ToolCallModuleState
        {
            Completions =
            {
                new WorkflowToolCallCompletionOutboxEntry
                {
                    RunId = actorId,
                    StepId = "tool-step",
                    CallId = $"workflow:{actorId}:tool-step:exec-outbox",
                    ExecutionId = "exec-outbox",
                    TerminalDecision = WorkflowToolCallTerminalDecision.NoApproval,
                    ProtectedMaterialReference = CreateProtectedReference("material-outbox", actorId, 1),
                },
            },
        };
        await ((IWorkflowExecutionStateHost)agent).UpsertExecutionStateAsync(
            ToolCallModule.ModuleStateKey,
            Any.Pack(toolState));

        await agent.HandleWorkflowCompleted(new WorkflowCompletedEvent
        {
            RunId = actorId,
            WorkflowName = "tool_recovery",
            Success = true,
            Output = "done",
        });

        var retained = agent.State.ExecutionStates[ToolCallModule.ModuleStateKey]
            .Unpack<ToolCallModuleState>();
        retained.Completions.Should().ContainSingle()
            .Which.ProtectedMaterialReference!.Ref.Should().Be("material-outbox");
        agent.GetModules().Should().BeEmpty();

        secretStore.FailReferences.Clear();
        var recovered = CreateAgent(
            actorId,
            store,
            new RecordingCallbackScheduler(),
            out _,
            out _,
            runtimeSecretStore: secretStore);
        await recovered.ActivateAsync();

        secretStore.RevokeRequests.Count(request => request.Ref == "material-outbox").Should().Be(2);
        recovered.State.ExecutionStates.Should().NotContainKey(ToolCallModule.ModuleStateKey);
        recovered.GetModules().Should().BeEmpty();
    }

    [Fact]
    public async Task TerminalCleanup_WhenRevocationFailsTransiently_ShouldRetryWithinSameActivation()
    {
        const string actorId = "run-tool-terminal-cleanup-retry";
        const string materialReference = "material-transient";
        var store = new InMemoryEventStore();
        var scheduler = new RecordingCallbackScheduler();
        var secretStore = new RecordingRuntimeSecretStore();
        secretStore.ThrowReferences.Add(materialReference);
        var agent = CreateAgent(
            actorId,
            store,
            scheduler,
            out _,
            out _,
            runtimeSecretStore: secretStore);
        await agent.ActivateAsync();
        await BindToolWorkflowAsync(agent, actorId);
        await ((IWorkflowExecutionStateHost)agent).UpsertExecutionStateAsync(
            ToolCallModule.ModuleStateKey,
            Any.Pack(CreateTerminalToolState(
                actorId,
                CreateProtectedReference(materialReference, actorId, 1))));

        await agent.HandleWorkflowCompleted(new WorkflowCompletedEvent
        {
            RunId = actorId,
            WorkflowName = "tool_recovery",
            Success = true,
            Output = "done",
        });

        agent.State.ExecutionStates.Should().ContainKey(ToolCallModule.ModuleStateKey);
        var scheduled = scheduler.TimeoutRequests.Should().ContainSingle().Subject;
        scheduled.CallbackId.Should().Contain("workflow-tool-terminal-cleanup-retry");
        scheduled.TriggerEnvelope.Payload!
            .Unpack<WorkflowToolCallTerminalCleanupRetryFiredEvent>()
            .RunId.Should().Be(actorId);

        secretStore.ThrowReferences.Clear();
        await agent.HandleEventAsync(scheduled.TriggerEnvelope);

        secretStore.RevokeRequests.Count(request => request.Ref == materialReference).Should().Be(2);
        secretStore.ResolveRequests.Count(request => request.Ref == materialReference).Should().Be(1);
        agent.State.ExecutionStates.Should().NotContainKey(ToolCallModule.ModuleStateKey);
        agent.GetModules().Should().BeEmpty();
    }

    [Fact]
    public async Task TerminalCleanup_WhenProtectedMaterialIsAlreadyUnavailable_ShouldClearStateWithoutRetry()
    {
        const string actorId = "run-tool-terminal-cleanup-unavailable";
        const string materialReference = "material-unavailable";
        var store = new InMemoryEventStore();
        var scheduler = new RecordingCallbackScheduler();
        var secretStore = new RecordingRuntimeSecretStore();
        secretStore.UnavailableReferences.Add(materialReference);
        var agent = CreateAgent(
            actorId,
            store,
            scheduler,
            out _,
            out _,
            runtimeSecretStore: secretStore);
        await agent.ActivateAsync();
        await BindToolWorkflowAsync(agent, actorId);
        await ((IWorkflowExecutionStateHost)agent).UpsertExecutionStateAsync(
            ToolCallModule.ModuleStateKey,
            Any.Pack(CreateTerminalToolState(
                actorId,
                CreateProtectedReference(materialReference, actorId, 1))));

        await agent.HandleWorkflowCompleted(new WorkflowCompletedEvent
        {
            RunId = actorId,
            WorkflowName = "tool_recovery",
            Success = true,
            Output = "done",
        });

        secretStore.RevokeRequests.Should().ContainSingle(request => request.Ref == materialReference);
        secretStore.ResolveRequests.Should().ContainSingle(request => request.Ref == materialReference);
        scheduler.TimeoutRequests.Should().BeEmpty();
        agent.State.ExecutionStates.Should().NotContainKey(ToolCallModule.ModuleStateKey);
        agent.GetModules().Should().BeEmpty();
    }

    [Fact]
    public async Task TerminalCleanup_WhenRetrySchedulingFails_ShouldPublishTypedSelfContinuation()
    {
        const string actorId = "run-tool-terminal-cleanup-scheduler-failure";
        const string materialReference = "material-scheduler-failure";
        var store = new InMemoryEventStore();
        var scheduler = new RecordingCallbackScheduler(failSchedule: true);
        var secretStore = new RecordingRuntimeSecretStore();
        secretStore.ThrowReferences.Add(materialReference);
        var agent = CreateAgent(
            actorId,
            store,
            scheduler,
            out _,
            out var publisher,
            runtimeSecretStore: secretStore);
        await agent.ActivateAsync();
        await BindToolWorkflowAsync(agent, actorId);
        await ((IWorkflowExecutionStateHost)agent).UpsertExecutionStateAsync(
            ToolCallModule.ModuleStateKey,
            Any.Pack(CreateTerminalToolState(
                actorId,
                CreateProtectedReference(materialReference, actorId, 1))));

        await agent.HandleWorkflowCompleted(new WorkflowCompletedEvent
        {
            RunId = actorId,
            WorkflowName = "tool_recovery",
            Success = true,
            Output = "done",
        });

        scheduler.ScheduleAttempts.Should().Be(1);
        publisher.Published
            .Where(item =>
                item.Audience == TopologyAudience.Self &&
                item.Event is WorkflowToolCallTerminalCleanupRetryFiredEvent)
            .Should().ContainSingle()
            .Which.Event.Should().BeOfType<WorkflowToolCallTerminalCleanupRetryFiredEvent>()
            .Which.RunId.Should().Be(actorId);
        agent.State.ExecutionStates.Should().ContainKey(ToolCallModule.ModuleStateKey);
    }

    [Fact]
    public async Task TerminalCleanup_WhenTypedFallbackCannotScheduleAgain_ShouldNotRepublishImmediateContinuation()
    {
        const string actorId = "run-tool-terminal-cleanup-fallback-exhausted";
        const string materialReference = "material-fallback-exhausted";
        var store = new InMemoryEventStore();
        var scheduler = new RecordingCallbackScheduler(failSchedule: true);
        var secretStore = new RecordingRuntimeSecretStore();
        secretStore.ThrowReferences.Add(materialReference);
        var agent = CreateAgent(
            actorId,
            store,
            scheduler,
            out _,
            out var publisher,
            runtimeSecretStore: secretStore);
        await agent.ActivateAsync();
        await BindToolWorkflowAsync(agent, actorId);
        await ((IWorkflowExecutionStateHost)agent).UpsertExecutionStateAsync(
            ToolCallModule.ModuleStateKey,
            Any.Pack(CreateTerminalToolState(
                actorId,
                CreateProtectedReference(materialReference, actorId, 1))));

        await agent.HandleWorkflowCompleted(new WorkflowCompletedEvent
        {
            RunId = actorId,
            WorkflowName = "tool_recovery",
            Success = true,
            Output = "done",
        });
        var fallback = publisher.Published
            .Select(static item => item.Event)
            .OfType<WorkflowToolCallTerminalCleanupRetryFiredEvent>()
            .Should().ContainSingle().Subject;

        var failure = await FluentActions.Awaiting(() =>
                agent.HandleEventAsync(EnvelopeFrom(actorId, fallback)))
            .Should().ThrowAsync<WorkflowDurablePublicationPendingException>();

        failure.Which.Should().BeAssignableTo<IRuntimeEnvelopeRetryableException>();
        scheduler.ScheduleAttempts.Should().Be(2);
        publisher.Published.Select(static item => item.Event)
            .OfType<WorkflowToolCallTerminalCleanupRetryFiredEvent>()
            .Should().ContainSingle();
        agent.State.ExecutionStates.Should().ContainKey(ToolCallModule.ModuleStateKey);
    }

    [Fact]
    public async Task TerminalCleanup_WhenCompletedEnvelopeIsRedelivered_ShouldFinishWithoutRepeatingTerminalSideEffects()
    {
        const string actorId = "run-tool-terminal-cleanup-redelivery";
        const string materialReference = "material-terminal-cleanup-redelivery";
        var store = new InMemoryEventStore();
        var scheduler = new RecordingCallbackScheduler(failSchedule: true);
        var secretStore = new RecordingRuntimeSecretStore();
        secretStore.ThrowReferences.Add(materialReference);
        var agent = CreateAgent(
            actorId,
            store,
            scheduler,
            out _,
            out var publisher,
            runtimeSecretStore: secretStore);
        await agent.ActivateAsync();
        await BindToolWorkflowAsync(agent, actorId);
        await ((IWorkflowExecutionStateHost)agent).UpsertExecutionStateAsync(
            ToolCallModule.ModuleStateKey,
            Any.Pack(CreateTerminalToolState(
                actorId,
                CreateProtectedReference(materialReference, actorId, 1))));
        publisher.FailNextPublishType = typeof(WorkflowToolCallTerminalCleanupRetryFiredEvent);
        var original = EnvelopeFrom(actorId, new WorkflowCompletedEvent
        {
            RunId = actorId,
            WorkflowName = "tool_recovery",
            Success = true,
            Output = "done",
        });

        var firstFailure = await FluentActions.Awaiting(() => agent.HandleEventAsync(original))
            .Should().ThrowAsync<WorkflowDurablePublicationPendingException>();

        firstFailure.Which.Should().BeAssignableTo<IRuntimeEnvelopeRetryableException>();
        agent.State.Status.Should().Be("completed");
        agent.State.ExecutionStates.Should().ContainKey(ToolCallModule.ModuleStateKey);
        agent.GetModules().Should().BeEmpty();
        publisher.Published.Count(item =>
                item.Audience == TopologyAudience.Parent &&
                item.Event is WorkflowCompletedEvent)
            .Should().Be(1);
        publisher.Published.Count(item =>
                item.Audience == TopologyAudience.Parent &&
                item.Event is WorkflowLlmInvocationCompletedEvent)
            .Should().Be(1);
        (await store.GetEventsAsync(actorId))
            .Count(evt => evt.EventData?.Is(WorkflowCompletedEvent.Descriptor) == true)
            .Should().Be(1);

        secretStore.ThrowReferences.Clear();
        var redelivery = original.Clone();
        redelivery.EnsureRuntime().Retry = new EnvelopeRetryContext
        {
            OriginEventId = original.Id,
            Attempt = 1,
            LastErrorType = nameof(WorkflowDurablePublicationPendingException),
        };
        await agent.HandleEventAsync(redelivery);

        secretStore.RevokeRequests.Count(request => request.Ref == materialReference).Should().Be(2);
        agent.State.ExecutionStates.Should().NotContainKey(ToolCallModule.ModuleStateKey);
        agent.GetModules().Should().BeEmpty();
        publisher.Published.Count(item =>
                item.Audience == TopologyAudience.Parent &&
                item.Event is WorkflowCompletedEvent)
            .Should().Be(1);
        publisher.Published.Count(item =>
                item.Audience == TopologyAudience.Parent &&
                item.Event is WorkflowLlmInvocationCompletedEvent)
            .Should().Be(1);
        publisher.Published.Select(static item => item.Event)
            .OfType<WorkflowToolCallTerminalCleanupRetryFiredEvent>()
            .Should().BeEmpty();
        (await store.GetEventsAsync(actorId))
            .Count(evt => evt.EventData?.Is(WorkflowCompletedEvent.Descriptor) == true)
            .Should().Be(1);
    }

    [Fact]
    public async Task TerminalActivationRecovery_WhenCleanupSchedulerIsUnavailable_ShouldNotPublishImmediateContinuation()
    {
        const string actorId = "run-tool-terminal-activation-cleanup";
        const string materialReference = "material-terminal-activation-cleanup";
        var store = new InMemoryEventStore();
        var seed = CreateAgent(
            actorId,
            store,
            new RecordingCallbackScheduler(),
            out _,
            out _);
        await seed.ActivateAsync();
        await BindToolWorkflowAsync(seed, actorId);
        await ((IWorkflowExecutionStateHost)seed).UpsertExecutionStateAsync(
            ToolCallModule.ModuleStateKey,
            Any.Pack(CreateTerminalToolState(
                actorId,
                CreateProtectedReference(materialReference, actorId, 1))));
        await PersistForTestAsync(seed, new WorkflowCompletedEvent
        {
            RunId = actorId,
            WorkflowName = "tool_recovery",
            Success = true,
            Output = "done",
        });

        var scheduler = new RecordingCallbackScheduler(failSchedule: true);
        var secretStore = new RecordingRuntimeSecretStore();
        secretStore.ThrowReferences.Add(materialReference);
        var recovered = CreateAgent(
            actorId,
            store,
            scheduler,
            out _,
            out var publisher,
            runtimeSecretStore: secretStore);

        var failure = await FluentActions.Awaiting(() => recovered.ActivateAsync())
            .Should().ThrowAsync<WorkflowDurablePublicationPendingException>();

        failure.Which.Should().BeAssignableTo<IRuntimeEnvelopeRetryableException>();
        scheduler.ScheduleAttempts.Should().Be(1);
        recovered.State.Status.Should().Be("completed");
        recovered.State.ExecutionStates.Should().ContainKey(ToolCallModule.ModuleStateKey);
        recovered.GetModules().Should().BeEmpty();
        publisher.Published.Select(static item => item.Event)
            .OfType<WorkflowToolCallTerminalCleanupRetryFiredEvent>()
            .Should().BeEmpty();
    }

    [Fact]
    public async Task Activation_ShouldPrepareAndSchedulePersistedPendingOperationWithoutExecutingTool()
    {
        const string actorId = "run-tool-operation-recovery";
        const string executionId = "exec-1";
        var callId = $"workflow:{actorId}:tool-step:{executionId}";
        var pending = CreatePendingOperation(actorId, executionId, callId);
        var seededState = new ToolCallModuleState();
        seededState.PendingOperations[
            RuntimeCallbackKeyComposer.BuildKey('|', callId, executionId)] = pending;
        var store = new InMemoryEventStore();
        var seed = CreateAgent(actorId, store, new RecordingCallbackScheduler(), out _, out _);
        await seed.ActivateAsync();
        await BindToolWorkflowAsync(seed, actorId);
        await ((IWorkflowExecutionStateHost)seed).UpsertExecutionStateAsync(
            ToolCallModule.ModuleStateKey,
            Any.Pack(seededState));

        var scheduler = new RecordingCallbackScheduler();
        var recovered = CreateAgent(actorId, store, scheduler, out var tool, out _);
        await recovered.ActivateAsync();

        var scheduled = scheduler.TimeoutRequests.Should().ContainSingle().Subject;
        var poll = scheduled.TriggerEnvelope.Payload!
            .Unpack<WorkflowToolCallOperationPollFiredEvent>();
        poll.OperationId.Should().Be(pending.OperationId);
        poll.PollAttempt.Should().Be(1);
        poll.CallbackId.Should().StartWith("workflow-tool-operation-poll:");
        tool.ExecuteCalls.Should().Be(0);
        var recoveredPending = recovered.State.ExecutionStates[ToolCallModule.ModuleStateKey]
            .Unpack<ToolCallModuleState>()
            .PendingOperations.Should().ContainSingle().Subject.Value;
        recoveredPending.PollCallbackId.Should().Be(poll.CallbackId);
        recoveredPending.NextPollUnixMs.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task Activation_ShouldPublishTypedOperationContinuation_WhenPollSchedulingFails()
    {
        const string actorId = "run-tool-operation-scheduler-failure";
        const string executionId = "exec-1";
        var callId = $"workflow:{actorId}:tool-step:{executionId}";
        var pending = CreatePendingOperation(actorId, executionId, callId);
        var seededState = new ToolCallModuleState();
        seededState.PendingOperations[
            RuntimeCallbackKeyComposer.BuildKey('|', callId, executionId)] = pending;
        var store = new InMemoryEventStore();
        var seed = CreateAgent(actorId, store, new RecordingCallbackScheduler(), out _, out _);
        await seed.ActivateAsync();
        await BindToolWorkflowAsync(seed, actorId);
        await ((IWorkflowExecutionStateHost)seed).UpsertExecutionStateAsync(
            ToolCallModule.ModuleStateKey,
            Any.Pack(seededState));

        var scheduler = new RecordingCallbackScheduler(failSchedule: true);
        var recovered = CreateAgent(actorId, store, scheduler, out var tool, out var publisher);
        await recovered.ActivateAsync();

        scheduler.ScheduleAttempts.Should().Be(1);
        var continuation = publisher.Published
            .Where(publication => publication.Audience == TopologyAudience.Self)
            .Select(publication => publication.Event)
            .OfType<WorkflowToolCallOperationPollFiredEvent>()
            .Should().ContainSingle().Subject;
        continuation.OperationId.Should().Be(pending.OperationId);
        continuation.CallbackId.Should().StartWith("workflow-tool-operation-poll:");
        tool.ExecuteCalls.Should().Be(0);
        recovered.State.ExecutionStates[ToolCallModule.ModuleStateKey]
            .Unpack<ToolCallModuleState>()
            .PendingOperations.Should().ContainSingle();
    }

    [Fact]
    public async Task Activation_ShouldRecoverStopCancellationInsteadOfOrdinaryOperationPoll()
    {
        const string actorId = "run-tool-stop-cancellation-recovery";
        const string executionId = "exec-1";
        var callId = $"workflow:{actorId}:tool-step:{executionId}";
        var pending = CreatePendingOperation(actorId, executionId, callId);
        var seededState = new ToolCallModuleState
        {
            StopCancellation = new PendingWorkflowToolStopCancellation
            {
                StopKind = WorkflowToolStopKind.WorkflowRunStopped,
                RunId = actorId,
                Reason = "requested by caller",
                CompletedAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
                ExpiresAtUnixMs = DateTimeOffset.UtcNow.AddMinutes(2).ToUnixTimeMilliseconds(),
            },
        };
        seededState.PendingOperations[
            RuntimeCallbackKeyComposer.BuildKey('|', callId, executionId)] = pending;
        var store = new InMemoryEventStore();
        var seed = CreateAgent(actorId, store, new RecordingCallbackScheduler(), out _, out _);
        await seed.ActivateAsync();
        await BindToolWorkflowAsync(seed, actorId);
        await ((IWorkflowExecutionStateHost)seed).UpsertExecutionStateAsync(
            ToolCallModule.ModuleStateKey,
            Any.Pack(seededState));

        var scheduler = new RecordingCallbackScheduler();
        var recovered = CreateAgent(actorId, store, scheduler, out var tool, out _);
        await recovered.ActivateAsync();

        var scheduled = scheduler.TimeoutRequests.Should().ContainSingle().Subject;
        var cancellation = scheduled.TriggerEnvelope.Payload!
            .Unpack<WorkflowToolCallStopCancellationFiredEvent>();
        cancellation.OperationId.Should().Be(pending.OperationId);
        cancellation.Attempt.Should().Be(1);
        cancellation.CallbackId.Should().StartWith("workflow-tool-stop-cancellation:");
        scheduled.TriggerEnvelope.Payload.Is(WorkflowToolCallOperationPollFiredEvent.Descriptor)
            .Should().BeFalse();
        tool.ExecuteCalls.Should().Be(0);
        var recoveredPending = recovered.State.ExecutionStates[ToolCallModule.ModuleStateKey]
            .Unpack<ToolCallModuleState>()
            .PendingOperations.Should().ContainSingle().Subject.Value;
        recoveredPending.StopCancellationPhase.Should().Be(WorkflowToolStopCancellationPhase.Requested);
        recoveredPending.StopCancellationCallbackId.Should().Be(cancellation.CallbackId);
    }

    [Fact]
    public async Task Activation_WhenStopCancellationHasSettled_ShouldRepublishOriginalStop()
    {
        const string actorId = "run-tool-stop-release-recovery";
        var store = new InMemoryEventStore();
        var seed = CreateAgent(actorId, store, new RecordingCallbackScheduler(), out _, out _);
        await seed.ActivateAsync();
        await BindToolWorkflowAsync(seed, actorId);
        await ((IWorkflowExecutionStateHost)seed).UpsertExecutionStateAsync(
            ToolCallModule.ModuleStateKey,
            Any.Pack(new ToolCallModuleState
            {
                StopCancellation = new PendingWorkflowToolStopCancellation
                {
                    StopKind = WorkflowToolStopKind.WorkflowStopped,
                    RunId = actorId,
                    WorkflowName = "tool_recovery",
                    Reason = "requested by caller",
                    CompletedAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
                    ExpiresAtUnixMs = DateTimeOffset.UtcNow.AddMinutes(2).ToUnixTimeMilliseconds(),
                },
            }));

        var scheduler = new RecordingCallbackScheduler();
        var recovered = CreateAgent(actorId, store, scheduler, out var tool, out var publisher);
        await recovered.ActivateAsync();

        scheduler.TimeoutRequests.Should().BeEmpty();
        publisher.Published
            .Where(publication => publication.Audience == TopologyAudience.Self)
            .Select(publication => publication.Event)
            .OfType<WorkflowStoppedEvent>()
            .Should().ContainSingle()
            .Which.Reason.Should().Be("requested by caller");
        tool.ExecuteCalls.Should().Be(0);
    }

    [Fact]
    public async Task Activation_WhenPersistedStopKindIsUnknown_ShouldExposeRetryableRecoveryFailure()
    {
        const string actorId = "run-tool-stop-release-unknown-kind";
        const int unknownStopKind = 99;
        var store = new InMemoryEventStore();
        var seed = CreateAgent(actorId, store, new RecordingCallbackScheduler(), out _, out _);
        await seed.ActivateAsync();
        await BindToolWorkflowAsync(seed, actorId);
        await ((IWorkflowExecutionStateHost)seed).UpsertExecutionStateAsync(
            ToolCallModule.ModuleStateKey,
            Any.Pack(new ToolCallModuleState
            {
                StopCancellation = new PendingWorkflowToolStopCancellation
                {
                    StopKind = (WorkflowToolStopKind)unknownStopKind,
                    RunId = actorId,
                    Reason = "requested by caller",
                    CompletedAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
                    ExpiresAtUnixMs = DateTimeOffset.UtcNow.AddMinutes(2).ToUnixTimeMilliseconds(),
                },
            }));

        var scheduler = new RecordingCallbackScheduler();
        var recovered = CreateAgent(actorId, store, scheduler, out var tool, out var publisher);

        var failure = await FluentActions.Awaiting(() => recovered.ActivateAsync())
            .Should().ThrowAsync<WorkflowDurablePublicationPendingException>();

        failure.Which.Should().BeAssignableTo<IRuntimeEnvelopeRetryableException>();
        failure.Which.Message.Should().Contain($"unsupported stop kind '{unknownStopKind}'");
        scheduler.TimeoutRequests.Should().BeEmpty();
        var publishedEvents = publisher.Published.Select(static publication => publication.Event).ToArray();
        publishedEvents.OfType<WorkflowStoppedEvent>().Should().BeEmpty();
        publishedEvents.OfType<WorkflowRunStoppedEvent>().Should().BeEmpty();
        tool.ExecuteCalls.Should().Be(0);
        recovered.State.ExecutionStates[ToolCallModule.ModuleStateKey]
            .Unpack<ToolCallModuleState>()
            .StopCancellation!.StopKind.Should().Be((WorkflowToolStopKind)unknownStopKind);
    }

    [Fact]
    public void TypedStopHandlers_ShouldRunAfterWorkflowExecutionBridgeGate()
    {
        var stopped = typeof(WorkflowRunGAgent)
            .GetMethod(nameof(WorkflowRunGAgent.HandleWorkflowStopped))!
            .GetCustomAttribute<Aevatar.Foundation.Abstractions.Attributes.EventHandlerAttribute>();
        var runStopped = typeof(WorkflowRunGAgent)
            .GetMethod(nameof(WorkflowRunGAgent.HandleWorkflowRunStoppedAsync))!
            .GetCustomAttribute<Aevatar.Foundation.Abstractions.Attributes.EventHandlerAttribute>();

        stopped.Should().NotBeNull();
        stopped!.Priority.Should().BeGreaterThan(0);
        runStopped.Should().NotBeNull();
        runStopped!.Priority.Should().BeGreaterThan(0);
    }

    private static ToolCallModuleState CreateRecoverableToolState(string runId)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var state = new ToolCallModuleState();
        var executing = CreatePendingExecution(runId, 1, WorkflowToolCallExecutionPhase.ExecutionPending);
        var retrying = CreatePendingExecution(runId, 2, WorkflowToolCallExecutionPhase.RetryPending);
        retrying.RetryCallbackId = "retry-recovery-2";
        retrying.RetryDueUnixMs = now + 30_000;
        state.PendingExecutions[$"{executing.CallId}|{executing.ExecutionId}"] = executing;
        state.PendingExecutions[$"{retrying.CallId}|{retrying.ExecutionId}"] = retrying;
        state.Completions.Add(new WorkflowToolCallCompletionOutboxEntry
        {
            RunId = runId,
            StepId = "tool-step",
            CallId = $"workflow:{runId}:tool-step:exec-completed",
            ExecutionId = "exec-completed",
            TerminalDecision = WorkflowToolCallTerminalDecision.NoApproval,
            ToolCompletion = new WorkflowToolCallCompletedEvent
            {
                RunId = runId,
                StepId = "tool-step",
                CallId = $"workflow:{runId}:tool-step:exec-completed",
                Success = true,
                ResultJson = "{}",
            },
            StepCompletion = new StepCompletedEvent
            {
                RunId = runId,
                StepId = "tool-step",
                ExecutionId = "exec-completed",
                Success = true,
                Output = "{}",
            },
        });
        return state;
    }

    private static void AssertLegacyPayloadFieldsScrubbed(ToolCallModuleState state)
    {
        var approval = state.PendingApprovals.Values.Should().ContainSingle().Subject;
        approval.ArgumentsJson.Should().BeEmpty();
        approval.Input.Should().BeEmpty();
        approval.InputFileRefs.Should().BeEmpty();
        approval.IdempotencyKey.Should().BeEmpty();
        approval.ExternalInvocation.Should().BeNull();
        approval.DisplayName.Should().BeEmpty();

        var execution = state.PendingExecutions.Values.Should().ContainSingle().Subject;
        execution.ArgumentsJson.Should().BeEmpty();
        execution.InputFileRefs.Should().BeEmpty();
        execution.IdempotencyKey.Should().BeEmpty();
        execution.ExternalInvocation.Should().BeNull();
        execution.DisplayName.Should().BeEmpty();
    }

    private static ToolCallModuleState CreatePendingApprovalToolState(string runId)
    {
        var callId = $"workflow:{runId}:tool-step:exec-1";
        var pending = new PendingToolCallApprovalState
        {
            RunId = runId,
            StepId = "tool-step",
            ExecutionId = "exec-1",
            ToolName = "counting_tool",
            ToolCallId = callId,
            ApprovalRequestId = "approval-1",
            TimeoutMs = 60_000,
            TimeoutDeadlineUnixMs = DateTimeOffset.UtcNow.AddMinutes(1).ToUnixTimeMilliseconds(),
            ContinuationId = "continuation-1",
            ExecutionPhase = WorkflowToolCallExecutionPhase.ApprovalPending,
        };
        var state = new ToolCallModuleState();
        state.PendingApprovals[$"{runId}:tool-step:exec-1:{callId}:approval-1"] = pending;
        return state;
    }

    private static ForEachModuleState CreatePendingForEachState(string runId)
    {
        var state = new ForEachModuleState
        {
            Backpressure = BackpressureHelper.Initialize(1),
        };
        state.Backpressure.ActiveWorkers = 1;
        state.Parents[$"{runId}:foreach-step"] = new ForEachParentState
        {
            Expected = 1,
            ParentRunId = runId,
            ParentStepId = "foreach-step",
            PendingDispatches =
            {
                BackpressureHelper.ToQueueEntry(
                    "foreach-step_item_0",
                    "tool_call",
                    runId,
                    "input",
                    string.Empty,
                    null,
                    executionId: "foreach-child-execution",
                    idempotencyKey: "foreach-child-idempotency"),
            },
        };
        state.Parents[$"{runId}:foreach-step"].ChildExecutionIds["foreach-step_item_0"] =
            "foreach-child-execution";
        return state;
    }

    private static ToolCallModuleState CreateTerminalToolState(
        string runId,
        params RuntimeSecretReference[] references)
    {
        var state = new ToolCallModuleState();
        for (var i = 0; i < references.Length; i++)
        {
            var index = i + 1;
            var pending = CreatePendingExecution(
                runId,
                index,
                WorkflowToolCallExecutionPhase.ExecutionPending);
            pending.ProtectedMaterialReference = references[i].Clone();
            pending.ProtectedMaterialDigestSha256 = $"digest-{index}";
            pending.TimeoutLease = new WorkflowRuntimeCallbackLeaseState
            {
                ActorId = runId,
                CallbackId = $"timeout-{index}",
                Generation = index,
                Backend = WorkflowRuntimeCallbackBackendState.InMemory,
            };
            pending.RetryLease = new WorkflowRuntimeCallbackLeaseState
            {
                ActorId = runId,
                CallbackId = $"retry-{index}",
                Generation = index,
                Backend = WorkflowRuntimeCallbackBackendState.InMemory,
            };
            state.PendingExecutions[$"{pending.CallId}|{pending.ExecutionId}"] = pending;
        }

        return state;
    }

    private static PendingToolCallExecutionState CreatePendingExecution(
        string runId,
        int index,
        WorkflowToolCallExecutionPhase phase) =>
        new()
        {
            RunId = runId,
            StepId = "tool-step",
            ExecutionId = $"exec-{index}",
            ToolName = "counting_tool",
            CallId = $"workflow:{runId}:tool-step:exec-{index}",
            TimeoutMs = 60_000,
            TimeoutDeadlineUnixMs = DateTimeOffset.UtcNow.AddMinutes(1).ToUnixTimeMilliseconds(),
            TimeoutCallbackId = $"timeout-recovery-{index}",
            Attempt = 1,
            ContinuationId = $"continuation-{index}",
            ExecutionPhase = phase,
        };

    private static RuntimeSecretReference CreateProtectedReference(string reference, string runId, int index) =>
        new()
        {
            Ref = reference,
            Purpose = CredentialSecretPurposes.WorkflowToolCallProtectedMaterial,
            OwnerRunId = runId,
            OwnerStepId = "tool-step",
            Fingerprint = $"sha256:{index}",
            ConsumeOnce = false,
            ExpiresAtUnixMs = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeMilliseconds(),
        };

    private static BindWorkflowRunDefinitionEvent CreateBindEvent(string runId) =>
        new()
        {
            DefinitionActorId = "definition-tool-recovery",
            WorkflowName = "tool_recovery",
            WorkflowYaml = """
                           name: tool_recovery
                           roles: []
                           steps:
                             - id: tool-step
                               type: tool_call
                               parameters:
                                 tool: counting_tool
                           """,
            RunId = runId,
            ScopeId = "scope-1",
            ExpectedExecutionMode = ExternalCapabilityExecutionMode.Interactive,
            ReusePolicy = WorkflowRunActorReusePolicy.SingleRun,
        };

    private static EventEnvelope EnvelopeFrom(string publisherActorId, IMessage payload) =>
        new()
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
            Payload = Any.Pack(payload),
            Route = EnvelopeRouteSemantics.CreateTopologyPublication(
                publisherActorId,
                TopologyAudience.Self),
        };

    private static EventEnvelope CreatePublicationRetryEnvelope(string actorId, IMessage payload) =>
        CreatePublicationRetryEnvelope(EnvelopeFrom(actorId, payload));

    private static EventEnvelope CreatePublicationRetryEnvelope(EventEnvelope original)
    {
        var retry = original.Clone();
        retry.EnsureRuntime().Retry = new EnvelopeRetryContext
        {
            OriginEventId = original.Id,
            Attempt = 1,
            LastErrorType = nameof(CommittedStatePublicationException),
        };
        return retry;
    }

    private static WorkflowDefinition? GetCompiledWorkflow(WorkflowRunGAgent agent) =>
        (WorkflowDefinition?)typeof(WorkflowRunGAgent)
            .GetField("_compiledWorkflow", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(agent);

    private static async Task PersistForTestAsync(WorkflowRunGAgent agent, IMessage evt)
    {
        var method = typeof(GAgentBase<WorkflowRunState>)
            .GetMethods(BindingFlags.Instance | BindingFlags.NonPublic)
            .Single(candidate =>
                candidate.Name == "PersistDomainEventAsync" &&
                candidate.IsGenericMethodDefinition &&
                candidate.GetParameters().Length == 2 &&
                candidate.GetParameters()[0].ParameterType.IsGenericParameter);
        var task = (Task)method.MakeGenericMethod(evt.GetType())
            .Invoke(agent, [evt, CancellationToken.None])!;
        await task;
    }

    private static int ReadAttempt(IReadOnlyDictionary<string, object?> entry) =>
        Convert.ToInt32(entry["Attempt"]);

    private static MeterListener ListenForWorkflowToolCallMetrics(List<MetricMeasurement> measurements)
    {
        var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (instrument.Meter.Name == WorkflowToolCallTelemetry.MeterName)
                meterListener.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
            RecordMetricMeasurement(measurements, instrument, value, tags));
        listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) =>
            RecordMetricMeasurement(measurements, instrument, value, tags));
        listener.Start();
        return listener;
    }

    private static void RecordMetricMeasurement(
        List<MetricMeasurement> measurements,
        Instrument instrument,
        double value,
        ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        var copiedTags = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var tag in tags)
            copiedTags[tag.Key] = tag.Value;

        lock (measurements)
            measurements.Add(new MetricMeasurement(instrument.Name, value, copiedTags));
    }

    private static PendingToolCallOperationState CreatePendingOperation(
        string actorId,
        string executionId,
        string callId) =>
        new()
        {
            RunId = actorId,
            StepId = "tool-step",
            ExecutionId = executionId,
            ToolName = "counting_tool",
            ToolCallId = callId,
            OperationId = "tool:v1:operation:" + new string('b', 64),
            ProviderOperationId = "provider-operation-1",
            StatusPath = "/executions/provider-operation-1",
            ResultPath = "/executions/provider-operation-1/result",
            CancelPath = "/executions/provider-operation-1/cancel",
            Status = WorkflowToolPendingOperationStatus.Running,
            RetryAfterMs = 100,
            ExpiresAtUnixMs = DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeMilliseconds(),
            ServiceSlug = "chrono-sandbox",
            TerminalDecision = WorkflowToolCallTerminalDecision.NoApproval,
            ProtectedMaterialReference = CreateProtectedReference(
                $"material-{executionId}",
                actorId,
                1),
            ProtectedMaterialDigestSha256 = new string('a', 64),
        };

    private static WorkflowRunGAgent CreateAgent(
        string actorId,
        InMemoryEventStore store,
        RecordingCallbackScheduler scheduler,
        out RecordingWorkflowTool tool,
        out RecordingEventPublisher publisher,
        IRuntimeSecretStore? runtimeSecretStore = null,
        ICommittedStatePublicationHook? publicationHook = null,
        ICommittedStatePublicationStateStore? publicationStateStore = null,
        TimeProvider? timeProvider = null)
    {
        tool = new RecordingWorkflowTool("counting_tool");
        var module = new ToolCallModule(
            [new SingleWorkflowToolSource(tool)],
            NullLogger<ToolCallModule>.Instance);
        var moduleFactory = new ToolModuleFactory(module);
        var runtime = new UnsupportedActorRuntime();
        var pack = new ToolModulePack();
        publisher = new RecordingEventPublisher();
        var agent = new WorkflowRunGAgent(runtime, runtime, moduleFactory, [pack], timeProvider: timeProvider)
        {
            EventSourcingBehaviorFactory = new DefaultEventSourcingBehaviorFactory<WorkflowRunState>(
                store,
                publicationStateStore: publicationStateStore),
            EventPublisher = publisher,
            Services = new TestServiceProvider(scheduler, runtimeSecretStore, publicationHook),
            Logger = NullLogger.Instance,
        };
        SetAgentId(agent, actorId);
        return agent;
    }

    private static Task BindToolWorkflowAsync(WorkflowRunGAgent agent, string runId)
    {
        var bind = CreateBindEvent(runId);
        return agent.BindWorkflowRunDefinitionAsync(
            bind.DefinitionActorId,
            bind.WorkflowYaml,
            bind.WorkflowName,
            bind.InlineWorkflowYamls,
            bind.RunId,
            bind.ScopeId,
            bind.RunOrigin,
            bind.ScheduleId,
            bind.WorkflowId,
            bind.RevisionId,
            bind.DefinitionVersion,
            bind.CapabilityAdmissionPlan,
            bind.ExpectedExecutionMode,
            bind.InitialLineage,
            bind.ReusePolicy,
            bind.BindingGeneration,
            bind.ReuseAuthorityActorId);
    }

    private static Task BindForEachWorkflowAsync(WorkflowRunGAgent agent, string runId)
    {
        var bind = CreateBindEvent(runId);
        bind.WorkflowName = "foreach_recovery";
        bind.WorkflowYaml = """
                            name: foreach_recovery
                            roles: []
                            steps:
                              - id: foreach-step
                                type: foreach
                                parameters:
                                  sub_step_type: tool_call
                                  sub_param_tool: counting_tool
                            """;
        return agent.BindWorkflowRunDefinitionAsync(
            bind.DefinitionActorId,
            bind.WorkflowYaml,
            bind.WorkflowName,
            bind.InlineWorkflowYamls,
            bind.RunId,
            bind.ScopeId,
            bind.RunOrigin,
            bind.ScheduleId,
            bind.WorkflowId,
            bind.RevisionId,
            bind.DefinitionVersion,
            bind.CapabilityAdmissionPlan,
            bind.ExpectedExecutionMode,
            bind.InitialLineage,
            bind.ReusePolicy,
            bind.BindingGeneration,
            bind.ReuseAuthorityActorId);
    }

    private static void SetAgentId(GAgentBase agent, string agentId)
    {
        var method = typeof(GAgentBase).GetMethod("SetId", BindingFlags.Instance | BindingFlags.NonPublic);
        method.Should().NotBeNull();
        method!.Invoke(agent, [agentId]);
    }

    private sealed class ToolModulePack : IWorkflowModulePack
    {
        public string Name => "test.tool";

        public IReadOnlyList<WorkflowModuleRegistration> Modules { get; } =
        [
            WorkflowModuleRegistration.Create<ToolCallModule>("tool_call"),
            WorkflowModuleRegistration.Create<ForEachModule>("foreach"),
        ];

        public IReadOnlyList<IWorkflowModuleDependencyExpander> DependencyExpanders { get; } =
        [
            new WorkflowStepTypeModuleDependencyExpander(),
        ];

        public IReadOnlyList<IWorkflowModuleConfigurator> Configurators { get; } = [];
    }

    private sealed class ToolModuleFactory(ToolCallModule module) : IEventModuleFactory<IWorkflowExecutionContext>
    {
        public bool TryCreate(string name, out IEventModule<IWorkflowExecutionContext>? created)
        {
            created = name.ToLowerInvariant() switch
            {
                "tool_call" => module,
                "foreach" => new ForEachModule(),
                _ => null,
            };
            return created != null;
        }
    }

    private sealed class RecordingWorkflowTool(string name) : IWorkflowTool
    {
        public string Name { get; } = name;

        public int ExecuteCalls { get; private set; }

        public Task<WorkflowToolExecutionResult> ExecuteAsync(
            WorkflowToolExecutionRequest request,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            ExecuteCalls++;
            return Task.FromResult(WorkflowToolExecutionResult.Success("{}"));
        }
    }

    private sealed class SingleWorkflowToolSource(IWorkflowTool tool) : IWorkflowToolSource
    {
        public Task<IReadOnlyList<IWorkflowTool>> GetToolsAsync(CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult<IReadOnlyList<IWorkflowTool>>([tool]);
        }
    }

    private sealed class RecordingEventPublisher : IEventPublisher
    {
        public List<(IMessage Event, TopologyAudience Audience)> Published { get; } = [];

        public global::System.Type? FailNextPublishType { get; set; }

        public Task PublishAsync<TEvent>(
            TEvent evt,
            TopologyAudience audience = TopologyAudience.Children,
            CancellationToken ct = default,
            EventEnvelope? sourceEnvelope = null,
            EventEnvelopePublishOptions? options = null)
            where TEvent : IMessage
        {
            ct.ThrowIfCancellationRequested();
            if (FailNextPublishType?.IsInstanceOfType(evt) == true)
            {
                FailNextPublishType = null;
                throw new InvalidOperationException("injected publication failure");
            }

            Published.Add((evt.Descriptor.Parser.ParseFrom(evt.ToByteArray()), audience));
            return Task.CompletedTask;
        }

        public Task SendToAsync<TEvent>(
            string targetActorId,
            TEvent evt,
            CancellationToken ct = default,
            EventEnvelope? sourceEnvelope = null,
            EventEnvelopePublishOptions? options = null)
            where TEvent : IMessage =>
            Task.CompletedTask;
    }

    private sealed class RecordingCallbackScheduler(
        bool failSchedule = false,
        Func<RuntimeCallbackTimeoutRequest, CancellationToken, Task>? afterSchedule = null)
        : IActorRuntimeCallbackScheduler
    {
        public List<RuntimeCallbackTimeoutRequest> TimeoutRequests { get; } = [];

        public List<RuntimeCallbackLease> CancelledLeases { get; } = [];

        public int ScheduleAttempts { get; private set; }

        public async Task<RuntimeCallbackLease> ScheduleTimeoutAsync(
            RuntimeCallbackTimeoutRequest request,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            ScheduleAttempts++;
            if (failSchedule)
                throw new InvalidOperationException("schedule failed");

            TimeoutRequests.Add(request);
            var lease = new RuntimeCallbackLease(
                request.ActorId,
                request.CallbackId,
                TimeoutRequests.Count,
                RuntimeCallbackBackend.InMemory);
            if (afterSchedule != null)
                await afterSchedule(request, ct);
            return lease;
        }

        public Task<RuntimeCallbackLease> ScheduleTimerAsync(
            RuntimeCallbackTimerRequest request,
            CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task CancelAsync(RuntimeCallbackLease lease, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            CancelledLeases.Add(lease);
            return Task.CompletedTask;
        }

        public Task PurgeActorAsync(string actorId, CancellationToken ct = default) =>
            Task.CompletedTask;
    }

    private sealed class TestServiceProvider(
        IActorRuntimeCallbackScheduler scheduler,
        IRuntimeSecretStore? runtimeSecretStore,
        ICommittedStatePublicationHook? publicationHook) : IServiceProvider
    {
        public object? GetService(global::System.Type serviceType)
        {
            if (serviceType == typeof(IActorRuntimeCallbackScheduler))
                return scheduler;
            if (serviceType == typeof(IRuntimeSecretStore))
                return runtimeSecretStore;
            if (serviceType == typeof(IEnumerable<IGAgentExecutionHook>))
                return Array.Empty<IGAgentExecutionHook>();
            if (serviceType == typeof(IEnumerable<ICommittedStatePublicationHook>))
                return publicationHook == null
                    ? Array.Empty<ICommittedStatePublicationHook>()
                    : new[] { publicationHook };
            return null;
        }
    }

    private sealed class FailOnceCommittedPublicationHook : ICommittedStatePublicationHook
    {
        public bool FailNext { get; set; }

        public int FailureCount { get; private set; }

        public Task BeforePublishAsync(CommittedStatePublicationContext context, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (!FailNext)
                return Task.CompletedTask;

            FailNext = false;
            FailureCount++;
            throw new InvalidOperationException("injected committed publication failure");
        }
    }

    private sealed class OrderedCommittedPublicationHook(
        params ICommittedStatePublicationHook[] hooks)
        : ICommittedStatePublicationHook
    {
        public async Task BeforePublishAsync(CommittedStatePublicationContext context, CancellationToken ct)
        {
            foreach (var hook in hooks)
                await hook.BeforePublishAsync(context, ct);
        }
    }

    private sealed class FailOnceAdvancePublicationStateStore(
        ICommittedStatePublicationStateStore inner)
        : ICommittedStatePublicationStateStore
    {
        public bool FailNext { get; set; }

        public List<StateEvent> AdvanceAttempts { get; } = [];

        public Task<CommittedStatePublicationState?> LoadAsync(
            string actorId,
            CancellationToken ct = default) =>
            inner.LoadAsync(actorId, ct);

        public Task<CommittedStatePublicationState> InitializeAsync(
            string actorId,
            long baselinePublishedVersion,
            CancellationToken ct = default) =>
            inner.InitializeAsync(actorId, baselinePublishedVersion, ct);

        public Task<CommittedStatePublicationState> AdvanceAsync(
            string actorId,
            long expectedPublishedVersion,
            StateEvent publishedEvent,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            AdvanceAttempts.Add(publishedEvent.Clone());
            if (!FailNext)
            {
                return inner.AdvanceAsync(
                    actorId,
                    expectedPublishedVersion,
                    publishedEvent,
                    ct);
            }

            FailNext = false;
            throw new InvalidOperationException("injected publication checkpoint failure");
        }

        public Task<CommittedStatePublicationState> RecordFailureAsync(
            string actorId,
            long expectedPublishedVersion,
            StateEvent failedEvent,
            CommittedStatePublicationFailureStage stage,
            Exception error,
            CancellationToken ct = default) =>
            inner.RecordFailureAsync(
                actorId,
                expectedPublishedVersion,
                failedEvent,
                stage,
                error,
                ct);
    }

    private sealed class RecordingPersistenceLogger
        : ILogger<WorkflowToolCallAttemptPersistenceTelemetryHook>
    {
        private readonly List<IReadOnlyDictionary<string, object?>> _entries = [];

        public IReadOnlyList<IReadOnlyDictionary<string, object?>> PendingPersistenceEntries =>
            _entries.Where(entry => Equals(entry.GetValueOrDefault("Waterline"), "pending_state_persisted"))
                .ToArray();

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
            _ = exception;
            _ = formatter;
            _entries.Add(state is IEnumerable<KeyValuePair<string, object?>> values
                ? values
                    .Where(static value => !string.Equals(value.Key, "{OriginalFormat}", StringComparison.Ordinal))
                    .ToDictionary(static value => value.Key, static value => value.Value, StringComparer.Ordinal)
                : new Dictionary<string, object?>(StringComparer.Ordinal));
        }
    }

    private sealed record MetricMeasurement(
        string Instrument,
        double Value,
        IReadOnlyDictionary<string, object?> Tags);

    private sealed class RecordingRuntimeSecretStore : IRuntimeSecretStore
    {
        public HashSet<string> FailReferences { get; } = new(StringComparer.Ordinal);

        public HashSet<string> ThrowReferences { get; } = new(StringComparer.Ordinal);

        public HashSet<string> UnavailableReferences { get; } = new(StringComparer.Ordinal);

        public List<RevokeRuntimeSecretRequest> RevokeRequests { get; } = [];

        public List<ResolveRuntimeSecretRequest> ResolveRequests { get; } = [];

        public Task<StoreRuntimeSecretResult> PutAsync(
            StoreRuntimeSecretRequest request,
            CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<ResolveRuntimeSecretResult> ResolveAsync(
            ResolveRuntimeSecretRequest request,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            ResolveRequests.Add(request);
            if (UnavailableReferences.Contains(request.Ref))
                return Task.FromResult(new ResolveRuntimeSecretResult(null, null));

            return Task.FromResult(new ResolveRuntimeSecretResult(
                new RuntimeSecretReference
                {
                    Ref = request.Ref,
                    Purpose = request.Purpose,
                    OwnerRunId = request.OwnerRunId,
                    OwnerStepId = request.OwnerStepId,
                },
                "present"));
        }

        public Task<ConsumeRuntimeSecretResult> ConsumeAsync(
            ConsumeRuntimeSecretRequest request,
            CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<RevokeRuntimeSecretResult> RevokeAsync(
            RevokeRuntimeSecretRequest request,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            RevokeRequests.Add(request);
            if (ThrowReferences.Contains(request.Ref))
                throw new InvalidOperationException("injected transient revoke failure");
            return Task.FromResult(new RevokeRuntimeSecretResult(
                !FailReferences.Contains(request.Ref) &&
                !UnavailableReferences.Contains(request.Ref)));
        }
    }

    private sealed class UnsupportedActorRuntime : IActorRuntime, IActorDispatchPort
    {
        public Task<IActor> CreateAsync<TAgent>(string? id = null, CancellationToken ct = default)
            where TAgent : IAgent => throw new NotSupportedException();

        public Task<IActor> CreateAsync(global::System.Type agentType, string? id = null, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task DestroyAsync(string id, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<IActor?> GetAsync(string id) => throw new NotSupportedException();

        public Task<bool> ExistsAsync(string id) => Task.FromResult(false);

        public Task LinkAsync(string parentId, string childId, CancellationToken ct = default) => Task.CompletedTask;

        public Task UnlinkAsync(string childId, CancellationToken ct = default) => Task.CompletedTask;

        public Task<DispatchAdmission> DispatchAsync(
            string actorId,
            EventEnvelope envelope,
            CancellationToken ct = default) =>
            Task.FromResult(DispatchAdmissionFactory.Create(actorId, envelope));
    }
}
#pragma warning restore CS0612
