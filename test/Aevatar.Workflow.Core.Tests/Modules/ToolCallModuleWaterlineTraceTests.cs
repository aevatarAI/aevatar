using Aevatar.Foundation.Abstractions;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Core.Execution;
using Aevatar.Workflow.Core.Modules;
using FluentAssertions;
using Microsoft.Extensions.Time.Testing;
using System.Threading.Channels;
using static Aevatar.Workflow.Core.Tests.Modules.ToolCallModuleContextTests;
using LogEntry = Aevatar.Workflow.Core.Tests.Modules.ToolCallModuleContextTests.RecordingToolCallLogger.LogEntry;

namespace Aevatar.Workflow.Core.Tests.Modules;

/// <summary>
/// Deterministic correlated-waterline traces for the tool-call attempt lifecycle
/// (issue #3478). Every trace asserts waterline order, correlation ids, dispositions,
/// payload-free log properties, and that the pre-existing terminal outcome is unchanged.
/// </summary>
public sealed class ToolCallModuleWaterlineTraceTests
{
    private const string ArgumentsSentinel = "arguments-sentinel-7f3a";
    private const string ResultSentinel = "result-sentinel-9c1d";
    private static readonly DateTimeOffset TraceEpoch = new(2026, 8, 18, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ToolCallModule_WhenToolThrows_ShouldTraceThrewDeliveryAndProviderFailedReconciliation()
    {
        var logger = new RecordingToolCallLogger();
        var tool = new BlockingWorkflowTool("throwing_tool");
        var module = CreateModule(tool, logger: logger);
        var ctx = new RecordingWorkflowContext { Clock = new FakeTimeProvider(TraceEpoch) };
        var request = TracedRequest(ctx, tool.Name, "step-throw", "exec-throw");

        await module.HandleAsync(Envelope(request), ctx, CancellationToken.None);
        var pending = SinglePending(ctx);
        var invocation = await tool.ReadInvocationAsync();
        invocation.Completion.SetException(new InvalidOperationException("provider exploded"));
        var completed = await WaitForDeliveredCompletionAsync(ctx, logger);
        await module.HandleAsync(ctx.PublishedEnvelope(completed), ctx, CancellationToken.None);

        var observations = Waterlines(logger);
        Timeline(observations).Should().Equal(
            new Trace("pending_state_persisted", "none", 1),
            new Trace("external_dispatch_started", "none", 1),
            new Trace("provider_returned", "threw", 1),
            new Trace("completion_delivery_producer_confirmed", "confirmed", 1),
            new Trace("actor_reconciliation_completed", "provider_failed", 1));
        AssertCorrelated(observations, ctx, pending);
        AssertSingleDispatchId(observations, completed.ProviderTiming.DispatchId);
        AssertRedacted(observations);

        completed.ProviderTiming.Disposition.Should().Be(WorkflowToolCallProviderDisposition.Threw);
        ctx.Published.Select(static item => item.Event)
            .OfType<StepCompletedEvent>()
            .Should().ContainSingle()
            .Which.Success.Should().BeFalse();
        ctx.LoadState<ToolCallModuleState>("tool_call").PendingExecutions.Should().BeEmpty();
    }

    [Fact]
    public async Task ToolCallModule_WhenExecutionIsCancelledBeforeTimeout_ShouldTraceCancelledWithoutDeliveryThenTimeoutUnknown()
    {
        var logger = new RecordingToolCallLogger();
        var tool = new CancellableBlockingWorkflowTool("cancellable_tool");
        var module = CreateModule(tool, logger: logger);
        var ctx = new RecordingWorkflowContext { Clock = new FakeTimeProvider(TraceEpoch) };
        var request = TracedRequest(ctx, tool.Name, "step-cancel", "exec-cancel");

        await module.HandleAsync(Envelope(request), ctx, CancellationToken.None);
        var pending = SinglePending(ctx);
        var invocation = await tool.ReadInvocationAsync();
        var timeout = ctx.Scheduled.Should()
            .ContainSingle(static callback => callback.Event is WorkflowToolCallTimeoutFiredEvent)
            .Subject;

        ((IWorkflowExecutionBackgroundWorkOwner)module).CancelBackgroundWork();
        invocation.CancellationToken.IsCancellationRequested.Should().BeTrue();
        await logger.WaitForEntryAsync(entry =>
            Equals(entry.Properties.GetValueOrDefault("Waterline"), "provider_returned") &&
            Equals(entry.Properties.GetValueOrDefault("ProviderDisposition"), "cancelled"));

        await module.HandleAsync(CallbackEnvelope(timeout), ctx, CancellationToken.None);

        var observations = Waterlines(logger);
        Timeline(observations).Should().Equal(
            new Trace("pending_state_persisted", "none", 1),
            new Trace("external_dispatch_started", "none", 1),
            new Trace("provider_returned", "cancelled", 1),
            new Trace("timeout_signal_accepted", "timeout_outcome_unknown", 1),
            new Trace("actor_reconciliation_completed", "timeout_outcome_unknown", 1));
        observations.Select(static entry => entry.Properties["Waterline"])
            .Should().NotContain("completion_delivery_producer_confirmed");
        AssertCorrelated(observations, ctx, pending);
        DispatchIds(observations).Should().ContainSingle();
        observations
            .Where(static entry => Equals(entry.Properties["ReconciliationDisposition"], "timeout_outcome_unknown"))
            .Should().HaveCount(2)
            .And.OnlyContain(entry => string.IsNullOrEmpty(entry.Properties["DispatchId"] as string));
        AssertRedacted(observations);

        ctx.Published.Select(static item => item.Event)
            .OfType<WorkflowToolCallAttemptCompletedEvent>()
            .Should().BeEmpty();
        ctx.Published.Select(static item => item.Event)
            .OfType<WorkflowToolCallCompletedEvent>()
            .Should().ContainSingle()
            .Which.Error.Should().Contain("tool_outcome_unknown");
        ctx.Published.Select(static item => item.Event)
            .OfType<StepCompletedEvent>()
            .Should().ContainSingle()
            .Which.FailureOutcome.Should().Be(WorkflowStepFailureOutcome.OutcomeUncertain);
        ctx.LoadState<ToolCallModuleState>("tool_call").PendingExecutions.Should().BeEmpty();
    }

    [Fact]
    public async Task ToolCallModule_WhenTrustedCompletionTargetsRetiredAttempt_ShouldTraceStaleWithoutSettling()
    {
        var logger = new RecordingToolCallLogger();
        var tool = new SequencedResultWorkflowTool(
            "retrying_tool",
            WorkflowToolExecutionResult.Failed(
                string.Empty,
                "tool_error",
                "transient provider failure",
                terminalInvoked: false,
                retryable: true));
        var module = CreateModule(tool, logger: logger);
        var ctx = new RecordingWorkflowContext { Clock = new FakeTimeProvider(TraceEpoch) };
        var request = TracedRequest(ctx, tool.Name, "step-stale", "exec-stale");

        await module.HandleAsync(Envelope(request), ctx, CancellationToken.None);
        var pending = SinglePending(ctx);
        var completed = await WaitForDeliveredCompletionAsync(ctx, logger);
        await module.HandleAsync(ctx.PublishedEnvelope(completed), ctx, CancellationToken.None);
        var retryPending = SinglePending(ctx);
        retryPending.ExecutionPhase.Should().Be(WorkflowToolCallExecutionPhase.RetryPending);
        retryPending.Attempt.Should().Be(2);

        // The same trusted attempt-1 completion arrives again after the pending moved on.
        await module.HandleAsync(ctx.PublishedEnvelope(completed), ctx, CancellationToken.None);

        var observations = Waterlines(logger);
        Timeline(observations).Should().Equal(
            new Trace("pending_state_persisted", "none", 1),
            new Trace("external_dispatch_started", "none", 1),
            new Trace("provider_returned", "failed", 1),
            new Trace("completion_delivery_producer_confirmed", "confirmed", 1),
            new Trace("actor_reconciliation_completed", "retry_scheduled", 1),
            new Trace("actor_reconciliation_completed", "stale", 1));
        AssertCorrelated(observations, ctx, pending);
        AssertSingleDispatchId(observations, completed.ProviderTiming.DispatchId);
        AssertRedacted(observations);

        tool.ExecuteCalls.Should().Be(1);
        var unchanged = SinglePending(ctx);
        unchanged.ExecutionPhase.Should().Be(WorkflowToolCallExecutionPhase.RetryPending);
        unchanged.Attempt.Should().Be(2);
        ctx.Published.Select(static item => item.Event).OfType<StepCompletedEvent>().Should().BeEmpty();
        ctx.Published.Select(static item => item.Event).OfType<WorkflowToolCallCompletedEvent>().Should().BeEmpty();
    }

    [Fact]
    public async Task ToolCallModule_WhenTimeoutContinuationDoesNotMatch_ShouldTraceStaleAndKeepAttemptAlive()
    {
        var logger = new RecordingToolCallLogger();
        var tool = new BlockingWorkflowTool("stale_timeout_tool");
        var module = CreateModule(tool, logger: logger);
        var ctx = new RecordingWorkflowContext { Clock = new FakeTimeProvider(TraceEpoch) };
        var request = TracedRequest(ctx, tool.Name, "step-stale-timeout", "exec-stale-timeout");

        await module.HandleAsync(Envelope(request), ctx, CancellationToken.None);
        var pending = SinglePending(ctx);
        var invocation = await tool.ReadInvocationAsync();
        var timeout = ctx.Scheduled.Should()
            .ContainSingle(static callback => callback.Event is WorkflowToolCallTimeoutFiredEvent)
            .Subject;
        var staleTimeout = ((WorkflowToolCallTimeoutFiredEvent)timeout.Event).Clone();
        staleTimeout.ContinuationId = "continuation-from-a-previous-life";

        await module.HandleAsync(
            CallbackEnvelope(new ScheduledToolCallback(timeout.DueTime, staleTimeout, timeout.Lease)),
            ctx,
            CancellationToken.None);

        SinglePending(ctx).ExecutionPhase.Should().Be(WorkflowToolCallExecutionPhase.ExecutionPending);
        invocation.CancellationToken.IsCancellationRequested.Should().BeFalse();
        ctx.Published.Select(static item => item.Event).OfType<StepCompletedEvent>().Should().BeEmpty();

        invocation.Completion.SetResult(WorkflowToolExecutionResult.Success(SentinelResult()));
        var completed = await WaitForDeliveredCompletionAsync(ctx, logger);
        await module.HandleAsync(ctx.PublishedEnvelope(completed), ctx, CancellationToken.None);

        var observations = Waterlines(logger);
        Timeline(observations).Should().Equal(
            new Trace("pending_state_persisted", "none", 1),
            new Trace("external_dispatch_started", "none", 1),
            new Trace("actor_reconciliation_completed", "stale", 1),
            new Trace("provider_returned", "succeeded", 1),
            new Trace("completion_delivery_producer_confirmed", "confirmed", 1),
            new Trace("actor_reconciliation_completed", "succeeded", 1));
        AssertCorrelated(observations, ctx, pending, requireContinuation: false);
        observations.Where(static entry => !Equals(entry.Properties["ReconciliationDisposition"], "stale"))
            .Should().OnlyContain(entry => Equals(entry.Properties["ContinuationId"], pending.ContinuationId));
        AssertSingleDispatchId(observations, completed.ProviderTiming.DispatchId);
        AssertRedacted(observations);

        ctx.Published.Select(static item => item.Event)
            .OfType<StepCompletedEvent>()
            .Should().ContainSingle()
            .Which.Success.Should().BeTrue();
    }

    [Fact]
    public async Task ToolCallModule_WhenCompletionEnvelopesAreForged_ShouldTraceUntrustedUntilTheGenuineOneSettles()
    {
        var logger = new RecordingToolCallLogger();
        var tool = new BlockingWorkflowTool("forged_completion_tool");
        var module = CreateModule(tool, logger: logger);
        var ctx = new RecordingWorkflowContext { Clock = new FakeTimeProvider(TraceEpoch) };
        var request = TracedRequest(ctx, tool.Name, "step-forged", "exec-forged");

        await module.HandleAsync(Envelope(request), ctx, CancellationToken.None);
        var pending = SinglePending(ctx);
        var invocation = await tool.ReadInvocationAsync();
        invocation.Completion.SetResult(WorkflowToolExecutionResult.Success(SentinelResult()));
        var completed = await WaitForDeliveredCompletionAsync(ctx, logger);

        var forgedPublisher = ctx.PublishedEnvelope(completed);
        forgedPublisher.Route.PublisherActorId = "actor-forged";
        await module.HandleAsync(forgedPublisher, ctx, CancellationToken.None);

        var forgedOperation = ctx.PublishedEnvelope(completed);
        forgedOperation.Runtime.DeliveryIdentity.OperationId = "operation-forged";
        await module.HandleAsync(forgedOperation, ctx, CancellationToken.None);

        SinglePending(ctx).ExecutionPhase.Should().Be(WorkflowToolCallExecutionPhase.ExecutionPending);
        ctx.Published.Select(static item => item.Event).OfType<StepCompletedEvent>().Should().BeEmpty();

        await module.HandleAsync(ctx.PublishedEnvelope(completed), ctx, CancellationToken.None);

        var observations = Waterlines(logger);
        Timeline(observations).Should().Equal(
            new Trace("pending_state_persisted", "none", 1),
            new Trace("external_dispatch_started", "none", 1),
            new Trace("provider_returned", "succeeded", 1),
            new Trace("completion_delivery_producer_confirmed", "confirmed", 1),
            new Trace("actor_reconciliation_completed", "untrusted", 1),
            new Trace("actor_reconciliation_completed", "untrusted", 1),
            new Trace("actor_reconciliation_completed", "succeeded", 1));
        AssertCorrelated(observations, ctx, pending);
        AssertSingleDispatchId(observations, completed.ProviderTiming.DispatchId);
        AssertRedacted(observations);

        ctx.LoadState<ToolCallModuleState>("tool_call").PendingExecutions.Should().BeEmpty();
        ctx.Published.Select(static item => item.Event)
            .OfType<StepCompletedEvent>()
            .Should().ContainSingle()
            .Which.Success.Should().BeTrue();
    }

    [Fact]
    public async Task ToolCallModule_WhenTimeoutLeaseDoesNotMatch_ShouldTraceUntrustedAndKeepAttemptAlive()
    {
        var logger = new RecordingToolCallLogger();
        var tool = new BlockingWorkflowTool("untrusted_timeout_tool");
        var module = CreateModule(tool, logger: logger);
        var ctx = new RecordingWorkflowContext { Clock = new FakeTimeProvider(TraceEpoch) };
        var request = TracedRequest(ctx, tool.Name, "step-untrusted-timeout", "exec-untrusted-timeout");

        await module.HandleAsync(Envelope(request), ctx, CancellationToken.None);
        var pending = SinglePending(ctx);
        pending.TimeoutLease.Should().NotBeNull();
        var invocation = await tool.ReadInvocationAsync();
        var timeout = ctx.Scheduled.Should()
            .ContainSingle(static callback => callback.Event is WorkflowToolCallTimeoutFiredEvent)
            .Subject;
        var forgedLease = CallbackEnvelope(timeout);
        forgedLease.Runtime.Callback.Generation = timeout.Lease.Generation + 41;

        await module.HandleAsync(forgedLease, ctx, CancellationToken.None);

        SinglePending(ctx).ExecutionPhase.Should().Be(WorkflowToolCallExecutionPhase.ExecutionPending);
        invocation.CancellationToken.IsCancellationRequested.Should().BeFalse();
        ctx.Published.Select(static item => item.Event).OfType<StepCompletedEvent>().Should().BeEmpty();

        invocation.Completion.SetResult(WorkflowToolExecutionResult.Success(SentinelResult()));
        var completed = await WaitForDeliveredCompletionAsync(ctx, logger);
        await module.HandleAsync(ctx.PublishedEnvelope(completed), ctx, CancellationToken.None);

        var observations = Waterlines(logger);
        Timeline(observations).Should().Equal(
            new Trace("pending_state_persisted", "none", 1),
            new Trace("external_dispatch_started", "none", 1),
            new Trace("actor_reconciliation_completed", "untrusted", 1),
            new Trace("provider_returned", "succeeded", 1),
            new Trace("completion_delivery_producer_confirmed", "confirmed", 1),
            new Trace("actor_reconciliation_completed", "succeeded", 1));
        AssertCorrelated(observations, ctx, pending);
        AssertSingleDispatchId(observations, completed.ProviderTiming.DispatchId);
        AssertRedacted(observations);

        ctx.Published.Select(static item => item.Event)
            .OfType<StepCompletedEvent>()
            .Should().ContainSingle()
            .Which.Success.Should().BeTrue();
    }

    [Fact]
    public async Task ToolCallModule_WhenRetryableFailureIsRetried_ShouldTraceBothAttemptsWithDistinctDispatchIds()
    {
        var logger = new RecordingToolCallLogger();
        var tool = new SequencedResultWorkflowTool(
            "retry_then_succeed_tool",
            WorkflowToolExecutionResult.Failed(
                string.Empty,
                "tool_error",
                "transient provider failure",
                terminalInvoked: false,
                retryable: true),
            WorkflowToolExecutionResult.Success(SentinelResult()));
        var module = CreateModule(tool, logger: logger);
        var ctx = new RecordingWorkflowContext { Clock = new FakeTimeProvider(TraceEpoch) };
        var request = TracedRequest(ctx, tool.Name, "step-retry", "exec-retry");

        await module.HandleAsync(Envelope(request), ctx, CancellationToken.None);
        var pending = SinglePending(ctx);
        var firstCompleted = await WaitForDeliveredCompletionAsync(ctx, logger, attempt: 1);
        await module.HandleAsync(ctx.PublishedEnvelope(firstCompleted), ctx, CancellationToken.None);
        var retry = ctx.Scheduled.Should()
            .ContainSingle(static callback => callback.Event is WorkflowToolCallRetryFiredEvent)
            .Subject;
        ((WorkflowToolCallRetryFiredEvent)retry.Event).Attempt.Should().Be(2);

        await module.HandleAsync(CallbackEnvelope(retry), ctx, CancellationToken.None);
        var secondCompleted = await WaitForDeliveredCompletionAsync(ctx, logger, attempt: 2);
        await module.HandleAsync(ctx.PublishedEnvelope(secondCompleted), ctx, CancellationToken.None);

        var observations = Waterlines(logger);
        Timeline(observations).Should().Equal(
            new Trace("pending_state_persisted", "none", 1),
            new Trace("external_dispatch_started", "none", 1),
            new Trace("provider_returned", "failed", 1),
            new Trace("completion_delivery_producer_confirmed", "confirmed", 1),
            new Trace("actor_reconciliation_completed", "retry_scheduled", 1),
            new Trace("pending_state_persisted", "none", 2),
            new Trace("external_dispatch_started", "none", 2),
            new Trace("provider_returned", "succeeded", 2),
            new Trace("completion_delivery_producer_confirmed", "confirmed", 2),
            new Trace("actor_reconciliation_completed", "succeeded", 2));
        AssertCorrelated(observations, ctx, pending);
        AssertRedacted(observations);
        firstCompleted.ProviderTiming.DispatchId.Should().NotBeNullOrWhiteSpace();
        secondCompleted.ProviderTiming.DispatchId.Should().NotBeNullOrWhiteSpace()
            .And.NotBe(firstCompleted.ProviderTiming.DispatchId);
        DispatchIds(observations.Where(static entry => Equals(entry.Properties["Attempt"], 1)))
            .Should().Equal(firstCompleted.ProviderTiming.DispatchId);
        DispatchIds(observations.Where(static entry => Equals(entry.Properties["Attempt"], 2)))
            .Should().Equal(secondCompleted.ProviderTiming.DispatchId);
        observations.Where(static entry => Equals(entry.Properties["Waterline"], "pending_state_persisted"))
            .Should().OnlyContain(entry => Equals(entry.Properties["ElapsedPhase"], "actor_preparation"));

        tool.ExecuteCalls.Should().Be(2);
        ctx.LoadState<ToolCallModuleState>("tool_call").PendingExecutions.Should().BeEmpty();
        ctx.Published.Select(static item => item.Event)
            .OfType<StepCompletedEvent>()
            .Should().ContainSingle()
            .Which.Success.Should().BeTrue();
    }

    [Fact]
    public async Task ToolCallModule_WhenActorRecoversAPendingAttempt_ShouldTraceRedispatchWithNewDispatchIdOnSameAttempt()
    {
        var firstLogger = new RecordingToolCallLogger();
        var recoveredLogger = new RecordingToolCallLogger();
        var tool = new BlockingWorkflowTool("recoverable_tool", WorkflowToolRecoverySafety.ReplayableReadOnly);
        var firstModule = CreateModule(tool, logger: firstLogger);
        var ctx = new RecordingWorkflowContext { Clock = new FakeTimeProvider(TraceEpoch) };
        var request = TracedRequest(ctx, tool.Name, "step-recover", "exec-recover");

        await firstModule.HandleAsync(Envelope(request), ctx, CancellationToken.None);
        var pending = SinglePending(ctx);
        var firstInvocation = await tool.ReadInvocationAsync();
        ((IWorkflowExecutionBackgroundWorkOwner)firstModule).CancelBackgroundWork();

        var recoveredModule = CreateModule(tool, logger: recoveredLogger);
        await recoveredModule.HandleAsync(Envelope(request), ctx, CancellationToken.None);
        var recoveryEnvelope = RecoveryEnvelope(ctx);
        await recoveredModule.HandleAsync(recoveryEnvelope, ctx, CancellationToken.None);
        var recoveredInvocation = await tool.ReadInvocationAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        recoveredInvocation.Completion.SetResult(WorkflowToolExecutionResult.Success(SentinelResult()));
        var completed = await WaitForDeliveredCompletionAsync(ctx, recoveredLogger, pending.Attempt);
        completed.ContinuationId.Should().Be(pending.ContinuationId);
        await recoveredModule.HandleAsync(ctx.PublishedEnvelope(completed), ctx, CancellationToken.None);

        var beforeCrash = Waterlines(firstLogger);
        Timeline(beforeCrash).Should().Equal(
            new Trace("pending_state_persisted", "none", 1),
            new Trace("external_dispatch_started", "none", 1));
        var afterRecovery = Waterlines(recoveredLogger);
        Timeline(afterRecovery).Should().Equal(
            new Trace("external_dispatch_started", "none", 1),
            new Trace("provider_returned", "succeeded", 1),
            new Trace("completion_delivery_producer_confirmed", "confirmed", 1),
            new Trace("actor_reconciliation_completed", "succeeded", 1));
        AssertCorrelated(beforeCrash, ctx, pending);
        AssertCorrelated(afterRecovery, ctx, pending);
        AssertRedacted(beforeCrash);
        AssertRedacted(afterRecovery);
        var originalDispatchId = DispatchIds(beforeCrash).Should().ContainSingle().Subject;
        AssertSingleDispatchId(afterRecovery, completed.ProviderTiming.DispatchId);
        completed.ProviderTiming.DispatchId.Should().NotBe(originalDispatchId);

        tool.ExecuteCalls.Should().Be(2);
        firstInvocation.CancellationToken.IsCancellationRequested.Should().BeTrue();
        ctx.LoadState<ToolCallModuleState>("tool_call").PendingExecutions.Should().BeEmpty();
        ctx.Published.Select(static item => item.Event)
            .OfType<StepCompletedEvent>()
            .Should().ContainSingle()
            .Which.Success.Should().BeTrue();
        firstInvocation.Completion.SetResult(WorkflowToolExecutionResult.Success("{}"));
    }

    [Fact]
    public async Task ToolCallModule_WhenRecoveryFindsTheDeadlineElapsed_ShouldTraceTimeoutOutcomeUnknownOnTheSameIds()
    {
        var clock = new FakeTimeProvider(TraceEpoch);
        var firstLogger = new RecordingToolCallLogger();
        var recoveredLogger = new RecordingToolCallLogger();
        var tool = new BlockingWorkflowTool("late_recovery_tool", WorkflowToolRecoverySafety.ReplayableReadOnly);
        var firstModule = CreateModule(tool, logger: firstLogger);
        var ctx = new RecordingWorkflowContext { Clock = clock };
        var request = TracedRequest(ctx, tool.Name, "step-late-recover", "exec-late-recover");

        await firstModule.HandleAsync(Envelope(request), ctx, CancellationToken.None);
        var pending = SinglePending(ctx);
        var firstInvocation = await tool.ReadInvocationAsync();
        ((IWorkflowExecutionBackgroundWorkOwner)firstModule).CancelBackgroundWork();

        var recoveredModule = CreateModule(tool, logger: recoveredLogger);
        await recoveredModule.HandleAsync(Envelope(request), ctx, CancellationToken.None);
        var recoveryEnvelope = RecoveryEnvelope(ctx);
        clock.Advance(TimeSpan.FromMilliseconds(pending.TimeoutMs + 1));

        await recoveredModule.HandleAsync(recoveryEnvelope, ctx, CancellationToken.None);

        var beforeCrash = Waterlines(firstLogger);
        Timeline(beforeCrash).Should().Equal(
            new Trace("pending_state_persisted", "none", 1),
            new Trace("external_dispatch_started", "none", 1));
        var afterRecovery = Waterlines(recoveredLogger);
        Timeline(afterRecovery).Should().Equal(
            new Trace("actor_reconciliation_completed", "timeout_outcome_unknown", 1));
        AssertCorrelated(beforeCrash, ctx, pending);
        AssertCorrelated(afterRecovery, ctx, pending);
        AssertRedacted(beforeCrash);
        AssertRedacted(afterRecovery);
        DispatchIds(beforeCrash).Should().ContainSingle();
        AssertSingleDispatchId(afterRecovery, expectedDispatchId: null);
        afterRecovery.Single().Properties["ElapsedPhase"].Should().Be("actor_reconciliation");

        tool.ExecuteCalls.Should().Be(1);
        ctx.Published.Select(static item => item.Event)
            .OfType<WorkflowToolCallCompletedEvent>()
            .Should().ContainSingle()
            .Which.Error.Should().Contain("tool_outcome_unknown");
        ctx.Published.Select(static item => item.Event)
            .OfType<StepCompletedEvent>()
            .Should().ContainSingle()
            .Which.FailureOutcome.Should().Be(WorkflowStepFailureOutcome.OutcomeUncertain);
        ctx.LoadState<ToolCallModuleState>("tool_call").PendingExecutions.Should().BeEmpty();
        firstInvocation.Completion.SetResult(WorkflowToolExecutionResult.Success("{}"));
    }

    [Fact]
    public async Task ToolCallModule_WhenRecoveryCannotRedispatchAnEffectfulTool_ShouldTraceRecoveryOutcomeUnknown()
    {
        var firstLogger = new RecordingToolCallLogger();
        var recoveredLogger = new RecordingToolCallLogger();
        var tool = new BlockingWorkflowTool("effectful_tool", WorkflowToolRecoverySafety.EffectfulNonReplayable);
        var firstModule = CreateModule(tool, logger: firstLogger);
        var ctx = new RecordingWorkflowContext { Clock = new FakeTimeProvider(TraceEpoch) };
        var request = TracedRequest(ctx, tool.Name, "step-effectful-recover", "exec-effectful-recover");

        await firstModule.HandleAsync(Envelope(request), ctx, CancellationToken.None);
        var pending = SinglePending(ctx);
        var firstInvocation = await tool.ReadInvocationAsync();
        ((IWorkflowExecutionBackgroundWorkOwner)firstModule).CancelBackgroundWork();

        var recoveredModule = CreateModule(tool, logger: recoveredLogger);
        await recoveredModule.HandleAsync(Envelope(request), ctx, CancellationToken.None);
        await recoveredModule.HandleAsync(RecoveryEnvelope(ctx), ctx, CancellationToken.None);

        var beforeCrash = Waterlines(firstLogger);
        Timeline(beforeCrash).Should().Equal(
            new Trace("pending_state_persisted", "none", 1),
            new Trace("external_dispatch_started", "none", 1));
        var afterRecovery = Waterlines(recoveredLogger);
        Timeline(afterRecovery).Should().Equal(
            new Trace("actor_reconciliation_completed", "recovery_outcome_unknown", 1));
        AssertCorrelated(beforeCrash, ctx, pending);
        AssertCorrelated(afterRecovery, ctx, pending);
        AssertRedacted(beforeCrash);
        AssertRedacted(afterRecovery);
        AssertSingleDispatchId(afterRecovery, expectedDispatchId: null);

        tool.ExecuteCalls.Should().Be(1);
        ctx.Published.Select(static item => item.Event)
            .OfType<WorkflowToolCallCompletedEvent>()
            .Should().ContainSingle()
            .Which.Error.Should().Contain("tool_outcome_unknown");
        ctx.Published.Select(static item => item.Event)
            .OfType<StepCompletedEvent>()
            .Should().ContainSingle()
            .Which.FailureOutcome.Should().Be(WorkflowStepFailureOutcome.OutcomeUncertain);
        ctx.LoadState<ToolCallModuleState>("tool_call").PendingExecutions.Should().BeEmpty();
        firstInvocation.Completion.SetResult(WorkflowToolExecutionResult.Success("{}"));
    }

    [Fact]
    public async Task ToolCallModule_WhenWatchdogCannotBeScheduled_ShouldTracePreDispatchFailureWithoutDispatch()
    {
        var logger = new RecordingToolCallLogger();
        var tool = new BlockingWorkflowTool("undispatched_tool");
        var module = CreateModule(tool, logger: logger);
        var ctx = new RecordingWorkflowContext
        {
            Clock = new FakeTimeProvider(TraceEpoch),
            FailNextSchedule = true,
        };
        var request = TracedRequest(ctx, tool.Name, "step-no-watchdog", "exec-no-watchdog");

        await module.HandleAsync(Envelope(request), ctx, CancellationToken.None);

        var observations = Waterlines(logger);
        Timeline(observations).Should().Equal(
            new Trace("pending_state_persisted", "none", 1),
            new Trace("actor_reconciliation_completed", "pre_dispatch_failed", 1));
        observations.Should().OnlyContain(entry =>
            Equals(entry.Properties["RunId"], request.RunId) &&
            Equals(entry.Properties["StepId"], request.StepId) &&
            Equals(entry.Properties["ExecutionId"], request.ExecutionId));
        observations.Select(static entry => entry.Properties["ContinuationId"]).Distinct().Should().ContainSingle();
        AssertSingleDispatchId(observations, expectedDispatchId: null);
        AssertRedacted(observations);

        tool.ExecuteCalls.Should().Be(0);
        ctx.Published.Select(static item => item.Event)
            .OfType<WorkflowToolCallCompletedEvent>()
            .Should().ContainSingle()
            .Which.Error.Should().Contain("tool_watchdog_unavailable");
        ctx.Published.Select(static item => item.Event)
            .OfType<StepCompletedEvent>()
            .Should().ContainSingle()
            .Which.Success.Should().BeFalse();
        ctx.LoadState<ToolCallModuleState>("tool_call").PendingExecutions.Should().BeEmpty();
    }

    /// <summary>
    /// The producer logs <c>completion_delivery_producer_confirmed</c> right after its self-publish
    /// returns, so a test that hands the completion to the actor immediately could interleave the
    /// reconciliation line ahead of it. Await the delivery waterline before reconciling.
    /// </summary>
    private static async Task<WorkflowToolCallAttemptCompletedEvent> WaitForDeliveredCompletionAsync(
        RecordingWorkflowContext ctx,
        RecordingToolCallLogger logger,
        int attempt = 1)
    {
        var completed = await ctx.WaitForPublishedAsync<WorkflowToolCallAttemptCompletedEvent>(
            candidate => candidate.Attempt == attempt);
        await logger.WaitForEntryAsync(entry =>
            Equals(entry.Properties.GetValueOrDefault("Waterline"), "completion_delivery_producer_confirmed") &&
            Equals(entry.Properties.GetValueOrDefault("Attempt"), attempt) &&
            Equals(entry.Properties.GetValueOrDefault("ExecutionId"), completed.ExecutionId));
        return completed;
    }

    private static StepRequestEvent TracedRequest(
        RecordingWorkflowContext ctx,
        string toolName,
        string stepId,
        string executionId)
    {
        var request = ToolRequest(ctx, toolName, stepId, executionId);
        request.Parameters["arguments"] = $$"""{"query":"{{ArgumentsSentinel}}"}""";
        return request;
    }

    private static string SentinelResult() => $$"""{"payload":"{{ResultSentinel}}"}""";

    private static PendingToolCallExecutionState SinglePending(RecordingWorkflowContext ctx) =>
        ctx.LoadState<ToolCallModuleState>("tool_call")
            .PendingExecutions.Values.Should().ContainSingle().Subject.Clone();

    private static EventEnvelope RecoveryEnvelope(RecordingWorkflowContext ctx)
    {
        var recoveryCallback = ctx.Scheduled
            .Last(static callback => callback.Event is WorkflowToolCallExecutionRecoveryFiredEvent);
        var envelope = CallbackEnvelope(recoveryCallback);
        envelope.Route = EnvelopeRouteSemantics.CreateTopologyPublication(ctx.AgentId, TopologyAudience.Self);
        return envelope;
    }

    private static IReadOnlyList<LogEntry> Waterlines(RecordingToolCallLogger logger) =>
        logger.Entries
            .Where(static entry => entry.Properties.ContainsKey("Waterline"))
            .ToList();

    private static IEnumerable<Trace> Timeline(IEnumerable<LogEntry> observations) =>
        observations.Select(static entry => new Trace(
            (string)entry.Properties["Waterline"]!,
            Disposition(entry),
            (int)entry.Properties["Attempt"]!));

    /// <summary>Mirrors the bounded metric disposition tag: reconciliation, then delivery, then provider.</summary>
    private static string Disposition(LogEntry entry)
    {
        foreach (var key in new[] { "ReconciliationDisposition", "DeliveryAcceptance", "ProviderDisposition" })
        {
            var value = (string)entry.Properties[key]!;
            if (value != "none")
                return value;
        }

        return "none";
    }

    private static IReadOnlyList<string> DispatchIds(IEnumerable<LogEntry> observations) =>
        observations
            .Select(static entry => entry.Properties["DispatchId"]?.ToString() ?? string.Empty)
            .Where(static dispatchId => dispatchId.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToList();

    private static void AssertSingleDispatchId(IEnumerable<LogEntry> observations, string? expectedDispatchId)
    {
        var dispatchIds = DispatchIds(observations);
        if (expectedDispatchId is null)
        {
            dispatchIds.Should().BeEmpty();
            return;
        }

        expectedDispatchId.Should().NotBeNullOrWhiteSpace();
        dispatchIds.Should().Equal(expectedDispatchId);
    }

    private static void AssertCorrelated(
        IEnumerable<LogEntry> observations,
        RecordingWorkflowContext ctx,
        PendingToolCallExecutionState pending,
        bool requireContinuation = true)
    {
        observations.Should().NotBeEmpty();
        observations.Should().OnlyContain(entry =>
            Equals(entry.Properties["ScopeId"], ctx.ScopeId) &&
            Equals(entry.Properties["RunId"], pending.RunId) &&
            Equals(entry.Properties["StepId"], pending.StepId) &&
            Equals(entry.Properties["CallId"], pending.CallId) &&
            Equals(entry.Properties["ExecutionId"], pending.ExecutionId));
        if (requireContinuation)
        {
            observations.Should().OnlyContain(entry =>
                Equals(entry.Properties["ContinuationId"], pending.ContinuationId));
        }
    }

    private static readonly string[] AllowedWaterlineProperties =
    [
        "ScopeId",
        "RunId",
        "StepId",
        "CallId",
        "ExecutionId",
        "ContinuationId",
        "Attempt",
        "DispatchId",
        "Waterline",
        "ObservedAtUtc",
        "TransportAttempt",
        "ProviderDisposition",
        "DeliveryMethod",
        "DeliveryAcceptance",
        "ReconciliationDisposition",
        "ElapsedPhase",
        "ElapsedMs",
    ];

    private static void AssertRedacted(IEnumerable<LogEntry> observations)
    {
        foreach (var entry in observations)
        {
            entry.Properties.Keys.Should().BeSubsetOf(AllowedWaterlineProperties);
            entry.Message.Should()
                .NotContain(ArgumentsSentinel)
                .And.NotContain(ResultSentinel)
                .And.NotContain("arguments")
                .And.NotContain("result_json")
                .And.NotContain("authorization")
                .And.NotContain("secret");
            entry.Properties.Values
                .Select(static value => value?.ToString() ?? string.Empty)
                .Should().NotContain(value =>
                    value.Contains(ArgumentsSentinel, StringComparison.Ordinal) ||
                    value.Contains(ResultSentinel, StringComparison.Ordinal));
        }
    }

    private sealed record Trace(string Waterline, string Disposition, int Attempt);

    /// <summary>
    /// Like <see cref="BlockingWorkflowTool"/> but honours the execution token, so a cancelled
    /// attempt surfaces as <see cref="OperationCanceledException"/> on the provider boundary.
    /// </summary>
    private sealed class CancellableBlockingWorkflowTool(string name) : IWorkflowTool
    {
        private readonly Channel<BlockingToolInvocation> _invocations =
            Channel.CreateUnbounded<BlockingToolInvocation>();

        public string Name { get; } = name;

        public Task<WorkflowToolExecutionResult> ExecuteAsync(
            WorkflowToolExecutionRequest request,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            var completion = new TaskCompletionSource<WorkflowToolExecutionResult>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            ct.Register(() => completion.TrySetCanceled(ct));
            _invocations.Writer.TryWrite(new BlockingToolInvocation(request, completion, ct));
            return completion.Task;
        }

        public ValueTask<BlockingToolInvocation> ReadInvocationAsync() =>
            _invocations.Reader.ReadAsync();
    }

    /// <summary>Returns scripted results in order; one result per dispatched attempt.</summary>
    private sealed class SequencedResultWorkflowTool(
        string name,
        params WorkflowToolExecutionResult[] results) : IWorkflowTool
    {
        private readonly Queue<WorkflowToolExecutionResult> _results = new(results);
        private int _executeCalls;

        public string Name { get; } = name;

        public int ExecuteCalls => Volatile.Read(ref _executeCalls);

        public Task<WorkflowToolExecutionResult> ExecuteAsync(
            WorkflowToolExecutionRequest request,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _executeCalls);
            lock (_results)
            {
                _results.Should().NotBeEmpty();
                return Task.FromResult(_results.Dequeue());
            }
        }
    }
}
