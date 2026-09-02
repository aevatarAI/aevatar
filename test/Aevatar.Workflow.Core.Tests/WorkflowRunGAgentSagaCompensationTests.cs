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

public sealed class WorkflowRunGAgentSagaCompensationTests
{
    [Fact]
    public async Task InFlightSideEffectingCompensableTimeout_ShouldDispatchProvisionalCompensation()
    {
        var harness = await CreateStartedRunAsync(InFlightCompensationWorkflowYaml());
        var request = StepRequests(harness.Publisher, "create_order").Single();
        var timeout = ScheduledTimeouts(harness.Publisher)
            .Should()
            .ContainSingle(x => x.Event is WorkflowStepTimeoutFiredEvent)
            .Subject;
        harness.Publisher.Published.Clear();

        await harness.Agent.HandleEventAsync(SelfEnvelope(
            harness.RunId,
            new WorkflowStepTimeoutFiredEvent
            {
                RunId = harness.RunId,
                StepId = "create_order",
                TimeoutMs = 500,
            },
            MetadataFor(timeout)));

        harness.Agent.State.CompensableLedger
            .Should()
            .ContainSingle()
            .Which.Should().BeEquivalentTo(new CompletedStepLedgerEntry
            {
                StepId = "create_order",
                CompensationStepId = "cancel_order",
                IdempotencyKey = request.IdempotencyKey,
                CapturedOutput = string.Empty,
                LedgerStatus = CompensableLedgerEntryStatus.Provisional,
            });

        var compensation = CompensationRequests(harness.Publisher).Should().ContainSingle().Subject;
        compensation.CompensationStepId.Should().Be("cancel_order");
        compensation.IdempotencyKey.Should().Be(request.IdempotencyKey);
        compensation.CapturedOutput.Should().BeEmpty();
    }

    [Fact]
    public async Task CalleeConfirmedFailureAfterProvisionalDispatch_ShouldDropLedgerAndNotCompensate()
    {
        var harness = await CreateStartedRunAsync(InFlightCompensationWorkflowYaml());
        var request = StepRequests(harness.Publisher, "create_order").Single();
        harness.Agent.State.CompensableLedger.Should().ContainSingle()
            .Which.LedgerStatus.Should().Be(CompensableLedgerEntryStatus.Provisional);
        harness.Publisher.Published.Clear();

        await harness.Agent.HandleEventAsync(SelfEnvelope(harness.RunId, new StepCompletedEvent
        {
            RunId = harness.RunId,
            StepId = "create_order",
            Success = false,
            Error = "callee rejected cleanly",
            ExecutionId = request.ExecutionId,
            FailureOutcome = WorkflowStepFailureOutcome.CalleeConfirmed,
        }));

        harness.Agent.State.CompensableLedger.Should().BeEmpty();
        CompensationRequests(harness.Publisher).Should().BeEmpty();
        CommittedEvents<WorkflowCompensationFailedEvent>(harness.CommittedPublisher).Should().BeEmpty();
    }

    [Fact]
    public async Task SuccessfulCompletionAfterProvisionalDispatch_ShouldConfirmSingleLedgerEntry()
    {
        var harness = await CreateStartedRunAsync(InFlightCompensationWorkflowYaml());
        var request = StepRequests(harness.Publisher, "create_order").Single();
        harness.Agent.State.CompensableLedger.Should().ContainSingle()
            .Which.LedgerStatus.Should().Be(CompensableLedgerEntryStatus.Provisional);
        harness.Publisher.Published.Clear();

        await harness.Agent.HandleEventAsync(SelfEnvelope(harness.RunId, new StepCompletedEvent
        {
            RunId = harness.RunId,
            StepId = "create_order",
            Success = true,
            Output = """{"orderId":"order-1"}""",
            ExecutionId = request.ExecutionId,
        }));

        harness.Agent.State.CompensableLedger.Should().ContainSingle()
            .Which.Should().BeEquivalentTo(new CompletedStepLedgerEntry
            {
                StepId = "create_order",
                CompensationStepId = "cancel_order",
                IdempotencyKey = request.IdempotencyKey,
                CapturedOutput = """{"orderId":"order-1"}""",
                LedgerStatus = CompensableLedgerEntryStatus.Confirmed,
            });
    }

    [Fact]
    public async Task LegacySuccessfulCompletionWithoutDispatchEvent_ShouldAppendConfirmedLedgerEntry()
    {
        var harness = await CreateStartedRunAsync(LegacyCompensationWorkflowYaml());

        await CompleteStepAsync(harness, "create_order", "order-output");

        harness.Agent.State.CompensableLedger.Should().ContainSingle()
            .Which.Should().BeEquivalentTo(new CompletedStepLedgerEntry
            {
                StepId = "create_order",
                CompensationStepId = "cancel_order",
                IdempotencyKey = $"{harness.RunId}:create_order:1",
                CapturedOutput = "order-output",
                LedgerStatus = CompensableLedgerEntryStatus.Confirmed,
            });
    }

    [Fact]
    public async Task TerminalFailure_WithCompensableLedger_ShouldDispatchCompensationsInReverseOrder()
    {
        var harness = await CreateStartedRunAsync(SagaWorkflowYaml());

        await CompleteStepAsync(harness, "create_order", "order-output");
        await CompleteStepAsync(harness, "charge_payment", "charge-output");
        harness.Agent.State.CompensableLedger.Should().HaveCount(2);

        await FailStepAsync(harness, "ship_order", "ship failed");

        var firstRequest = CompensationRequests(harness.Publisher).Should().ContainSingle().Subject;
        firstRequest.CompensationStepId.Should().Be("refund_payment");
        firstRequest.CapturedOutput.Should().Be("charge-output");
        firstRequest.IdempotencyKey.Should().Be($"{harness.RunId}:charge_payment:1");
        CommittedEvents<WorkflowCompletedEvent>(harness.CommittedPublisher)
            .Where(x => !x.Success)
            .Should()
            .BeEmpty();

        await CompleteCompensationAsync(harness, firstRequest);
        var secondRequest = CompensationRequests(harness.Publisher).Last();
        secondRequest.CompensationStepId.Should().Be("cancel_order");
        secondRequest.CapturedOutput.Should().Be("order-output");
        secondRequest.IdempotencyKey.Should().Be($"{harness.RunId}:create_order:1");

        await CompleteCompensationAsync(harness, secondRequest);

        CommittedEvents<CompensationStepCompletedEvent>(harness.CommittedPublisher)
            .Select(x => x.CompensationStepId)
            .Should()
            .Equal("refund_payment", "cancel_order");
        CommittedEvents<WorkflowCompensationCompletedEvent>(harness.CommittedPublisher)
            .Should()
            .ContainSingle()
            .Which.CompensatedSteps.Should().Be(2);
        CommittedEvents<WorkflowCompletedEvent>(harness.CommittedPublisher)
            .Where(x => !x.Success)
            .Should()
            .ContainSingle()
            .Which.RunId.Should().Be(harness.RunId);
        harness.Agent.State.SagaStatus.Should().Be(WorkflowSagaStatus.CompensatedFailed);
    }

    [Fact]
    public async Task TerminalFailure_WithMultipleCompensations_ShouldPreserveOriginalFailedStepId()
    {
        var harness = await CreateStartedRunAsync(SagaWorkflowYaml());

        await CompleteStepAsync(harness, "create_order", "order-output");
        await CompleteStepAsync(harness, "charge_payment", "charge-output");
        await FailStepAsync(harness, "ship_order", "ship failed");

        var firstRequest = CompensationRequests(harness.Publisher).Should().ContainSingle().Subject;
        firstRequest.FailedStepId.Should().Be("ship_order");
        harness.Agent.State.CompensationOriginFailedStepId.Should().Be("ship_order");

        await CompleteCompensationAsync(harness, firstRequest);

        var secondRequest = CompensationRequests(harness.Publisher).Last();
        secondRequest.CompensationStepId.Should().Be("cancel_order");
        secondRequest.FailedStepId.Should().Be("ship_order");
        harness.Agent.State.CompensationOriginFailedStepId.Should().Be("ship_order");
    }

    [Fact]
    public async Task WorkflowCompletionRedelivery_WhenRunIsTerminal_ShouldNotDuplicateTerminalFacts()
    {
        var harness = await CreateStartedRunAsync(NoCompensationWorkflowYaml());

        await FailStepAsync(harness, "failed_step", "boom");
        var terminal = CommittedEvents<WorkflowCompletedEvent>(harness.CommittedPublisher)
            .Should()
            .ContainSingle(x => !x.Success)
            .Subject;
        var committedEventCount = harness.CommittedPublisher.Events.Count;
        var publishedEventCount = harness.Publisher.Published.Count;

        await harness.Agent.HandleEventAsync(SelfEnvelope(harness.RunId, terminal.Clone()));

        CommittedEvents<WorkflowCompletedEvent>(harness.CommittedPublisher)
            .Where(x => !x.Success)
            .Should()
            .ContainSingle();
        harness.CommittedPublisher.Events.Should().HaveCount(committedEventCount);
        harness.Publisher.Published.Should().HaveCount(publishedEventCount);
    }

    [Fact]
    public async Task Reactivation_MidCompensation_ShouldReplayCurrentRequestWithoutDuplicateCommit()
    {
        var harness = await CreateStartedRunAsync(SagaWorkflowYaml());
        await CompleteStepAsync(harness, "create_order", "order-output");
        await CompleteStepAsync(harness, "charge_payment", "charge-output");
        await FailStepAsync(harness, "ship_order", "ship failed");

        var originalRequests = CommittedEvents<CompensationRequestEvent>(harness.CommittedPublisher).ToList();
        originalRequests.Should().ContainSingle();

        var reactivated = await CreateRunAsync(
            harness.RunId,
            SagaWorkflowYaml(),
            harness.EventStore,
            autoReplaySelfPublished: false);

        reactivated.Agent.State.SagaStatus.Should().Be(WorkflowSagaStatus.Compensating);
        reactivated.Agent.State.CompensationCursor.Should().Be(1);
        reactivated.Agent.State.CompensationExecutionId.Should().Be(originalRequests[0].ExecutionId);
        var replayed = CompensationRequests(reactivated.Publisher).Should().ContainSingle().Subject;
        replayed.CompensationStepId.Should().Be(originalRequests[0].CompensationStepId);
        replayed.IdempotencyKey.Should().Be(originalRequests[0].IdempotencyKey);
        replayed.CapturedOutput.Should().Be(originalRequests[0].CapturedOutput);
        replayed.ExecutionId.Should().Be(originalRequests[0].ExecutionId);
        CommittedEvents<CompensationRequestEvent>(reactivated.CommittedPublisher).Should().BeEmpty();
    }

    [Fact]
    public async Task Reactivation_AfterPartialCompensation_ShouldReplayRemainingCurrentRequest()
    {
        var harness = await CreateStartedRunAsync(SagaWorkflowYaml());
        await CompleteStepAsync(harness, "create_order", "order-output");
        await CompleteStepAsync(harness, "charge_payment", "charge-output");
        await FailStepAsync(harness, "ship_order", "ship failed");

        var firstRequest = CompensationRequests(harness.Publisher).Single();
        await CompleteCompensationAsync(harness, firstRequest);
        var secondRequest = CompensationRequests(harness.Publisher).Last();

        var reactivated = await CreateRunAsync(
            harness.RunId,
            SagaWorkflowYaml(),
            harness.EventStore,
            autoReplaySelfPublished: false);

        reactivated.Agent.State.SagaStatus.Should().Be(WorkflowSagaStatus.Compensating);
        reactivated.Agent.State.CompensationCursor.Should().Be(0);
        var replayed = CompensationRequests(reactivated.Publisher).Should().ContainSingle().Subject;
        replayed.CompensationStepId.Should().Be("cancel_order");
        replayed.IdempotencyKey.Should().Be(secondRequest.IdempotencyKey);
        replayed.CapturedOutput.Should().Be("order-output");
        replayed.ExecutionId.Should().Be(secondRequest.ExecutionId);
        CommittedEvents<CompensationRequestEvent>(reactivated.CommittedPublisher).Should().BeEmpty();
    }

    [Fact]
    public async Task Reactivation_WithRepeatedCompensationStepId_ShouldReplayExactCursor()
    {
        var harness = await CreateStartedRunAsync(RepeatedCompensationTargetWorkflowYaml());
        await CompleteStepAsync(harness, "reserve_stock", "stock-output");
        await CompleteStepAsync(harness, "reserve_credit", "credit-output");
        await FailStepAsync(harness, "finalize", "finalize failed");

        var firstRequest = CompensationRequests(harness.Publisher).Single();
        firstRequest.CompensationStepId.Should().Be("release");
        firstRequest.CapturedOutput.Should().Be("credit-output");
        firstRequest.IdempotencyKey.Should().Be($"{harness.RunId}:reserve_credit:1");

        var reactivated = await CreateRunAsync(
            harness.RunId,
            RepeatedCompensationTargetWorkflowYaml(),
            harness.EventStore,
            autoReplaySelfPublished: false);

        reactivated.Agent.State.CompensationCursor.Should().Be(1);
        var replayed = CompensationRequests(reactivated.Publisher).Should().ContainSingle().Subject;
        replayed.CompensationStepId.Should().Be("release");
        replayed.CapturedOutput.Should().Be("credit-output");
        replayed.IdempotencyKey.Should().Be($"{harness.RunId}:reserve_credit:1");
        replayed.ExecutionId.Should().Be(firstRequest.ExecutionId);
    }

    [Fact]
    public async Task StaleOrDuplicateCompensationCompletion_ShouldBeRejectedAndNotAdvanceCursor()
    {
        var harness = await CreateStartedRunAsync(SagaWorkflowYaml());
        await CompleteStepAsync(harness, "create_order", "order-output");
        await CompleteStepAsync(harness, "charge_payment", "charge-output");
        await FailStepAsync(harness, "ship_order", "ship failed");

        var request = CompensationRequests(harness.Publisher).Single();
        await harness.Agent.HandleEventAsync(SelfEnvelope(harness.RunId, new CompensationStepCompletedEvent
        {
            RunId = harness.RunId,
            CompensationStepId = request.CompensationStepId,
            Success = true,
            ExecutionId = "stale-execution",
        }));

        CommittedEvents<CompensationStepCompletedEvent>(harness.CommittedPublisher).Should().BeEmpty();
        CommittedEvents<StaleStepCompletionRejectedEvent>(harness.CommittedPublisher)
            .Should()
            .ContainSingle()
            .Which.ReceivedExecutionId.Should().Be("stale-execution");
        CommittedEvents<WorkflowCompensationFailedEvent>(harness.CommittedPublisher).Should().BeEmpty();
        CommittedEvents<WorkflowCompensationCompletedEvent>(harness.CommittedPublisher).Should().BeEmpty();
        harness.Agent.State.CompensationCursor.Should().Be(1);

        await CompleteCompensationAsync(harness, request);
        await harness.Agent.HandleEventAsync(SelfEnvelope(harness.RunId, new CompensationStepCompletedEvent
        {
            RunId = harness.RunId,
            CompensationStepId = request.CompensationStepId,
            Success = true,
            ExecutionId = request.ExecutionId,
        }));

        CommittedEvents<CompensationStepCompletedEvent>(harness.CommittedPublisher)
            .Should()
            .ContainSingle(x => x.CompensationStepId == "refund_payment");
        CommittedEvents<StaleStepCompletionRejectedEvent>(harness.CommittedPublisher)
            .Should()
            .HaveCount(2);
    }

    [Fact]
    public async Task NoCompensationWorkflow_TerminalFailure_ShouldKeepOriginalCompletionShape()
    {
        var harness = await CreateStartedRunAsync(NoCompensationWorkflowYaml());

        await FailStepAsync(harness, "failed_step", "boom");

        CommittedEvents<CompensationRequestEvent>(harness.CommittedPublisher).Should().BeEmpty();
        var completed = CommittedEvents<WorkflowCompletedEvent>(harness.CommittedPublisher)
            .Where(x => !x.Success)
            .Should()
            .ContainSingle()
            .Subject;
        completed.Should().BeEquivalentTo(new WorkflowCompletedEvent
        {
            WorkflowName = "wf_2097",
            RunId = harness.RunId,
            Success = false,
            Error = "boom",
        });
        completed.ToByteArray().Should().Equal(new WorkflowCompletedEvent
        {
            WorkflowName = "wf_2097",
            RunId = harness.RunId,
            Success = false,
            Error = "boom",
        }.ToByteArray());
    }

    [Fact]
    public async Task CompensationDispatch_ShouldUseSelfContinuation()
    {
        var harness = await CreateStartedRunAsync(SagaWorkflowYaml());
        await CompleteStepAsync(harness, "create_order", "order-output");
        await CompleteStepAsync(harness, "charge_payment", "charge-output");

        await FailStepAsync(harness, "ship_order", "ship failed");

        harness.Publisher.Published
            .Where(x => x.Event is CompensationRequestEvent)
            .Should()
            .ContainSingle()
            .Which.Audience.Should().Be(TopologyAudience.Self);
        harness.Publisher.Published
            .Where(x => x.Event is StepRequestEvent { StepId: "refund_payment" })
            .Should()
            .ContainSingle()
            .Which.Audience.Should().Be(TopologyAudience.Self);
    }

    [Fact]
    public async Task FailedCompensation_ShouldPersistDeadLetterTerminalAndNotifyCaller()
    {
        var harness = await CreateStartedRunAsync(SagaWorkflowYaml());
        await CompleteStepAsync(harness, "create_order", "order-output");
        await CompleteStepAsync(harness, "charge_payment", "charge-output");
        await FailStepAsync(harness, "ship_order", "ship failed");

        var request = CompensationRequests(harness.Publisher).Single();
        await FailCompensationAsync(harness, request, "refund failed");

        CommittedEvents<CompensationStepCompletedEvent>(harness.CommittedPublisher)
            .Should()
            .ContainSingle()
            .Which.Should().BeEquivalentTo(new CompensationStepCompletedEvent
            {
                RunId = harness.RunId,
                CompensationStepId = "refund_payment",
                Success = false,
                Error = "refund failed",
                ExecutionId = request.ExecutionId,
            });
        CommittedEvents<WorkflowCompensationCompletedEvent>(harness.CommittedPublisher).Should().BeEmpty();
        var failed = CommittedEvents<WorkflowCompensationFailedEvent>(harness.CommittedPublisher)
            .Should()
            .ContainSingle()
            .Subject;
        failed.Should().BeEquivalentTo(new WorkflowCompensationFailedEvent
        {
            RunId = harness.RunId,
            FailedCompensationStepId = "refund_payment",
            RemainingUncompensated = 2,
            Error = "refund failed",
        });
        var completed = CommittedEvents<WorkflowCompletedEvent>(harness.CommittedPublisher)
            .Where(x => !x.Success)
            .Should()
            .ContainSingle()
            .Subject;
        completed.RunId.Should().Be(harness.RunId);
        completed.Error.Should().Be("refund failed");
        AssertDeadLetterCompletionPublished(harness, "refund failed");
        CommittedEvents<WorkflowCompensationCompletedEvent>(harness.CommittedPublisher).Should().BeEmpty();
        harness.Agent.State.Status.Should().Be("failed");
        harness.Agent.State.SagaStatus.Should().Be(WorkflowSagaStatus.CompensationDeadLetter);
        harness.Agent.State.DeadLetterFailedCompensationStepId.Should().Be("refund_payment");
        harness.Agent.State.DeadLetterRemainingUncompensated.Should().Be(2);
        harness.Agent.State.DeadLetterError.Should().Be("refund failed");
        var reactivated = await CreateRunAsync(
            harness.RunId,
            SagaWorkflowYaml(),
            harness.EventStore,
            autoReplaySelfPublished: false);
        CompensationRequests(reactivated.Publisher).Should().BeEmpty();
        reactivated.Agent.State.SagaStatus.Should().Be(WorkflowSagaStatus.CompensationDeadLetter);
    }

    [Fact]
    public async Task CompensationStepWithoutTimeout_ShouldUseDefaultTimeoutAndDeadLetter()
    {
        var harness = await CreateStartedRunAsync(NoTimeoutCompensationWorkflowYaml());
        await CompleteStepAsync(harness, "create_order", "order-output");
        await FailStepAsync(harness, "ship_order", "ship failed");

        var request = CompensationRequests(harness.Publisher).Should().ContainSingle().Subject;
        request.CompensationStepId.Should().Be("cancel_order");
        var timeout = SingleScheduledStepTimeout(harness, "cancel_order", 30_000);

        await harness.Agent.HandleEventAsync(SelfEnvelope(
            harness.RunId,
            new WorkflowStepTimeoutFiredEvent
            {
                RunId = harness.RunId,
                StepId = "cancel_order",
                TimeoutMs = 30_000,
            },
            MetadataFor(timeout)));

        var failed = CommittedEvents<WorkflowCompensationFailedEvent>(harness.CommittedPublisher)
            .Should()
            .ContainSingle()
            .Subject;
        failed.FailedCompensationStepId.Should().Be("cancel_order");
        failed.RemainingUncompensated.Should().Be(1);
        failed.Error.Should().Be("TIMEOUT after 30000ms");
        harness.Agent.State.SagaStatus.Should().Be(WorkflowSagaStatus.CompensationDeadLetter);
        AssertDeadLetterCompletionPublished(harness, "TIMEOUT after 30000ms");
    }

    [Fact]
    public async Task CompensationStepWithExplicitTimeout_ShouldUseConfiguredTimeoutAndDeadLetter()
    {
        var harness = await CreateStartedRunAsync(ExplicitTimeoutCompensationWorkflowYaml());
        await CompleteStepAsync(harness, "create_order", "order-output");
        await FailStepAsync(harness, "ship_order", "ship failed");

        var request = CompensationRequests(harness.Publisher).Should().ContainSingle().Subject;
        request.CompensationStepId.Should().Be("cancel_order");
        var timeout = SingleScheduledStepTimeout(harness, "cancel_order", 750);

        await harness.Agent.HandleEventAsync(SelfEnvelope(
            harness.RunId,
            new WorkflowStepTimeoutFiredEvent
            {
                RunId = harness.RunId,
                StepId = "cancel_order",
                TimeoutMs = 750,
            },
            MetadataFor(timeout)));

        var failed = CommittedEvents<WorkflowCompensationFailedEvent>(harness.CommittedPublisher)
            .Should()
            .ContainSingle()
            .Subject;
        failed.FailedCompensationStepId.Should().Be("cancel_order");
        failed.RemainingUncompensated.Should().Be(1);
        failed.Error.Should().Be("TIMEOUT after 750ms");
        harness.Agent.State.SagaStatus.Should().Be(WorkflowSagaStatus.CompensationDeadLetter);
        AssertDeadLetterCompletionPublished(harness, "TIMEOUT after 750ms");
    }

    [Fact]
    public async Task CompensationStepWithTooSmallTimeout_ShouldClampScheduledTimeout()
    {
        var harness = await CreateStartedRunAsync(TinyTimeoutCompensationWorkflowYaml());
        await CompleteStepAsync(harness, "create_order", "order-output");
        await FailStepAsync(harness, "ship_order", "ship failed");

        var request = CompensationRequests(harness.Publisher).Should().ContainSingle().Subject;
        request.CompensationStepId.Should().Be("cancel_order");

        SingleScheduledStepTimeout(harness, "cancel_order", 100);
        harness.CallbackScheduler.ScheduledCallbacks
            .Where(x => x.Event is WorkflowStepTimeoutFiredEvent timeout &&
                        timeout.StepId == "cancel_order")
            .Should()
            .ContainSingle();
    }

    [Fact]
    public async Task CompensationPhaseDeadline_ShouldForceDeadLetterAndNotifyCaller()
    {
        var harness = await CreateStartedRunAsync(SagaWorkflowYaml());
        await CompleteStepAsync(harness, "create_order", "order-output");
        await CompleteStepAsync(harness, "charge_payment", "charge-output");
        await FailStepAsync(harness, "ship_order", "ship failed");

        var deadline = SingleCompensationPhaseDeadline(harness);
        await harness.Agent.HandleEventAsync(SelfEnvelope(
            harness.RunId,
            new WorkflowCompensationPhaseDeadlineFiredEvent
            {
                RunId = harness.RunId,
            },
            MetadataFor(deadline)));

        var failed = CommittedEvents<WorkflowCompensationFailedEvent>(harness.CommittedPublisher)
            .Should()
            .ContainSingle()
            .Subject;
        failed.FailedCompensationStepId.Should().Be("refund_payment");
        failed.RemainingUncompensated.Should().Be(2);
        failed.Error.Should().Be("compensation phase deadline exceeded after 300000ms");
        harness.Agent.State.SagaStatus.Should().Be(WorkflowSagaStatus.CompensationDeadLetter);
        AssertDeadLetterCompletionPublished(harness, "compensation phase deadline exceeded after 300000ms");
    }

    [Fact]
    public async Task CompensationPhaseDeadlineWithStaleLeaseMetadata_ShouldBeIgnored()
    {
        var harness = await CreateStartedRunAsync(SagaWorkflowYaml());
        await CompleteStepAsync(harness, "create_order", "order-output");
        await CompleteStepAsync(harness, "charge_payment", "charge-output");
        await FailStepAsync(harness, "ship_order", "ship failed");

        var deadline = SingleCompensationPhaseDeadline(harness);
        await harness.Agent.HandleEventAsync(SelfEnvelope(
            harness.RunId,
            new WorkflowCompensationPhaseDeadlineFiredEvent
            {
                RunId = harness.RunId,
            },
            new EnvelopeCallbackContext
            {
                CallbackId = deadline.CallbackId,
                Generation = deadline.Generation + 1,
                SlotEpoch = deadline.SlotEpoch,
                FiredAtUnixTimeMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            }));

        CommittedEvents<WorkflowCompensationFailedEvent>(harness.CommittedPublisher).Should().BeEmpty();
        CommittedEvents<WorkflowCompletedEvent>(harness.CommittedPublisher)
            .Where(x => !x.Success)
            .Should()
            .BeEmpty();
        harness.Agent.State.SagaStatus.Should().Be(WorkflowSagaStatus.Compensating);
        CompensationRequests(harness.Publisher)
            .Should()
            .ContainSingle(x => x.CompensationStepId == "refund_payment");
    }

    [Fact]
    public async Task CompensationPhaseDeadline_ShouldBeScheduledOnceAcrossMultipleRequests()
    {
        var harness = await CreateStartedRunAsync(SagaWorkflowYaml());
        await CompleteStepAsync(harness, "create_order", "order-output");
        await CompleteStepAsync(harness, "charge_payment", "charge-output");
        await FailStepAsync(harness, "ship_order", "ship failed");

        var deadline = SingleCompensationPhaseDeadline(harness);
        var firstRequest = CompensationRequests(harness.Publisher).Single();

        await CompleteCompensationAsync(harness, firstRequest);

        harness.CallbackScheduler.ScheduledCallbacks
            .Where(x => x.Event is WorkflowCompensationPhaseDeadlineFiredEvent)
            .Should()
            .ContainSingle()
            .Which.CallbackId.Should().Be(deadline.CallbackId);
        CompensationRequests(harness.Publisher)
            .Last()
            .CompensationStepId.Should().Be("cancel_order");
    }

    [Fact]
    public async Task CompensationDeadlineAfterTimedOutCompensation_ShouldStayTerminalAndCanceled()
    {
        var harness = await CreateStartedRunAsync(NoTimeoutCompensationWorkflowYaml());
        await CompleteStepAsync(harness, "create_order", "order-output");
        await FailStepAsync(harness, "ship_order", "ship failed");

        var deadline = SingleCompensationPhaseDeadline(harness);
        var timeout = SingleScheduledStepTimeout(harness, "cancel_order", 30_000);
        await harness.Agent.HandleEventAsync(SelfEnvelope(
            harness.RunId,
            new WorkflowStepTimeoutFiredEvent
            {
                RunId = harness.RunId,
                StepId = "cancel_order",
                TimeoutMs = 30_000,
            },
            MetadataFor(timeout)));

        var terminalCount = CommittedEvents<WorkflowCompletedEvent>(harness.CommittedPublisher)
            .Count(x => !x.Success);
        terminalCount.Should().Be(1);
        harness.CallbackScheduler.CanceledCallbacks
            .Should()
            .ContainSingle(x =>
                x.CallbackId == deadline.CallbackId &&
                x.Generation == deadline.Generation);

        await harness.Agent.HandleEventAsync(SelfEnvelope(
            harness.RunId,
            new WorkflowCompensationPhaseDeadlineFiredEvent
            {
                RunId = harness.RunId,
            },
            MetadataFor(deadline)));

        CommittedEvents<WorkflowCompletedEvent>(harness.CommittedPublisher)
            .Count(x => !x.Success)
            .Should()
            .Be(terminalCount);
        CommittedEvents<WorkflowCompensationFailedEvent>(harness.CommittedPublisher)
            .Should()
            .ContainSingle()
            .Which.Error.Should().Be("TIMEOUT after 30000ms");
    }

    [Fact]
    public async Task CompensationPhaseDeadlineHostGuard_ShouldRejectNonCompensatingOrMismatchedRun()
    {
        var activeHarness = await CreateStartedRunAsync(SagaWorkflowYaml());
        var activeHost = (IWorkflowExecutionStateHost)activeHarness.Agent;

        var activeResult = await activeHost.RecordCompensationPhaseDeadlineExceededAsync(
            activeHarness.RunId,
            "deadline",
            CancellationToken.None);

        activeResult.Status.Should().Be(WorkflowCompensationTransitionStatus.NoCompensableLedger);
        CommittedEvents<WorkflowCompensationFailedEvent>(activeHarness.CommittedPublisher).Should().BeEmpty();

        var compensatingHarness = await CreateStartedRunAsync(SagaWorkflowYaml());
        await CompleteStepAsync(compensatingHarness, "create_order", "order-output");
        await CompleteStepAsync(compensatingHarness, "charge_payment", "charge-output");
        await FailStepAsync(compensatingHarness, "ship_order", "ship failed");
        var compensatingHost = (IWorkflowExecutionStateHost)compensatingHarness.Agent;

        var mismatchedRunResult = await compensatingHost.RecordCompensationPhaseDeadlineExceededAsync(
            "run-other",
            "deadline",
            CancellationToken.None);

        mismatchedRunResult.Status.Should().Be(WorkflowCompensationTransitionStatus.NoCompensableLedger);
        CommittedEvents<WorkflowCompensationFailedEvent>(compensatingHarness.CommittedPublisher).Should().BeEmpty();
        compensatingHarness.Agent.State.SagaStatus.Should().Be(WorkflowSagaStatus.Compensating);
    }

    [Fact]
    public async Task CompensationPhaseDeadlineHostGuard_WhenCursorHasNoLedgerEntry_ShouldDeadLetterWithoutFailedStepId()
    {
        var harness = await CreateStartedRunAsync(NoTimeoutCompensationWorkflowYaml());
        await CompleteStepAsync(harness, "create_order", "order-output");
        await FailStepAsync(harness, "ship_order", "ship failed");
        harness.Agent.State.CompensationCursor = 9;
        var host = (IWorkflowExecutionStateHost)harness.Agent;

        var result = await host.RecordCompensationPhaseDeadlineExceededAsync(
            harness.RunId,
            "deadline",
            CancellationToken.None);

        result.Status.Should().Be(WorkflowCompensationTransitionStatus.CompensationDeadLettered);
        var failed = CommittedEvents<WorkflowCompensationFailedEvent>(harness.CommittedPublisher)
            .Should()
            .ContainSingle()
            .Subject;
        failed.FailedCompensationStepId.Should().BeEmpty();
        failed.RemainingUncompensated.Should().Be(1);
        failed.Error.Should().Be("deadline");
    }

    [Fact]
    public async Task CompensationPhaseDeadlineAfterTerminal_ShouldBeIgnored()
    {
        var harness = await CreateStartedRunAsync(SagaWorkflowYaml());
        await CompleteStepAsync(harness, "create_order", "order-output");
        await CompleteStepAsync(harness, "charge_payment", "charge-output");
        await FailStepAsync(harness, "ship_order", "ship failed");

        var deadline = SingleCompensationPhaseDeadline(harness);
        var firstRequest = CompensationRequests(harness.Publisher).Single();
        await CompleteCompensationAsync(harness, firstRequest);
        var secondRequest = CompensationRequests(harness.Publisher).Last();
        await CompleteCompensationAsync(harness, secondRequest);

        var terminalCount = CommittedEvents<WorkflowCompletedEvent>(harness.CommittedPublisher)
            .Count(x => !x.Success);
        terminalCount.Should().Be(1);

        await harness.Agent.HandleEventAsync(SelfEnvelope(
            harness.RunId,
            new WorkflowCompensationPhaseDeadlineFiredEvent
            {
                RunId = harness.RunId,
            },
            MetadataFor(deadline)));

        CommittedEvents<WorkflowCompletedEvent>(harness.CommittedPublisher)
            .Count(x => !x.Success)
            .Should()
            .Be(terminalCount);
        CommittedEvents<WorkflowCompensationFailedEvent>(harness.CommittedPublisher).Should().BeEmpty();
    }

    [Fact]
    public async Task SuccessfulCompensation_ShouldCancelPhaseDeadlineLease()
    {
        var harness = await CreateStartedRunAsync(SagaWorkflowYaml());
        await CompleteStepAsync(harness, "create_order", "order-output");
        await CompleteStepAsync(harness, "charge_payment", "charge-output");
        await FailStepAsync(harness, "ship_order", "ship failed");

        var deadline = SingleCompensationPhaseDeadline(harness);
        var firstRequest = CompensationRequests(harness.Publisher).Single();
        await CompleteCompensationAsync(harness, firstRequest);
        var secondRequest = CompensationRequests(harness.Publisher).Last();
        await CompleteCompensationAsync(harness, secondRequest);

        harness.CallbackScheduler.CanceledCallbacks
            .Should()
            .ContainSingle(x =>
                x.CallbackId == deadline.CallbackId &&
                x.Generation == deadline.Generation);
        CommittedEvents<WorkflowCompensationCompletedEvent>(harness.CommittedPublisher)
            .Should()
            .ContainSingle()
            .Which.CompensatedSteps.Should().Be(2);
    }

    [Fact]
    public async Task FailedCompensation_WithOnErrorFallback_ShouldDeadLetterWithoutFallback()
    {
        var harness = await CreateStartedRunAsync(CompensationOnErrorWorkflowYaml());
        await CompleteStepAsync(harness, "create_order", "order-output");
        await FailStepAsync(harness, "ship_order", "ship failed");

        var request = CompensationRequests(harness.Publisher).Single();
        await FailCompensationAsync(harness, request, "cancel failed");

        CommittedEvents<CompensationStepCompletedEvent>(harness.CommittedPublisher)
            .Should()
            .ContainSingle()
            .Which.Success.Should().BeFalse();
        harness.Publisher.Published
            .Where(x => x.Event is StepRequestEvent { StepId: "fallback_cancel" })
            .Should()
            .BeEmpty();
        CommittedEvents<WorkflowCompletedEvent>(harness.CommittedPublisher)
            .Where(x => !x.Success)
            .Should()
            .ContainSingle()
            .Which.Error.Should().Be("cancel failed");
        AssertDeadLetterCompletionPublished(harness, "cancel failed");
        var failed = CommittedEvents<WorkflowCompensationFailedEvent>(harness.CommittedPublisher)
            .Should()
            .ContainSingle()
            .Subject;
        failed.FailedCompensationStepId.Should().Be("cancel_order");
        failed.RemainingUncompensated.Should().Be(1);
        failed.Error.Should().Be("cancel failed");
        harness.Agent.State.SagaStatus.Should().Be(WorkflowSagaStatus.CompensationDeadLetter);
    }

    [Fact]
    public async Task ManagedWorkflowCallChild_WhenCompensationCompletes_ShouldSendCompensatedChildCompletionToParent()
    {
        const string parentActorId = "parent-run-actor";
        const string parentRunId = "parent-run";
        const string parentStepId = "call_child";
        const string invocationId = "invoke-child-1";
        var childRunId = "child-run-" + Guid.NewGuid().ToString("N");
        var harness = await CreateRunAsync(childRunId, SagaWorkflowYaml());

        await harness.Agent.HandleEventAsync(EnvelopeFrom(parentActorId, new StartWorkflowEvent
        {
            WorkflowName = "wf_2097",
            RunId = childRunId,
            Input = "hello",
            WorkflowRuntime = new WorkflowToolRuntimeContextPayload
            {
                ParentActorId = parentActorId,
                ParentRunId = parentRunId,
                ParentStepId = parentStepId,
                RootRunId = parentRunId,
                Depth = 1,
            },
            Parameters =
            {
                ["workflow_call.invocation_id"] = invocationId,
            },
        }));

        await CompleteStepAsync(harness, "create_order", "order-output");
        await CompleteStepAsync(harness, "charge_payment", "charge-output");
        await FailStepAsync(harness, "ship_order", "ship failed");
        var firstRequest = CompensationRequests(harness.Publisher).Single();
        await CompleteCompensationAsync(harness, firstRequest);
        var secondRequest = CompensationRequests(harness.Publisher).Last();
        await CompleteCompensationAsync(harness, secondRequest);

        PublishedEvents<WorkflowCompletedEvent>(harness.Publisher)
            .Where(x => x.Audience == TopologyAudience.Parent)
            .Should()
            .BeEmpty();
        var sent = harness.Publisher.Sent.Should().ContainSingle(x => x.TargetActorId == parentActorId).Subject;
        var completed = sent.Event.Should().BeOfType<SubWorkflowInvocationCompletedEvent>().Subject;
        completed.InvocationId.Should().Be(invocationId);
        completed.ChildRunId.Should().Be(childRunId);
        completed.Success.Should().BeFalse();
        completed.Compensated.Should().BeTrue();
    }

    [Fact]
    public async Task RetryCompensation_FromMatchingDeadLetter_ShouldPersistRetryAndRepublishCurrentCompensation()
    {
        var harness = await CreateStartedRunAsync(SagaWorkflowYaml());
        await CompleteStepAsync(harness, "create_order", "order-output");
        await CompleteStepAsync(harness, "charge_payment", "charge-output");
        await FailStepAsync(harness, "ship_order", "ship failed");
        var failedRequest = CompensationRequests(harness.Publisher).Single();
        await FailCompensationAsync(harness, failedRequest, "refund failed");
        harness.Agent.State.SagaStatus.Should().Be(WorkflowSagaStatus.CompensationDeadLetter);
        harness.Publisher.Published.Clear();
        harness.CommittedPublisher.Events.Clear();

        await harness.Agent.HandleEventAsync(SelfEnvelope(harness.RunId, new WorkflowCompensationRetryRequestedEvent
        {
            RunId = harness.RunId,
            FailedCompensationStepId = "refund_payment",
            Reason = "operator retry",
            CommandId = "cmd-retry",
            CorrelationId = "corr-retry",
        }));

        harness.Agent.State.SagaStatus.Should().Be(WorkflowSagaStatus.Compensating);
        harness.Agent.State.DeadLetterFailedCompensationStepId.Should().BeEmpty();
        harness.Agent.State.DeadLetterRemainingUncompensated.Should().Be(0);
        harness.Agent.State.DeadLetterError.Should().BeEmpty();
        var retry = CommittedEvents<WorkflowCompensationRetryRequestedEvent>(harness.CommittedPublisher)
            .Should()
            .ContainSingle()
            .Subject;
        retry.RunId.Should().Be(harness.RunId);
        retry.FailedCompensationStepId.Should().Be("refund_payment");
        retry.Reason.Should().Be("operator retry");
        retry.CommandId.Should().Be("cmd-retry");
        retry.CorrelationId.Should().Be("corr-retry");
        var republished = PublishedEvents<CompensationRequestEvent>(harness.Publisher)
            .Should()
            .ContainSingle()
            .Subject;
        republished.Audience.Should().Be(TopologyAudience.Self);
        republished.Event.CompensationStepId.Should().Be("refund_payment");
        republished.Event.CapturedOutput.Should().Be("charge-output");
        republished.Event.IdempotencyKey.Should().Be($"{harness.RunId}:charge_payment:1");
        republished.Event.ExecutionId.Should().NotBe(failedRequest.ExecutionId);
        CommittedEvents<CompensationRequestEvent>(harness.CommittedPublisher)
            .Should()
            .ContainSingle()
            .Which.ExecutionId.Should().Be(republished.Event.ExecutionId);
    }

    [Fact]
    public async Task RetryCompensation_OutsideDeadLetter_ShouldRejectWithoutPublishing()
    {
        var harness = await CreateStartedRunAsync(SagaWorkflowYaml());
        await CompleteStepAsync(harness, "create_order", "order-output");
        await CompleteStepAsync(harness, "charge_payment", "charge-output");
        await FailStepAsync(harness, "ship_order", "ship failed");
        harness.Agent.State.SagaStatus.Should().Be(WorkflowSagaStatus.Compensating);
        harness.Publisher.Published.Clear();
        harness.CommittedPublisher.Events.Clear();

        await harness.Agent.HandleEventAsync(SelfEnvelope(harness.RunId, new WorkflowCompensationRetryRequestedEvent
        {
            RunId = harness.RunId,
            FailedCompensationStepId = "refund_payment",
            Reason = "too early",
        }));

        harness.Agent.State.SagaStatus.Should().Be(WorkflowSagaStatus.Compensating);
        CommittedEvents<WorkflowCompensationRetryRequestedEvent>(harness.CommittedPublisher).Should().BeEmpty();
        CommittedEvents<CompensationRequestEvent>(harness.CommittedPublisher).Should().BeEmpty();
        PublishedEvents<CompensationRequestEvent>(harness.Publisher).Should().BeEmpty();
    }

    [Fact]
    public async Task RetryCompensation_WithMismatchedFailedStep_ShouldRejectWithoutPublishing()
    {
        var harness = await CreateStartedRunAsync(SagaWorkflowYaml());
        await CompleteStepAsync(harness, "create_order", "order-output");
        await CompleteStepAsync(harness, "charge_payment", "charge-output");
        await FailStepAsync(harness, "ship_order", "ship failed");
        var failedRequest = CompensationRequests(harness.Publisher).Single();
        await FailCompensationAsync(harness, failedRequest, "refund failed");
        harness.Agent.State.SagaStatus.Should().Be(WorkflowSagaStatus.CompensationDeadLetter);
        harness.Publisher.Published.Clear();
        harness.CommittedPublisher.Events.Clear();

        await harness.Agent.HandleEventAsync(SelfEnvelope(harness.RunId, new WorkflowCompensationRetryRequestedEvent
        {
            RunId = harness.RunId,
            FailedCompensationStepId = "cancel_order",
            Reason = "wrong step",
        }));

        harness.Agent.State.SagaStatus.Should().Be(WorkflowSagaStatus.CompensationDeadLetter);
        harness.Agent.State.DeadLetterFailedCompensationStepId.Should().Be("refund_payment");
        CommittedEvents<WorkflowCompensationRetryRequestedEvent>(harness.CommittedPublisher).Should().BeEmpty();
        CommittedEvents<CompensationRequestEvent>(harness.CommittedPublisher).Should().BeEmpty();
        PublishedEvents<CompensationRequestEvent>(harness.Publisher).Should().BeEmpty();
    }

    private static async Task CompleteStepAsync(RunHarness harness, string stepId, string output)
    {
        var request = harness.Publisher.Published
            .Where(x => x.Event is StepRequestEvent requestEvent && requestEvent.StepId == stepId)
            .Select(x => (StepRequestEvent)x.Event)
            .Last();
        harness.Publisher.Published.Clear();

        await harness.Agent.HandleEventAsync(SelfEnvelope(harness.RunId, new StepCompletedEvent
        {
            RunId = harness.RunId,
            StepId = stepId,
            Success = true,
            Output = output,
            ExecutionId = request.ExecutionId,
        }));
    }

    private static async Task FailStepAsync(RunHarness harness, string stepId, string error)
    {
        var request = harness.Publisher.Published
            .Where(x => x.Event is StepRequestEvent requestEvent && requestEvent.StepId == stepId)
            .Select(x => (StepRequestEvent)x.Event)
            .Last();

        await harness.Agent.HandleEventAsync(SelfEnvelope(harness.RunId, new StepCompletedEvent
        {
            RunId = harness.RunId,
            StepId = stepId,
            Success = false,
            Error = error,
            ExecutionId = request.ExecutionId,
        }));
    }

    private static async Task CompleteCompensationAsync(RunHarness harness, CompensationRequestEvent request)
    {
        var stepRequest = harness.Publisher.Published
            .Where(x => x.Event is StepRequestEvent stepRequestEvent && stepRequestEvent.StepId == request.CompensationStepId)
            .Select(x => (StepRequestEvent)x.Event)
            .Last();
        harness.Publisher.Published.Clear();

        await harness.Agent.HandleEventAsync(SelfEnvelope(harness.RunId, new StepCompletedEvent
        {
            RunId = harness.RunId,
            StepId = request.CompensationStepId,
            Success = true,
            Output = $"done:{request.CompensationStepId}",
            ExecutionId = stepRequest.ExecutionId,
        }));
    }

    private static async Task FailCompensationAsync(RunHarness harness, CompensationRequestEvent request, string error)
    {
        var stepRequest = harness.Publisher.Published
            .Where(x => x.Event is StepRequestEvent stepRequestEvent && stepRequestEvent.StepId == request.CompensationStepId)
            .Select(x => (StepRequestEvent)x.Event)
            .Last();

        await harness.Agent.HandleEventAsync(SelfEnvelope(harness.RunId, new StepCompletedEvent
        {
            RunId = harness.RunId,
            StepId = request.CompensationStepId,
            Success = false,
            Error = error,
            ExecutionId = stepRequest.ExecutionId,
        }));
    }

    private static void AssertDeadLetterCompletionPublished(RunHarness harness, string error)
    {
        var parentCompletion = PublishedEvents<WorkflowCompletedEvent>(harness.Publisher)
            .Where(x => x.Audience == TopologyAudience.Parent && !x.Event.Success)
            .Should()
            .ContainSingle()
            .Subject
            .Event;
        parentCompletion.RunId.Should().Be(harness.RunId);
        parentCompletion.Error.Should().Be(error);

        var llmCompletion = PublishedEvents<WorkflowLlmInvocationCompletedEvent>(harness.Publisher)
            .Where(x => x.Audience == TopologyAudience.Parent)
            .Should()
            .ContainSingle()
            .Subject
            .Event;
        llmCompletion.RunId.Should().Be(harness.RunId);
        llmCompletion.Success.Should().BeFalse();
        llmCompletion.Error.Should().Be(error);
    }

    private static async Task<RunHarness> CreateStartedRunAsync(string workflowYaml)
    {
        var runId = "run-2097-" + Guid.NewGuid().ToString("N");
        var harness = await CreateRunAsync(runId, workflowYaml);

        await harness.Agent.HandleEventAsync(EnvelopeFrom("api", new WorkflowChatRequestEvent
        {
            Prompt = "hello",
            ScopeId = "scope-1",
        }));

        return harness;
    }

    private static async Task<RunHarness> CreateRunAsync(
        string runId,
        string workflowYaml,
        RecordingEventStore? eventStore = null,
        bool autoReplaySelfPublished = true)
    {
        eventStore ??= new RecordingEventStore();
        var committedHook = new RecordingCommittedStatePublicationHook();
        var callbackScheduler = new RecordingRuntimeCallbackScheduler();
        var topologyPublisher = new RecordingEventPublisher(runId)
        {
            AutoReplaySelfPublished = autoReplaySelfPublished,
        };
        var agent = new WorkflowRunGAgent(
            new UnsupportedActorRuntime(),
            new UnsupportedActorRuntime(),
            new EmptyEventModuleFactory(),
            [new EmptyWorkflowModulePack()])
        {
            EventSourcingBehaviorFactory = new DefaultEventSourcingBehaviorFactory<WorkflowRunState>(eventStore),
            EventPublisher = topologyPublisher,
            Services = new TestServiceProvider(callbackScheduler, committedHook),
            Logger = NullLogger.Instance,
        };
        SetAgentId(agent, runId);
        topologyPublisher.Agent = agent;
        await agent.ActivateAsync();

        if (string.IsNullOrWhiteSpace(agent.State.WorkflowYaml))
        {
            await agent.HandleEventAsync(EnvelopeFrom("workflow-run-actor-port", new BindWorkflowRunDefinitionEvent
            {
                DefinitionActorId = "definition-2097",
                WorkflowName = "wf_2097",
                WorkflowYaml = workflowYaml,
                RunId = runId,
                ScopeId = "scope-1",
                ExpectedExecutionMode = ExternalCapabilityExecutionMode.Interactive,
            }));
        }

        return new RunHarness(agent, runId, eventStore, committedHook, topologyPublisher, callbackScheduler);
    }

    private static IReadOnlyList<CompensationRequestEvent> CompensationRequests(RecordingEventPublisher publisher) =>
        publisher.Published
            .Where(x => x.Event is CompensationRequestEvent)
            .Select(x => (CompensationRequestEvent)x.Event)
            .ToArray();

    private static IReadOnlyList<StepRequestEvent> StepRequests(RecordingEventPublisher publisher, string stepId) =>
        publisher.Published
            .Where(x => x.Event is StepRequestEvent requestEvent && requestEvent.StepId == stepId)
            .Select(x => (StepRequestEvent)x.Event)
            .ToArray();

    private static IReadOnlyList<ScheduledCallback> ScheduledTimeouts(RecordingEventPublisher publisher) =>
        publisher.CallbackScheduler.ScheduledCallbacks.ToArray();

    private static ScheduledCallback SingleScheduledStepTimeout(
        RunHarness harness,
        string stepId,
        int timeoutMs) =>
        harness.CallbackScheduler.ScheduledCallbacks
            .Where(x => x.Event is WorkflowStepTimeoutFiredEvent timeout &&
                        timeout.StepId == stepId &&
                        timeout.TimeoutMs == timeoutMs)
            .Should()
            .ContainSingle()
            .Subject;

    private static ScheduledCallback SingleCompensationPhaseDeadline(RunHarness harness) =>
        harness.CallbackScheduler.ScheduledCallbacks
            .Where(x => x.Event is WorkflowCompensationPhaseDeadlineFiredEvent)
            .Should()
            .ContainSingle()
            .Subject;

    private static IReadOnlyList<(TEvent Event, TopologyAudience Audience)> PublishedEvents<TEvent>(
        RecordingEventPublisher publisher)
        where TEvent : class, IMessage<TEvent>, new() =>
        publisher.Published
            .Where(x => x.Event is TEvent)
            .Select(x => ((TEvent)x.Event, x.Audience))
            .ToArray();

    private static IReadOnlyList<TEvent> CommittedEvents<TEvent>(RecordingCommittedStatePublicationHook committedPublisher)
        where TEvent : class, IMessage<TEvent>, new() =>
        committedPublisher.Events
            .Where(x => x.StateEvent?.EventData?.Is(new TEvent().Descriptor) == true)
            .Select(x => x.StateEvent.EventData.Unpack<TEvent>())
            .ToArray();

    private static EventEnvelope SelfEnvelope(
        string runId,
        IMessage payload,
        EnvelopeCallbackContext? callback = null) =>
        EnvelopeFrom(runId, payload, callback);

    private static EventEnvelope EnvelopeFrom(
        string publisherActorId,
        IMessage payload,
        EnvelopeCallbackContext? callback = null) =>
        new()
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
            Payload = Any.Pack(payload),
            Route = EnvelopeRouteSemantics.CreateTopologyPublication(publisherActorId, TopologyAudience.Self),
            Runtime = callback == null
                ? null
                : new EnvelopeRuntime
                {
                    Callback = callback.Clone(),
                },
            Propagation = new EnvelopePropagation
            {
                CorrelationId = Guid.NewGuid().ToString("N"),
            },
        };

    private static EnvelopeCallbackContext MetadataFor(ScheduledCallback callback) =>
        new()
        {
            CallbackId = callback.CallbackId,
            Generation = callback.Generation,
            SlotEpoch = callback.SlotEpoch,
            FiredAtUnixTimeMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        };

    private static string SagaWorkflowYaml() =>
        """
        name: wf_2097
        roles: []
        steps:
          - id: create_order
            type: transform
            next: charge_payment
            compensation: cancel_order
          - id: charge_payment
            type: transform
            next: ship_order
            compensation: refund_payment
          - id: ship_order
            type: transform
          - id: refund_payment
            type: transform
          - id: cancel_order
            type: transform
        """;

    private static string InFlightCompensationWorkflowYaml() =>
        """
        name: wf_2097
        roles: []
        steps:
          - id: create_order
            type: connector_call
            timeout_ms: 500
            compensation: cancel_order
          - id: cancel_order
            type: connector_call
        """;

    private static string LegacyCompensationWorkflowYaml() =>
        """
        name: wf_2097
        roles: []
        steps:
          - id: create_order
            type: transform
            compensation: cancel_order
          - id: cancel_order
            type: transform
        """;

    private static string NoTimeoutCompensationWorkflowYaml() =>
        """
        name: wf_2097
        roles: []
        steps:
          - id: create_order
            type: transform
            next: ship_order
            compensation: cancel_order
          - id: ship_order
            type: transform
          - id: cancel_order
            type: connector_call
        """;

    private static string ExplicitTimeoutCompensationWorkflowYaml() =>
        """
        name: wf_2097
        roles: []
        steps:
          - id: create_order
            type: transform
            next: ship_order
            compensation: cancel_order
          - id: ship_order
            type: transform
          - id: cancel_order
            type: connector_call
            timeout_ms: 750
        """;

    private static string TinyTimeoutCompensationWorkflowYaml() =>
        """
        name: wf_2097
        roles: []
        steps:
          - id: create_order
            type: transform
            next: ship_order
            compensation: cancel_order
          - id: ship_order
            type: transform
          - id: cancel_order
            type: connector_call
            timeout_ms: 1
        """;

    private static string NoCompensationWorkflowYaml() =>
        """
        name: wf_2097
        roles: []
        steps:
          - id: failed_step
            type: transform
        """;

    private static string RepeatedCompensationTargetWorkflowYaml() =>
        """
        name: wf_2097
        roles: []
        steps:
          - id: reserve_stock
            type: transform
            next: reserve_credit
            compensation: release
          - id: reserve_credit
            type: transform
            next: finalize
            compensation: release
          - id: finalize
            type: transform
          - id: release
            type: transform
        """;

    private static string CompensationOnErrorWorkflowYaml() =>
        """
        name: wf_2097
        roles: []
        steps:
          - id: create_order
            type: transform
            next: ship_order
            compensation: cancel_order
          - id: ship_order
            type: transform
          - id: cancel_order
            type: transform
            on_error:
              strategy: fallback
              fallback_step: fallback_cancel
          - id: fallback_cancel
            type: transform
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
        RecordingEventStore EventStore,
        RecordingCommittedStatePublicationHook CommittedPublisher,
        RecordingEventPublisher Publisher,
        RecordingRuntimeCallbackScheduler CallbackScheduler);

    private sealed class EmptyWorkflowModulePack : IWorkflowModulePack
    {
        public string Name => "test.empty";

        public IReadOnlyList<WorkflowModuleRegistration> Modules { get; } = [];

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

        public List<(string TargetActorId, IMessage Event)> Sent { get; } = [];

        public bool AutoReplaySelfPublished { get; init; } = true;

        public WorkflowRunGAgent Agent { get; set; } = null!;

        public RecordingRuntimeCallbackScheduler CallbackScheduler =>
            (RecordingRuntimeCallbackScheduler)((TestServiceProvider)Agent.Services).Scheduler;

        public async Task PublishAsync<T>(
            T evt,
            TopologyAudience audience,
            CancellationToken ct,
            EventEnvelope? sourceEnvelope,
            EventEnvelopePublishOptions? options)
            where T : IMessage
        {
            ct.ThrowIfCancellationRequested();
            Published.Add((evt.Descriptor.Parser.ParseFrom(evt.ToByteArray()), audience));

            if (AutoReplaySelfPublished && audience == TopologyAudience.Self)
                await Agent.HandleEventAsync(SelfEnvelope(runId, evt), ct);
        }

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

    private sealed record ScheduledCallback(
        string CallbackId,
        long Generation,
        int SlotEpoch,
        IMessage Event);

    private sealed record CanceledCallback(
        string ActorId,
        string CallbackId,
        long Generation);

    private sealed class RecordingCommittedStatePublicationHook : ICommittedStatePublicationHook
    {
        public List<CommittedStateEventPublished> Events { get; } = [];

        public Task BeforePublishAsync(CommittedStatePublicationContext context, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Events.Add(context.Published.Clone());
            return Task.CompletedTask;
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
        IActorRuntimeCallbackScheduler scheduler,
        RecordingCommittedStatePublicationHook committedHook) : IServiceProvider
    {
        public IActorRuntimeCallbackScheduler Scheduler { get; } = scheduler;

        public object? GetService(System.Type serviceType)
        {
            if (serviceType == typeof(IEnumerable<IGAgentExecutionHook>))
                return Array.Empty<IGAgentExecutionHook>();
            if (serviceType == typeof(IActorRuntimeCallbackScheduler))
                return Scheduler;
            if (serviceType == typeof(IEnumerable<ICommittedStatePublicationHook>))
                return new ICommittedStatePublicationHook[] { committedHook };

            return null;
        }
    }

    private sealed class RecordingRuntimeCallbackScheduler : IActorRuntimeCallbackScheduler
    {
        public List<ScheduledCallback> ScheduledCallbacks { get; } = [];
        public List<CanceledCallback> CanceledCallbacks { get; } = [];

        public Task<RuntimeCallbackLease> ScheduleTimeoutAsync(
            RuntimeCallbackTimeoutRequest request,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            var generation = ScheduledCallbacks.Count(x => x.CallbackId == request.CallbackId) + 1;
            var slotEpoch = request.TriggerEnvelope.Runtime?.Callback?.SlotEpoch ?? 0;
            var payload = request.TriggerEnvelope.Payload
                          ?? throw new InvalidOperationException("Expected scheduled timeout payload.");
            IMessage evt;
            if (payload.Is(WorkflowStepTimeoutFiredEvent.Descriptor))
                evt = payload.Unpack<WorkflowStepTimeoutFiredEvent>();
            else if (payload.Is(WorkflowStepRetryBackoffFiredEvent.Descriptor))
                evt = payload.Unpack<WorkflowStepRetryBackoffFiredEvent>();
            else if (payload.Is(WorkflowCompensationPhaseDeadlineFiredEvent.Descriptor))
                evt = payload.Unpack<WorkflowCompensationPhaseDeadlineFiredEvent>();
            else
                throw new InvalidOperationException($"Unexpected scheduled timeout payload: {payload.TypeUrl}");

            ScheduledCallbacks.Add(new ScheduledCallback(request.CallbackId, generation, slotEpoch, evt));
            var lease = new RuntimeCallbackLease(
                request.ActorId,
                request.CallbackId,
                generation,
                RuntimeCallbackBackend.InMemory)
            {
                SlotEpoch = slotEpoch,
            };
            return Task.FromResult(lease);
        }

        public Task<RuntimeCallbackLease> ScheduleTimerAsync(
            RuntimeCallbackTimerRequest request,
            CancellationToken ct = default) =>
            throw new NotSupportedException("Saga compensation tests do not use durable timers.");

        public Task CancelAsync(RuntimeCallbackLease lease, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            CanceledCallbacks.Add(new CanceledCallback(
                lease.ActorId,
                lease.CallbackId,
                lease.Generation));
            return Task.CompletedTask;
        }

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
