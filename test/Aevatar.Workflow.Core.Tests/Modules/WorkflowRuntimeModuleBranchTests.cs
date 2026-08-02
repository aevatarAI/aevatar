using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.EventModules;
using Aevatar.Foundation.Abstractions.Runtime.Callbacks;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Abstractions.Execution;
using Aevatar.Workflow.Abstractions.Credentials;
using Aevatar.Workflow.Core.Composition;
using Aevatar.Workflow.Core.Execution;
using Aevatar.Workflow.Core.Modules;
using Aevatar.Workflow.Core.Primitives;
using Aevatar.Workflow.Core.Tests.Primitives;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.Globalization;

namespace Aevatar.Workflow.Core.Tests.Modules;

public sealed class WorkflowRuntimeModuleBranchTests
{
    [Fact]
    public void LlmRuntimeModules_CanHandle_ShouldReturnFalseForEmptyPayload()
    {
        new EvaluateModule().CanHandle(new EventEnvelope()).Should().BeFalse();
        new ReflectModule().CanHandle(new EventEnvelope()).Should().BeFalse();
    }

    [Fact]
    public async Task DelayModule_ShouldValidateIds_AndCompleteImmediatelyForZeroDuration()
    {
        var module = new DelayModule();
        var ctx = new RecordingWorkflowContext();

        await module.HandleAsync(
            Wrap(new StepRequestEvent
            {
                StepId = " ",
                StepType = "delay",
                RunId = " ",
            }),
            ctx,
            CancellationToken.None);

        var invalid = ctx.Published.Select(x => x.Event).OfType<StepCompletedEvent>().Single();
        invalid.Success.Should().BeFalse();
        invalid.Error.Should().Contain("run_id and step_id");

        ctx.Published.Clear();
        await module.HandleAsync(
            Wrap(new StepRequestEvent
            {
                StepId = "delay-now",
                StepType = "delay",
                RunId = "run-delay",
                Input = "payload",
                Parameters = { ["duration_ms"] = "0" },
            }),
            ctx,
            CancellationToken.None);

        var completion = ctx.Published.Select(x => x.Event).OfType<StepCompletedEvent>().Single();
        completion.Success.Should().BeTrue();
        completion.Output.Should().Be("payload");
        ctx.Scheduled.Should().BeEmpty();
    }

    [Fact]
    public async Task DelayModule_ShouldCancelExistingPending_AndRequireMatchingLease()
    {
        var module = new DelayModule();
        var ctx = new RecordingWorkflowContext();

        await module.HandleAsync(
            Wrap(new StepRequestEvent
            {
                StepId = "delay-step",
                StepType = "delay",
                RunId = "run-delay",
                Input = "first",
                Parameters = { ["duration_ms"] = "1000" },
            }),
            ctx,
            CancellationToken.None);
        var first = ctx.Scheduled.Single();

        await module.HandleAsync(
            Wrap(new StepRequestEvent
            {
                StepId = "delay-step",
                StepType = "delay",
                RunId = "run-delay",
                Input = "second",
                Parameters = { ["duration_ms"] = "2000" },
            }),
            ctx,
            CancellationToken.None);
        var second = ctx.Scheduled.Last();

        ctx.Canceled.Should().ContainSingle(x => x.CallbackId == first.CallbackId);
        ctx.Published.Clear();

        await module.HandleAsync(
            Wrap(
                new DelayStepTimeoutFiredEvent
                {
                    RunId = "run-delay",
                    StepId = "delay-step",
                    DurationMs = 2000,
                },
                MetadataFor(second, generation: second.Generation - 1)),
            ctx,
            CancellationToken.None);
        ctx.Published.Should().BeEmpty();

        await module.HandleAsync(
            Wrap(
                new DelayStepTimeoutFiredEvent
                {
                    RunId = "run-delay",
                    StepId = "delay-step",
                    DurationMs = 2000,
                },
                MetadataFor(second)),
            ctx,
            CancellationToken.None);

        var completion = ctx.Published.Select(x => x.Event).OfType<StepCompletedEvent>().Single();
        completion.Success.Should().BeTrue();
        completion.Output.Should().Be("second");
    }

    [Fact]
    public async Task WorkflowCallModule_ShouldValidateMissingFieldsAndLifecycle()
    {
        var module = new WorkflowCallModule();
        var ctx = new RecordingWorkflowContext();

        await module.HandleAsync(
            Wrap(new StepRequestEvent
            {
                StepId = " ",
                StepType = "workflow_call",
                RunId = "parent-run",
            }),
            ctx,
            CancellationToken.None);
        ctx.Published.Select(x => x.Event).OfType<StepCompletedEvent>().Single().Error
            .Should().Contain("missing step_id");

        ctx.Published.Clear();
        await module.HandleAsync(
            Wrap(new StepRequestEvent
            {
                StepId = "step-a",
                StepType = "workflow_call",
                RunId = "parent-run",
            }),
            ctx,
            CancellationToken.None);
        ctx.Published.Select(x => x.Event).OfType<StepCompletedEvent>().Single().Error
            .Should().Contain("missing workflow parameter");

        ctx.Published.Clear();
        await module.HandleAsync(
            Wrap(new StepRequestEvent
            {
                StepId = "step-a",
                StepType = "workflow_call",
                RunId = "parent-run",
                Parameters =
                {
                    ["workflow"] = "child_flow",
                    ["lifecycle"] = "invalid",
                },
            }),
            ctx,
            CancellationToken.None);
        ctx.Published.Select(x => x.Event).OfType<StepCompletedEvent>().Single().Error
            .Should().Contain(WorkflowCallLifecycle.AllowedValuesText);
    }

    [Fact]
    public async Task WorkflowCallModule_ShouldPublishInvocationForValidRequest()
    {
        var module = new WorkflowCallModule();
        var ctx = new RecordingWorkflowContext();

        var request = new StepRequestEvent
        {
            StepId = "step-b",
            StepType = "workflow_call",
            RunId = "parent-run",
            Input = "payload",
            Parameters =
            {
                ["workflow"] = "child_flow",
                ["lifecycle"] = "scope",
            },
        };
        request.InputFileRefs.Add(BuildWorkflowFileRef("file-workflow-call"));

        await module.HandleAsync(
            Wrap(request),
            ctx,
            CancellationToken.None);

        var invocation = ctx.Published.Select(x => x.Event).OfType<SubWorkflowInvokeRequestedEvent>().Single();
        invocation.ParentRunId.Should().Be("parent-run");
        invocation.ParentStepId.Should().Be("step-b");
        invocation.WorkflowName.Should().Be("child_flow");
        invocation.Input.Should().Be("payload");
        invocation.Lifecycle.Should().Be(WorkflowCallLifecycle.Scope);
        invocation.InvocationId.Should().StartWith("parent-run:workflow_call:step-b:");
        invocation.InputFileRefs.Should().ContainSingle().Which.FileId.Should().Be("file-workflow-call");
    }

    [Fact]
    public async Task LlmCallModule_ShouldPublishDeterministicFailure_WhenStepIdMissing()
    {
        var module = new LLMCallModule();
        var ctx = new RecordingWorkflowContext();

        await module.HandleAsync(
            Wrap(new StepRequestEvent
            {
                StepId = "",
                StepType = "llm_call",
                RunId = "run-llm-invalid",
                Input = "prompt",
            }),
            ctx,
            CancellationToken.None);

        var failure = ctx.Published.Select(x => x.Event).OfType<StepCompletedEvent>().Single();
        failure.Success.Should().BeFalse();
        failure.StepId.Should().BeEmpty();
        failure.Error.Should().Contain("requires non-empty step_id");
    }

    [Fact]
    public async Task LlmCallModule_ShouldPopulateCallerCredentialFromActorOwnedExecutionState()
    {
        var module = new LLMCallModule();
        var ctx = new RecordingWorkflowContext
        {
            ExecutionContextState =
            {
                CallerCredential = new WorkflowCallerCredentialState
                {
                    BearerToken = " typed-token ",
                },
                Llm = new WorkflowLlmExecutionContextState
                {
                    RoutePreference = " route-a ",
                },
            },
        };
        ctx.RuntimeContext.ApplyRequestMetadata(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["connector.http.authorization"] = "Bearer metadata-token",
            ["trace-id"] = "trace-1",
        });

        await module.HandleAsync(
            Wrap(new StepRequestEvent
            {
                StepId = "reply",
                StepType = "llm_call",
                RunId = "run-llm-auth",
                Input = "prompt",
            }),
            ctx,
            CancellationToken.None);

        var intent = DispatchedLlmIntent(ctx);
        intent.CallerCredential.BearerToken.Should().Be("typed-token");
        intent.RoutePreference.Should().Be("route-a");
        intent.Headers.Should().Contain("trace-id", "trace-1");
        intent.Headers.Should().NotContainKey("connector.http.authorization");
    }

    [Fact]
    public async Task LlmCallModule_ShouldIssueFreshCallerTokenForEveryDispatch()
    {
        var tokenProvider = new RotatingCallerAccessTokenProvider();
        var module = new LLMCallModule(callerAccessTokenProvider: tokenProvider);
        var ctx = new RecordingWorkflowContext();
        ctx.ExecutionContextState.CallerCredential = new WorkflowCallerCredentialState
        {
            NyxIdAuthority = CreateCallerAuthority(),
        };

        await module.HandleAsync(Wrap(new StepRequestEvent
        {
            StepId = "reply-1",
            StepType = "llm_call",
            RunId = "run-llm-auth",
            Input = "first",
        }), ctx, CancellationToken.None);
        await module.HandleAsync(Wrap(new StepRequestEvent
        {
            StepId = "reply-2",
            StepType = "llm_call",
            RunId = "run-llm-auth",
            Input = "second",
        }), ctx, CancellationToken.None);

        ctx.Sent.Select(x => x.Event).OfType<WorkflowLlmExecutionIntent>()
            .Select(intent => intent.CallerCredential.BearerToken)
            .Should().Equal("token-1", "token-2");
        tokenProvider.Authorities.Should().HaveCount(2);
    }

    [Fact]
    public async Task LlmCallModule_WithAuthorityAndNoProvider_ShouldFailClosed()
    {
        var module = new LLMCallModule();
        var ctx = new RecordingWorkflowContext();
        ctx.ExecutionContextState.CallerCredential = new WorkflowCallerCredentialState
        {
            NyxIdAuthority = CreateCallerAuthority(),
        };

        await module.HandleAsync(Wrap(new StepRequestEvent
        {
            StepId = "reply",
            StepType = "llm_call",
            RunId = "run-llm-auth",
            Input = "prompt",
        }), ctx, CancellationToken.None);

        ctx.Sent.Should().BeEmpty();
        var failure = ctx.Published.Select(x => x.Event).OfType<StepCompletedEvent>().Single();
        failure.Success.Should().BeFalse();
        failure.Error.Should().Contain("access token provider is unavailable");
    }

    [Fact]
    public async Task LlmCallModule_ShouldPopulateWorkflowRuntimeContextFromActorOwnedExecutionState()
    {
        var module = new LLMCallModule();
        var ctx = new RecordingWorkflowContext
        {
            ExecutionContextState =
            {
                WorkflowRuntime = new WorkflowToolRuntimeContextState
                {
                    ParentActorId = " upstream-parent ",
                    ParentRunId = " upstream-run ",
                    ParentStepId = " upstream-step ",
                    RootRunId = " root-run ",
                    Depth = 2,
                },
            },
        };

        await module.HandleAsync(
            Wrap(new StepRequestEvent
            {
                StepId = "reply",
                StepType = "llm_call",
                RunId = "run-llm-runtime",
                Input = "prompt",
            }),
            ctx,
            CancellationToken.None);

        var intent = DispatchedLlmIntent(ctx);
        intent.WorkflowRuntimeContext.Should().NotBeNull();
        intent.WorkflowRuntimeContext.ParentActorId.Should().Be("agent-1");
        intent.WorkflowRuntimeContext.ParentRunId.Should().Be("run-llm-runtime");
        intent.WorkflowRuntimeContext.ParentStepId.Should().Be("reply");
        intent.WorkflowRuntimeContext.RootRunId.Should().Be("root-run");
        intent.WorkflowRuntimeContext.Depth.Should().Be(2);
    }

    [Fact]
    public async Task LlmCallModule_WhenCompletionReportsManagedHandoff_ShouldLeaveParentStepPending()
    {
        var module = new LLMCallModule();
        var ctx = new RecordingWorkflowContext();

        await module.HandleAsync(
            Wrap(new StepRequestEvent
            {
                StepId = "reply",
                StepType = "llm_call",
                RunId = "run-llm-handoff",
                Input = "prompt",
            }),
            ctx,
            CancellationToken.None);
        var intent = DispatchedLlmIntent(ctx);
        var watchdog = ctx.Scheduled.Single();

        await module.HandleAsync(
            Wrap(new WorkflowLlmInvocationCompletedEvent
            {
                SessionId = intent.SessionId,
                Success = true,
                ManagedHandoff = new WorkflowManagedHandoffOutcome
                {
                    ParentActorId = "agent-1",
                    ParentRunId = "run-llm-handoff",
                    ParentStepId = "reply",
                    InvocationId = "run-llm-handoff:workflow_tool:reply:call-1",
                    ChildRunId = "run-llm-handoff:workflow_tool:reply:call-1",
                },
            }),
            ctx,
            CancellationToken.None);

        ctx.Canceled.Should().ContainSingle(x => x.CallbackId == watchdog.CallbackId);
        ctx.Published.Select(x => x.Event).OfType<StepCompletedEvent>().Should().BeEmpty();
    }

    [Fact]
    public async Task LlmCallModule_ShouldIgnoreMetadataOnlyCallerCredential()
    {
        var module = new LLMCallModule();
        var ctx = new RecordingWorkflowContext();
        ctx.RuntimeContext.ApplyRequestMetadata(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["connector.http.authorization"] = "Bearer metadata-token",
        });

        await module.HandleAsync(
            Wrap(new StepRequestEvent
            {
                StepId = "reply",
                StepType = "llm_call",
                RunId = "run-llm-auth",
                Input = "prompt",
            }),
            ctx,
            CancellationToken.None);

        var intent = DispatchedLlmIntent(ctx);
        intent.CallerCredential.BearerToken.Should().BeEmpty();
        intent.Headers.Should().NotContainKey("connector.http.authorization");
    }

    [Fact]
    public async Task LlmCallModule_ShouldForwardSenderNyxIdAccessTokenToIntent()
    {
        var module = new LLMCallModule();
        var ctx = new RecordingWorkflowContext();
        ctx.RuntimeContext.ApplySenderNyxIdAccessToken(" sender-token-llm ");

        await module.HandleAsync(
            Wrap(new StepRequestEvent
            {
                StepId = "llm-token",
                StepType = "llm_call",
                RunId = "run-llm-token",
                Input = "prompt",
            }),
            ctx,
            CancellationToken.None);

        var intent = DispatchedLlmIntent(ctx);
        intent.SenderNyxIdAccessToken.Should().Be("sender-token-llm");
    }

    [Fact]
    public async Task LlmCallModule_ShouldDispatchScheduleIdFromWorkflowContext()
    {
        var module = new LLMCallModule();
        var ctx = new RecordingWorkflowContext
        {
            ScheduleId = " schedule-llm ",
        };

        await module.HandleAsync(
            Wrap(new StepRequestEvent
            {
                StepId = "llm-schedule",
                StepType = "llm_call",
                RunId = "run-llm-schedule",
                Input = "prompt",
            }),
            ctx,
            CancellationToken.None);

        DispatchedLlmIntent(ctx).ScheduleId.Should().Be("schedule-llm");
    }

    [Fact]
    public async Task EvaluateModule_ShouldPublishDeterministicFailure_WhenStepIdMissing()
    {
        var module = new EvaluateModule();
        var ctx = new RecordingWorkflowContext();

        await module.HandleAsync(
            Wrap(new StepRequestEvent
            {
                StepId = "",
                StepType = "evaluate",
                RunId = "run-evaluate-invalid",
                Input = "draft",
            }),
            ctx,
            CancellationToken.None);

        var failure = ctx.Published.Select(x => x.Event).OfType<StepCompletedEvent>().Single();
        failure.Success.Should().BeFalse();
        failure.StepId.Should().BeEmpty();
        failure.Error.Should().Contain("requires non-empty step_id");
    }

    [Fact]
    public async Task EvaluateModule_ShouldIgnoreUnsupportedAndUncorrelatedCompletionBranches()
    {
        var module = new EvaluateModule();
        var ctx = new RecordingWorkflowContext();

        await module.HandleAsync(new EventEnvelope(), ctx, CancellationToken.None);
        await module.HandleAsync(
            Wrap(new StepRequestEvent
            {
                StepId = "transform-step",
                StepType = "transform",
                RunId = "run-evaluate-ignore",
            }),
            ctx,
            CancellationToken.None);
        await module.HandleAsync(
            Wrap(new WorkflowLlmInvocationCompletedEvent
            {
                SessionId = "",
                Success = true,
                Content = "5",
            }),
            ctx,
            CancellationToken.None);
        await module.HandleAsync(
            Wrap(new WorkflowLlmInvocationCompletedEvent
            {
                SessionId = "missing-session",
                Success = true,
                Content = "5",
            }),
            ctx,
            CancellationToken.None);

        ctx.Published.Should().BeEmpty();
        ctx.Sent.Should().BeEmpty();
    }

    [Fact]
    public async Task EvaluateModule_ShouldPublishDefaultLlmFailure_WhenCompletionFailsWithoutError()
    {
        var module = new EvaluateModule();
        var ctx = new RecordingWorkflowContext();

        await module.HandleAsync(
            Wrap(new StepRequestEvent
            {
                StepId = "evaluate-failure",
                StepType = "evaluate",
                RunId = "run-evaluate-failure",
                Input = "draft",
                TargetRole = "judge",
            }),
            ctx,
            CancellationToken.None);
        var intent = ctx.Sent.Select(x => x.Event).OfType<WorkflowLlmExecutionIntent>().Single();

        await module.HandleAsync(
            Wrap(new WorkflowLlmInvocationCompletedEvent
            {
                SessionId = intent.SessionId,
                Success = false,
                Error = " ",
            }),
            ctx,
            CancellationToken.None);

        var failure = ctx.Published.Select(x => x.Event).OfType<StepCompletedEvent>().Single();
        failure.StepId.Should().Be("evaluate-failure");
        failure.RunId.Should().Be("run-evaluate-failure");
        failure.Success.Should().BeFalse();
        failure.Error.Should().Be("evaluate LLM call failed.");
    }

    [Fact]
    public async Task EvaluateModule_ShouldDispatchScheduleIdFromWorkflowContext()
    {
        var module = new EvaluateModule();
        var ctx = new RecordingWorkflowContext
        {
            ScheduleId = " schedule-evaluate ",
        };

        await module.HandleAsync(
            Wrap(new StepRequestEvent
            {
                StepId = "evaluate-schedule",
                StepType = "evaluate",
                RunId = "run-evaluate-schedule",
                Input = "draft",
            }),
            ctx,
            CancellationToken.None);

        DispatchedLlmIntent(ctx).ScheduleId.Should().Be("schedule-evaluate");
    }

    [Fact]
    public async Task LlmCallModule_ShouldCopyStepInputFileRefsToExecutionIntent()
    {
        var module = new LLMCallModule();
        var ctx = new RecordingWorkflowContext();
        var request = new StepRequestEvent
        {
            StepId = "llm-files",
            StepType = "llm_call",
            RunId = "run-llm-files",
            Input = "describe this",
            TargetRole = "reviewer",
        };
        request.InputFileRefs.Add(BuildWorkflowFileRef("file-intent"));

        await module.HandleAsync(Wrap(request), ctx, CancellationToken.None);

        var intent = ctx.Sent.Select(x => x.Event).OfType<WorkflowLlmExecutionIntent>().Single();
        intent.InputFileRefs.Should().ContainSingle().Which.FileId.Should().Be("file-intent");
    }

    [Fact]
    public async Task EvaluateModule_ShouldPublishDispatchFailure_WhenRoleSendFails()
    {
        var module = new EvaluateModule();
        var ctx = new RecordingWorkflowContext
        {
            SendToException = new InvalidOperationException("role inbox unavailable"),
        };

        await module.HandleAsync(
            Wrap(new StepRequestEvent
            {
                StepId = "evaluate-dispatch-failure",
                StepType = "evaluate",
                RunId = "run-evaluate-dispatch-failure",
                Input = "draft",
                TargetRole = "judge",
            }),
            ctx,
            CancellationToken.None);

        var failure = ctx.Published.Select(x => x.Event).OfType<StepCompletedEvent>().Single();
        failure.StepId.Should().Be("evaluate-dispatch-failure");
        failure.Success.Should().BeFalse();
        failure.Error.Should().Contain("evaluate dispatch failed");
        failure.Error.Should().Contain("role inbox unavailable");
    }

    [Fact]
    public async Task EvaluateModule_ShouldTrimChatAnnotationsAndUseFallbackThreshold()
    {
        var module = new EvaluateModule();
        var ctx = new RecordingWorkflowContext();

        await module.HandleAsync(
            Wrap(new StepRequestEvent
            {
                StepId = "evaluate-annotations",
                StepType = "evaluate",
                RunId = " ",
                Parameters =
                {
                    ["threshold"] = "not-a-number",
                    ["on_below"] = "revise",
                    [" tenant "] = " alpha ",
                    ["timeout_ms"] = "100",
                    ["blank"] = " ",
                    [" "] = "ignored",
                },
            }),
            ctx,
            CancellationToken.None);

        var intent = ctx.Published.Select(x => x.Event).OfType<WorkflowLlmExecutionIntent>().Single();
        intent.RunId.Should().Be("default");
        intent.StepId.Should().Be("evaluate-annotations");
        intent.SessionId.Should().Be("agent-1:default:evaluate-annotations:a1");
        intent.Prompt.Should().Contain("Content to evaluate:");
        intent.Annotations.Should().ContainKey("tenant").WhoseValue.Should().Be("alpha");
        intent.Annotations.Should().NotContainKey("timeout_ms");
        intent.Annotations.Should().NotContainKey("blank");

        ctx.RuntimeContext.ApplySenderNyxIdAccessToken(" sender-token-evaluate ");
        await module.HandleAsync(
            Wrap(new StepRequestEvent
            {
                StepId = "evaluate-token",
                StepType = "evaluate",
                RunId = "run-evaluate-token",
                Input = "draft",
            }),
            ctx,
            CancellationToken.None);
        ctx.Published.Select(x => x.Event).OfType<WorkflowLlmExecutionIntent>().Last()
            .SenderNyxIdAccessToken.Should().Be("sender-token-evaluate");

        await module.HandleAsync(
            Wrap(new WorkflowLlmInvocationCompletedEvent
            {
                SessionId = intent.SessionId,
                Success = true,
                Content = "score: 2 / 5",
            }),
            ctx,
            CancellationToken.None);

        var completion = ctx.Published.Select(x => x.Event).OfType<StepCompletedEvent>().Single();
        completion.Success.Should().BeTrue();
        completion.Output.Should().BeEmpty();
        completion.BranchKey.Should().Be("revise");
        completion.Annotations["evaluate.score"].Should().Be("2.0");
        completion.Annotations["evaluate.passed"].Should().Be(bool.FalseString);
    }

    [Fact]
    public async Task LlmCallModule_ShouldCopyTypedAgentToolScopeToIntent()
    {
        var module = new LLMCallModule();
        var ctx = new RecordingWorkflowContext();

        await module.HandleAsync(
            Wrap(new StepRequestEvent
            {
                StepId = "llm-tool-scope",
                StepType = "llm_call",
                RunId = "run-llm-tool-scope",
                Input = "draft",
                StepParameters = new WorkflowStepParameters
                {
                    AgentToolScope = new WorkflowAgentToolScope
                    {
                        RestrictAllowedToolNames = true,
                        RestrictToolSets = true,
                        AllowedToolNames = { "search" },
                        ToolSetRefs = { "nyxid.connected_services" },
                    },
                },
            }),
            ctx,
            CancellationToken.None);

        var intent = DispatchedLlmIntent(ctx);
        intent.AgentToolScope.Should().NotBeNull();
        intent.AgentToolScope.AllowedToolNames.Should().Equal("search");
        intent.AgentToolScope.ToolSetRefs.Should().Equal("nyxid.connected_services");
        intent.AgentToolScope.RestrictAllowedToolNames.Should().BeTrue();
        intent.AgentToolScope.RestrictToolSets.Should().BeTrue();
        intent.Annotations.Should().NotContainKey("allowed_tools");
    }

    [Fact]
    public async Task LlmCallModule_WithNoAgentToolScope_ShouldLeaveIntentUnrestricted()
    {
        var module = new LLMCallModule();
        var ctx = new RecordingWorkflowContext();

        await module.HandleAsync(
            Wrap(new StepRequestEvent
            {
                StepId = "llm-unrestricted",
                StepType = "llm_call",
                RunId = "run-llm-unrestricted",
                Input = "draft",
            }),
            ctx,
            CancellationToken.None);

        DispatchedLlmIntent(ctx).AgentToolScope.Should().BeNull();
    }

    [Fact]
    public async Task EvaluateModule_ShouldPublishZeroScorePass_WhenContentHasNoNumberAndThresholdIsZero()
    {
        var module = new EvaluateModule();
        var ctx = new RecordingWorkflowContext();

        await module.HandleAsync(
            Wrap(new StepRequestEvent
            {
                StepId = "evaluate-zero",
                StepType = "evaluate",
                RunId = "run-evaluate-zero",
                Input = "draft",
                Parameters = { ["threshold"] = "0" },
            }),
            ctx,
            CancellationToken.None);
        var intent = ctx.Published.Select(x => x.Event).OfType<WorkflowLlmExecutionIntent>().Single();

        await module.HandleAsync(
            Wrap(new WorkflowLlmInvocationCompletedEvent
            {
                SessionId = intent.SessionId,
                Success = true,
                Content = "no numeric verdict",
            }),
            ctx,
            CancellationToken.None);

        var completion = ctx.Published.Select(x => x.Event).OfType<StepCompletedEvent>().Single();
        completion.Success.Should().BeTrue();
        completion.BranchKey.Should().BeEmpty();
        completion.Annotations["evaluate.score"].Should().Be("0.0");
        completion.Annotations["evaluate.passed"].Should().Be(bool.TrueString);
    }

    [Fact]
    public async Task ReflectModule_ShouldPublishDeterministicFailure_WhenStepIdMissing()
    {
        var module = new ReflectModule();
        var ctx = new RecordingWorkflowContext();

        await module.HandleAsync(
            Wrap(new StepRequestEvent
            {
                StepId = "",
                StepType = "reflect",
                RunId = "run-reflect-invalid",
                Input = "draft",
            }),
            ctx,
            CancellationToken.None);

        var failure = ctx.Published.Select(x => x.Event).OfType<StepCompletedEvent>().Single();
        failure.Success.Should().BeFalse();
        failure.StepId.Should().BeEmpty();
        failure.Error.Should().Contain("requires non-empty step_id");
    }

    [Fact]
    public async Task ReflectModule_ShouldIgnoreUnsupportedAndUncorrelatedCompletionBranches()
    {
        var module = new ReflectModule();
        var ctx = new RecordingWorkflowContext();

        await module.HandleAsync(new EventEnvelope(), ctx, CancellationToken.None);
        await module.HandleAsync(
            Wrap(new StepRequestEvent
            {
                StepId = "transform-step",
                StepType = "transform",
                RunId = "run-reflect-ignore",
            }),
            ctx,
            CancellationToken.None);
        await module.HandleAsync(
            Wrap(new WorkflowLlmInvocationCompletedEvent
            {
                SessionId = "",
                Success = true,
                Content = "PASS",
            }),
            ctx,
            CancellationToken.None);
        await module.HandleAsync(
            Wrap(new WorkflowLlmInvocationCompletedEvent
            {
                SessionId = "missing-session",
                Success = true,
                Content = "PASS",
            }),
            ctx,
            CancellationToken.None);

        ctx.Published.Should().BeEmpty();
        ctx.Sent.Should().BeEmpty();
    }

    [Fact]
    public async Task ReflectModule_ShouldPublishDefaultLlmFailure_WhenCompletionFailsWithoutError()
    {
        var module = new ReflectModule();
        var ctx = new RecordingWorkflowContext();

        await module.HandleAsync(
            Wrap(new StepRequestEvent
            {
                StepId = "reflect-failure",
                StepType = "reflect",
                RunId = "run-reflect-failure",
                Input = "draft",
                TargetRole = "reviewer",
            }),
            ctx,
            CancellationToken.None);
        var intent = ctx.Sent.Select(x => x.Event).OfType<WorkflowLlmExecutionIntent>().Single();

        await module.HandleAsync(
            Wrap(new WorkflowLlmInvocationCompletedEvent
            {
                SessionId = intent.SessionId,
                Success = false,
                Error = " ",
            }),
            ctx,
            CancellationToken.None);

        var failure = ctx.Published.Select(x => x.Event).OfType<StepCompletedEvent>().Single();
        failure.StepId.Should().Be("reflect-failure");
        failure.RunId.Should().Be("run-reflect-failure");
        failure.Success.Should().BeFalse();
        failure.Error.Should().Be("reflect LLM call failed.");
    }

    [Fact]
    public async Task ReflectModule_ShouldPreserveScheduleIdOnCritiqueAndImproveIntents()
    {
        var module = new ReflectModule();
        var ctx = new RecordingWorkflowContext
        {
            ScheduleId = " schedule-reflect ",
        };

        await module.HandleAsync(
            Wrap(new StepRequestEvent
            {
                StepId = "reflect-schedule",
                StepType = "reflect",
                RunId = "run-reflect-schedule",
                Input = "draft",
            }),
            ctx,
            CancellationToken.None);

        var critique = ctx.Published.Select(x => x.Event).OfType<WorkflowLlmExecutionIntent>().Single();
        critique.ScheduleId.Should().Be("schedule-reflect");

        await module.HandleAsync(
            Wrap(new WorkflowLlmInvocationCompletedEvent
            {
                SessionId = critique.SessionId,
                Success = true,
                Content = "needs work",
            }),
            ctx,
            CancellationToken.None);

        var improve = ctx.Published.Select(x => x.Event).OfType<WorkflowLlmExecutionIntent>().Last();
        improve.SessionId.Should().Contain("_improve");
        improve.ScheduleId.Should().Be("schedule-reflect");
    }

    [Fact]
    public async Task ReflectModule_ShouldPublishDispatchFailure_WhenRoleSendFails()
    {
        var module = new ReflectModule();
        var ctx = new RecordingWorkflowContext
        {
            SendToException = new InvalidOperationException("role inbox unavailable"),
        };

        await module.HandleAsync(
            Wrap(new StepRequestEvent
            {
                StepId = "reflect-dispatch-failure",
                StepType = "reflect",
                RunId = "run-reflect-dispatch-failure",
                Input = "draft",
                TargetRole = "reviewer",
            }),
            ctx,
            CancellationToken.None);

        var failure = ctx.Published.Select(x => x.Event).OfType<StepCompletedEvent>().Single();
        failure.StepId.Should().Be("reflect-dispatch-failure");
        failure.Success.Should().BeFalse();
        failure.Error.Should().Contain("reflect dispatch failed");
        failure.Error.Should().Contain("role inbox unavailable");
    }

    [Fact]
    public async Task ReflectModule_ShouldClampRoundsTrimParametersAndUseDefaultRunSessionId()
    {
        var module = new ReflectModule();
        var ctx = new RecordingWorkflowContext();

        await module.HandleAsync(
            Wrap(new StepRequestEvent
            {
                StepId = "reflect-trim",
                StepType = "reflect",
                RunId = " ",
                Parameters =
                {
                    ["max_rounds"] = "99",
                    [" criteria "] = " correctness ",
                    [" trace "] = " abc ",
                    ["empty"] = " ",
                },
            }),
            ctx,
            CancellationToken.None);

        var intent = ctx.Published.Select(x => x.Event).OfType<WorkflowLlmExecutionIntent>().Single();
        intent.RunId.Should().Be("default");
        intent.StepId.Should().Be("reflect-trim");
        intent.SessionId.Should().Be("agent-1:default:reflect-trim_r0_critique:a1");
        intent.Annotations.Should().ContainKey("trace").WhoseValue.Should().Be("abc");
        intent.Annotations.Should().NotContainKey("empty");
        intent.Prompt.Should().Contain("correctness");
        ctx.RuntimeContext.ApplySenderNyxIdAccessToken(" sender-token-reflect ");

        await module.HandleAsync(
            Wrap(new WorkflowLlmInvocationCompletedEvent
            {
                SessionId = intent.SessionId,
                Success = true,
                Content = "still needs work",
            }),
            ctx,
            CancellationToken.None);

        var improve = ctx.Published.Select(x => x.Event).OfType<WorkflowLlmExecutionIntent>().Last();
        improve.SessionId.Should().Be("agent-1:default:reflect-trim_r1_improve:a1");
        improve.SenderNyxIdAccessToken.Should().Be("sender-token-reflect");
        ctx.Published.Select(x => x.Event).OfType<StepCompletedEvent>().Should().BeEmpty();
    }

    [Fact]
    public async Task ReflectModule_ShouldPublishImproveDispatchFailure()
    {
        var module = new ReflectModule();
        var ctx = new RecordingWorkflowContext();

        await module.HandleAsync(
            Wrap(new StepRequestEvent
            {
                StepId = "reflect-improve-fails",
                StepType = "reflect",
                RunId = "run-reflect-improve-fails",
                Input = "draft",
                TargetRole = "reviewer",
            }),
            ctx,
            CancellationToken.None);
        var critique = ctx.Sent.Select(x => x.Event).OfType<WorkflowLlmExecutionIntent>().Single();
        ctx.SendToException = new InvalidOperationException("improve inbox unavailable");

        await module.HandleAsync(
            Wrap(new WorkflowLlmInvocationCompletedEvent
            {
                SessionId = critique.SessionId,
                Success = true,
                Content = "needs work",
            }),
            ctx,
            CancellationToken.None);

        var failure = ctx.Published.Select(x => x.Event).OfType<StepCompletedEvent>().Single();
        failure.Success.Should().BeFalse();
        failure.Error.Should().Contain("reflect improve dispatch failed");
        failure.Error.Should().Contain("improve inbox unavailable");
    }

    [Fact]
    public async Task ReflectModule_ShouldPublishCritiqueDispatchFailureAfterImproveCompletion()
    {
        var module = new ReflectModule();
        var ctx = new RecordingWorkflowContext();

        await module.HandleAsync(
            Wrap(new StepRequestEvent
            {
                StepId = "reflect-critique-fails",
                StepType = "reflect",
                RunId = "run-reflect-critique-fails",
                Input = "draft",
                TargetRole = "reviewer",
            }),
            ctx,
            CancellationToken.None);
        var critique = ctx.Sent.Select(x => x.Event).OfType<WorkflowLlmExecutionIntent>().Single();

        await module.HandleAsync(
            Wrap(new WorkflowLlmInvocationCompletedEvent
            {
                SessionId = critique.SessionId,
                Success = true,
                Content = "needs work",
            }),
            ctx,
            CancellationToken.None);
        var improve = ctx.Sent.Select(x => x.Event).OfType<WorkflowLlmExecutionIntent>().Last();
        ctx.SendToException = new InvalidOperationException("critique inbox unavailable");

        await module.HandleAsync(
            Wrap(new WorkflowLlmInvocationCompletedEvent
            {
                SessionId = improve.SessionId,
                Success = true,
                Content = "improved draft",
            }),
            ctx,
            CancellationToken.None);

        var failure = ctx.Published.Select(x => x.Event).OfType<StepCompletedEvent>().Single();
        failure.Success.Should().BeFalse();
        failure.Error.Should().Contain("reflect critique dispatch failed");
        failure.Error.Should().Contain("critique inbox unavailable");
    }

    [Fact]
    public async Task DynamicWorkflowModule_ShouldIgnoreUnsupportedPayload_AndValidateYamlBlocks()
    {
        var module = new DynamicWorkflowModule();
        var ctx = new RecordingWorkflowContext();

        await module.HandleAsync(
            new EventEnvelope { Payload = Any.Pack(new WorkflowCompletedEvent()) },
            ctx,
            CancellationToken.None);
        await module.HandleAsync(
            Wrap(new StepRequestEvent
            {
                StepId = "step-x",
                RunId = "run-x",
                StepType = "transform",
            }),
            ctx,
            CancellationToken.None);
        ctx.Published.Should().BeEmpty();

        await module.HandleAsync(
            Wrap(new StepRequestEvent
            {
                StepId = "step-x",
                RunId = "run-x",
                StepType = "dynamic_workflow",
                Input = "no fenced yaml here",
            }),
            ctx,
            CancellationToken.None);
        ctx.Published.Select(x => x.Event).OfType<StepCompletedEvent>().Single().Error
            .Should().Contain("No workflow YAML found");

        ctx.Published.Clear();
        await module.HandleAsync(
            Wrap(new StepRequestEvent
            {
                StepId = "step-x",
                RunId = "run-x",
                StepType = "dynamic_workflow",
                Input =
                    """
                    ```yaml
                    name: bad
                    roles: []
                    steps:
                      - id: broken
                        type: unknown_step
                    ```
                    """,
            }),
            ctx,
            CancellationToken.None);
        ctx.Published.Select(x => x.Event).OfType<StepCompletedEvent>().Single().Error
            .Should().Contain("Invalid workflow YAML");
    }

    [Fact]
    public async Task DynamicWorkflowModule_ShouldPublishReplaceEventForValidYaml()
    {
        var module = new DynamicWorkflowModule();
        var ctx = new RecordingWorkflowContext();

        await module.HandleAsync(
            Wrap(new StepRequestEvent
            {
                StepId = "step-y",
                RunId = "run-y",
                StepType = "dynamic_workflow",
                Input =
                    """
                    preface
                    ```yaml
                    name: wf-a
                    roles: []
                    steps:
                      - id: s1
                        type: transform
                    ```
                    trailing
                    ```yaml
                    name: wf-b
                    roles: []
                    steps:
                      - id: s2
                        type: transform
                    ```
                    """,
                Parameters = { ["original_input"] = "hello" },
            }),
            ctx,
            CancellationToken.None);

        var replace = ctx.Published.Select(x => x.Event).OfType<ReplaceWorkflowDefinitionAndExecuteEvent>().Single();
        replace.Input.Should().Be("hello");
        replace.WorkflowYaml.Should().Contain("name: wf-b");
        DynamicWorkflowModule.ExtractYaml(" ").Should().BeNull();
    }

    [Fact]
    public void DynamicWorkflowModule_ValidateWorkflowYaml_ShouldExpandKnownTypesFromFactory()
    {
        var ctx = new RecordingWorkflowContext(new TestEventModuleFactory("custom_executor"));

        var errors = DynamicWorkflowModule.ValidateWorkflowYaml(
            """
            name: wf-custom
            roles: []
            steps:
              - id: s1
                type: custom_executor
            """,
            ctx);

        errors.Should().BeEmpty();
    }

    [Fact]
    public void DynamicWorkflowModule_ValidateWorkflowYaml_ShouldRejectExcessiveNesting()
    {
        var ctx = new RecordingWorkflowContext();
        var yaml = WorkflowYamlResourceGuardTests.BuildNestedWorkflow(childLinks: 31);

        var errors = DynamicWorkflowModule.ValidateWorkflowYaml(yaml, ctx);

        errors.Should().ContainSingle()
            .Which.Should().ContainAll("YAML parse failed", "nesting depth");
    }

    [Fact]
    public void DynamicWorkflowModule_ValidateWorkflowYaml_ShouldRejectCollectionAliasCycle()
    {
        var ctx = new RecordingWorkflowContext();
        const string yaml = """
                            name: cyclic
                            roles: []
                            steps: &steps
                              - id: loop
                                type: assign
                                children: *steps
                            """;

        var errors = DynamicWorkflowModule.ValidateWorkflowYaml(yaml, ctx);

        errors.Should().ContainSingle()
            .Which.Should().ContainAll("YAML parse failed", "nesting depth");
    }

    private static EventEnvelope Wrap(IMessage evt, EnvelopeCallbackContext? callback = null)
    {
        return new EventEnvelope
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
            Payload = Any.Pack(evt),
            Route = EnvelopeRouteSemantics.CreateTopologyPublication("test", TopologyAudience.Self),
            Runtime = callback == null
                ? null
                : new EnvelopeRuntime
                {
                    Callback = callback.Clone(),
                },
        };
    }

    private static WorkflowLlmExecutionIntent DispatchedLlmIntent(RecordingWorkflowContext ctx) =>
        ctx.Published.Select(x => x.Event)
            .Concat(ctx.Sent.Select(x => x.Event))
            .OfType<WorkflowLlmExecutionIntent>()
            .Single();

    private static WorkflowFileRef BuildWorkflowFileRef(string fileId) =>
        new()
        {
            FileId = fileId,
            ArtifactId = $"workflow-file://{fileId}",
            SourceKind = WorkflowFileSourceKind.ConnectedServiceResource,
            SourceMessageId = "om_1",
            SourceResourceKey = "image_key_1",
            FileName = $"{fileId}.png",
            MediaType = "image/png",
            SizeBytes = 3,
            Sha256 = $"sha-{fileId}",
            CreatedAtUnixMs = 1710000000000,
            ExpiresAtUnixMs = 1710003600000,
        };

    private static WorkflowCallerNyxIdAuthority CreateCallerAuthority() =>
        new()
        {
            Platform = "nyxid",
            Tenant = "tenant-1",
            ExternalUserId = "m-alpha",
            Scope = "invoke",
        };

    private static EnvelopeCallbackContext MetadataFor(
        RecordedCallback callback,
        long? generation = null) =>
        new()
        {
            CallbackId = callback.CallbackId,
            Generation = generation ?? callback.Generation,
            FireIndex = 0,
            FiredAtUnixTimeMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        };

    private sealed class RecordingWorkflowContext
        : IWorkflowExecutionContext, IWorkflowExecutionRuntimeContextAccessor, IWorkflowExecutionStateHost
    {
        private readonly Dictionary<string, Any> _states = new(StringComparer.Ordinal);
        private readonly Dictionary<string, long> _callbackGenerations = new(StringComparer.Ordinal);
        private readonly IServiceProvider _services;

        public RecordingWorkflowContext(IEventModuleFactory<IWorkflowExecutionContext>? moduleFactory = null)
        {
            _services = new TestServiceProvider(moduleFactory);
        }

        public EventEnvelope InboundEnvelope { get; } = new()
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
        };

        public string AgentId => "agent-1";

        public string RunId => "run-1";

        public string ScheduleId { get; init; } = string.Empty;

        public IServiceProvider Services => _services;

        public ILogger Logger { get; } = NullLogger.Instance;

        public WorkflowExecutionRuntimeContext RuntimeContext { get; } = new();

        public WorkflowRunExecutionContextState ExecutionContextState { get; } = new();

        public WorkflowRunExecutionContextState ExecutionContextSnapshot => ExecutionContextState.Clone();

        public List<(IMessage Event, TopologyAudience Direction)> Published { get; } = [];

        public List<(string TargetActorId, IMessage Event)> Sent { get; } = [];

        public Exception? SendToException { get; set; }

        public List<RecordedCallback> Scheduled { get; } = [];

        public List<RuntimeCallbackLease> Canceled { get; } = [];

        public TState LoadState<TState>(string scopeKey)
            where TState : class, IMessage<TState>, new()
        {
            if (!_states.TryGetValue(scopeKey, out var packed) || !packed.Is(new TState().Descriptor))
                return new TState();

            return packed.Unpack<TState>() ?? new TState();
        }

        public IReadOnlyList<KeyValuePair<string, TState>> LoadStates<TState>(string scopeKeyPrefix = "")
            where TState : class, IMessage<TState>, new() =>
            _states
                .Where(x => string.IsNullOrEmpty(scopeKeyPrefix) || x.Key.StartsWith(scopeKeyPrefix, StringComparison.Ordinal))
                .Where(x => x.Value.Is(new TState().Descriptor))
                .Select(x => new KeyValuePair<string, TState>(x.Key, x.Value.Unpack<TState>() ?? new TState()))
                .ToList();

        public Task SaveStateAsync<TState>(string scopeKey, TState state, CancellationToken ct = default)
            where TState : class, IMessage<TState>
        {
            ct.ThrowIfCancellationRequested();
            _states[scopeKey] = Any.Pack(state);
            return Task.CompletedTask;
        }

        public Task ClearStateAsync(string scopeKey, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            _states.Remove(scopeKey);
            return Task.CompletedTask;
        }

        public Any? GetExecutionState(string scopeKey) =>
            _states.GetValueOrDefault(scopeKey);

        public IReadOnlyList<KeyValuePair<string, Any>> GetExecutionStates() =>
            _states.ToList();

        public Task UpsertExecutionStateAsync(string scopeKey, Any state, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            _states[scopeKey] = state;
            return Task.CompletedTask;
        }

        public Task ClearExecutionStateAsync(string scopeKey, CancellationToken ct = default) =>
            ClearStateAsync(scopeKey, ct);

        Task<WorkflowCompensationTransitionResult> IWorkflowExecutionStateHost.TryStartCompensationAsync(
            WorkflowCompletedEvent terminalFailure,
            StepCompletedEvent? terminalStep,
            CancellationToken ct)
        {
            _ = terminalFailure;
            _ = terminalStep;
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(NoCompensableLedger());
        }

        Task IWorkflowExecutionStateHost.RecordCompensableStepDispatchAsync(
            CompensableStepDispatchedEvent evt,
            CancellationToken ct)
        {
            _ = evt;
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public Task<WorkflowCompensationTransitionResult> RecordCompensationStepCompletionAsync(
            CompensationStepCompletedEvent completion,
            CancellationToken ct = default)
        {
            _ = completion;
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(NoCompensableLedger());
        }

        public Task<WorkflowCompensationTransitionResult> RecordCompensationPhaseDeadlineExceededAsync(
            string runId,
            string error,
            CancellationToken ct = default)
        {
            _ = runId;
            _ = error;
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(NoCompensableLedger());
        }

        private static WorkflowCompensationTransitionResult NoCompensableLedger() =>
            new(
                WorkflowCompensationTransitionStatus.NoCompensableLedger,
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty);

        public Task UpdateExecutionContextAsync(
            WorkflowRunExecutionContextDelta delta,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            if (delta.ClearLlm)
                ExecutionContextState.Llm = null;
            if (delta.ClearCallerCredential)
                ExecutionContextState.CallerCredential = null;
            if (delta.ClearWorkflowRuntime)
                ExecutionContextState.WorkflowRuntime = null;
            if (delta.Llm != null)
            {
                ExecutionContextState.Llm = new WorkflowLlmExecutionContextState
                {
                    ModelOverride = delta.Llm.ModelOverride,
                    UserMemoryPrompt = delta.Llm.UserMemoryPrompt,
                    RoutePreference = delta.Llm.RoutePreference,
                };
                if (delta.Llm.HasMaxToolRoundsOverride)
                    ExecutionContextState.Llm.MaxToolRoundsOverride = delta.Llm.MaxToolRoundsOverride;
            }

            if (delta.CallerCredential != null)
            {
                ExecutionContextState.CallerCredential = new WorkflowCallerCredentialState
                {
                    BearerToken = delta.CallerCredential.BearerToken,
                };
            }

            if (delta.WorkflowRuntime != null)
            {
                ExecutionContextState.WorkflowRuntime = new WorkflowToolRuntimeContextState
                {
                    ParentActorId = delta.WorkflowRuntime.ParentActorId,
                    ParentRunId = delta.WorkflowRuntime.ParentRunId,
                    ParentStepId = delta.WorkflowRuntime.ParentStepId,
                    RootRunId = delta.WorkflowRuntime.RootRunId,
                    Depth = delta.WorkflowRuntime.Depth,
                };
            }

            return Task.CompletedTask;
        }

        public Task ClearExecutionContextAsync(CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            ExecutionContextState.Llm = null;
            ExecutionContextState.CallerCredential = null;
            ExecutionContextState.WorkflowRuntime = null;
            return Task.CompletedTask;
        }

        public Task PublishAsync<TEvent>(
            TEvent evt,
            TopologyAudience direction = TopologyAudience.Children,
            CancellationToken ct = default,
            EventEnvelopePublishOptions? options = null)
            where TEvent : IMessage
        {
            ct.ThrowIfCancellationRequested();
            Published.Add((evt, direction));
            return Task.CompletedTask;
        }

        public Task SendToAsync<TEvent>(string targetActorId, TEvent evt, CancellationToken ct = default,
            EventEnvelopePublishOptions? options = null)
            where TEvent : IMessage
        {
            ct.ThrowIfCancellationRequested();
            _ = options;
            if (SendToException != null)
                throw SendToException;

            Sent.Add((targetActorId, evt));
            return Task.CompletedTask;
        }

        public Task<RuntimeCallbackLease> ScheduleSelfDurableTimeoutAsync(
            string callbackId,
            TimeSpan dueTime,
            IMessage evt,
            EventEnvelopePublishOptions? options = null,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            _ = dueTime;
            _ = options;
            var generation = _callbackGenerations.GetValueOrDefault(callbackId, 0) + 1;
            _callbackGenerations[callbackId] = generation;
            Scheduled.Add(new RecordedCallback(callbackId, generation, evt));
            return Task.FromResult(new RuntimeCallbackLease(AgentId, callbackId, generation, RuntimeCallbackBackend.InMemory));
        }

        public Task CancelDurableCallbackAsync(RuntimeCallbackLease lease, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Canceled.Add(lease);
            return Task.CompletedTask;
        }
    }

    private sealed class TestServiceProvider : IServiceProvider
    {
        private readonly IReadOnlyList<IWorkflowModulePack> _modulePacks = [new WorkflowCoreModulePack()];
        private readonly IEventModuleFactory<IWorkflowExecutionContext>? _moduleFactory;

        public TestServiceProvider(IEventModuleFactory<IWorkflowExecutionContext>? moduleFactory)
        {
            _moduleFactory = moduleFactory;
        }

        public object? GetService(global::System.Type serviceType)
        {
            if (serviceType == typeof(IEnumerable<IWorkflowModulePack>))
                return _modulePacks;
            if (serviceType == typeof(IEventModuleFactory<IWorkflowExecutionContext>))
                return _moduleFactory;

            return null;
        }
    }

    private sealed class RotatingCallerAccessTokenProvider : IWorkflowCallerAccessTokenProvider
    {
        public List<WorkflowCallerNyxIdAuthority> Authorities { get; } = [];

        public Task<string> IssueAsync(WorkflowCallerNyxIdAuthority authority, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Authorities.Add(authority.Clone());
            return Task.FromResult($"token-{Authorities.Count}");
        }
    }

    private sealed class TestEventModuleFactory(string supportedName) : IEventModuleFactory<IWorkflowExecutionContext>
    {
        public bool TryCreate(string name, out IEventModule<IWorkflowExecutionContext>? module)
        {
            module = null;
            return string.Equals(name, supportedName, StringComparison.OrdinalIgnoreCase);
        }
    }

    private sealed record RecordedCallback(
        string CallbackId,
        long Generation,
        IMessage Event);
}
