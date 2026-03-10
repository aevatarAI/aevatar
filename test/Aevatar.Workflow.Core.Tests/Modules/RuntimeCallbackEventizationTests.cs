using Aevatar.AI.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.EventModules;
using Aevatar.Foundation.Abstractions.Runtime.Callbacks;
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
using System.Globalization;

namespace Aevatar.Workflow.Core.Tests.Modules;

public class RuntimeCallbackEventizationTests
{
    [Fact]
    public async Task DelayModule_ShouldCompleteOnlyOnMatchingGeneration()
    {
        var module = new DelayModule();
        var ctx = new SchedulingContext();

        await module.HandleAsync(
            Wrap(new StepRequestEvent
            {
                StepId = "delay-step",
                StepType = "delay",
                RunId = "run-1",
                Input = "payload",
                Parameters = { ["duration_ms"] = "1200" },
            }),
            ctx,
            CancellationToken.None);

        ctx.Scheduled.Should().ContainSingle();
        ctx.Published.Should().NotContain(x => x.Event is StepCompletedEvent);
        var scheduled = ctx.Scheduled.Single();

        await module.HandleAsync(
            Wrap(
                new DelayStepTimeoutFiredEvent
                {
                    RunId = "run-1",
                    StepId = "delay-step",
                    DurationMs = 1200,
                },
                MetadataFor(scheduled with { CallbackId = "other-callback" })),
            ctx,
            CancellationToken.None);

        ctx.Published.Should().NotContain(x => x.Event is StepCompletedEvent);

        await module.HandleAsync(
            Wrap(
                new DelayStepTimeoutFiredEvent
                {
                    RunId = "run-1",
                    StepId = "delay-step",
                    DurationMs = 1200,
                },
                MetadataFor(scheduled, generation: scheduled.Generation - 1)),
            ctx,
            CancellationToken.None);

        ctx.Published.Should().NotContain(x => x.Event is StepCompletedEvent);

        await module.HandleAsync(
            Wrap(
                new DelayStepTimeoutFiredEvent
                {
                    RunId = "run-1",
                    StepId = "delay-step",
                    DurationMs = 1200,
                },
                MetadataFor(scheduled)),
            ctx,
            CancellationToken.None);

        var completed = ctx.Published
            .Select(x => x.Event)
            .OfType<StepCompletedEvent>()
            .Single();
        completed.Success.Should().BeTrue();
        completed.RunId.Should().Be("run-1");
        completed.StepId.Should().Be("delay-step");
        completed.Output.Should().Be("payload");
    }

    [Fact]
    public async Task DelayModule_ShouldAcceptCallbackIdFallback_WhenLeaseIsMissing()
    {
        var module = new DelayModule();
        var ctx = new SchedulingContext();

        await module.HandleAsync(
            Wrap(new StepRequestEvent
            {
                StepId = "delay-step",
                StepType = "delay",
                RunId = "run-delay-fallback",
                Input = "payload",
                Parameters = { ["duration_ms"] = "1200" },
            }),
            ctx,
            CancellationToken.None);

        var scheduled = ctx.Scheduled.Single();
        var state = ctx.LoadState<DelayModuleState>("delay");
        state.Pending["run-delay-fallback:delay-step"].Lease = null;
        await ctx.SaveStateAsync("delay", state, CancellationToken.None);
        ctx.Published.Clear();

        await module.HandleAsync(
            Wrap(
                new DelayStepTimeoutFiredEvent
                {
                    RunId = "run-delay-fallback",
                    StepId = "delay-step",
                    DurationMs = 1200,
                },
                MetadataFor(scheduled)),
            ctx,
            CancellationToken.None);

        var completion = ctx.Published
            .Select(x => x.Event)
            .OfType<StepCompletedEvent>()
            .Single();
        completion.Success.Should().BeTrue();
        completion.Output.Should().Be("payload");
    }

    [Fact]
    public async Task DelayModule_ShouldReplayTimeoutCompletion_WhenPublishFailsTransiently()
    {
        var module = new DelayModule();
        var ctx = new SchedulingContext
        {
            FailPublishOnce = evt => evt is StepCompletedEvent completed &&
                                     string.Equals(completed.StepId, "delay-step", StringComparison.Ordinal) &&
                                     completed.Success,
        };

        await module.HandleAsync(
            Wrap(new StepRequestEvent
            {
                StepId = "delay-step",
                StepType = "delay",
                RunId = "run-delay-replay",
                Input = "payload",
                Parameters = { ["duration_ms"] = "1200" },
            }),
            ctx,
            CancellationToken.None);

        var scheduled = ctx.Scheduled.Single();
        ctx.Published.Clear();
        var timeoutEnvelope = Wrap(
            new DelayStepTimeoutFiredEvent
            {
                RunId = "run-delay-replay",
                StepId = "delay-step",
                DurationMs = 1200,
            },
            MetadataFor(scheduled));

        await FluentActions
            .Invoking(() => module.HandleAsync(timeoutEnvelope, ctx, CancellationToken.None))
            .Should()
            .ThrowAsync<InvalidOperationException>()
            .WithMessage("transient publish failure");

        ctx.LoadState<DelayModuleState>("delay").Pending.Should().ContainKey("run-delay-replay:delay-step");

        await module.HandleAsync(timeoutEnvelope, ctx, CancellationToken.None);

        var completion = ctx.Published
            .Select(x => x.Event)
            .OfType<StepCompletedEvent>()
            .Single();
        completion.Success.Should().BeTrue();
        completion.Output.Should().Be("payload");
    }

    [Fact]
    public async Task WaitSignalModule_ShouldIgnoreStaleTimeoutWithoutDroppingPending()
    {
        var module = new WaitSignalModule();
        var ctx = new SchedulingContext();

        await module.HandleAsync(
            Wrap(new StepRequestEvent
            {
                StepId = "wait-1",
                StepType = "wait_signal",
                RunId = "run-1",
                Input = "default-output",
                Parameters =
                {
                    ["signal_name"] = "approve",
                    ["timeout_ms"] = "5000",
                },
            }),
            ctx,
            CancellationToken.None);

        var scheduled = ctx.Scheduled.Single(x => x.Event is WaitSignalTimeoutFiredEvent);
        ctx.Published.Clear();

        await module.HandleAsync(
            Wrap(
                new WaitSignalTimeoutFiredEvent
                {
                    RunId = "run-1",
                    StepId = "wait-1",
                    SignalName = "approve",
                    TimeoutMs = 5000,
                },
                MetadataFor(scheduled, generation: scheduled.Generation - 1)),
            ctx,
            CancellationToken.None);

        ctx.Published.Should().NotContain(x => x.Event is StepCompletedEvent);

        await module.HandleAsync(
            Wrap(new SignalReceivedEvent
            {
                RunId = "run-1",
                StepId = "wait-1",
                SignalName = "approve",
                Payload = "approved",
            }),
            ctx,
            CancellationToken.None);

        var completion = ctx.Published
            .Select(x => x.Event)
            .OfType<StepCompletedEvent>()
            .Single();
        completion.Success.Should().BeTrue();
        completion.Output.Should().Be("approved");

        ctx.Canceled.Should().ContainSingle(x =>
            x.CallbackId == scheduled.CallbackId &&
            x.ExpectedGeneration == scheduled.Generation);
    }

    [Fact]
    public async Task WaitSignalModule_ShouldAcceptCallbackIdFallback_WhenLeaseIsMissing()
    {
        var module = new WaitSignalModule();
        var ctx = new SchedulingContext();

        await module.HandleAsync(
            Wrap(new StepRequestEvent
            {
                StepId = "wait-1",
                StepType = "wait_signal",
                RunId = "run-wait-fallback",
                Input = "default-output",
                Parameters =
                {
                    ["signal_name"] = "approve",
                    ["timeout_ms"] = "5000",
                },
            }),
            ctx,
            CancellationToken.None);

        var scheduled = ctx.Scheduled.Single(x => x.Event is WaitSignalTimeoutFiredEvent);
        var state = ctx.LoadState<WaitSignalModuleState>("wait_signal");
        state.Pending["run-wait-fallback:approve:wait-1"].TimeoutLease = null;
        await ctx.SaveStateAsync("wait_signal", state, CancellationToken.None);
        ctx.Published.Clear();

        await module.HandleAsync(
            Wrap(
                new WaitSignalTimeoutFiredEvent
                {
                    RunId = "run-wait-fallback",
                    StepId = "wait-1",
                    SignalName = "approve",
                    TimeoutMs = 5000,
                },
                MetadataFor(scheduled)),
            ctx,
            CancellationToken.None);

        var completion = ctx.Published
            .Select(x => x.Event)
            .OfType<StepCompletedEvent>()
            .Single();
        completion.Success.Should().BeFalse();
        completion.Error.Should().Contain("timed out");
    }

    [Fact]
    public async Task WorkflowLoop_ShouldReplayTimeoutCompletion_WhenPublishFailsTransiently()
    {
        var ctx = new SchedulingContext
        {
            FailPublishOnce = evt => evt is StepCompletedEvent completed &&
                                     string.Equals(completed.StepId, "step-1", StringComparison.Ordinal) &&
                                     completed.Error.Contains("TIMEOUT", StringComparison.OrdinalIgnoreCase),
        };
        var module = CreateKernel(new WorkflowDefinition
        {
            Name = "wf",
            Roles = [],
            Steps =
            [
                new StepDefinition
                {
                    Id = "step-1",
                    Type = "llm_call",
                    TimeoutMs = 800,
                },
            ],
        }, ctx);

        await module.HandleAsync(
            Wrap(new StartWorkflowEvent
            {
                WorkflowName = "wf",
                RunId = "run-timeout-replay",
                Input = "input-v1",
            }),
            ctx,
            CancellationToken.None);
        var scheduled = ctx.Scheduled.Single(x => x.Event is WorkflowStepTimeoutFiredEvent);
        ctx.Published.Clear();

        var timeoutEnvelope = Wrap(
            new WorkflowStepTimeoutFiredEvent
            {
                RunId = "run-timeout-replay",
                StepId = "step-1",
                TimeoutMs = 800,
            },
            MetadataFor(scheduled));

        await FluentActions
            .Invoking(() => module.HandleAsync(timeoutEnvelope, ctx, CancellationToken.None))
            .Should()
            .ThrowAsync<InvalidOperationException>()
            .WithMessage("transient publish failure");

        ctx.Published.Should().BeEmpty();

        await module.HandleAsync(timeoutEnvelope, ctx, CancellationToken.None);

        var completion = ctx.Published
            .Select(x => x.Event)
            .OfType<StepCompletedEvent>()
            .Single();
        completion.Success.Should().BeFalse();
        completion.Error.Should().Contain("TIMEOUT");
    }

    [Fact]
    public async Task WorkflowLoop_ShouldResumeInitialDispatch_WhenStartPublishFailsTransiently()
    {
        var ctx = new SchedulingContext
        {
            FailPublishOnce = evt => evt is StepRequestEvent request &&
                                     string.Equals(request.StepId, "step-1", StringComparison.Ordinal),
        };
        var module = CreateKernel(new WorkflowDefinition
        {
            Name = "wf",
            Roles = [],
            Steps =
            [
                new StepDefinition
                {
                    Id = "step-1",
                    Type = "transform",
                },
            ],
        }, ctx);

        var startEnvelope = Wrap(new StartWorkflowEvent
        {
            WorkflowName = "wf",
            RunId = "run-start-replay",
            Input = "input-v1",
        });

        await FluentActions
            .Invoking(() => module.HandleAsync(startEnvelope, ctx, CancellationToken.None))
            .Should()
            .ThrowAsync<InvalidOperationException>()
            .WithMessage("transient publish failure");

        var pendingState = ctx.LoadState<WorkflowExecutionKernelState>("workflow_execution_kernel");
        pendingState.Active.Should().BeTrue();
        pendingState.CurrentStepDispatchPending.Should().BeTrue();
        pendingState.CurrentStepId.Should().Be("step-1");

        await module.HandleAsync(startEnvelope, ctx, CancellationToken.None);

        var retryStepRequest = ctx.Published
            .Select(x => x.Event)
            .OfType<StepRequestEvent>()
            .Single();
        retryStepRequest.RunId.Should().Be("run-start-replay");
        retryStepRequest.StepId.Should().Be("step-1");
        retryStepRequest.Input.Should().Be("input-v1");
    }

    [Fact]
    public async Task WorkflowLoop_ShouldScheduleRetryBackoffAndDispatchOnMatchingGeneration()
    {
        var ctx = new SchedulingContext();
        var module = CreateKernel(new WorkflowDefinition
        {
            Name = "wf",
            Roles = [],
            Steps =
            [
                new StepDefinition
                {
                    Id = "step-1",
                    Type = "transform",
                    Retry = new StepRetryPolicy
                    {
                        MaxAttempts = 3,
                        Backoff = "fixed",
                        DelayMs = 800,
                    },
                },
            ],
        }, ctx);

        await module.HandleAsync(
            Wrap(new StartWorkflowEvent
            {
                WorkflowName = "wf",
                RunId = "run-1",
                Input = "input-v1",
            }),
            ctx,
            CancellationToken.None);
        ctx.Published.Clear();
        ctx.Scheduled.Clear();

        await module.HandleAsync(
            Wrap(new StepCompletedEvent
            {
                StepId = "step-1",
                RunId = "run-1",
                Success = false,
                Error = "boom",
            }),
            ctx,
            CancellationToken.None);

        ctx.Published.Should().NotContain(x => x.Event is StepRequestEvent);
        var scheduled = ctx.Scheduled.Single(x => x.Event is WorkflowStepRetryBackoffFiredEvent);
        (scheduled.Event as WorkflowStepRetryBackoffFiredEvent)!.NextAttempt.Should().Be(2);

        await module.HandleAsync(
            Wrap(
                new WorkflowStepRetryBackoffFiredEvent
                {
                    RunId = "run-1",
                    StepId = "step-1",
                    DelayMs = 800,
                    NextAttempt = 2,
                },
                MetadataFor(scheduled, generation: scheduled.Generation - 1)),
            ctx,
            CancellationToken.None);

        ctx.Published.Should().NotContain(x => x.Event is StepRequestEvent);

        await module.HandleAsync(
            Wrap(
                new WorkflowStepRetryBackoffFiredEvent
                {
                    RunId = "run-1",
                    StepId = "step-1",
                    DelayMs = 800,
                    NextAttempt = 2,
                },
                MetadataFor(scheduled)),
            ctx,
            CancellationToken.None);

        var retryStepRequest = ctx.Published
            .Select(x => x.Event)
            .OfType<StepRequestEvent>()
            .Single();
        retryStepRequest.RunId.Should().Be("run-1");
        retryStepRequest.StepId.Should().Be("step-1");
        retryStepRequest.Input.Should().Be("input-v1");
    }

    [Fact]
    public async Task WorkflowLoop_ShouldConsumeRetryBackoffReplay_AfterRedispatchAlreadyCommitted()
    {
        var ctx = new SchedulingContext();
        var module = CreateKernel(new WorkflowDefinition
        {
            Name = "wf",
            Roles = [],
            Steps =
            [
                new StepDefinition
                {
                    Id = "step-1",
                    Type = "transform",
                    Retry = new StepRetryPolicy
                    {
                        MaxAttempts = 3,
                        Backoff = "fixed",
                        DelayMs = 800,
                    },
                },
            ],
        }, ctx);

        await module.HandleAsync(
            Wrap(new StartWorkflowEvent
            {
                WorkflowName = "wf",
                RunId = "run-retry-cleanup",
                Input = "input-v1",
            }),
            ctx,
            CancellationToken.None);
        ctx.Published.Clear();
        ctx.Scheduled.Clear();

        await module.HandleAsync(
            Wrap(new StepCompletedEvent
            {
                StepId = "step-1",
                RunId = "run-retry-cleanup",
                Success = false,
                Error = "boom",
            }),
            ctx,
            CancellationToken.None);

        var scheduled = ctx.Scheduled.Single(x => x.Event is WorkflowStepRetryBackoffFiredEvent);
        var state = ctx.LoadState<WorkflowExecutionKernelState>("workflow_execution_kernel");
        state.RetryBackoffsByStepId["step-1"].DispatchPending = true;
        await ctx.SaveStateAsync("workflow_execution_kernel", state, CancellationToken.None);
        ctx.Published.Clear();

        await module.HandleAsync(
            Wrap(
                new WorkflowStepRetryBackoffFiredEvent
                {
                    RunId = "run-retry-cleanup",
                    StepId = "step-1",
                    DelayMs = 800,
                    NextAttempt = 2,
                },
                MetadataFor(scheduled)),
            ctx,
            CancellationToken.None);

        ctx.Published.Should().NotContain(x => x.Event is StepRequestEvent);
        ctx.LoadState<WorkflowExecutionKernelState>("workflow_execution_kernel")
            .RetryBackoffsByStepId.Should().NotContainKey("step-1");
    }

    [Fact]
    public async Task WorkflowLoop_ShouldReplayRetryBackoff_WhenRedispatchFailsTransiently()
    {
        var ctx = new SchedulingContext();
        var module = CreateKernel(new WorkflowDefinition
        {
            Name = "wf",
            Roles = [],
            Steps =
            [
                new StepDefinition
                {
                    Id = "step-1",
                    Type = "transform",
                    TimeoutMs = 1500,
                    Retry = new StepRetryPolicy
                    {
                        MaxAttempts = 3,
                        Backoff = "fixed",
                        DelayMs = 800,
                    },
                },
            ],
        }, ctx);

        await module.HandleAsync(
            Wrap(new StartWorkflowEvent
            {
                WorkflowName = "wf",
                RunId = "run-retry-replay",
                Input = "input-v1",
            }),
            ctx,
            CancellationToken.None);
        ctx.Published.Clear();
        ctx.Scheduled.Clear();

        await module.HandleAsync(
            Wrap(new StepCompletedEvent
            {
                StepId = "step-1",
                RunId = "run-retry-replay",
                Success = false,
                Error = "boom",
            }),
            ctx,
            CancellationToken.None);

        var scheduled = ctx.Scheduled.Single(x => x.Event is WorkflowStepRetryBackoffFiredEvent);
        ctx.Published.Clear();
        ctx.Canceled.Clear();
        ctx.FailPublishOnce = evt => evt is StepRequestEvent request &&
                                     string.Equals(request.StepId, "step-1", StringComparison.Ordinal);

        var backoffEnvelope = Wrap(
            new WorkflowStepRetryBackoffFiredEvent
            {
                RunId = "run-retry-replay",
                StepId = "step-1",
                DelayMs = 800,
                NextAttempt = 2,
            },
            MetadataFor(scheduled));

        await FluentActions
            .Invoking(() => module.HandleAsync(backoffEnvelope, ctx, CancellationToken.None))
            .Should()
            .ThrowAsync<InvalidOperationException>()
            .WithMessage("transient publish failure");

        ctx.Published.Should().BeEmpty();
        ctx.Canceled.Should().ContainSingle(x =>
            x.CallbackId.StartsWith("workflow-step-timeout:run-retry-replay:step-1:", StringComparison.Ordinal) &&
            x.ExpectedGeneration == 2);

        await module.HandleAsync(backoffEnvelope, ctx, CancellationToken.None);

        var retryStepRequest = ctx.Published
            .Select(x => x.Event)
            .OfType<StepRequestEvent>()
            .Single();
        retryStepRequest.RunId.Should().Be("run-retry-replay");
        retryStepRequest.StepId.Should().Be("step-1");
        retryStepRequest.Input.Should().Be("input-v1");
    }

    [Fact]
    public async Task LlmCallModule_ShouldReplayCompletion_WhenPublishFailsTransiently()
    {
        var module = new LLMCallModule();
        var ctx = new SchedulingContext
        {
            FailPublishOnce = evt => evt is StepCompletedEvent completed &&
                                     string.Equals(completed.StepId, "step-1", StringComparison.Ordinal) &&
                                     completed.Success,
        };

        await module.HandleAsync(
            Wrap(new StepRequestEvent
            {
                StepId = "step-1",
                StepType = "llm_call",
                RunId = "run-llm-replay",
                Input = "prompt",
                Parameters = { ["timeout_ms"] = "5000" },
            }),
            ctx,
            CancellationToken.None);

        var chatRequest = ctx.Published.Select(x => x.Event).OfType<ChatRequestEvent>().Single();
        ctx.Published.Clear();
        var responseEnvelope = Wrap(new ChatResponseEvent
        {
            SessionId = chatRequest.SessionId,
            Content = "ok",
        });

        await FluentActions
            .Invoking(() => module.HandleAsync(responseEnvelope, ctx, CancellationToken.None))
            .Should()
            .ThrowAsync<InvalidOperationException>()
            .WithMessage("transient publish failure");

        ctx.LoadState<LLMCallModuleState>("llm_call")
            .PendingBySessionId.Should().ContainKey(chatRequest.SessionId);

        await module.HandleAsync(responseEnvelope, ctx, CancellationToken.None);

        var completion = ctx.Published
            .Select(x => x.Event)
            .OfType<StepCompletedEvent>()
            .Single();
        completion.Success.Should().BeTrue();
        completion.Output.Should().Be("ok");
    }

    [Fact]
    public async Task WorkflowLoop_ShouldRejectStartWhenRunAlreadyActive()
    {
        var ctx = new SchedulingContext();
        var module = CreateKernel(new WorkflowDefinition
        {
            Name = "wf",
            Roles = [],
            Steps =
            [
                new StepDefinition
                {
                    Id = "step-1",
                    Type = "transform",
                },
            ],
        }, ctx);

        await module.HandleAsync(
            Wrap(new StartWorkflowEvent
            {
                WorkflowName = "wf",
                RunId = "run-active",
                Input = "first",
            }),
            ctx,
            CancellationToken.None);
        ctx.Published.Clear();

        await module.HandleAsync(
            Wrap(new StartWorkflowEvent
            {
                WorkflowName = "wf",
                RunId = "run-active",
                Input = "second",
            }),
            ctx,
            CancellationToken.None);

        var completion = ctx.Published.Select(x => x.Event).OfType<WorkflowCompletedEvent>().Single();
        completion.Success.Should().BeFalse();
        completion.Error.Should().Contain("already active");
    }

    [Fact]
    public async Task WorkflowLoop_ShouldFailWhenWorkflowHasNoSteps()
    {
        var ctx = new SchedulingContext();
        var module = CreateKernel(new WorkflowDefinition
        {
            Name = "wf-empty",
            Roles = [],
            Steps = [],
        }, ctx);

        await module.HandleAsync(
            Wrap(new StartWorkflowEvent
            {
                WorkflowName = "wf-empty",
                RunId = "run-empty",
                Input = "input",
            }),
            ctx,
            CancellationToken.None);

        var completion = ctx.Published.Select(x => x.Event).OfType<WorkflowCompletedEvent>().Single();
        completion.Success.Should().BeFalse();
        completion.Error.Should().Contain("无步骤");
        ctx.LoadState<WorkflowExecutionKernelState>("workflow_execution_kernel").Active.Should().BeFalse();
    }

    [Fact]
    public async Task WorkflowLoop_ShouldCompleteSuccessfullyWhenOnErrorSkipHasNoNextStep()
    {
        var ctx = new SchedulingContext();
        var module = CreateKernel(new WorkflowDefinition
        {
            Name = "wf-skip",
            Roles = [],
            Steps =
            [
                new StepDefinition
                {
                    Id = "step-1",
                    Type = "transform",
                    OnError = new StepErrorPolicy
                    {
                        Strategy = "skip",
                        DefaultOutput = "skipped-output",
                    },
                },
            ],
        }, ctx);

        await module.HandleAsync(
            Wrap(new StartWorkflowEvent
            {
                WorkflowName = "wf-skip",
                RunId = "run-skip",
                Input = "input",
            }),
            ctx,
            CancellationToken.None);
        ctx.Published.Clear();

        await module.HandleAsync(
            Wrap(new StepCompletedEvent
            {
                StepId = "step-1",
                RunId = "run-skip",
                Success = false,
                Error = "boom",
            }),
            ctx,
            CancellationToken.None);

        var completion = ctx.Published.Select(x => x.Event).OfType<WorkflowCompletedEvent>().Single();
        completion.Success.Should().BeTrue();
        completion.Output.Should().Be("skipped-output");
    }

    [Fact]
    public async Task WorkflowLoop_ShouldDispatchFallbackStepWhenOnErrorFallbackConfigured()
    {
        var ctx = new SchedulingContext();
        var module = CreateKernel(new WorkflowDefinition
        {
            Name = "wf-fallback",
            Roles = [],
            Steps =
            [
                new StepDefinition
                {
                    Id = "step-1",
                    Type = "transform",
                    OnError = new StepErrorPolicy
                    {
                        Strategy = "fallback",
                        FallbackStep = "step-2",
                    },
                },
                new StepDefinition
                {
                    Id = "step-2",
                    Type = "transform",
                },
            ],
        }, ctx);

        await module.HandleAsync(
            Wrap(new StartWorkflowEvent
            {
                WorkflowName = "wf-fallback",
                RunId = "run-fallback",
                Input = "input",
            }),
            ctx,
            CancellationToken.None);
        ctx.Published.Clear();

        await module.HandleAsync(
            Wrap(new StepCompletedEvent
            {
                StepId = "step-1",
                RunId = "run-fallback",
                Success = false,
                Error = "boom",
                Output = "fallback-input",
            }),
            ctx,
            CancellationToken.None);

        var fallbackRequest = ctx.Published.Select(x => x.Event).OfType<StepRequestEvent>().Single();
        fallbackRequest.StepId.Should().Be("step-2");
        fallbackRequest.Input.Should().Be("fallback-input");
    }

    [Fact]
    public async Task WorkflowLoop_ShouldFailWhenDirectNextStepIsInvalid()
    {
        var ctx = new SchedulingContext();
        var module = CreateKernel(new WorkflowDefinition
        {
            Name = "wf-next",
            Roles = [],
            Steps =
            [
                new StepDefinition
                {
                    Id = "step-1",
                    Type = "transform",
                },
                new StepDefinition
                {
                    Id = "step-2",
                    Type = "transform",
                },
            ],
        }, ctx);

        await module.HandleAsync(
            Wrap(new StartWorkflowEvent
            {
                WorkflowName = "wf-next",
                RunId = "run-next",
                Input = "input",
            }),
            ctx,
            CancellationToken.None);
        ctx.Published.Clear();

        await module.HandleAsync(
            Wrap(new StepCompletedEvent
            {
                StepId = "step-1",
                RunId = "run-next",
                Success = true,
                Output = "done",
                Metadata = { ["next_step"] = "missing-step" },
            }),
            ctx,
            CancellationToken.None);

        var completion = ctx.Published.Select(x => x.Event).OfType<WorkflowCompletedEvent>().Single();
        completion.Success.Should().BeFalse();
        completion.Error.Should().Contain("invalid next_step");
    }

    [Fact]
    public async Task WorkflowLoop_ShouldRedispatchImmediatelyWhenRetryDelayIsZero()
    {
        var ctx = new SchedulingContext();
        var module = CreateKernel(new WorkflowDefinition
        {
            Name = "wf-retry-immediate",
            Roles = [],
            Steps =
            [
                new StepDefinition
                {
                    Id = "step-1",
                    Type = "transform",
                    Retry = new StepRetryPolicy
                    {
                        MaxAttempts = 3,
                        Backoff = "fixed",
                        DelayMs = 0,
                    },
                },
            ],
        }, ctx);

        await module.HandleAsync(
            Wrap(new StartWorkflowEvent
            {
                WorkflowName = "wf-retry-immediate",
                RunId = "run-retry-immediate",
                Input = "input",
            }),
            ctx,
            CancellationToken.None);
        ctx.Published.Clear();

        await module.HandleAsync(
            Wrap(new StepCompletedEvent
            {
                StepId = "step-1",
                RunId = "run-retry-immediate",
                Success = false,
                Error = "boom",
            }),
            ctx,
            CancellationToken.None);

        var retryRequest = ctx.Published.Select(x => x.Event).OfType<StepRequestEvent>().Single();
        retryRequest.StepId.Should().Be("step-1");
        retryRequest.Input.Should().Be("input");
        ctx.Scheduled.Should().BeEmpty();
    }

    [Fact]
    public async Task WorkflowLoop_ShouldFailTimeoutWithoutRetrying()
    {
        var ctx = new SchedulingContext();
        var module = CreateKernel(new WorkflowDefinition
        {
            Name = "wf-timeout",
            Roles = [],
            Steps =
            [
                new StepDefinition
                {
                    Id = "step-1",
                    Type = "transform",
                    Retry = new StepRetryPolicy
                    {
                        MaxAttempts = 3,
                        DelayMs = 100,
                    },
                },
            ],
        }, ctx);

        await module.HandleAsync(
            Wrap(new StartWorkflowEvent
            {
                WorkflowName = "wf-timeout",
                RunId = "run-timeout",
                Input = "input",
            }),
            ctx,
            CancellationToken.None);
        ctx.Published.Clear();
        ctx.Scheduled.Clear();

        await module.HandleAsync(
            Wrap(new StepCompletedEvent
            {
                StepId = "step-1",
                RunId = "run-timeout",
                Success = false,
                Error = "TIMEOUT after 100ms",
            }),
            ctx,
            CancellationToken.None);

        var completion = ctx.Published.Select(x => x.Event).OfType<WorkflowCompletedEvent>().Single();
        completion.Success.Should().BeFalse();
        completion.Error.Should().Contain("TIMEOUT");
        ctx.Scheduled.Should().BeEmpty();
    }

    private static EventEnvelope Wrap(
        IMessage evt,
        IReadOnlyDictionary<string, string>? metadata = null)
    {
        var envelope = new EventEnvelope
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
            Payload = Any.Pack(evt),
            PublisherId = "test",
            Direction = EventDirection.Self,
        };

        if (metadata != null)
        {
            foreach (var pair in metadata)
                envelope.Metadata[pair.Key] = pair.Value;
        }

        return envelope;
    }

    private static IReadOnlyDictionary<string, string> MetadataFor(
        ScheduledCallback callback,
        long? generation = null,
        long fireIndex = 0) =>
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [RuntimeCallbackMetadataKeys.CallbackId] = callback.CallbackId,
            [RuntimeCallbackMetadataKeys.CallbackGeneration] = (generation ?? callback.Generation)
                .ToString(CultureInfo.InvariantCulture),
            [RuntimeCallbackMetadataKeys.CallbackFireIndex] = fireIndex.ToString(CultureInfo.InvariantCulture),
            [RuntimeCallbackMetadataKeys.CallbackFiredAtUnixTimeMs] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                .ToString(CultureInfo.InvariantCulture),
        };

    private static WorkflowExecutionKernel CreateKernel(
        WorkflowDefinition workflow,
        SchedulingContext ctx) =>
        new(workflow, (IWorkflowExecutionStateHost)ctx.Agent);

    private sealed class SchedulingContext : IEventHandlerContext, IWorkflowExecutionContext
    {
        private readonly Dictionary<string, long> _generations = new(StringComparer.Ordinal);
        private bool _publishFailureConsumed;

        public EventEnvelope InboundEnvelope { get; } = new()
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
        };

        public string AgentId => "agent-1";

        public IAgent Agent { get; } = new StubWorkflowRunAgent("agent-1", "test-run");

        public IServiceProvider Services { get; } = new NullServiceProvider();

        public ILogger Logger { get; } = NullLogger.Instance;

        public string RunId => ((IWorkflowExecutionStateHost)Agent).RunId;

        public List<(IMessage Event, EventDirection Direction)> Published { get; } = [];

        public List<ScheduledCallback> Scheduled { get; } = [];

        public List<CanceledCallback> Canceled { get; } = [];

        public Func<IMessage, bool>? FailPublishOnce { get; set; }

        public Task PublishAsync<TEvent>(
            TEvent evt,
            EventDirection direction = EventDirection.Down,
            CancellationToken ct = default)
            where TEvent : IMessage
        {
            _ = ct;
            if (!_publishFailureConsumed && FailPublishOnce?.Invoke(evt) == true)
            {
                _publishFailureConsumed = true;
                throw new InvalidOperationException("transient publish failure");
            }

            Published.Add((evt, direction));
            return Task.CompletedTask;
        }

        public Task SendToAsync<TEvent>(string targetActorId, TEvent evt, CancellationToken ct = default)
            where TEvent : IMessage
        {
            _ = targetActorId;
            return PublishAsync(evt, EventDirection.Self, ct);
        }

        public TState LoadState<TState>(string scopeKey)
            where TState : class, IMessage<TState>, new()
        {
            var packed = ((IWorkflowExecutionStateHost)Agent).GetExecutionState(scopeKey);
            if (packed == null || !packed.Is(new TState().Descriptor))
                return new TState();

            return packed.Unpack<TState>() ?? new TState();
        }

        public IReadOnlyList<KeyValuePair<string, TState>> LoadStates<TState>(string scopeKeyPrefix = "")
            where TState : class, IMessage<TState>, new()
        {
            var result = new List<KeyValuePair<string, TState>>();
            foreach (var (scopeKey, packed) in ((IWorkflowExecutionStateHost)Agent).GetExecutionStates())
            {
                if (!string.IsNullOrEmpty(scopeKeyPrefix) &&
                    !scopeKey.StartsWith(scopeKeyPrefix, StringComparison.Ordinal))
                {
                    continue;
                }

                if (!packed.Is(new TState().Descriptor))
                    continue;

                result.Add(new KeyValuePair<string, TState>(scopeKey, packed.Unpack<TState>() ?? new TState()));
            }

            return result;
        }

        public Task SaveStateAsync<TState>(string scopeKey, TState state, CancellationToken ct = default)
            where TState : class, IMessage<TState> =>
            ((IWorkflowExecutionStateHost)Agent).UpsertExecutionStateAsync(scopeKey, Any.Pack(state), ct);

        public Task ClearStateAsync(string scopeKey, CancellationToken ct = default) =>
            ((IWorkflowExecutionStateHost)Agent).ClearExecutionStateAsync(scopeKey, ct);

        public Task<RuntimeCallbackLease> ScheduleSelfDurableTimeoutAsync(
            string callbackId,
            TimeSpan dueTime,
            IMessage evt,
            IReadOnlyDictionary<string, string>? metadata = null,
            CancellationToken ct = default)
        {
            _ = dueTime;
            _ = metadata;
            _ = ct;
            var generation = _generations.GetValueOrDefault(callbackId, 0) + 1;
            _generations[callbackId] = generation;
            Scheduled.Add(new ScheduledCallback(callbackId, generation, evt));
            return Task.FromResult(new RuntimeCallbackLease(AgentId, callbackId, generation, RuntimeCallbackBackend.InMemory));
        }

        public Task<RuntimeCallbackLease> ScheduleSelfDurableTimerAsync(
            string callbackId,
            TimeSpan dueTime,
            TimeSpan period,
            IMessage evt,
            IReadOnlyDictionary<string, string>? metadata = null,
            CancellationToken ct = default)
        {
            _ = dueTime;
            _ = period;
            _ = metadata;
            _ = ct;
            var generation = _generations.GetValueOrDefault(callbackId, 0) + 1;
            _generations[callbackId] = generation;
            Scheduled.Add(new ScheduledCallback(callbackId, generation, evt));
            return Task.FromResult(new RuntimeCallbackLease(AgentId, callbackId, generation, RuntimeCallbackBackend.InMemory));
        }

        public Task CancelDurableCallbackAsync(
            RuntimeCallbackLease lease,
            CancellationToken ct = default)
        {
            _ = ct;
            Canceled.Add(new CanceledCallback(lease));
            return Task.CompletedTask;
        }
    }

    private sealed class StubWorkflowRunAgent(string id, string runId) : IAgent, IWorkflowExecutionStateHost
    {
        private readonly Dictionary<string, Any> _executionStates = new(StringComparer.Ordinal);

        public string Id => id;

        public string RunId { get; } = runId;

        public Any? GetExecutionState(string scopeKey) =>
            _executionStates.TryGetValue(scopeKey, out var state) ? state : null;

        public IReadOnlyList<KeyValuePair<string, Any>> GetExecutionStates() =>
            _executionStates.ToList();

        public Task UpsertExecutionStateAsync(string scopeKey, Any state, CancellationToken ct = default)
        {
            _ = ct;
            _executionStates[scopeKey] = state;
            return Task.CompletedTask;
        }

        public Task ClearExecutionStateAsync(string scopeKey, CancellationToken ct = default)
        {
            _ = ct;
            _executionStates.Remove(scopeKey);
            return Task.CompletedTask;
        }

        public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default) => Task.CompletedTask;

        public Task<string> GetDescriptionAsync() => Task.FromResult("stub");

        public Task<IReadOnlyList<System.Type>> GetSubscribedEventTypesAsync() =>
            Task.FromResult<IReadOnlyList<System.Type>>([]);

        public Task ActivateAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class NullServiceProvider : IServiceProvider
    {
        public object? GetService(System.Type serviceType) => null;
    }

    private sealed record ScheduledCallback(
        string CallbackId,
        long Generation,
        IMessage Event);

    private sealed record CanceledCallback(
        RuntimeCallbackLease Lease)
    {
        public string CallbackId => Lease.CallbackId;

        public long ExpectedGeneration => Lease.Generation;
    }
}
