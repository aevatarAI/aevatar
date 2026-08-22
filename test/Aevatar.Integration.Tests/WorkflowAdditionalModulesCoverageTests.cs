using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Connectors;
using Aevatar.Foundation.Core;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Abstractions.Execution;
using Aevatar.Workflow.Core;
using Aevatar.Workflow.Core.Execution;
using Aevatar.Workflow.Core.Modules;
using Aevatar.Foundation.Abstractions.Interactions;
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

        ctx.Published.Should().HaveCount(2);
        ctx.Published[0].direction.Should().Be(TopologyAudience.ParentAndChildren);
        ctx.Published[1].direction.Should().Be(TopologyAudience.Self);
        var emitted = ctx.Published[0].evt.Should().BeOfType<StepCompletedEvent>().Subject;
        emitted.Annotations["emit.event_type"].Should().Be("audit");
        emitted.Annotations["emit.payload"].Should().Be("{\"k\":1}");
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

        var defaultEmit = ctx.Published
            .Where(static publication => publication.direction == TopologyAudience.Self)
            .Select(static publication => publication.evt)
            .OfType<StepCompletedEvent>()
            .Single();
        defaultEmit.Annotations["emit.event_type"].Should().Be("custom");
        defaultEmit.Annotations["emit.payload"].Should().Be("fallback-payload");
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
        completions["switch-1"].BranchKey.Should().Be("foo");
        completions["switch-2"].BranchKey.Should().Be("bar");
        completions["switch-3"].BranchKey.Should().Be("_default");
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
    public async Task WaitSignalModule_WhenSignalRunIdMissingEvenWithSingleWaiter_ShouldNotResume()
    {
        var module = new WaitSignalModule();
        var ctx = CreateContext();

        await module.HandleAsync(
            Envelope(new StepRequestEvent
            {
                StepId = "wait-single",
                StepType = "wait_signal",
                RunId = "run-single",
                Input = "input-single",
                Parameters = { ["signal_name"] = "approval" },
            }),
            ctx,
            CancellationToken.None);
        ctx.Published.Clear();

        await module.HandleAsync(
            Envelope(new SignalReceivedEvent
            {
                SignalName = "approval",
                Payload = "ignored-without-run-id",
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
                Parameters =
                {
                    ["signal_name"] = "approval",
                    ["timeout_ms"] = "250",
                },
            }),
            ctx,
            CancellationToken.None);
        ctx.Published.Clear();
        var scheduled = ctx.Scheduled.Single(x => x.Event is WaitSignalTimeoutFiredEvent);

        await module.HandleAsync(
            ctx.CreateScheduledEnvelope(scheduled),
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
        ctx.UtcNow = DateTimeOffset.Parse("2026-05-20T10:00:00Z");

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
        ctx.UtcNow = ctx.UtcNow.AddMinutes(30);

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
        pendingCompletions.Should().OnlyContain(x => x.Annotations["cache.hit"] == "false");
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
        hitCompletion.Annotations["cache.hit"].Should().Be("true");

        ctx.Published.Clear();
        ctx.UtcNow = DateTimeOffset.Parse("2026-05-20T11:30:01Z");
        await module.HandleAsync(
            Envelope(new StepRequestEvent
            {
                StepId = "cache-4",
                StepType = "cache",
                Input = "after-expiry",
                Parameters =
                {
                    ["cache_key"] = "k1",
                    ["child_step_type"] = "transform",
                },
            }),
            ctx,
            CancellationToken.None);

        ctx.Published.Select(x => x.evt).OfType<StepRequestEvent>().Should().ContainSingle(x => x.StepId.StartsWith("cache-4_cached_", StringComparison.Ordinal));
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
        completions["guard-skip"].Annotations["guard.skipped"].Should().Be("true");
        completions["guard-branch"].Success.Should().BeTrue();
        completions["guard-branch"].NextStepId.Should().Be("manual_review");
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
                StepParameters = new WorkflowStepParameters
                {
                    DeliveryTargetId = "agent-approval-1",
                    InteractionSpec = new InteractionSpec
                    {
                        Title = "Approve release",
                        Body = "Ship it?",
                        Disposition = InteractionDisposition.Ephemeral,
                    },
                },
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
        suspended.Content.Should().Be("original");
        suspended.DeliveryTargetId.Should().Be("agent-approval-1");
        suspended.Interaction.Title.Should().Be("Approve release");
        suspended.Interaction.Body.Should().Be("Ship it?");
        suspended.Interaction.Disposition.Should().Be(InteractionDisposition.Ephemeral);
        ctx.Published.Clear();

        await module.HandleAsync(
            Envelope(new WorkflowResumedEvent
            {
                RunId = "run-1",
                StepId = "approval-1",
                Approved = true,
                UserInput = "legacy-approved-output",
                EditedContent = "approved-output",
                Feedback = "looks good",
            }),
            ctx,
            CancellationToken.None);

        var approved = ctx.Published.Select(x => x.evt).OfType<StepCompletedEvent>().Single();
        approved.StepId.Should().Be("approval-1");
        approved.RunId.Should().Be("run-1");
        approved.Success.Should().BeTrue();
        approved.Output.Should().Be("approved-output");
        var approvalResolved = ctx.Published.Select(x => x.evt).OfType<WorkflowHumanApprovalResolvedEvent>().Single();
        approvalResolved.RunId.Should().Be("run-1");
        approvalResolved.StepId.Should().Be("approval-1");
        approvalResolved.Approved.Should().BeTrue();
        approvalResolved.UserInput.Should().Be("legacy-approved-output");
        approvalResolved.EditedContent.Should().Be("approved-output");
        approvalResolved.Feedback.Should().Be("looks good");
        approvalResolved.DeliveryTargetId.Should().Be("agent-approval-1");
        approvalResolved.ResolvedContent.Should().Be("approved-output");
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
        ctx.Published.Select(x => x.evt).OfType<WorkflowHumanApprovalResolvedEvent>().Should().BeEmpty();
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
    public async Task HumanApprovalModule_WhenResumeRunIdMissing_ShouldIgnoreSinglePendingApproval()
    {
        var module = new HumanApprovalModule();
        var ctx = CreateContext();

        await module.HandleAsync(
            Envelope(new StepRequestEvent
            {
                StepId = "approval-missing-run",
                StepType = "human_approval",
                RunId = "run-approval",
                Input = "payload",
            }),
            ctx,
            CancellationToken.None);
        ctx.Published.Clear();

        await module.HandleAsync(
            Envelope(new WorkflowResumedEvent
            {
                StepId = "approval-missing-run",
                Approved = true,
                UserInput = "approved",
            }),
            ctx,
            CancellationToken.None);

        ctx.Published.Should().BeEmpty();
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
                StepParameters = new WorkflowStepParameters
                {
                    DeliveryTargetId = "agent-input-1",
                    InteractionSpec = new InteractionSpec
                    {
                        Title = "Clarify source",
                        Body = "Need a source note",
                    },
                },
                Parameters =
                {
                    ["prompt"] = "please type",
                    ["variable"] = "answer",
                },
            }),
            ctx,
            CancellationToken.None);

        var suspended = ctx.Published.Select(x => x.evt).OfType<WorkflowSuspendedEvent>().Single();
        suspended.VariableName.Should().Be("answer");
        suspended.Content.Should().Be("fallback");
        suspended.DeliveryTargetId.Should().Be("agent-input-1");
        suspended.Interaction.Title.Should().Be("Clarify source");
        suspended.Interaction.Body.Should().Be("Need a source note");
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
    public async Task WorkflowModules_ShouldRedactRawContentInInformationLogs()
    {
        const string sensitiveAssignValue = "customer secret assigned value";
        const string sensitiveHumanPrompt = "customer secret human prompt";
        const string sensitiveApprovalPrompt = "customer secret approval prompt";
        const string sensitiveFanoutInput = "customer secret fanout input";
        const string sensitiveLlmPrompt = "customer secret llm prompt";
        const string sensitiveLlmOutput = "customer secret llm output";
        var logger = new RecordingLogger();
        var ctx = CreateContext(logger: logger);

        await new AssignModule().HandleAsync(
            Envelope(new StepRequestEvent
            {
                StepId = "assign-log-redaction",
                StepType = "assign",
                RunId = "run-log-redaction",
                Parameters =
                {
                    ["target"] = "answer",
                    ["value"] = sensitiveAssignValue,
                },
            }),
            ctx,
            CancellationToken.None);

        await new HumanInputModule().HandleAsync(
            Envelope(new StepRequestEvent
            {
                StepId = "human-log-redaction",
                StepType = "human_input",
                RunId = "run-log-redaction",
                Parameters =
                {
                    ["prompt"] = sensitiveHumanPrompt,
                },
            }),
            ctx,
            CancellationToken.None);

        await new HumanApprovalModule().HandleAsync(
            Envelope(new StepRequestEvent
            {
                StepId = "approval-log-redaction",
                StepType = "human_approval",
                RunId = "run-log-redaction",
                Parameters =
                {
                    ["prompt"] = sensitiveApprovalPrompt,
                },
            }),
            ctx,
            CancellationToken.None);

        await new ParallelFanOutModule().HandleAsync(
            Envelope(new StepRequestEvent
            {
                StepId = "parallel-log-redaction",
                StepType = "parallel",
                RunId = "run-log-redaction",
                Input = sensitiveFanoutInput,
                Parameters =
                {
                    ["workers"] = "[\"worker_a\",\"worker_b\"]",
                },
            }),
            ctx,
            CancellationToken.None);

        var llmCall = new LLMCallModule();
        await llmCall.HandleAsync(
            Envelope(new StepRequestEvent
            {
                StepId = "llm-log-redaction",
                StepType = "llm_call",
                RunId = "run-log-redaction",
                Input = sensitiveLlmPrompt,
                TargetRole = "worker_a",
            }),
            ctx,
            CancellationToken.None);
        var llmSessionId = ctx.Sent.Select(x => x.evt).OfType<WorkflowLlmExecutionIntent>().Single().SessionId;
        await llmCall.HandleAsync(
            Envelope(new WorkflowLlmInvocationCompletedEvent
            {
                SessionId = llmSessionId,
                Success = true,
                Content = sensitiveLlmOutput,
            }),
            ctx,
            CancellationToken.None);

        var messages = logger.Messages.Should().NotBeEmpty().And.Subject;
        messages.Should().Contain(message =>
            message.Contains("value_redacted=true", StringComparison.Ordinal) &&
            message.Contains($"value_len={sensitiveAssignValue.Length}", StringComparison.Ordinal));
        messages.Should().Contain(message =>
            message.Contains("prompt_redacted=true", StringComparison.Ordinal) &&
            message.Contains($"prompt_len={sensitiveHumanPrompt.Length}", StringComparison.Ordinal));
        messages.Should().Contain(message =>
            message.Contains("prompt_redacted=true", StringComparison.Ordinal) &&
            message.Contains($"prompt_len={sensitiveApprovalPrompt.Length}", StringComparison.Ordinal));
        messages.Should().Contain(message =>
            message.Contains("input_redacted=true", StringComparison.Ordinal) &&
            message.Contains($"input_len={sensitiveFanoutInput.Length}", StringComparison.Ordinal));
        messages.Should().Contain(message =>
            message.Contains("prompt_redacted=true", StringComparison.Ordinal) &&
            message.Contains($"prompt_len={sensitiveLlmPrompt.Length}", StringComparison.Ordinal));
        messages.Should().Contain(message =>
            message.Contains("output_redacted=true", StringComparison.Ordinal) &&
            message.Contains($"output_len={sensitiveLlmOutput.Length}", StringComparison.Ordinal));
        messages.Should().NotContain(message => message.Contains(sensitiveAssignValue, StringComparison.Ordinal));
        messages.Should().NotContain(message => message.Contains(sensitiveHumanPrompt, StringComparison.Ordinal));
        messages.Should().NotContain(message => message.Contains(sensitiveApprovalPrompt, StringComparison.Ordinal));
        messages.Should().NotContain(message => message.Contains(sensitiveFanoutInput, StringComparison.Ordinal));
        messages.Should().NotContain(message => message.Contains(sensitiveLlmPrompt, StringComparison.Ordinal));
        messages.Should().NotContain(message => message.Contains(sensitiveLlmOutput, StringComparison.Ordinal));
    }

    [Fact]
    public async Task SwitchModule_ShouldRedactSensitiveSwitchInputInInformationLogs()
    {
        const string sensitiveSwitchInput = "customer secret switch input route-blue";
        var logger = new RecordingLogger();
        var ctx = CreateContext(logger: logger);

        await new SwitchModule().HandleAsync(
            Envelope(new StepRequestEvent
            {
                StepId = "switch-log-redaction",
                StepType = "switch",
                RunId = "run-switch-log-redaction",
                Parameters =
                {
                    ["on"] = sensitiveSwitchInput,
                    ["branch.blue"] = "blue-step",
                    ["branch._default"] = "fallback-step",
                },
            }),
            ctx,
            CancellationToken.None);

        var messages = logger.Messages.Should().NotBeEmpty().And.Subject;
        messages.Should().Contain(message =>
            message.Contains("value_redacted=true", StringComparison.Ordinal) &&
            message.Contains($"value_len={sensitiveSwitchInput.Length}", StringComparison.Ordinal));
        messages.Should().NotContain(message => message.Contains(sensitiveSwitchInput, StringComparison.Ordinal));
    }

    [Fact]
    public async Task LlmCallModule_ShouldRedactNonStreamingChatResponseInInformationLogs()
    {
        const string sensitiveLlmPrompt = "customer secret llm non streaming prompt";
        const string sensitiveLlmOutput = "customer secret llm non streaming output";
        var logger = new RecordingLogger();
        var ctx = CreateContext(logger: logger);
        var module = new LLMCallModule();

        await module.HandleAsync(
            Envelope(new StepRequestEvent
            {
                StepId = "llm-non-stream-log-redaction",
                StepType = "llm_call",
                RunId = "run-llm-non-stream-log-redaction",
                Input = sensitiveLlmPrompt,
                TargetRole = "worker_a",
            }),
            ctx,
            CancellationToken.None);

        var sessionId = ctx.Sent.Select(x => x.evt).OfType<WorkflowLlmExecutionIntent>().Single().SessionId;
        await module.HandleAsync(
            Envelope(new WorkflowLlmInvocationCompletedEvent
            {
                SessionId = sessionId,
                Success = true,
                Content = sensitiveLlmOutput,
            }),
            ctx,
            CancellationToken.None);

        var messages = logger.Messages.Should().NotBeEmpty().And.Subject;
        messages.Should().Contain(message =>
            message.Contains("status=completed", StringComparison.Ordinal) &&
            message.Contains("output_redacted=true", StringComparison.Ordinal) &&
            message.Contains($"output_len={sensitiveLlmOutput.Length}", StringComparison.Ordinal));
        messages.Should().NotContain(message => message.Contains(sensitiveLlmPrompt, StringComparison.Ordinal));
        messages.Should().NotContain(message => message.Contains(sensitiveLlmOutput, StringComparison.Ordinal));
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
    public async Task HumanInputModule_WhenResumeRunIdMissing_ShouldIgnoreSinglePendingInput()
    {
        var module = new HumanInputModule();
        var ctx = CreateContext();

        await module.HandleAsync(
            Envelope(new StepRequestEvent
            {
                StepId = "input-missing-run",
                StepType = "human_input",
                RunId = "run-input",
                Input = "payload",
            }),
            ctx,
            CancellationToken.None);
        ctx.Published.Clear();

        await module.HandleAsync(
            Envelope(new WorkflowResumedEvent
            {
                StepId = "input-missing-run",
                Approved = true,
                UserInput = "provided",
            }),
            ctx,
            CancellationToken.None);

        ctx.Published.Should().BeEmpty();
    }

    [Fact]
    public async Task SecureInputModule_ShouldCaptureMaskedValueAndPublishSecureEvent()
    {
        var services = new ServiceCollection().AddAevatarWorkflow().BuildServiceProvider();
        var agent = new TestWorkflowRunAgent("workflow-secure-module-test-agent", "run-secure");
        var module = new SecureInputModule();
        var ctx = new TestEventHandlerContext(services, agent, NullLogger.Instance);

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
                    ["redacted_output"] = "[api key captured]",
                    ["delivery_target_id"] = "agent-secure-1",
                },
            }),
            ctx,
            CancellationToken.None);

        var suspended = ctx.Published.Select(x => x.evt).OfType<WorkflowSuspendedEvent>().Single();
        suspended.SuspensionType.Should().Be("secure_input");
        suspended.VariableName.Should().Be("api_key");
        suspended.Secure.Should().BeTrue();
        suspended.RedactedOutput.Should().Be("[api key captured]");
        suspended.Metadata.Should().NotContainKey("secure");
        suspended.Metadata.Should().NotContainKey("variable");
        suspended.Metadata.Should().NotContainKey("input_mode");
        suspended.Metadata.Should().NotContainKey("redacted_output");
        suspended.Content.Should().BeEmpty();
        suspended.DeliveryTargetId.Should().Be("agent-secure-1");
        ctx.Published.Clear();

        var persistedState = ctx.LoadState<SecureInputModuleState>(SecureInputStateAccess.ModuleStateKey);
        persistedState.Pending.Should().ContainKey("run-secure::secure-1");

        var resumedModule = new SecureInputModule();
        var resumedCtx = new TestEventHandlerContext(services, agent, NullLogger.Instance);
        await resumedModule.HandleAsync(
            Envelope(new WorkflowResumedEvent
            {
                RunId = "run-secure",
                StepId = "secure-1",
                Approved = true,
                UserInput = "top-secret-value",
            }),
            resumedCtx,
            CancellationToken.None);

        var captured = resumedCtx.Published.Select(x => x.evt).OfType<SecureValueCapturedEvent>().Single();
        captured.Variable.Should().Be("api_key");
        captured.Value.Should().BeEmpty();

        var completed = resumedCtx.Published.Select(x => x.evt).OfType<StepCompletedEvent>().Single();
        completed.Success.Should().BeTrue();
        completed.Output.Should().Be("[api key captured]");
        completed.Annotations["secure.input"].Should().Be("true");
        completed.Annotations["secure.variable"].Should().Be("api_key");
        completed.Annotations["secure.redacted_output"].Should().Be("[api key captured]");

        var resumedState = resumedCtx.LoadState<SecureInputModuleState>(SecureInputStateAccess.ModuleStateKey);
        resumedState.Pending.Should().BeEmpty();
        resumedState.Captured.Should().ContainKey("run-secure::api_key");
        resumedState.Captured["run-secure::api_key"].Value.Should().BeEmpty(); resumedState.Captured["run-secure::api_key"].ValueReference.Should().NotBeNull(); (await SecureInputRuntimeContextAccess.TryGetCapturedValueAsync(resumedCtx, "run-secure", "api_key")).Should().Be((true, "top-secret-value"));

        await resumedModule.HandleAsync(
            Envelope(new WorkflowCompletedEvent
            {
                RunId = "run-secure",
            }),
            resumedCtx,
            CancellationToken.None);

        agent.GetExecutionState(SecureInputStateAccess.ModuleStateKey).Should().BeNull();
    }

    [Fact]
    public async Task SecureInputModule_ShouldClearPreviousCapturedValue_WhenSameVariableIsRequestedAgainAndRecaptureFails()
    {
        var services = new ServiceCollection().AddAevatarWorkflow().BuildServiceProvider();
        var agent = new TestWorkflowRunAgent("workflow-secure-recapture-test-agent", "run-secure-recapture");
        var module = new SecureInputModule();
        var ctx = new TestEventHandlerContext(services, agent, NullLogger.Instance);

        await module.HandleAsync(
            Envelope(new StepRequestEvent
            {
                StepId = "secure-1",
                StepType = "secure_input",
                RunId = "run-secure-recapture",
                Parameters =
                {
                    ["variable"] = "api_key",
                },
            }),
            ctx,
            CancellationToken.None);
        ctx.Published.Clear();

        await module.HandleAsync(
            Envelope(new WorkflowResumedEvent
            {
                RunId = "run-secure-recapture",
                StepId = "secure-1",
                Approved = true,
                UserInput = "old-secret",
            }),
            ctx,
            CancellationToken.None);

        var capturedState = ctx.LoadState<SecureInputModuleState>(SecureInputStateAccess.ModuleStateKey);
        capturedState.Captured["run-secure-recapture::api_key"].Value.Should().BeEmpty(); capturedState.Captured["run-secure-recapture::api_key"].ValueReference.Should().NotBeNull(); (await SecureInputRuntimeContextAccess.TryGetCapturedValueAsync(ctx, "run-secure-recapture", "api_key")).Should().Be((true, "old-secret"));
        ctx.Published.Clear();

        await module.HandleAsync(
            Envelope(new StepRequestEvent
            {
                StepId = "secure-2",
                StepType = "secure_input",
                RunId = "run-secure-recapture",
                Parameters =
                {
                    ["variable"] = "api_key",
                },
            }),
            ctx,
            CancellationToken.None);

        ctx.LoadState<SecureInputModuleState>(SecureInputStateAccess.ModuleStateKey)
            .Captured.Should().NotContainKey("run-secure-recapture::api_key");
        ctx.Published.Clear();

        await module.HandleAsync(
            Envelope(new WorkflowResumedEvent
            {
                RunId = "run-secure-recapture",
                StepId = "secure-2",
                Approved = false,
            }),
            ctx,
            CancellationToken.None);

        var failed = ctx.Published.Select(x => x.evt).OfType<StepCompletedEvent>().Single();
        failed.Success.Should().BeFalse();
        failed.Error.Should().Contain("timed out");
        ctx.LoadState<SecureInputModuleState>(SecureInputStateAccess.ModuleStateKey)
            .Captured.Should().NotContainKey("run-secure-recapture::api_key");
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
        winner.Annotations["race.winner"].Should().Be("race-1_race_1");
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
    public async Task HumanModules_ShouldRejectInteractionTemplateForHitlSuspensions()
    {
        var approval = new HumanApprovalModule();
        var input = new HumanInputModule();
        var ctx = CreateContext();

        await approval.HandleAsync(
            Envelope(new StepRequestEvent
            {
                StepId = "approval-template",
                StepType = "human_approval",
                RunId = "run-template",
                StepParameters = new WorkflowStepParameters
                {
                    InteractionTemplateSpec = new InteractionTemplateSpec
                    {
                        TemplateId = "tpl-approval",
                    },
                },
            }),
            ctx,
            CancellationToken.None);

        var approvalCompleted = ctx.Published.Select(x => x.evt).OfType<StepCompletedEvent>().Single();
        approvalCompleted.Success.Should().BeFalse();
        approvalCompleted.Error.Should().Contain("interaction_template");
        ctx.Published.Should().NotContain(x => x.evt is WorkflowSuspendedEvent);
        ctx.Published.Clear();

        await input.HandleAsync(
            Envelope(new StepRequestEvent
            {
                StepId = "input-template",
                StepType = "human_input",
                RunId = "run-template",
                StepParameters = new WorkflowStepParameters
                {
                    InteractionTemplateSpec = new InteractionTemplateSpec
                    {
                        TemplateId = "tpl-input",
                    },
                },
            }),
            ctx,
            CancellationToken.None);

        var inputCompleted = ctx.Published.Select(x => x.evt).OfType<StepCompletedEvent>().Single();
        inputCompleted.Success.Should().BeFalse();
        inputCompleted.Error.Should().Contain("interaction_template");
        ctx.Published.Should().NotContain(x => x.evt is WorkflowSuspendedEvent);
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
        reduced.Annotations["map_reduce.phase"].Should().Be("reduce");
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

        var judgeRequest = ctx.Published.Select(x => x.evt).OfType<WorkflowLlmExecutionIntent>().Single();
        var firstSessionId = judgeRequest.SessionId;
        ctx.Published.Clear();

        await module.HandleAsync(
            Envelope(new WorkflowLlmInvocationCompletedEvent
            {
                SessionId = firstSessionId,
                Success = true,
                Content = "score: 3.5",
            }),
            ctx,
            CancellationToken.None);

        var lowScore = ctx.Published.Select(x => x.evt).OfType<StepCompletedEvent>().Single();
        lowScore.StepId.Should().Be("eval-1");
        lowScore.Success.Should().BeTrue();
        lowScore.Annotations["evaluate.score"].Should().Be("3.5");
        lowScore.Annotations["evaluate.passed"].Should().Be("False");
        lowScore.BranchKey.Should().Be("retry_path");
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
        var secondSessionId = ctx.Published.Select(x => x.evt).OfType<WorkflowLlmExecutionIntent>().Single().SessionId;
        ctx.Published.Clear();

        await module.HandleAsync(
            Envelope(new WorkflowLlmInvocationCompletedEvent
            {
                SessionId = secondSessionId,
                Success = true,
                Content = "5",
            }),
            ctx,
            CancellationToken.None);

        var highScore = ctx.Published.Select(x => x.evt).OfType<StepCompletedEvent>().Single();
        highScore.StepId.Should().Be("eval-2");
        highScore.Annotations["evaluate.passed"].Should().Be("True");
        highScore.BranchKey.Should().BeEmpty();
    }

    [Fact]
    public async Task LlmCallModule_ShouldDispatchViaTargetRoleAndPromoteTelegramParameters()
    {
        var module = new LLMCallModule();
        var ctx = CreateContext();

        var request = new StepRequestEvent
        {
            StepId = "llm-target-role",
            StepType = "llm_call",
            RunId = "run-target-role",
            Input = "hello bridge",
            TargetRole = "telegram_user_bridge",
        };
        request.Parameters["chat_id"] = "10001";
        request.Parameters["llm_timeout_ms"] = "120000";

        await module.HandleAsync(Envelope(request), ctx, CancellationToken.None);

        ctx.Sent.Should().ContainSingle();
        ctx.Sent[0].targetActorId.Should().Be($"{ctx.AgentId}:telegram_user_bridge");
        var chatRequest = ctx.Sent[0].evt.Should().BeOfType<WorkflowLlmExecutionIntent>().Subject;
        chatRequest.Annotations["chat_id"].Should().Be("10001");
        chatRequest.RunId.Should().Be("run-target-role");
        chatRequest.StepId.Should().Be("llm-target-role");
        chatRequest.Annotations.Should().NotContainKey("llm_timeout_ms");

        await module.HandleAsync(
            Envelope(new WorkflowLlmInvocationCompletedEvent
            {
                SessionId = chatRequest.SessionId,
                Success = true,
                Content = "telegram-ack",
            }),
            ctx,
            CancellationToken.None);

        var completed = ctx.Published.Select(x => x.evt).OfType<StepCompletedEvent>().Single();
        completed.StepId.Should().Be("llm-target-role");
        completed.Success.Should().BeTrue();
        completed.Output.Should().Be("telegram-ack");
    }

    [Fact]
    public async Task LlmCallModule_ShouldForwardTypedRuntimeContextOverrides()
    {
        var module = new LLMCallModule();
        var ctx = CreateContext();
        await WorkflowRequestMetadataRuntimeContextAccess.SetRequestMetadataAsync(
            (IWorkflowExecutionStateHost)ctx.Agent,
            new Dictionary<string, string>
            {
                ["trace-id"] = " trace-abc ",
            });
        await WorkflowRequestMetadataRuntimeContextAccess.SetLlmControlAsync(
            (IWorkflowExecutionStateHost)ctx.Agent,
            new WorkflowLlmControlContext
            {
                ModelOverride = " model-main ",
                MaxToolRoundsOverride = 3,
                UserMemoryPrompt = " memory-main ",
            });

        await module.HandleAsync(
            Envelope(new StepRequestEvent
            {
                StepId = "llm-runtime-metadata",
                StepType = "llm_call",
                RunId = "run-runtime-metadata",
                Input = "hello metadata",
            }),
            ctx,
            CancellationToken.None);

        var intent = ctx.Published.Select(x => x.evt).OfType<WorkflowLlmExecutionIntent>().Single();
        intent.Model.Should().Be("model-main");
        intent.MaxToolRounds.Should().Be(3);
        intent.UserMemoryPrompt.Should().Be("memory-main");
        intent.Headers["trace-id"].Should().Be("trace-abc");
    }

    [Fact]
    public async Task ConnectorCallModule_ShouldForwardTypedRuntimeAuthorizationToConnectorRequest()
    {
        var connector = new RecordingConnector("runtime-auth");
        var module = new ConnectorCallModule(new FixedWorkflowConnectorResolver(connector));
        var ctx = CreateContext();
        await WorkflowCallerCredentialRuntimeContextAccess.SetCredentialAsync(
            (IWorkflowExecutionStateHost)ctx.Agent,
            new WorkflowCallerCredential
            {
                BearerToken = " token-123 ",
            });

        await module.HandleAsync(
            Envelope(new StepRequestEvent
            {
                StepId = "connector-runtime-auth",
                StepType = "connector_call",
                RunId = "run-runtime-auth",
                Input = "payload",
                Parameters =
                {
                    ["connector"] = "runtime-auth",
                    ["operation"] = "invoke",
                },
            }),
            ctx,
            CancellationToken.None);

        connector.LastRequest.Should().NotBeNull();
        connector.LastRequest!.HttpAuthorization.Should().Be("Bearer token-123");
        connector.LastRequest.Metadata.Should().NotContainKey("connector.http.authorization");
    }

    [Fact]
    public async Task ConnectorCallModule_ShouldResolveCapturedSecureValueAfterFreshRuntimeContext()
    {
        var connector = new RecordingConnector("secure-state");
        var module = new ConnectorCallModule(new FixedWorkflowConnectorResolver(connector));
        var services = new ServiceCollection().AddAevatarWorkflow().BuildServiceProvider();
        var agent = new TestAgent("workflow-secure-state-agent", "run-secure-state");
        var captureCtx = new TestEventHandlerContext(services, agent, NullLogger.Instance);

        await SecureInputRuntimeContextAccess.SetCapturedValueAsync(
            captureCtx,
            "run-secure-state",
            "api_key",
            "sk-state",
            CancellationToken.None);

        var callCtx = new TestEventHandlerContext(services, agent, NullLogger.Instance);
        await module.HandleAsync(
            Envelope(new StepRequestEvent
            {
                StepId = "connector-secure-state",
                StepType = "secure_connector_call",
                RunId = "run-secure-state",
                Parameters =
                {
                    ["connector"] = "secure-state",
                    ["stdin_template"] = """{"apiKey":"[[secure:api_key]]"}""",
                },
            }),
            callCtx,
            CancellationToken.None);

        connector.LastRequest.Should().NotBeNull();
        connector.LastRequest!.Payload.Should().Be("""{"apiKey":"sk-state"}""");
    }

    [Fact]
    public async Task EvaluateAndReflectModules_ShouldDispatchViaTargetRole()
    {
        var ctx = CreateContext();

        var evaluate = new EvaluateModule();
        var evaluateRequest = new StepRequestEvent
        {
            StepId = "eval-target-role",
            StepType = "evaluate",
            RunId = "run-eval-target-role",
            Input = "draft",
            TargetRole = "judge",
        };
        evaluateRequest.Parameters["chat_id"] = "chat-eval";
        evaluateRequest.Parameters["threshold"] = "2";
        await evaluate.HandleAsync(Envelope(evaluateRequest), ctx, CancellationToken.None);

        ctx.Sent.Should().ContainSingle(x => x.targetActorId == $"{ctx.AgentId}:judge");
        var evaluateChat = ctx.Sent.Last().evt.Should().BeOfType<WorkflowLlmExecutionIntent>().Subject;
        evaluateChat.Annotations["chat_id"].Should().Be("chat-eval");
        ctx.Published.Clear();

        await evaluate.HandleAsync(
            Envelope(new WorkflowLlmInvocationCompletedEvent
            {
                SessionId = evaluateChat.SessionId,
                Success = true,
                Content = "3",
            }),
            ctx,
            CancellationToken.None);
        ctx.Published.Select(x => x.evt).OfType<StepCompletedEvent>()
            .Single(x => x.StepId == "eval-target-role")
            .Success.Should().BeTrue();
        ctx.Published.Clear();

        var reflect = new ReflectModule();
        var reflectRequest = new StepRequestEvent
        {
            StepId = "reflect-target-role",
            StepType = "reflect",
            RunId = "run-reflect-target-role",
            Input = "draft-reflect",
            TargetRole = "reviewer",
        };
        reflectRequest.Parameters["chat_id"] = "chat-reflect";
        reflectRequest.Parameters["max_rounds"] = "1";
        await reflect.HandleAsync(Envelope(reflectRequest), ctx, CancellationToken.None);

        ctx.Sent.Should().Contain(x => x.targetActorId == $"{ctx.AgentId}:reviewer");
        var reflectChat = ctx.Sent.Last().evt.Should().BeOfType<WorkflowLlmExecutionIntent>().Subject;
        reflectChat.Annotations["chat_id"].Should().Be("chat-reflect");
        ctx.Published.Clear();

        await reflect.HandleAsync(
            Envelope(new WorkflowLlmInvocationCompletedEvent
            {
                SessionId = reflectChat.SessionId,
                Success = true,
                Content = "PASS",
            }),
            ctx,
            CancellationToken.None);
        ctx.Published.Select(x => x.evt).OfType<StepCompletedEvent>()
            .Single(x => x.StepId == "reflect-target-role")
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
        var firstCritiqueSession = ctx.Published.Select(x => x.evt).OfType<WorkflowLlmExecutionIntent>().Single().SessionId;
        ctx.Published.Clear();

        await module.HandleAsync(
            Envelope(new WorkflowLlmInvocationCompletedEvent
            {
                SessionId = firstCritiqueSession,
                Success = true,
                Content = "PASS",
            }),
            ctx,
            CancellationToken.None);

        var passCompleted = ctx.Published.Select(x => x.evt).OfType<StepCompletedEvent>().Single();
        passCompleted.StepId.Should().Be("reflect-1");
        passCompleted.Success.Should().BeTrue();
        passCompleted.Output.Should().Be("draft-1");
        passCompleted.Annotations["reflect.rounds"].Should().Be("1");
        passCompleted.Annotations["reflect.passed"].Should().Be("True");
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
        var critiqueSession0 = ctx.Published.Select(x => x.evt).OfType<WorkflowLlmExecutionIntent>().Single().SessionId;
        ctx.Published.Clear();

        await module.HandleAsync(
            Envelope(new WorkflowLlmInvocationCompletedEvent
            {
                SessionId = critiqueSession0,
                Success = true,
                Content = "Needs improvement",
            }),
            ctx,
            CancellationToken.None);
        var improveSession = ctx.Published.Select(x => x.evt).OfType<WorkflowLlmExecutionIntent>().Single().SessionId;
        ctx.Published.Clear();

        await module.HandleAsync(
            Envelope(new WorkflowLlmInvocationCompletedEvent
            {
                SessionId = improveSession,
                Success = true,
                Content = "draft-2-better",
            }),
            ctx,
            CancellationToken.None);
        var critiqueSession1 = ctx.Published.Select(x => x.evt).OfType<WorkflowLlmExecutionIntent>().Single().SessionId;
        ctx.Published.Clear();

        await module.HandleAsync(
            Envelope(new WorkflowLlmInvocationCompletedEvent
            {
                SessionId = critiqueSession1,
                Success = true,
                Content = "still not good",
            }),
            ctx,
            CancellationToken.None);

        var maxRoundCompleted = ctx.Published.Select(x => x.evt).OfType<StepCompletedEvent>().Single();
        maxRoundCompleted.StepId.Should().Be("reflect-2");
        maxRoundCompleted.Success.Should().BeTrue();
        maxRoundCompleted.Output.Should().Be("draft-2-better");
        maxRoundCompleted.Annotations["reflect.rounds"].Should().Be("2");
        maxRoundCompleted.Annotations["reflect.passed"].Should().Be("False");
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
        var sessionA = ctx.Published.Select(x => x.evt).OfType<WorkflowLlmExecutionIntent>().Single().SessionId;
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
        var sessionB = ctx.Published.Select(x => x.evt).OfType<WorkflowLlmExecutionIntent>().Single().SessionId;
        ctx.Published.Clear();

        await module.HandleAsync(
            Envelope(new WorkflowLlmInvocationCompletedEvent
            {
                SessionId = sessionB,
                Success = true,
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
            Envelope(new WorkflowLlmInvocationCompletedEvent
            {
                SessionId = sessionA,
                Success = true,
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
                UserInput = "legacy-approved",
                EditedContent = "looks good",
                Feedback = "approved as edited",
            }),
            ctx,
            CancellationToken.None);

        var approved = ctx.Published.Select(x => x.evt).OfType<StepCompletedEvent>().Single();
        approved.Success.Should().BeTrue();
        approved.Output.Should().Be("looks good");
        approved.BranchKey.Should().Be("true");
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
                UserInput = "legacy-reject",
                EditedContent = "edited but rejected",
                Feedback = "change the model to gpt-4",
            }),
            ctx,
            CancellationToken.None);

        var rejected = ctx.Published.Select(x => x.evt).OfType<StepCompletedEvent>().Single();
        rejected.Success.Should().BeTrue();
        rejected.BranchKey.Should().Be("false");
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
        rejected.BranchKey.Should().Be("false");
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

    private sealed class FixedWorkflowConnectorResolver(IConnector connector) : IWorkflowConnectorResolver
    {
        public ValueTask<IConnector?> ResolveAsync(
            IWorkflowExecutionContext context,
            string connectorName,
            CancellationToken ct = default)
        {
            _ = context;
            _ = connectorName;
            ct.ThrowIfCancellationRequested();
            return ValueTask.FromResult<IConnector?>(connector);
        }
    }

    private sealed class RecordingConnector(string name) : IConnector
    {
        public string Name { get; } = name;

        public string Type => "test";

        public ConnectorRequest? LastRequest { get; private set; }

        public Task<ConnectorResponse> ExecuteAsync(ConnectorRequest request, CancellationToken ct = default)
        {
            _ = ct;
            LastRequest = request;
            return Task.FromResult(new ConnectorResponse
            {
                Success = true,
                Output = "ok",
            });
        }
    }

    private static TestEventHandlerContext CreateContext(IServiceProvider? services = null, ILogger? logger = null)
    {
        return new TestEventHandlerContext(
            services ?? new ServiceCollection().AddAevatarWorkflow().BuildServiceProvider(),
            new TestAgent("workflow-advanced-module-test-agent"),
            logger ?? NullLogger.Instance);
    }

    private static EventEnvelope Envelope(IMessage evt, string? publisherId = null)
    {
        return new EventEnvelope
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
            Payload = Any.Pack(evt),
            Route = EnvelopeRouteSemantics.CreateTopologyPublication(publisherId ?? "test-publisher", TopologyAudience.Self),
        };
    }

    private sealed class RecordingLogger : ILogger
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull =>
            null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Information)
                Messages.Add(formatter(state, exception));
        }
    }

}
