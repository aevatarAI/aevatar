using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Core;
using Aevatar.Workflow.Core;
using Aevatar.Workflow.Core.Modules;
using Aevatar.Workflow.Core.Primitives;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.Integration.Tests;

[Trait("Category", "Integration")]
[Trait("Feature", "WorkflowLoopModule")]
public sealed class WorkflowLoopModuleCoverageTests
{
    [Fact]
    public void CanHandle_ShouldMatchStartAndStepCompleted()
    {
        var module = new WorkflowLoopModule();

        module.CanHandle(Envelope(new StartWorkflowEvent())).Should().BeTrue();
        module.CanHandle(Envelope(new StepCompletedEvent())).Should().BeTrue();
        module.CanHandle(new EventEnvelope()).Should().BeFalse();
    }

    [Fact]
    public async Task HandleAsync_WhenWorkflowNotSet_ShouldNoop()
    {
        var module = new WorkflowLoopModule();
        var ctx = CreateContext();

        await module.HandleAsync(Envelope(new StartWorkflowEvent { Input = "x" }), ctx, CancellationToken.None);

        ctx.Published.Should().BeEmpty();
    }

    [Fact]
    public async Task HandleAsync_WhenWorkflowHasNoSteps_ShouldPublishFailure()
    {
        var module = new WorkflowLoopModule();
        module.SetWorkflow(new WorkflowDefinition
        {
            Name = "wf-empty",
            Roles = [],
            Steps = [],
        });
        var ctx = CreateContext();

        await module.HandleAsync(Envelope(new StartWorkflowEvent { Input = "x" }), ctx, CancellationToken.None);

        var completed = ctx.Published.Should().ContainSingle().Subject.evt.Should().BeOfType<WorkflowCompletedEvent>().Subject;
        completed.Success.Should().BeFalse();
        completed.Error.Should().Contain("无步骤");
    }

    [Fact]
    public async Task HandleAsync_ShouldDispatchStepAdvanceAndComplete()
    {
        var module = new WorkflowLoopModule();
        module.SetWorkflow(BuildWorkflow(
            new StepDefinition
            {
                Id = "s1",
                Type = "connector_call",
                TargetRole = "coordinator",
                Parameters = new Dictionary<string, string> { ["connector"] = "conn-a" },
            },
            new StepDefinition
            {
                Id = "s2",
                Type = "transform",
            }));
        var ctx = CreateContext();
        const string runId = "run-dispatch-advance-complete";

        await module.HandleAsync(Envelope(new StartWorkflowEvent { RunId = runId, Input = "hello" }), ctx, CancellationToken.None);
        var firstRequest = ctx.Published.Should().ContainSingle().Subject.evt.Should().BeOfType<StepRequestEvent>().Subject;
        firstRequest.StepId.Should().Be("s1");
        firstRequest.Input.Should().Be("hello");
        firstRequest.Parameters["allowed_connectors"].Should().Be("conn-a,conn-b");
        ctx.Published.Clear();

        await module.HandleAsync(
            Envelope(new StepCompletedEvent { StepId = "s1", RunId = runId, Success = true, Output = "next-input" }),
            ctx,
            CancellationToken.None);
        var secondRequest = ctx.Published.Should().ContainSingle().Subject.evt.Should().BeOfType<StepRequestEvent>().Subject;
        secondRequest.StepId.Should().Be("s2");
        secondRequest.Input.Should().Be("next-input");
        ctx.Published.Clear();

        await module.HandleAsync(
            Envelope(new StepCompletedEvent { StepId = "s2", RunId = runId, Success = true, Output = "done" }),
            ctx,
            CancellationToken.None);
        var completed = ctx.Published.Should().ContainSingle().Subject.evt.Should().BeOfType<WorkflowCompletedEvent>().Subject;
        completed.Success.Should().BeTrue();
        completed.Output.Should().Be("done");
    }

    [Fact]
    public async Task HandleAsync_ShouldCanonicalizeStepTypeAndStepTypeParametersBeforeDispatch()
    {
        var module = new WorkflowLoopModule();
        module.SetWorkflow(BuildWorkflow(
            new StepDefinition
            {
                Id = "s1",
                Type = "loop",
                Parameters = new Dictionary<string, string>
                {
                    ["step"] = "judge",
                    ["max_iterations"] = "1",
                },
            }));
        var ctx = CreateContext();

        await module.HandleAsync(
            Envelope(new StartWorkflowEvent
            {
                RunId = "run-alias",
                Input = "seed",
            }),
            ctx,
            CancellationToken.None);

        var request = ctx.Published.Should().ContainSingle().Subject.evt.Should().BeOfType<StepRequestEvent>().Subject;
        request.StepId.Should().Be("s1");
        request.StepType.Should().Be("while");
        request.RunId.Should().Be("run-alias");
        request.Parameters["step"].Should().Be("evaluate");
    }

    [Fact]
    public async Task HandleAsync_WhenDispatchingWhileStep_ShouldPreserveRuntimeEvaluatedParameters()
    {
        var module = new WorkflowLoopModule();
        module.SetWorkflow(BuildWorkflow(
            new StepDefinition
            {
                Id = "s1",
                Type = "while",
                Parameters = new Dictionary<string, string>
                {
                    ["step"] = "transform",
                    ["max_iterations"] = "3",
                    ["condition"] = "${lt(iteration, 3)}",
                    ["sub_param_prompt"] = "${concat('iter=', iteration, ', input=', input)}",
                },
            }));
        var ctx = CreateContext();

        await module.HandleAsync(
            Envelope(new StartWorkflowEvent
            {
                RunId = "run-while-runtime-params",
                Input = "seed",
            }),
            ctx,
            CancellationToken.None);

        var request = ctx.Published.Should().ContainSingle().Subject.evt.Should().BeOfType<StepRequestEvent>().Subject;
        request.Parameters["condition"].Should().Be("${lt(iteration, 3)}");
        request.Parameters["sub_param_prompt"].Should().Be("${concat('iter=', iteration, ', input=', input)}");
    }

    [Fact]
    public async Task HandleAsync_WhenStartParametersProvided_ShouldExposeContextVariablesToStepExpressions()
    {
        var module = new WorkflowLoopModule();
        module.SetWorkflow(BuildWorkflow(
            new StepDefinition
            {
                Id = "s1",
                Type = "assign",
                Parameters = new Dictionary<string, string>
                {
                    ["target"] = "result",
                    ["value"] = "${session_id}",
                },
            }));
        var ctx = CreateContext();

        await module.HandleAsync(
            Envelope(new StartWorkflowEvent
            {
                RunId = "run-start-parameters",
                Input = "seed",
                Parameters =
                {
                    ["session_id"] = "session-ctx-001",
                    ["workflow.session_id"] = "workflow-session-ctx-001",
                },
            }),
            ctx,
            CancellationToken.None);

        var request = ctx.Published.Should().ContainSingle().Subject.evt.Should().BeOfType<StepRequestEvent>().Subject;
        request.Parameters["value"].Should().Be("session-ctx-001");
    }

    [Fact]
    public async Task HandleAsync_WhenAlreadyRunning_ShouldPublishFailure()
    {
        var module = new WorkflowLoopModule();
        module.SetWorkflow(BuildWorkflow(new StepDefinition { Id = "s1", Type = "llm_call" }));
        var ctx = CreateContext();
        const string runId = "run-already-running";

        await module.HandleAsync(
            Envelope(new StartWorkflowEvent { RunId = runId, Input = "first" }),
            ctx,
            CancellationToken.None);
        await module.HandleAsync(
            Envelope(new StartWorkflowEvent { RunId = runId, Input = "second" }),
            ctx,
            CancellationToken.None);

        var completed = ctx.Published.Select(x => x.evt).OfType<WorkflowCompletedEvent>().Single();
        completed.Success.Should().BeFalse();
        completed.RunId.Should().Be(runId);
        completed.Error.Should().Contain("already active");
    }

    [Fact]
    public async Task HandleAsync_WhenRunIdContainsWhitespace_ShouldNormalizeStartAndCompletion()
    {
        var module = new WorkflowLoopModule();
        module.SetWorkflow(BuildWorkflow(new StepDefinition { Id = "s1", Type = "llm_call" }));
        var ctx = CreateContext();

        await module.HandleAsync(
            Envelope(new StartWorkflowEvent { RunId = "  run-trim  ", Input = "start" }),
            ctx,
            CancellationToken.None);

        var request = ctx.Published.Should().ContainSingle().Subject.evt.Should().BeOfType<StepRequestEvent>().Subject;
        request.RunId.Should().Be("run-trim");
        ctx.Published.Clear();

        await module.HandleAsync(
            Envelope(new StepCompletedEvent
            {
                StepId = "s1",
                RunId = "run-trim",
                Success = true,
                Output = "done",
            }),
            ctx,
            CancellationToken.None);

        var completed = ctx.Published.Should().ContainSingle().Subject.evt.Should().BeOfType<WorkflowCompletedEvent>().Subject;
        completed.RunId.Should().Be("run-trim");
        completed.Success.Should().BeTrue();
        completed.Output.Should().Be("done");
    }

    [Fact]
    public async Task HandleAsync_WhenStepFails_ShouldPublishWorkflowFailure()
    {
        var module = new WorkflowLoopModule();
        module.SetWorkflow(BuildWorkflow(new StepDefinition { Id = "s1", Type = "llm_call" }));
        var ctx = CreateContext();
        const string runId = "run-step-fails";

        await module.HandleAsync(Envelope(new StartWorkflowEvent { RunId = runId, Input = "start" }), ctx, CancellationToken.None);
        ctx.Published.Clear();

        await module.HandleAsync(
            Envelope(new StepCompletedEvent { StepId = "s1", RunId = runId, Success = false, Error = "boom" }),
            ctx,
            CancellationToken.None);

        var completed = ctx.Published.Should().ContainSingle().Subject.evt.Should().BeOfType<WorkflowCompletedEvent>().Subject;
        completed.Success.Should().BeFalse();
        completed.Error.Should().Be("boom");
    }

    [Fact]
    public async Task HandleAsync_WhenCompletionStepIsUnknown_ShouldIgnore()
    {
        var module = new WorkflowLoopModule();
        module.SetWorkflow(BuildWorkflow(new StepDefinition { Id = "s1", Type = "llm_call" }));
        var ctx = CreateContext();
        const string runId = "run-unknown-step";

        await module.HandleAsync(Envelope(new StartWorkflowEvent { RunId = runId, Input = "start" }), ctx, CancellationToken.None);
        ctx.Published.Clear();

        await module.HandleAsync(
            Envelope(new StepCompletedEvent { StepId = "s1_internal_sub_1", RunId = runId, Success = true, Output = "x" }),
            ctx,
            CancellationToken.None);

        ctx.Published.Should().BeEmpty();
    }

    [Fact]
    public async Task HandleAsync_WhenCompletionRunIdMissing_ShouldIgnoreEvenWithSingleActiveRun()
    {
        var module = new WorkflowLoopModule();
        module.SetWorkflow(BuildWorkflow(new StepDefinition { Id = "s1", Type = "llm_call" }));
        var ctx = CreateContext();

        await module.HandleAsync(
            Envelope(new StartWorkflowEvent
            {
                RunId = "run-missing-completion-run-id",
                Input = "start",
            }),
            ctx,
            CancellationToken.None);
        ctx.Published.Clear();

        await module.HandleAsync(
            Envelope(new StepCompletedEvent
            {
                StepId = "s1",
                Success = true,
                Output = "done",
            }),
            ctx,
            CancellationToken.None);

        ctx.Published.Should().BeEmpty();
    }

    [Fact]
    public async Task HandleAsync_WhenRetryAllowed_ShouldRedispatchStep()
    {
        var module = new WorkflowLoopModule();
        module.SetWorkflow(BuildWorkflow(new StepDefinition
        {
            Id = "s1",
            Type = "llm_call",
            Retry = new StepRetryPolicy
            {
                MaxAttempts = 3,
                DelayMs = 0,
                Backoff = "fixed",
            },
        }));
        var ctx = CreateContext();
        const string runId = "run-retry-allowed";

        await module.HandleAsync(
            Envelope(new StartWorkflowEvent { RunId = runId, Input = "start" }),
            ctx,
            CancellationToken.None);
        ctx.Published.Clear();

        await module.HandleAsync(
            Envelope(new StepCompletedEvent { StepId = "s1", RunId = runId, Success = false, Error = "boom-1" }),
            ctx,
            CancellationToken.None);

        var retryRequest = ctx.Published.Should().ContainSingle().Subject.evt.Should().BeOfType<StepRequestEvent>().Subject;
        retryRequest.StepId.Should().Be("s1");
        retryRequest.RunId.Should().Be(runId);
    }

    [Fact]
    public async Task HandleAsync_WhenRetryExhausted_ShouldPublishFailure()
    {
        var module = new WorkflowLoopModule();
        module.SetWorkflow(BuildWorkflow(new StepDefinition
        {
            Id = "s1",
            Type = "llm_call",
            Retry = new StepRetryPolicy
            {
                MaxAttempts = 3,
                DelayMs = 0,
                Backoff = "fixed",
            },
        }));
        var ctx = CreateContext();
        const string runId = "run-retry-exhausted";

        await module.HandleAsync(
            Envelope(new StartWorkflowEvent { RunId = runId, Input = "start" }),
            ctx,
            CancellationToken.None);
        ctx.Published.Clear();

        await module.HandleAsync(
            Envelope(new StepCompletedEvent { StepId = "s1", RunId = runId, Success = false, Error = "boom-1" }),
            ctx,
            CancellationToken.None);
        ctx.Published.Select(x => x.evt)
            .OfType<StepRequestEvent>()
            .Should()
            .ContainSingle(req => req.StepId == "s1");
        ctx.Published.Clear();

        await module.HandleAsync(
            Envelope(new StepCompletedEvent { StepId = "s1", RunId = runId, Success = false, Error = "boom-2" }),
            ctx,
            CancellationToken.None);
        ctx.Published.Select(x => x.evt)
            .OfType<StepRequestEvent>()
            .Should()
            .ContainSingle(req => req.StepId == "s1");
        ctx.Published.Clear();

        await module.HandleAsync(
            Envelope(new StepCompletedEvent { StepId = "s1", RunId = runId, Success = false, Error = "boom-3" }),
            ctx,
            CancellationToken.None);

        var completed = ctx.Published.Should().ContainSingle().Subject.evt.Should().BeOfType<WorkflowCompletedEvent>().Subject;
        completed.Success.Should().BeFalse();
        completed.Error.Should().Be("boom-3");
    }

    [Fact]
    public async Task HandleAsync_WhenOnErrorSkipWithoutNext_ShouldCompleteSuccess()
    {
        var module = new WorkflowLoopModule();
        module.SetWorkflow(BuildWorkflow(new StepDefinition
        {
            Id = "s1",
            Type = "llm_call",
            OnError = new StepErrorPolicy
            {
                Strategy = "skip",
                DefaultOutput = "default-skip-output",
            },
        }));
        var ctx = CreateContext();
        const string runId = "run-onerror-skip-final";

        await module.HandleAsync(
            Envelope(new StartWorkflowEvent { RunId = runId, Input = "start" }),
            ctx,
            CancellationToken.None);
        ctx.Published.Clear();

        await module.HandleAsync(
            Envelope(new StepCompletedEvent { StepId = "s1", RunId = runId, Success = false, Error = "boom", Output = "ignored" }),
            ctx,
            CancellationToken.None);

        var completed = ctx.Published.Should().ContainSingle().Subject.evt.Should().BeOfType<WorkflowCompletedEvent>().Subject;
        completed.Success.Should().BeTrue();
        completed.Output.Should().Be("default-skip-output");
    }

    [Fact]
    public async Task HandleAsync_WhenOnErrorSkipWithNext_ShouldDispatchNextStep()
    {
        var module = new WorkflowLoopModule();
        module.SetWorkflow(BuildWorkflow(
            new StepDefinition
            {
                Id = "s1",
                Type = "llm_call",
                OnError = new StepErrorPolicy
                {
                    Strategy = "skip",
                    DefaultOutput = "skip-next-input",
                },
            },
            new StepDefinition
            {
                Id = "s2",
                Type = "transform",
            }));
        var ctx = CreateContext();
        const string runId = "run-onerror-skip-next";

        await module.HandleAsync(
            Envelope(new StartWorkflowEvent { RunId = runId, Input = "start" }),
            ctx,
            CancellationToken.None);
        ctx.Published.Clear();

        await module.HandleAsync(
            Envelope(new StepCompletedEvent { StepId = "s1", RunId = runId, Success = false, Error = "boom", Output = "ignored" }),
            ctx,
            CancellationToken.None);

        var nextRequest = ctx.Published.Should().ContainSingle().Subject.evt.Should().BeOfType<StepRequestEvent>().Subject;
        nextRequest.StepId.Should().Be("s2");
        nextRequest.Input.Should().Be("skip-next-input");
    }

    [Fact]
    public async Task HandleAsync_WhenOnErrorFallbackValid_ShouldDispatchFallbackStep()
    {
        var module = new WorkflowLoopModule();
        module.SetWorkflow(BuildWorkflow(
            new StepDefinition
            {
                Id = "s1",
                Type = "llm_call",
                OnError = new StepErrorPolicy
                {
                    Strategy = "fallback",
                    FallbackStep = "s2",
                },
            },
            new StepDefinition
            {
                Id = "s2",
                Type = "transform",
            }));
        var ctx = CreateContext();
        const string runId = "run-onerror-fallback";

        await module.HandleAsync(
            Envelope(new StartWorkflowEvent { RunId = runId, Input = "start" }),
            ctx,
            CancellationToken.None);
        ctx.Published.Clear();

        await module.HandleAsync(
            Envelope(new StepCompletedEvent { StepId = "s1", RunId = runId, Success = false, Error = "boom", Output = "fallback-input" }),
            ctx,
            CancellationToken.None);

        var fallbackRequest = ctx.Published.Should().ContainSingle().Subject.evt.Should().BeOfType<StepRequestEvent>().Subject;
        fallbackRequest.StepId.Should().Be("s2");
        fallbackRequest.Input.Should().Be("fallback-input");
    }

    [Fact]
    public async Task HandleAsync_WhenOnErrorFallbackMissing_ShouldPublishFailure()
    {
        var module = new WorkflowLoopModule();
        module.SetWorkflow(BuildWorkflow(new StepDefinition
        {
            Id = "s1",
            Type = "llm_call",
            OnError = new StepErrorPolicy
            {
                Strategy = "fallback",
                FallbackStep = "missing-step",
            },
        }));
        var ctx = CreateContext();
        const string runId = "run-onerror-fallback-missing";

        await module.HandleAsync(
            Envelope(new StartWorkflowEvent { RunId = runId, Input = "start" }),
            ctx,
            CancellationToken.None);
        ctx.Published.Clear();

        await module.HandleAsync(
            Envelope(new StepCompletedEvent { StepId = "s1", RunId = runId, Success = false, Error = "boom" }),
            ctx,
            CancellationToken.None);

        var completed = ctx.Published.Should().ContainSingle().Subject.evt.Should().BeOfType<WorkflowCompletedEvent>().Subject;
        completed.Success.Should().BeFalse();
        completed.Error.Should().Be("boom");
    }

    [Fact]
    public async Task HandleAsync_WhenBranchMetadataProvided_ShouldRouteToBranchStep()
    {
        var module = new WorkflowLoopModule();
        module.SetWorkflow(BuildWorkflow(
            new StepDefinition
            {
                Id = "s1",
                Type = "conditional",
                Branches = new Dictionary<string, string>
                {
                    ["yes"] = "s2",
                    ["_default"] = "s3",
                },
            },
            new StepDefinition { Id = "s2", Type = "transform" },
            new StepDefinition { Id = "s3", Type = "transform" }));
        var ctx = CreateContext();
        const string runId = "run-branch";

        await module.HandleAsync(
            Envelope(new StartWorkflowEvent { RunId = runId, Input = "start" }),
            ctx,
            CancellationToken.None);
        ctx.Published.Clear();

        await module.HandleAsync(
            Envelope(new StepCompletedEvent
            {
                StepId = "s1",
                RunId = runId,
                Success = true,
                Output = "branch-output",
                BranchKey = "yes",
            }),
            ctx,
            CancellationToken.None);

        var nextRequest = ctx.Published.Should().ContainSingle().Subject.evt.Should().BeOfType<StepRequestEvent>().Subject;
        nextRequest.StepId.Should().Be("s2");
    }

    [Fact]
    public async Task HandleAsync_WhenNextStepMetadataProvided_ShouldJumpToTargetStep()
    {
        var module = new WorkflowLoopModule();
        module.SetWorkflow(BuildWorkflow(
            new StepDefinition { Id = "s1", Type = "guard" },
            new StepDefinition { Id = "s2", Type = "transform" },
            new StepDefinition { Id = "s3", Type = "transform" }));
        var ctx = CreateContext();
        const string runId = "run-next-step";

        await module.HandleAsync(
            Envelope(new StartWorkflowEvent { RunId = runId, Input = "start" }),
            ctx,
            CancellationToken.None);
        ctx.Published.Clear();

        await module.HandleAsync(
            Envelope(new StepCompletedEvent
            {
                StepId = "s1",
                RunId = runId,
                Success = true,
                Output = "branch-output",
                NextStepId = "s3",
            }),
            ctx,
            CancellationToken.None);

        var nextRequest = ctx.Published.Should().ContainSingle().Subject.evt.Should().BeOfType<StepRequestEvent>().Subject;
        nextRequest.StepId.Should().Be("s3");
    }

    [Fact]
    public async Task HandleAsync_WhenTimeoutConfigured_ShouldPublishTimeoutCompletion()
    {
        var module = new WorkflowLoopModule();
        module.SetWorkflow(BuildWorkflow(new StepDefinition
        {
            Id = "s1",
            Type = "llm_call",
            TimeoutMs = 1,
        }));
        var ctx = CreateContext();

        await module.HandleAsync(
            Envelope(new StartWorkflowEvent { RunId = "run-timeout", Input = "start" }),
            ctx,
            CancellationToken.None);

        var scheduled = ctx.Scheduled.Should().ContainSingle(x => x.Event is WorkflowStepTimeoutFiredEvent).Subject;
        await module.HandleAsync(ctx.CreateScheduledEnvelope(scheduled), ctx, CancellationToken.None);

        var timeoutEvent = ctx.Published.Select(x => x.evt).OfType<StepCompletedEvent>().Single();
        timeoutEvent.Error.Should().Contain("TIMEOUT");
    }

    [Fact]
    public async Task HandleAsync_WhenRetryUsesExponentialDelay_ShouldRetryWithOriginalInput()
    {
        var module = new WorkflowLoopModule();
        module.SetWorkflow(BuildWorkflow(new StepDefinition
        {
            Id = "s1",
            Type = "llm_call",
            Retry = new StepRetryPolicy
            {
                MaxAttempts = 3,
                Backoff = "exponential",
                DelayMs = 1,
            },
        }));
        var ctx = CreateContext();
        const string runId = "run-retry-exp";

        await module.HandleAsync(
            Envelope(new StartWorkflowEvent { RunId = runId, Input = "start" }),
            ctx,
            CancellationToken.None);
        ctx.Published.Clear();

        await module.HandleAsync(
            Envelope(new StepCompletedEvent { StepId = "s1", RunId = runId, Success = false, Error = "boom" }),
            ctx,
            CancellationToken.None);

        var scheduled = ctx.Scheduled.Should().ContainSingle(x => x.Event is WorkflowStepRetryBackoffFiredEvent).Subject;
        await module.HandleAsync(ctx.CreateScheduledEnvelope(scheduled), ctx, CancellationToken.None);

        var retry = ctx.Published.Select(x => x.evt).OfType<StepRequestEvent>().Single();
        retry.StepId.Should().Be("s1");
        retry.Input.Should().Be("start");
    }

    [Fact]
    public async Task HandleAsync_WhenOnErrorStrategyUnsupported_ShouldFailWorkflow()
    {
        var module = new WorkflowLoopModule();
        module.SetWorkflow(BuildWorkflow(new StepDefinition
        {
            Id = "s1",
            Type = "llm_call",
            OnError = new StepErrorPolicy { Strategy = "unknown" },
        }));
        var ctx = CreateContext();

        await module.HandleAsync(Envelope(new StartWorkflowEvent { RunId = "run-unsupported", Input = "start" }), ctx, CancellationToken.None);
        ctx.Published.Clear();

        await module.HandleAsync(
            Envelope(new StepCompletedEvent { StepId = "s1", RunId = "run-unsupported", Success = false, Error = "boom" }),
            ctx,
            CancellationToken.None);

        var completed = ctx.Published.Should().ContainSingle().Subject.evt.Should().BeOfType<WorkflowCompletedEvent>().Subject;
        completed.Success.Should().BeFalse();
        completed.Error.Should().Be("boom");
    }

    [Fact]
    public async Task HandleAsync_WhenOnErrorFallbackStepIsBlank_ShouldFailWorkflow()
    {
        var module = new WorkflowLoopModule();
        module.SetWorkflow(BuildWorkflow(new StepDefinition
        {
            Id = "s1",
            Type = "llm_call",
            OnError = new StepErrorPolicy
            {
                Strategy = "fallback",
                FallbackStep = " ",
            },
        }));
        var ctx = CreateContext();

        await module.HandleAsync(Envelope(new StartWorkflowEvent { RunId = "run-fallback-blank", Input = "start" }), ctx, CancellationToken.None);
        ctx.Published.Clear();

        await module.HandleAsync(
            Envelope(new StepCompletedEvent { StepId = "s1", RunId = "run-fallback-blank", Success = false, Error = "boom" }),
            ctx,
            CancellationToken.None);

        var completed = ctx.Published.Should().ContainSingle().Subject.evt.Should().BeOfType<WorkflowCompletedEvent>().Subject;
        completed.Success.Should().BeFalse();
        completed.Error.Should().Be("boom");
    }

    [Fact]
    public async Task HandleAsync_WhenOnErrorFallbackHasNoOutput_ShouldPassErrorTextToFallbackStep()
    {
        var module = new WorkflowLoopModule();
        module.SetWorkflow(BuildWorkflow(
            new StepDefinition
            {
                Id = "s1",
                Type = "workflow_yaml_validate",
                OnError = new StepErrorPolicy
                {
                    Strategy = "fallback",
                    FallbackStep = "repair",
                },
            },
            new StepDefinition
            {
                Id = "repair",
                Type = "llm_call",
            }));
        var ctx = CreateContext();
        const string runId = "run-onerror-fallback-error-input";

        await module.HandleAsync(
            Envelope(new StartWorkflowEvent { RunId = runId, Input = "invalid-yaml" }),
            ctx,
            CancellationToken.None);
        ctx.Published.Clear();

        await module.HandleAsync(
            Envelope(new StepCompletedEvent
            {
                StepId = "s1",
                RunId = runId,
                Success = false,
                Error = "Invalid workflow YAML: Property 'description' not found on type 'RawStep'.",
            }),
            ctx,
            CancellationToken.None);

        var fallbackRequest = ctx.Published.Should().ContainSingle().Subject.evt.Should().BeOfType<StepRequestEvent>().Subject;
        fallbackRequest.StepId.Should().Be("repair");
        fallbackRequest.Input.Should().Contain("Property 'description' not found");
    }

    [Fact]
    public async Task HandleAsync_WhenStepCompletesBeforeTimeout_ShouldCancelPendingTimeout()
    {
        var module = new WorkflowLoopModule();
        module.SetWorkflow(BuildWorkflow(
            new StepDefinition
            {
                Id = "s1",
                Type = "llm_call",
                TimeoutMs = 2000,
            },
            new StepDefinition
            {
                Id = "s2",
                Type = "transform",
            }));
        var ctx = CreateContext();

        await module.HandleAsync(
            Envelope(new StartWorkflowEvent { RunId = "run-cancel-timeout", Input = "start" }),
            ctx,
            CancellationToken.None);
        ctx.Published.Clear();

        await module.HandleAsync(
            Envelope(new StepCompletedEvent { StepId = "s1", RunId = "run-cancel-timeout", Success = true, Output = "ok" }),
            ctx,
            CancellationToken.None);

        var next = ctx.Published.Should().ContainSingle().Subject.evt.Should().BeOfType<StepRequestEvent>().Subject;
        next.StepId.Should().Be("s2");
        ctx.Canceled.Should().ContainSingle(x =>
            x.CallbackId.StartsWith("workflow-step-timeout:run-cancel-timeout:s1:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task HandleAsync_WhenLateCompletionArrivesAfterTimeoutFailure_ShouldIgnoreIt()
    {
        var module = new WorkflowLoopModule();
        module.SetWorkflow(BuildWorkflow(
            new StepDefinition
            {
                Id = "s1",
                Type = "llm_call",
                OnError = new StepErrorPolicy
                {
                    Strategy = "fallback",
                    FallbackStep = "s2",
                },
            },
            new StepDefinition
            {
                Id = "s2",
                Type = "transform",
            }));
        var ctx = CreateContext();
        const string runId = "run-late-completion-after-timeout";

        await module.HandleAsync(
            Envelope(new StartWorkflowEvent { RunId = runId, Input = "start" }),
            ctx,
            CancellationToken.None);
        ctx.Published.Clear();

        await module.HandleAsync(
            Envelope(new StepCompletedEvent
            {
                StepId = "s1",
                RunId = runId,
                Success = false,
                Error = "TIMEOUT after 100ms",
            }),
            ctx,
            CancellationToken.None);
        ctx.Published.Should().ContainSingle().Which.evt.Should().BeOfType<WorkflowCompletedEvent>().Which.Success.Should().BeFalse();
        ctx.Published.Clear();

        await module.HandleAsync(
            Envelope(new StepCompletedEvent
            {
                StepId = "s1",
                RunId = runId,
                Success = true,
                Output = "late-success",
            }),
            ctx,
            CancellationToken.None);
        ctx.Published.Should().BeEmpty();
    }

    [Fact]
    public async Task HandleAsync_WhenOnErrorSkipWithoutDefaultAndOutput_ShouldUseEmptyOutput()
    {
        var module = new WorkflowLoopModule();
        module.SetWorkflow(BuildWorkflow(new StepDefinition
        {
            Id = "s1",
            Type = "llm_call",
            OnError = new StepErrorPolicy
            {
                Strategy = "skip",
                DefaultOutput = null,
            },
        }));
        var ctx = CreateContext();

        await module.HandleAsync(
            Envelope(new StartWorkflowEvent { RunId = "run-skip-empty", Input = "start" }),
            ctx,
            CancellationToken.None);
        ctx.Published.Clear();

        await module.HandleAsync(
            Envelope(new StepCompletedEvent { StepId = "s1", RunId = "run-skip-empty", Success = false, Error = "boom" }),
            ctx,
            CancellationToken.None);

        var completed = ctx.Published.Should().ContainSingle().Subject.evt.Should().BeOfType<WorkflowCompletedEvent>().Subject;
        completed.Success.Should().BeTrue();
        completed.Output.Should().BeEmpty();
    }

    [Fact]
    public async Task HandleAsync_WhenAssignMetadataPresent_ShouldUpdateRunVariables()
    {
        var module = new WorkflowLoopModule();
        module.SetWorkflow(BuildWorkflow(
            new StepDefinition
            {
                Id = "s1",
                Type = "assign",
            },
            new StepDefinition
            {
                Id = "s2",
                Type = "conditional",
                Parameters = new Dictionary<string, string>
                {
                    ["condition"] = "${eq(variables.counter, '1')}",
                },
                Branches = new Dictionary<string, string>
                {
                    ["true"] = "s3",
                    ["false"] = "s4",
                },
            },
            new StepDefinition { Id = "s3", Type = "transform" },
            new StepDefinition { Id = "s4", Type = "transform" }));
        var ctx = CreateContext();
        const string runId = "run-assign-metadata";

        await module.HandleAsync(
            Envelope(new StartWorkflowEvent { RunId = runId, Input = "start" }),
            ctx,
            CancellationToken.None);
        ctx.Published.Clear();

        await module.HandleAsync(
            Envelope(new StepCompletedEvent
            {
                StepId = "s1",
                RunId = runId,
                Success = true,
                Output = "ignored",
                AssignedVariable = "counter",
                AssignedValue = "1",
            }),
            ctx,
            CancellationToken.None);

        var conditionalRequest = ctx.Published.Should().ContainSingle().Subject.evt.Should().BeOfType<StepRequestEvent>().Subject;
        conditionalRequest.StepId.Should().Be("s2");
        conditionalRequest.Parameters["condition"].Should().Be("true");
    }

    [Fact]
    public async Task HandleAsync_WhenClosedWorldBlocksStep_ShouldFailWorkflow()
    {
        var module = new WorkflowLoopModule();
        module.SetWorkflow(new WorkflowDefinition
        {
            Name = "wf-closed-world",
            Configuration = new WorkflowRuntimeConfiguration
            {
                ClosedWorldMode = true,
            },
            Roles = [],
            Steps =
            [
                new StepDefinition { Id = "s1", Type = "llm_call" },
            ],
        });
        var ctx = CreateContext();
        const string runId = "run-closed-world";

        await module.HandleAsync(
            Envelope(new StartWorkflowEvent { RunId = runId, Input = "start" }),
            ctx,
            CancellationToken.None);

        var blocked = ctx.Published.Select(x => x.evt).OfType<StepCompletedEvent>().Single();
        blocked.Success.Should().BeFalse();
        blocked.Error.Should().Contain("closed_world_mode");

        ctx.Published.Clear();
        await module.HandleAsync(Envelope(blocked), ctx, CancellationToken.None);

        var completed = ctx.Published.Should().ContainSingle().Subject.evt.Should().BeOfType<WorkflowCompletedEvent>().Subject;
        completed.Success.Should().BeFalse();
        completed.Error.Should().Contain("closed_world_mode");
    }

    private static WorkflowDefinition BuildWorkflow(params StepDefinition[] steps)
    {
        return new WorkflowDefinition
        {
            Name = "wf",
            Roles =
            [
                new RoleDefinition
                {
                    Id = "coordinator",
                    Name = "Coordinator",
                    Connectors = ["conn-a", "conn-b"],
                },
            ],
            Steps = steps.ToList(),
        };
    }

    private static TestEventHandlerContext CreateContext()
    {
        return new TestEventHandlerContext(
            new ServiceCollection().BuildServiceProvider(),
            new TestAgent("workflow-loop-test-agent"),
            NullLogger.Instance);
    }

    private static EventEnvelope Envelope(IMessage evt)
    {
        return new EventEnvelope
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
            Payload = Any.Pack(evt),
            Route = EnvelopeRouteSemantics.CreateTopologyPublication("test-publisher", TopologyAudience.Self),
        };
    }

}
