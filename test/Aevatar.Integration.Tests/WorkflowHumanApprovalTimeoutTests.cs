using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Core;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Core;
using Aevatar.Workflow.Core.Modules;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.Integration.Tests;

[Trait("Category", "Integration")]
[Trait("Feature", "WorkflowHumanApprovalTimeout")]
public sealed class WorkflowHumanApprovalTimeoutTests
{
    [Fact]
    public async Task HumanApprovalModule_ShouldScheduleAndCancelTimeoutOnManualResume()
    {
        var module = new HumanApprovalModule();
        var ctx = CreateContext();

        await module.HandleAsync(
            Envelope(new StepRequestEvent
            {
                StepId = "approval-timeout-cancel",
                StepType = "human_approval",
                RunId = "run-timeout-cancel",
                Input = "payload",
                Parameters = { ["timeout"] = "45" },
            }),
            ctx,
            CancellationToken.None);

        ctx.Scheduled.Should().ContainSingle();
        var scheduled = ctx.Scheduled.Single();
        scheduled.Event.Should().BeOfType<WorkflowHumanApprovalTimeoutFiredEvent>();
        scheduled.DueTime.Should().Be(TimeSpan.FromSeconds(45));
        ctx.Published.Clear();

        await module.HandleAsync(
            Envelope(new WorkflowResumedEvent
            {
                RunId = "run-timeout-cancel",
                StepId = "approval-timeout-cancel",
                Approved = true,
                UserInput = "approved",
            }),
            ctx,
            CancellationToken.None);

        ctx.Published.Select(x => x.evt).OfType<StepCompletedEvent>().Single().Success.Should().BeTrue();
        ctx.Canceled.Should().ContainSingle(x => x.CallbackId == scheduled.CallbackId);
    }

    [Fact]
    public async Task HumanApprovalModule_WhenTimeoutFires_ShouldApplyConfiguredDecisionAndIgnoreLateResume()
    {
        var module = new HumanApprovalModule();
        var ctx = CreateContext();

        await module.HandleAsync(
            Envelope(new StepRequestEvent
            {
                StepId = "approval-auto-approve",
                StepType = "human_approval",
                RunId = "run-auto-approve",
                Input = "payload",
                StepParameters = new WorkflowStepParameters
                {
                    DeliveryTargetId = "approval-target",
                    HumanApproval = new WorkflowHumanApprovalOptions
                    {
                        TimeoutDefaultDecision = WorkflowHumanApprovalTimeoutDefaultDecision.Approve,
                    },
                },
                Parameters = { ["timeout"] = "10" },
            }),
            ctx,
            CancellationToken.None);

        var suspended = ctx.Published.Select(x => x.evt).OfType<WorkflowSuspendedEvent>().Single();
        suspended.TimeoutDefaultDecision.Should().Be(WorkflowHumanApprovalTimeoutDefaultDecision.Approve);
        suspended.Callback.Should().NotBeNull();
        suspended.Callback.Kind.Should().Be("workflow_resume");
        suspended.Callback.ActorId.Should().Be("workflow-human-approval-timeout-test-agent");
        suspended.Callback.RunId.Should().Be("run-auto-approve");
        suspended.Callback.StepId.Should().Be("approval-auto-approve");
        var scheduled = ctx.Scheduled.Single();
        ctx.Published.Clear();

        await module.HandleAsync(
            ctx.CreateScheduledEnvelope(scheduled),
            ctx,
            CancellationToken.None);

        var completed = ctx.Published.Select(x => x.evt).OfType<StepCompletedEvent>().Single();
        completed.Success.Should().BeTrue();
        completed.BranchKey.Should().Be("true");
        completed.Output.Should().Be("payload");
        var resolved = ctx.Published.Select(x => x.evt).OfType<WorkflowHumanApprovalResolvedEvent>().Single();
        resolved.Approved.Should().BeTrue();
        resolved.ResolutionSource.Should().Be(WorkflowHumanApprovalResolutionSource.Timeout);
        resolved.DeliveryTargetId.Should().Be("approval-target");
        ctx.Published.Clear();

        await module.HandleAsync(
            Envelope(new WorkflowResumedEvent
            {
                RunId = "run-auto-approve",
                StepId = "approval-auto-approve",
                Approved = false,
            }),
            ctx,
            CancellationToken.None);

        ctx.Published.Should().BeEmpty();
    }

    [Fact]
    public async Task HumanApprovalModule_WhenTimeoutDecisionMissing_ShouldRejectSafely()
    {
        var module = new HumanApprovalModule();
        var ctx = CreateContext();

        await module.HandleAsync(
            Envelope(new StepRequestEvent
            {
                StepId = "approval-auto-reject",
                StepType = "human_approval",
                RunId = "run-auto-reject",
                Input = "payload",
                Parameters =
                {
                    ["timeout"] = "10",
                    ["on_reject"] = "continue",
                },
            }),
            ctx,
            CancellationToken.None);

        var scheduled = ctx.Scheduled.Single();
        ctx.Published.Clear();

        await module.HandleAsync(
            ctx.CreateScheduledEnvelope(scheduled),
            ctx,
            CancellationToken.None);

        var completed = ctx.Published.Select(x => x.evt).OfType<StepCompletedEvent>().Single();
        completed.Success.Should().BeTrue();
        completed.BranchKey.Should().Be("false");
        completed.Output.Should().Be("payload");
    }

    [Fact]
    public async Task HumanApprovalModule_WhenTimeoutLeaseIsStale_ShouldIgnoreCallback()
    {
        var module = new HumanApprovalModule();
        var ctx = CreateContext();

        await module.HandleAsync(
            Envelope(new StepRequestEvent
            {
                StepId = "approval-stale-timeout",
                StepType = "human_approval",
                RunId = "run-stale-timeout",
                Input = "payload",
                Parameters = { ["timeout"] = "10" },
            }),
            ctx,
            CancellationToken.None);

        var scheduled = ctx.Scheduled.Single();
        ctx.Published.Clear();

        await module.HandleAsync(
            Envelope(new WorkflowHumanApprovalTimeoutFiredEvent
            {
                RunId = "run-stale-timeout",
                StepId = "approval-stale-timeout",
                TimeoutSeconds = 10,
            }),
            ctx,
            CancellationToken.None);

        ctx.Published.Should().BeEmpty();

        await module.HandleAsync(
            ctx.CreateScheduledEnvelope(scheduled),
            ctx,
            CancellationToken.None);

        ctx.Published.Select(x => x.evt).OfType<StepCompletedEvent>().Should().ContainSingle();
    }

    private static TestEventHandlerContext CreateContext()
    {
        return new TestEventHandlerContext(
            new ServiceCollection().AddAevatarWorkflow().BuildServiceProvider(),
            new TestAgent("workflow-human-approval-timeout-test-agent"),
            NullLogger.Instance);
    }

    private static EventEnvelope Envelope(IMessage evt)
    {
        return new EventEnvelope
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
            Payload = Any.Pack(evt),
            Route = EnvelopeRouteSemantics.CreateTopologyPublication("test", TopologyAudience.Self),
        };
    }
}
