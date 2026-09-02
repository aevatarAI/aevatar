using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.EventModules;
using Aevatar.Foundation.Abstractions.Interactions;
using Aevatar.Foundation.Abstractions.Runtime.Callbacks;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Core.Execution;
using Aevatar.Workflow.Core.Primitives;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.Workflow.Core.Tests.Execution;

public sealed class IdempotentStepExecutionTests
{
    private static WorkflowDefinition SingleStepWorkflow(string stepId = "step-1") => new()
    {
        Name = "test-workflow",
        Roles = [new RoleDefinition { Id = "worker", Name = "Worker" }],
        Steps = [new StepDefinition { Id = stepId, Type = "llm_call", TargetRole = "worker" }],
    };

    private static WorkflowDefinition ThreeStepWorkflow() => new()
    {
        Name = "test-resume-workflow",
        Roles = [new RoleDefinition { Id = "worker", Name = "Worker" }],
        Steps =
        [
            new StepDefinition { Id = "step-a", Type = "transform" },
            new StepDefinition
            {
                Id = "step-b",
                Type = "transform",
                Parameters =
                {
                    ["summary"] = "${concat(step_a_output, ':', topic, ':', input)}",
                    ["topic_value"] = "${topic}",
                },
            },
            new StepDefinition { Id = "step-c", Type = "transform" },
        ],
    };

    private static EventEnvelope Wrap(IMessage msg) => new()
    {
        Id = Guid.NewGuid().ToString("N"),
        Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
        Payload = Any.Pack(msg),
    };

    [Fact]
    public async Task StepRequest_ShouldContainExecutionId()
    {
        var ctx = new RecordingEventHandlerContext();
        var host = new RecordingStateHost();
        var kernel = new WorkflowExecutionKernel(SingleStepWorkflow(), host);

        await kernel.HandleAsync(
            Wrap(new StartWorkflowEvent { RunId = "run-1", Input = "hello" }),
            ctx, CancellationToken.None);

        var request = ctx.Published
            .Select(p => p.Event)
            .Where(e => e.Is(StepRequestEvent.Descriptor))
            .Select(e => e.Unpack<StepRequestEvent>())
            .FirstOrDefault(r => r.StepId == "step-1");

        request.Should().NotBeNull();
        request!.ExecutionId.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task Dispatch_ShouldNotLogRawStepInputContent()
    {
        // Tool-call inputs routinely carry secrets (e.g. {"token":"<NyxID JWT>"}).
        // The dispatch log previously previewed up to 200 chars of raw input, which
        // leaked partial credentials into stdout -> Elasticsearch. Regression guard:
        // the dispatch line is still emitted, but the raw content never is.
        const string secret = "eyJhbGciOiJSENTINEL.secret-credential-payload-must-never-be-logged";
        var ctx = new RecordingEventHandlerContext();
        var host = new RecordingStateHost();
        var kernel = new WorkflowExecutionKernel(SingleStepWorkflow(), host);

        await kernel.HandleAsync(
            Wrap(new StartWorkflowEvent { RunId = "run-1", Input = secret }),
            ctx, CancellationToken.None);

        ctx.RecordingLogger.Messages.Should()
            .Contain(m => m.Contains("workflow_loop: dispatch") && m.Contains("input=("),
                "the dispatch log line must still be emitted with a length marker");
        ctx.RecordingLogger.Messages.Should()
            .NotContain(m => m.Contains(secret),
                "raw step input content may carry secrets and must never be logged");
    }

    [Fact]
    public async Task StepRequest_ShouldResolveAndPersistDefaultIdempotencyKeyBeforePublish()
    {
        var ctx = new RecordingEventHandlerContext();
        var host = new RecordingStateHost();
        var kernel = new WorkflowExecutionKernel(SingleStepWorkflow(), host);

        await kernel.HandleAsync(
            Wrap(new StartWorkflowEvent { RunId = "run-1", Input = "hello" }),
            ctx,
            CancellationToken.None);

        var request = StepRequests(ctx).Single();
        request.IdempotencyKey.Should().Be("run-1:step-1:1");

        var state = LoadKernelState(host);
        state.IdempotencyByStepId.Should().ContainKey("step-1");
        state.IdempotencyByStepId["step-1"].Should().BeEquivalentTo(new
        {
            LogicalRunId = "run-1",
            StepId = "step-1",
            LogicalAttempt = 1,
            IdempotencyKey = "run-1:step-1:1",
        });
    }

    [Fact]
    public async Task StepRequest_ShouldUseAuthorExpressionOverDefaultAndEvaluateVariables()
    {
        var workflow = new WorkflowDefinition
        {
            Name = "author-key-workflow",
            Roles = [new RoleDefinition { Id = "worker", Name = "Worker" }],
            Steps =
            [
                new StepDefinition
                {
                    Id = "charge",
                    Type = "tool_call",
                    TargetRole = "worker",
                    IdempotencyKey = "${concat(order_id, ':', step_id, ':', logical_attempt, ':', input)}",
                    Parameters = { ["tool"] = "charge" },
                },
            ],
        };
        var ctx = new RecordingEventHandlerContext();
        var host = new RecordingStateHost();
        var kernel = new WorkflowExecutionKernel(workflow, host);
        var start = new StartWorkflowEvent { RunId = "run-1", Input = "payload" };
        start.Parameters["order_id"] = "order-7";

        await kernel.HandleAsync(Wrap(start), ctx, CancellationToken.None);

        StepRequests(ctx).Single().IdempotencyKey.Should().Be("order-7:charge:1:payload");
        LoadKernelState(host).IdempotencyByStepId["charge"].IdempotencyKey.Should().Be("order-7:charge:1:payload");
    }

    [Fact]
    public async Task PendingDispatchReplay_ShouldRestorePersistedIdempotencyKey()
    {
        var workflow = new WorkflowDefinition
        {
            Name = "replay-workflow",
            Roles = [new RoleDefinition { Id = "worker", Name = "Worker" }],
            Steps =
            [
                new StepDefinition
                {
                    Id = "side_effect",
                    Type = "tool_call",
                    TargetRole = "worker",
                    IdempotencyKey = "${nonce}",
                    Parameters = { ["tool"] = "external" },
                },
            ],
        };
        var ctx = new RecordingEventHandlerContext();
        var host = new RecordingStateHost();
        var kernel = new WorkflowExecutionKernel(workflow, host);
        var start = new StartWorkflowEvent { RunId = "run-1", Input = "payload" };
        start.Parameters["nonce"] = "first-key";

        await kernel.HandleAsync(Wrap(start), ctx, CancellationToken.None);

        var state = LoadKernelState(host);
        var originalExecutionId = state.ExecutionIdsByStepId["side_effect"];
        state.CurrentStepDispatchPending = true;
        state.Variables["nonce"] = "changed-key";
        await host.UpsertExecutionStateAsync("workflow_execution_kernel", Any.Pack(state), CancellationToken.None);
        ctx.Published.Clear();

        await kernel.HandleAsync(Wrap(new StartWorkflowEvent { RunId = "run-1", Input = "payload" }), ctx, CancellationToken.None);

        var replay = StepRequests(ctx).Single();
        replay.IdempotencyKey.Should().Be("first-key");
        replay.ExecutionId.Should().Be(originalExecutionId);
    }

    [Fact]
    public async Task StartWorkflow_WithForkSeedIdempotency_ShouldReuseSourceFailedStepKey()
    {
        var ctx = new RecordingEventHandlerContext();
        var host = new RecordingStateHost();
        var kernel = new WorkflowExecutionKernel(ThreeStepWorkflow(), host);
        var start = new StartWorkflowEvent
        {
            RunId = "fork-run",
            Input = "fresh-input",
            ForkSeed = new WorkflowRunForkSeed
            {
                SourceRunId = "source-run",
                StartAtStepId = "step-b",
                StartStepIdempotency = new WorkflowStepIdempotencyState
                {
                    LogicalRunId = "source-run",
                    StepId = "step-b",
                    LogicalAttempt = 2,
                    IdempotencyKey = "source-run:step-b:2",
                },
            },
        };
        start.ForkSeed.Variables["input"] = "seed-input";

        await kernel.HandleAsync(Wrap(start), ctx, CancellationToken.None);

        var request = StepRequests(ctx).Single();
        request.IdempotencyKey.Should().Be("source-run:step-b:2");
        var persisted = LoadKernelState(host).IdempotencyByStepId["step-b"];
        persisted.LogicalRunId.Should().Be("source-run");
        persisted.LogicalAttempt.Should().Be(2);
    }

    [Fact]
    public async Task CompensationRequest_ShouldPersistCarriedIdempotencyKeyForCrashReplay()
    {
        var workflow = new WorkflowDefinition
        {
            Name = "compensation-workflow",
            Roles = [new RoleDefinition { Id = "worker", Name = "Worker" }],
            Steps =
            [
                new StepDefinition
                {
                    Id = "cancel",
                    Type = "tool_call",
                    Parameters = { ["tool"] = "cancel_order" },
                },
            ],
        };
        var ctx = new RecordingEventHandlerContext();
        var host = new RecordingStateHost();
        var kernel = new WorkflowExecutionKernel(workflow, host);

        await kernel.HandleAsync(Wrap(new StartWorkflowEvent { RunId = "run-1", Input = "start" }), ctx, CancellationToken.None);
        var state = LoadKernelState(host);
        state.CurrentStepDispatchPending = false;
        state.ExecutionIdsByStepId.Clear();
        state.IdempotencyByStepId.Clear();
        await host.UpsertExecutionStateAsync("workflow_execution_kernel", Any.Pack(state), CancellationToken.None);
        ctx.Published.Clear();

        await kernel.HandleAsync(
            Wrap(new CompensationRequestEvent
            {
                RunId = "run-1",
                FailedStepId = "charge",
                CompensationStepId = "cancel",
                IdempotencyKey = "compensate:charge:1",
                CapturedOutput = "captured",
            }),
            ctx,
            CancellationToken.None);

        var first = StepRequests(ctx).Single();
        first.IdempotencyKey.Should().Be("compensate:charge:1");
        first.Input.Should().Be("captured");
        var persistedState = LoadKernelState(host);
        persistedState.IdempotencyByStepId["cancel"].IdempotencyKey.Should().Be("compensate:charge:1");
        persistedState.CurrentStepDispatchPending = true;
        persistedState.Variables["input"] = "changed";
        await host.UpsertExecutionStateAsync("workflow_execution_kernel", Any.Pack(persistedState), CancellationToken.None);
        ctx.Published.Clear();

        await kernel.HandleAsync(
            Wrap(new CompensationRequestEvent
            {
                RunId = "run-1",
                FailedStepId = "charge",
                CompensationStepId = "cancel",
                IdempotencyKey = "different",
                CapturedOutput = "other",
            }),
            ctx,
            CancellationToken.None);

        StepRequests(ctx).Single().IdempotencyKey.Should().Be("compensate:charge:1");
    }

    [Fact]
    public async Task DuplicateWorkflowCallStart_WithSameRunAndInvocation_ShouldNotPublishAlreadyActiveFailure()
    {
        var ctx = new RecordingEventHandlerContext();
        var host = new RecordingStateHost();
        var kernel = new WorkflowExecutionKernel(SingleStepWorkflow(), host);
        var start = new StartWorkflowEvent
        {
            RunId = "run-1",
            Input = "hello",
        };
        start.Parameters["workflow_call.invocation_id"] = "invoke-1";

        await kernel.HandleAsync(Wrap(start), ctx, CancellationToken.None);
        ctx.Published.Clear();

        await kernel.HandleAsync(Wrap(start.Clone()), ctx, CancellationToken.None);

        ctx.Published.Select(p => p.Event)
            .Where(e => e.Is(WorkflowCompletedEvent.Descriptor))
            .Should()
            .BeEmpty();
        ctx.Published.Select(p => p.Event)
            .Where(e => e.Is(StepRequestEvent.Descriptor))
            .Should()
            .BeEmpty();
    }

    [Fact]
    public async Task StartWorkflow_WithForkSeed_ShouldPersistHydratedSeedVariables()
    {
        var ctx = new RecordingEventHandlerContext();
        var host = new RecordingStateHost();
        var kernel = new WorkflowExecutionKernel(ThreeStepWorkflow(), host);
        var start = new StartWorkflowEvent
        {
            RunId = "run-1",
            Input = "fresh-input",
            ForkSeed = new WorkflowRunForkSeed
            {
                SourceRunId = "source-run",
                StartAtStepId = "step-b",
            },
        };
        start.ForkSeed.Variables["input"] = "seed-input";
        start.ForkSeed.Variables["step_a_output"] = "alpha";
        start.ForkSeed.Variables["topic"] = "seed-topic";

        await kernel.HandleAsync(Wrap(start), ctx, CancellationToken.None);

        var request = ctx.Published
            .Select(p => p.Event)
            .Where(e => e.Is(StepRequestEvent.Descriptor))
            .Select(e => e.Unpack<StepRequestEvent>())
            .Single();
        request.StepId.Should().Be("step-b");
        request.Input.Should().Be("seed-input");
        request.Parameters["summary"].Should().Be("alpha:seed-topic:seed-input");
        StepRequests(ctx).Should().NotContain(x => x.StepId == "step-a");

        var state = host.States["workflow_execution_kernel"].Unpack<WorkflowExecutionKernelState>();
        state.Variables["step_a_output"].Should().Be("alpha");
        state.Variables["input"].Should().Be("seed-input");
        state.Variables["topic"].Should().Be("seed-topic");
    }

    [Fact]
    public async Task StartWorkflow_WithForkSeedMissingStep_ShouldPublishFailureWithoutDispatch()
    {
        var ctx = new RecordingEventHandlerContext();
        var host = new RecordingStateHost();
        var kernel = new WorkflowExecutionKernel(ThreeStepWorkflow(), host);

        await kernel.HandleAsync(
            Wrap(new StartWorkflowEvent
            {
                RunId = "run-1",
                Input = "hello",
                ForkSeed = new WorkflowRunForkSeed
                {
                    SourceRunId = "source-run",
                    StartAtStepId = "missing-step",
                },
            }),
            ctx,
            CancellationToken.None);

        ctx.Published.Select(p => p.Event)
            .Where(e => e.Is(StepRequestEvent.Descriptor))
            .Should()
            .BeEmpty();
        var completed = ctx.Published.Select(p => p.Event)
            .Where(e => e.Is(WorkflowCompletedEvent.Descriptor))
            .Select(e => e.Unpack<WorkflowCompletedEvent>())
            .First(e => !e.Success && e.Error.Contains("missing-step", StringComparison.Ordinal));
        completed.Success.Should().BeFalse();
        completed.Error.Should().Contain("missing-step");
    }

    [Fact]
    public async Task StepCompleted_MatchingId_ShouldAccept()
    {
        var ctx = new RecordingEventHandlerContext();
        var host = new RecordingStateHost();
        var kernel = new WorkflowExecutionKernel(SingleStepWorkflow(), host);

        // Start workflow → dispatches step-1
        await kernel.HandleAsync(
            Wrap(new StartWorkflowEvent { RunId = "run-1", Input = "hello" }),
            ctx, CancellationToken.None);

        var executionId = ctx.Published
            .Select(p => p.Event)
            .Where(e => e.Is(StepRequestEvent.Descriptor))
            .Select(e => e.Unpack<StepRequestEvent>())
            .First(r => r.StepId == "step-1")
            .ExecutionId;

        ctx.Published.Clear();

        // Complete step-1 with matching execution_id → should be accepted
        await kernel.HandleAsync(
            Wrap(new StepCompletedEvent
            {
                StepId = "step-1",
                RunId = "run-1",
                Success = true,
                Output = "done",
                ExecutionId = executionId,
            }),
            ctx, CancellationToken.None);

        // Should NOT have published a StaleStepCompletionRejectedEvent
        ctx.Published.Select(p => p.Event)
            .Where(e => e.Is(StaleStepCompletionRejectedEvent.Descriptor))
            .Should().BeEmpty();

        // Should have published a WorkflowCompletedEvent (single-step workflow done)
        ctx.Published.Select(p => p.Event)
            .Where(e => e.Is(WorkflowCompletedEvent.Descriptor))
            .Should().NotBeEmpty();
    }

    [Fact]
    public async Task StepCompleted_MismatchId_ShouldReject()
    {
        var ctx = new RecordingEventHandlerContext();
        var host = new RecordingStateHost();
        var kernel = new WorkflowExecutionKernel(SingleStepWorkflow(), host);

        await kernel.HandleAsync(
            Wrap(new StartWorkflowEvent { RunId = "run-1", Input = "hello" }),
            ctx, CancellationToken.None);

        ctx.Published.Clear();

        // Complete step-1 with WRONG execution_id → should be rejected
        await kernel.HandleAsync(
            Wrap(new StepCompletedEvent
            {
                StepId = "step-1",
                RunId = "run-1",
                Success = true,
                Output = "stale",
                ExecutionId = "wrong-execution-id",
            }),
            ctx, CancellationToken.None);

        var rejection = ctx.Published.Select(p => p.Event)
            .Where(e => e.Is(StaleStepCompletionRejectedEvent.Descriptor))
            .Select(e => e.Unpack<StaleStepCompletionRejectedEvent>())
            .FirstOrDefault();

        rejection.Should().NotBeNull();
        rejection!.StepId.Should().Be("step-1");
        rejection.ReceivedExecutionId.Should().Be("wrong-execution-id");

        // Should NOT have completed the workflow
        ctx.Published.Select(p => p.Event)
            .Where(e => e.Is(WorkflowCompletedEvent.Descriptor))
            .Should().BeEmpty();
    }

    [Fact]
    public async Task StepCompleted_EmptyId_ShouldAcceptForBackwardsCompat()
    {
        var ctx = new RecordingEventHandlerContext();
        var host = new RecordingStateHost();
        var kernel = new WorkflowExecutionKernel(SingleStepWorkflow(), host);

        await kernel.HandleAsync(
            Wrap(new StartWorkflowEvent { RunId = "run-1", Input = "hello" }),
            ctx, CancellationToken.None);

        ctx.Published.Clear();

        // Complete step-1 with empty execution_id (backwards-compatible old worker)
        await kernel.HandleAsync(
            Wrap(new StepCompletedEvent
            {
                StepId = "step-1",
                RunId = "run-1",
                Success = true,
                Output = "done",
                ExecutionId = "",
            }),
            ctx, CancellationToken.None);

        // Should NOT reject — empty execution_id is backwards compatible
        ctx.Published.Select(p => p.Event)
            .Where(e => e.Is(StaleStepCompletionRejectedEvent.Descriptor))
            .Should().BeEmpty();

        // Should complete
        ctx.Published.Select(p => p.Event)
            .Where(e => e.Is(WorkflowCompletedEvent.Descriptor))
            .Should().NotBeEmpty();
    }

    [Fact]
    public async Task StaleCompletion_ShouldPublishRejectionEvent()
    {
        var ctx = new RecordingEventHandlerContext();
        var host = new RecordingStateHost();
        var kernel = new WorkflowExecutionKernel(SingleStepWorkflow(), host);

        await kernel.HandleAsync(
            Wrap(new StartWorkflowEvent { RunId = "run-1", Input = "hello" }),
            ctx, CancellationToken.None);

        var executionId = ctx.Published
            .Select(p => p.Event)
            .Where(e => e.Is(StepRequestEvent.Descriptor))
            .Select(e => e.Unpack<StepRequestEvent>())
            .First(r => r.StepId == "step-1")
            .ExecutionId;

        ctx.Published.Clear();

        // Send stale completion
        await kernel.HandleAsync(
            Wrap(new StepCompletedEvent
            {
                StepId = "step-1",
                RunId = "run-1",
                Success = true,
                Output = "stale",
                ExecutionId = "old-execution-id",
            }),
            ctx, CancellationToken.None);

        var rejection = ctx.Published.Select(p => p.Event)
            .Where(e => e.Is(StaleStepCompletionRejectedEvent.Descriptor))
            .Select(e => e.Unpack<StaleStepCompletionRejectedEvent>())
            .Single();

        rejection.StepId.Should().Be("step-1");
        rejection.ExpectedExecutionId.Should().Be(executionId);
        rejection.ReceivedExecutionId.Should().Be("old-execution-id");
    }

    [Fact]
    public async Task StepRetry_ShouldGenerateNewExecutionId()
    {
        // Workflow with retry policy
        var workflow = new WorkflowDefinition
        {
            Name = "test-retry",
            Roles = [new RoleDefinition { Id = "worker", Name = "Worker" }],
            Steps =
            [
                new StepDefinition
                {
                    Id = "step-1",
                    Type = "llm_call",
                    TargetRole = "worker",
                    Retry = new StepRetryPolicy { MaxAttempts = 3, Backoff = "fixed", DelayMs = 0 },
                },
            ],
        };

        var ctx = new RecordingEventHandlerContext();
        var host = new RecordingStateHost();
        var kernel = new WorkflowExecutionKernel(workflow, host);
        var start = new StartWorkflowEvent { RunId = "run-1", Input = "hello" };
        start.InputFileRefs.Add(BuildWorkflowFileRef("file-retry"));

        // Start workflow → first dispatch
        await kernel.HandleAsync(Wrap(start), ctx, CancellationToken.None);

        var firstRequest = ctx.Published
            .Select(p => p.Event)
            .Where(e => e.Is(StepRequestEvent.Descriptor))
            .Select(e => e.Unpack<StepRequestEvent>())
            .First(r => r.StepId == "step-1");
        var firstExecutionId = firstRequest.ExecutionId;
        firstRequest.InputFileRefs.Should().ContainSingle().Which.FileId.Should().Be("file-retry");

        ctx.Published.Clear();

        // Fail step-1 → triggers retry (delayMs=0 → immediate re-dispatch)
        await kernel.HandleAsync(
            Wrap(new StepCompletedEvent
            {
                StepId = "step-1",
                RunId = "run-1",
                Success = false,
                Error = "transient error",
                ExecutionId = firstExecutionId,
            }),
            ctx, CancellationToken.None);

        var secondRequest = ctx.Published
            .Select(p => p.Event)
            .Where(e => e.Is(StepRequestEvent.Descriptor))
            .Select(e => e.Unpack<StepRequestEvent>())
            .FirstOrDefault(r => r.StepId == "step-1");

        secondRequest.Should().NotBeNull("retry with delayMs=0 should immediately re-dispatch");
        secondRequest!.ExecutionId.Should().NotBeNullOrEmpty();
        secondRequest.ExecutionId.Should().NotBe(firstExecutionId, "retry must generate a new execution_id");
        secondRequest.IdempotencyKey.Should().Be("run-1:step-1:2");
        secondRequest.InputFileRefs.Should().ContainSingle().Which.FileId.Should().Be("file-retry");
    }

    [Fact]
    public async Task StepCompleted_WithUsage_ShouldMirrorRunAndStepUsageVariables()
    {
        var ctx = new RecordingEventHandlerContext();
        var host = new RecordingStateHost();
        var workflow = new WorkflowDefinition
        {
            Name = "usage-workflow",
            Roles = [new RoleDefinition { Id = "worker", Name = "Worker" }],
            Steps =
            [
                new StepDefinition { Id = "step-1", Type = "llm_call", TargetRole = "worker" },
                new StepDefinition { Id = "step-2", Type = "transform" },
            ],
        };
        var kernel = new WorkflowExecutionKernel(workflow, host);

        await kernel.HandleAsync(
            Wrap(new StartWorkflowEvent { RunId = "run-usage", Input = "hello" }),
            ctx,
            CancellationToken.None);

        var executionId = ctx.Published
            .Select(p => p.Event)
            .Where(e => e.Is(StepRequestEvent.Descriptor))
            .Select(e => e.Unpack<StepRequestEvent>())
            .First(r => r.StepId == "step-1")
            .ExecutionId;

        ctx.Published.Clear();

        await kernel.HandleAsync(
            Wrap(new StepCompletedEvent
            {
                StepId = "step-1",
                RunId = "run-usage",
                Success = true,
                Output = "done",
                ExecutionId = executionId,
                Usage = new WorkflowUsageMetrics
                {
                    PromptTokens = 10,
                    CompletionTokens = 15,
                    TotalTokens = 25,
                    Model = "gpt-5.4",
                    Cost = 0.42,
                    LatencyMs = 123,
                },
            }),
            ctx,
            CancellationToken.None);

        var state = host.GetExecutionState("workflow_execution_kernel")!.Unpack<WorkflowExecutionKernelState>();
        state.Variables["workflow.usage.prompt_tokens"].Should().Be("10");
        state.Variables["workflow.usage.completion_tokens"].Should().Be("15");
        state.Variables["workflow.usage.total_tokens"].Should().Be("25");
        state.Variables["workflow.usage.model"].Should().Be("gpt-5.4");
        state.Variables["workflow.usage.cost"].Should().Be("0.41999999999999998");
        state.Variables["workflow.usage.latency_ms"].Should().Be("123");
        state.Variables["steps.step-1.usage.total_tokens"].Should().Be("25");
        state.Variables["steps.step-1.usage.model"].Should().Be("gpt-5.4");
    }

    [Fact]
    public async Task StartWorkflow_WithForkSeed_ShouldDispatchSeedStartStepAndHydrateVariables()
    {
        var ctx = new RecordingEventHandlerContext();
        var host = new RecordingStateHost();
        var kernel = new WorkflowExecutionKernel(ThreeStepWorkflow(), host);
        var start = new StartWorkflowEvent
        {
            RunId = "run-resume",
            Input = "fresh-input",
            ForkSeed = new WorkflowRunForkSeed
            {
                SourceRunId = "run-source",
                StartAtStepId = "step-b",
            },
        };
        start.ForkSeed.Variables["input"] = "seed-input";
        start.ForkSeed.Variables["step_a_output"] = "alpha";
        start.ForkSeed.Variables["topic"] = "seed-topic";

        await kernel.HandleAsync(Wrap(start), ctx, CancellationToken.None);

        var requests = StepRequests(ctx);
        requests.Should().ContainSingle();
        requests[0].StepId.Should().Be("step-b");
        requests[0].Input.Should().Be("seed-input");
        requests[0].Parameters["summary"].Should().Be("alpha:seed-topic:seed-input");
        requests.Should().NotContain(x => x.StepId == "step-a");

        var state = LoadKernelState(host);
        state.CurrentStepId.Should().Be("step-b");
        state.CurrentStepInput.Should().Be("seed-input");
        state.Variables["step_a_output"].Should().Be("alpha");
        state.Variables["topic"].Should().Be("seed-topic");
    }

    [Fact]
    public async Task StartWorkflow_WithForkSeedMissingStartStep_ShouldPublishFailureWithoutDispatch()
    {
        var ctx = new RecordingEventHandlerContext();
        var host = new RecordingStateHost();
        var kernel = new WorkflowExecutionKernel(ThreeStepWorkflow(), host);
        var start = new StartWorkflowEvent
        {
            RunId = "run-resume",
            Input = "fresh-input",
            ForkSeed = new WorkflowRunForkSeed
            {
                SourceRunId = "run-source",
                StartAtStepId = "missing-step",
            },
        };
        start.ForkSeed.Variables["step_a_output"] = "alpha";

        await kernel.HandleAsync(Wrap(start), ctx, CancellationToken.None);

        StepRequests(ctx).Should().BeEmpty();
        var completions = WorkflowCompletions(ctx);
        completions.Should().ContainSingle();
        completions.Should().OnlyContain(x => !x.Success);
        completions.Should().OnlyContain(
            x => x.Error == "fork seed start step 'missing-step' was not found");
        host.GetExecutionState("workflow_execution_kernel").Should().BeNull();
    }

    [Fact]
    public async Task StartWorkflow_WithForkSeed_ShouldLetStartParametersOverrideSeedVariables()
    {
        var ctx = new RecordingEventHandlerContext();
        var host = new RecordingStateHost();
        var kernel = new WorkflowExecutionKernel(ThreeStepWorkflow(), host);
        var start = new StartWorkflowEvent
        {
            RunId = "run-resume",
            Input = "fresh-input",
            ForkSeed = new WorkflowRunForkSeed
            {
                SourceRunId = "run-source",
                StartAtStepId = "step-b",
            },
        };
        start.ForkSeed.Variables["input"] = "seed-input";
        start.ForkSeed.Variables["step_a_output"] = "alpha";
        start.ForkSeed.Variables["topic"] = "seed-topic";
        start.Parameters["topic"] = "parameter-topic";

        await kernel.HandleAsync(Wrap(start), ctx, CancellationToken.None);

        var request = StepRequests(ctx).Single();
        request.StepId.Should().Be("step-b");
        request.Input.Should().Be("seed-input");
        request.Parameters["topic_value"].Should().Be("parameter-topic");

        var state = LoadKernelState(host);
        state.Variables["topic"].Should().Be("parameter-topic");
        state.Variables["step_a_output"].Should().Be("alpha");
    }

    [Fact]
    public async Task StartWorkflow_WithoutForkSeed_ShouldDispatchFirstStepWithFreshVariables()
    {
        var ctx = new RecordingEventHandlerContext();
        var host = new RecordingStateHost();
        var kernel = new WorkflowExecutionKernel(ThreeStepWorkflow(), host);

        await kernel.HandleAsync(
            Wrap(new StartWorkflowEvent { RunId = "run-plain", Input = "fresh-input" }),
            ctx,
            CancellationToken.None);

        var requests = StepRequests(ctx);
        requests.Should().ContainSingle();
        requests[0].StepId.Should().Be("step-a");
        requests[0].Input.Should().Be("fresh-input");

        var state = LoadKernelState(host);
        state.Variables["input"].Should().Be("fresh-input");
        state.Variables.Should().NotContainKey("step_a_output");
        state.Variables.Keys.Should().BeEquivalentTo(DefaultStartVariableKeys);
    }

    [Fact]
    public async Task StartWorkflow_WithInputFileRefs_ShouldDispatchFirstStepAndPersistCurrentStepRefs()
    {
        var ctx = new RecordingEventHandlerContext();
        var host = new RecordingStateHost();
        var kernel = new WorkflowExecutionKernel(ThreeStepWorkflow(), host);
        var start = new StartWorkflowEvent
        {
            RunId = "run-files",
            Input = "fresh-input",
        };
        start.InputFileRefs.Add(BuildWorkflowFileRef("file-first"));

        await kernel.HandleAsync(Wrap(start), ctx, CancellationToken.None);

        var request = StepRequests(ctx).Single();
        request.StepId.Should().Be("step-a");
        request.InputFileRefs.Should().ContainSingle().Which.FileId.Should().Be("file-first");

        var state = LoadKernelState(host);
        state.InputFileRefs.Should().ContainSingle().Which.FileId.Should().Be("file-first");
        state.CurrentStepInputFileRefs.Should().ContainSingle().Which.FileId.Should().Be("file-first");
    }

    [Fact]
    public async Task StepCompleted_ShouldNotCarryStartInputFileRefsToNextStep()
    {
        var ctx = new RecordingEventHandlerContext();
        var host = new RecordingStateHost();
        var kernel = new WorkflowExecutionKernel(ThreeStepWorkflow(), host);
        var start = new StartWorkflowEvent
        {
            RunId = "run-files",
            Input = "fresh-input",
        };
        start.InputFileRefs.Add(BuildWorkflowFileRef("file-first"));

        await kernel.HandleAsync(Wrap(start), ctx, CancellationToken.None);
        var first = StepRequests(ctx).Single();
        ctx.Published.Clear();

        await kernel.HandleAsync(
            Wrap(new StepCompletedEvent
            {
                StepId = "step-a",
                RunId = "run-files",
                Success = true,
                Output = "alpha",
                ExecutionId = first.ExecutionId,
            }),
            ctx,
            CancellationToken.None);

        var second = StepRequests(ctx).Single();
        second.StepId.Should().Be("step-b");
        second.InputFileRefs.Should().BeEmpty();
        LoadKernelState(host).CurrentStepInputFileRefs.Should().BeEmpty();
    }

    [Fact]
    public async Task StartWorkflow_WithStepPresentation_ShouldDispatchTypedInteractionSpec()
    {
        var ctx = new RecordingEventHandlerContext();
        var host = new RecordingStateHost();
        var workflow = new WorkflowDefinition
        {
            Name = "interaction-workflow",
            Roles = [new RoleDefinition { Id = "worker", Name = "Worker" }],
            Steps =
            [
                new StepDefinition
                {
                    Id = "approval",
                    Type = "human_approval",
                    TargetRole = "worker",
                    Presentation = new StepPresentation
                    {
                        InteractionSpec = new InteractionSpec
                        {
                            Title = "Approve ${input}",
                            Body = "Release ${release}",
                            Disposition = InteractionDisposition.Ephemeral,
                            Actions =
                            {
                                new InteractionAction
                                {
                                    Kind = InteractionActionKind.Button,
                                    ActionId = "approve",
                                    Label = "Approve ${release}",
                                    Value = "${release}",
                                    Style = InteractionActionStyle.Primary,
                                },
                            },
                        },
                    },
                },
            ],
        };
        var kernel = new WorkflowExecutionKernel(workflow, host);
        var start = new StartWorkflowEvent { RunId = "run-interaction", Input = "deploy" };
        start.Parameters["release"] = "v1";

        await kernel.HandleAsync(Wrap(start), ctx, CancellationToken.None);

        var request = StepRequests(ctx).Single();
        request.Parameters.Should().NotContainKey("interaction_spec");
        request.StepParameters.InteractionSpec.Title.Should().Be("Approve deploy");
        request.StepParameters.InteractionSpec.Body.Should().Be("Release v1");
        request.StepParameters.InteractionSpec.Disposition.Should().Be(InteractionDisposition.Ephemeral);
        request.StepParameters.InteractionSpec.Actions[0].Label.Should().Be("Approve v1");
        request.StepParameters.InteractionSpec.Actions[0].Value.Should().Be("v1");
        request.StepParameters.InteractionSpec.Actions[0].Style.Should().Be(InteractionActionStyle.Primary);
    }

    [Fact]
    public async Task StartWorkflow_WithRoleAndStepToolScopes_ShouldDispatchIntersection()
    {
        var ctx = new RecordingEventHandlerContext();
        var host = new RecordingStateHost();
        var workflow = new WorkflowDefinition
        {
            Name = "tool-scope-workflow",
            Roles =
            [
                new RoleDefinition
                {
                    Id = "worker",
                    Name = "Worker",
                    AgentToolScope = new WorkflowAgentToolScopeDefinition
                    {
                        AllowedToolNames = ["search", "calendar"],
                        ToolSetRefs = ["nyxid.connected_services", "shared"],
                    },
                },
            ],
            Steps =
            [
                new StepDefinition
                {
                    Id = "scoped",
                    Type = "llm_call",
                    TargetRole = "worker",
                    AgentToolScope = new WorkflowAgentToolScopeDefinition
                    {
                        AllowedToolNames = ["calendar", "forbidden"],
                        ToolSetRefs = ["nyxid.connected_services", "other"],
                    },
                },
            ],
        };
        var kernel = new WorkflowExecutionKernel(workflow, host);

        await kernel.HandleAsync(Wrap(new StartWorkflowEvent { RunId = "run-tools", Input = "hello" }), ctx, CancellationToken.None);

        var request = StepRequests(ctx).Single();
        request.StepParameters.AgentToolScope.Should().NotBeNull();
        request.StepParameters.AgentToolScope.AllowedToolNames.Should().Equal("calendar");
        request.StepParameters.AgentToolScope.ToolSetRefs.Should().Equal("nyxid.connected_services");
    }

    [Fact]
    public async Task StartWorkflow_WhenStepRestrictsOnlyAllowedTools_ShouldInheritRoleToolSets()
    {
        var ctx = new RecordingEventHandlerContext();
        var host = new RecordingStateHost();
        var workflow = new WorkflowParser().Parse(
            """
            name: independent-tool-scope
            roles:
              - id: worker
                tool_sets: [nyxid.connected_services]
            steps:
              - id: scoped
                type: llm_call
                target_role: worker
                allowed_tools: [search]
            """);
        var kernel = new WorkflowExecutionKernel(workflow, host);

        await kernel.HandleAsync(Wrap(new StartWorkflowEvent { RunId = "run-independent-static", Input = "hello" }), ctx, CancellationToken.None);

        var request = StepRequests(ctx).Single();
        request.StepParameters.AgentToolScope.AllowedToolNames.Should().Equal("search");
        request.StepParameters.AgentToolScope.ToolSetRefs.Should().Equal("nyxid.connected_services");
    }

    [Fact]
    public async Task StartWorkflow_WhenStepRestrictsOnlyToolSets_ShouldInheritRoleAllowedTools()
    {
        var ctx = new RecordingEventHandlerContext();
        var host = new RecordingStateHost();
        var workflow = new WorkflowParser().Parse(
            """
            name: independent-tool-set-scope
            roles:
              - id: worker
                allowed_tools: [search]
            steps:
              - id: scoped
                type: llm_call
                target_role: worker
                tool_sets: [nyxid.connected_services]
            """);
        var kernel = new WorkflowExecutionKernel(workflow, host);

        await kernel.HandleAsync(Wrap(new StartWorkflowEvent { RunId = "run-independent-tool-set", Input = "hello" }), ctx, CancellationToken.None);

        var request = StepRequests(ctx).Single();
        request.StepParameters.AgentToolScope.AllowedToolNames.Should().Equal("search");
        request.StepParameters.AgentToolScope.ToolSetRefs.Should().Equal("nyxid.connected_services");
    }

    [Fact]
    public async Task StartWorkflow_WithExplicitEmptyAllowedTools_ShouldClearOnlyStaticTools()
    {
        var ctx = new RecordingEventHandlerContext();
        var host = new RecordingStateHost();
        var workflow = new WorkflowParser().Parse(
            """
            name: empty-static-tool-scope
            roles:
              - id: worker
                allowed_tools: [search]
                tool_sets: [nyxid.connected_services]
            steps:
              - id: scoped
                type: llm_call
                target_role: worker
                allowed_tools: []
            """);
        var kernel = new WorkflowExecutionKernel(workflow, host);

        await kernel.HandleAsync(Wrap(new StartWorkflowEvent { RunId = "run-empty-static", Input = "hello" }), ctx, CancellationToken.None);

        var request = StepRequests(ctx).Single();
        request.StepParameters.AgentToolScope.AllowedToolNames.Should().BeEmpty();
        request.StepParameters.AgentToolScope.ToolSetRefs.Should().Equal("nyxid.connected_services");
    }

    [Fact]
    public async Task StartWorkflow_WithExplicitEmptyToolSets_ShouldClearOnlyDynamicTools()
    {
        var ctx = new RecordingEventHandlerContext();
        var host = new RecordingStateHost();
        var workflow = new WorkflowParser().Parse(
            """
            name: empty-dynamic-tool-scope
            roles:
              - id: worker
                allowed_tools: [search]
                tool_sets: [nyxid.connected_services]
            steps:
              - id: scoped
                type: llm_call
                target_role: worker
                tool_sets: []
            """);
        var kernel = new WorkflowExecutionKernel(workflow, host);

        await kernel.HandleAsync(Wrap(new StartWorkflowEvent { RunId = "run-empty-dynamic", Input = "hello" }), ctx, CancellationToken.None);

        var request = StepRequests(ctx).Single();
        request.StepParameters.AgentToolScope.AllowedToolNames.Should().Equal("search");
        request.StepParameters.AgentToolScope.ToolSetRefs.Should().BeEmpty();
    }

    [Fact]
    public async Task StartWorkflow_WithExternalApprovalYaml_ShouldDispatchEvaluatedTypedWaitOptions()
    {
        var workflow = new WorkflowParser().Parse(
            """
            name: approval_yaml
            roles: []
            steps:
              - id: wait_approval
                type: wait_signal
                parameters:
                  external_approval.source_id: "NyxID"
                  external_approval.external_id_kind: "Instance_Code"
                  external_approval.external_id: "${instance_code}"
                  external_approval.signal_name: "approval-${input}"
                  external_approval.callback_idempotency_key: "${concat('idem-', instance_code)}"
                  external_approval.request_id: "${request_id}"
            """);
        var ctx = new RecordingEventHandlerContext();
        var host = new RecordingStateHost();
        var kernel = new WorkflowExecutionKernel(workflow, host);
        var start = new StartWorkflowEvent { RunId = "run-approval", Input = "deploy" };
        start.Parameters["instance_code"] = "APP-42";
        start.Parameters["request_id"] = "REQ-42";

        await kernel.HandleAsync(Wrap(start), ctx, CancellationToken.None);

        var externalApproval = StepRequests(ctx)
            .Single()
            .StepParameters
            .ExternalApproval;
        externalApproval.Should().NotBeNull();
        externalApproval.SourceId.Should().Be("NyxID");
        externalApproval.ExternalIdKind.Should().Be("Instance_Code");
        externalApproval.ExternalId.Should().Be("APP-42");
        externalApproval.SignalName.Should().Be("approval-deploy");
        externalApproval.CallbackIdempotencyKey.Should().Be("idem-APP-42");
        externalApproval.RequestId.Should().Be("REQ-42");
    }

    // ──── Test infrastructure ────

    private static readonly string[] DefaultStartVariableKeys =
    [
        "input",
        "workflow.usage.prompt_tokens",
        "workflow.usage.completion_tokens",
        "workflow.usage.total_tokens",
        "workflow.usage.model",
        "workflow.usage.cost",
        "workflow.usage.latency_ms",
    ];

    private static IReadOnlyList<StepRequestEvent> StepRequests(RecordingEventHandlerContext ctx) =>
        ctx.Published
            .Select(p => p.Event)
            .Where(e => e.Is(StepRequestEvent.Descriptor))
            .Select(e => e.Unpack<StepRequestEvent>())
            .ToList();

    private static IReadOnlyList<WorkflowCompletedEvent> WorkflowCompletions(RecordingEventHandlerContext ctx) =>
        ctx.Published
            .Select(p => p.Event)
            .Where(e => e.Is(WorkflowCompletedEvent.Descriptor))
            .Select(e => e.Unpack<WorkflowCompletedEvent>())
            .ToList();

    private static WorkflowExecutionKernelState LoadKernelState(RecordingStateHost host) =>
        host.GetExecutionState("workflow_execution_kernel")!.Unpack<WorkflowExecutionKernelState>();

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

    private sealed class RecordingEventHandlerContext : IEventHandlerContext
    {
        public EventEnvelope InboundEnvelope { get; } = new()
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
        };

        public string AgentId => "agent-1";

        public IAgent Agent { get; } = new StubAgent("agent-1");

        public IServiceProvider Services { get; } = new NullServiceProvider();

        public RecordingLogger RecordingLogger { get; } = new();

        public ILogger Logger => RecordingLogger;

        public List<(Any Event, TopologyAudience Direction)> Published { get; } = [];

        public List<(string TargetActorId, Any Event)> Sent { get; } = [];

        public List<RecordedCallback> ScheduledTimeouts { get; } = [];

        public List<RecordedTimer> ScheduledTimers { get; } = [];

        public List<RuntimeCallbackLease> Canceled { get; } = [];

        public Task PublishAsync<TEvent>(
            TEvent evt,
            TopologyAudience direction = TopologyAudience.Children,
            CancellationToken ct = default,
            EventEnvelopePublishOptions? options = null)
            where TEvent : IMessage
        {
            ct.ThrowIfCancellationRequested();
            Published.Add((Any.Pack(evt), direction));
            return Task.CompletedTask;
        }

        public Task SendToAsync<TEvent>(
            string targetActorId,
            TEvent evt,
            CancellationToken ct = default,
            EventEnvelopePublishOptions? options = null)
            where TEvent : IMessage
        {
            ct.ThrowIfCancellationRequested();
            Sent.Add((targetActorId, Any.Pack(evt)));
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
            ScheduledTimeouts.Add(new RecordedCallback(callbackId, dueTime, Any.Pack(evt), options));
            return Task.FromResult(new RuntimeCallbackLease(AgentId, callbackId, 1, RuntimeCallbackBackend.InMemory));
        }

        public Task<RuntimeCallbackLease> ScheduleSelfDurableTimerAsync(
            string callbackId,
            TimeSpan dueTime,
            TimeSpan period,
            IMessage evt,
            EventEnvelopePublishOptions? options = null,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            ScheduledTimers.Add(new RecordedTimer(callbackId, dueTime, period, Any.Pack(evt), options));
            return Task.FromResult(new RuntimeCallbackLease(AgentId, callbackId, 2, RuntimeCallbackBackend.InMemory));
        }

        public Task CancelDurableCallbackAsync(RuntimeCallbackLease lease, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Canceled.Add(lease);
            return Task.CompletedTask;
        }
    }

    internal sealed class RecordingLogger : ILogger
    {
        public List<string> Messages { get; } = [];

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Messages.Add(formatter(state, exception));

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }

    private sealed class RecordingStateHost : IWorkflowExecutionStateHost
    {
        public string RunId { get; set; } = "run-1";

        public WorkflowExecutionRuntimeContext RuntimeContext { get; } = new();

        public WorkflowRunExecutionContextState ExecutionContextState { get; } = new();

        public WorkflowRunExecutionContextState ExecutionContextSnapshot => ExecutionContextState.Clone();

        public Task UpdateExecutionContextAsync(WorkflowRunExecutionContextDelta delta, CancellationToken ct = default)
        {
            ApplyDelta(ExecutionContextState, delta);
            return Task.CompletedTask;
        }

        public Task ClearExecutionContextAsync(CancellationToken ct = default)
        {
            ExecutionContextState.Llm = null;
            ExecutionContextState.CallerCredential = null;
            return Task.CompletedTask;
        }

        public Dictionary<string, Any> States { get; } = new(StringComparer.Ordinal);

        public Any? GetExecutionState(string scopeKey) =>
            States.TryGetValue(scopeKey, out var state) ? state : null;

        public IReadOnlyList<KeyValuePair<string, Any>> GetExecutionStates() =>
            States.ToList();

        public Task UpsertExecutionStateAsync(string scopeKey, Any state, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            States[scopeKey] = state;
            return Task.CompletedTask;
        }

        public Task ClearExecutionStateAsync(string scopeKey, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            States.Remove(scopeKey);
            return Task.CompletedTask;
        }

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
    }

    private sealed class StubAgent(string id) : IAgent
    {
        public string Id { get; } = id;
        public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default) => Task.CompletedTask;
        public Task<string> GetDescriptionAsync() => Task.FromResult("stub");
        public Task<IReadOnlyList<System.Type>> GetSubscribedEventTypesAsync() => Task.FromResult<IReadOnlyList<System.Type>>([]);
        public Task ActivateAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class NullServiceProvider : IServiceProvider
    {
        public object? GetService(System.Type serviceType) => null;
    }

    private static void ApplyDelta(
        WorkflowRunExecutionContextState state,
        WorkflowRunExecutionContextDelta delta)
    {
        if (delta.ClearLlm)
            state.Llm = null;
        if (delta.ClearCallerCredential)
            state.CallerCredential = null;
        if (delta.Llm != null)
        {
            state.Llm = new WorkflowLlmExecutionContextState
            {
                ModelOverride = delta.Llm.ModelOverride,
                UserMemoryPrompt = delta.Llm.UserMemoryPrompt,
                RoutePreference = delta.Llm.RoutePreference,
            };
            if (delta.Llm.HasMaxToolRoundsOverride)
                state.Llm.MaxToolRoundsOverride = delta.Llm.MaxToolRoundsOverride;
        }

        if (delta.CallerCredential != null)
        {
            state.CallerCredential = new WorkflowCallerCredentialState
            {
                BearerToken = delta.CallerCredential.BearerToken,
            };
        }
    }

    internal record RecordedCallback(string CallbackId, TimeSpan DueTime, Any Event, EventEnvelopePublishOptions? Options);
    internal record RecordedTimer(string CallbackId, TimeSpan DueTime, TimeSpan Period, Any Event, EventEnvelopePublishOptions? Options);
}
