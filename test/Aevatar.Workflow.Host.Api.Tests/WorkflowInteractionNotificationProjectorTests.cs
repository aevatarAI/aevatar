using Aevatar.CQRS.Projection.Core.Abstractions.Orchestration;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.HumanInteraction;
using Aevatar.Foundation.Abstractions.Interactions;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Presentation.AGUIAdapter;
using Aevatar.Workflow.Projection;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Workflow.Host.Api.Tests;

public sealed class WorkflowInteractionNotificationProjectorTests
{
    [Fact]
    public async Task ProjectAsync_ShouldDeliverInteractionNotification_WhenRouteIsDispatchable()
    {
        var port = new RecordingNotificationPort();
        var projector = new WorkflowInteractionNotificationProjector(port);

        await projector.ProjectAsync(
            BuildContext(),
            new EventEnvelope
            {
                Id = "evt-notify-1",
                Route = EnvelopeRouteSemantics.CreateObserverPublication("workflow-notify-test"),
                Payload = Any.Pack(new WorkflowInteractionNotificationEvent
                {
                    RunId = "run-1",
                    StepId = "notify-1",
                    DeliveryTargetId = "agent-delivery-1",
                    Interaction = new InteractionSpec
                    {
                        Title = "Status",
                        Body = "Accepted",
                    },
                }),
            },
            CancellationToken.None);

        port.Calls.Should().ContainSingle();
        var call = port.Calls[0];
        call.ActorId.Should().Be("workflow-actor-1");
        call.RunId.Should().Be("run-1");
        call.StepId.Should().Be("notify-1");
        call.DeliveryTargetId.Should().Be("agent-delivery-1");
        call.InteractionSpec.Should().NotBeNull();
        call.InteractionSpec!.Title.Should().Be("Status");
        call.InteractionTemplateSpec.Should().BeNull();
    }

    [Fact]
    public async Task ProjectAsync_ShouldDeliverInteractionNotification_FromCommittedStatePublication()
    {
        var port = new RecordingNotificationPort();
        var projector = new WorkflowInteractionNotificationProjector(port);

        await projector.ProjectAsync(
            BuildContext(),
            BuildCommittedStateEnvelope(
                "outer-notify-committed",
                "evt-notify-committed",
                new WorkflowInteractionNotificationEvent
                {
                    RunId = "run-committed",
                    StepId = "notify-committed",
                    DeliveryTargetId = "agent-delivery-committed",
                    Interaction = new InteractionSpec
                    {
                        Title = "Committed status",
                        Body = "Committed accepted",
                    },
                }),
            CancellationToken.None);

        port.Calls.Should().ContainSingle();
        var call = port.Calls[0];
        call.DeliveryTargetId.Should().Be("agent-delivery-committed");
        call.RunId.Should().Be("run-committed");
        call.StepId.Should().Be("notify-committed");
        call.InteractionSpec.Should().NotBeNull();
        call.InteractionSpec!.Title.Should().Be("Committed status");
    }

    [Fact]
    public async Task ProjectAsync_ShouldDeliverTemplateNotification_WhenTemplatePayloadIsPresent()
    {
        var port = new RecordingNotificationPort();
        var projector = new WorkflowInteractionNotificationProjector(port);
        var template = new InteractionTemplateSpec { TemplateId = "tpl-1" };
        template.TemplateVariable["run"] = "run-1";

        await projector.ProjectAsync(
            BuildContext(),
            new EventEnvelope
            {
                Id = "evt-notify-template",
                Route = EnvelopeRouteSemantics.CreateObserverPublication("workflow-notify-test"),
                Payload = Any.Pack(new WorkflowInteractionNotificationEvent
                {
                    RunId = "run-1",
                    StepId = "notify-template",
                    DeliveryTargetId = "agent-delivery-1",
                    InteractionTemplate = template,
                }),
            },
            CancellationToken.None);

        port.Calls.Should().ContainSingle();
        port.Calls[0].InteractionSpec.Should().BeNull();
        port.Calls[0].InteractionTemplateSpec.Should().NotBeNull();
        port.Calls[0].InteractionTemplateSpec!.TemplateVariable["run"].Should().Be("run-1");
    }

    [Fact]
    public async Task ProjectAsync_ShouldIgnoreNonDispatchRouteNonNotificationAndMissingTarget()
    {
        var port = new RecordingNotificationPort();
        var projector = new WorkflowInteractionNotificationProjector(port);

        await projector.ProjectAsync(
            BuildContext(),
            new EventEnvelope
            {
                Id = "evt-direct",
                Route = EnvelopeRouteSemantics.CreateDirect("workflow-actor-1", "projection-test"),
                Payload = Any.Pack(new WorkflowInteractionNotificationEvent
                {
                    RunId = "run-1",
                    StepId = "notify-direct",
                    DeliveryTargetId = "agent-delivery-1",
                    Interaction = new InteractionSpec { Title = "Ignored" },
                }),
            },
            CancellationToken.None);
        await projector.ProjectAsync(
            BuildContext(),
            new EventEnvelope
            {
                Id = "evt-other",
                Route = EnvelopeRouteSemantics.CreateObserverPublication("workflow-notify-test"),
                Payload = Any.Pack(new WorkflowCompletedEvent { RunId = "run-1" }),
            },
            CancellationToken.None);
        await projector.ProjectAsync(
            BuildContext(),
            new EventEnvelope
            {
                Id = "evt-missing-target",
                Route = EnvelopeRouteSemantics.CreateObserverPublication("workflow-notify-test"),
                Payload = Any.Pack(new WorkflowInteractionNotificationEvent
                {
                    RunId = "run-1",
                    StepId = "notify-missing",
                    Interaction = new InteractionSpec { Title = "Ignored" },
                }),
            },
            CancellationToken.None);

        port.Calls.Should().BeEmpty();
    }

    private static WorkflowExecutionProjectionContext BuildContext() => new()
    {
        SessionId = "cmd-1",
        RootActorId = "workflow-actor-1",
        ProjectionKind = "workflow-execution-session",
    };

    private static EventEnvelope BuildCommittedStateEnvelope(
        string envelopeId,
        string eventId,
        IMessage payload) => new()
    {
        Id = envelopeId,
        Route = EnvelopeRouteSemantics.CreateObserverPublication("workflow-notify-test"),
        Payload = Any.Pack(new CommittedStateEventPublished
        {
            StateEvent = new StateEvent
            {
                EventId = eventId,
                Version = 12,
                EventData = Any.Pack(payload),
            },
        }),
    };

    private sealed class RecordingNotificationPort : IChannelInteractionNotificationPort
    {
        public List<ChannelInteractionNotificationRequest> Calls { get; } = [];

        public Task DeliverAsync(
            ChannelInteractionNotificationRequest request,
            CancellationToken cancellationToken = default)
        {
            Calls.Add(request);
            return Task.CompletedTask;
        }
    }
}
