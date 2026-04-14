using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.HumanInteraction;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Presentation.AGUIAdapter;
using Aevatar.Workflow.Projection;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Workflow.Host.Api.Tests;

public sealed class WorkflowHumanInteractionProjectorTests
{
    [Fact]
    public async Task ProjectAsync_ShouldDeliverSuspension_WhenDeliveryTargetIsPresent()
    {
        var port = new RecordingHumanInteractionPort();
        var projector = new WorkflowHumanInteractionProjector(port);

        await projector.ProjectAsync(
            BuildContext(),
            new EventEnvelope
            {
                Id = "evt-human-1",
                Route = EnvelopeRouteSemantics.CreateObserverPublication("workflow-human-interaction-test"),
                Payload = Any.Pack(new WorkflowSuspendedEvent
                {
                    RunId = "run-1",
                    StepId = "approval-1",
                    SuspensionType = "human_approval",
                    Prompt = "Need approval",
                    Content = "Please review the summary.",
                    DeliveryTargetId = "agent-delivery-1",
                    TimeoutSeconds = 90,
                    Metadata = { ["source"] = "workflow-test" },
                }),
            },
            CancellationToken.None);

        port.Calls.Should().ContainSingle();
        var call = port.Calls[0];
        call.deliveryTargetId.Should().Be("agent-delivery-1");
        call.request.ActorId.Should().Be("workflow-actor-1");
        call.request.RunId.Should().Be("run-1");
        call.request.StepId.Should().Be("approval-1");
        call.request.SuspensionType.Should().Be("human_approval");
        call.request.Content.Should().Be("Please review the summary.");
        call.request.Options.Should().Equal("approve", "reject");
        call.request.TimeoutSeconds.Should().Be(90);
        call.request.Annotations.Should().ContainKey("source").WhoseValue.Should().Be("workflow-test");
    }

    [Fact]
    public async Task ProjectAsync_ShouldIgnoreSuspension_WhenDeliveryTargetMissing()
    {
        var port = new RecordingHumanInteractionPort();
        var projector = new WorkflowHumanInteractionProjector(port);

        await projector.ProjectAsync(
            BuildContext(),
            new EventEnvelope
            {
                Id = "evt-human-2",
                Route = EnvelopeRouteSemantics.CreateObserverPublication("workflow-human-interaction-test"),
                Payload = Any.Pack(new WorkflowSuspendedEvent
                {
                    RunId = "run-2",
                    StepId = "input-1",
                    SuspensionType = "human_input",
                    Prompt = "Need extra details",
                }),
            },
            CancellationToken.None);

        port.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task ProjectAsync_ShouldIgnoreNonProjectionRoute()
    {
        var port = new RecordingHumanInteractionPort();
        var projector = new WorkflowHumanInteractionProjector(port);

        await projector.ProjectAsync(
            BuildContext(),
            new EventEnvelope
            {
                Id = "evt-human-3",
                Route = EnvelopeRouteSemantics.CreateDirect("projection-test", "workflow-actor-1"),
                Payload = Any.Pack(new WorkflowSuspendedEvent
                {
                    RunId = "run-3",
                    StepId = "approval-3",
                    SuspensionType = "human_approval",
                    Prompt = "Need approval",
                    DeliveryTargetId = "agent-delivery-3",
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

    private sealed class RecordingHumanInteractionPort : IHumanInteractionPort
    {
        public List<(HumanInteractionRequest request, string deliveryTargetId)> Calls { get; } = [];

        public Task DeliverSuspensionAsync(
            HumanInteractionRequest request,
            string deliveryTargetId,
            CancellationToken cancellationToken = default)
        {
            Calls.Add((request, deliveryTargetId));
            return Task.CompletedTask;
        }
    }
}
