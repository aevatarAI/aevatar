using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.EventSourcing;
using Aevatar.Foundation.Abstractions.EventModules;
using Aevatar.Foundation.Abstractions.Persistence;
using Aevatar.Foundation.Abstractions.Runtime.Callbacks;
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Abstractions.Execution;
using Aevatar.Workflow.Core.Execution;
using Aevatar.Workflow.Core.Modules;
using Aevatar.Workflow.Core.Primitives;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.Workflow.Core.Tests.Execution;

public sealed class WorkflowRuntimeTerminalFailureBoundaryTests
{
    [Fact]
    public void RuntimeFailureMessages_ShouldSanitizeExceptionSummary()
    {
        var ex = new InvalidOperationException(
            "Authorization: Bearer secret-token-123\n" +
            "at Namespace.Type.Method() in /tmp/source.cs:line 42\n" +
            "payload={\"token\":\"abc.def.ghi\",\"value\":\"large\"}");

        var message = WorkflowRuntimeFailureMessages.StepExecutorFailed(
            "step-1",
            "tool_call",
            ex);

        message.Should().StartWith("step_executor_failed: step 'step-1' (tool_call) failed during executor: ");
        message.Should().NotContain("secret-token-123");
        message.Should().NotContain("abc.def.ghi");
        message.Should().NotContain("Bearer");
        message.Should().NotContain("Authorization");
        message.Should().NotContain("payload=");
        message.Should().NotContain(" at ");
        message.Should().NotContain("\n");
        message.Length.Should().BeLessThanOrEqualTo(240);
    }

    [Fact]
    public void RuntimeFailureMessages_ShouldFallbackToExceptionType_WhenSummaryIsBlank()
    {
        var message = WorkflowRuntimeFailureMessages.StartDispatchFailed(new InvalidOperationException("   "));

        message.Should().Be("start_dispatch_failed: failed during start_dispatch: InvalidOperationException");
    }

    [Fact]
    public void RuntimeFailureMessages_ShouldSanitizeCompletionStepFallbackAndTruncateLongTokens()
    {
        var longStepId = new string('s', 90);
        var completion = new StepCompletedEvent
        {
            StepId = "  step with spaces'and quote  ",
        };
        var step = new StepDefinition
        {
            Id = longStepId,
            Type = "custom type'with quote",
        };

        var message = WorkflowRuntimeFailureMessages.StepCompletionHandlingFailed(
            step,
            completion,
            new InvalidOperationException(new string('x', 180)));

        message.Should().StartWith(
            "step_completion_handling_failed: step 'step_with_spacesand_quote' (custom_typewith_quote) failed during completion: ");
        message.Should().NotContain("spaces'and");
        message.Should().NotContain("type'with");
        message.Length.Should().BeLessThanOrEqualTo(240);
    }

    [Fact]
    public void RuntimeFailureMessages_ShouldUseDefinitionStep_WhenCompletionStepIdIsBlank()
    {
        var message = WorkflowRuntimeFailureMessages.StepCompletionHandlingFailed(
            new StepDefinition { Id = "definition-step", Type = "notify" },
            new StepCompletedEvent { StepId = " " },
            new InvalidOperationException("boom"));

        message.Should().Be(
            "step_completion_handling_failed: step 'definition-step' (notify) failed during completion: boom");
    }

    [Fact]
    public void InfrastructureFailurePolicy_ShouldRecognizeWrappedCommitConsistencyFailures()
    {
        var publicationFailure = new CommittedStatePublicationException(
            "run-1",
            new StateEvent
            {
                EventId = "event-7",
                Version = 7,
            },
            CommittedStatePublicationFailureStage.AdapterAcceptance,
            new InvalidOperationException("stream adapter unavailable"));

        WorkflowRuntimeInfrastructureFailurePolicy.IsCommitConsistencyFailure(
                new InvalidOperationException("runtime wrapper", publicationFailure))
            .Should()
            .BeTrue();
        WorkflowRuntimeInfrastructureFailurePolicy.IsCommitConsistencyFailure(
                new AggregateException(new InvalidOperationException("other"), publicationFailure))
            .Should()
            .BeTrue();
        WorkflowRuntimeInfrastructureFailurePolicy.IsCommitConsistencyFailure(
                new EventStoreOptimisticConcurrencyException("run-1", 6, 7))
            .Should()
            .BeTrue();
        WorkflowRuntimeInfrastructureFailurePolicy.IsCommitConsistencyFailure(
                new InvalidOperationException(
                    "runtime wrapper",
                    new EventStoreVersionDriftException("run-1", 6, 7)))
            .Should()
            .BeTrue();
        WorkflowRuntimeInfrastructureFailurePolicy.IsCommitConsistencyFailure(
                new InvalidOperationException("ordinary workflow failure"))
            .Should()
            .BeFalse();
    }

    [Fact]
    public async Task Bridge_ShouldPublishFailedStepCompletion_WhenSelectedExecutorThrows()
    {
        var module = new WorkflowExecutionBridgeModule(
            [new ThrowingStepExecutor(), new RecordingStepExecutor()],
            new RecordingStateHost { RunId = "run-1" });
        var ctx = new RecordingEventHandlerContext();

        await module.HandleAsync(
            Envelope(new StepRequestEvent
            {
                RunId = "run-1",
                StepId = "step-1",
                StepType = "tool_call",
                ExecutionId = "exec-1",
            }),
            ctx,
            CancellationToken.None);

        var failed = ctx.Published.Should().ContainSingle().Subject.Event
            .Unpack<StepCompletedEvent>();
        failed.RunId.Should().Be("run-1");
        failed.StepId.Should().Be("step-1");
        failed.ExecutionId.Should().Be("exec-1");
        failed.Success.Should().BeFalse();
        failed.FailureOutcome.Should().Be(WorkflowStepFailureOutcome.OutcomeUncertain);
        failed.Error.Should().StartWith("step_executor_failed: step 'step-1' (tool_call) failed during executor: ");
        failed.Error.Should().NotContain("super-secret-token");
    }

    [Fact]
    public async Task Bridge_ShouldPropagateRuntimeRetryablePublicationPending_ForInboundRedelivery()
    {
        var expected = new WorkflowRuntimeEnvelopeRetryablePublicationPendingException(
            "pending",
            new InvalidOperationException("transport unavailable"));
        var bridge = new WorkflowExecutionBridgeModule(
            [new DurablePublicationPendingExecutor(expected)],
            new RecordingStateHost { RunId = "run-1" });
        var ctx = new RecordingEventHandlerContext();

        var error = await FluentActions.Awaiting(() => bridge.HandleAsync(
                Envelope(new StepRequestEvent
                {
                    RunId = "run-1",
                    StepId = "foreach-step",
                    StepType = "foreach",
                    ExecutionId = "exec-1",
                }),
                ctx,
                CancellationToken.None))
            .Should()
            .ThrowAsync<WorkflowRuntimeEnvelopeRetryablePublicationPendingException>();

        error.Which.Should().BeSameAs(expected);
        ctx.Published.Should().BeEmpty();
    }

    [Fact]
    public async Task Bridge_ShouldPropagateCommitConsistencyFailure_ForRuntimeRecovery()
    {
        var expected = new EventStoreOptimisticConcurrencyException("run-1", 6, 7);
        var bridge = new WorkflowExecutionBridgeModule(
            [new DurablePublicationPendingExecutor(expected)],
            new RecordingStateHost { RunId = "run-1" });
        var ctx = new RecordingEventHandlerContext();

        var error = await FluentActions.Awaiting(() => bridge.HandleAsync(
                Envelope(new StepRequestEvent
                {
                    RunId = "run-1",
                    StepId = "foreach-step",
                    StepType = "foreach",
                    ExecutionId = "exec-1",
                }),
                ctx,
                CancellationToken.None))
            .Should()
            .ThrowAsync<EventStoreOptimisticConcurrencyException>();

        error.Which.Should().BeSameAs(expected);
        ctx.Published.Should().BeEmpty();
    }

    [Fact]
    public async Task Bridge_ShouldSwallowActorOwnedDurablePublicationPending()
    {
        var bridge = new WorkflowExecutionBridgeModule(
            [new DurablePublicationPendingExecutor(new WorkflowDurablePublicationPendingException(
                "pending",
                new InvalidOperationException("callback already scheduled")))],
            new RecordingStateHost { RunId = "run-1" });
        var ctx = new RecordingEventHandlerContext();

        await bridge.HandleAsync(
            Envelope(new StepRequestEvent
            {
                RunId = "run-1",
                StepId = "tool-step",
                StepType = "tool_call",
                ExecutionId = "exec-1",
            }),
            ctx,
            CancellationToken.None);

        ctx.Published.Should().BeEmpty();
    }

    [Fact]
    public async Task Bridge_ShouldLetActorOwnedRetryDrainPersistedToolCompletionWithoutUncertainOutcome()
    {
        var tool = new RecordingWorkflowTool(
            "counting_tool",
            _ => WorkflowToolExecutionResult.Success("""{"ok":true}"""));
        var executor = new ToolCallModule(
            [new SingleWorkflowToolSource(tool)],
            NullLogger<ToolCallModule>.Instance);
        var host = new RecordingStateHost { RunId = "run-1" };
        var bridge = new WorkflowExecutionBridgeModule([executor], host);
        var ctx = new RecordingEventHandlerContext
        {
            FailNextPublishType = typeof(WorkflowToolCallCompletedEvent),
        };
        var request = new StepRequestEvent
        {
            RunId = "run-1",
            StepId = "tool-step",
            StepType = "tool_call",
            ExecutionId = "exec-1",
            Parameters = { ["tool"] = tool.Name },
        };

        await bridge.HandleAsync(Envelope(request), ctx, CancellationToken.None);

        tool.ExecuteCalls.Should().Be(1);
        ctx.Published.Select(x => x.Event)
            .Where(x => x.Is(StepCompletedEvent.Descriptor))
            .Should().BeEmpty();
        var retry = ctx.Scheduled
            .Select(x => x.Event.Unpack<WorkflowToolCallPublicationRetryFiredEvent>())
            .Should().ContainSingle()
            .Subject;
        retry.PublicationKind.Should().Be(WorkflowToolCallPublicationKind.Completion);
        host.States[ToolCallModule.ModuleStateKey]
            .Unpack<ToolCallModuleState>()
            .Completions.Should().ContainSingle();

        await bridge.HandleAsync(Envelope(retry), ctx, CancellationToken.None);

        tool.ExecuteCalls.Should().Be(1);
        ctx.Published.Select(x => x.Event)
            .Where(x => x.Is(WorkflowToolCallCompletedEvent.Descriptor))
            .Should().ContainSingle();
        ctx.Published.Select(x => x.Event)
            .Where(x => x.Is(StepCompletedEvent.Descriptor))
            .Should().ContainSingle();
        var recovered = host.States[ToolCallModule.ModuleStateKey].Unpack<ToolCallModuleState>();
        recovered.Completions.Should().BeEmpty();
        recovered.CompletionTombstones.Should().ContainSingle();
    }

    [Fact]
    public async Task Bridge_ShouldLetActorOwnedRetryDrainPersistedApprovalSuspensionWithoutReexecution()
    {
        var pending = new WorkflowToolApprovalPendingOutcome(
            "approval-1",
            "danger",
            "workflow:run-1:danger-step:exec-1",
            "{}",
            "AlwaysRequire",
            false,
            true);
        var tool = new RecordingWorkflowTool(
            "danger",
            _ => new WorkflowToolExecutionResult(string.Empty, PendingApproval: pending));
        var executor = new ToolCallModule(
            [new SingleWorkflowToolSource(tool)],
            NullLogger<ToolCallModule>.Instance);
        var host = new RecordingStateHost { RunId = "run-1" };
        var bridge = new WorkflowExecutionBridgeModule([executor], host);
        var ctx = new RecordingEventHandlerContext
        {
            FailNextPublishType = typeof(WorkflowSuspendedEvent),
        };
        var request = new StepRequestEvent
        {
            RunId = "run-1",
            StepId = "danger-step",
            StepType = "tool_call",
            ExecutionId = "exec-1",
            Parameters = { ["tool"] = tool.Name },
        };

        await bridge.HandleAsync(Envelope(request), ctx, CancellationToken.None);

        tool.ExecuteCalls.Should().Be(1);
        ctx.Published.Select(x => x.Event)
            .Where(x => x.Is(StepCompletedEvent.Descriptor))
            .Should().BeEmpty();
        var retry = ctx.Scheduled
            .Select(x => x.Event.Unpack<WorkflowToolCallPublicationRetryFiredEvent>())
            .Should().ContainSingle()
            .Subject;
        retry.PublicationKind.Should().Be(WorkflowToolCallPublicationKind.Suspension);
        host.States[ToolCallModule.ModuleStateKey]
            .Unpack<ToolCallModuleState>()
            .PendingApprovals.Values.Should().ContainSingle()
            .Which.SuspensionPublished.Should().BeFalse();

        await bridge.HandleAsync(Envelope(retry), ctx, CancellationToken.None);

        tool.ExecuteCalls.Should().Be(1);
        ctx.Published.Select(x => x.Event)
            .Where(x => x.Is(WorkflowSuspendedEvent.Descriptor))
            .Should().ContainSingle();
        host.States[ToolCallModule.ModuleStateKey]
            .Unpack<ToolCallModuleState>()
            .PendingApprovals.Values.Should().ContainSingle()
            .Which.SuspensionPublished.Should().BeTrue();
    }

    [Fact]
    public async Task Bridge_ShouldPublishSingleTypedContinuation_WhenCompletionRecoverySchedulingFails()
    {
        var tool = new RecordingWorkflowTool(
            "counting_tool",
            _ => WorkflowToolExecutionResult.Success("""{"ok":true}"""));
        var executor = new ToolCallModule(
            [new SingleWorkflowToolSource(tool)],
            NullLogger<ToolCallModule>.Instance);
        var host = new RecordingStateHost { RunId = "run-1" };
        var bridge = new WorkflowExecutionBridgeModule([executor], host);
        var ctx = new RecordingEventHandlerContext
        {
            FailPublish = evt => evt is WorkflowToolCallCompletedEvent,
            FailSchedule = true,
        };
        var request = new StepRequestEvent
        {
            RunId = "run-1",
            StepId = "tool-step",
            StepType = "tool_call",
            ExecutionId = "exec-1",
            Parameters = { ["tool"] = tool.Name },
        };

        await bridge.HandleAsync(Envelope(request), ctx, CancellationToken.None);

        tool.ExecuteCalls.Should().Be(1);
        ctx.ScheduleAttempts.Should().Be(1);
        var retry = ctx.Published.Select(x => x.Event)
            .Where(x => x.Is(WorkflowToolCallPublicationRetryFiredEvent.Descriptor))
            .Should().ContainSingle()
            .Subject
            .Unpack<WorkflowToolCallPublicationRetryFiredEvent>();
        retry.PublicationKind.Should().Be(WorkflowToolCallPublicationKind.Completion);
        ctx.Published.Select(x => x.Event)
            .Should().NotContain(x => x.Is(StepCompletedEvent.Descriptor));
        host.States[ToolCallModule.ModuleStateKey]
            .Unpack<ToolCallModuleState>()
            .Completions.Should().ContainSingle();

        await bridge.HandleAsync(Envelope(retry), ctx, CancellationToken.None);

        tool.ExecuteCalls.Should().Be(1);
        ctx.ScheduleAttempts.Should().Be(2);
        ctx.Published.Select(x => x.Event)
            .Where(x => x.Is(WorkflowToolCallPublicationRetryFiredEvent.Descriptor))
            .Should().ContainSingle();
        ctx.Published.Select(x => x.Event)
            .Should().NotContain(x => x.Is(StepCompletedEvent.Descriptor));
        host.States[ToolCallModule.ModuleStateKey]
            .Unpack<ToolCallModuleState>()
            .Completions.Should().ContainSingle();
    }

    [Fact]
    public async Task Bridge_ShouldPublishSingleTypedContinuation_WhenSuspensionRecoverySchedulingFails()
    {
        var pending = new WorkflowToolApprovalPendingOutcome(
            "approval-1",
            "danger",
            "workflow:run-1:danger-step:exec-1",
            "{}",
            "AlwaysRequire",
            false,
            true);
        var tool = new RecordingWorkflowTool(
            "danger",
            _ => new WorkflowToolExecutionResult(string.Empty, PendingApproval: pending));
        var executor = new ToolCallModule(
            [new SingleWorkflowToolSource(tool)],
            NullLogger<ToolCallModule>.Instance);
        var host = new RecordingStateHost { RunId = "run-1" };
        var bridge = new WorkflowExecutionBridgeModule([executor], host);
        var ctx = new RecordingEventHandlerContext
        {
            FailPublish = evt => evt is WorkflowSuspendedEvent,
            FailSchedule = true,
        };
        var request = new StepRequestEvent
        {
            RunId = "run-1",
            StepId = "danger-step",
            StepType = "tool_call",
            ExecutionId = "exec-1",
            Parameters = { ["tool"] = tool.Name },
        };

        await bridge.HandleAsync(Envelope(request), ctx, CancellationToken.None);

        tool.ExecuteCalls.Should().Be(1);
        ctx.ScheduleAttempts.Should().Be(1);
        var retry = ctx.Published.Select(x => x.Event)
            .Where(x => x.Is(WorkflowToolCallPublicationRetryFiredEvent.Descriptor))
            .Should().ContainSingle()
            .Subject
            .Unpack<WorkflowToolCallPublicationRetryFiredEvent>();
        retry.PublicationKind.Should().Be(WorkflowToolCallPublicationKind.Suspension);
        ctx.Published.Select(x => x.Event)
            .Should().NotContain(x => x.Is(StepCompletedEvent.Descriptor));
        host.States[ToolCallModule.ModuleStateKey]
            .Unpack<ToolCallModuleState>()
            .PendingApprovals.Values.Should().ContainSingle()
            .Which.SuspensionPublished.Should().BeFalse();

        await bridge.HandleAsync(Envelope(retry), ctx, CancellationToken.None);

        tool.ExecuteCalls.Should().Be(1);
        ctx.ScheduleAttempts.Should().Be(2);
        ctx.Published.Select(x => x.Event)
            .Where(x => x.Is(WorkflowToolCallPublicationRetryFiredEvent.Descriptor))
            .Should().ContainSingle();
        ctx.Published.Select(x => x.Event)
            .Should().NotContain(x => x.Is(StepCompletedEvent.Descriptor));
        host.States[ToolCallModule.ModuleStateKey]
            .Unpack<ToolCallModuleState>()
            .PendingApprovals.Values.Should().ContainSingle()
            .Which.SuspensionPublished.Should().BeFalse();
    }

    [Fact]
    public async Task Bridge_ShouldKeepAdapterSingleExecution_WhenPublishSucceedsBeforeCheckpointFailure()
    {
        var tool = new RecordingWorkflowTool(
            "counting_tool",
            _ => WorkflowToolExecutionResult.Success("""{"ok":true}"""));
        var executor = new ToolCallModule(
            [new SingleWorkflowToolSource(tool)],
            NullLogger<ToolCallModule>.Instance);
        var host = new RecordingStateHost
        {
            RunId = "run-1",
            FailSaveAttempt = 2,
        };
        var bridge = new WorkflowExecutionBridgeModule([executor], host);
        var ctx = new RecordingEventHandlerContext();
        var request = new StepRequestEvent
        {
            RunId = "run-1",
            StepId = "tool-step",
            StepType = "tool_call",
            ExecutionId = "exec-1",
            Parameters = { ["tool"] = tool.Name },
        };

        await bridge.HandleAsync(Envelope(request), ctx, CancellationToken.None);

        tool.ExecuteCalls.Should().Be(1);
        host.States[ToolCallModule.ModuleStateKey]
            .Unpack<ToolCallModuleState>()
            .Completions.Should().ContainSingle()
            .Which.ToolCompletionPublished.Should().BeFalse();
        var retry = ctx.Scheduled.Select(x => x.Event)
            .Select(x => x.Unpack<WorkflowToolCallPublicationRetryFiredEvent>())
            .Should().ContainSingle()
            .Subject;
        ctx.Published.Select(x => x.Event)
            .Where(x => x.Is(WorkflowToolCallCompletedEvent.Descriptor))
            .Should().ContainSingle();
        ctx.Published.Select(x => x.Event)
            .Should().NotContain(x => x.Is(StepCompletedEvent.Descriptor));

        await bridge.HandleAsync(Envelope(retry), ctx, CancellationToken.None);

        tool.ExecuteCalls.Should().Be(1);
        ctx.Published.Select(x => x.Event)
            .Where(x => x.Is(WorkflowToolCallCompletedEvent.Descriptor))
            .Should().HaveCount(2);
        ctx.Published.Select(x => x.Event)
            .Where(x => x.Is(StepCompletedEvent.Descriptor))
            .Should().ContainSingle()
            .Which.Unpack<StepCompletedEvent>().Success.Should().BeTrue();
        host.States[ToolCallModule.ModuleStateKey]
            .Unpack<ToolCallModuleState>()
            .CompletionTombstones.Should().ContainSingle();
    }

    [Fact]
    public async Task Kernel_ShouldPublishTerminalFailure_WhenStepDispatchPublishFails()
    {
        var workflow = new WorkflowDefinition
        {
            Name = "wf",
            Roles = [],
            Steps =
            [
                new StepDefinition { Id = "step-1", Type = "notify" },
            ],
        };
        var host = new RecordingStateHost { RunId = "run-1" };
        var module = new WorkflowExecutionKernel(workflow, host);
        var ctx = new RecordingEventHandlerContext
        {
            FailPublish = evt => evt is StepRequestEvent,
        };

        await module.HandleAsync(
            Envelope(new StartWorkflowEvent
            {
                RunId = "run-1",
                WorkflowName = "wf",
                Input = "hello",
            }),
            ctx,
            CancellationToken.None);

        var completion = ctx.Published
            .Select(x => x.Event)
            .Where(x => x.Is(WorkflowCompletedEvent.Descriptor))
            .Select(x => x.Unpack<WorkflowCompletedEvent>())
            .Should()
            .ContainSingle()
            .Subject;
        completion.Success.Should().BeFalse();
        completion.Error.Should().StartWith("step_dispatch_failed: step 'step-1' (notify) failed during dispatch: ");
        completion.Error.Should().NotContain("super-secret-token");
    }

    [Fact]
    public async Task Kernel_ShouldPropagateCommittedStatePublicationFailure_WithoutTerminalizingRun()
    {
        var workflow = new WorkflowDefinition
        {
            Name = "wf",
            Roles = [],
            Steps =
            [
                new StepDefinition { Id = "step-1", Type = "notify" },
                new StepDefinition { Id = "step-2", Type = "notify" },
            ],
        };
        var host = new RecordingStateHost { RunId = "run-1" };
        var module = new WorkflowExecutionKernel(workflow, host);
        var publicationFailure = new CommittedStatePublicationException(
            "run-1",
            new StateEvent
            {
                EventId = "event-7",
                Version = 7,
            },
            CommittedStatePublicationFailureStage.AdapterAcceptance,
            new InvalidOperationException("stream adapter unavailable"));
        var stepRequestCount = 0;
        var ctx = new RecordingEventHandlerContext
        {
            FailPublish = evt =>
                evt is StepRequestEvent && ++stepRequestCount == 2
                    ? throw publicationFailure
                    : false,
        };

        await module.HandleAsync(
            Envelope(new StartWorkflowEvent
            {
                RunId = "run-1",
                WorkflowName = "wf",
                Input = "hello",
            }),
            ctx,
            CancellationToken.None);

        var error = await FluentActions.Awaiting(() => module.HandleAsync(
                Envelope(new StepCompletedEvent
                {
                    RunId = "run-1",
                    StepId = "step-1",
                    Success = true,
                    Output = "done",
                }),
                ctx,
                CancellationToken.None))
            .Should()
            .ThrowAsync<CommittedStatePublicationException>();

        error.Which.Should().BeSameAs(publicationFailure);
        ctx.Published
            .Select(static published => published.Event)
            .Should()
            .NotContain(published => published.Is(WorkflowCompletedEvent.Descriptor));
    }

    [Fact]
    public async Task Kernel_ShouldQueryRunLedger_WhenCompensableStepDispatchPublishFailsBeforeExecutorReceipt()
    {
        var workflow = new WorkflowDefinition
        {
            Name = "wf",
            Roles = [],
            Steps =
            [
                new StepDefinition
                {
                    Id = "charge",
                    Type = "tool_call",
                    Compensation = "refund",
                },
                new StepDefinition
                {
                    Id = "refund",
                    Type = "tool_call",
                },
            ],
        };
        var host = new RecordingStateHost
        {
            RunId = "run-1",
            StartCompensationWhenLedgerRecorded = true,
        };
        var module = new WorkflowExecutionKernel(workflow, host);
        var ctx = new RecordingEventHandlerContext
        {
            FailPublish = evt => evt is StepRequestEvent,
        };

        await module.HandleAsync(
            Envelope(new StartWorkflowEvent
            {
                RunId = "run-1",
                WorkflowName = "wf",
                Input = "hello",
            }),
            ctx,
            CancellationToken.None);

        var completion = ctx.Published
            .Select(x => x.Event)
            .Where(x => x.Is(WorkflowCompletedEvent.Descriptor))
            .Select(x => x.Unpack<WorkflowCompletedEvent>())
            .Should()
            .ContainSingle()
            .Subject;
        completion.Success.Should().BeFalse();
        completion.Error.Should().StartWith("step_dispatch_failed: step 'charge' (tool_call) failed during dispatch: ");
        ctx.Published
            .Select(x => x.Event)
            .Should()
            .NotContain(x => x.Is(CompensationRequestEvent.Descriptor));
        host.CompensationStartAttempts.Should().Be(1);
        host.TerminalStepAttempts.Should().ContainSingle().Which.Should().BeNull();
    }

    [Fact]
    public async Task Kernel_ShouldCompensatePreviouslyCompletedStep_WhenNextStepDispatchPublishFails()
    {
        var workflow = new WorkflowDefinition
        {
            Name = "wf",
            Roles = [],
            Steps =
            [
                new StepDefinition
                {
                    Id = "charge",
                    Type = "tool_call",
                    Compensation = "refund",
                },
                new StepDefinition { Id = "notify", Type = "notify" },
                new StepDefinition { Id = "refund", Type = "tool_call" },
            ],
        };
        var host = new RecordingStateHost
        {
            RunId = "run-1",
            StartCompensationWhenLedgerRecorded = true,
        };
        var module = new WorkflowExecutionKernel(workflow, host);
        var ctx = new RecordingEventHandlerContext
        {
            FailPublish = evt => evt is StepRequestEvent { StepId: "notify" },
        };

        await module.HandleAsync(
            Envelope(new StartWorkflowEvent
            {
                RunId = "run-1",
                WorkflowName = "wf",
                Input = "hello",
            }),
            ctx,
            CancellationToken.None);

        var chargeRequest = ctx.Published
            .Select(x => x.Event)
            .Where(x => x.Is(StepRequestEvent.Descriptor))
            .Select(x => x.Unpack<StepRequestEvent>())
            .Should()
            .ContainSingle()
            .Subject;
        ctx.Published.Clear();

        await module.HandleAsync(
            Envelope(new StepCompletedEvent
            {
                RunId = "run-1",
                StepId = "charge",
                ExecutionId = chargeRequest.ExecutionId,
                Success = true,
                Output = "charged",
            }),
            ctx,
            CancellationToken.None);

        host.CompensationStartAttempts.Should().Be(1);
        host.CompensableDispatches.Should().ContainSingle()
            .Which.StepId.Should().Be("charge");
        ctx.Published.Select(x => x.Event)
            .Should()
            .ContainSingle(x => x.Is(CompensationRequestEvent.Descriptor));
    }

    [Fact]
    public async Task Kernel_ShouldEnterCompensationDecision_WhenCompensableDispatchFailsAfterExecutorReceipt()
    {
        var workflow = new WorkflowDefinition
        {
            Name = "wf",
            Roles = [],
            Steps =
            [
                new StepDefinition
                {
                    Id = "charge",
                    Type = "tool_call",
                    Compensation = "refund",
                },
                new StepDefinition
                {
                    Id = "refund",
                    Type = "tool_call",
                },
            ],
        };
        var host = new RecordingStateHost
        {
            RunId = "run-1",
            FailRecordCompensableDispatch = true,
        };
        var module = new WorkflowExecutionKernel(workflow, host);
        var ctx = new RecordingEventHandlerContext();

        await module.HandleAsync(
            Envelope(new StartWorkflowEvent
            {
                RunId = "run-1",
                WorkflowName = "wf",
                Input = "hello",
            }),
            ctx,
            CancellationToken.None);

        var completion = ctx.Published
            .Select(x => x.Event)
            .Where(x => x.Is(WorkflowCompletedEvent.Descriptor))
            .Select(x => x.Unpack<WorkflowCompletedEvent>())
            .Should()
            .ContainSingle()
            .Subject;
        completion.Success.Should().BeFalse();
        completion.Error.Should().StartWith("step_dispatch_failed: step 'charge' (tool_call) failed during dispatch: ");
        host.CompensationStartAttempts.Should().Be(1);
        var terminalStep = host.TerminalStepAttempts.Should()
            .ContainSingle()
            .Subject;
        terminalStep.Should().NotBeNull();
        terminalStep!.FailureOutcome.Should().Be(WorkflowStepFailureOutcome.OutcomeUncertain);
    }

    [Fact]
    public async Task Kernel_ShouldIgnoreDuplicateFailedCompletion_WhenRetryBackoffIsPending()
    {
        var workflow = new WorkflowDefinition
        {
            Name = "wf",
            Roles = [],
            Steps =
            [
                new StepDefinition
                {
                    Id = "step-1",
                    Type = "notify",
                    Retry = new StepRetryPolicy
                    {
                        MaxAttempts = 3,
                        DelayMs = 800,
                    },
                },
            ],
        };
        var host = new RecordingStateHost { RunId = "run-1" };
        var module = new WorkflowExecutionKernel(workflow, host);
        var ctx = new RecordingEventHandlerContext();

        await module.HandleAsync(
            Envelope(new StartWorkflowEvent
            {
                RunId = "run-1",
                WorkflowName = "wf",
                Input = "hello",
            }),
            ctx,
            CancellationToken.None);

        var firstRequest = ctx.Published
            .Select(x => x.Event)
            .Where(x => x.Is(StepRequestEvent.Descriptor))
            .Select(x => x.Unpack<StepRequestEvent>())
            .Should()
            .ContainSingle()
            .Subject;
        ctx.Published.Clear();

        await module.HandleAsync(
            Envelope(new StepCompletedEvent
            {
                RunId = "run-1",
                StepId = "step-1",
                Success = false,
                Error = "transient failure",
                ExecutionId = firstRequest.ExecutionId,
            }),
            ctx,
            CancellationToken.None);

        var stateWithBackoff = host.States["workflow_execution_kernel"].Unpack<WorkflowExecutionKernelState>();
        stateWithBackoff.RetryBackoffsByStepId.Should().ContainKey("step-1");
        ctx.Published.Clear();

        await module.HandleAsync(
            Envelope(new StepCompletedEvent
            {
                RunId = "run-1",
                StepId = "step-1",
                Success = false,
                Error = "duplicate failure",
                ExecutionId = firstRequest.ExecutionId,
            }),
            ctx,
            CancellationToken.None);

        ctx.Published.Select(x => x.Event)
            .Should()
            .NotContain(x => x.Is(StepRequestEvent.Descriptor) || x.Is(WorkflowCompletedEvent.Descriptor));
        var finalState = host.States["workflow_execution_kernel"].Unpack<WorkflowExecutionKernelState>();
        finalState.RetryBackoffsByStepId.Should().ContainKey("step-1");
    }

    [Fact]
    public async Task Kernel_ShouldPublishTerminalFailure_WhenCompletionHandlingThrowsAfterRunIsActive()
    {
        var workflow = new WorkflowDefinition
        {
            Name = "wf",
            Roles = [],
            Steps =
            [
                new StepDefinition { Id = "step-1", Type = "notify" },
                new StepDefinition { Id = "step-2", Type = "notify" },
            ],
        };
        var host = new RecordingStateHost { RunId = "run-1" };
        var module = new WorkflowExecutionKernel(workflow, host);
        var ctx = new RecordingEventHandlerContext();

        await module.HandleAsync(
            Envelope(new StartWorkflowEvent
            {
                RunId = "run-1",
                WorkflowName = "wf",
                Input = "hello",
            }),
            ctx,
            CancellationToken.None);
        ctx.Published.Clear();
        host.FailSave = true;

        await module.HandleAsync(
            Envelope(new StepCompletedEvent
            {
                RunId = "run-1",
                StepId = "step-1",
                Success = true,
                Output = "next",
            }),
            ctx,
            CancellationToken.None);

        var completion = ctx.Published
            .Select(x => x.Event)
            .Where(x => x.Is(WorkflowCompletedEvent.Descriptor))
            .Select(x => x.Unpack<WorkflowCompletedEvent>())
            .Should()
            .ContainSingle()
            .Subject;
        completion.Success.Should().BeFalse();
        completion.Error.Should().StartWith("step_completion_handling_failed: step 'step-1' (notify) failed during completion: ");
        completion.Error.Should().NotContain("super-secret-token");
    }

    private static EventEnvelope Envelope(IMessage payload) => new()
    {
        Id = "envelope-1",
        Payload = Any.Pack(payload),
    };

    private sealed class ThrowingStepExecutor : IEventModule<IWorkflowExecutionContext>
    {
        public string Name => "throwing_executor";

        public int Priority => 0;

        public bool CanHandle(EventEnvelope envelope) =>
            envelope.Payload?.Is(StepRequestEvent.Descriptor) == true;

        public Task HandleAsync(EventEnvelope envelope, IWorkflowExecutionContext ctx, CancellationToken ct) =>
            throw new InvalidOperationException("executor failed with bearer super-secret-token");
    }

    private sealed class RecordingStepExecutor : IEventModule<IWorkflowExecutionContext>
    {
        public string Name => "recording_executor";

        public int Priority => 1;

        public bool CanHandle(EventEnvelope envelope) =>
            envelope.Payload?.Is(StepRequestEvent.Descriptor) == true;

        public Task HandleAsync(EventEnvelope envelope, IWorkflowExecutionContext ctx, CancellationToken ct) =>
            ctx.PublishAsync(
                new StepCompletedEvent
                {
                    RunId = "run-1",
                    StepId = "step-1",
                    Success = true,
                },
                TopologyAudience.Self,
                ct);
    }

    private sealed class DurablePublicationPendingExecutor(Exception exception)
        : IEventModule<IWorkflowExecutionContext>
    {
        public string Name => "durable_publication_pending_executor";

        public int Priority => 0;

        public bool CanHandle(EventEnvelope envelope) =>
            envelope.Payload?.Is(StepRequestEvent.Descriptor) == true;

        public Task HandleAsync(EventEnvelope envelope, IWorkflowExecutionContext ctx, CancellationToken ct) =>
            throw exception;
    }

    private sealed class RecordingEventHandlerContext : IEventHandlerContext
    {
        public string AgentId { get; } = "agent-1";

        public EventEnvelope InboundEnvelope { get; } = new()
        {
            Id = "inbound-1",
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
        };

        public IAgent Agent { get; } = new StubAgent("agent-1");

        public IServiceProvider Services { get; set; } = new NullServiceProvider();

        public ILogger Logger { get; set; } = NullLogger.Instance;

        public Func<IMessage, bool>? FailPublish { get; init; }

        public System.Type? FailNextPublishType { get; set; }

        public bool FailSchedule { get; init; }

        public int ScheduleAttempts { get; private set; }

        public List<(Any Event, TopologyAudience Direction)> Published { get; } = [];

        public List<RuntimeCallbackLease> Canceled { get; } = [];

        public List<(string CallbackId, Any Event)> Scheduled { get; } = [];

        public Task PublishAsync<TEvent>(
            TEvent evt,
            TopologyAudience direction = TopologyAudience.Children,
            CancellationToken ct = default,
            EventEnvelopePublishOptions? options = null)
            where TEvent : IMessage
        {
            ct.ThrowIfCancellationRequested();
            if (FailNextPublishType?.IsInstanceOfType(evt) == true)
            {
                FailNextPublishType = null;
                throw new InvalidOperationException("publish failed with bearer super-secret-token");
            }

            if (FailPublish?.Invoke(evt) == true)
                throw new InvalidOperationException("publish failed with bearer super-secret-token");

            Published.Add((Any.Pack(evt), direction));
            return Task.CompletedTask;
        }

        public Task SendToAsync<TEvent>(
            string targetActorId,
            TEvent evt,
            CancellationToken ct = default,
            EventEnvelopePublishOptions? options = null)
            where TEvent : IMessage =>
            Task.CompletedTask;

        public Task<RuntimeCallbackLease> ScheduleSelfDurableTimeoutAsync(
            string callbackId,
            TimeSpan dueTime,
            IMessage evt,
            EventEnvelopePublishOptions? options = null,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            ScheduleAttempts++;
            if (FailSchedule)
                throw new InvalidOperationException("schedule failed");

            Scheduled.Add((callbackId, Any.Pack(evt)));
            return Task.FromResult(new RuntimeCallbackLease(AgentId, callbackId, 1, RuntimeCallbackBackend.InMemory));
        }

        public Task<RuntimeCallbackLease> ScheduleSelfDurableTimerAsync(
            string callbackId,
            TimeSpan dueTime,
            TimeSpan period,
            IMessage evt,
            EventEnvelopePublishOptions? options = null,
            CancellationToken ct = default) =>
            Task.FromResult(new RuntimeCallbackLease(AgentId, callbackId, 1, RuntimeCallbackBackend.InMemory));

        public Task CancelDurableCallbackAsync(RuntimeCallbackLease lease, CancellationToken ct = default)
        {
            Canceled.Add(lease);
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingWorkflowTool(
        string name,
        Func<WorkflowToolExecutionRequest, WorkflowToolExecutionResult> execute) : IWorkflowTool
    {
        public string Name { get; } = name;

        public int ExecuteCalls { get; private set; }

        public Task<WorkflowToolExecutionResult> ExecuteAsync(
            WorkflowToolExecutionRequest request,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            ExecuteCalls++;
            return Task.FromResult(execute(request));
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

    private sealed class RecordingStateHost : IWorkflowExecutionStateHost
    {
        public string RunId { get; init; } = "run-1";

        public WorkflowExecutionRuntimeContext RuntimeContext { get; } = new();

        public WorkflowRunExecutionContextState ExecutionContextSnapshot { get; } = new();

        public bool FailSave { get; set; }

        public int? FailSaveAttempt { get; init; }

        public int SaveAttempts { get; private set; }

        public bool FailRecordCompensableDispatch { get; init; }

        public bool StartCompensationWhenLedgerRecorded { get; init; }

        public int CompensationStartAttempts { get; private set; }

        public List<CompensableStepDispatchedEvent> CompensableDispatches { get; } = [];

        public List<StepCompletedEvent?> TerminalStepAttempts { get; } = [];

        public Dictionary<string, Any> States { get; } = new(StringComparer.Ordinal);

        public Any? GetExecutionState(string scopeKey) =>
            States.GetValueOrDefault(scopeKey);

        public IReadOnlyList<KeyValuePair<string, Any>> GetExecutionStates() =>
            States.ToList();

        public Task UpsertExecutionStateAsync(string scopeKey, Any state, CancellationToken ct = default)
        {
            SaveAttempts++;
            if (FailSave || SaveAttempts == FailSaveAttempt)
                throw new InvalidOperationException("save failed with bearer super-secret-token");

            States[scopeKey] = state;
            return Task.CompletedTask;
        }

        public Task ClearExecutionStateAsync(string scopeKey, CancellationToken ct = default)
        {
            States.Remove(scopeKey);
            return Task.CompletedTask;
        }

        public Task UpdateExecutionContextAsync(WorkflowRunExecutionContextDelta delta, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task ClearExecutionContextAsync(CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task<WorkflowCompensationTransitionResult> TryStartCompensationAsync(
            WorkflowCompletedEvent terminalFailure,
            StepCompletedEvent? terminalStep,
            CancellationToken ct)
        {
            TerminalStepAttempts.Add(terminalStep?.Clone());
            CompensationStartAttempts++;
            if (StartCompensationWhenLedgerRecorded && CompensableDispatches.Count > 0)
            {
                var dispatch = CompensableDispatches[^1];
                return Task.FromResult(new WorkflowCompensationTransitionResult(
                    WorkflowCompensationTransitionStatus.Started,
                    dispatch.CompensationStepId,
                    terminalStep?.StepId ?? string.Empty,
                    dispatch.IdempotencyKey,
                    string.Empty,
                    "compensation-exec-1"));
            }

            return Task.FromResult(NoCompensableLedger());
        }

        public Task RecordCompensableStepDispatchAsync(CompensableStepDispatchedEvent evt, CancellationToken ct)
        {
            if (FailRecordCompensableDispatch)
                throw new InvalidOperationException("compensable dispatch record failed with bearer super-secret-token");

            CompensableDispatches.Add(evt);
            return Task.CompletedTask;
        }

        public Task<WorkflowCompensationTransitionResult> RecordCompensationStepCompletionAsync(
            CompensationStepCompletedEvent completion,
            CancellationToken ct = default) =>
            Task.FromResult(NoCompensableLedger());

        public Task<WorkflowCompensationTransitionResult> RecordCompensationPhaseDeadlineExceededAsync(
            string runId,
            string error,
            CancellationToken ct = default) =>
            Task.FromResult(NoCompensableLedger());

        private static WorkflowCompensationTransitionResult NoCompensableLedger() =>
            new(
                WorkflowCompensationTransitionStatus.NoCompensableLedger,
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty);
    }

    private sealed class NullServiceProvider : IServiceProvider
    {
        public object? GetService(System.Type serviceType) => null;
    }

    private sealed class StubAgent(string id) : IAgent
    {
        public string Id { get; } = id;

        public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task<string> GetDescriptionAsync() =>
            Task.FromResult("stub");

        public Task<IReadOnlyList<System.Type>> GetSubscribedEventTypesAsync() =>
            Task.FromResult<IReadOnlyList<System.Type>>([]);

        public Task ActivateAsync(CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task DeactivateAsync(CancellationToken ct = default) =>
            Task.CompletedTask;
    }
}
