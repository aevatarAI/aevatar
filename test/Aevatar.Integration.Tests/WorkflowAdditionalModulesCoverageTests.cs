using Aevatar.AI.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.EventModules;
using Aevatar.Foundation.Core;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Core;
using Aevatar.Workflow.Core.Modules;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.Integration.Tests;

[Trait("Category", "Integration")]
[Trait("Feature", "WorkflowAdditionalModules")]
public sealed class WorkflowAdditionalModulesCoverageTests
{
    [Fact]
    public async Task DelayAndEmitModules_ShouldHandleCorePaths()
    {
        var delay = new DelayModule();
        var emit = new EmitModule();
        var ctx = CreateContext();

        await delay.HandleAsync(
            Envelope(new StepRequestEvent
            {
                StepId = "d-ignore",
                StepType = "llm_call",
                Input = "x",
            }),
            ctx,
            CancellationToken.None);
        ctx.Published.Should().BeEmpty();

        await delay.HandleAsync(
            Envelope(new StepRequestEvent
            {
                StepId = "delay-1",
                StepType = "delay",
                Input = "payload",
                Parameters = { ["duration_ms"] = "-5" },
            }),
            ctx,
            CancellationToken.None);

        var delayCompleted = ctx.Published.Select(x => x.evt).OfType<StepCompletedEvent>().Single();
        delayCompleted.StepId.Should().Be("delay-1");
        delayCompleted.Success.Should().BeTrue();
        delayCompleted.Output.Should().Be("payload");
        ctx.Published.Clear();

        await emit.HandleAsync(
            Envelope(new StepRequestEvent
            {
                StepId = "emit-1",
                StepType = "emit",
                Input = "source-input",
                Parameters =
                {
                    ["event_type"] = "audit",
                    ["payload"] = "{\"k\":1}",
                },
            }),
            ctx,
            CancellationToken.None);

        ctx.Published.Should().ContainSingle();
        ctx.Published[0].direction.Should().Be(EventDirection.Self);
        var emitted = ctx.Published[0].evt.Should().BeOfType<StepCompletedEvent>().Subject;
        emitted.Metadata["emit.event_type"].Should().Be("audit");
        emitted.Metadata["emit.payload"].Should().Be("{\"k\":1}");
        ctx.Published.Clear();

        await emit.HandleAsync(
            Envelope(new StepRequestEvent
            {
                StepId = "emit-2",
                StepType = "emit",
                Input = "fallback-payload",
            }),
            ctx,
            CancellationToken.None);

        var defaultEmit = ctx.Published.Select(x => x.evt).OfType<StepCompletedEvent>().Single();
        defaultEmit.Metadata["emit.event_type"].Should().Be("custom");
        defaultEmit.Metadata["emit.payload"].Should().Be("fallback-payload");
    }

    [Fact]
    public async Task SwitchModule_ShouldResolveExactContainsAndDefaultBranch()
    {
        var module = new SwitchModule();
        var ctx = CreateContext();

        await module.HandleAsync(
            Envelope(new StepRequestEvent
            {
                StepId = "switch-1",
                StepType = "switch",
                Input = "ignored",
                Parameters =
                {
                    ["on"] = "foo",
                    ["branch.foo"] = "s-next-foo",
                    ["branch.bar"] = "s-next-bar",
                    ["branch._default"] = "s-next-default",
                },
            }),
            ctx,
            CancellationToken.None);

        await module.HandleAsync(
            Envelope(new StepRequestEvent
            {
                StepId = "switch-2",
                StepType = "switch",
                Input = "prefix BAR suffix",
                Parameters =
                {
                    ["branch.foo"] = "s-next-foo",
                    ["branch.bar"] = "s-next-bar",
                    ["branch._default"] = "s-next-default",
                },
            }),
            ctx,
            CancellationToken.None);

        await module.HandleAsync(
            Envelope(new StepRequestEvent
            {
                StepId = "switch-3",
                StepType = "switch",
                Input = "unmatched",
                Parameters =
                {
                    ["branch.foo"] = "s-next-foo",
                    ["branch._default"] = "s-next-default",
                },
            }),
            ctx,
            CancellationToken.None);

        var completions = ctx.Published.Select(x => x.evt).OfType<StepCompletedEvent>().ToDictionary(x => x.StepId, x => x);
        completions["switch-1"].Metadata["branch"].Should().Be("foo");
        completions["switch-2"].Metadata["branch"].Should().Be("bar");
        completions["switch-3"].Metadata["branch"].Should().Be("_default");
    }

    [Fact]
    public async Task WaitSignalModule_ShouldSuspendAndResumeWithSignalPayloadOrFallbackInput()
    {
        var module = new WaitSignalModule();
        var ctx = CreateContext();

        await module.HandleAsync(
            Envelope(new StepRequestEvent
            {
                StepId = "wait-1",
                StepType = "wait_signal",
                RunId = "run-w1",
                Input = "fallback-input",
                Parameters =
                {
                    ["signal_name"] = "approval",
                    ["prompt"] = "waiting",
                    ["timeout_ms"] = "0",
                },
            }),
            ctx,
            CancellationToken.None);

        var waiting = ctx.Published.Select(x => x.evt).OfType<WaitingForSignalEvent>().Single();
        waiting.StepId.Should().Be("wait-1");
        waiting.SignalName.Should().Be("approval");
        waiting.RunId.Should().Be("run-w1");
        ctx.Published.Clear();

        await module.HandleAsync(
            Envelope(new SignalReceivedEvent
            {
                SignalName = "approval",
                Payload = "",
                RunId = "run-w1",
            }),
            ctx,
            CancellationToken.None);

        var resumed = ctx.Published.Select(x => x.evt).OfType<StepCompletedEvent>().Single();
        resumed.StepId.Should().Be("wait-1");
        resumed.RunId.Should().Be("run-w1");
        resumed.Success.Should().BeTrue();
        resumed.Output.Should().Be("fallback-input");
        ctx.Published.Clear();

        await module.HandleAsync(
            Envelope(new SignalReceivedEvent
            {
                SignalName = "unknown",
                Payload = "noop",
                RunId = "run-w1",
            }),
            ctx,
            CancellationToken.None);
        ctx.Published.Should().BeEmpty();
    }

    [Fact]
    public async Task WaitSignalModule_WhenSignalRunIdMissingAndAmbiguous_ShouldNotResumeAnyRun()
    {
        var module = new WaitSignalModule();
        var ctx = CreateContext();

        await module.HandleAsync(
            Envelope(new StepRequestEvent
            {
                StepId = "wait-a",
                StepType = "wait_signal",
                RunId = "run-a",
                Input = "input-a",
                Parameters = { ["signal_name"] = "approval" },
            }),
            ctx,
            CancellationToken.None);

        await module.HandleAsync(
            Envelope(new StepRequestEvent
            {
                StepId = "wait-b",
                StepType = "wait_signal",
                RunId = "run-b",
                Input = "input-b",
                Parameters = { ["signal_name"] = "approval" },
            }),
            ctx,
            CancellationToken.None);
        ctx.Published.Clear();

        await module.HandleAsync(
            Envelope(new SignalReceivedEvent
            {
                SignalName = "approval",
                Payload = "ambiguous",
            }),
            ctx,
            CancellationToken.None);
        ctx.Published.Should().BeEmpty();

        await module.HandleAsync(
            Envelope(new SignalReceivedEvent
            {
                SignalName = "approval",
                RunId = "run-b",
                Payload = "resolved-b",
            }),
            ctx,
            CancellationToken.None);

        var resumed = ctx.Published.Select(x => x.evt).OfType<StepCompletedEvent>().Single();
        resumed.StepId.Should().Be("wait-b");
        resumed.RunId.Should().Be("run-b");
        resumed.Output.Should().Be("resolved-b");
    }

    [Fact]
    public async Task WaitSignalModule_WhenSameRunAndSignalHasMultipleWaiters_ShouldRequireStepIdToDisambiguate()
    {
        var module = new WaitSignalModule();
        var ctx = CreateContext();

        await module.HandleAsync(
            Envelope(new StepRequestEvent
            {
                StepId = "wait-a",
                StepType = "wait_signal",
                RunId = "run-shared",
                Input = "fallback-a",
                Parameters = { ["signal_name"] = "approval" },
            }),
            ctx,
            CancellationToken.None);

        await module.HandleAsync(
            Envelope(new StepRequestEvent
            {
                StepId = "wait-b",
                StepType = "wait_signal",
                RunId = "run-shared",
                Input = "fallback-b",
                Parameters = { ["signal_name"] = "approval" },
            }),
            ctx,
            CancellationToken.None);
        ctx.Published.Clear();

        await module.HandleAsync(
            Envelope(new SignalReceivedEvent
            {
                SignalName = "approval",
                RunId = "run-shared",
                Payload = "ambiguous",
            }),
            ctx,
            CancellationToken.None);
        ctx.Published.Should().BeEmpty();

        await module.HandleAsync(
            Envelope(new SignalReceivedEvent
            {
                SignalName = "approval",
                RunId = "run-shared",
                StepId = "wait-b",
                Payload = "resolved-b",
            }),
            ctx,
            CancellationToken.None);

        var resumedB = ctx.Published.Select(x => x.evt).OfType<StepCompletedEvent>().Single();
        resumedB.StepId.Should().Be("wait-b");
        resumedB.RunId.Should().Be("run-shared");
        resumedB.Output.Should().Be("resolved-b");
        ctx.Published.Clear();

        await module.HandleAsync(
            Envelope(new SignalReceivedEvent
            {
                SignalName = "approval",
                RunId = "run-shared",
                StepId = "wait-a",
                Payload = "",
            }),
            ctx,
            CancellationToken.None);

        var resumedA = ctx.Published.Select(x => x.evt).OfType<StepCompletedEvent>().Single();
        resumedA.StepId.Should().Be("wait-a");
        resumedA.RunId.Should().Be("run-shared");
        resumedA.Output.Should().Be("fallback-a");
    }

    [Fact]
    public async Task WaitSignalModule_ShouldCompleteStepWithTimeoutError_WhenTimeoutEventMatchesPending()
    {
        var module = new WaitSignalModule();
        var ctx = CreateContext();

        await module.HandleAsync(
            Envelope(new StepRequestEvent
            {
                StepId = "wait-timeout",
                StepType = "wait_signal",
                RunId = "run-timeout",
                Input = "fallback-timeout",
                Parameters = { ["signal_name"] = "approval" },
            }),
            ctx,
            CancellationToken.None);
        ctx.Published.Clear();

        await module.HandleAsync(
            Envelope(new WaitSignalTimeoutFiredEvent
            {
                RunId = "run-timeout",
                StepId = "wait-timeout",
                SignalName = "approval",
                TimeoutMs = 250,
            }),
            ctx,
            CancellationToken.None);

        var timedOut = ctx.Published.Select(x => x.evt).OfType<StepCompletedEvent>().Single();
        timedOut.StepId.Should().Be("wait-timeout");
        timedOut.RunId.Should().Be("run-timeout");
        timedOut.Success.Should().BeFalse();
        timedOut.Error.Should().Contain("timed out");
    }

    [Fact]
    public async Task WaitSignalModule_WhenTimeoutCannotResolvePending_ShouldIgnore()
    {
        var module = new WaitSignalModule();
        var ctx = CreateContext();

        await module.HandleAsync(
            Envelope(new WaitSignalTimeoutFiredEvent
            {
                RunId = "run-timeout",
                StepId = " ",
                SignalName = "approval",
                TimeoutMs = 100,
            }),
            ctx,
            CancellationToken.None);

        await module.HandleAsync(
            Envelope(new WaitSignalTimeoutFiredEvent
            {
                RunId = "run-timeout",
                StepId = "missing-step",
                SignalName = "approval",
                TimeoutMs = 100,
            }),
            ctx,
            CancellationToken.None);

        ctx.Published.Should().BeEmpty();
    }

    [Fact]
    public async Task WaitSignalModule_CanHandleAndNoPayloadPaths_ShouldBehaveAsExpected()
    {
        var module = new WaitSignalModule();
        var ctx = CreateContext();

        module.CanHandle(new EventEnvelope()).Should().BeFalse();
        module.CanHandle(Envelope(new StepRequestEvent())).Should().BeTrue();
        module.CanHandle(Envelope(new SignalReceivedEvent())).Should().BeTrue();
        module.CanHandle(Envelope(new WaitSignalTimeoutFiredEvent())).Should().BeTrue();

        await module.HandleAsync(new EventEnvelope(), ctx, CancellationToken.None);
        ctx.Published.Should().BeEmpty();

        await module.HandleAsync(
            Envelope(new StepRequestEvent
            {
                StepId = "non-wait",
                StepType = "llm_call",
            }),
            ctx,
            CancellationToken.None);
        ctx.Published.Should().BeEmpty();
    }

    [Fact]
    public async Task CacheModule_ShouldDispatchOnMissJoinPendingAndHitOnReadyValue()
    {
        var module = new CacheModule();
        var ctx = CreateContext();

        await module.HandleAsync(
            Envelope(new StepRequestEvent
            {
                StepId = "cache-1",
                StepType = "cache",
                Input = "origin",
                Parameters =
                {
                    ["cache_key"] = "k1",
                    ["ttl_seconds"] = "3600",
                    ["child_step_type"] = "transform",
                    ["child_target_role"] = "worker",
                },
            }),
            ctx,
            CancellationToken.None);

        var childDispatch = ctx.Published.Select(x => x.evt).OfType<StepRequestEvent>().Single();
        childDispatch.StepId.Should().StartWith("cache-1_cached_");
        childDispatch.StepType.Should().Be("transform");
        childDispatch.TargetRole.Should().Be("worker");
        var childStepId = childDispatch.StepId;
        ctx.Published.Clear();

        await module.HandleAsync(
            Envelope(new StepRequestEvent
            {
                StepId = "cache-2",
                StepType = "cache",
                Input = "origin-2",
                Parameters = { ["cache_key"] = "k1" },
            }),
            ctx,
            CancellationToken.None);
        ctx.Published.Should().BeEmpty();

        await module.HandleAsync(
            Envelope(new StepCompletedEvent
            {
                StepId = childStepId,
                Success = true,
                Output = "cached-value",
            }),
            ctx,
            CancellationToken.None);

        var pendingCompletions = ctx.Published.Select(x => x.evt).OfType<StepCompletedEvent>().ToList();
        pendingCompletions.Should().HaveCount(2);
        pendingCompletions.Should().ContainSingle(x => x.StepId == "cache-1" && x.Success && x.Output == "cached-value");
        pendingCompletions.Should().ContainSingle(x => x.StepId == "cache-2" && x.Success && x.Output == "cached-value");
        pendingCompletions.Should().OnlyContain(x => x.Metadata["cache.hit"] == "false");
        ctx.Published.Clear();

        await module.HandleAsync(
            Envelope(new StepRequestEvent
            {
                StepId = "cache-3",
                StepType = "cache",
                Input = "ignored",
                Parameters = { ["cache_key"] = "k1" },
            }),
            ctx,
            CancellationToken.None);

        var hitCompletion = ctx.Published.Select(x => x.evt).OfType<StepCompletedEvent>().Single();
        hitCompletion.StepId.Should().Be("cache-3");
        hitCompletion.Success.Should().BeTrue();
        hitCompletion.Output.Should().Be("cached-value");
        hitCompletion.Metadata["cache.hit"].Should().Be("true");
    }

    [Fact]
    public async Task GuardModule_ShouldSupportPassSkipBranchAndFailStrategies()
    {
        var module = new GuardModule();
        var ctx = CreateContext();

        await module.HandleAsync(
            Envelope(new StepRequestEvent
            {
                StepId = "guard-pass",
                StepType = "guard",
                Input = "hello",
                Parameters = { ["check"] = "not_empty" },
            }),
            ctx,
            CancellationToken.None);

        await module.HandleAsync(
            Envelope(new StepRequestEvent
            {
                StepId = "guard-skip",
                StepType = "guard",
                Input = "abcdef",
                Parameters =
                {
                    ["check"] = "contains",
                    ["keyword"] = "missing",
                    ["on_fail"] = "skip",
                },
            }),
            ctx,
            CancellationToken.None);

        await module.HandleAsync(
            Envelope(new StepRequestEvent
            {
                StepId = "guard-branch",
                StepType = "guard",
                Input = "no digits here",
                Parameters =
                {
                    ["check"] = "regex",
                    ["pattern"] = "[0-9]+",
                    ["on_fail"] = "branch",
                    ["branch_target"] = "manual_review",
                },
            }),
            ctx,
            CancellationToken.None);

        await module.HandleAsync(
            Envelope(new StepRequestEvent
            {
                StepId = "guard-fail",
                StepType = "guard",
                Input = "abcd",
                Parameters =
                {
                    ["check"] = "max_length",
                    ["max"] = "2",
                },
            }),
            ctx,
            CancellationToken.None);

        var completions = ctx.Published.Select(x => x.evt).OfType<StepCompletedEvent>().ToDictionary(x => x.StepId, x => x);
        completions["guard-pass"].Success.Should().BeTrue();
        completions["guard-skip"].Success.Should().BeTrue();
        completions["guard-skip"].Metadata["guard.skipped"].Should().Be("true");
        completions["guard-branch"].Success.Should().BeTrue();
        completions["guard-branch"].Metadata["next_step"].Should().Be("manual_review");
        completions["guard-fail"].Success.Should().BeFalse();
        completions["guard-fail"].Error.Should().Contain("guard check");
    }

    [Fact]
    public async Task HumanApprovalModule_ShouldSuspendThenHandleApproveAndReject()
    {
        var module = new HumanApprovalModule();
        var ctx = CreateContext();

        await module.HandleAsync(
            Envelope(new StepRequestEvent
            {
                StepId = "approval-1",
                StepType = "human_approval",
                RunId = "run-1",
                Input = "original",
                Parameters =
                {
                    ["prompt"] = "approve?",
                    ["timeout"] = "90",
                },
            }),
            ctx,
            CancellationToken.None);

        var suspended = ctx.Published.Select(x => x.evt).OfType<WorkflowSuspendedEvent>().Single();
        suspended.StepId.Should().Be("approval-1");
        suspended.SuspensionType.Should().Be("human_approval");
        ctx.Published.Clear();

        await module.HandleAsync(
            Envelope(new WorkflowResumedEvent
            {
                RunId = "run-1",
                StepId = "approval-1",
                Approved = true,
                UserInput = "approved-output",
            }),
            ctx,
            CancellationToken.None);

        var approved = ctx.Published.Select(x => x.evt).OfType<StepCompletedEvent>().Single();
        approved.StepId.Should().Be("approval-1");
        approved.RunId.Should().Be("run-1");
        approved.Success.Should().BeTrue();
        approved.Output.Should().Be("approved-output");
        ctx.Published.Clear();

        await module.HandleAsync(
            Envelope(new StepRequestEvent
            {
                StepId = "approval-2",
                StepType = "human_approval",
                RunId = "run-2",
                Input = "keep-me",
                Parameters = { ["on_reject"] = "continue" },
            }),
            ctx,
            CancellationToken.None);
        ctx.Published.Clear();

        await module.HandleAsync(
            Envelope(new WorkflowResumedEvent
            {
                RunId = "run-2",
                StepId = "approval-2",
                Approved = false,
            }),
            ctx,
            CancellationToken.None);

        var rejected = ctx.Published.Select(x => x.evt).OfType<StepCompletedEvent>().Single();
        rejected.StepId.Should().Be("approval-2");
        rejected.Success.Should().BeTrue();
        rejected.Output.Should().Be("keep-me");
        rejected.Error.Should().BeEmpty();
    }

    [Fact]
    public async Task HumanApprovalModule_ShouldUseRunScopedPendingForSameStepId()
    {
        var module = new HumanApprovalModule();
        var ctx = CreateContext();

        await module.HandleAsync(
            Envelope(new StepRequestEvent
            {
                StepId = "approval-shared",
                StepType = "human_approval",
                RunId = "run-a",
                Input = "A",
            }),
            ctx,
            CancellationToken.None);

        await module.HandleAsync(
            Envelope(new StepRequestEvent
            {
                StepId = "approval-shared",
                StepType = "human_approval",
                RunId = "run-b",
                Input = "B",
            }),
            ctx,
            CancellationToken.None);
        ctx.Published.Clear();

        await module.HandleAsync(
            Envelope(new WorkflowResumedEvent
            {
                RunId = "run-b",
                StepId = "approval-shared",
                Approved = true,
                UserInput = "B-approved",
            }),
            ctx,
            CancellationToken.None);

        var resumedB = ctx.Published.Select(x => x.evt).OfType<StepCompletedEvent>().Single();
        resumedB.RunId.Should().Be("run-b");
        resumedB.StepId.Should().Be("approval-shared");
        resumedB.Output.Should().Be("B-approved");
        ctx.Published.Clear();

        await module.HandleAsync(
            Envelope(new WorkflowResumedEvent
            {
                RunId = "run-a",
                StepId = "approval-shared",
                Approved = true,
                UserInput = "A-approved",
            }),
            ctx,
            CancellationToken.None);

        var resumedA = ctx.Published.Select(x => x.evt).OfType<StepCompletedEvent>().Single();
        resumedA.RunId.Should().Be("run-a");
        resumedA.Output.Should().Be("A-approved");
    }

    [Fact]
    public async Task HumanInputModule_ShouldSuspendThenHandleInputAndTimeoutStrategies()
    {
        var module = new HumanInputModule();
        var ctx = CreateContext();

        await module.HandleAsync(
            Envelope(new StepRequestEvent
            {
                StepId = "input-1",
                StepType = "human_input",
                RunId = "run-i1",
                Input = "fallback",
                Parameters =
                {
                    ["prompt"] = "please type",
                    ["variable"] = "answer",
                },
            }),
            ctx,
            CancellationToken.None);

        var suspended = ctx.Published.Select(x => x.evt).OfType<WorkflowSuspendedEvent>().Single();
        suspended.Metadata["variable"].Should().Be("answer");
        ctx.Published.Clear();

        await module.HandleAsync(
            Envelope(new WorkflowResumedEvent
            {
                RunId = "run-i1",
                StepId = "input-1",
                Approved = true,
                UserInput = "typed-value",
            }),
            ctx,
            CancellationToken.None);

        var provided = ctx.Published.Select(x => x.evt).OfType<StepCompletedEvent>().Single();
        provided.Success.Should().BeTrue();
        provided.Output.Should().Be("typed-value");
        ctx.Published.Clear();

        await module.HandleAsync(
            Envelope(new StepRequestEvent
            {
                StepId = "input-2",
                StepType = "human_input",
                RunId = "run-i2",
                Input = "fallback-2",
                Parameters = { ["on_timeout"] = "continue" },
            }),
            ctx,
            CancellationToken.None);
        ctx.Published.Clear();

        await module.HandleAsync(
            Envelope(new WorkflowResumedEvent
            {
                RunId = "run-i2",
                StepId = "input-2",
                Approved = false,
                UserInput = "",
            }),
            ctx,
            CancellationToken.None);

        var timeoutContinue = ctx.Published.Select(x => x.evt).OfType<StepCompletedEvent>().Single();
        timeoutContinue.Success.Should().BeTrue();
        timeoutContinue.Output.Should().Be("fallback-2");
        timeoutContinue.Error.Should().BeEmpty();
        ctx.Published.Clear();

        await module.HandleAsync(
            Envelope(new StepRequestEvent
            {
                StepId = "input-3",
                StepType = "human_input",
                RunId = "run-i3",
                Input = "fallback-3",
            }),
            ctx,
            CancellationToken.None);
        ctx.Published.Clear();

        await module.HandleAsync(
            Envelope(new WorkflowResumedEvent
            {
                RunId = "run-i3",
                StepId = "input-3",
                Approved = false,
            }),
            ctx,
            CancellationToken.None);

        var timeoutFail = ctx.Published.Select(x => x.evt).OfType<StepCompletedEvent>().Single();
        timeoutFail.Success.Should().BeFalse();
        timeoutFail.Error.Should().Be("Human input timed out");
    }

    [Fact]
    public async Task HumanInputModule_ShouldUseRunScopedPendingForSameStepId()
    {
        var module = new HumanInputModule();
        var ctx = CreateContext();

        await module.HandleAsync(
            Envelope(new StepRequestEvent
            {
                StepId = "input-shared",
                StepType = "human_input",
                RunId = "run-a",
                Input = "A",
            }),
            ctx,
            CancellationToken.None);

        await module.HandleAsync(
            Envelope(new StepRequestEvent
            {
                StepId = "input-shared",
                StepType = "human_input",
                RunId = "run-b",
                Input = "B",
            }),
            ctx,
            CancellationToken.None);
        ctx.Published.Clear();

        await module.HandleAsync(
            Envelope(new WorkflowResumedEvent
            {
                RunId = "run-b",
                StepId = "input-shared",
                Approved = true,
                UserInput = "input-from-b",
            }),
            ctx,
            CancellationToken.None);

        var resumedB = ctx.Published.Select(x => x.evt).OfType<StepCompletedEvent>().Single();
        resumedB.RunId.Should().Be("run-b");
        resumedB.Output.Should().Be("input-from-b");
        ctx.Published.Clear();

        await module.HandleAsync(
            Envelope(new WorkflowResumedEvent
            {
                RunId = "run-a",
                StepId = "input-shared",
                Approved = true,
                UserInput = "input-from-a",
            }),
            ctx,
            CancellationToken.None);

        var resumedA = ctx.Published.Select(x => x.evt).OfType<StepCompletedEvent>().Single();
        resumedA.RunId.Should().Be("run-a");
        resumedA.Output.Should().Be("input-from-a");
    }

    [Fact]
    public async Task SecureInputModule_ShouldCaptureMaskedValueAndPublishSecureEvent()
    {
        var module = new SecureInputModule();
        var ctx = CreateContext();

        await module.HandleAsync(
            Envelope(new StepRequestEvent
            {
                StepId = "secure-1",
                StepType = "secure_input",
                RunId = "run-secure",
                Parameters =
                {
                    ["prompt"] = "provide secret",
                    ["variable"] = "api_key",
                },
            }),
            ctx,
            CancellationToken.None);

        var suspended = ctx.Published.Select(x => x.evt).OfType<WorkflowSuspendedEvent>().Single();
        suspended.SuspensionType.Should().Be("secure_input");
        suspended.Metadata["secure"].Should().Be("true");
        suspended.Metadata["variable"].Should().Be("api_key");
        ctx.Published.Clear();

        await module.HandleAsync(
            Envelope(new WorkflowResumedEvent
            {
                RunId = "run-secure",
                StepId = "secure-1",
                Approved = true,
                UserInput = "top-secret-value",
            }),
            ctx,
            CancellationToken.None);

        var captured = ctx.Published.Select(x => x.evt).OfType<SecureValueCapturedEvent>().Single();
        captured.Variable.Should().Be("api_key");
        captured.Value.Should().BeEmpty();

        var completed = ctx.Published.Select(x => x.evt).OfType<StepCompletedEvent>().Single();
        completed.Success.Should().BeTrue();
        completed.Output.Should().Be("[secure input captured]");
        completed.Metadata["secure.input"].Should().Be("true");
        completed.Metadata["secure.variable"].Should().Be("api_key");
    }

    [Fact]
    public async Task RaceModule_ShouldPickFirstSuccessAndFailWhenAllBranchesFail()
    {
        var module = new RaceModule();
        var ctx = CreateContext();

        await module.HandleAsync(
            Envelope(new StepRequestEvent
            {
                StepId = "race-1",
                StepType = "race",
                Input = "question",
                Parameters = { ["workers"] = "worker-a,worker-b" },
            }),
            ctx,
            CancellationToken.None);

        var dispatched = ctx.Published.Select(x => x.evt).OfType<StepRequestEvent>().ToList();
        dispatched.Should().HaveCount(2);
        dispatched[0].StepId.Should().Be("race-1_race_0");
        dispatched[1].StepId.Should().Be("race-1_race_1");
        ctx.Published.Clear();

        await module.HandleAsync(
            Envelope(new StepCompletedEvent
            {
                StepId = "race-1_race_0",
                Success = false,
                Error = "bad",
            }),
            ctx,
            CancellationToken.None);
        ctx.Published.Should().BeEmpty();

        await module.HandleAsync(
            Envelope(new StepCompletedEvent
            {
                StepId = "race-1_race_1",
                Success = true,
                Output = "winner-output",
                WorkerId = "worker-b",
            }),
            ctx,
            CancellationToken.None);

        var winner = ctx.Published.Select(x => x.evt).OfType<StepCompletedEvent>().Single();
        winner.StepId.Should().Be("race-1");
        winner.Success.Should().BeTrue();
        winner.Output.Should().Be("winner-output");
        winner.Metadata["race.winner"].Should().Be("race-1_race_1");
        ctx.Published.Clear();

        await module.HandleAsync(
            Envelope(new StepRequestEvent
            {
                StepId = "race-2",
                StepType = "race",
                Input = "q2",
                TargetRole = "worker-default",
                Parameters = { ["count"] = "2" },
            }),
            ctx,
            CancellationToken.None);
        ctx.Published.Clear();

        await module.HandleAsync(Envelope(new StepCompletedEvent { StepId = "race-2_race_0", Success = false }), ctx, CancellationToken.None);
        await module.HandleAsync(Envelope(new StepCompletedEvent { StepId = "race-2_race_1", Success = false }), ctx, CancellationToken.None);

        var failed = ctx.Published.Select(x => x.evt).OfType<StepCompletedEvent>().Single();
        failed.StepId.Should().Be("race-2");
        failed.Success.Should().BeFalse();
        failed.Error.Should().Contain("all race branches failed");
    }

    [Fact]
    public async Task RaceModule_ShouldAcceptJsonWorkersAndFailFastWhenNoWorkersOrRole()
    {
        var module = new RaceModule();
        var ctx = CreateContext();

        await module.HandleAsync(
            Envelope(new StepRequestEvent
            {
                StepId = "race-json",
                StepType = "race",
                RunId = "run-race-json",
                Input = "q",
                Parameters =
                {
                    ["workers"] = "[\"worker_a\",\"worker_a\",\"worker_b\"]",
                },
            }),
            ctx,
            CancellationToken.None);

        var jsonWorkers = ctx.Published.Select(x => x.evt).OfType<StepRequestEvent>().ToList();
        jsonWorkers.Should().HaveCount(3);
        jsonWorkers[0].TargetRole.Should().Be("worker_a");
        jsonWorkers[1].TargetRole.Should().Be("worker_a");
        jsonWorkers[2].TargetRole.Should().Be("worker_b");
        ctx.Published.Clear();

        await module.HandleAsync(
            Envelope(new StepRequestEvent
            {
                StepId = "race-missing",
                StepType = "race",
                RunId = "run-race-missing",
                Input = "q2",
            }),
            ctx,
            CancellationToken.None);

        var failed = ctx.Published.Select(x => x.evt).OfType<StepCompletedEvent>().Single();
        failed.StepId.Should().Be("race-missing");
        failed.RunId.Should().Be("run-race-missing");
        failed.Success.Should().BeFalse();
        failed.Error.Should().Contain("race requires parameters.workers");
    }

    [Fact]
    public async Task ForEachModule_ShouldSupportEscapedDelimiterAndJsonArrayInput()
    {
        var module = new ForEachModule();
        var ctx = CreateContext();

        await module.HandleAsync(
            Envelope(new StepRequestEvent
            {
                StepId = "foreach-escaped",
                StepType = "foreach",
                RunId = "run-foreach",
                Input = "a\n---\nb",
                Parameters =
                {
                    ["delimiter"] = "\\n---\\n",
                    ["sub_step_type"] = "assign",
                },
            }),
            ctx,
            CancellationToken.None);

        var escapedDispatches = ctx.Published.Select(x => x.evt).OfType<StepRequestEvent>().ToList();
        escapedDispatches.Should().HaveCount(2);
        escapedDispatches[0].Input.Should().Be("a");
        escapedDispatches[1].Input.Should().Be("b");
        ctx.Published.Clear();

        await module.HandleAsync(
            Envelope(new StepCompletedEvent
            {
                StepId = "foreach-escaped_item_0",
                RunId = "run-foreach",
                Success = true,
                Output = "A",
            }),
            ctx,
            CancellationToken.None);
        await module.HandleAsync(
            Envelope(new StepCompletedEvent
            {
                StepId = "foreach-escaped_item_1",
                RunId = "run-foreach",
                Success = true,
                Output = "B",
            }),
            ctx,
            CancellationToken.None);

        var merged = ctx.Published.Select(x => x.evt).OfType<StepCompletedEvent>().Single();
        merged.StepId.Should().Be("foreach-escaped");
        merged.RunId.Should().Be("run-foreach");
        merged.Success.Should().BeTrue();
        merged.Output.Should().Be("A\n---\nB");
        ctx.Published.Clear();

        await module.HandleAsync(
            Envelope(new StepRequestEvent
            {
                StepId = "foreach-json",
                StepType = "foreach",
                RunId = "run-foreach-json",
                Input = "[\"x\",\"y\"]",
                Parameters = { ["sub_step_type"] = "assign" },
            }),
            ctx,
            CancellationToken.None);

        var jsonDispatches = ctx.Published.Select(x => x.evt).OfType<StepRequestEvent>().ToList();
        jsonDispatches.Should().HaveCount(2);
        jsonDispatches[0].Input.Should().Be("x");
        jsonDispatches[1].Input.Should().Be("y");
    }

    [Fact]
    public async Task ParallelFanOutModule_ShouldAcceptJsonArrayWorkersAndMergeCompletions()
    {
        var module = new ParallelFanOutModule();
        var ctx = CreateContext();

        await module.HandleAsync(
            Envelope(new StepRequestEvent
            {
                StepId = "parallel-json",
                StepType = "parallel",
                RunId = "run-parallel-json",
                Input = "translate me",
                Parameters =
                {
                    ["workers"] = "[\"worker_a\",\"worker_b\",\"worker_c\"]",
                },
            }),
            ctx,
            CancellationToken.None);

        var dispatched = ctx.Published.Select(x => x.evt).OfType<StepRequestEvent>().ToList();
        dispatched.Should().HaveCount(3);
        dispatched[0].TargetRole.Should().Be("worker_a");
        dispatched[1].TargetRole.Should().Be("worker_b");
        dispatched[2].TargetRole.Should().Be("worker_c");
        ctx.Published.Clear();

        await module.HandleAsync(
            Envelope(new StepCompletedEvent
            {
                StepId = "parallel-json_sub_0",
                RunId = "run-parallel-json",
                Success = true,
                Output = "A",
            }),
            ctx,
            CancellationToken.None);
        await module.HandleAsync(
            Envelope(new StepCompletedEvent
            {
                StepId = "parallel-json_sub_1",
                RunId = "run-parallel-json",
                Success = true,
                Output = "B",
            }),
            ctx,
            CancellationToken.None);
        await module.HandleAsync(
            Envelope(new StepCompletedEvent
            {
                StepId = "parallel-json_sub_2",
                RunId = "run-parallel-json",
                Success = true,
                Output = "C",
            }),
            ctx,
            CancellationToken.None);

        var merged = ctx.Published.Select(x => x.evt).OfType<StepCompletedEvent>().Single();
        merged.StepId.Should().Be("parallel-json");
        merged.RunId.Should().Be("run-parallel-json");
        merged.Success.Should().BeTrue();
        merged.Output.Should().Be("A\n---\nB\n---\nC");
    }

    [Fact]
    public async Task ParallelFanOutModule_WhenMissingWorkersAndRole_ShouldFailFast()
    {
        var module = new ParallelFanOutModule();
        var ctx = CreateContext();

        await module.HandleAsync(
            Envelope(new StepRequestEvent
            {
                StepId = "parallel-missing-role",
                StepType = "parallel",
                RunId = "run-parallel-missing-role",
                Input = "x",
            }),
            ctx,
            CancellationToken.None);

        var failed = ctx.Published.Select(x => x.evt).OfType<StepCompletedEvent>().Single();
        failed.StepId.Should().Be("parallel-missing-role");
        failed.RunId.Should().Be("run-parallel-missing-role");
        failed.Success.Should().BeFalse();
        failed.Error.Should().Contain("parallel requires parameters.workers");
    }

    [Fact]
    public async Task MapReduceModule_ShouldSupportJsonArrayInputAndEscapedDelimiter()
    {
        var module = new MapReduceModule();
        var ctx = CreateContext();

        await module.HandleAsync(
            Envelope(new StepRequestEvent
            {
                StepId = "mr-json",
                StepType = "map_reduce",
                RunId = "run-mr-json",
                Input = "[\"a\",\"b\"]",
                Parameters =
                {
                    ["map_step_type"] = "transform",
                    ["reduce_step_type"] = "",
                },
            }),
            ctx,
            CancellationToken.None);

        var mapDispatches = ctx.Published.Select(x => x.evt).OfType<StepRequestEvent>().ToList();
        mapDispatches.Should().HaveCount(2);
        mapDispatches[0].Input.Should().Be("a");
        mapDispatches[1].Input.Should().Be("b");
        ctx.Published.Clear();

        await module.HandleAsync(
            Envelope(new StepCompletedEvent
            {
                StepId = "mr-json_map_0",
                RunId = "run-mr-json",
                Success = true,
                Output = "A",
            }),
            ctx,
            CancellationToken.None);
        await module.HandleAsync(
            Envelope(new StepCompletedEvent
            {
                StepId = "mr-json_map_1",
                RunId = "run-mr-json",
                Success = true,
                Output = "B",
            }),
            ctx,
            CancellationToken.None);

        var merged = ctx.Published.Select(x => x.evt).OfType<StepCompletedEvent>().Single();
        merged.StepId.Should().Be("mr-json");
        merged.RunId.Should().Be("run-mr-json");
        merged.Success.Should().BeTrue();
        merged.Output.Should().Be("A\n---\nB");
        ctx.Published.Clear();

        await module.HandleAsync(
            Envelope(new StepRequestEvent
            {
                StepId = "mr-delimiter",
                StepType = "map_reduce",
                RunId = "run-mr-delimiter",
                Input = "x\n---\ny",
                Parameters =
                {
                    ["delimiter"] = "\\n---\\n",
                    ["map_step_type"] = "transform",
                    ["reduce_step_type"] = "",
                },
            }),
            ctx,
            CancellationToken.None);

        var escapedDispatches = ctx.Published.Select(x => x.evt).OfType<StepRequestEvent>().ToList();
        escapedDispatches.Should().HaveCount(2);
        escapedDispatches[0].Input.Should().Be("x");
        escapedDispatches[1].Input.Should().Be("y");
    }

    [Fact]
    public async Task InteractionModules_ShouldSupportTimeoutAliases()
    {
        var waitSignal = new WaitSignalModule();
        var approval = new HumanApprovalModule();
        var input = new HumanInputModule();
        var ctx = CreateContext();

        await waitSignal.HandleAsync(
            Envelope(new StepRequestEvent
            {
                StepId = "wait-timeout-alias",
                StepType = "wait_signal",
                RunId = "run-timeout-alias",
                Parameters =
                {
                    ["signal"] = "go",
                    ["timeout"] = "2",
                },
            }),
            ctx,
            CancellationToken.None);

        var waiting = ctx.Published.Select(x => x.evt).OfType<WaitingForSignalEvent>().Single();
        waiting.SignalName.Should().Be("go");
        waiting.TimeoutMs.Should().Be(2000);
        ctx.Published.Clear();

        await approval.HandleAsync(
            Envelope(new StepRequestEvent
            {
                StepId = "approval-timeout-ms",
                StepType = "human_approval",
                RunId = "run-timeout-alias",
                Parameters =
                {
                    ["timeout_ms"] = "2500",
                },
            }),
            ctx,
            CancellationToken.None);

        var approvalSuspended = ctx.Published.Select(x => x.evt).OfType<WorkflowSuspendedEvent>().Single();
        approvalSuspended.TimeoutSeconds.Should().Be(3);
        ctx.Published.Clear();

        await input.HandleAsync(
            Envelope(new StepRequestEvent
            {
                StepId = "input-timeout-seconds",
                StepType = "human_input",
                RunId = "run-timeout-alias",
                Parameters =
                {
                    ["timeout_seconds"] = "7",
                },
            }),
            ctx,
            CancellationToken.None);

        var inputSuspended = ctx.Published.Select(x => x.evt).OfType<WorkflowSuspendedEvent>().Single();
        inputSuspended.TimeoutSeconds.Should().Be(7);
    }

    [Fact]
    public async Task MapReduceModule_ShouldCoverEmptyInputReduceAndMapFailurePaths()
    {
        var module = new MapReduceModule();
        var ctx = CreateContext();

        await module.HandleAsync(
            Envelope(new StepRequestEvent
            {
                StepId = "mr-empty",
                StepType = "map_reduce",
                Input = "",
            }),
            ctx,
            CancellationToken.None);

        var empty = ctx.Published.Select(x => x.evt).OfType<StepCompletedEvent>().Single();
        empty.StepId.Should().Be("mr-empty");
        empty.Success.Should().BeTrue();
        empty.Output.Should().BeEmpty();
        ctx.Published.Clear();

        await module.HandleAsync(
            Envelope(new StepRequestEvent
            {
                StepId = "mr-1",
                StepType = "map_reduce",
                Input = "a\n---\nb",
                Parameters =
                {
                    ["map_step_type"] = "transform",
                    ["reduce_step_type"] = "llm_call",
                    ["reduce_prompt_prefix"] = "summarize",
                },
            }),
            ctx,
            CancellationToken.None);

        var mapDispatches = ctx.Published.Select(x => x.evt).OfType<StepRequestEvent>().ToList();
        mapDispatches.Should().HaveCount(2);
        ctx.Published.Clear();

        await module.HandleAsync(Envelope(new StepCompletedEvent { StepId = "mr-1_map_0", Success = true, Output = "A" }), ctx, CancellationToken.None);
        await module.HandleAsync(Envelope(new StepCompletedEvent { StepId = "mr-1_map_1", Success = true, Output = "B" }), ctx, CancellationToken.None);

        var reduceDispatch = ctx.Published.Select(x => x.evt).OfType<StepRequestEvent>().Single();
        reduceDispatch.StepId.Should().Be("mr-1_reduce");
        reduceDispatch.Input.Should().Contain("summarize");
        reduceDispatch.Input.Should().Contain("A");
        reduceDispatch.Input.Should().Contain("B");
        ctx.Published.Clear();

        await module.HandleAsync(
            Envelope(new StepCompletedEvent
            {
                StepId = "mr-1_reduce",
                Success = true,
                Output = "FINAL",
            }),
            ctx,
            CancellationToken.None);

        var reduced = ctx.Published.Select(x => x.evt).OfType<StepCompletedEvent>().Single();
        reduced.StepId.Should().Be("mr-1");
        reduced.Success.Should().BeTrue();
        reduced.Output.Should().Be("FINAL");
        reduced.Metadata["map_reduce.phase"].Should().Be("reduce");
        ctx.Published.Clear();

        await module.HandleAsync(
            Envelope(new StepRequestEvent
            {
                StepId = "mr-2",
                StepType = "map_reduce",
                Input = "x\n---\ny",
                Parameters = { ["reduce_step_type"] = "" },
            }),
            ctx,
            CancellationToken.None);
        ctx.Published.Clear();

        await module.HandleAsync(Envelope(new StepCompletedEvent { StepId = "mr-2_map_0", Success = true, Output = "X" }), ctx, CancellationToken.None);
        await module.HandleAsync(Envelope(new StepCompletedEvent { StepId = "mr-2_map_1", Success = false, Output = "Y" }), ctx, CancellationToken.None);

        var mapFailed = ctx.Published.Select(x => x.evt).OfType<StepCompletedEvent>().Single();
        mapFailed.StepId.Should().Be("mr-2");
        mapFailed.Success.Should().BeFalse();
        mapFailed.Output.Should().Be("X\n---\nY");
        mapFailed.Error.Should().Contain("one or more map steps failed");
    }

    [Fact]
    public async Task EvaluateModule_ShouldBranchOnLowScoreAndPassOnHighScore()
    {
        var module = new EvaluateModule();
        var ctx = CreateContext();

        await module.HandleAsync(
            Envelope(new StepRequestEvent
            {
                StepId = "eval-1",
                StepType = "evaluate",
                Input = "draft content",
                Parameters =
                {
                    ["criteria"] = "quality",
                    ["threshold"] = "4",
                    ["on_below"] = "retry_path",
                },
            }),
            ctx,
            CancellationToken.None);

        var judgeRequest = ctx.Published.Select(x => x.evt).OfType<ChatRequestEvent>().Single();
        var firstSessionId = judgeRequest.SessionId;
        ctx.Published.Clear();

        await module.HandleAsync(
            Envelope(new TextMessageEndEvent
            {
                SessionId = firstSessionId,
                Content = "score: 3.5",
            }),
            ctx,
            CancellationToken.None);

        var lowScore = ctx.Published.Select(x => x.evt).OfType<StepCompletedEvent>().Single();
        lowScore.StepId.Should().Be("eval-1");
        lowScore.Success.Should().BeTrue();
        lowScore.Metadata["evaluate.score"].Should().Be("3.5");
        lowScore.Metadata["evaluate.passed"].Should().Be("False");
        lowScore.Metadata["branch"].Should().Be("retry_path");
        ctx.Published.Clear();

        await module.HandleAsync(
            Envelope(new StepRequestEvent
            {
                StepId = "eval-2",
                StepType = "evaluate",
                Input = "second draft",
                Parameters = { ["threshold"] = "2" },
            }),
            ctx,
            CancellationToken.None);
        var secondSessionId = ctx.Published.Select(x => x.evt).OfType<ChatRequestEvent>().Single().SessionId;
        ctx.Published.Clear();

        await module.HandleAsync(
            Envelope(new ChatResponseEvent
            {
                SessionId = secondSessionId,
                Content = "5",
            }),
            ctx,
            CancellationToken.None);

        var highScore = ctx.Published.Select(x => x.evt).OfType<StepCompletedEvent>().Single();
        highScore.StepId.Should().Be("eval-2");
        highScore.Metadata["evaluate.passed"].Should().Be("True");
        highScore.Metadata.Should().NotContainKey("branch");
    }

    [Fact]
    public async Task LlmCallModule_ShouldDispatchViaAgentTypeAndForwardStepParametersAsMetadata()
    {
        var runtime = new RecordingActorRuntimeForAgentType();
        var services = new ServiceCollection()
            .AddSingleton<IActorRuntime>(runtime)
            .AddAevatarWorkflow()
            .BuildServiceProvider();
        var module = new LLMCallModule();
        var ctx = CreateContext(services);

        var request = new StepRequestEvent
        {
            StepId = "llm-agent-type",
            StepType = "llm_call",
            RunId = "run-agent-type",
            Input = "hello bridge",
            TargetRole = "legacy-role",
        };
        request.Parameters["agent_type"] = typeof(AgentTypeDispatchTargetAgent).AssemblyQualifiedName!;
        request.Parameters["agent_id"] = "bridge:telegram:prod";
        request.Parameters["chat_id"] = "10001";
        request.Parameters["llm_timeout_ms"] = "120000";

        await module.HandleAsync(Envelope(request), ctx, CancellationToken.None);

        ctx.Sent.Should().ContainSingle();
        ctx.Sent[0].targetActorId.Should().Be("bridge:telegram:prod");
        var chatRequest = ctx.Sent[0].evt.Should().BeOfType<ChatRequestEvent>().Subject;
        chatRequest.Metadata["chat_id"].Should().Be("10001");
        runtime.Created.Should().ContainSingle(x => x.actorId == "bridge:telegram:prod");

        await module.HandleAsync(
            Envelope(new ChatResponseEvent
            {
                SessionId = chatRequest.SessionId,
                Content = "telegram-ack",
            }),
            ctx,
            CancellationToken.None);

        var completed = ctx.Published.Select(x => x.evt).OfType<StepCompletedEvent>().Single();
        completed.StepId.Should().Be("llm-agent-type");
        completed.Success.Should().BeTrue();
        completed.Output.Should().Be("telegram-ack");
    }

    [Fact]
    public async Task EvaluateAndReflectModules_ShouldDispatchViaAgentType()
    {
        var runtime = new RecordingActorRuntimeForAgentType();
        var services = new ServiceCollection()
            .AddSingleton<IActorRuntime>(runtime)
            .AddAevatarWorkflow()
            .BuildServiceProvider();
        var ctx = CreateContext(services);

        var evaluate = new EvaluateModule();
        var evaluateRequest = new StepRequestEvent
        {
            StepId = "eval-agent-type",
            StepType = "evaluate",
            RunId = "run-eval-agent-type",
            Input = "draft",
        };
        evaluateRequest.Parameters["agent_type"] = typeof(AgentTypeDispatchTargetAgent).AssemblyQualifiedName!;
        evaluateRequest.Parameters["agent_id"] = "agent:evaluate";
        evaluateRequest.Parameters["chat_id"] = "chat-eval";
        evaluateRequest.Parameters["threshold"] = "2";
        await evaluate.HandleAsync(Envelope(evaluateRequest), ctx, CancellationToken.None);

        ctx.Sent.Should().ContainSingle(x => x.targetActorId == "agent:evaluate");
        var evaluateChat = ctx.Sent.Last().evt.Should().BeOfType<ChatRequestEvent>().Subject;
        evaluateChat.Metadata["chat_id"].Should().Be("chat-eval");
        ctx.Published.Clear();

        await evaluate.HandleAsync(
            Envelope(new ChatResponseEvent
            {
                SessionId = evaluateChat.SessionId,
                Content = "3",
            }),
            ctx,
            CancellationToken.None);
        ctx.Published.Select(x => x.evt).OfType<StepCompletedEvent>()
            .Single(x => x.StepId == "eval-agent-type")
            .Success.Should().BeTrue();
        ctx.Published.Clear();

        var reflect = new ReflectModule();
        var reflectRequest = new StepRequestEvent
        {
            StepId = "reflect-agent-type",
            StepType = "reflect",
            RunId = "run-reflect-agent-type",
            Input = "draft-reflect",
        };
        reflectRequest.Parameters["agent_type"] = typeof(AgentTypeDispatchTargetAgent).AssemblyQualifiedName!;
        reflectRequest.Parameters["agent_id"] = "agent:reflect";
        reflectRequest.Parameters["chat_id"] = "chat-reflect";
        reflectRequest.Parameters["max_rounds"] = "1";
        await reflect.HandleAsync(Envelope(reflectRequest), ctx, CancellationToken.None);

        ctx.Sent.Should().Contain(x => x.targetActorId == "agent:reflect");
        var reflectChat = ctx.Sent.Last().evt.Should().BeOfType<ChatRequestEvent>().Subject;
        reflectChat.Metadata["chat_id"].Should().Be("chat-reflect");
        ctx.Published.Clear();

        await reflect.HandleAsync(
            Envelope(new ChatResponseEvent
            {
                SessionId = reflectChat.SessionId,
                Content = "PASS",
            }),
            ctx,
            CancellationToken.None);
        ctx.Published.Select(x => x.evt).OfType<StepCompletedEvent>()
            .Single(x => x.StepId == "reflect-agent-type")
            .Success.Should().BeTrue();
    }

    [Fact]
    public async Task ReflectModule_ShouldHandlePassPathAndIterativeImprovementPath()
    {
        var module = new ReflectModule();
        var ctx = CreateContext();

        await module.HandleAsync(
            Envelope(new StepRequestEvent
            {
                StepId = "reflect-1",
                StepType = "reflect",
                Input = "draft-1",
                Parameters = { ["max_rounds"] = "3" },
            }),
            ctx,
            CancellationToken.None);
        var firstCritiqueSession = ctx.Published.Select(x => x.evt).OfType<ChatRequestEvent>().Single().SessionId;
        ctx.Published.Clear();

        await module.HandleAsync(
            Envelope(new ChatResponseEvent
            {
                SessionId = firstCritiqueSession,
                Content = "PASS",
            }),
            ctx,
            CancellationToken.None);

        var passCompleted = ctx.Published.Select(x => x.evt).OfType<StepCompletedEvent>().Single();
        passCompleted.StepId.Should().Be("reflect-1");
        passCompleted.Success.Should().BeTrue();
        passCompleted.Output.Should().Be("draft-1");
        passCompleted.Metadata["reflect.rounds"].Should().Be("1");
        passCompleted.Metadata["reflect.passed"].Should().Be("True");
        ctx.Published.Clear();

        await module.HandleAsync(
            Envelope(new StepRequestEvent
            {
                StepId = "reflect-2",
                StepType = "reflect",
                Input = "draft-2",
                Parameters = { ["max_rounds"] = "2" },
            }),
            ctx,
            CancellationToken.None);
        var critiqueSession0 = ctx.Published.Select(x => x.evt).OfType<ChatRequestEvent>().Single().SessionId;
        ctx.Published.Clear();

        await module.HandleAsync(
            Envelope(new ChatResponseEvent
            {
                SessionId = critiqueSession0,
                Content = "Needs improvement",
            }),
            ctx,
            CancellationToken.None);
        var improveSession = ctx.Published.Select(x => x.evt).OfType<ChatRequestEvent>().Single().SessionId;
        ctx.Published.Clear();

        await module.HandleAsync(
            Envelope(new TextMessageEndEvent
            {
                SessionId = improveSession,
                Content = "draft-2-better",
            }),
            ctx,
            CancellationToken.None);
        var critiqueSession1 = ctx.Published.Select(x => x.evt).OfType<ChatRequestEvent>().Single().SessionId;
        ctx.Published.Clear();

        await module.HandleAsync(
            Envelope(new ChatResponseEvent
            {
                SessionId = critiqueSession1,
                Content = "still not good",
            }),
            ctx,
            CancellationToken.None);

        var maxRoundCompleted = ctx.Published.Select(x => x.evt).OfType<StepCompletedEvent>().Single();
        maxRoundCompleted.StepId.Should().Be("reflect-2");
        maxRoundCompleted.Success.Should().BeTrue();
        maxRoundCompleted.Output.Should().Be("draft-2-better");
        maxRoundCompleted.Metadata["reflect.rounds"].Should().Be("2");
        maxRoundCompleted.Metadata["reflect.passed"].Should().Be("False");
    }

    [Fact]
    public async Task ReflectModule_ShouldIsolateConcurrentRunsWithSameStepId()
    {
        var module = new ReflectModule();
        var ctx = CreateContext();

        await module.HandleAsync(
            Envelope(new StepRequestEvent
            {
                StepId = "reflect-shared",
                StepType = "reflect",
                RunId = "run-a",
                Input = "draft-a",
                Parameters = { ["max_rounds"] = "2" },
            }),
            ctx,
            CancellationToken.None);
        var sessionA = ctx.Published.Select(x => x.evt).OfType<ChatRequestEvent>().Single().SessionId;
        ctx.Published.Clear();

        await module.HandleAsync(
            Envelope(new StepRequestEvent
            {
                StepId = "reflect-shared",
                StepType = "reflect",
                RunId = "run-b",
                Input = "draft-b",
                Parameters = { ["max_rounds"] = "2" },
            }),
            ctx,
            CancellationToken.None);
        var sessionB = ctx.Published.Select(x => x.evt).OfType<ChatRequestEvent>().Single().SessionId;
        ctx.Published.Clear();

        await module.HandleAsync(
            Envelope(new ChatResponseEvent
            {
                SessionId = sessionB,
                Content = "PASS",
            }),
            ctx,
            CancellationToken.None);
        var completedB = ctx.Published.Select(x => x.evt).OfType<StepCompletedEvent>().Single();
        completedB.RunId.Should().Be("run-b");
        completedB.StepId.Should().Be("reflect-shared");
        completedB.Output.Should().Be("draft-b");
        ctx.Published.Clear();

        await module.HandleAsync(
            Envelope(new ChatResponseEvent
            {
                SessionId = sessionA,
                Content = "PASS",
            }),
            ctx,
            CancellationToken.None);
        var completedA = ctx.Published.Select(x => x.evt).OfType<StepCompletedEvent>().Single();
        completedA.RunId.Should().Be("run-a");
        completedA.StepId.Should().Be("reflect-shared");
        completedA.Output.Should().Be("draft-a");
    }

    [Fact]
    public async Task HumanApprovalModule_ShouldSetBranchMetadataOnApproval()
    {
        var module = new HumanApprovalModule();
        var ctx = CreateContext();

        await module.HandleAsync(
            Envelope(new StepRequestEvent
            {
                StepId = "branch-approve",
                StepType = "human_approval",
                RunId = "run-branch-1",
                Input = "pending-content",
            }),
            ctx,
            CancellationToken.None);
        ctx.Published.Clear();

        await module.HandleAsync(
            Envelope(new WorkflowResumedEvent
            {
                RunId = "run-branch-1",
                StepId = "branch-approve",
                Approved = true,
                UserInput = "looks good",
            }),
            ctx,
            CancellationToken.None);

        var approved = ctx.Published.Select(x => x.evt).OfType<StepCompletedEvent>().Single();
        approved.Success.Should().BeTrue();
        approved.Output.Should().Be("looks good");
        approved.Metadata["branch"].Should().Be("true");
    }

    [Fact]
    public async Task HumanApprovalModule_ShouldSetBranchMetadataAndUserFeedbackOnRejection()
    {
        var module = new HumanApprovalModule();
        var ctx = CreateContext();

        await module.HandleAsync(
            Envelope(new StepRequestEvent
            {
                StepId = "branch-reject",
                StepType = "human_approval",
                RunId = "run-branch-2",
                Input = "original-yaml",
                Parameters = { ["on_reject"] = "skip" },
            }),
            ctx,
            CancellationToken.None);
        ctx.Published.Clear();

        await module.HandleAsync(
            Envelope(new WorkflowResumedEvent
            {
                RunId = "run-branch-2",
                StepId = "branch-reject",
                Approved = false,
                UserInput = "change the model to gpt-4",
            }),
            ctx,
            CancellationToken.None);

        var rejected = ctx.Published.Select(x => x.evt).OfType<StepCompletedEvent>().Single();
        rejected.Success.Should().BeTrue();
        rejected.Metadata["branch"].Should().Be("false");
        rejected.Output.Should().Contain("original-yaml");
        rejected.Output.Should().Contain("change the model to gpt-4");
    }

    [Fact]
    public async Task HumanApprovalModule_RejectionWithoutUserInput_ShouldPreserveOriginalInput()
    {
        var module = new HumanApprovalModule();
        var ctx = CreateContext();

        await module.HandleAsync(
            Envelope(new StepRequestEvent
            {
                StepId = "branch-reject-empty",
                StepType = "human_approval",
                RunId = "run-branch-3",
                Input = "keep-me",
                Parameters = { ["on_reject"] = "skip" },
            }),
            ctx,
            CancellationToken.None);
        ctx.Published.Clear();

        await module.HandleAsync(
            Envelope(new WorkflowResumedEvent
            {
                RunId = "run-branch-3",
                StepId = "branch-reject-empty",
                Approved = false,
            }),
            ctx,
            CancellationToken.None);

        var rejected = ctx.Published.Select(x => x.evt).OfType<StepCompletedEvent>().Single();
        rejected.Metadata["branch"].Should().Be("false");
        rejected.Output.Should().Be("keep-me");
    }

    [Fact]
    public async Task DynamicWorkflowModule_ShouldExtractYamlAndPublishReconfigureEvent()
    {
        var module = new DynamicWorkflowModule();
        var ctx = CreateContext();

        var input = """
            Here is the workflow I designed:

            ```yaml
            name: analysis
            description: Multi-step analysis
            roles:
              - id: analyst
                system_prompt: You analyze data.
            steps:
              - id: analyze
                type: llm_call
                role: analyst
            ```

            This workflow will analyze the data in two steps.
            """;

        await module.HandleAsync(
            Envelope(new StepRequestEvent
            {
                StepId = "dw-1",
                StepType = "dynamic_workflow",
                RunId = "run-dw-1",
                Input = input,
                Parameters = { ["original_input"] = "analyze my data" },
            }),
            ctx,
            CancellationToken.None);

        var reconfigure = ctx.Published.Select(x => x.evt).OfType<ReplaceWorkflowDefinitionAndExecuteEvent>().Single();
        reconfigure.WorkflowYaml.Should().Contain("name: analysis");
        reconfigure.WorkflowYaml.Should().Contain("analyst");
        reconfigure.Input.Should().Be("analyze my data");
    }

    [Fact]
    public async Task DynamicWorkflowModule_WhenNoYamlBlock_ShouldFailWithError()
    {
        var module = new DynamicWorkflowModule();
        var ctx = CreateContext();

        await module.HandleAsync(
            Envelope(new StepRequestEvent
            {
                StepId = "dw-2",
                StepType = "dynamic_workflow",
                RunId = "run-dw-2",
                Input = "No yaml here, just plain text.",
            }),
            ctx,
            CancellationToken.None);

        var completed = ctx.Published.Select(x => x.evt).OfType<StepCompletedEvent>().Single();
        completed.StepId.Should().Be("dw-2");
        completed.Success.Should().BeFalse();
        completed.Error.Should().Contain("No workflow YAML found");
    }

    [Fact]
    public async Task DynamicWorkflowModule_ShouldIgnoreNonDynamicWorkflowStepType()
    {
        var module = new DynamicWorkflowModule();
        var ctx = CreateContext();

        await module.HandleAsync(
            Envelope(new StepRequestEvent
            {
                StepId = "other-step",
                StepType = "llm_call",
                RunId = "run-other",
                Input = "hello",
            }),
            ctx,
            CancellationToken.None);

        ctx.Published.Should().BeEmpty();
    }

    [Fact]
    public async Task DynamicWorkflowModule_WithYmlFence_ShouldAlsoExtractYaml()
    {
        var module = new DynamicWorkflowModule();
        var ctx = CreateContext();

        await module.HandleAsync(
            Envelope(new StepRequestEvent
            {
                StepId = "dw-yml",
                StepType = "dynamic_workflow",
                RunId = "run-dw-yml",
                Input = "```yml\nname: test2\nroles: []\nsteps:\n  - id: s1\n    type: assign\n```",
                Parameters = { ["original_input"] = "hello" },
            }),
            ctx,
            CancellationToken.None);

        var reconfigure = ctx.Published.Select(x => x.evt).OfType<ReplaceWorkflowDefinitionAndExecuteEvent>().Single();
        reconfigure.WorkflowYaml.Should().Contain("name: test2");
        reconfigure.Input.Should().Be("hello");
    }

    [Fact]
    public async Task DynamicWorkflowModule_WhenYamlValidationFails_ShouldEmitFailedStepAndSkipReconfigure()
    {
        var module = new DynamicWorkflowModule();
        var ctx = CreateContext();

        await module.HandleAsync(
            Envelope(new StepRequestEvent
            {
                StepId = "dw-invalid",
                StepType = "dynamic_workflow",
                RunId = "run-dw-invalid",
                Input = """
                        ```yaml
                        name: bad_flow
                        roles: []
                        steps:
                          - id: bad_step
                            type: unknown_step
                        ```
                        """,
            }),
            ctx,
            CancellationToken.None);

        ctx.Published.Select(x => x.evt).OfType<ReplaceWorkflowDefinitionAndExecuteEvent>().Should().BeEmpty();
        var completed = ctx.Published.Select(x => x.evt).OfType<StepCompletedEvent>().Single();
        completed.Success.Should().BeFalse();
        completed.Error.Should().Contain("Invalid workflow YAML");
    }

    [Fact]
    public async Task WorkflowYamlValidateModule_WhenYamlIsValid_ShouldReturnCanonicalYamlFence()
    {
        var module = new WorkflowYamlValidateModule();
        var ctx = CreateContext();

        await module.HandleAsync(
            Envelope(new StepRequestEvent
            {
                StepId = "validate-1",
                StepType = "workflow_yaml_validate",
                RunId = "run-validate-1",
                Input = """
                        ```yaml
                        name: validate_ok
                        roles: []
                        steps:
                          - id: done
                            type: assign
                            parameters:
                              target: result
                              value: "$input"
                        ```
                        """,
            }),
            ctx,
            CancellationToken.None);

        var completed = ctx.Published.Select(x => x.evt).OfType<StepCompletedEvent>().Single();
        completed.Success.Should().BeTrue();
        completed.Output.Should().Contain("```yaml");
        completed.Output.Should().Contain("name: validate_ok");
    }

    [Fact]
    public async Task WorkflowYamlValidateModule_WhenYamlContainsDynamicWorkflowStep_ShouldFailValidation()
    {
        var module = new WorkflowYamlValidateModule();
        var ctx = CreateContext();

        await module.HandleAsync(
            Envelope(new StepRequestEvent
            {
                StepId = "validate-dynamic-workflow",
                StepType = "workflow_yaml_validate",
                RunId = "run-validate-dynamic-workflow",
                Input = """
                        ```yaml
                        name: dynamic_workflow_not_allowed
                        roles: []
                        steps:
                          - id: ensure_runtime_ready
                            type: dynamic_workflow
                          - id: done
                            type: assign
                            parameters:
                              target: result
                              value: "$input"
                        ```
                        """,
            }),
            ctx,
            CancellationToken.None);

        var completed = ctx.Published.Select(x => x.evt).OfType<StepCompletedEvent>().Single();
        completed.Success.Should().BeFalse();
        completed.Error.Should().Contain("dynamic_workflow");
    }

    private sealed class RecordingActorRuntimeForAgentType : IActorRuntime
    {
        private readonly Dictionary<string, IActor> _actors = new(StringComparer.Ordinal);
        public List<(System.Type agentType, string actorId)> Created { get; } = [];

        public Task<IActor> CreateAsync<TAgent>(string? id = null, CancellationToken ct = default)
            where TAgent : IAgent =>
            CreateAsync(typeof(TAgent), id, ct);

        public Task<IActor> CreateAsync(System.Type agentType, string? id = null, CancellationToken ct = default)
        {
            var actorId = string.IsNullOrWhiteSpace(id) ? Guid.NewGuid().ToString("N") : id;
            var agent = (IAgent)Activator.CreateInstance(agentType, actorId)!;
            var actor = new RecordingRuntimeActor(actorId, agent);
            _actors[actorId] = actor;
            Created.Add((agentType, actorId));
            return Task.FromResult<IActor>(actor);
        }

        public Task DestroyAsync(string id, CancellationToken ct = default)
        {
            _actors.Remove(id);
            return Task.CompletedTask;
        }

        public Task<IActor?> GetAsync(string id)
        {
            _actors.TryGetValue(id, out var actor);
            return Task.FromResult(actor);
        }

        public Task<bool> ExistsAsync(string id) => Task.FromResult(_actors.ContainsKey(id));

        public Task LinkAsync(string parentId, string childId, CancellationToken ct = default) => Task.CompletedTask;

        public Task UnlinkAsync(string childId, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class RecordingRuntimeActor(string id, IAgent agent) : IActor
    {
        public string Id { get; } = id;
        public IAgent Agent { get; } = agent;
        public Task ActivateAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default) => Task.CompletedTask;
        public Task<string?> GetParentIdAsync() => Task.FromResult<string?>(null);
        public Task<IReadOnlyList<string>> GetChildrenIdsAsync() => Task.FromResult<IReadOnlyList<string>>([]);
    }

    private sealed class AgentTypeDispatchTargetAgent(string id) : IAgent
    {
        public string Id { get; } = id;
        public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default) => Task.CompletedTask;
        public Task<string> GetDescriptionAsync() => Task.FromResult("agent-type-target");
        public Task<IReadOnlyList<System.Type>> GetSubscribedEventTypesAsync() => Task.FromResult<IReadOnlyList<System.Type>>([]);
        public Task ActivateAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    private static RecordingEventHandlerContext CreateContext(IServiceProvider? services = null)
    {
        return new RecordingEventHandlerContext(
            services ?? new ServiceCollection().AddAevatarWorkflow().BuildServiceProvider(),
            new StubAgent("workflow-advanced-module-test-agent"),
            NullLogger.Instance);
    }

    private static EventEnvelope Envelope(IMessage evt, string? publisherId = null)
    {
        return new EventEnvelope
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
            Payload = Any.Pack(evt),
            PublisherId = publisherId ?? "test-publisher",
            Direction = EventDirection.Self,
        };
    }

    private sealed class RecordingEventHandlerContext : IEventHandlerContext
    {
        public RecordingEventHandlerContext(IServiceProvider services, IAgent agent, ILogger logger)
        {
            Services = services;
            Agent = agent;
            Logger = logger;
            InboundEnvelope = new EventEnvelope();
        }

        public List<(IMessage evt, EventDirection direction)> Published { get; } = [];
        public List<(string targetActorId, IMessage evt)> Sent { get; } = [];
        public EventEnvelope InboundEnvelope { get; }
        public string AgentId => Agent.Id;
        public IAgent Agent { get; }
        public IServiceProvider Services { get; }
        public ILogger Logger { get; }

        public Task PublishAsync<TEvent>(
            TEvent evt,
            EventDirection direction = EventDirection.Down,
            CancellationToken ct = default)
            where TEvent : IMessage
        {
            Published.Add((evt, direction));
            return Task.CompletedTask;
        }

        public Task SendToAsync<TEvent>(
            string targetActorId,
            TEvent evt,
            CancellationToken ct = default)
            where TEvent : IMessage
        {
            Sent.Add((targetActorId, evt));
            return Task.CompletedTask;
        }
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
}
