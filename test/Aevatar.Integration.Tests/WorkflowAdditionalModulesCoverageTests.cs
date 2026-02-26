using Aevatar.AI.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.EventModules;
using Aevatar.Foundation.Core;
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
        ctx.Published[0].direction.Should().Be(EventDirection.Both);
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

    private static RecordingEventHandlerContext CreateContext(IServiceProvider? services = null)
    {
        return new RecordingEventHandlerContext(
            services ?? new ServiceCollection().BuildServiceProvider(),
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
