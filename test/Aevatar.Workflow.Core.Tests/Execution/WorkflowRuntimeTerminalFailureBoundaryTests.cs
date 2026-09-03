using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Credentials;
using Aevatar.Foundation.Abstractions.Credentials.Testing;
using Aevatar.Foundation.Abstractions.EventSourcing;
using Aevatar.Foundation.Abstractions.EventModules;
using Aevatar.Foundation.Abstractions.Persistence;
using Aevatar.Foundation.Abstractions.Runtime;
using Aevatar.Foundation.Abstractions.Runtime.Callbacks;
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.Foundation.Core.Runtime;
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
using System.Threading.Channels;

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
    public async Task Bridge_ShouldPropagateActorOwnedDurablePublicationPending_ForRuntimeRedelivery()
    {
        var bridge = new WorkflowExecutionBridgeModule(
            [new DurablePublicationPendingExecutor(new WorkflowDurablePublicationPendingException(
                "pending",
                new InvalidOperationException("callback already scheduled")))],
            new RecordingStateHost { RunId = "run-1" });
        var ctx = new RecordingEventHandlerContext();

        var failure = await FluentActions.Awaiting(() => bridge.HandleAsync(
                Envelope(new StepRequestEvent
                {
                    RunId = "run-1",
                    StepId = "tool-step",
                    StepType = "tool_call",
                    ExecutionId = "exec-1",
                }),
                ctx,
                CancellationToken.None))
            .Should().ThrowAsync<WorkflowDurablePublicationPendingException>();

        failure.Which.Should().BeAssignableTo<IRuntimeEnvelopeRetryableException>();
        ctx.Published.Should().BeEmpty();
    }

    [Fact]
    public async Task Bridge_ForEachToolCalls_ShouldOverlapTenExecutionsAndReachSingleParentCompletion()
    {
        const int expectedConcurrency = 10;
        const string runId = "run-foreach-tool-overlap";
        const string parentStepId = "foreach-tools";
        var tool = new OverlappingWorkflowTool("blocking_tool", expectedConcurrency);
        var host = new RecordingStateHost { RunId = runId };
        var bridge = new WorkflowExecutionBridgeModule(
            [
                new ForEachModule(),
                new ToolCallModule(
                    [new SingleWorkflowToolSource(tool)],
                    NullLogger<ToolCallModule>.Instance),
            ],
            host);
        var ctx = new RecordingEventHandlerContext();

        await bridge.HandleAsync(
            Envelope(new StepRequestEvent
            {
                RunId = runId,
                StepId = parentStepId,
                StepType = "foreach",
                ExecutionId = "foreach-exec-1",
                Input = string.Join("\n---\n", Enumerable.Range(0, expectedConcurrency).Select(static i => $"item-{i}")),
                Parameters =
                {
                    ["sub_step_type"] = "tool_call",
                    ["sub_param_tool"] = tool.Name,
                    ["min_concurrent_workers"] = expectedConcurrency.ToString(),
                    ["max_concurrent_workers"] = expectedConcurrency.ToString(),
                },
            }),
            ctx,
            CancellationToken.None);

        var childRequests = ctx.Published
            .Select(static publication => publication.Event)
            .Where(static payload => payload.Is(StepRequestEvent.Descriptor))
            .Select(static payload => payload.Unpack<StepRequestEvent>())
            .ToArray();
        childRequests.Should().HaveCount(expectedConcurrency);
        ctx.Published.Clear();

        var sequentialPump = Task.Run(async () =>
        {
            foreach (var child in childRequests)
                await bridge.HandleAsync(Envelope(child), ctx, CancellationToken.None);
        });
        try
        {
            await tool.AllExpectedExecutionsEntered.WaitAsync(TimeSpan.FromSeconds(5));
            tool.ExecuteCalls.Should().Be(expectedConcurrency);
            tool.MaxActiveExecutions.Should().Be(expectedConcurrency);
            host.States[ToolCallModule.ModuleStateKey]
                .Unpack<ToolCallModuleState>()
                .PendingExecutions.Should().HaveCount(expectedConcurrency);
        }
        finally
        {
            tool.ReleaseAll();
            await sequentialPump.WaitAsync(TimeSpan.FromSeconds(5));
        }

        var attemptCompletions = new List<RecordedPublication>(expectedConcurrency);
        for (var i = 0; i < expectedConcurrency; i++)
        {
            attemptCompletions.Add(await ctx.WaitForPublishedAsync(static publication =>
                publication.Event.Is(WorkflowToolCallAttemptCompletedEvent.Descriptor)));
        }

        var childCompletions = new List<StepCompletedEvent>(expectedConcurrency);
        foreach (var publication in attemptCompletions)
        {
            var attemptCompletion = publication.Event.Unpack<WorkflowToolCallAttemptCompletedEvent>();
            var envelope = Envelope(attemptCompletion);
            envelope.Route = EnvelopeRouteSemantics.CreateTopologyPublication(ctx.AgentId, TopologyAudience.Self);
            envelope.Runtime = new EnvelopeRuntime
            {
                DeliveryIdentity = new DeliveryIdentity
                {
                    OperationId = publication.Options?.Delivery?.OperationId ?? string.Empty,
                },
            };
            await bridge.HandleAsync(envelope, ctx, CancellationToken.None);
            childCompletions.Add((await ctx.WaitForPublishedAsync(candidate =>
                    candidate.Event.Is(StepCompletedEvent.Descriptor) &&
                    string.Equals(
                        candidate.Event.Unpack<StepCompletedEvent>().StepId,
                        attemptCompletion.StepId,
                        StringComparison.Ordinal)))
                .Event.Unpack<StepCompletedEvent>());
        }

        var settledToolState = host.States[ToolCallModule.ModuleStateKey]
            .Unpack<ToolCallModuleState>();
        settledToolState.PendingExecutions.Should().BeEmpty();
        settledToolState.CompletionTombstones.Should().HaveCount(expectedConcurrency);

        childCompletions.Should().HaveCount(expectedConcurrency)
            .And.OnlyContain(static completion => completion.Success);

        foreach (var childCompletion in childCompletions)
            await bridge.HandleAsync(Envelope(childCompletion), ctx, CancellationToken.None);

        var parentCompletion = (await ctx.WaitForPublishedAsync(candidate =>
                candidate.Event.Is(StepCompletedEvent.Descriptor) &&
                string.Equals(
                    candidate.Event.Unpack<StepCompletedEvent>().StepId,
                    parentStepId,
                    StringComparison.Ordinal)))
            .Event.Unpack<StepCompletedEvent>();
        parentCompletion.Success.Should().BeTrue();
        host.States[ForEachModule.ModuleStateKey]
            .Unpack<ForEachModuleState>()
            .CompletionTombstones.Should().ContainSingle();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Bridge_ForEachToolCalls_ShouldTopUpAfterFirstChildCompletion(bool recreateForEachBeforeCompletion)
    {
        const int expectedConcurrency = 10;
        const int totalItems = expectedConcurrency + 1;
        const string runId = "run-foreach-tool-top-up";
        const string parentStepId = "foreach-tools";
        var tool = new OverlappingWorkflowTool("blocking_tool", expectedConcurrency);
        var host = new RecordingStateHost { RunId = runId };
        var toolCallModule = new ToolCallModule(
            [new SingleWorkflowToolSource(tool)],
            NullLogger<ToolCallModule>.Instance);
        var bridge = new WorkflowExecutionBridgeModule(
            [new ForEachModule(), toolCallModule],
            host);
        var ctx = new RecordingEventHandlerContext();

        await bridge.HandleAsync(
            Envelope(new StepRequestEvent
            {
                RunId = runId,
                StepId = parentStepId,
                StepType = "foreach",
                ExecutionId = "foreach-exec-1",
                Input = string.Join("\n---\n", Enumerable.Range(0, totalItems).Select(static i => $"item-{i}")),
                Parameters =
                {
                    ["sub_step_type"] = "tool_call",
                    ["sub_param_tool"] = tool.Name,
                    ["min_concurrent_workers"] = expectedConcurrency.ToString(),
                    ["max_concurrent_workers"] = expectedConcurrency.ToString(),
                },
            }),
            ctx,
            CancellationToken.None);

        var childRequests = ctx.Published
            .Select(static publication => publication.Event)
            .Where(static payload => payload.Is(StepRequestEvent.Descriptor))
            .Select(static payload => payload.Unpack<StepRequestEvent>())
            .ToArray();
        childRequests.Should().HaveCount(expectedConcurrency);
        ctx.Published.Clear();

        foreach (var child in childRequests)
            await bridge.HandleAsync(Envelope(child), ctx, CancellationToken.None);

        await tool.AllExpectedExecutionsEntered.WaitAsync(TimeSpan.FromSeconds(5));
        tool.ReleaseExecution(1);

        var attemptPublication = await ctx.WaitForPublishedAsync(static publication =>
            publication.Event.Is(WorkflowToolCallAttemptCompletedEvent.Descriptor));
        var attemptCompletion = attemptPublication.Event.Unpack<WorkflowToolCallAttemptCompletedEvent>();
        var attemptEnvelope = Envelope(attemptCompletion);
        attemptEnvelope.Route = EnvelopeRouteSemantics.CreateTopologyPublication(ctx.AgentId, TopologyAudience.Self);
        attemptEnvelope.Runtime = new EnvelopeRuntime
        {
            DeliveryIdentity = new DeliveryIdentity
            {
                OperationId = attemptPublication.Options?.Delivery?.OperationId ?? string.Empty,
            },
        };
        await bridge.HandleAsync(attemptEnvelope, ctx, CancellationToken.None);

        var childCompletion = (await ctx.WaitForPublishedAsync(candidate =>
                candidate.Event.Is(StepCompletedEvent.Descriptor) &&
                string.Equals(
                    candidate.Event.Unpack<StepCompletedEvent>().StepId,
                    attemptCompletion.StepId,
                    StringComparison.Ordinal)))
            .Event.Unpack<StepCompletedEvent>();

        var completionBridge = recreateForEachBeforeCompletion
            ? new WorkflowExecutionBridgeModule([new ForEachModule(), toolCallModule], host)
            : bridge;
        await completionBridge.HandleAsync(Envelope(childCompletion), ctx, CancellationToken.None);

        ctx.Published
            .Select(static publication => publication.Event)
            .Where(static payload => payload.Is(StepRequestEvent.Descriptor))
            .Select(static payload => payload.Unpack<StepRequestEvent>())
            .Should()
            .ContainSingle(request => request.Input == "item-10");

        tool.ReleaseAll();
    }

    [Fact]
    public async Task Kernel_WaitSignalTimeoutWithFallback_ShouldDispatchFallbackStep()
    {
        const string runId = "run-wait-signal-timeout-fallback";
        var workflow = new WorkflowDefinition
        {
            Name = "wait-signal-timeout-fallback",
            Roles = [],
            Steps =
            [
                new StepDefinition
                {
                    Id = "wait-choice",
                    Type = "wait_signal",
                    OnError = new StepErrorPolicy
                    {
                        Strategy = "fallback",
                        FallbackStep = "timeout-hold-all",
                    },
                },
                new StepDefinition { Id = "timeout-hold-all", Type = "assign" },
            ],
        };
        var host = new RecordingStateHost { RunId = runId };
        var kernel = new WorkflowExecutionKernel(workflow, host);
        var ctx = new RecordingEventHandlerContext();

        await kernel.HandleAsync(
            Envelope(new StartWorkflowEvent
            {
                RunId = runId,
                WorkflowName = workflow.Name,
                Input = "start",
            }),
            ctx,
            CancellationToken.None);
        var kernelState = host.States[WorkflowExecutionKernel.ModuleStateKey]
            .Unpack<WorkflowExecutionKernelState>();
        var waitExecutionId = kernelState.ExecutionIdsByStepId["wait-choice"];
        ctx.Published.Clear();

        await kernel.HandleAsync(
            Envelope(new StepCompletedEvent
            {
                RunId = runId,
                StepId = "wait-choice",
                ExecutionId = waitExecutionId,
                Success = false,
                Error = "signal 'dinner_date_user_choice' timed out after 10000ms",
            }),
            ctx,
            CancellationToken.None);

        var fallbackRequest = ctx.Published
            .Select(static publication => publication.Event)
            .Where(static payload => payload.Is(StepRequestEvent.Descriptor))
            .Select(static payload => payload.Unpack<StepRequestEvent>())
            .Should()
            .ContainSingle()
            .Subject;
        fallbackRequest.StepId.Should().Be("timeout-hold-all");
        ctx.Published
            .Select(static publication => publication.Event)
            .Should()
            .NotContain(static payload => payload.Is(WorkflowCompletedEvent.Descriptor));
    }

    [Theory]
    [InlineData("foreach-parent_item_0")]
    [InlineData("foreach-parent_execution_0123456789abcdef_item_42")]
    public async Task Kernel_ShouldNotCheckpointDirectForEachChildCompletion(string childStepId)
    {
        const string runId = "run-foreach";
        const string parentStepId = "foreach-parent";
        const string childExecutionId = "child-execution";
        var workflow = new WorkflowDefinition
        {
            Name = "foreach-workflow",
            Roles = [],
            Steps =
            [
                new StepDefinition { Id = parentStepId, Type = "for_each" },
            ],
        };
        var host = new RecordingStateHost { RunId = runId };
        var kernel = new WorkflowExecutionKernel(workflow, host);
        var ctx = new RecordingEventHandlerContext();

        await kernel.HandleAsync(
            Envelope(new StartWorkflowEvent
            {
                RunId = runId,
                WorkflowName = workflow.Name,
                Input = "item",
            }),
            ctx,
            CancellationToken.None);
        var kernelState = host.States[WorkflowExecutionKernel.ModuleStateKey]
            .Unpack<WorkflowExecutionKernelState>();
        host.States[ForEachModule.ModuleStateKey] = Any.Pack(CreateForEachAttemptState(
            runId,
            parentStepId,
            kernelState.ExecutionIdsByStepId[parentStepId],
            childStepId,
            childExecutionId));
        var saveAttemptsBeforeCompletion = host.SaveAttempts;

        await kernel.HandleAsync(
            Envelope(new StepCompletedEvent
            {
                RunId = runId,
                StepId = childStepId,
                ExecutionId = childExecutionId,
                Success = true,
                Output = "child-output",
            }),
            ctx,
            CancellationToken.None);

        host.SaveAttempts.Should().Be(saveAttemptsBeforeCompletion);
        kernelState = host.States[WorkflowExecutionKernel.ModuleStateKey]
            .Unpack<WorkflowExecutionKernelState>();
        kernelState.CurrentStepId.Should().Be(parentStepId);
        kernelState.Variables.Should().NotContainKey(childStepId);
    }

    [Fact]
    public async Task Kernel_ShouldIgnoreLateChildWithoutExecutionIdFromCompletedForEachAttempt()
    {
        const string runId = "run-foreach-late-child";
        const string parentStepId = "foreach-parent";
        const string childStepId = "foreach-parent_execution_0123456789abcdef_item_0";
        const string childExecutionId = "child-execution-old";
        var workflow = new WorkflowDefinition
        {
            Name = "foreach-late-child-workflow",
            Roles = [],
            Steps =
            [
                new StepDefinition { Id = parentStepId, Type = "foreach", Next = "done" },
                new StepDefinition { Id = "done", Type = "notify" },
            ],
        };
        var host = new RecordingStateHost { RunId = runId };
        var kernel = new WorkflowExecutionKernel(workflow, host);
        var ctx = new RecordingEventHandlerContext();
        await kernel.HandleAsync(
            Envelope(new StartWorkflowEvent
            {
                RunId = runId,
                WorkflowName = workflow.Name,
                Input = "item",
            }),
            ctx,
            CancellationToken.None);
        var kernelState = host.States[WorkflowExecutionKernel.ModuleStateKey]
            .Unpack<WorkflowExecutionKernelState>();
        var parentExecutionId = kernelState.ExecutionIdsByStepId[parentStepId];
        host.States[ForEachModule.ModuleStateKey] = Any.Pack(new ForEachModuleState
        {
            CompletedParentAttempts =
            {
                ["completed-parent-attempt"] = new ForEachCompletedParentAttemptState
                {
                    ParentRunId = runId,
                    ParentStepId = parentStepId,
                    ParentExecutionId = parentExecutionId,
                    ChildExecutionIds =
                    {
                        [childStepId] = childExecutionId,
                    },
                },
            },
        });

        await kernel.HandleAsync(
            Envelope(new StepCompletedEvent
            {
                RunId = runId,
                StepId = parentStepId,
                ExecutionId = parentExecutionId,
                Success = true,
                Output = "parent-output",
            }),
            ctx,
            CancellationToken.None);
        kernelState = host.States[WorkflowExecutionKernel.ModuleStateKey]
            .Unpack<WorkflowExecutionKernelState>();
        kernelState.CurrentStepId.Should().Be("done");
        var saveAttemptsBeforeLateChild = host.SaveAttempts;

        await kernel.HandleAsync(
            Envelope(new StepCompletedEvent
            {
                RunId = runId,
                StepId = childStepId,
                Success = true,
                Output = "late-child-output",
            }),
            ctx,
            CancellationToken.None);

        host.SaveAttempts.Should().Be(saveAttemptsBeforeLateChild);
        host.States[WorkflowExecutionKernel.ModuleStateKey]
            .Unpack<WorkflowExecutionKernelState>()
            .Variables.Should().NotContainKey(childStepId);
    }

    [Theory]
    [InlineData("foreach", "foreach-parent_item_0_nested")]
    [InlineData("foreach", "foreach-parent_item_00")]
    [InlineData("foreach", "foreach-parent_execution_0123456789abcde_item_0")]
    [InlineData("foreach", "other-parent_item_0")]
    [InlineData("notify", "foreach-parent_item_0")]
    public async Task Kernel_ShouldPreserveUnknownCompletionCheckpoint_WhenItIsNotCurrentForEachDirectChild(
        string currentStepType,
        string childStepId)
    {
        var workflow = new WorkflowDefinition
        {
            Name = "internal-completion-workflow",
            Roles = [],
            Steps =
            [
                new StepDefinition { Id = "foreach-parent", Type = currentStepType },
            ],
        };
        var host = new RecordingStateHost { RunId = "run-internal" };
        var kernel = new WorkflowExecutionKernel(workflow, host);
        var ctx = new RecordingEventHandlerContext();

        await kernel.HandleAsync(
            Envelope(new StartWorkflowEvent
            {
                RunId = "run-internal",
                WorkflowName = workflow.Name,
                Input = "input",
            }),
            ctx,
            CancellationToken.None);
        var kernelState = host.States[WorkflowExecutionKernel.ModuleStateKey]
            .Unpack<WorkflowExecutionKernelState>();
        host.States[ForEachModule.ModuleStateKey] = Any.Pack(CreateForEachAttemptState(
            "run-internal",
            "foreach-parent",
            kernelState.ExecutionIdsByStepId["foreach-parent"],
            childStepId,
            "child-execution"));
        var saveAttemptsBeforeCompletion = host.SaveAttempts;

        await kernel.HandleAsync(
            Envelope(new StepCompletedEvent
            {
                RunId = "run-internal",
                StepId = childStepId,
                ExecutionId = "child-execution",
                Success = true,
                Output = "internal-output",
            }),
            ctx,
            CancellationToken.None);

        host.SaveAttempts.Should().Be(saveAttemptsBeforeCompletion + 1);
        host.States[WorkflowExecutionKernel.ModuleStateKey]
            .Unpack<WorkflowExecutionKernelState>()
            .Variables.Should().Contain(childStepId, "internal-output");
    }

    [Theory]
    [InlineData("missing_foreach_state")]
    [InlineData("missing_parent_run_id")]
    [InlineData("wrong_parent_run_id")]
    [InlineData("missing_parent_step_id")]
    [InlineData("wrong_parent_step_id")]
    [InlineData("missing_parent_execution_id")]
    [InlineData("wrong_parent_execution_id")]
    [InlineData("missing_child_membership")]
    [InlineData("missing_expected_child_execution_id")]
    [InlineData("wrong_expected_child_execution_id")]
    [InlineData("missing_received_child_execution_id")]
    [InlineData("missing_kernel_parent_execution_id")]
    public async Task Kernel_ShouldPreserveCheckpoint_WhenForEachAttemptLedgerIsIncompleteOrMismatched(
        string mismatch)
    {
        const string runId = "run-foreach-ledger-mismatch";
        const string parentStepId = "foreach-parent";
        const string childStepId = "foreach-parent_execution_0123456789abcdef_item_0";
        const string childExecutionId = "child-execution";
        var workflow = new WorkflowDefinition
        {
            Name = "foreach-ledger-mismatch",
            Roles = [],
            Steps = [new StepDefinition { Id = parentStepId, Type = "foreach" }],
        };
        var host = new RecordingStateHost { RunId = runId };
        var kernel = new WorkflowExecutionKernel(workflow, host);
        var ctx = new RecordingEventHandlerContext();

        await kernel.HandleAsync(
            Envelope(new StartWorkflowEvent
            {
                RunId = runId,
                WorkflowName = workflow.Name,
                Input = "item",
            }),
            ctx,
            CancellationToken.None);
        var kernelState = host.States[WorkflowExecutionKernel.ModuleStateKey]
            .Unpack<WorkflowExecutionKernelState>();
        var parentExecutionId = kernelState.ExecutionIdsByStepId[parentStepId];
        var forEachState = CreateForEachAttemptState(
            runId,
            parentStepId,
            parentExecutionId,
            childStepId,
            childExecutionId);
        var parentState = forEachState.Parents.Values.Should().ContainSingle().Subject;
        switch (mismatch)
        {
            case "missing_foreach_state":
                break;
            case "missing_parent_run_id":
                parentState.ParentRunId = string.Empty;
                host.States[ForEachModule.ModuleStateKey] = Any.Pack(forEachState);
                break;
            case "wrong_parent_run_id":
                parentState.ParentRunId = "other-run";
                host.States[ForEachModule.ModuleStateKey] = Any.Pack(forEachState);
                break;
            case "missing_parent_step_id":
                parentState.ParentStepId = string.Empty;
                host.States[ForEachModule.ModuleStateKey] = Any.Pack(forEachState);
                break;
            case "wrong_parent_step_id":
                parentState.ParentStepId = "other-parent";
                host.States[ForEachModule.ModuleStateKey] = Any.Pack(forEachState);
                break;
            case "missing_parent_execution_id":
                parentState.ParentExecutionId = string.Empty;
                host.States[ForEachModule.ModuleStateKey] = Any.Pack(forEachState);
                break;
            case "wrong_parent_execution_id":
                parentState.ParentExecutionId = "other-parent-execution";
                host.States[ForEachModule.ModuleStateKey] = Any.Pack(forEachState);
                break;
            case "missing_child_membership":
                parentState.ChildExecutionIds.Clear();
                host.States[ForEachModule.ModuleStateKey] = Any.Pack(forEachState);
                break;
            case "missing_expected_child_execution_id":
                parentState.ChildExecutionIds[childStepId] = string.Empty;
                host.States[ForEachModule.ModuleStateKey] = Any.Pack(forEachState);
                break;
            case "wrong_expected_child_execution_id":
                parentState.ChildExecutionIds[childStepId] = "other-child-execution";
                host.States[ForEachModule.ModuleStateKey] = Any.Pack(forEachState);
                break;
            case "missing_received_child_execution_id":
                host.States[ForEachModule.ModuleStateKey] = Any.Pack(forEachState);
                break;
            case "missing_kernel_parent_execution_id":
                kernelState.ExecutionIdsByStepId.Remove(parentStepId);
                host.States[WorkflowExecutionKernel.ModuleStateKey] = Any.Pack(kernelState);
                host.States[ForEachModule.ModuleStateKey] = Any.Pack(forEachState);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(mismatch), mismatch, null);
        }

        var saveAttemptsBeforeCompletion = host.SaveAttempts;
        await kernel.HandleAsync(
            Envelope(new StepCompletedEvent
            {
                RunId = runId,
                StepId = childStepId,
                ExecutionId = mismatch == "missing_received_child_execution_id"
                    ? string.Empty
                    : childExecutionId,
                Success = true,
                Output = "compatibility-output",
            }),
            ctx,
            CancellationToken.None);

        if (mismatch == "missing_received_child_execution_id")
        {
            // A completion that omits its execution id but names an exact ledgered child of
            // the current foreach attempt is that child's completion: the kernel leaves it to
            // the foreach module and must not park it as a compatibility variable.
            host.SaveAttempts.Should().Be(saveAttemptsBeforeCompletion);
            host.States[WorkflowExecutionKernel.ModuleStateKey]
                .Unpack<WorkflowExecutionKernelState>()
                .Variables.Should().NotContainKey(childStepId);
            return;
        }

        host.SaveAttempts.Should().Be(saveAttemptsBeforeCompletion + 1);
        host.States[WorkflowExecutionKernel.ModuleStateKey]
            .Unpack<WorkflowExecutionKernelState>()
            .Variables.Should().Contain(childStepId, "compatibility-output");
    }

    [Fact]
    public async Task Bridge_ForEachNewRun_ShouldDiscardCompletedChildOutputsFromPreviousRun()
    {
        const string runId = "current-run";
        var host = new RecordingStateHost { RunId = runId };
        host.States[ForEachModule.ModuleStateKey] = Any.Pack(new ForEachModuleState
        {
            CompletedChildOutputsRunId = "previous-run",
            CompletedChildOutputs =
            {
                ["previous-child"] = "previous-output",
            },
        });
        var bridge = new WorkflowExecutionBridgeModule([new ForEachModule()], host);

        await bridge.HandleAsync(
            Envelope(new StepRequestEvent
            {
                RunId = runId,
                StepId = "foreach-parent",
                StepType = "foreach",
                ExecutionId = "parent-execution",
                Input = "item",
                Parameters =
                {
                    ["sub_step_type"] = "notify",
                },
            }),
            new RecordingEventHandlerContext(),
            CancellationToken.None);

        var state = host.States[ForEachModule.ModuleStateKey].Unpack<ForEachModuleState>();
        state.CompletedChildOutputs.Should().BeEmpty();
        state.CompletedChildOutputsRunId.Should().BeEmpty();
        state.Parents.Values.Should().ContainSingle()
            .Which.ParentRunId.Should().Be(runId);
    }

    [Theory]
    [InlineData(false, "previous-run")]
    [InlineData(false, "")]
    [InlineData(true, "previous-run")]
    [InlineData(true, "   ")]
    public async Task BridgeAndForEach_ShouldIgnoreFencedStepRequestWithoutMutatingCurrentRun(
        bool invokeForEachDirectly,
        string requestedRunId)
    {
        const string currentRunId = "current-run";
        var host = new RecordingStateHost { RunId = currentRunId };
        var retainedState = new ForEachModuleState
        {
            CompletedChildOutputsRunId = currentRunId,
            CompletedChildOutputs =
            {
                ["current-child"] = "current-output",
            },
        };
        host.States[ForEachModule.ModuleStateKey] = Any.Pack(retainedState);
        var retainedPackedState = host.States[ForEachModule.ModuleStateKey].Clone();
        var ctx = new RecordingEventHandlerContext();
        var request = Envelope(new StepRequestEvent
        {
            RunId = requestedRunId,
            StepId = "foreach-parent",
            StepType = "foreach",
            ExecutionId = "stale-parent-execution",
            Input = "stale-item",
            Parameters =
            {
                ["sub_step_type"] = "notify",
            },
        });

        if (invokeForEachDirectly)
        {
            var workflowContext = WorkflowExecutionContextAdapter.Create(ctx, host);
            await new ForEachModule().HandleAsync(request, workflowContext, CancellationToken.None);
        }
        else
        {
            var bridge = new WorkflowExecutionBridgeModule([new ForEachModule()], host);
            await bridge.HandleAsync(request, ctx, CancellationToken.None);
        }

        host.SaveAttempts.Should().Be(0);
        host.ClearAttempts.Should().Be(0);
        host.States[ForEachModule.ModuleStateKey].Should().Be(retainedPackedState);
        ctx.Published.Should().BeEmpty();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NormalizedKernelAndBridge_ForEachDirectChildren_ShouldRecoverAndCheckpointCanonicalChildren(
        bool recreateForEachAfterFirstCompletion)
    {
        const string runId = "run-foreach-kernel-checkpoints";
        const string parentStepId = "foreach-parent";
        var workflow = new WorkflowDefinition
        {
            Name = "foreach-kernel-checkpoints",
            Roles = [],
            Steps =
            [
                new StepDefinition
                {
                    Id = parentStepId,
                    Type = "foreach",
                    Next = "done",
                    Parameters =
                    {
                        ["sub_step_type"] = "notify",
                        ["min_concurrent_workers"] = "3",
                        ["max_concurrent_workers"] = "3",
                    },
                },
                new StepDefinition { Id = "done", Type = "notify" },
            ],
        };
        var host = CreateNormalizedStateHost(runId);
        var kernel = new WorkflowExecutionKernel(workflow, host);
        var foreachBridge = new WorkflowExecutionBridgeModule([new ForEachModule()], host);
        var ctx = new RecordingEventHandlerContext();

        await kernel.HandleAsync(
            Envelope(new StartWorkflowEvent
            {
                RunId = runId,
                WorkflowName = workflow.Name,
                Input = "item-0\n---\nitem-1\n---\nitem-2",
                ValueRepresentation = WorkflowExecutionValueRepresentation.Normalized,
            }),
            ctx,
            CancellationToken.None);
        var parentRequest = ctx.Published
            .Select(static publication => publication.Event)
            .Where(static payload => payload.Is(StepRequestEvent.Descriptor))
            .Select(static payload => payload.Unpack<StepRequestEvent>())
            .Should().ContainSingle().Subject;
        ctx.Published.Clear();

        await foreachBridge.HandleAsync(Envelope(parentRequest), ctx, CancellationToken.None);
        var childRequests = ctx.Published
            .Select(static publication => publication.Event)
            .Where(static payload => payload.Is(StepRequestEvent.Descriptor))
            .Select(static payload => payload.Unpack<StepRequestEvent>())
            .OrderBy(static request => request.Input, StringComparer.Ordinal)
            .ToArray();
        childRequests.Should().HaveCount(3);
        var dispatchedKernelState = host.States[WorkflowExecutionKernel.ModuleStateKey]
            .Unpack<WorkflowExecutionKernelState>();
        dispatchedKernelState.NormalizedValues.Should().NotBeNull();
        dispatchedKernelState.NormalizedValues!.PendingInternalDispatches.Should().HaveCount(3);
        foreach (var child in childRequests)
        {
            WorkflowExecutionValueStore.TryGetInternalDispatch(
                    dispatchedKernelState,
                    new StepCompletedEvent
                    {
                        RunId = runId,
                        StepId = child.StepId,
                        ExecutionId = child.ExecutionId,
                    },
                    out _)
                .Should().BeTrue();
        }
        ctx.Published.Clear();

        for (var i = 0; i < childRequests.Length; i++)
        {
            var child = childRequests[i];
            var childCompletion = new StepCompletedEvent
            {
                RunId = runId,
                StepId = child.StepId,
                ExecutionId = child.ExecutionId,
                Success = true,
                Output = $"output-{i}",
                OutputProvenance = WorkflowStepOutputProvenance.Produced,
            };
            var saveAttemptsBeforeKernel = host.SaveAttempts;

            await kernel.HandleAsync(Envelope(childCompletion), ctx, CancellationToken.None);

            host.SaveAttempts.Should().Be(saveAttemptsBeforeKernel + 1);
            await foreachBridge.HandleAsync(Envelope(childCompletion), ctx, CancellationToken.None);
            if (recreateForEachAfterFirstCompletion && i == 0)
                foreachBridge = new WorkflowExecutionBridgeModule([new ForEachModule()], host);
        }

        var parentCompletion = ctx.Published
            .Select(static publication => publication.Event)
            .Where(static payload => payload.Is(StepCompletedEvent.Descriptor))
            .Select(static payload => payload.Unpack<StepCompletedEvent>())
            .Should().ContainSingle(completion => completion.StepId == parentStepId)
            .Subject;
        parentCompletion.Success.Should().BeTrue();
        parentCompletion.Output.Should().Be("output-0\n---\noutput-1\n---\noutput-2");
        var completedForEachState = host.States[ForEachModule.ModuleStateKey]
            .Unpack<ForEachModuleState>();
        completedForEachState.CompletionTombstones.Should().ContainSingle();
        completedForEachState.CompletedChildOutputsRunId.Should().BeEmpty();
        completedForEachState.CompletedChildOutputs.Should().BeEmpty();

        var saveAttemptsBeforeParentCompletion = host.SaveAttempts;
        await kernel.HandleAsync(Envelope(parentCompletion), ctx, CancellationToken.None);

        host.SaveAttempts.Should().BeGreaterThan(saveAttemptsBeforeParentCompletion);
        var kernelState = host.States[WorkflowExecutionKernel.ModuleStateKey]
            .Unpack<WorkflowExecutionKernelState>();
        kernelState.CurrentStepId.Should().Be("done");
        WorkflowExecutionValueStore.ResolveCompletedStepOutput(kernelState, parentStepId)
            .Should().Be(parentCompletion.Output);
        foreach (var (child, index) in childRequests.Select(static (request, index) => (request, index)))
        {
            WorkflowExecutionValueStore.ResolveCompletedStepOutput(kernelState, child.StepId)
                .Should().Be($"output-{index}");
        }
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
        await DrainToolAttemptCompletionAsync(bridge, ctx);

        tool.ExecuteCalls.Should().Be(1);
        ctx.Published.Select(x => x.Event)
            .Where(x => x.Is(StepCompletedEvent.Descriptor))
            .Should().BeEmpty();
        var retry = ctx.Scheduled
            .Where(x => x.Event.Is(WorkflowToolCallPublicationRetryFiredEvent.Descriptor))
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
    public async Task Bridge_ShouldLetOriginalAttemptRedeliveryDrainPersistedApprovalSuspension()
    {
        var rejectApprovalPublication = true;
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
            FailPublish = evt => rejectApprovalPublication &&
                                 evt is WorkflowToolCallPublicationRetryFiredEvent or WorkflowSuspendedEvent,
            FailSchedule = evt => rejectApprovalPublication &&
                                  evt is WorkflowToolCallPublicationRetryFiredEvent,
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
        var publication = await ctx.WaitForPublishedAsync(static candidate =>
            candidate.Event.Is(WorkflowToolCallAttemptCompletedEvent.Descriptor));
        var completion = publication.Event.Unpack<WorkflowToolCallAttemptCompletedEvent>();
        var completionEnvelope = Envelope(completion);
        completionEnvelope.Route = EnvelopeRouteSemantics.CreateTopologyPublication(
            ctx.AgentId,
            TopologyAudience.Self);
        completionEnvelope.Runtime = new EnvelopeRuntime
        {
            DeliveryIdentity = new DeliveryIdentity
            {
                OperationId = publication.Options?.Delivery?.OperationId ?? string.Empty,
            },
        };

        await FluentActions.Awaiting(() =>
                bridge.HandleAsync(completionEnvelope, ctx, CancellationToken.None))
            .Should().ThrowAsync<WorkflowDurablePublicationPendingException>();

        tool.ExecuteCalls.Should().Be(1);
        var durable = host.States[ToolCallModule.ModuleStateKey].Unpack<ToolCallModuleState>();
        durable.PendingExecutions.Should().BeEmpty();
        durable.PendingApprovals.Values.Should().ContainSingle()
            .Which.SuspensionPublished.Should().BeFalse();

        rejectApprovalPublication = false;
        await bridge.HandleAsync(completionEnvelope, ctx, CancellationToken.None);

        tool.ExecuteCalls.Should().Be(1);
        ctx.Published.Select(static item => item.Event)
            .Where(static item => item.Is(WorkflowSuspendedEvent.Descriptor))
            .Should().ContainSingle();
        host.States[ToolCallModule.ModuleStateKey]
            .Unpack<ToolCallModuleState>()
            .PendingApprovals.Values.Should().ContainSingle()
            .Which.SuspensionPublished.Should().BeTrue();
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
        await DrainToolAttemptCompletionAsync(bridge, ctx);

        tool.ExecuteCalls.Should().Be(1);
        ctx.Published.Select(x => x.Event)
            .Where(x => x.Is(StepCompletedEvent.Descriptor))
            .Should().BeEmpty();
        var retry = ctx.Scheduled
            .Where(x => x.Event.Is(WorkflowToolCallPublicationRetryFiredEvent.Descriptor))
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
            FailSchedule = evt => evt is WorkflowToolCallPublicationRetryFiredEvent,
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
        await DrainToolAttemptCompletionAsync(bridge, ctx);

        tool.ExecuteCalls.Should().Be(1);
        ctx.ScheduleAttempts.Should().Be(2);
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

        var redelivery = await FluentActions.Awaiting(() =>
                bridge.HandleAsync(Envelope(retry), ctx, CancellationToken.None))
            .Should()
            .ThrowAsync<WorkflowRuntimeEnvelopeRetryablePublicationPendingException>();
        redelivery.Which.Should().BeAssignableTo<IRuntimeEnvelopeRetryableException>();

        tool.ExecuteCalls.Should().Be(1);
        ctx.ScheduleAttempts.Should().Be(3);
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
            FailSchedule = evt => evt is WorkflowToolCallPublicationRetryFiredEvent,
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
        await DrainToolAttemptCompletionAsync(bridge, ctx);

        tool.ExecuteCalls.Should().Be(1);
        ctx.ScheduleAttempts.Should().Be(2);
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

        var redelivery = await FluentActions.Awaiting(() =>
                bridge.HandleAsync(Envelope(retry), ctx, CancellationToken.None))
            .Should()
            .ThrowAsync<WorkflowRuntimeEnvelopeRetryablePublicationPendingException>();
        redelivery.Which.Should().BeAssignableTo<IRuntimeEnvelopeRetryableException>();

        tool.ExecuteCalls.Should().Be(1);
        ctx.ScheduleAttempts.Should().Be(3);
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
            FailSaveAttempt = 4,
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
        await DrainToolAttemptCompletionAsync(bridge, ctx);

        tool.ExecuteCalls.Should().Be(1);
        host.States[ToolCallModule.ModuleStateKey]
            .Unpack<ToolCallModuleState>()
            .Completions.Should().ContainSingle()
            .Which.ToolCompletionPublished.Should().BeFalse();
        var retry = ctx.Scheduled.Select(x => x.Event)
            .Where(x => x.Is(WorkflowToolCallPublicationRetryFiredEvent.Descriptor))
            .Select(x => x.Unpack<WorkflowToolCallPublicationRetryFiredEvent>())
            .Should().ContainSingle()
            .Subject;
        ctx.Published.Select(x => x.Event)
            .Where(x => x.Is(WorkflowToolCallCompletedEvent.Descriptor))
            .Should().ContainSingle();
        ctx.Published.Select(x => x.Event)
            .Where(x => x.Is(StepCompletedEvent.Descriptor))
            .Should().ContainSingle();

        await bridge.HandleAsync(Envelope(retry), ctx, CancellationToken.None);

        tool.ExecuteCalls.Should().Be(1);
        var toolPublications = ctx.Published
            .Where(x => x.Event.Is(WorkflowToolCallCompletedEvent.Descriptor))
            .ToList();
        var stepPublications = ctx.Published
            .Where(x => x.Event.Is(StepCompletedEvent.Descriptor))
            .ToList();
        toolPublications.Should().HaveCount(2);
        stepPublications.Should().HaveCount(2);
        toolPublications.Select(x => x.Options?.Delivery?.OperationId)
            .Distinct().Should().ContainSingle().Which.Should().NotBeNullOrWhiteSpace();
        stepPublications.Select(x => x.Options?.Delivery?.OperationId)
            .Distinct().Should().ContainSingle().Which.Should().NotBeNullOrWhiteSpace();
        stepPublications.Should().OnlyContain(x => x.Event.Unpack<StepCompletedEvent>().Success);
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
    public async Task Kernel_ShouldNotRetryOutcomeUncertainToolFailure()
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
                    Type = "tool_call",
                    Retry = new StepRetryPolicy
                    {
                        MaxAttempts = 3,
                        DelayMs = 0,
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
                Error = "tool 'proxy' execution failed: tool_outcome_unknown: external result cannot be proven",
                ExecutionId = firstRequest.ExecutionId,
                FailureOutcome = WorkflowStepFailureOutcome.OutcomeUncertain,
            }),
            ctx,
            CancellationToken.None);

        ctx.Published.Select(x => x.Event)
            .Should()
            .NotContain(x => x.Is(StepRequestEvent.Descriptor));
        ctx.Published.Select(x => x.Event)
            .Where(x => x.Is(WorkflowCompletedEvent.Descriptor))
            .Select(x => x.Unpack<WorkflowCompletedEvent>())
            .Should()
            .ContainSingle()
            .Which.Success.Should().BeFalse();
    }

    [Fact]
    public async Task Kernel_ShouldNotPublishTerminalFailure_WhenDurableCompletionIntentCannotBeSaved()
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

        await FluentActions.Awaiting(() => module.HandleAsync(
                Envelope(new StepCompletedEvent
                {
                    RunId = "run-1",
                    StepId = "step-1",
                    Success = true,
                    Output = "next",
                }),
                ctx,
                CancellationToken.None))
            .Should().ThrowAsync<InvalidOperationException>();

        ctx.Published
            .Select(x => x.Event)
            .Should().NotContain(x => x.Is(WorkflowCompletedEvent.Descriptor));
    }

    private static EventEnvelope Envelope(IMessage payload) => new()
    {
        Id = "envelope-1",
        Payload = Any.Pack(payload),
        Route = new EnvelopeRoute { PublisherActorId = "agent-1" },
    };

    private static ForEachModuleState CreateForEachAttemptState(
        string runId,
        string parentStepId,
        string parentExecutionId,
        string childStepId,
        string childExecutionId)
    {
        var state = new ForEachModuleState();
        var parent = new ForEachParentState
        {
            Expected = 1,
            ParentRunId = runId,
            ParentStepId = parentStepId,
            ParentExecutionId = parentExecutionId,
        };
        parent.ChildExecutionIds[childStepId] = childExecutionId;
        state.Parents[$"{runId}:{parentStepId}:execution:test"] = parent;
        return state;
    }

    private static async Task DrainToolAttemptCompletionAsync(
        WorkflowExecutionBridgeModule bridge,
        RecordingEventHandlerContext ctx)
    {
        var publication = await ctx.WaitForPublishedAsync(static candidate =>
            candidate.Event.Is(WorkflowToolCallAttemptCompletedEvent.Descriptor));
        var completion = publication.Event.Unpack<WorkflowToolCallAttemptCompletedEvent>();
        var envelope = Envelope(completion);
        envelope.Route = EnvelopeRouteSemantics.CreateTopologyPublication(ctx.AgentId, TopologyAudience.Self);
        envelope.Runtime = new EnvelopeRuntime
        {
            DeliveryIdentity = new DeliveryIdentity
            {
                OperationId = publication.Options?.Delivery?.OperationId ?? string.Empty,
            },
        };
        var failure = await FluentActions.Awaiting(() =>
                bridge.HandleAsync(envelope, ctx, CancellationToken.None))
            .Should()
            .ThrowAsync<WorkflowDurablePublicationPendingException>();
        failure.Which.Should().BeAssignableTo<IRuntimeEnvelopeRetryableException>();
    }

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
        private readonly Channel<RecordedPublication> _publishedEvents =
            Channel.CreateUnbounded<RecordedPublication>();

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

        public Func<IMessage, bool>? FailSchedule { get; init; }

        public int ScheduleAttempts { get; private set; }

        public List<RecordedPublication> Published { get; } = [];

        public List<RuntimeCallbackLease> Canceled { get; } = [];

        public List<(string CallbackId, Any Event)> Scheduled { get; } = [];

        public async Task<RecordedPublication> WaitForPublishedAsync(
            Func<RecordedPublication, bool> predicate)
        {
            while (true)
            {
                var publication = await _publishedEvents.Reader.ReadAsync().AsTask()
                    .WaitAsync(TimeSpan.FromSeconds(5));
                if (predicate(publication))
                    return publication;
            }
        }

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

            var publication = new RecordedPublication(Any.Pack(evt), direction, options?.DeepClone());
            Published.Add(publication);
            _publishedEvents.Writer.TryWrite(publication);
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
            if (FailSchedule?.Invoke(evt) == true)
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

    private sealed record RecordedPublication(
        Any Event,
        TopologyAudience Direction,
        EventEnvelopePublishOptions? Options);

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

    private sealed class OverlappingWorkflowTool(string name, int expectedExecutions) : IWorkflowTool
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly System.Collections.Concurrent.ConcurrentDictionary<int, TaskCompletionSource> _executionReleases = new();
        private readonly TaskCompletionSource _allExpectedExecutionsEntered =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _activeExecutions;
        private int _executeCalls;
        private int _maxActiveExecutions;

        public string Name { get; } = name;

        public int ExecuteCalls => Volatile.Read(ref _executeCalls);

        public int MaxActiveExecutions => Volatile.Read(ref _maxActiveExecutions);

        public Task AllExpectedExecutionsEntered => _allExpectedExecutionsEntered.Task;

        public async Task<WorkflowToolExecutionResult> ExecuteAsync(
            WorkflowToolExecutionRequest request,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            var executeCalls = Interlocked.Increment(ref _executeCalls);
            var active = Interlocked.Increment(ref _activeExecutions);
            UpdateMaxActive(active);
            if (executeCalls == expectedExecutions)
                _allExpectedExecutionsEntered.TrySetResult();

            try
            {
                var executionRelease = _executionReleases.GetOrAdd(
                    executeCalls,
                    static _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
                await Task.WhenAny(_release.Task, executionRelease.Task).WaitAsync(ct);
                return WorkflowToolExecutionResult.Success("{}");
            }
            finally
            {
                Interlocked.Decrement(ref _activeExecutions);
            }
        }

        public void ReleaseAll() => _release.TrySetResult();

        public void ReleaseExecution(int executeCall) =>
            _executionReleases.GetOrAdd(
                executeCall,
                static _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously))
                .TrySetResult();

        private void UpdateMaxActive(int candidate)
        {
            var observed = Volatile.Read(ref _maxActiveExecutions);
            while (candidate > observed)
            {
                var prior = Interlocked.CompareExchange(ref _maxActiveExecutions, candidate, observed);
                if (prior == observed)
                    return;
                observed = prior;
            }
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

    private sealed class RecordingStateHost : IWorkflowExecutionStateHost, IRuntimeSecretStoreAccessor
    {
        public string RunId { get; init; } = "run-1";

        public WorkflowExecutionRuntimeContext RuntimeContext { get; } = new();

        public IRuntimeSecretStore? RuntimeSecretStore { get; } = new InMemoryRuntimeSecretStore();

        public WorkflowRunExecutionContextState ExecutionContextSnapshot { get; } = new();

        public IRuntimeActorStateSchemaContextReader? RuntimeStateSchemaContextReader { get; init; }

        public IRuntimeFleetCapabilityAdmissionReader? RuntimeFleetCapabilityAdmissionReader { get; init; }

        public IRuntimeLocalMembershipIdentityReader? RuntimeLocalMembershipIdentityReader { get; init; }

        public TimeProvider? RuntimeFleetAdmissionTimeProvider { get; init; }

        public RuntimeActorStateMigrationAdmissionOptions? RuntimeFleetAdmissionOptions { get; init; }

        public bool FailSave { get; set; }

        public int? FailSaveAttempt { get; init; }

        public int SaveAttempts { get; private set; }

        public int ClearAttempts { get; private set; }

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
            ClearAttempts++;
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

    private static RecordingStateHost CreateNormalizedStateHost(string runId)
    {
        var now = new DateTimeOffset(2026, 8, 18, 2, 0, 0, TimeSpan.Zero);
        var receipt = new RuntimeActorStateSchemaAdoptionReceipt
        {
            StateSchemaVersion = 1,
            RequiredCapability = RuntimeFleetCapability.WorkflowNormalizedStateWritesV1,
            RequiredContractId = WorkflowNormalizedStateWriteAdmission.ContractId,
            RequiredContractVersion = WorkflowNormalizedStateWriteAdmission.RequiredReaderContractVersion,
            CapabilityEpoch = 3,
            AuthorityStateVersion = 9,
            MembershipEpoch = 7,
            DeploymentRevision = "revision-a",
            AdoptedAt = Timestamp.FromDateTimeOffset(now),
            AuthorityActorId = RuntimeFleetCapabilityAuthorityIdentity.ActorId,
            MembershipDigest = "digest-a",
        };
        var admission = new RuntimeFleetCapabilityAdmission
        {
            Capability = RuntimeFleetCapability.WorkflowNormalizedStateWritesV1,
            Status = RuntimeFleetCapabilityGateStatus.Open,
            AuthorityActorId = RuntimeFleetCapabilityAuthorityIdentity.ActorId,
            AuthorityStateVersion = 9,
            CapabilityEpoch = 3,
            MembershipEpoch = 7,
            DeploymentRevision = "revision-a",
            MinimumReaderContractVersion = WorkflowNormalizedStateWriteAdmission.RequiredReaderContractVersion,
            MembershipObservedAt = Timestamp.FromDateTimeOffset(now.AddSeconds(-5)),
            MembershipValidUntil = Timestamp.FromDateTimeOffset(now.AddMinutes(1)),
            ActiveMemberCount = 1,
            ConfirmedMemberCount = 1,
            MembershipDigest = "digest-a",
            ContractId = WorkflowNormalizedStateWriteAdmission.ContractId,
        };
        admission.AdmittedMembers.Add(new RuntimeFleetAdmittedMember
        {
            MemberId = "member-a",
            Incarnation = "inc-a",
        });

        return new RecordingStateHost
        {
            RunId = runId,
            RuntimeStateSchemaContextReader = new FixedSchemaContextAccessor(
                new RuntimeActorStateSchemaContext("workflow.run", 1, [receipt])),
            RuntimeFleetCapabilityAdmissionReader = new FixedAdmissionReader(admission),
            RuntimeLocalMembershipIdentityReader = new FixedMembershipReader(new RuntimeLocalMembershipIdentity(
                7,
                "digest-a",
                "revision-a",
                "member-a",
                "inc-a")),
            RuntimeFleetAdmissionTimeProvider = new FixedTimeProvider(now),
            RuntimeFleetAdmissionOptions = new RuntimeActorStateMigrationAdmissionOptions(),
        };
    }

    private sealed class FixedSchemaContextAccessor(RuntimeActorStateSchemaContext current)
        : IRuntimeActorStateSchemaContextReader
    {
        public RuntimeActorStateSchemaContext? Current { get; } = current;

        public IDisposable Bind(RuntimeActorIdentity identity) =>
            throw new NotSupportedException("The fixed test accessor cannot be rebound.");
    }

    private sealed class FixedAdmissionReader(RuntimeFleetCapabilityAdmission admission)
        : IRuntimeFleetCapabilityAdmissionReader
    {
        public Task<RuntimeFleetCapabilityAdmission?> GetAsync(
            RuntimeFleetCapability capability,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult<RuntimeFleetCapabilityAdmission?>(admission.Clone());
        }
    }

    private sealed class FixedMembershipReader(RuntimeLocalMembershipIdentity membership)
        : IRuntimeLocalMembershipIdentityReader
    {
        public ValueTask<RuntimeLocalMembershipIdentity?> GetCurrentAsync(CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return ValueTask.FromResult<RuntimeLocalMembershipIdentity?>(membership);
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class NullServiceProvider : IServiceProvider
    {
        private readonly IRuntimeSecretStore _runtimeSecretStore = new InMemoryRuntimeSecretStore();

        public object? GetService(System.Type serviceType) =>
            serviceType == typeof(IRuntimeSecretStore) ? _runtimeSecretStore : null;
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
