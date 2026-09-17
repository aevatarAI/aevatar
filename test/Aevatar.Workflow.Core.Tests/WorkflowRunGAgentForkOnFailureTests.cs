using System.Reflection;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.EventModules;
using Aevatar.Foundation.Abstractions.EventSourcing;
using Aevatar.Foundation.Abstractions.Hooks;
using Aevatar.Foundation.Abstractions.Persistence;
using Aevatar.Foundation.Abstractions.Runtime.Callbacks;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Abstractions.Execution;
using Aevatar.Workflow.Core.Composition;
using Aevatar.Workflow.Core.Execution;
using Aevatar.Workflow.Core.Modules;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.Workflow.Core.Tests;

public sealed class WorkflowRunGAgentForkOnFailureTests
{
    [Fact]
    public void BuildExecutionStartLineage_WithForkSeed_ShouldExposeSourceAndOriginalRunIds()
    {
        var lineage = WorkflowRunGAgent.BuildExecutionStartLineage(
            new WorkflowRunForkSeed
            {
                SourceRunId = "run-source-gamma",
                OriginalRunId = "run-original-alpha",
                StartAtStepId = "step-retry",
                Attempt = 3,
            },
            currentLineage: null,
            runId: "run-child-beta");

        lineage.Availability.Should().Be(WorkflowRunLineageAvailability.Available);
        lineage.RetryFork.Availability.Should().Be(WorkflowRunLineageAvailability.Available);
        lineage.RetryFork.SourceRunId.Should().Be("run-source-gamma");
        lineage.RetryFork.OriginalRunId.Should().Be("run-original-alpha");
        lineage.RetryFork.StartAtStepId.Should().Be("step-retry");
        lineage.RetryFork.Attempt.Should().Be(3);
        lineage.SubWorkflow.Availability.Should().Be(WorkflowRunLineageAvailability.Unavailable);
    }

    [Fact]
    public void BuildExecutionStartLineage_WithoutForkSeed_ShouldReturnExplicitUnavailableLineage()
    {
        var lineage = WorkflowRunGAgent.BuildExecutionStartLineage(
            forkSeed: null,
            currentLineage: null,
            runId: "run-standalone-beta");

        lineage.Availability.Should().Be(WorkflowRunLineageAvailability.Unavailable);
        lineage.RetryFork.Availability.Should().Be(WorkflowRunLineageAvailability.Unavailable);
        lineage.SubWorkflow.Availability.Should().Be(WorkflowRunLineageAvailability.Unavailable);
        lineage.UnavailableReason.Should().Contain("unavailable");
    }

    [Fact]
    public async Task HandleReplaceWorkflowDefinitionAndExecute_ShouldPreserveExistingLineage()
    {
        const string runId = "run-child-beta";
        const string sourceRunId = "run-source-gamma";
        const string originalRunId = "run-original-alpha";
        var harness = await CreateUnboundRunAsync(runId);

        await harness.Agent.HandleBindWorkflowRunDefinition(new BindWorkflowRunDefinitionEvent
        {
            DefinitionActorId = "definition-child-delta",
            WorkflowName = "wf_1859",
            WorkflowYaml = WorkflowYaml(onFailure: false),
            RunId = runId,
            ScopeId = "scope-child",
            ExpectedExecutionMode = ExternalCapabilityExecutionMode.Interactive,
            InitialLineage = new WorkflowRunLineage
            {
                Availability = WorkflowRunLineageAvailability.Available,
                UnavailableReason = "stale unavailable reason",
                RetryFork = new WorkflowRunRetryForkLineage
                {
                    Availability = WorkflowRunLineageAvailability.Available,
                    SourceRunId = sourceRunId,
                    OriginalRunId = originalRunId,
                    Attempt = 2,
                    StartAtStepId = "step-failed",
                },
                SubWorkflow = new WorkflowRunSubWorkflowLineage
                {
                    Availability = WorkflowRunLineageAvailability.Unavailable,
                },
            },
        });

        await harness.Agent.HandleReplaceWorkflowDefinitionAndExecute(new ReplaceWorkflowDefinitionAndExecuteEvent
        {
            WorkflowYaml = WorkflowYaml(onFailure: false),
            Input = "replacement-input",
        });

        var committedReplacement = CommittedEvents<BindWorkflowRunDefinitionEvent>(harness.CommittedPublisher)
            .Last();
        committedReplacement.InitialLineage.Availability.Should().Be(WorkflowRunLineageAvailability.Available);
        committedReplacement.InitialLineage.RetryFork.SourceRunId.Should().Be(sourceRunId);
        committedReplacement.InitialLineage.RetryFork.OriginalRunId.Should().Be(originalRunId);
        harness.Agent.State.Lineage.Availability.Should().Be(WorkflowRunLineageAvailability.Available);
        harness.Agent.State.Lineage.UnavailableReason.Should().BeEmpty();
        harness.Agent.State.Lineage.RetryFork.SourceRunId.Should().Be(sourceRunId);
        harness.Agent.State.Lineage.RetryFork.OriginalRunId.Should().Be(originalRunId);
    }

    [Fact]
    public async Task HandleWorkflowRunLineageRecorded_ShouldClearUnavailableReasonWhenLineageBecomesAvailable()
    {
        const string runId = "run-source-gamma";
        var harness = await CreateUnboundRunAsync(runId);

        await harness.Agent.HandleBindWorkflowRunDefinition(new BindWorkflowRunDefinitionEvent
        {
            DefinitionActorId = "definition-source-delta",
            WorkflowName = "wf_1859",
            WorkflowYaml = WorkflowYaml(onFailure: false),
            RunId = runId,
            ScopeId = "scope-source",
            ExpectedExecutionMode = ExternalCapabilityExecutionMode.Interactive,
            InitialLineage = WorkflowRunGAgent.CreateUnavailableLineage("Run lineage is unavailable for this run."),
        });

        await harness.Agent.HandleWorkflowRunLineageRecorded(new WorkflowRunLineageRecordedEvent
        {
            SourceRunId = runId,
            ChildRunId = "run-child-beta",
            ChildActorId = "actor-child-delta",
            OriginalRunId = "run-original-alpha",
            StartAtStepId = "step-failed",
            Attempt = 2,
            RelationKind = WorkflowRunLineageRelationKind.RetryFork,
        });

        harness.Agent.State.Lineage.Availability.Should().Be(WorkflowRunLineageAvailability.Available);
        harness.Agent.State.Lineage.UnavailableReason.Should().BeEmpty();
        harness.Agent.State.Lineage.RetryFork.Availability.Should().Be(WorkflowRunLineageAvailability.Available);
        harness.Agent.State.Lineage.RetryFork.ChildRuns.Should().ContainSingle(child =>
            child.RunId == "run-child-beta" &&
            child.ActorId == "actor-child-delta" &&
            child.RelationKind == WorkflowRunLineageRelationKind.RetryFork);
    }

    [Fact]
    public async Task HandleBindWorkflowRunDefinition_WithInitialSubWorkflowLineage_ShouldCommitAndApplyChildLineage()
    {
        const string childRunId = "run-child-beta";
        const string parentRunId = "run-parent-alpha";
        const string rootRunId = "run-root-omega";
        var harness = await CreateUnboundRunAsync(childRunId);

        var initialLineage = new WorkflowRunLineage
        {
            Availability = WorkflowRunLineageAvailability.Available,
            RetryFork = new WorkflowRunRetryForkLineage
            {
                Availability = WorkflowRunLineageAvailability.Unavailable,
            },
            SubWorkflow = new WorkflowRunSubWorkflowLineage
            {
                Availability = WorkflowRunLineageAvailability.Available,
                ParentRunId = parentRunId,
                ParentActorId = "actor-parent-gamma",
                ParentStepId = "step-call-child",
                RootRunId = rootRunId,
                Depth = 2,
            },
        };

        await harness.Agent.HandleBindWorkflowRunDefinition(new BindWorkflowRunDefinitionEvent
        {
            DefinitionActorId = "definition-child-delta",
            WorkflowName = "wf_child_beta",
            WorkflowYaml = WorkflowYaml(onFailure: false),
            RunId = childRunId,
            ScopeId = "scope-child",
            ExpectedExecutionMode = ExternalCapabilityExecutionMode.Interactive,
            InitialLineage = initialLineage,
        });

        var committed = CommittedEvents<BindWorkflowRunDefinitionEvent>(harness.CommittedPublisher)
            .Should()
            .ContainSingle()
            .Subject;
        committed.RunId.Should().Be(childRunId);
        committed.InitialLineage.SubWorkflow.ParentRunId.Should().Be(parentRunId);
        committed.InitialLineage.SubWorkflow.RootRunId.Should().Be(rootRunId);
        committed.InitialLineage.SubWorkflow.ParentStepId.Should().Be("step-call-child");

        harness.Agent.State.RunId.Should().Be(childRunId);
        harness.Agent.State.Lineage.Availability.Should().Be(WorkflowRunLineageAvailability.Available);
        harness.Agent.State.Lineage.RetryFork.Availability.Should().Be(WorkflowRunLineageAvailability.Unavailable);
        harness.Agent.State.Lineage.SubWorkflow.Availability.Should().Be(WorkflowRunLineageAvailability.Available);
        harness.Agent.State.Lineage.SubWorkflow.ParentRunId.Should().Be(parentRunId);
        harness.Agent.State.Lineage.SubWorkflow.ParentActorId.Should().Be("actor-parent-gamma");
        harness.Agent.State.Lineage.SubWorkflow.ParentStepId.Should().Be("step-call-child");
        harness.Agent.State.Lineage.SubWorkflow.RootRunId.Should().Be(rootRunId);
        harness.Agent.State.Lineage.SubWorkflow.Depth.Should().Be(2);
    }

    [Fact]
    public async Task TerminalFailedRun_WithForkPolicy_ShouldCommitForkRequestedEvent()
    {
        var harness = await CreateStartedRunAsync(WorkflowYaml(onFailure: true), attempt: 1);

        await harness.Agent.HandleEventAsync(SelfEnvelope(harness.RunId, new StepCompletedEvent
        {
            RunId = harness.RunId,
            StepId = "failed-step",
            Success = false,
            Error = "boom",
            ExecutionId = harness.StepExecutionId,
        }));

        var requested = harness.CommittedPublisher.Events
            .Select(TryUnpackForkRequest)
            .OfType<WorkflowRunForkRequestedEvent>()
            .Should()
            .ContainSingle()
            .Subject;
        requested.SourceRunId.Should().Be(harness.RunId);
        requested.StartAtStepId.Should().Be("failed-step");
        requested.Attempt.Should().Be(2);
        requested.ScopeId.Should().Be("scope-1");
    }

    [Fact]
    public async Task TerminalFailedRun_WithoutForkPolicy_ShouldNotCommitForkRequestedEvent()
    {
        var harness = await CreateStartedRunAsync(WorkflowYaml(onFailure: false), attempt: 0);

        await harness.Agent.HandleEventAsync(SelfEnvelope(harness.RunId, new StepCompletedEvent
        {
            RunId = harness.RunId,
            StepId = "failed-step",
            Success = false,
            Error = "boom",
            ExecutionId = harness.StepExecutionId,
        }));

        harness.CommittedPublisher.Events
            .Select(TryUnpackForkRequest)
            .OfType<WorkflowRunForkRequestedEvent>()
            .Should()
            .BeEmpty();
    }

    [Fact]
    public async Task TerminalFailedRun_WhenAttemptReachedMax_ShouldNotCommitForkRequestedEvent()
    {
        var harness = await CreateStartedRunAsync(WorkflowYaml(onFailure: true, maxAttempts: 2), attempt: 2);

        await harness.Agent.HandleEventAsync(SelfEnvelope(harness.RunId, new StepCompletedEvent
        {
            RunId = harness.RunId,
            StepId = "failed-step",
            Success = false,
            Error = "boom",
            ExecutionId = harness.StepExecutionId,
        }));

        harness.CommittedPublisher.Events
            .Select(TryUnpackForkRequest)
            .OfType<WorkflowRunForkRequestedEvent>()
            .Should()
            .BeEmpty();
    }

    [Fact]
    public async Task WorkflowCompletionSelfPublishFailure_ShouldRecoverPersistedSuccessOnActivation()
    {
        var runId = "run-completion-intent-" + Guid.NewGuid().ToString("N");
        const string output = "durable-success-output";
        var harness = await CreateStartedRunAsync(runId, WorkflowYaml(onFailure: false));
        var failFirstCompletion = true;
        harness.Publisher.FailPublish = evt =>
        {
            if (evt is not WorkflowCompletedEvent || !failFirstCompletion)
                return false;

            failFirstCompletion = false;
            return true;
        };

        await FluentActions.Awaiting(() => harness.Agent.HandleEventAsync(
                SelfEnvelope(runId, SuccessfulStepCompletion(harness, output))))
            .Should()
            .ThrowAsync<WorkflowDurablePublicationPendingException>();

        harness.Agent.State.Status.Should().Be("running");
        var pending = KernelState(harness.Agent).PendingWorkflowCompletion;
        pending.Should().NotBeNull();
        pending!.RunId.Should().Be(runId);
        pending.Success.Should().BeTrue();
        pending.Output.Should().Be(output);

        var recovered = await CreateRunAsync(
            runId,
            WorkflowYaml(onFailure: false),
            eventStore: harness.EventStore);

        recovered.Agent.State.Status.Should().Be("completed");
        recovered.Agent.State.FinalOutput.Should().Be(output);
        KernelState(recovered.Agent).PendingWorkflowCompletion.Should().BeNull();
        var recoveredCompletion = recovered.Publisher.Published
            .Where(static item => item.Audience == TopologyAudience.Self)
            .Select(static item => item.Event)
            .OfType<WorkflowCompletedEvent>()
            .Should()
            .ContainSingle()
            .Subject;
        recoveredCompletion.Should().BeEquivalentTo(pending);

        var initialOperationId = harness.Publisher.PublishAttempts
            .Single(static attempt =>
                attempt.Event is WorkflowCompletedEvent &&
                attempt.Audience == TopologyAudience.Self)
            .Options?.Delivery?.OperationId;
        var recoveredOperationId = recovered.Publisher.PublishAttempts
            .Single(static attempt =>
                attempt.Event is WorkflowCompletedEvent &&
                attempt.Audience == TopologyAudience.Self)
            .Options?.Delivery?.OperationId;
        recoveredOperationId.Should().Be(initialOperationId).And.NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task PendingCompletion_ShouldWinOverDynamicDefinitionReplacement()
    {
        var runId = "run-completion-before-replace-" + Guid.NewGuid().ToString("N");
        const string output = "terminal-output-before-replace";
        var harness = await CreateStartedRunAsync(runId, WorkflowYaml(onFailure: false));
        var originalWorkflowYaml = harness.Agent.State.WorkflowYaml;
        var bindCountBefore = CommittedEvents<BindWorkflowRunDefinitionEvent>(harness.CommittedPublisher).Count;
        var failFirstCompletion = true;
        harness.Publisher.FailPublish = evt =>
            evt is WorkflowCompletedEvent && failFirstCompletion && !(failFirstCompletion = false);

        await FluentActions.Awaiting(() => harness.Agent.HandleEventAsync(
                SelfEnvelope(runId, SuccessfulStepCompletion(harness, output))))
            .Should()
            .ThrowAsync<WorkflowDurablePublicationPendingException>();

        await harness.Agent.HandleReplaceWorkflowDefinitionAndExecute(
            new ReplaceWorkflowDefinitionAndExecuteEvent
            {
                WorkflowYaml = WorkflowYaml(onFailure: false)
                    .Replace("wf_1859", "wf_replacement", StringComparison.Ordinal),
                Input = "replacement-input",
            });

        harness.Agent.State.Status.Should().Be("completed");
        harness.Agent.State.FinalOutput.Should().Be(output);
        harness.Agent.State.WorkflowYaml.Should().Be(originalWorkflowYaml);
        KernelState(harness.Agent).PendingWorkflowCompletion.Should().BeNull();
        CommittedEvents<BindWorkflowRunDefinitionEvent>(harness.CommittedPublisher)
            .Should().HaveCount(bindCountBefore);
    }

    [Fact]
    public async Task PendingCompletion_ShouldWinOverLiveDefinitionBind()
    {
        var runId = "run-completion-before-bind-" + Guid.NewGuid().ToString("N");
        const string output = "terminal-output-before-bind";
        var harness = await CreateStartedRunAsync(runId, WorkflowYaml(onFailure: false));
        var bindCountBefore = CommittedEvents<BindWorkflowRunDefinitionEvent>(harness.CommittedPublisher).Count;
        var failFirstCompletion = true;
        harness.Publisher.FailPublish = evt =>
            evt is WorkflowCompletedEvent && failFirstCompletion && !(failFirstCompletion = false);

        await FluentActions.Awaiting(() => harness.Agent.HandleEventAsync(
                SelfEnvelope(runId, SuccessfulStepCompletion(harness, output))))
            .Should()
            .ThrowAsync<WorkflowDurablePublicationPendingException>();

        await harness.Agent.HandleBindWorkflowRunDefinition(new BindWorkflowRunDefinitionEvent
        {
            DefinitionActorId = "definition-replacement",
            WorkflowName = "wf_replacement",
            WorkflowYaml = WorkflowYaml(onFailure: false)
                .Replace("wf_1859", "wf_replacement", StringComparison.Ordinal),
            RunId = runId,
            ScopeId = "scope-replacement",
            ExpectedExecutionMode = ExternalCapabilityExecutionMode.Interactive,
        });

        harness.Agent.State.Status.Should().Be("completed");
        harness.Agent.State.FinalOutput.Should().Be(output);
        KernelState(harness.Agent).PendingWorkflowCompletion.Should().BeNull();
        CommittedEvents<BindWorkflowRunDefinitionEvent>(harness.CommittedPublisher)
            .Should().HaveCount(bindCountBefore);
    }

    [Fact]
    public async Task PendingCompletionCommittedPublicationRecovery_ShouldPublishCompletionWithoutHandlerReplay()
    {
        var runId = "run-completion-commit-recovery-" + Guid.NewGuid().ToString("N");
        const string output = "completion-after-checkpoint-recovery";
        var harness = await CreateStartedRunAsync(runId, WorkflowYaml(onFailure: false));
        var failPendingIntentPublication = true;
        harness.CommittedPublisher.FailBeforePublish = evt =>
        {
            if (!failPendingIntentPublication ||
                evt is not WorkflowExecutionStateUpsertedEvent upserted ||
                upserted.ScopeKey != WorkflowExecutionKernel.ModuleStateKey ||
                upserted.State?.Is(WorkflowExecutionKernelState.Descriptor) != true ||
                upserted.State.Unpack<WorkflowExecutionKernelState>().PendingWorkflowCompletion == null)
            {
                return false;
            }

            failPendingIntentPublication = false;
            return true;
        };
        var original = SelfEnvelope(runId, SuccessfulStepCompletion(harness, output));

        await FluentActions.Awaiting(() => harness.Agent.HandleEventAsync(original))
            .Should()
            .ThrowAsync<CommittedStatePublicationException>();

        harness.Agent.State.Status.Should().Be("running");
        KernelState(harness.Agent).PendingWorkflowCompletion!.Output.Should().Be(output);
        harness.Publisher.PublishAttempts
            .Should().NotContain(static attempt => attempt.Event is WorkflowCompletedEvent);

        await harness.Agent.HandleEventAsync(CreatePublicationRetryEnvelope(original));

        harness.Agent.State.Status.Should().Be("completed");
        harness.Agent.State.FinalOutput.Should().Be(output);
        harness.Publisher.Published
            .Where(static item => item.Audience == TopologyAudience.Self)
            .Select(static item => item.Event)
            .OfType<WorkflowCompletedEvent>()
            .Should().ContainSingle()
            .Which.Output.Should().Be(output);
        KernelState(harness.Agent).PendingWorkflowCompletion.Should().BeNull();
    }

    [Fact]
    public async Task Activation_WithActiveKernelAndNoCompletionIntent_ShouldNotRecoverCompletion()
    {
        var runId = "run-active-no-completion-intent-" + Guid.NewGuid().ToString("N");
        var active = await CreateStartedRunAsync(runId, WorkflowYaml(onFailure: false));

        var recovered = await CreateRunAsync(
            runId,
            WorkflowYaml(onFailure: false),
            eventStore: active.EventStore);

        recovered.Agent.State.Status.Should().Be("running");
        KernelState(recovered.Agent).Active.Should().BeTrue();
        KernelState(recovered.Agent).PendingWorkflowCompletion.Should().BeNull();
        recovered.Publisher.PublishAttempts
            .Should().NotContain(static attempt => attempt.Event is WorkflowCompletedEvent);
    }

    [Fact]
    public async Task TerminalReconciliation_WithPersistedCompletion_ShouldPublishPendingTerminalEvent()
    {
        var runId = "run-reconcile-pending-" + Guid.NewGuid().ToString("N");
        var harness = await CreateStartedRunAsync(runId, WorkflowYaml(onFailure: false));
        var failFirstCompletion = true;
        harness.Publisher.FailPublish = evt =>
            evt is WorkflowCompletedEvent && failFirstCompletion && !(failFirstCompletion = false);

        await FluentActions.Awaiting(() => harness.Agent.HandleEventAsync(
                SelfEnvelope(runId, SuccessfulStepCompletion(harness, "pending-terminal-output"))))
            .Should()
            .ThrowAsync<WorkflowDurablePublicationPendingException>();

        await harness.Agent.HandleEventAsync(TerminalReconciliationEnvelope(runId, observedStateVersion: 91));

        harness.Agent.State.Status.Should().Be("completed");
        harness.Agent.State.FinalOutput.Should().Be("pending-terminal-output");
        (await harness.EventStore.GetEventsAsync(runId))
            .Count(static stored => stored.EventData?.Is(WorkflowCompletedEvent.Descriptor) == true)
            .Should().Be(1);
    }

    [Fact]
    public async Task StepCompletionRetry_WithPersistedCompletion_ShouldRecoverOnSameActivation()
    {
        var runId = "run-same-activation-retry-" + Guid.NewGuid().ToString("N");
        var harness = await CreateStartedRunAsync(runId, WorkflowYaml(onFailure: false));
        var failFirstCompletion = true;
        harness.Publisher.FailPublish = evt =>
            evt is WorkflowCompletedEvent && failFirstCompletion && !(failFirstCompletion = false);
        var completionEnvelope = SelfEnvelope(
            runId,
            SuccessfulStepCompletion(harness, "same-activation-terminal-output"));

        await FluentActions.Awaiting(() => harness.Agent.HandleEventAsync(completionEnvelope))
            .Should()
            .ThrowAsync<WorkflowDurablePublicationPendingException>();

        await harness.Agent.HandleEventAsync(completionEnvelope.Clone());

        harness.Agent.State.Status.Should().Be("completed");
        harness.Agent.State.FinalOutput.Should().Be("same-activation-terminal-output");
        (await harness.EventStore.GetEventsAsync(runId))
            .Count(static stored => stored.EventData?.Is(WorkflowCompletedEvent.Descriptor) == true)
            .Should().Be(1);
    }

    [Fact]
    public async Task TerminalReconciliation_WithLegacyInactiveKernel_ShouldPersistIntentBeforeFailingRun()
    {
        var runId = "run-reconcile-orphan-" + Guid.NewGuid().ToString("N");
        var harness = await CreateStartedRunAsync(runId, WorkflowYaml(onFailure: false));
        var kernel = KernelState(harness.Agent);
        kernel.Active = false;
        kernel.RunId = string.Empty;
        kernel.CurrentStepDispatchPending = false;
        kernel.CurrentStepTimeoutCallbackId = string.Empty;
        kernel.PendingWorkflowCompletion = null;
        await harness.Agent.UpsertExecutionStateAsync(
            WorkflowExecutionKernel.ModuleStateKey,
            Any.Pack(kernel));

        await harness.Agent.HandleEventAsync(TerminalReconciliationEnvelope(runId, observedStateVersion: 1465));

        harness.Agent.State.Status.Should().Be("failed");
        harness.Agent.State.FinalError.Should().Be(WorkflowRunGAgent.OrphanedExecutionTerminalError);
        var storedEvents = await harness.EventStore.GetEventsAsync(runId);
        var pendingIntent = storedEvents.Single(stored =>
            stored.EventData is { } eventData &&
            eventData.Is(WorkflowExecutionStateUpsertedEvent.Descriptor) &&
            eventData.Unpack<WorkflowExecutionStateUpsertedEvent>().State
                .Unpack<WorkflowExecutionKernelState>().PendingWorkflowCompletion != null);
        var completion = storedEvents.Single(stored =>
            stored.EventData is { } eventData &&
            eventData.Is(WorkflowCompletedEvent.Descriptor));
        pendingIntent.Version.Should().BeLessThan(completion.Version);
        completion.EventData.Unpack<WorkflowCompletedEvent>().Success.Should().BeFalse();
    }

    [Fact]
    public async Task TerminalReconciliation_WithMissingKernel_ShouldPersistIntentBeforeFailingRun()
    {
        var runId = "run-reconcile-missing-kernel-" + Guid.NewGuid().ToString("N");
        var harness = await CreateStartedRunAsync(runId, WorkflowYaml(onFailure: false));
        await harness.Agent.ClearExecutionStateAsync(WorkflowExecutionKernel.ModuleStateKey);

        await harness.Agent.HandleEventAsync(TerminalReconciliationEnvelope(runId, observedStateVersion: 1466));

        harness.Agent.State.Status.Should().Be("failed");
        harness.Agent.State.FinalError.Should().Be(WorkflowRunGAgent.OrphanedExecutionTerminalError);
        var storedEvents = await harness.EventStore.GetEventsAsync(runId);
        var pendingIntent = storedEvents.Single(stored =>
            stored.EventData is { } eventData &&
            eventData.Is(WorkflowExecutionStateUpsertedEvent.Descriptor) &&
            eventData.Unpack<WorkflowExecutionStateUpsertedEvent>().State
                .Unpack<WorkflowExecutionKernelState>().PendingWorkflowCompletion != null);
        var completion = storedEvents.Single(stored =>
            stored.EventData is { } eventData &&
            eventData.Is(WorkflowCompletedEvent.Descriptor));
        pendingIntent.Version.Should().BeLessThan(completion.Version);
    }

    [Fact]
    public async Task TerminalReconciliation_WithPendingStartAndMissingKernel_ShouldPreserveStartRecovery()
    {
        var runId = "run-reconcile-pending-start-" + Guid.NewGuid().ToString("N");
        var harness = await CreateStartedRunAsync(runId, WorkflowYaml(onFailure: false));
        await harness.Agent.ClearExecutionStateAsync(WorkflowExecutionKernel.ModuleStateKey);
        harness.Agent.State.PendingStartWorkflow = new StartWorkflowEvent
        {
            RunId = runId,
            WorkflowName = harness.Agent.State.WorkflowName,
        };

        await harness.Agent.HandleEventAsync(TerminalReconciliationEnvelope(runId, observedStateVersion: 1467));

        harness.Agent.State.Status.Should().Be("running");
        (await harness.EventStore.GetEventsAsync(runId))
            .Any(static stored =>
                stored.EventData is { } eventData &&
                eventData.Is(WorkflowCompletedEvent.Descriptor))
            .Should().BeFalse();
    }

    [Fact]
    public async Task TerminalReconciliation_WithPendingLlmCall_ShouldRequestCommittedChildCompletionRedelivery()
    {
        var runId = "run-reconcile-llm-" + Guid.NewGuid().ToString("N");
        var harness = await CreateStartedRunAsync(runId, WorkflowYaml(onFailure: false));
        var llmState = new LLMCallModuleState();
        llmState.PendingBySessionId["session-reconcile"] = new PendingLlmCallState
        {
            RunId = runId,
            StepId = "failed-step",
            ExecutionId = harness.StepExecutionId,
            TargetRole = "assistant",
            RequestDispatched = true,
        };
        await harness.Agent.UpsertExecutionStateAsync("llm_call", Any.Pack(llmState));
        harness.Publisher.Sent.Clear();

        await harness.Agent.HandleEventAsync(TerminalReconciliationEnvelope(runId, observedStateVersion: 1468));

        harness.Agent.State.Status.Should().Be("running");
        var sent = harness.Publisher.Sent.Should().ContainSingle().Subject;
        sent.TargetActorId.Should().Be($"{runId}:assistant");
        var reconcile = sent.Event.Should().BeOfType<ReconcileWorkflowLlmCompletionCommand>().Subject;
        reconcile.RunId.Should().Be(runId);
        reconcile.StepId.Should().Be("failed-step");
        reconcile.SessionId.Should().Be("session-reconcile");
        reconcile.ExecutionId.Should().Be(harness.StepExecutionId);
        reconcile.ObservedParentStateVersion.Should().Be(1468);
    }

    [Fact]
    public async Task TerminalReconciliation_WithLegacyPendingLlmCall_ShouldUseCurrentExecutionIdentityForRedelivery()
    {
        var runId = "run-reconcile-legacy-llm-" + Guid.NewGuid().ToString("N");
        var harness = await CreateStartedRunAsync(runId, WorkflowYaml(onFailure: false));
        var llmState = new LLMCallModuleState();
        llmState.PendingBySessionId["session-reconcile-legacy"] = new PendingLlmCallState
        {
            RunId = runId,
            StepId = "failed-step",
            ExecutionId = string.Empty,
            TargetRole = "assistant",
            RequestDispatched = true,
        };
        await harness.Agent.UpsertExecutionStateAsync("llm_call", Any.Pack(llmState));
        harness.Publisher.Sent.Clear();

        await harness.Agent.HandleEventAsync(TerminalReconciliationEnvelope(runId, observedStateVersion: 1469));

        var sent = harness.Publisher.Sent.Should().ContainSingle().Subject;
        var reconcile = sent.Event.Should().BeOfType<ReconcileWorkflowLlmCompletionCommand>().Subject;
        reconcile.SessionId.Should().Be("session-reconcile-legacy");
        reconcile.ExecutionId.Should().Be(harness.StepExecutionId);
    }

    [Fact]
    public async Task TerminalReconciliation_WithUnconfirmedLlmDispatch_ShouldRearmExactStepDispatch()
    {
        var runId = "run-reconcile-unconfirmed-llm-" + Guid.NewGuid().ToString("N");
        var harness = await CreateStartedRunAsync(runId, WorkflowYaml(onFailure: false));
        var llmState = new LLMCallModuleState();
        llmState.PendingBySessionId["session-reconcile-unconfirmed"] = new PendingLlmCallState
        {
            RunId = runId,
            StepId = "failed-step",
            ExecutionId = harness.StepExecutionId,
            TargetRole = "assistant",
            RequestDispatched = false,
        };
        await harness.Agent.UpsertExecutionStateAsync("llm_call", Any.Pack(llmState));
        harness.Publisher.Published.Clear();
        harness.Publisher.HandleSelfPublications = false;

        await harness.Agent.HandleEventAsync(TerminalReconciliationEnvelope(runId, observedStateVersion: 1470));

        KernelState(harness.Agent).CurrentStepDispatchPending.Should().BeTrue();
        harness.Publisher.Published
            .Should()
            .ContainSingle(item => item.Event is WorkflowExecutionRecoveryRequestedEvent)
            .Which.Event.Should().BeOfType<WorkflowExecutionRecoveryRequestedEvent>()
            .Which.RunId.Should().Be(runId);
    }

    [Fact]
    public async Task TerminalReconciliation_WithTerminalAuthoritativeState_ShouldRepublishCurrentStateWithoutNewCommit()
    {
        var runId = "run-reconcile-terminal-replica-" + Guid.NewGuid().ToString("N");
        var harness = await CreateStartedRunAsync(runId, WorkflowYaml(onFailure: false));
        await harness.Agent.HandleEventAsync(SelfEnvelope(
            runId,
            SuccessfulStepCompletion(harness, "terminal-output")));
        harness.Agent.State.Status.Should().Be("completed");
        var committedCount = (await harness.EventStore.GetEventsAsync(runId)).Count;
        harness.CommittedPublisher.Events.Clear();

        await harness.Agent.HandleEventAsync(TerminalReconciliationEnvelope(runId, observedStateVersion: 9));

        (await harness.EventStore.GetEventsAsync(runId)).Should().HaveCount(committedCount);
        var republished = harness.CommittedPublisher.Events.Should().ContainSingle().Subject;
        CommittedStateRepublish.IsRepublishEventId(republished.StateEvent.EventId).Should().BeTrue();
        republished.StateEvent.EventData.Is(WorkflowCompletedEvent.Descriptor).Should().BeTrue();
        republished.StateRoot.Unpack<WorkflowRunState>().Status.Should().Be("completed");
    }

    [Fact]
    public async Task TerminalReconciliation_WithActiveKernel_ShouldPreserveLegitimateWait()
    {
        var runId = "run-reconcile-active-" + Guid.NewGuid().ToString("N");
        var harness = await CreateStartedRunAsync(runId, WorkflowYaml(onFailure: false));

        await harness.Agent.HandleEventAsync(TerminalReconciliationEnvelope(runId, observedStateVersion: 77));

        harness.Agent.State.Status.Should().Be("running");
        KernelState(harness.Agent).Active.Should().BeTrue();
        harness.Publisher.PublishAttempts.Should().NotContain(static attempt =>
            attempt.Event is WorkflowCompletedEvent &&
            attempt.Audience == TopologyAudience.Self);
        (await harness.EventStore.GetEventsAsync(runId))
            .Any(static stored =>
                stored.EventData is { } eventData &&
                eventData.Is(WorkflowCompletedEvent.Descriptor))
            .Should().BeFalse();
    }

    [Fact]
    public async Task TerminalReconciliation_WithStrandedEmitCompletion_ShouldFailExactExecutionWithoutRedispatch()
    {
        var runId = "run-reconcile-emit-" + Guid.NewGuid().ToString("N");
        var harness = await CreateStartedRunAsync(runId, EmitWorkflowYaml());
        harness.Publisher.Published.Clear();
        harness.Publisher.PublishAttempts.Clear();

        await harness.Agent.HandleEventAsync(TerminalReconciliationEnvelope(runId, observedStateVersion: 90));

        harness.Agent.State.Status.Should().Be("failed");
        harness.Agent.State.FinalError.Should().Contain("emit step completion was not observed");
        harness.Publisher.PublishAttempts
            .Select(static attempt => attempt.Event)
            .OfType<StepRequestEvent>()
            .Should().NotContain(request => request.StepId == "announce-job");
        var recoveredCompletion = harness.Publisher.PublishAttempts
            .Where(static attempt => attempt.Audience == TopologyAudience.Self)
            .Select(static attempt => attempt.Event)
            .OfType<StepCompletedEvent>()
            .Should().ContainSingle().Subject;
        recoveredCompletion.StepId.Should().Be("announce-job");
        recoveredCompletion.ExecutionId.Should().Be(harness.StepExecutionId);
        recoveredCompletion.FailureOutcome.Should().Be(WorkflowStepFailureOutcome.OutcomeUncertain);
        recoveredCompletion.RetryDisposition.Should().Be(WorkflowStepRetryDisposition.Forbidden);
    }

    [Fact]
    public async Task TerminalReconciliation_WithMismatchedRunId_ShouldNotChangeAuthoritativeState()
    {
        var runId = "run-reconcile-mismatch-" + Guid.NewGuid().ToString("N");
        var harness = await CreateStartedRunAsync(runId, WorkflowYaml(onFailure: false));

        await harness.Agent.HandleEventAsync(TerminalReconciliationEnvelope("run-other", observedStateVersion: 77));

        harness.Agent.State.Status.Should().Be("running");
        KernelState(harness.Agent).Active.Should().BeTrue();
        (await harness.EventStore.GetEventsAsync(runId))
            .Any(static stored =>
                stored.EventData is { } eventData &&
                eventData.Is(WorkflowCompletedEvent.Descriptor))
            .Should().BeFalse();
    }

    [Fact]
    public async Task TerminalReconciliation_WithCompensationInProgress_ShouldNotInferLegacyFailure()
    {
        var runId = "run-reconcile-compensating-" + Guid.NewGuid().ToString("N");
        var harness = await CreateStartedRunAsync(runId, WorkflowYaml(onFailure: false));
        var kernel = KernelState(harness.Agent);
        kernel.Active = false;
        kernel.RunId = string.Empty;
        kernel.PendingWorkflowCompletion = null;
        await harness.Agent.UpsertExecutionStateAsync(
            WorkflowExecutionKernel.ModuleStateKey,
            Any.Pack(kernel));
        harness.Agent.State.SagaStatus = WorkflowSagaStatus.Compensating;

        await harness.Agent.HandleEventAsync(TerminalReconciliationEnvelope(runId, observedStateVersion: 88));

        harness.Agent.State.Status.Should().Be("running");
        (await harness.EventStore.GetEventsAsync(runId))
            .Any(static stored =>
                stored.EventData is { } eventData &&
                eventData.Is(WorkflowCompletedEvent.Descriptor))
            .Should().BeFalse();
    }

    [Fact]
    public async Task CompletionIntentRecovery_ShouldRemainIdempotentAcrossRepeatedActivation()
    {
        var runId = "run-completion-idempotent-" + Guid.NewGuid().ToString("N");
        var harness = await CreateStartedRunAsync(runId, WorkflowYaml(onFailure: false));
        var failFirstCompletion = true;
        harness.Publisher.FailPublish = evt =>
            evt is WorkflowCompletedEvent && failFirstCompletion && !(failFirstCompletion = false);

        await FluentActions.Awaiting(() => harness.Agent.HandleEventAsync(
                SelfEnvelope(runId, SuccessfulStepCompletion(harness, "one-terminal-result"))))
            .Should()
            .ThrowAsync<WorkflowDurablePublicationPendingException>();

        var firstRecovery = await CreateRunAsync(
            runId,
            WorkflowYaml(onFailure: false),
            eventStore: harness.EventStore);
        var secondRecovery = await CreateRunAsync(
            runId,
            WorkflowYaml(onFailure: false),
            eventStore: harness.EventStore);

        firstRecovery.Publisher.Published
            .Where(static item => item.Audience == TopologyAudience.Self)
            .Select(static item => item.Event)
            .OfType<WorkflowCompletedEvent>()
            .Should().ContainSingle();
        secondRecovery.Publisher.PublishAttempts
            .Should().NotContain(static attempt => attempt.Event is WorkflowCompletedEvent);
        (await harness.EventStore.GetEventsAsync(runId))
            .Count(static stored => stored.EventData?.Is(WorkflowCompletedEvent.Descriptor) == true)
            .Should().Be(1);
    }

    [Fact]
    public async Task ChatRequest_WithInputFileRef_ShouldBindArtifactOwnerBeforeStartingWorkflow()
    {
        var runId = "run-1917-" + Guid.NewGuid().ToString("N");
        var ownershipPort = new RecordingWorkflowFileArtifactOwnershipPort();
        var harness = await CreateRunAsync(runId, WorkflowYaml(onFailure: false), ownershipPort);

        await harness.Agent.HandleEventAsync(EnvelopeFrom("api", new WorkflowChatRequestEvent
        {
            Prompt = "hello",
            ScopeId = "scope-1",
            InputParts =
            {
                new WorkflowChatInputPartPayload
                {
                    Kind = WorkflowChatInputPartKind.Text,
                    FileRef = new WorkflowFileRef
                    {
                        FileId = "wf-file-123",
                        ArtifactId = "workflow-file://wf-file-123",
                        SourceKind = WorkflowFileSourceKind.ChatInput,
                        FileName = "invoice.txt",
                        MediaType = "text/plain",
                        SizeBytes = 7,
                        Sha256 = "hash-1",
                    },
                },
            },
        }));

        ownershipPort.Bindings.Should().ContainSingle().Which.Should().BeEquivalentTo(new FileOwnerBinding(
            "wf-file-123",
            "workflow-file://wf-file-123",
            runId,
            "scope-1"));
        harness.Publisher.Published
            .Where(x => x.Event is StepRequestEvent)
            .Should()
            .ContainSingle();
    }

    [Fact]
    public async Task ChatRequest_WithFileIdOnlyInputFileRef_ShouldBindArtifactOwnerBeforeStartingWorkflow()
    {
        var runId = "run-1917-" + Guid.NewGuid().ToString("N");
        var ownershipPort = new RecordingWorkflowFileArtifactOwnershipPort();
        var harness = await CreateRunAsync(runId, WorkflowYaml(onFailure: false), ownershipPort);

        await harness.Agent.HandleEventAsync(EnvelopeFrom("api", new WorkflowChatRequestEvent
        {
            Prompt = "hello",
            ScopeId = "scope-1",
            InputParts =
            {
                new WorkflowChatInputPartPayload
                {
                    Kind = WorkflowChatInputPartKind.File,
                    FileRef = new WorkflowFileRef
                    {
                        FileId = "wf-file-only-123",
                        SourceKind = WorkflowFileSourceKind.ChatInput,
                        FileName = "invoice.txt",
                        MediaType = "text/plain",
                    },
                },
            },
        }));

        ownershipPort.Bindings.Should().ContainSingle().Which.Should().BeEquivalentTo(new FileOwnerBinding(
            "wf-file-only-123",
            string.Empty,
            runId,
            "scope-1"));
        harness.Publisher.Published
            .Where(x => x.Event is StepRequestEvent)
            .Should()
            .ContainSingle();
    }

    [Fact]
    public async Task ChatRequest_WhenInputFileOwnerBindingFails_ShouldNotStartWorkflow()
    {
        var runId = "run-1917-" + Guid.NewGuid().ToString("N");
        var ownershipPort = new RecordingWorkflowFileArtifactOwnershipPort
        {
            Exception = new InvalidOperationException("owner already bound"),
        };
        var harness = await CreateRunAsync(runId, WorkflowYaml(onFailure: false), ownershipPort);

        await harness.Agent.HandleEventAsync(EnvelopeFrom("api", new WorkflowChatRequestEvent
        {
            Prompt = "hello",
            ScopeId = "scope-1",
            InputParts =
            {
                new WorkflowChatInputPartPayload
                {
                    Kind = WorkflowChatInputPartKind.Text,
                    FileRef = new WorkflowFileRef
                    {
                        FileId = "wf-file-123",
                        ArtifactId = "workflow-file://wf-file-123",
                        SourceKind = WorkflowFileSourceKind.ChatInput,
                        SizeBytes = 7,
                    },
                },
            },
        }));

        harness.Publisher.Published
            .Where(x => x.Event is StepRequestEvent)
            .Should()
            .BeEmpty();
        harness.Publisher.Published
            .Where(x => x.Event is WorkflowLlmInvocationCompletedEvent)
            .Select(x => (WorkflowLlmInvocationCompletedEvent)x.Event)
            .Should()
            .ContainSingle()
            .Which.Error.Should().Be("workflow_input_file_binding_failed");
    }

    [Fact]
    public async Task ChatRequest_WhenStartSelfPublishFails_ShouldCommitTerminalFailure()
    {
        var runId = "run-1917-" + Guid.NewGuid().ToString("N");
        var harness = await CreateRunAsync(runId, WorkflowYaml(onFailure: false));
        harness.Publisher.FailPublish = evt => evt is StartWorkflowEvent;

        await harness.Agent.HandleEventAsync(EnvelopeFrom("api", new WorkflowChatRequestEvent
        {
            Prompt = "hello",
            ScopeId = "scope-1",
        }));

        var started = CommittedEvents<WorkflowRunExecutionStartedEvent>(harness.CommittedPublisher)
            .Should()
            .ContainSingle()
            .Subject;
        started.RunId.Should().Be(runId);
        started.PendingStartWorkflow.Should().NotBeNull();
        started.PendingStartWorkflow.RunId.Should().Be(runId);
        started.PendingStartWorkflow.Input.Should().Be("hello");
        var completed = CommittedEvents<WorkflowCompletedEvent>(harness.CommittedPublisher)
            .Should()
            .ContainSingle()
            .Subject;
        completed.RunId.Should().Be(runId);
        completed.Success.Should().BeFalse();
        completed.Error.Should().StartWith("start_dispatch_failed: failed during start_dispatch: ");
        completed.Error.Should().NotContain("super-secret-token");
        completed.Error.Should().NotContain("Bearer");
        harness.Agent.State.FinalError.Should().Be(completed.Error);
        harness.Agent.State.PendingStartWorkflow.Should().BeNull();
    }

    [Fact]
    public async Task ReplaceWorkflowDefinitionAndExecute_WhenStartSelfPublishFails_ShouldCommitTerminalFailure()
    {
        var runId = "run-1917-" + Guid.NewGuid().ToString("N");
        var harness = await CreateRunAsync(runId, WorkflowYaml(onFailure: false));
        harness.Publisher.FailPublish = evt => evt is StartWorkflowEvent;

        await harness.Agent.HandleEventAsync(EnvelopeFrom("api", new ReplaceWorkflowDefinitionAndExecuteEvent
        {
            WorkflowYaml = WorkflowYaml(onFailure: false),
            Input = "direct-input",
        }));

        var started = CommittedEvents<WorkflowRunExecutionStartedEvent>(harness.CommittedPublisher)
            .Should()
            .ContainSingle()
            .Subject;
        started.Input.Should().Be("direct-input");
        started.PendingStartWorkflow.Should().NotBeNull();
        started.PendingStartWorkflow.Input.Should().Be("direct-input");
        var completed = CommittedEvents<WorkflowCompletedEvent>(harness.CommittedPublisher)
            .Should()
            .ContainSingle()
            .Subject;
        completed.RunId.Should().Be(runId);
        completed.Success.Should().BeFalse();
        completed.Error.Should().StartWith("start_dispatch_failed: failed during start_dispatch: ");
        completed.Error.Should().NotContain("super-secret-token");
        completed.Error.Should().NotContain("Bearer");
        harness.Agent.State.PendingStartWorkflow.Should().BeNull();
    }

    [Fact]
    public async Task ChatRequest_WhenStartTerminalizationPublicationFails_ShouldPropagateWithoutParentFailure()
    {
        var runId = "run-1917-" + Guid.NewGuid().ToString("N");
        var harness = await CreateRunAsync(runId, WorkflowYaml(onFailure: false));
        harness.Publisher.FailPublish = evt => evt is StartWorkflowEvent;
        harness.CommittedPublisher.FailBeforePublish = evt => evt is WorkflowCompletedEvent;

        await FluentActions.Awaiting(() => harness.Agent.HandleEventAsync(
                EnvelopeFrom("api", new WorkflowChatRequestEvent
                {
                    Prompt = "hello",
                    ScopeId = "scope-1",
                    SessionId = "session-1",
                })))
            .Should()
            .ThrowAsync<CommittedStatePublicationException>();

        harness.Publisher.Published
            .Select(static published => published.Event)
            .Should()
            .NotContain(published => published is WorkflowLlmInvocationCompletedEvent);
    }

    [Fact]
    public async Task ChatRequest_ReplayedWithSameCommandId_ShouldStartRunOnlyOnce()
    {
        var runId = "work-order-run-" + Guid.NewGuid().ToString("N");
        var harness = await CreateRunAsync(runId, WorkflowYaml(onFailure: false));
        var envelope = EnvelopeFrom("work-order", new WorkflowChatRequestEvent
        {
            Prompt = "hello",
            ScopeId = "scope-1",
        });
        envelope.Id = "work-order-dispatch-command-1";
        envelope.Propagation.CorrelationId = "work-order-dispatch-command-1";

        await harness.Agent.HandleEventAsync(envelope);
        await harness.Agent.HandleEventAsync(envelope.Clone());

        CommittedEvents<WorkflowRunExecutionStartedEvent>(harness.CommittedPublisher)
            .Should().ContainSingle();
        harness.Publisher.Published.Count(item => item.Event is StartWorkflowEvent)
            .Should().Be(1);
        harness.Agent.State.LastCommandId.Should().Be("work-order-dispatch-command-1");
    }

    [Fact]
    public async Task ChatRequest_ConcurrentRedeliveryWithSameCommandId_ShouldStartRunOnlyOnce()
    {
        var runId = "work-order-run-" + Guid.NewGuid().ToString("N");
        var ownershipPort = new BlockingWorkflowFileArtifactOwnershipPort();
        var harness = await CreateRunAsync(runId, WorkflowYaml(onFailure: false), ownershipPort);
        var envelope = EnvelopeFrom("work-order", new WorkflowChatRequestEvent
        {
            Prompt = "hello",
            ScopeId = "scope-1",
            InputParts =
            {
                new WorkflowChatInputPartPayload
                {
                    Kind = WorkflowChatInputPartKind.File,
                    FileRef = new WorkflowFileRef
                    {
                        FileId = "wf-file-concurrent-123",
                        SourceKind = WorkflowFileSourceKind.ChatInput,
                        FileName = "invoice.txt",
                        MediaType = "text/plain",
                    },
                },
            },
        });
        envelope.Id = "work-order-dispatch-command-concurrent";
        envelope.Propagation.CorrelationId = "work-order-dispatch-command-concurrent";

        var first = harness.Agent.HandleEventAsync(envelope);
        await ownershipPort.FirstBindingStarted;
        var redelivery = harness.Agent.HandleEventAsync(envelope.Clone());
        ownershipPort.Release();
        await Task.WhenAll(first, redelivery);

        ownershipPort.BindingCount.Should().Be(1);
        CommittedEvents<WorkflowRunExecutionStartedEvent>(harness.CommittedPublisher)
            .Should().ContainSingle();
        harness.Publisher.Published.Count(item => item.Event is StartWorkflowEvent)
            .Should().Be(1);
    }

    private static Task<RunHarness> CreateStartedRunAsync(string workflowYaml, int attempt) =>
        CreateStartedRunAsync(
            "run-1859-" + Guid.NewGuid().ToString("N"),
            workflowYaml,
            attempt);

    private static async Task<RunHarness> CreateStartedRunAsync(
        string runId,
        string workflowYaml,
        int attempt = 0)
    {
        var harness = await CreateRunAsync(runId, workflowYaml);

        await harness.Agent.HandleEventAsync(EnvelopeFrom("api", new WorkflowChatRequestEvent
        {
            Prompt = "hello",
            ScopeId = "scope-1",
            ForkSeed = new WorkflowRunForkSeed
            {
                Attempt = attempt,
            },
        }));

        var stepRequest = harness.Publisher.Published
            .Where(x => x.Event is StepRequestEvent)
            .Select(x => (StepRequestEvent)x.Event)
            .Should()
            .ContainSingle()
            .Subject;

        return harness with { StepExecutionId = stepRequest.ExecutionId };
    }

    private static async Task<RunHarness> CreateUnboundRunAsync(string runId)
    {
        var eventStore = new RecordingEventStore();
        var committedHook = new RecordingCommittedStatePublicationHook();
        var topologyPublisher = new RecordingEventPublisher(runId);
        var agent = new WorkflowRunGAgent(
            new UnsupportedActorRuntime(),
            new UnsupportedActorRuntime(),
            new EmptyEventModuleFactory(),
            [new EmptyWorkflowModulePack()])
        {
            EventSourcingBehaviorFactory = new DefaultEventSourcingBehaviorFactory<WorkflowRunState>(eventStore),
            EventPublisher = topologyPublisher,
            Services = new TestServiceProvider(new NoopRuntimeCallbackScheduler(), committedHook),
            Logger = NullLogger.Instance,
        };
        SetAgentId(agent, runId);
        topologyPublisher.Agent = agent;
        await agent.ActivateAsync();
        return new RunHarness(agent, runId, string.Empty, eventStore, committedHook, topologyPublisher);
    }

    private static async Task<RunHarness> CreateRunAsync(
        string runId,
        string workflowYaml,
        Aevatar.Workflow.Application.Abstractions.Runs.IFileArtifactOwnershipPort? fileOwnershipPort = null,
        RecordingEventStore? eventStore = null)
    {
        eventStore ??= new RecordingEventStore();
        var committedHook = new RecordingCommittedStatePublicationHook();
        var topologyPublisher = new RecordingEventPublisher(runId);
        var agent = new WorkflowRunGAgent(
            new UnsupportedActorRuntime(),
            new UnsupportedActorRuntime(),
            new EmptyEventModuleFactory(),
            [new EmptyWorkflowModulePack()],
            fileArtifactOwnership: fileOwnershipPort)
        {
            EventSourcingBehaviorFactory = new DefaultEventSourcingBehaviorFactory<WorkflowRunState>(eventStore),
            EventPublisher = topologyPublisher,
            Services = new TestServiceProvider(new NoopRuntimeCallbackScheduler(), committedHook),
            Logger = NullLogger.Instance,
        };
        SetAgentId(agent, runId);
        topologyPublisher.Agent = agent;
        await agent.ActivateAsync();

        if (string.IsNullOrWhiteSpace(agent.State.WorkflowYaml))
        {
            await agent.HandleEventAsync(EnvelopeFrom("workflow-run-actor-port", new BindWorkflowRunDefinitionEvent
            {
                DefinitionActorId = "definition-1859",
                WorkflowName = "wf_1859",
                WorkflowYaml = workflowYaml,
                RunId = runId,
                ScopeId = "scope-1",
                ExpectedExecutionMode = ExternalCapabilityExecutionMode.Interactive,
            }));
        }

        return new RunHarness(agent, runId, string.Empty, eventStore, committedHook, topologyPublisher);
    }

    private static StepCompletedEvent SuccessfulStepCompletion(RunHarness harness, string output) =>
        new()
        {
            RunId = harness.RunId,
            StepId = "failed-step",
            Success = true,
            Output = output,
            ExecutionId = harness.StepExecutionId,
        };

    private static WorkflowExecutionKernelState KernelState(WorkflowRunGAgent agent) =>
        agent.State.ExecutionStates[WorkflowExecutionKernel.ModuleStateKey]
            .Unpack<WorkflowExecutionKernelState>();

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

    private static EventEnvelope TerminalReconciliationEnvelope(
        string runId,
        long observedStateVersion) =>
        EnvelopeFrom("workflow.run.terminal-recovery", new ReconcileWorkflowTerminalStateCommand
        {
            RunId = runId,
            ObservedStateVersion = observedStateVersion,
        });

    private static WorkflowRunForkRequestedEvent? TryUnpackForkRequest(CommittedStateEventPublished published)
    {
        if (published.StateEvent?.EventData?.Is(WorkflowRunForkRequestedEvent.Descriptor) != true)
            return null;

        return published.StateEvent.EventData.Unpack<WorkflowRunForkRequestedEvent>();
    }

    private static IReadOnlyList<TEvent> CommittedEvents<TEvent>(RecordingCommittedStatePublicationHook hook)
        where TEvent : class, IMessage<TEvent>, new()
    {
        var descriptor = new TEvent().Descriptor;
        return hook.Events
            .Where(x => x.StateEvent?.EventData?.Is(descriptor) == true)
            .Select(x => x.StateEvent!.EventData.Unpack<TEvent>())
            .ToArray();
    }

    private static EventEnvelope SelfEnvelope(string runId, IMessage payload) =>
        EnvelopeFrom(runId, payload);

    private static EventEnvelope EnvelopeFrom(string publisherActorId, IMessage payload) =>
        new()
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
            Payload = Any.Pack(payload),
            Route = EnvelopeRouteSemantics.CreateTopologyPublication(publisherActorId, TopologyAudience.Self),
            Propagation = new EnvelopePropagation
            {
                CorrelationId = Guid.NewGuid().ToString("N"),
            },
        };

    private static string WorkflowYaml(bool onFailure, int maxAttempts = 3) =>
        onFailure
            ? $$"""
                name: wf_1859
                on_failure:
                  action: fork_from_failed_step
                  max_attempts: {{maxAttempts}}
                roles: []
                steps:
                  - id: failed-step
                    type: transform
                """
            : """
                name: wf_1859
                roles: []
                steps:
                  - id: failed-step
                    type: transform
                """;

    private static string EmitWorkflowYaml() =>
        """
        name: wf_emit_recovery
        roles: []
        steps:
          - id: announce-job
            type: emit
            parameters:
              event_type: codex.job.requested
              payload: $input
        """;

    private static void SetAgentId(GAgentBase agent, string agentId)
    {
        var setIdMethod = typeof(GAgentBase).GetMethod(
            "SetId",
            BindingFlags.Instance | BindingFlags.NonPublic);
        setIdMethod.Should().NotBeNull();
        setIdMethod!.Invoke(agent, [agentId]);
    }

    private sealed record RunHarness(
        WorkflowRunGAgent Agent,
        string RunId,
        string StepExecutionId,
        RecordingEventStore EventStore,
        RecordingCommittedStatePublicationHook CommittedPublisher,
        RecordingEventPublisher Publisher);

    private sealed record FileOwnerBinding(
        string? FileId,
        string? ArtifactId,
        string OwnerRunId,
        string? OwnerScopeId);

    private sealed class RecordingWorkflowFileArtifactOwnershipPort :
        Aevatar.Workflow.Application.Abstractions.Runs.IFileArtifactOwnershipPort
    {
        public List<FileOwnerBinding> Bindings { get; } = [];

        public Exception? Exception { get; init; }

        public ValueTask BindOwnerAsync(
            Aevatar.Workflow.Application.Abstractions.Runs.FileArtifactRef fileRef,
            string ownerRunId,
            string? ownerScopeId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Exception != null)
                throw Exception;

            Bindings.Add(new FileOwnerBinding(
                fileRef.FileId,
                fileRef.ArtifactId,
                ownerRunId,
                ownerScopeId));
            return ValueTask.CompletedTask;
        }
    }

    private sealed class BlockingWorkflowFileArtifactOwnershipPort :
        Aevatar.Workflow.Application.Abstractions.Runs.IFileArtifactOwnershipPort
    {
        private readonly TaskCompletionSource _firstBindingStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _bindingCount;

        public Task FirstBindingStarted => _firstBindingStarted.Task;

        public int BindingCount => Volatile.Read(ref _bindingCount);

        public async ValueTask BindOwnerAsync(
            Aevatar.Workflow.Application.Abstractions.Runs.FileArtifactRef fileRef,
            string ownerRunId,
            string? ownerScopeId,
            CancellationToken cancellationToken = default)
        {
            _ = fileRef;
            _ = ownerRunId;
            _ = ownerScopeId;
            Interlocked.Increment(ref _bindingCount);
            _firstBindingStarted.TrySetResult();
            await _release.Task.WaitAsync(cancellationToken);
        }

        public void Release() => _release.TrySetResult();
    }

    private sealed class EmptyWorkflowModulePack : IWorkflowModulePack
    {
        public string Name => "test.empty";

        public IReadOnlyList<WorkflowModuleRegistration> Modules { get; } =
        [
            WorkflowModuleRegistration.Create<TransformModule>("transform"),
        ];

        public IReadOnlyList<IWorkflowModuleDependencyExpander> DependencyExpanders { get; } = [];

        public IReadOnlyList<IWorkflowModuleConfigurator> Configurators { get; } = [];
    }

    private sealed class EmptyEventModuleFactory : IEventModuleFactory<IWorkflowExecutionContext>
    {
        public bool TryCreate(string name, out IEventModule<IWorkflowExecutionContext>? module)
        {
            _ = name;
            module = null;
            return false;
        }
    }

    private sealed class RecordingEventPublisher(string runId) : IEventPublisher
    {
        public List<(IMessage Event, TopologyAudience Audience)> Published { get; } = [];

        public List<(IMessage Event, TopologyAudience Audience, EventEnvelopePublishOptions? Options)> PublishAttempts { get; } = [];

        public List<(string TargetActorId, IMessage Event)> Sent { get; } = [];

        public Func<IMessage, bool>? FailPublish { get; set; }

        public bool HandleSelfPublications { get; set; } = true;

        public async Task PublishAsync<T>(
            T evt,
            TopologyAudience audience,
            CancellationToken ct,
            EventEnvelope? sourceEnvelope,
            EventEnvelopePublishOptions? options)
            where T : IMessage
        {
            ct.ThrowIfCancellationRequested();
            PublishAttempts.Add((
                evt.Descriptor.Parser.ParseFrom(evt.ToByteArray()),
                audience,
                options?.DeepClone()));
            if (FailPublish?.Invoke(evt) == true)
                throw new InvalidOperationException("start failed with bearer super-secret-token");

            Published.Add((evt, audience));

            if (audience == TopologyAudience.Self && HandleSelfPublications)
                await Agent.HandleEventAsync(SelfEnvelope(runId, evt), ct);
        }

        public WorkflowRunGAgent Agent { get; set; } = null!;

        public Task SendToAsync<T>(
            string targetActorId,
            T evt,
            CancellationToken ct,
            EventEnvelope? sourceEnvelope,
            EventEnvelopePublishOptions? options)
            where T : IMessage
        {
            ct.ThrowIfCancellationRequested();
            Sent.Add((targetActorId, evt.Descriptor.Parser.ParseFrom(evt.ToByteArray())));
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingCommittedStatePublicationHook : ICommittedStatePublicationHook
    {
        public List<CommittedStateEventPublished> Events { get; } = [];

        public Func<IMessage, bool>? FailBeforePublish { get; set; }

        public Task BeforePublishAsync(CommittedStatePublicationContext context, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (context.Published.StateEvent?.EventData is { } eventData)
            {
                var message = TryUnpackKnownEvent(eventData);
                if (message != null && FailBeforePublish?.Invoke(message) == true)
                    throw new InvalidOperationException("committed publication failed with bearer super-secret-token");
            }

            Events.Add(context.Published.Clone());
            return Task.CompletedTask;
        }

        private static IMessage? TryUnpackKnownEvent(Any eventData)
        {
            if (eventData.Is(WorkflowCompletedEvent.Descriptor))
                return eventData.Unpack<WorkflowCompletedEvent>();
            if (eventData.Is(WorkflowRunExecutionStartedEvent.Descriptor))
                return eventData.Unpack<WorkflowRunExecutionStartedEvent>();
            if (eventData.Is(WorkflowRunForkRequestedEvent.Descriptor))
                return eventData.Unpack<WorkflowRunForkRequestedEvent>();
            if (eventData.Is(WorkflowExecutionStateUpsertedEvent.Descriptor))
                return eventData.Unpack<WorkflowExecutionStateUpsertedEvent>();

            return null;
        }
    }

    private sealed class RecordingEventStore : IEventStore
    {
        private readonly Dictionary<string, List<StateEvent>> _streams = new(StringComparer.Ordinal);

        public Task<EventStoreCommitResult> AppendAsync(
            string agentId,
            IEnumerable<StateEvent> events,
            long expectedVersion,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            var stream = _streams.GetValueOrDefault(agentId) ?? [];
            var currentVersion = stream.Count == 0 ? 0 : stream[^1].Version;
            currentVersion.Should().Be(expectedVersion);

            var committed = events.Select(x => x.Clone()).ToList();
            stream.AddRange(committed);
            _streams[agentId] = stream;

            return Task.FromResult(new EventStoreCommitResult
            {
                AgentId = agentId,
                LatestVersion = stream.Count == 0 ? 0 : stream[^1].Version,
                CommittedEvents = { committed },
            });
        }

        public Task<IReadOnlyList<StateEvent>> GetEventsAsync(
            string agentId,
            long? fromVersion = null,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            var events = _streams.GetValueOrDefault(agentId) ?? [];
            return Task.FromResult<IReadOnlyList<StateEvent>>(
                events
                    .Where(x => !fromVersion.HasValue || x.Version >= fromVersion.Value)
                    .Select(x => x.Clone())
                    .ToArray());
        }

        public Task<long> GetVersionAsync(string agentId, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            var events = _streams.GetValueOrDefault(agentId) ?? [];
            return Task.FromResult(events.Count == 0 ? 0 : events[^1].Version);
        }

        public Task<long> DeleteEventsUpToAsync(
            string agentId,
            long toVersion,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            if (!_streams.TryGetValue(agentId, out var stream))
                return Task.FromResult(0L);

            var removed = stream.RemoveAll(x => x.Version <= toVersion);
            return Task.FromResult((long)removed);
        }
    }

    private sealed class TestServiceProvider(
        NoopRuntimeCallbackScheduler scheduler,
        RecordingCommittedStatePublicationHook committedHook) : IServiceProvider
    {
        public object? GetService(System.Type serviceType)
        {
            if (serviceType == typeof(IEnumerable<IGAgentExecutionHook>))
                return Array.Empty<IGAgentExecutionHook>();
            if (serviceType == typeof(IActorRuntimeCallbackScheduler))
                return scheduler;
            if (serviceType == typeof(IEnumerable<ICommittedStatePublicationHook>))
                return new ICommittedStatePublicationHook[] { committedHook };

            return null;
        }
    }

    private sealed class NoopRuntimeCallbackScheduler : IActorRuntimeCallbackScheduler
    {
        public Task<RuntimeCallbackLease> ScheduleTimeoutAsync(
            RuntimeCallbackTimeoutRequest request,
            CancellationToken ct = default) =>
            Task.FromResult(new RuntimeCallbackLease(
                request.ActorId,
                request.CallbackId,
                1,
                RuntimeCallbackBackend.InMemory));

        public Task<RuntimeCallbackLease> ScheduleTimerAsync(
            RuntimeCallbackTimerRequest request,
            CancellationToken ct = default) =>
            Task.FromResult(new RuntimeCallbackLease(
                request.ActorId,
                request.CallbackId,
                1,
                RuntimeCallbackBackend.InMemory));

        public Task CancelAsync(RuntimeCallbackLease lease, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task PurgeActorAsync(string actorId, CancellationToken ct = default) =>
            Task.CompletedTask;
    }

    private sealed class UnsupportedActorRuntime : IActorRuntime, IActorDispatchPort
    {
        public Task<IActor> CreateAsync<TAgent>(string? id = null, CancellationToken ct = default)
            where TAgent : IAgent =>
            throw new NotSupportedException();

        public Task<IActor> CreateAsync(System.Type agentType, string? id = null, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task DestroyAsync(string id, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<IActor?> GetAsync(string id) =>
            throw new NotSupportedException();

        public Task<bool> ExistsAsync(string id) =>
            throw new NotSupportedException();

        public Task LinkAsync(string parentId, string childId, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task UnlinkAsync(string childId, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<DispatchAdmission> DispatchAsync(string actorId, EventEnvelope envelope, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }
}
